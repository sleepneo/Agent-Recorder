using System;
using System.Collections.Concurrent;
using System.Drawing;
using System.Linq;
using System.Security.Principal;
using System.Threading;
using System.Threading.Tasks;
using AgentRecorder.Capture;
using AgentRecorder.Api;
using AgentRecorder.Core;
using AgentRecorder.Core.Automation;
using AgentRecorder.Infrastructure;
using AgentRecorder.Logging;
using AgentRecorder.Persistence;
using AgentRecorder.Windows;

namespace AgentRecorder.App;

/// <summary>
/// Production adapter for the first one-shot Plan API.  It is intentionally
/// not a scheduler: it only performs the durable setup flow and leaves the
/// future execution to the later natural-wake dispatcher.
/// </summary>
internal sealed class StandingPlanSetupCoordinator : IStandingPlanSetupGateway, IDisposable
{
    private readonly SqliteOperationalStore _store;
    private readonly AuditLogger _audit;
    private readonly IStandingPlanSetupUi _setupUi;
    private readonly StandingLeaseSafetyControlService _safety;
    private readonly StandingSetupIntentService _intents;
    private readonly StandingLeasePreparationService _preparation;
    private readonly StandingLeasePreparedIntentApprovalActivationService _activation;
    private readonly StandingPlanSetupQueryService _query;
    private readonly StandingPlanSetupTerminalService _terminal;
    private readonly Func<IReadOnlyList<StandingLeaseDisplayMetadata>> _currentDisplays;
    private readonly ConcurrentDictionary<string, Lazy<Task>> _flights = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _setupUiGate = new(1, 1);
    private readonly CancellationTokenSource _lifetimeCts = new();
    private readonly object _approvalCommitGate = new();
    private readonly Func<Task>? _beforeApprovalCommitForTest;
    private readonly Action? _approvalCommitLinearizedForTest;
    private readonly Action? _shutdownLinearizedForTest;
    private readonly Func<bool> _executionSupported;
    private readonly Func<string, bool> _recordingExists;
    private readonly IStandingLeaseOutputReadinessProvider _outputReadinessProvider;
    private int _disposed;
    private int _disposeRequested;
    private int _shutdownLinearized;

    internal StandingPlanSetupCoordinator(
        SqliteOperationalStore store,
        AuditLogger audit,
        TrayContext tray,
        StandingLeaseSafetyControlService safety,
        Func<bool>? executionSupportedForTest = null,
        Func<string, bool>? recordingExistsForTest = null,
        IStandingLeaseOutputReadinessProvider? outputReadinessProviderForTest = null)
        : this(store, audit, new TrayStandingPlanSetupUi(tray), safety, null, null, null, null, executionSupportedForTest, recordingExistsForTest, outputReadinessProviderForTest)
    {
    }

