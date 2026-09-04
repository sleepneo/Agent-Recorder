using System.Threading;
using AgentRecorder.Capture;
using AgentRecorder.Core.Automation;
using AgentRecorder.Windows;

namespace AgentRecorder.Core;

/// <summary>
/// Process-local bridge from a claimed standing ticket to the existing
/// proof-bearing capture backend. It is deliberately not connected to
/// RecordingEngine, scheduler, API, UI, or any application entry point.
/// </summary>
internal sealed class StandingLeaseCaptureExecutionBridge
{
    private const int FixedFramesPerSecond = 30;
    private const string FixedQuality = "medium";

    private readonly IStandingLeaseExecutionEnvironmentProvider? _environmentProvider;
    private readonly Func<StandingLeaseCaptureSpecification, ICaptureBackend?> _backendFactory;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private readonly Func<DateTimeOffset?> _utcNow;

    internal StandingLeaseCaptureExecutionBridge()
        : this(
            environmentProviderForTest: null,
            backendFactoryForTest: null,
            delayForTest: null,
            utcNowForTest: null)
    {
    }

    // Internal seams are deterministic test seams. Production uses the
    // parameterless constructor and the fixed FFmpeg-region factory below.
    internal StandingLeaseCaptureExecutionBridge(
        IStandingLeaseExecutionEnvironmentProvider? environmentProviderForTest,
        Func<StandingLeaseCaptureSpecification, ICaptureBackend?>? backendFactoryForTest,
        Func<TimeSpan, CancellationToken, Task>? delayForTest = null,
        Func<DateTimeOffset?>? utcNowForTest = null)
    {
        _environmentProvider = environmentProviderForTest;
        _backendFactory = backendFactoryForTest ?? CreateProductionBackend;
        _delay = delayForTest ?? ((duration, cancellationToken) => Task.Delay(duration, cancellationToken));
        _utcNow = utcNowForTest ?? (() => DateTimeOffset.UtcNow);
    }

    internal async Task<StandingLeaseCaptureExecutionResult> ExecuteAsync(
        StandingLeaseCaptureExecutionTicket? ticket,
        CancellationToken cancellationToken = default)
    {
        if (ticket is null)
            return StandingLeaseCaptureExecutionResult.Rejected("standing_execution_ticket_missing");

        if (!ticket.TryClaim(out var claimFailure))
            return StandingLeaseCaptureExecutionResult.Rejected(claimFailure);

        if (!ticket.IsProofConsumed)
            return StandingLeaseCaptureExecutionResult.Rejected("standing_execution_proof_not_consumed");

        var specification = ticket.Specification;
        if (!TryValidateSpecificationForExecution(specification, out var specificationFailure))
            return StandingLeaseCaptureExecutionResult.Rejected(specificationFailure);

        var duration = TimeSpan.FromSeconds(specification.CountdownSeconds);
        if (specification.CountdownSeconds > 0)
        {
            try
            {
                await _delay(duration, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return StandingLeaseCaptureExecutionResult.Cancelled();
            }
            catch
            {
                return StandingLeaseCaptureExecutionResult.Failed("standing_execution_countdown_failed");
            }
        }

        if (cancellationToken.IsCancellationRequested)
            return StandingLeaseCaptureExecutionResult.Cancelled();

        if (!ticket.IsProofConsumed)
            return StandingLeaseCaptureExecutionResult.Rejected("standing_execution_proof_not_consumed");

        if (!TryReadUtcNow(out var executionNowUtc, out var timeFailure) ||
            !TryValidateExecutionTime(ticket, executionNowUtc, out timeFailure))
        {
            return StandingLeaseCaptureExecutionResult.Rejected(timeFailure);
        }

        StandingLeaseExecutionEnvironment? environment;
        string environmentFailure = "standing_execution_environment_unavailable";
        try
        {
            environment = _environmentProvider?.Capture(ticket.Scope) ??
                StandingLeaseExecutionEnvironment.CaptureCurrent(ticket.Scope);
        }
        catch
        {
            return StandingLeaseCaptureExecutionResult.Rejected("standing_execution_environment_unavailable");
        }

        if (environment is null || environment.NowUtc.Offset != TimeSpan.Zero)
            return StandingLeaseCaptureExecutionResult.Rejected("standing_execution_time_not_utc");

        if (!StandingLeaseExecutionEnvironmentValidator.TryValidate(
            environment,
            ticket.Scope,
            out environmentFailure))
        {
            return StandingLeaseCaptureExecutionResult.Rejected(environmentFailure);
        }

        if (!TryMapCaptureConfig(specification, out var config, out var configFailure))
            return StandingLeaseCaptureExecutionResult.Rejected(configFailure);

        // This is deliberately the final await-free authorization check. It
        // must remain immediately before the backend factory so a valid
        // window cannot be consumed by environment/configuration work after
        // the last time sample.
        if (!TryReadUtcNow(out executionNowUtc, out timeFailure) ||
            !TryValidateExecutionTime(ticket, executionNowUtc, out timeFailure))
        {
            return StandingLeaseCaptureExecutionResult.Rejected(timeFailure);
        }

        ICaptureBackend? backend = null;
        try
        {
            backend = _backendFactory(specification);
            if (backend is null)
                return StandingLeaseCaptureExecutionResult.Failed("standing_execution_backend_unavailable");

            backend.Start(config, ticket.GetConsumedProofForBackendStart());
            return StandingLeaseCaptureExecutionResult.Started(backend);
        }
        catch
        {
            if (backend is not null)
            {
                try
                {
                    backend.Dispose();
                }
                catch
                {
                    // The start failure remains the authoritative result.
                }
            }

            return StandingLeaseCaptureExecutionResult.Failed("standing_execution_backend_start_failed");
        }
    }

    private bool TryReadUtcNow(out DateTimeOffset nowUtc, out string failureReason)
    {
        nowUtc = default;
        failureReason = "standing_execution_clock_unavailable";

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
            failureReason = "standing_execution_time_not_utc";
            return false;
        }

        failureReason = "";
        return true;
    }

