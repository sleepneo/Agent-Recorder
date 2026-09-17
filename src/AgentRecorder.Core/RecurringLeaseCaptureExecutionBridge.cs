using System.Threading;
using AgentRecorder.Capture;
using AgentRecorder.Core.Automation;
using AgentRecorder.Windows;

namespace AgentRecorder.Core;

internal enum RecurringLeaseCurrentSafetyStatus
{
    Allowed,
    UnattendedDisabled,
    StopAllActive,
    LeaseRevoked,
    ReenableRequiresNewAuthorization,
    StateInvalid,
}

internal readonly record struct RecurringLeaseCurrentSafetyDecision
{
    internal RecurringLeaseCurrentSafetyDecision(RecurringLeaseCurrentSafetyStatus status)
    {
        Status = status;
    }

    internal RecurringLeaseCurrentSafetyStatus Status { get; }
}

/// <summary>
/// Process-local, one-time bridge from a claimed recurring execution ticket to
/// a proof-bearing fake/test backend. This type is intentionally not connected
/// to a scheduler, product entry point, UI, RecordingEngine, or real backend
/// selector.
/// </summary>
internal sealed class RecurringLeaseCaptureExecutionBridge
{
    private const int FixedFramesPerSecond = 30;
    private const string FixedQuality = "medium";

    private readonly IRecurringOccurrenceEnvironmentProvider? _environmentProvider;
    private readonly Func<RecurringOccurrenceExecutionSpecification, ICaptureBackend?> _backendFactory;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private readonly Func<DateTimeOffset?> _utcNow;
    private readonly StandingLeaseStartSafetyInterlock? _startSafetyInterlock;
    private readonly Func<RecurringLeaseCaptureExecutionTicket, DateTimeOffset, RecurringLeaseCurrentSafetyDecision>? _currentSafetyValidator;
    private readonly Func<RecurringLeaseCaptureExecutionTicket, ICaptureBackend, IRecurringLeaseCaptureLifecycleSession?>? _lifecycleSessionFactory;

    internal RecurringLeaseCaptureExecutionBridge()
        : this(
            environmentProviderForTest: null,
            backendFactoryForTest: null,
            delayForTest: null,
            utcNowForTest: null,
            startSafetyInterlockForTest: null,
            currentSafetyValidatorForTest: null,
            lifecycleSessionFactoryForTest: null)
    {
    }

    // All seams are internal and deterministic. The bridge deliberately has
    // no production backend/environment fallback: a later product host must
    // inject the shared environment and safety-control dependencies.
    internal RecurringLeaseCaptureExecutionBridge(
        IRecurringOccurrenceEnvironmentProvider? environmentProviderForTest,
        Func<RecurringOccurrenceExecutionSpecification, ICaptureBackend?>? backendFactoryForTest,
        Func<TimeSpan, CancellationToken, Task>? delayForTest = null,
        Func<DateTimeOffset?>? utcNowForTest = null,
        StandingLeaseStartSafetyInterlock? startSafetyInterlockForTest = null,
        Func<RecurringLeaseCaptureExecutionTicket, DateTimeOffset, RecurringLeaseCurrentSafetyDecision>? currentSafetyValidatorForTest = null,
        Func<RecurringLeaseCaptureExecutionTicket, ICaptureBackend, IRecurringLeaseCaptureLifecycleSession?>? lifecycleSessionFactoryForTest = null)
    {
        _environmentProvider = environmentProviderForTest;
        _backendFactory = backendFactoryForTest ?? UnsupportedBackendFactory;
        _delay = delayForTest ?? ((duration, cancellationToken) => Task.Delay(duration, cancellationToken));
        _utcNow = utcNowForTest ?? (() => DateTimeOffset.UtcNow);
        _startSafetyInterlock = startSafetyInterlockForTest;
        _currentSafetyValidator = currentSafetyValidatorForTest;
        _lifecycleSessionFactory = lifecycleSessionFactoryForTest;
    }