    internal StandingPlanSetupCoordinator(
        SqliteOperationalStore store,
        AuditLogger audit,
        IStandingPlanSetupUi setupUi,
        StandingLeaseSafetyControlService safety,
        Func<IReadOnlyList<StandingLeaseDisplayMetadata>>? currentDisplaysForTest = null,
        Func<Task>? beforeApprovalCommitForTest = null,
        Action? approvalCommitLinearizedForTest = null,
        Action? shutdownLinearizedForTest = null,
        Func<bool>? executionSupportedForTest = null,
        Func<string, bool>? recordingExistsForTest = null,
        IStandingLeaseOutputReadinessProvider? outputReadinessProviderForTest = null)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _audit = audit ?? throw new ArgumentNullException(nameof(audit));
        _setupUi = setupUi ?? throw new ArgumentNullException(nameof(setupUi));
        _safety = safety ?? throw new ArgumentNullException(nameof(safety));
        _intents = new StandingSetupIntentService(store);
        _preparation = new StandingLeasePreparationService(store);
        _activation = new StandingLeasePreparedIntentApprovalActivationService(store);
        _query = new StandingPlanSetupQueryService(store);
        _terminal = new StandingPlanSetupTerminalService(store);
        _currentDisplays = currentDisplaysForTest ??
            (() => SystemQueryDisplayTopologyProvider.Instance.GetCurrentExecutionMetadata());
        _beforeApprovalCommitForTest = beforeApprovalCommitForTest;
        _approvalCommitLinearizedForTest = approvalCommitLinearizedForTest;
        _shutdownLinearizedForTest = shutdownLinearizedForTest;
        _executionSupported = executionSupportedForTest ?? (() => false);
        _recordingExists = recordingExistsForTest ?? (_ => false);
        _outputReadinessProvider = outputReadinessProviderForTest ??
            SystemQueryStandingLeaseOutputReadinessProvider.Instance;
        RecoverPendingSetups();
    }

    public bool IsInteractiveDesktopAvailable => _setupUi.IsInteractiveDesktopAvailable;

    public bool IsUnattendedEnabled =>
        _safety.IsUnattendedEnabled(CurrentUserSid());

    public bool IsExecutionSupported
    {
        get
        {
            try { return _executionSupported(); } catch { return false; }
        }
    }

    public StandingPlanSetupCreateResult CreateOrGet(StandingPlanApiRequest request)
    {
        ObjectDisposedException.ThrowIf(IsDisposed, this);
        ArgumentNullException.ThrowIfNull(request);

        var currentUserSid = CurrentUserSid();
        var sessionBinding = CaptureAuthorizationSessionBinding.Current;
        if (string.IsNullOrWhiteSpace(currentUserSid) || string.IsNullOrWhiteSpace(sessionBinding))
        {
            return new StandingPlanSetupCreateResult(
                StandingPlanSetupCreateStatus.Rejected,
                null,
                "setup_pending",
                0,
                null,
                null,
                null,
                "interactive_desktop_required");
        }

        if (!_safety.IsUnattendedEnabled(currentUserSid))
        {
            return new StandingPlanSetupCreateResult(
                StandingPlanSetupCreateStatus.Rejected,
                null,
                "setup_pending",
                0,
                null,
                null,
                null,
                "unattended_disabled");
        }

        var requestedAtUtc = DateTimeOffset.UtcNow;
        var intentId = "standing-setup-" + Guid.NewGuid().ToString("N");
        StandingSetupIntentSnapshot snapshot;
        try
        {
            snapshot = StandingSetupIntentSnapshot.CreateForTrustedSetupAdapter(
                intentId,
                request.IdempotencyKey,
                currentUserSid,
                sessionBinding,
                requestedAtUtc,
                request.LeaseExpiresUtc,
                request.ScheduledStartUtc,
                request.LatestStartUtc,
                request.PlannedEndUtc,
                request.Duration,
                request.LeaseExpiresUtc,
                request.OutputDirectory,
                request.FrozenFileName);
        }
        catch (Phase3DomainException exception)
        {
            return Rejected(intentId, exception.ReasonCode);
        }

        var result = _intents.CreateOrGet(snapshot);
        var state = result.IntentId is null ? null : ReadState(result.IntentId);
        if (result.IntentId is not null &&
            state?.StatusCode is "setup_pending" or "pending_lease_approval")
        {
            StartFlight(result.IntentId);
        }

        var status = result.Result switch
        {
            StandingSetupIntentResultStatus.Created => StandingPlanSetupCreateStatus.Created,
            StandingSetupIntentResultStatus.Existing => StandingPlanSetupCreateStatus.Existing,
            StandingSetupIntentResultStatus.Conflict => StandingPlanSetupCreateStatus.Conflict,
            StandingSetupIntentResultStatus.Expired => StandingPlanSetupCreateStatus.Expired,
            _ => StandingPlanSetupCreateStatus.Rejected,
        };
        return new StandingPlanSetupCreateResult(
            status,
            result.IntentId,
            state?.StatusCode ?? ProjectionFor(StandingSetupIntentStatusCodes.ToCode(result.IntentStatus)),
            state?.StatusVersion ?? 0,
            state?.PlanId,
            state?.OccurrenceId,
            state?.LeaseId,
            result.Reason is "created" or "existing" ? null : result.Reason,
            state?.StatusVersionCursor);
    }

    public StandingPlanSetupState? Get(string setupIntentId)
    {
        ObjectDisposedException.ThrowIf(IsDisposed, this);
        var currentUserSid = CurrentUserSid();
        var sessionBinding = CaptureAuthorizationSessionBinding.Current;
        var record = _query.Get(setupIntentId, currentUserSid, sessionBinding);
        if (record is null)
            return null;

        if (record.StatusCode is "region_selection_pending" or "lease_approval_pending" &&
            DateTimeOffset.UtcNow >= record.ExpiresAtUtc)
        {
            _terminal.TrySetTerminal(record.IntentId, "setup_intent_expired", expired: true);
            record = _query.Get(setupIntentId, currentUserSid, sessionBinding);
            if (record is null)
                return null;
        }

        return ToState(record, _query.GetExecution(setupIntentId, currentUserSid, sessionBinding));
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposeRequested, 1) == 1)
            return;

        Interlocked.Exchange(ref _disposed, 1);
        lock (_approvalCommitGate)
        {
            Volatile.Write(ref _shutdownLinearized, 1);
            _shutdownLinearizedForTest?.Invoke();
        }
        _lifetimeCts.Cancel();
        var flights = _flights.Values
            .Select(lazy =>
            {
                try { return lazy.Value; }
                catch { return Task.CompletedTask; }
            })
            .Distinct()
            .ToArray();
        try
        {
            Task.WaitAll(flights, TimeSpan.FromSeconds(5));
        }
        catch (AggregateException exception)
        {
            _audit.Log("standing_setup.dispose_wait_failed", new
            {
                reason_code = "setup_shutdown_incomplete",
                exception_type = exception.GetType().Name,
            });
        }

        if (flights.All(flight => flight.IsCompleted))
        {
            _setupUiGate.Dispose();
            _lifetimeCts.Dispose();
        }
        else
        {
            // Do not dispose a semaphore that a still-running flight may
            // release. The process is shutting down; safety beats cleanup.
            _audit.Log("standing_setup.dispose_wait_timeout", new
            {
                reason_code = "setup_shutdown_incomplete",
                pending_flights = flights.Count(flight => !flight.IsCompleted),
            });
        }
    }

    private void StartFlight(string intentId)
    {
        var flight = _flights.GetOrAdd(
            intentId,
            id => new Lazy<Task>(() => RunFlightAsync(id), LazyThreadSafetyMode.ExecutionAndPublication));
        _ = flight.Value;
    }

    private void RecoverPendingSetups()
    {
        var currentUserSid = CurrentUserSid();
        var sessionBinding = CaptureAuthorizationSessionBinding.Current;
        if (string.IsNullOrWhiteSpace(currentUserSid) || string.IsNullOrWhiteSpace(sessionBinding))
            return;

        try
        {
            foreach (var intentId in _query.ListRecoverable(currentUserSid, sessionBinding))
                StartFlight(intentId);
        }
        catch (Exception exception)
        {
            _audit.Log("standing_setup.recovery_scan_failed", new
            {
                reason_code = "setup_persistence_failed",
                exception_type = exception.GetType().Name,
            });
        }
    }

    private async Task RunFlightAsync(string intentId)
    {
        var enteredUiGate = false;
        try
        {
            await _setupUiGate.WaitAsync(_lifetimeCts.Token).ConfigureAwait(false);
            enteredUiGate = true;
            try
            {
                await RunFlightUnderUiGateAsync(intentId).ConfigureAwait(false);
            }
            finally
            {
                _setupUiGate.Release();
                enteredUiGate = false;
            }
        }
        catch (OperationCanceledException) when (_lifetimeCts.IsCancellationRequested)
        {
            // Host shutdown leaves an uncompleted intent pending and
            // recoverable; it is not a setup conflict.
        }
        catch (ObjectDisposedException) when (_lifetimeCts.IsCancellationRequested)
        {
            // The lifetime owner only disposes after waiting for flights.
        }
        catch (Exception exception)
        {
            _audit.Log("standing_setup.failed", new
            {
                reason_code = "setup_conflict",
                exception_type = exception.GetType().Name,
            });
            _terminal.TrySetTerminal(intentId, "setup_conflict");
        }
        finally
        {
            if (enteredUiGate)
                _setupUiGate.Release();
            _flights.TryRemove(intentId, out _);
        }
    }

    private async Task RunFlightUnderUiGateAsync(string intentId)
    {
        var state = Get(intentId);
        if (state is null || state.StatusCode is "scheduled" or "rejected" or "expired")
            return;

        _lifetimeCts.Token.ThrowIfCancellationRequested();

        var currentUserSid = CurrentUserSid();
        var sessionBinding = CaptureAuthorizationSessionBinding.Current;
        if (!_safety.IsUnattendedEnabled(currentUserSid))
        {
            _terminal.TrySetTerminal(intentId, "unattended_disabled");
            return;
        }

        var setupRecord = _query.Get(intentId, currentUserSid, sessionBinding);
        if (setupRecord is null)
        {
            _terminal.TrySetTerminal(intentId, "setup_conflict");
            return;
        }

        // This is deliberately before region selection for a new request and
        // before approval for a recovered pending request. It only observes
        // the exact frozen output target; it never creates a directory or
        // substitutes a fallback path.
        if ((state.StatusCode is "setup_pending" or "pending_lease_approval") &&
            !TryCheckOutputReadiness(
                setupRecord.OutputDirectory,
                setupRecord.FrozenFileName,
                setupRecord.MaximumDuration,
                out var initialOutputFailure))
        {
            await RejectForOutputReadinessAsync(intentId, initialOutputFailure).ConfigureAwait(false);
            return;
        }

        if (state.StatusCode == "setup_pending")
        {
            var selection = await _setupUi.SelectFreshRegionAsync(_lifetimeCts.Token).ConfigureAwait(false);
            if (selection.Status == "host_shutdown")
                return;
            if (selection.Status != "selected")
            {
                _terminal.TrySetTerminal(intentId, SelectionReason(selection.Status));
                return;
            }

            if (!TryBuildSelection(intentId, currentUserSid, sessionBinding, selection, out var snapshot, out var failure))
            {
                _terminal.TrySetTerminal(intentId, failure!);
                return;
            }

            var prepared = _preparation.Prepare(snapshot!);
            if (prepared.Status is not (StandingLeasePreparationResultStatus.Prepared or StandingLeasePreparationResultStatus.Existing))
            {
                _terminal.TrySetTerminal(intentId, PreparationReason(prepared.Reason));
                return;
            }
        }

        var preparedRecord = _query.GetPrepared(intentId, currentUserSid, sessionBinding);
        var intent = _query.Get(intentId, currentUserSid, sessionBinding);
        if (preparedRecord is null || intent is null || intent.StatusCode != "lease_approval_pending")
        {
            if (intent?.StatusCode is "scheduled")
                return;
            _terminal.TrySetTerminal(intentId, "setup_conflict");
            return;
        }

        if (DateTimeOffset.UtcNow >= intent.ExpiresAtUtc)
        {
            _terminal.TrySetTerminal(intentId, "setup_intent_expired", expired: true);
            return;
        }

        var approvalDetails = new StandingLeaseApprovalDetails(
            ResolveDisplayName(preparedRecord.StableDisplayFingerprint),
            preparedRecord.DisplayBounds,
            preparedRecord.RegionWithinDisplay,
            intent.ScheduledStartUtc ?? DateTimeOffset.UtcNow,
            intent.LatestStartUtc ?? DateTimeOffset.UtcNow,
            intent.PlannedEndUtc ?? DateTimeOffset.UtcNow,
            intent.MaximumDuration ?? TimeSpan.Zero,
            intent.LeaseValidUntilUtc ?? DateTimeOffset.UtcNow,
            preparedRecord.OutputDirectory,
            preparedRecord.FrozenFileName);

        var approved = await _setupUi.ShowApprovalAsync(approvalDetails, _lifetimeCts.Token).ConfigureAwait(false);
        if (_lifetimeCts.IsCancellationRequested)
            return;
        if (!approved)
        {
            _terminal.TrySetTerminal(intentId, "lease_rejected_by_user");
            return;
        }

        var approvalUserSid = CurrentUserSid();
        var approvalSession = CaptureAuthorizationSessionBinding.Current;
        if (_lifetimeCts.IsCancellationRequested)
            return;
        if (!string.Equals(approvalUserSid, preparedRecord.CurrentUserSid, StringComparison.Ordinal) ||
            !string.Equals(approvalSession, preparedRecord.SessionBinding, StringComparison.Ordinal) ||
            !_safety.IsUnattendedEnabled(approvalUserSid))
        {
            _terminal.TrySetTerminal(intentId, "setup_conflict");
            return;
        }

        if (_beforeApprovalCommitForTest is not null)
            await _beforeApprovalCommitForTest().ConfigureAwait(false);

        // Re-read the exact prepared target after the approval callback and
        // immediately before activation. The pending chain is terminalized on
        // failure, so no active Lease or executable Occurrence is left behind.
        if (!TryCheckOutputReadiness(
                preparedRecord.OutputDirectory,
                preparedRecord.FrozenFileName,
                intent.MaximumDuration,
                out var approvalOutputFailure))
        {
            await RejectForOutputReadinessAsync(intentId, approvalOutputFailure).ConfigureAwait(false);
            return;
        }

        StandingLeaseAuthorizationActivationResult activated;
        lock (_approvalCommitGate)
        {
            if (Volatile.Read(ref _disposeRequested) != 0 ||
                Volatile.Read(ref _shutdownLinearized) != 0)
            {
                return;
            }

            _approvalCommitLinearizedForTest?.Invoke();

            StandingLeaseLocalApprovalReceipt receipt;
            try
            {
                receipt = StandingLeaseLocalApprovalReceipt.CreateForTrustedLocalApprovalAdapter(
                    "standing-approval-" + Guid.NewGuid().ToString("N"),
                    preparedRecord.PlanId,
                    preparedRecord.OccurrenceId,
                    preparedRecord.LeaseId,
                    preparedRecord.ScopeId,
                    preparedRecord.ScopeDigest,
                    approvalUserSid,
                    approvalSession,
                    DateTimeOffset.UtcNow);
            }
            catch (Phase3DomainException)
            {
                _terminal.TrySetTerminal(intentId, "setup_conflict");
                return;
            }

            activated = _activation.Activate(intentId, receipt);
        }

        if (activated.Status is StandingLeaseAuthorizationActivationStatus.Activated or StandingLeaseAuthorizationActivationStatus.AlreadyActive)
        {
            _audit.Log("consent_lease.approved", new
            {
                intent_id = intentId,
                status = "scheduled",
                reason_code = "scheduled",
            });
        }
        else
        {
            _terminal.TrySetTerminal(intentId, ActivationReason(activated.Reason));
        }
    }

    private bool TryCheckOutputReadiness(
        string? outputDirectory,
        string? frozenFileName,
        TimeSpan? maximumDuration,
        out string failureReason)
    {
        failureReason = "execution_output_environment_unavailable";
        StandingLeaseOutputReadinessResult result;
        try
        {
            result = _outputReadinessProvider.Check(
                outputDirectory ?? string.Empty,
                frozenFileName ?? string.Empty,
                maximumDuration ?? TimeSpan.Zero);
        }
        catch
        {
            return false;
        }

        if (result.IsReady)
            return true;

        if (!string.IsNullOrWhiteSpace(result.ReasonCode))
            failureReason = result.ReasonCode;
        _audit.Log("standing_setup.output_readiness_failed", new
        {
            reason_code = failureReason,
        });
        return false;
    }

    private async Task RejectForOutputReadinessAsync(string intentId, string reasonCode)
    {
        if (!_terminal.TrySetTerminal(intentId, reasonCode))
            return;

        try
        {
            await _setupUi.ShowFailureAsync(reasonCode, _lifetimeCts.Token).ConfigureAwait(false);
        }
        catch
        {
            _audit.Log("standing_setup.output_readiness_feedback_failed", new
            {
                reason_code = reasonCode,
            });
        }
    }

    private bool TryBuildSelection(
        string intentId,
        string currentUserSid,
        string sessionBinding,
        StandingPlanSetupSelection selection,
        out StandingFixedRegionSelectionSnapshot? snapshot,
        out string? failure)
    {
        snapshot = null;
        failure = null;
        if (!string.Equals(selection.CoordinateSpace, "virtual_screen", StringComparison.Ordinal) ||
            selection.Width <= 0 || selection.Height <= 0)
        {
            failure = "region_selection_invalid";
            return false;
        }

        try
        {
            var selected = new AuthorizedPhysicalRectangle(selection.X, selection.Y, selection.Width, selection.Height);
            var displays = _currentDisplays();
            if (!StandingLeaseDisplayTopologyDigest.TryCompute(displays, out var topologyDigest))
            {
                failure = "display_identity_unresolved";
                return false;
            }

            var matches = displays.Where(display =>
                    display.IdentityStatus == DisplayIdentityResolutionStatus.Resolved &&
                    !string.IsNullOrWhiteSpace(display.StableDisplayFingerprint) &&
                    display.PhysicalBounds is not null &&
                    IsContained(selected, display.PhysicalBounds.Value))
                .ToArray();
            if (matches.Length != 1)
            {
                failure = "region_spans_displays";
                return false;
            }

            var display = matches[0];
            if (display.DpiX is not > 0 || display.DpiY is not > 0 ||
                display.PhysicalWidth is not > 0 || display.PhysicalHeight is not > 0 ||
                display.Orientation is null)
            {
                failure = "display_identity_unresolved";
                return false;
            }

            if (display.PhysicalBounds is not { } bounds)
            {
                failure = "display_identity_unresolved";
                return false;
            }
            var orientation = display.Orientation.Value;
            var relative = new AuthorizedPhysicalRectangle(
                checked(selected.X - bounds.X),
                checked(selected.Y - bounds.Y),
                selected.Width,
                selected.Height);
            snapshot = StandingFixedRegionSelectionSnapshot.CreateForTrustedLocalSelectionAdapter(
                intentId,
                currentUserSid,
                sessionBinding,
                DateTimeOffset.UtcNow,
                display.StableDisplayFingerprint!,
                bounds,
                relative,
                display.DpiX.Value,
                display.DpiY.Value,
                display.PhysicalWidth.Value,
                display.PhysicalHeight.Value,
                orientation,
                topologyDigest);
            return true;
        }
        catch (OverflowException)
        {
            failure = "region_selection_invalid";
            return false;
        }
        catch (Phase3DomainException exception)
        {
            failure = exception.ReasonCode;
            return false;
        }
    }

    private static bool IsContained(AuthorizedPhysicalRectangle region, AuthorizedPhysicalRectangle display) =>
        (long)region.X >= display.X &&
        (long)region.Y >= display.Y &&
        (long)region.X + region.Width <= (long)display.X + display.Width &&
        (long)region.Y + region.Height <= (long)display.Y + display.Height;

    private static string ResolveDisplayName(string stableFingerprint)
    {
        try
        {
            return SystemQuery.EnumDisplayTopology()
                .FirstOrDefault(item => string.Equals(item.stable_identity, stableFingerprint, StringComparison.Ordinal))?.name
                ?? "已解析显示器 / Resolved display";
        }
        catch
        {
            return "已解析显示器 / Resolved display";
        }
    }

    private StandingPlanSetupState? ReadState(string intentId)
    {
        var currentUserSid = CurrentUserSid();
        var sessionBinding = CaptureAuthorizationSessionBinding.Current;
        var record = _query.Get(intentId, currentUserSid, sessionBinding);
        return record is null
            ? null
            : ToState(record, _query.GetExecution(intentId, currentUserSid, sessionBinding));
    }

    private StandingPlanSetupState ToState(
        StandingPlanSetupRecord record,
        StandingPlanSetupExecutionRecord? execution)
    {
        var status = ProjectionFor(record.StatusCode);
        if (string.Equals(record.StatusCode, "activated", StringComparison.Ordinal))
            status = ProjectionForExecution(execution);
        var localAction = status is "setup_pending" or "pending_lease_approval";
        var nextAction = status switch
        {
            "setup_pending" => "local_region_selection",
            "pending_lease_approval" => "local_lease_approval",
            "scheduled" or "starting" or "recording" or "finalizing" => "natural_wake",
            _ => null,
        };
        var cursor = StandingPlanStatusVersionCursor.Create(
            record.Version,
            execution?.PlanVersion,
            execution?.OccurrenceVersion,
            execution?.LeaseVersion,
            execution?.UseVersion,
            execution?.RunVersion);
        var statusVersion = StandingPlanStatusVersionCursor.CreateNumeric(
            record.Version,
            execution?.PlanVersion,
            execution?.OccurrenceVersion,
            execution?.LeaseVersion,
            execution?.UseVersion,
            execution?.RunVersion);
        var runId = execution?.RunId;
        var recordingStatusUrl = runId is not null && _recordingExists(runId)
            ? "/api/v1/recordings/" + Uri.EscapeDataString(runId)
            : null;
        return new StandingPlanSetupState(
            record.IntentId,
            status,
            statusVersion,
            record.PlanId,
            record.OccurrenceId,
            record.LeaseId,
            localAction,
            nextAction,
            execution?.RunTerminalReasonCode ??
                execution?.OccurrenceTerminalReasonCode ??
                record.TerminalReasonCode,
            runId,
            recordingStatusUrl,
            execution?.RunCreatedAtUtc,
            execution?.RunStatusCode is "settled" or "failed" or "session_interrupted" or "started_unknown"
                ? execution.RunUpdatedAtUtc
                : null,
            cursor);
    }

    private static string ProjectionForExecution(StandingPlanSetupExecutionRecord? execution)
    {
        if (execution is null)
            return "scheduled";

        return execution.OccurrenceStatusCode switch
        {
            "missed" or "expired" => "missed",
            "blocked" => "blocked",
            "cancelled" => "blocked",
            "completed" => "completed",
            _ => execution.RunStatusCode switch
            {
                "created" or "preparing" or "start_committed" => "starting",
                "recording" => "recording",
                "finalizing" => "finalizing",
                "media_ready" or "settled" => "completed",
                "failed" => "failed",
                "session_interrupted" or "started_unknown" => "session_interrupted",
                _ => "scheduled",
            },
        };
    }

    private static string ProjectionFor(string statusCode) => statusCode switch
    {
        "region_selection_pending" => "setup_pending",
        "lease_approval_pending" => "pending_lease_approval",
        "activated" => "scheduled",
        "rejected" => "rejected",
        "expired" => "expired",
        _ => "blocked",
    };

    private static StandingPlanSetupCreateResult Rejected(string intentId, string reason) =>
        new(StandingPlanSetupCreateStatus.Rejected, intentId, "rejected", 0, null, null, null, reason);

    private static string SelectionReason(string status) => status switch
    {
        "selection_cancelled" => "region_selection_cancelled",
        "selection_timeout" => "region_selection_timeout",
        "display_unavailable" => "display_identity_unresolved",
        _ => "region_selection_error",
    };

    private static string PreparationReason(string reason) =>
        string.IsNullOrWhiteSpace(reason) ? "setup_persistence_failed" : reason;

    private static string ActivationReason(string reason) =>
        string.IsNullOrWhiteSpace(reason) ? "setup_conflict" : reason;

    private static string CurrentUserSid()
    {
        try
        {
            return WindowsIdentity.GetCurrent().User?.Value ?? string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }

    private bool IsDisposed => Volatile.Read(ref _disposed) != 0;

}