    private static bool TryValidateExecutionTime(
        StandingLeaseCaptureExecutionTicket ticket,
        DateTimeOffset nowUtc,
        out string failureReason)
    {
        failureReason = "standing_execution_clock_unavailable";

        StandingLeaseUseProof proof;
        StandingLeaseCaptureSpecification specification;
        try
        {
            proof = ticket.GetConsumedProofForBackendStart() as StandingLeaseUseProof ??
                throw new InvalidOperationException("A standing proof is required.");
            specification = ticket.Specification;
        }
        catch
        {
            return false;
        }

        if (nowUtc.Offset != TimeSpan.Zero ||
            proof.IssuedAtUtc.Offset != TimeSpan.Zero ||
            proof.ExpiresAtUtc.Offset != TimeSpan.Zero)
        {
            failureReason = "standing_execution_time_not_utc";
            return false;
        }

        if (proof.ExpiresAtUtc <= proof.IssuedAtUtc)
        {
            failureReason = "standing_execution_clock_unavailable";
            return false;
        }

        if (nowUtc < proof.IssuedAtUtc)
        {
            failureReason = "standing_execution_not_yet_valid";
            return false;
        }

        if (nowUtc >= proof.ExpiresAtUtc)
        {
            failureReason = "standing_execution_window_expired";
            return false;
        }

        var duration = specification.MaximumDuration;
        if (duration <= TimeSpan.Zero ||
            duration.Ticks % TimeSpan.TicksPerMillisecond != 0 ||
            duration > AuthorizedFixedRegionScope.MaximumReservedDuration)
        {
            failureReason = "standing_execution_duration_not_representable";
            return false;
        }

        long endTicks;
        try
        {
            endTicks = checked(nowUtc.UtcDateTime.Ticks + duration.Ticks);
        }
        catch (OverflowException)
        {
            failureReason = "standing_execution_duration_exceeds_window";
            return false;
        }

        if (endTicks > proof.ExpiresAtUtc.UtcDateTime.Ticks)
        {
            failureReason = "standing_execution_duration_exceeds_window";
            return false;
        }

        failureReason = "";
        return true;
    }