    internal async Task<RecurringLeaseCaptureExecutionResult> ExecuteAsync(
        RecurringLeaseCaptureExecutionTicket? ticket,
        CancellationToken cancellationToken = default)
    {
        if (ticket is null)
            return RecurringLeaseCaptureExecutionResult.Rejected("recurring_execution_ticket_missing");

        // This is intentionally the first stateful action. Every outcome
        // after this CAS permanently burns the ticket.
        if (!ticket.TryClaim(out var claimFailure))
            return RecurringLeaseCaptureExecutionResult.Rejected(claimFailure);

        if (!ticket.IsProofConsumed)
            return RecurringLeaseCaptureExecutionResult.Rejected("recurring_execution_proof_not_consumed");

        RecurringOccurrenceExecutionSpecification specification;
        RecurringLeaseUseProof consumedProof;
        try
        {
            specification = ticket.Specification;
            consumedProof = ticket.GetConsumedProofForBackendStart() as RecurringLeaseUseProof
                ?? throw new InvalidOperationException("A recurring proof is required.");
        }
        catch
        {
            return RecurringLeaseCaptureExecutionResult.Rejected("recurring_execution_proof_unavailable");
        }

        if (!TryValidateSpecificationForExecution(ticket, specification, out var specificationFailure) ||
            !TryValidateProofBinding(ticket, specification, consumedProof, out specificationFailure))
        {
            return RecurringLeaseCaptureExecutionResult.Rejected(specificationFailure);
        }

        if (specification.CountdownSeconds > 0)
        {
            try
            {
                await _delay(
                    TimeSpan.FromSeconds(specification.CountdownSeconds),
                    cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return RecurringLeaseCaptureExecutionResult.Cancelled();
            }
            catch
            {
                return RecurringLeaseCaptureExecutionResult.Failed("recurring_execution_countdown_failed");
            }
        }

        if (cancellationToken.IsCancellationRequested)
            return RecurringLeaseCaptureExecutionResult.Cancelled();

        if (!TryReadUtcNow(out var executionNowUtc, out var timeFailure) ||
            !TryValidateExecutionTime(ticket, specification, consumedProof, executionNowUtc, out timeFailure))
        {
            return RecurringLeaseCaptureExecutionResult.Rejected(timeFailure);
        }

        StandingLeaseExecutionEnvironment? environment;
        try
        {
            if (_environmentProvider is null)
                return RecurringLeaseCaptureExecutionResult.Rejected("recurring_execution_environment_unavailable");

            var requirements = FixedRegionExecutionEnvironmentRequirements
                .FromRecurringSpecification(specification);
            var request = new RecurringOccurrenceEnvironmentCaptureRequest(
                requirements,
                executionNowUtc);
            environment = _environmentProvider.Capture(request);
        }
        catch
        {
            return RecurringLeaseCaptureExecutionResult.Rejected("recurring_execution_environment_unavailable");
        }

        if (environment is null)
            return RecurringLeaseCaptureExecutionResult.Rejected("recurring_execution_environment_unavailable");

        // A cancellation observed after provider return must prevent factory
        // construction as well as Start. The final interlock check below is
        // still mandatory for cancellation during factory/configuration.
        if (cancellationToken.IsCancellationRequested)
            return RecurringLeaseCaptureExecutionResult.Cancelled();

        if (environment.NowUtc.Offset != TimeSpan.Zero)
            return RecurringLeaseCaptureExecutionResult.Rejected("recurring_execution_time_not_utc");

        if (environment.NowUtc.UtcDateTime.Ticks != executionNowUtc.UtcDateTime.Ticks)
            return RecurringLeaseCaptureExecutionResult.Rejected("recurring_execution_environment_time_mismatch");

        var environmentRequirements = FixedRegionExecutionEnvironmentRequirements
            .FromRecurringSpecification(specification);
        if (!StandingLeaseExecutionEnvironmentValidator.TryValidate(
                environment,
                environmentRequirements,
                out var environmentFailure))
        {
            return RecurringLeaseCaptureExecutionResult.Rejected(environmentFailure);
        }

        if (!TryMapCaptureConfig(specification, out var config, out var configFailure))
            return RecurringLeaseCaptureExecutionResult.Rejected(configFailure);

        // Backend construction is intentionally before the final synchronous
        // boundary. If that boundary rejects the start, this bridge remains
        // responsible for disposing this exact instance once.
        ICaptureBackend? backend;
        try
        {
            backend = _backendFactory(specification);
        }
        catch
        {
            return RecurringLeaseCaptureExecutionResult.Failed("recurring_execution_backend_unavailable");
        }

        if (backend is null)
            return RecurringLeaseCaptureExecutionResult.Failed("recurring_execution_backend_unavailable");

        if (_startSafetyInterlock is null)
        {
            DisposeBackendOnce(backend);
            return RecurringLeaseCaptureExecutionResult.Rejected("recurring_execution_safety_interlock_unavailable");
        }

        // Execute is synchronous by contract. There is no await in this
        // critical section, and the same interlock is supplied to the future
        // unattended safety control service by the product host.
        return _startSafetyInterlock.Execute(
            "recurring_backend_start",
            () =>
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    DisposeBackendOnce(backend);
                    return RecurringLeaseCaptureExecutionResult.Cancelled();
                }

                if (!TryReadUtcNow(out var finalNowUtc, out var finalTimeFailure) ||
                    !TryValidateExecutionTime(
                        ticket,
                        specification,
                        consumedProof,
                        finalNowUtc,
                        out finalTimeFailure))
                {
                    DisposeBackendOnce(backend);
                    return RecurringLeaseCaptureExecutionResult.Rejected(finalTimeFailure);
                }

                if (_currentSafetyValidator is null)
                {
                    DisposeBackendOnce(backend);
                    return RecurringLeaseCaptureExecutionResult.Rejected(
                        "recurring_execution_safety_validator_unavailable");
                }

                RecurringLeaseCurrentSafetyDecision safetyDecision;
                try
                {
                    safetyDecision = _currentSafetyValidator(ticket, finalNowUtc);
                }
                catch
                {
                    DisposeBackendOnce(backend);
                    return RecurringLeaseCaptureExecutionResult.Rejected(
                        "recurring_execution_safety_validator_unavailable");
                }

                if (cancellationToken.IsCancellationRequested)
                {
                    DisposeBackendOnce(backend);
                    return RecurringLeaseCaptureExecutionResult.Cancelled();
                }

                if (safetyDecision.Status != RecurringLeaseCurrentSafetyStatus.Allowed)
                {
                    DisposeBackendOnce(backend);
                    return RecurringLeaseCaptureExecutionResult.Rejected(
                        MapSafetyFailureReason(safetyDecision.Status));
                }

                IRecurringLeaseCaptureLifecycleSession? lifecycleSession = null;
                var lifecycleAttached = false;
                try
                {
                    if (_lifecycleSessionFactory is null)
                    {
                        DisposeBackendOnce(backend);
                        return RecurringLeaseCaptureExecutionResult.Failed(
                            "recurring_execution_lifecycle_session_unavailable");
                    }

                    lifecycleSession = _lifecycleSessionFactory(ticket, backend);
                    if (lifecycleSession is null)
                    {
                        DisposeBackendOnce(backend);
                        return RecurringLeaseCaptureExecutionResult.Failed(
                            "recurring_execution_lifecycle_session_unavailable");
                    }

                    if (lifecycleSession is not IRecurringLeaseCaptureLifecycleDriver lifecycleDriver)
                    {
                        DisposeLifecycleSessionOnce(lifecycleSession);
                        DisposeBackendOnce(backend);
                        return RecurringLeaseCaptureExecutionResult.Failed(
                            "recurring_execution_lifecycle_session_unavailable");
                    }

                    if (!lifecycleSession.TryAttach(backend, out _))
                    {
                        DisposeLifecycleSessionOnce(lifecycleSession);
                        DisposeBackendOnce(backend);
                        return RecurringLeaseCaptureExecutionResult.Failed(
                            "recurring_execution_lifecycle_session_attach_failed");
                    }

                    lifecycleAttached = true;

                    // This is the final cancellation point after ownership
                    // has transferred. The session must durably record an
                    // interruption before it is disposed; the bridge never
                    // issues a physical Stop for a backend that was not
                    // started.
                    if (cancellationToken.IsCancellationRequested)
                    {
                        var interrupted = lifecycleDriver.FailBeforeStart("session_interrupted");
                        DisposeLifecycleSessionOnce(lifecycleSession);
                        if (!interrupted.Succeeded || !interrupted.Terminal)
                        {
                            return RecurringLeaseCaptureExecutionResult.Failed(
                                "recurring_execution_lifecycle_failure");
                        }

                        return RecurringLeaseCaptureExecutionResult.Cancelled();
                    }

                    backend.Start(config, consumedProof);

                    // A fake/test backend may synchronously raise first-frame,
                    // natural-exit, or persistence-failure callbacks from
                    // Start. Only the session's serialized handoff query can
                    // distinguish an active owner from one already released.
                    var handoff = lifecycleSession.CompleteStartHandoff();
                    if (handoff.Status == RecurringLeaseCaptureLifecycleHandoffStatus.Active)
                    {
                        return RecurringLeaseCaptureExecutionResult.Started(
                            backend,
                            lifecycleSession);
                    }

                    DisposeLifecycleSessionOnce(lifecycleSession);
                    return handoff.Status switch
                    {
                        RecurringLeaseCaptureLifecycleHandoffStatus.AlreadyTerminal =>
                            IsDurableMediaSettled(handoff.FirstTerminalResult)
                                ? RecurringLeaseCaptureExecutionResult.CompletedDuringStart(
                                    "recurring_execution_completed_during_start")
                                : RecurringLeaseCaptureExecutionResult.Failed(
                                    handoff.FirstTerminalResult is null
                                        ? "recurring_execution_terminal_classification_unavailable"
                                        : "recurring_execution_abnormal_terminal_during_start"),
                        RecurringLeaseCaptureLifecycleHandoffStatus.FailClosed =>
                            RecurringLeaseCaptureExecutionResult.Failed(
                                "recurring_execution_lifecycle_failure"),
                        _ => RecurringLeaseCaptureExecutionResult.Failed(
                            "recurring_execution_lifecycle_session_unavailable"),
                    };
                }
                catch
                {
                    if (lifecycleSession is not null)
                    {
                        if (lifecycleAttached)
                        {
                            try
                            {
                                lifecycleSession.StartFailed();
                            }
                            catch
                            {
                                // The stable bridge failure below remains
                                // authoritative; the session remains the
                                // only backend owner after attach.
                            }
                        }

                        DisposeLifecycleSessionOnce(lifecycleSession);
                        if (!lifecycleAttached)
                            DisposeBackendOnce(backend);
                    }
                    else
                    {
                        DisposeBackendOnce(backend);
                    }

                    var failureReason = lifecycleSession is null
                        ? "recurring_execution_lifecycle_session_unavailable"
                        : !lifecycleAttached
                            ? "recurring_execution_lifecycle_session_attach_failed"
                            : "recurring_execution_backend_start_failed";
                    return RecurringLeaseCaptureExecutionResult.Failed(failureReason);
                }
            });
    }

    internal static bool TryBuildCaptureConfigForEngine(
        RecurringOccurrenceExecutionSpecification specification,
        out CaptureConfig config,
        out string failureReason) =>
        TryMapCaptureConfig(specification, out config, out failureReason);

    internal static bool TryMapCaptureConfig(
        RecurringOccurrenceExecutionSpecification specification,
        out CaptureConfig config,
        out string failureReason)
    {
        config = new CaptureConfig();
        failureReason = "recurring_execution_specification_invalid";
        if (specification is null)
            return false;

        var duration = specification.Duration;
        if (duration <= TimeSpan.Zero ||
            duration.Ticks % TimeSpan.TicksPerMillisecond != 0 ||
            duration > AuthorizedFixedRegionScope.MaximumReservedDuration)
        {
            failureReason = "recurring_execution_duration_not_representable";
            return false;
        }

        if (duration.Ticks % TimeSpan.TicksPerSecond != 0)
        {
            failureReason = "recurring_execution_duration_not_representable";
            return false;
        }

        var durationSeconds = duration.Ticks / TimeSpan.TicksPerSecond;
        if (durationSeconds <= 0 || durationSeconds > int.MaxValue)
        {
            failureReason = "recurring_execution_duration_out_of_range";
            return false;
        }

        var bounds = specification.VirtualScreenRegion;
        var displayBounds = specification.DisplayBounds;
        if (bounds.Width <= 0 ||
            bounds.Height <= 0 ||
            displayBounds.Width <= 0 ||
            displayBounds.Height <= 0 ||
            specification.PhysicalWidth != displayBounds.Width ||
            specification.PhysicalHeight != displayBounds.Height ||
            string.IsNullOrWhiteSpace(specification.StableDisplayFingerprint) ||
            string.IsNullOrWhiteSpace(specification.FrozenOutputFilePath) ||
            specification.Backend != AuthorizedCaptureBackend.FfmpegRegion ||
            specification.AudioMode != AuthorizedAudioMode.None ||
            specification.OutputConflictPolicy != AuthorizedOutputConflictPolicy.FailIfExists ||
            specification.CountdownSeconds is < CaptureConfig.MinCountdownSeconds or > CaptureConfig.MaxCountdownSeconds)
        {
            failureReason = "recurring_execution_specification_invalid";
            return false;
        }

        config = new CaptureConfig
        {
            SourceKind = "region",
            Mode = "video",
            Bounds = (bounds.X, bounds.Y, bounds.Width, bounds.Height),
            DisplayBounds = (displayBounds.X, displayBounds.Y, displayBounds.Width, displayBounds.Height),
            DisplayStableIdentity = specification.StableDisplayFingerprint,
            DisplayIdentityStatus = DisplayIdentityResolutionStatus.Resolved,
            OutputPath = specification.FrozenOutputFilePath,
            OutputConflictPolicy = "fail_if_exists",
            AudioSourceKind = AudioCaptureSourceKind.None,
            Microphone = false,
            MicDevice = null,
            MicDeviceName = null,
            SystemLoopbackEndpoint = null,
            SystemLoopbackEndpointName = null,
            SystemLoopbackEndpointIsDefault = null,
            WindowTitle = null,
            WindowHandle = nint.Zero,
            ScreenshotSeries = null,
            Fps = FixedFramesPerSecond,
            Quality = FixedQuality,
            DurationSeconds = checked((int)durationSeconds),
            CountdownSeconds = specification.CountdownSeconds,
            CommandArgs = "",
            RegionNormalizedBounds = null,
            DeferCaptureStart = false,
        };

        failureReason = "";
        return true;
    }

    private static bool TryValidateSpecificationForExecution(
        RecurringLeaseCaptureExecutionTicket ticket,
        RecurringOccurrenceExecutionSpecification specification,
        out string failureReason)
    {
        failureReason = "recurring_execution_specification_invalid";
        if (specification is null ||
            ticket.PlanIsOneTime ||
            !ticket.PlanIsPeriodic ||
            ticket.PlanStatus != PlanDefinitionStatus.Enabled ||
            ticket.OccurrenceStatus != PlanOccurrenceStatus.RunCreated ||
            ticket.UseStatus != LeaseUseStatus.StartCommitted ||
            (ticket.LeaseStatus != ConsentLeaseStatus.Active &&
                ticket.LeaseStatus != ConsentLeaseStatus.Exhausted) ||
            ticket.RunStatus != RecordingRunStatus.StartCommitted ||
            !ticket.RunHasCrossedStartCommit ||
            !ticket.UseIsQuotaConsumed ||
            !string.Equals(ticket.OccurrenceRunId, ticket.RunId, StringComparison.Ordinal) ||
            !string.Equals(ticket.RunOccurrenceId, ticket.OccurrenceId, StringComparison.Ordinal) ||
            !string.Equals(ticket.UseRunId, ticket.RunId, StringComparison.Ordinal) ||
            !string.Equals(ticket.UseLeaseId, ticket.LeaseId, StringComparison.Ordinal) ||
            !string.Equals(ticket.UseOccurrenceId, ticket.OccurrenceId, StringComparison.Ordinal) ||
            !string.Equals(ticket.OccurrencePlanId, ticket.PlanId, StringComparison.Ordinal) ||
            !string.Equals(ticket.LeasePlanId, ticket.PlanId, StringComparison.Ordinal) ||
            !string.Equals(ticket.LeaseOccurrenceId, ticket.OccurrenceId, StringComparison.Ordinal) ||
            specification.Backend != AuthorizedCaptureBackend.FfmpegRegion ||
            specification.AudioMode != AuthorizedAudioMode.None ||
            ticket.TargetType != AuthorizedScopeTargetType.FixedRegion ||
            ticket.CaptureSemantics != AuthorizedCaptureSemantics.DesktopRegion ||
            ticket.CoordinateSpace != AuthorizedCoordinateSpace.PhysicalVirtualScreen ||
            ticket.DisplayIdentityStatus != AuthorizedDisplayIdentityStatus.Resolved ||
            ticket.RebindPolicy != RecurringFixedRegionRebindPolicy.ExactMatchOnly ||
            ticket.Backend != AuthorizedCaptureBackend.FfmpegRegion ||
            ticket.AudioMode != AuthorizedAudioMode.None ||
            ticket.OutputConflictPolicy != AuthorizedOutputConflictPolicy.FailIfExists ||
            ticket.WakePolicy != AuthorizedWakePolicy.NaturalWakeOnly ||
            ticket.DesktopRequirement != AuthorizedDesktopRequirement.InteractiveDesktopRequired ||
            specification.OutputConflictPolicy != AuthorizedOutputConflictPolicy.FailIfExists ||
            specification.CountdownSeconds is < CaptureConfig.MinCountdownSeconds or > CaptureConfig.MaxCountdownSeconds)
        {
            failureReason = "recurring_execution_policy_not_supported";
            return false;
        }

        if (!string.Equals(specification.PlanId, ticket.PlanId, StringComparison.Ordinal) ||
            !string.Equals(specification.OccurrenceId, ticket.OccurrenceId, StringComparison.Ordinal) ||
            !string.Equals(specification.LeaseId, ticket.LeaseId, StringComparison.Ordinal) ||
            !string.Equals(specification.OccurrenceIdentity, ticket.OccurrenceIdentity, StringComparison.Ordinal) ||
            !string.Equals(specification.ProfileId, ticket.ProfileId, StringComparison.Ordinal) ||
            specification.ProfileVersion != ticket.ProfileVersion ||
            !string.Equals(specification.ProfileDigest, ticket.ProfileDigest, StringComparison.Ordinal) ||
            !string.Equals(specification.StableDisplayFingerprint, ticket.StableDisplayFingerprint, StringComparison.Ordinal) ||
            specification.DisplayBounds != ticket.DisplayBounds ||
            specification.RegionWithinDisplay != ticket.RegionWithinDisplay ||
            specification.VirtualScreenRegion != ticket.VirtualScreenRegion ||
            specification.DpiX != ticket.DpiX ||
            specification.DpiY != ticket.DpiY ||
            specification.PhysicalWidth != ticket.PhysicalWidth ||
            specification.PhysicalHeight != ticket.PhysicalHeight ||
            specification.Orientation != ticket.Orientation ||
            !string.Equals(specification.TopologyDigest, ticket.TopologyDigest, StringComparison.Ordinal) ||
            specification.Duration != ticket.Duration ||
            specification.CountdownSeconds != ticket.CountdownSeconds ||
            !string.Equals(specification.NormalizedOutputDirectory, ticket.NormalizedOutputDirectory, StringComparison.Ordinal) ||
            !string.Equals(specification.FrozenOutputFilePath, ticket.OutputFilePath, StringComparison.Ordinal))
        {
            failureReason = "recurring_execution_specification_invalid";
            return false;
        }

        failureReason = "";
        return true;
    }

    private static bool TryValidateProofBinding(
        RecurringLeaseCaptureExecutionTicket ticket,
        RecurringOccurrenceExecutionSpecification specification,
        RecurringLeaseUseProof proof,
        out string failureReason)
    {
        failureReason = "recurring_execution_proof_binding_invalid";
        if (!proof.IsConsumed ||
            proof.Kind != CaptureAuthorizationProofKind.RecurringLeaseUse ||
            !string.Equals(proof.RecordingId, ticket.RunId, StringComparison.Ordinal) ||
            !string.Equals(proof.RunId, ticket.RunId, StringComparison.Ordinal) ||
            !string.Equals(proof.LeaseId, ticket.LeaseId, StringComparison.Ordinal) ||
            !string.Equals(proof.LeaseUseId, ticket.UseId, StringComparison.Ordinal) ||
            !string.Equals(proof.OccurrenceIdentity, specification.OccurrenceIdentity, StringComparison.Ordinal) ||
            !string.Equals(proof.SpecificationDigest, specification.SpecificationDigest, StringComparison.Ordinal) ||
            !string.Equals(proof.CurrentUserSid, specification.ApprovedCurrentUserSid, StringComparison.Ordinal) ||
            !string.Equals(proof.SessionBinding, specification.ApprovedSessionBinding, StringComparison.Ordinal) ||
            !string.Equals(proof.UserSessionBinding, proof.CurrentUserSid + "|" + proof.SessionBinding, StringComparison.Ordinal) ||
            proof.MaxDuration != specification.Duration ||
            proof.MaxDuration != ticket.UseReservedDuration ||
            proof.MaxDuration != ticket.LeasePerRunDuration ||
            proof.MaxDurationMilliseconds != proof.MaxDuration.Ticks / TimeSpan.TicksPerMillisecond ||
            proof.MaxDurationSeconds is not null ||
            proof.MaxFrameCount is not null ||
            proof.IssuedAtUtc != ticket.RunUpdatedAtUtc ||
            proof.IssuedAtUtc != ticket.UseUpdatedAtUtc)
        {
            return false;
        }

        var expectedProofExpiry = specification.PlannedEndUtc <= ticket.LeaseValidUntilUtc
            ? specification.PlannedEndUtc
            : ticket.LeaseValidUntilUtc;
        if (proof.ExpiresAtUtc != expectedProofExpiry)
        {
            failureReason = "recurring_execution_proof_time_binding_invalid";
            return false;
        }

        failureReason = "";
        return true;
    }

    private static bool TryValidateExecutionTime(
        RecurringLeaseCaptureExecutionTicket ticket,
        RecurringOccurrenceExecutionSpecification specification,
        RecurringLeaseUseProof proof,
        DateTimeOffset nowUtc,
        out string failureReason)
    {
        failureReason = "recurring_execution_clock_unavailable";
        if (nowUtc.Offset != TimeSpan.Zero ||
            specification.ScheduledStartUtc.Offset != TimeSpan.Zero ||
            specification.LatestStartUtc.Offset != TimeSpan.Zero ||
            specification.PlannedEndUtc.Offset != TimeSpan.Zero ||
            specification.EvaluatedAtUtc.Offset != TimeSpan.Zero ||
            proof.IssuedAtUtc.Offset != TimeSpan.Zero ||
            proof.ExpiresAtUtc.Offset != TimeSpan.Zero ||
            ticket.OccurrenceWindowStartUtc.Offset != TimeSpan.Zero ||
            ticket.OccurrenceWindowEndUtc.Offset != TimeSpan.Zero ||
            ticket.LeaseValidFromUtc.Offset != TimeSpan.Zero ||
            ticket.LeaseValidUntilUtc.Offset != TimeSpan.Zero)
        {
            failureReason = "recurring_execution_time_not_utc";
            return false;
        }

        if (specification.LatestStartUtc < specification.ScheduledStartUtc ||
            specification.PlannedEndUtc <= specification.ScheduledStartUtc ||
            ticket.OccurrenceWindowEndUtc <= ticket.OccurrenceWindowStartUtc ||
            ticket.LeaseValidUntilUtc <= ticket.LeaseValidFromUtc ||
            proof.ExpiresAtUtc <= proof.IssuedAtUtc)
        {
            failureReason = "recurring_execution_time_window_invalid";
            return false;
        }

        if (nowUtc < proof.IssuedAtUtc || nowUtc < specification.ScheduledStartUtc)
        {
            failureReason = "recurring_execution_not_yet_valid";
            return false;
        }

        if (nowUtc > specification.LatestStartUtc)
        {
            failureReason = "recurring_execution_start_window_expired";
            return false;
        }

        if (nowUtc >= proof.ExpiresAtUtc ||
            nowUtc >= ticket.OccurrenceWindowEndUtc ||
            nowUtc >= ticket.LeaseValidUntilUtc)
        {
            failureReason = "recurring_execution_window_expired";
            return false;
        }

        if (nowUtc < ticket.OccurrenceWindowStartUtc ||
            nowUtc < ticket.LeaseValidFromUtc)
        {
            failureReason = "recurring_execution_window_invalid";
            return false;
        }

        var duration = specification.Duration;
        if (duration <= TimeSpan.Zero ||
            duration.Ticks % TimeSpan.TicksPerMillisecond != 0 ||
            duration > AuthorizedFixedRegionScope.MaximumReservedDuration ||
            duration != ticket.Duration ||
            duration != ticket.UseReservedDuration ||
            duration != ticket.LeasePerRunDuration ||
            proof.MaxDuration != duration ||
            proof.MaxDurationMilliseconds != duration.Ticks / TimeSpan.TicksPerMillisecond)
        {
            failureReason = "recurring_execution_duration_not_representable";
            return false;
        }

        var expectedProofExpiry = specification.PlannedEndUtc <= ticket.LeaseValidUntilUtc
            ? specification.PlannedEndUtc
            : ticket.LeaseValidUntilUtc;
        if (proof.ExpiresAtUtc != expectedProofExpiry)
        {
            failureReason = "recurring_execution_proof_time_binding_invalid";
            return false;
        }

        long endTicks;
        try
        {
            endTicks = checked(nowUtc.UtcDateTime.Ticks + duration.Ticks);
        }
        catch (OverflowException)
        {
            failureReason = "recurring_execution_duration_out_of_scope";
            return false;
        }

        if (endTicks > proof.ExpiresAtUtc.UtcDateTime.Ticks ||
            endTicks > specification.PlannedEndUtc.UtcDateTime.Ticks ||
            endTicks > ticket.OccurrenceWindowEndUtc.UtcDateTime.Ticks ||
            endTicks > ticket.LeaseValidUntilUtc.UtcDateTime.Ticks)
        {
            failureReason = "recurring_execution_duration_out_of_scope";
            return false;
        }

        failureReason = "";
        return true;
    }

    private bool TryReadUtcNow(out DateTimeOffset nowUtc, out string failureReason)
    {
        nowUtc = default;
        failureReason = "recurring_execution_clock_unavailable";

        DateTimeOffset? sampled;
        try
        {
            sampled = _utcNow();
        }
        catch
        {
            return false;
        }

        if (sampled is null)
            return false;

        nowUtc = sampled.Value;
        if (nowUtc.Offset != TimeSpan.Zero)
        {
            failureReason = "recurring_execution_time_not_utc";
            return false;
        }

        failureReason = "";
        return true;
    }

    private static string MapSafetyFailureReason(RecurringLeaseCurrentSafetyStatus status) => status switch
    {
        RecurringLeaseCurrentSafetyStatus.Allowed => "recurring_execution_safety_state_invalid",
        RecurringLeaseCurrentSafetyStatus.UnattendedDisabled =>
            "recurring_execution_unattended_disabled",
        RecurringLeaseCurrentSafetyStatus.StopAllActive =>
            "recurring_execution_stop_all_active",
        RecurringLeaseCurrentSafetyStatus.LeaseRevoked =>
            "recurring_execution_lease_revoked",
        RecurringLeaseCurrentSafetyStatus.ReenableRequiresNewAuthorization =>
            "recurring_execution_reenable_requires_new_authorization",
        RecurringLeaseCurrentSafetyStatus.StateInvalid =>
            "recurring_execution_safety_state_invalid",
        _ => "recurring_execution_safety_state_invalid",
    };

    private static bool IsDurableMediaSettled(
        RecurringLeaseLifecycleActionResult? terminalResult) =>
        terminalResult is
        {
            Succeeded: true,
            Terminal: true,
            Reason: "media_settled",
        };

    private static void DisposeBackendOnce(ICaptureBackend backend)
    {
        try
        {
            backend.Dispose();
        }
        catch
        {
            // Disposal is best effort; the bridge still reports the original
            // failed/rejected start boundary with a stable reason.
        }
    }

    private static void DisposeLifecycleSessionOnce(IRecurringLeaseCaptureLifecycleSession lifecycleSession)
    {
        try
        {
            lifecycleSession.Dispose();
        }
        catch
        {
            // Lifecycle cleanup is best effort. The bridge reports only a
            // stable closed reason and never exposes exception text.
        }
    }

    private static ICaptureBackend? UnsupportedBackendFactory(
        RecurringOccurrenceExecutionSpecification specification) => null;
}