internal sealed record StandingPlanSetupSelection(
    string Status,
    int X,
    int Y,
    int Width,
    int Height,
    string DisplayId,
    string CoordinateSpace);

internal interface IStandingPlanSetupUi
{
    bool IsInteractiveDesktopAvailable { get; }
    Task<StandingPlanSetupSelection> SelectFreshRegionAsync(CancellationToken cancellationToken);
    Task<bool> ShowApprovalAsync(StandingLeaseApprovalDetails details, CancellationToken cancellationToken);

    Task ShowFailureAsync(string reasonCode, CancellationToken cancellationToken) =>
        Task.CompletedTask;
}

internal sealed class TrayStandingPlanSetupUi : IStandingPlanSetupUi
{
    private readonly TrayContext _tray;

    internal TrayStandingPlanSetupUi(TrayContext tray)
    {
        _tray = tray ?? throw new ArgumentNullException(nameof(tray));
    }

    public bool IsInteractiveDesktopAvailable => _tray.SupportsRegionSelectionUi;

    public Task<StandingPlanSetupSelection> SelectFreshRegionAsync(CancellationToken cancellationToken)
    {
        var completion = new TaskCompletionSource<StandingPlanSetupSelection>(TaskCreationOptions.RunContinuationsAsynchronously);
        _tray.RequestStandingRegionSelection(
            300,
            (status, x, y, width, height, displayId, coordinateSpace) =>
                completion.TrySetResult(new StandingPlanSetupSelection(status, x, y, width, height, displayId, coordinateSpace)),
            cancellationToken);
        return completion.Task;
    }