    private static bool TryValidateSpecificationForExecution(
        StandingLeaseCaptureSpecification specification,
        out string failureReason)
    {
        failureReason = "standing_execution_specification_invalid";
        if (specification is null)
            return false;

        if (specification.TargetType != AuthorizedScopeTargetType.FixedRegion ||
            specification.CaptureSemantics != AuthorizedCaptureSemantics.DesktopRegion ||
            specification.CoordinateSpace != AuthorizedCoordinateSpace.PhysicalVirtualScreen ||
            specification.Backend != AuthorizedCaptureBackend.FfmpegRegion ||
            !string.Equals(specification.BackendCodeValue, StandingLeaseCaptureSpecification.BackendCode, StringComparison.Ordinal) ||
            specification.AudioMode != AuthorizedAudioMode.None ||
            !string.Equals(specification.AudioCodeValue, StandingLeaseCaptureSpecification.AudioCode, StringComparison.Ordinal) ||
            specification.OutputConflictPolicy != AuthorizedOutputConflictPolicy.FailIfExists ||
            specification.WakePolicy != AuthorizedWakePolicy.NaturalWakeOnly ||
            !string.Equals(specification.WakePolicyCodeValue, StandingLeaseCaptureSpecification.WakePolicyCode, StringComparison.Ordinal) ||
            specification.DesktopRequirement != AuthorizedDesktopRequirement.InteractiveDesktopRequired ||
            !string.Equals(specification.DesktopRequirementCodeValue, StandingLeaseCaptureSpecification.DesktopRequirementCode, StringComparison.Ordinal) ||
            specification.CountdownStrategy != StandingLeaseCountdownStrategy.FixedSeconds ||
            !string.Equals(specification.CountdownStrategyCodeValue, StandingLeaseCaptureSpecification.CountdownStrategyCode, StringComparison.Ordinal) ||
            specification.CountdownSeconds is < CaptureConfig.MinCountdownSeconds or > CaptureConfig.MaxCountdownSeconds)
        {
            failureReason = "standing_execution_policy_not_supported";
            return false;
        }

        return true;
    }

    private static bool TryMapCaptureConfig(
        StandingLeaseCaptureSpecification specification,
        out CaptureConfig config,
        out string failureReason)
    {
        config = new CaptureConfig();
        failureReason = "standing_execution_specification_invalid";

        var duration = specification.MaximumDuration;
        if (duration <= TimeSpan.Zero ||
            duration.Ticks % TimeSpan.TicksPerSecond != 0 ||
            duration > AuthorizedFixedRegionScope.MaximumReservedDuration)
        {
            failureReason = "standing_execution_duration_not_representable";
            return false;
        }

        var durationSeconds = duration.Ticks / TimeSpan.TicksPerSecond;
        if (durationSeconds <= 0 || durationSeconds > int.MaxValue)
        {
            failureReason = "standing_execution_duration_out_of_range";
            return false;
        }

        var bounds = specification.VirtualScreenRegion;
        var displayBounds = specification.DisplayBounds;
        if (bounds.Width <= 0 || bounds.Height <= 0 ||
            displayBounds.Width <= 0 || displayBounds.Height <= 0 ||
            specification.PhysicalWidth != displayBounds.Width ||
            specification.PhysicalHeight != displayBounds.Height ||
            string.IsNullOrWhiteSpace(specification.StableDisplayFingerprint) ||
            string.IsNullOrWhiteSpace(specification.OutputFilePath))
        {
            failureReason = "standing_execution_specification_invalid";
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
            OutputPath = specification.OutputFilePath,
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

    private static ICaptureBackend CreateProductionBackend(
        StandingLeaseCaptureSpecification specification)
    {
        if (specification.Backend != AuthorizedCaptureBackend.FfmpegRegion ||
            !string.Equals(specification.BackendCodeValue, StandingLeaseCaptureSpecification.BackendCode, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("The standing bridge only supports the approved FFmpeg region backend.");
        }

        return new FfmpegCaptureBackend();
    }
}