internal enum RecurringLeaseCaptureExecutionStatus
{
    Started,
    CompletedDuringStart,
    Rejected,
    Failed,
    Cancelled,
}

/// <summary>
/// Internal result whose backend is transferred only on the Started path.
/// It has no public construction, properties, serialization, or retry API.
/// </summary>
internal sealed class RecurringLeaseCaptureExecutionResult
{
    private RecurringLeaseCaptureExecutionResult(
        RecurringLeaseCaptureExecutionStatus status,
        string reason,
        ICaptureBackend? backend,
        IRecurringLeaseCaptureLifecycleSession? lifecycleSession)
    {
        Status = status;
        Reason = reason;
        Backend = backend;
        LifecycleSession = lifecycleSession;
    }

    internal RecurringLeaseCaptureExecutionStatus Status { get; }

    internal string Reason { get; }

    internal ICaptureBackend? Backend { get; }

    internal IRecurringLeaseCaptureLifecycleSession? LifecycleSession { get; }

    internal static RecurringLeaseCaptureExecutionResult Started(
        ICaptureBackend backend,
        IRecurringLeaseCaptureLifecycleSession lifecycleSession) =>
        new(RecurringLeaseCaptureExecutionStatus.Started, "", backend, lifecycleSession);

    internal static RecurringLeaseCaptureExecutionResult CompletedDuringStart(string reason) =>
        new(RecurringLeaseCaptureExecutionStatus.CompletedDuringStart, reason, null, null);

    internal static RecurringLeaseCaptureExecutionResult Rejected(string reason) =>
        new(RecurringLeaseCaptureExecutionStatus.Rejected, reason, null, null);

    internal static RecurringLeaseCaptureExecutionResult Failed(string reason) =>
        new(RecurringLeaseCaptureExecutionStatus.Failed, reason, null, null);

    internal static RecurringLeaseCaptureExecutionResult Cancelled() =>
        new(RecurringLeaseCaptureExecutionStatus.Cancelled, "recurring_execution_cancelled", null, null);
}