    public Task<bool> ShowApprovalAsync(StandingLeaseApprovalDetails details, CancellationToken cancellationToken)
    {
        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                completion.TrySetResult(StandingLeaseApprovalForm.ShowModal(
                    details,
                    textProvider: _tray.CurrentUiTextProvider,
                    cancellationToken: cancellationToken));
            }
            catch
            {
                completion.TrySetResult(false);
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.IsBackground = true;
        thread.Start();
        return completion.Task;
    }

    public Task ShowFailureAsync(string reasonCode, CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
            return Task.CompletedTask;

        var reasonKey = reasonCode switch
        {
            "execution_output_directory_unavailable" => "StandingLeaseSetup_OutputDirectoryUnavailable",
            "execution_output_directory_unwritable" => "StandingLeaseSetup_OutputDirectoryUnwritable",
            "execution_disk_space_unavailable" => "StandingLeaseSetup_OutputDiskSpaceUnavailable",
            "execution_disk_space_insufficient" => "StandingLeaseSetup_OutputDiskSpaceInsufficient",
            "execution_output_file_exists" => "StandingLeaseSetup_OutputFileExists",
            _ => "StandingLeaseSetup_OutputReadinessGeneric",
        };
        var text = _tray.CurrentUiTextProvider.Format(
            "StandingLeaseSetup_OutputReadinessFailure",
            _tray.CurrentUiTextProvider.Get(reasonKey),
            reasonCode);
        _tray.ShowError(text);
        return Task.CompletedTask;
    }
}
