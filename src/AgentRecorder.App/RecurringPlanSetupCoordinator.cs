using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security.Principal;
using System.Threading;
using System.Threading.Tasks;
using AgentRecorder.Api;
using AgentRecorder.Capture;
using AgentRecorder.Core;
using AgentRecorder.Core.Automation;
using AgentRecorder.Infrastructure;
using AgentRecorder.Logging;
using AgentRecorder.Persistence;
using AgentRecorder.Windows;

namespace AgentRecorder.App;

internal sealed record RecurringSetupPrincipal(string Sid, string Session);
internal enum RecurringRegionSelectionStatus { Selected, Cancelled, TimedOut, Invalid, Unavailable, Conflict, HostShutdown }
internal sealed record RecurringPlanSetupSelection(
    RecurringRegionSelectionStatus Status, int X = 0, int Y = 0, int Width = 0, int Height = 0,
    string CoordinateSpace = "virtual_screen");
internal enum RecurringLeaseApprovalResult { Approved, Rejected, TimedOut, HostShutdown, Unavailable }

internal interface IRecurringPlanSetupUi
{
    bool IsInteractiveDesktopAvailable { get; }
    // Completion means the UI is closed. Implementations must honor cancellation.
    Task<RecurringPlanSetupSelection> SelectFreshRegionAsync(CancellationToken cancellationToken);
    Task<RecurringLeaseApprovalResult> ShowApprovalAsync(RecurringLeaseApprovalDetails details, CancellationToken cancellationToken);
}

internal sealed class RecurringPlanSetupCoordinator : IRecurringPlanSetupGateway, IDisposable
{
    private readonly ConcurrentDictionary<string, Lazy<Task>> _flights = new(StringComparer.Ordinal);
    private readonly object _commitGate = new();
    private readonly object _clockGate = new();
    private readonly CancellationTokenSource _lifetime = new();
    private readonly CancellationToken _token;
    private readonly IRecurringPlanSetupUi _ui;
    private readonly AuditLogger _audit;
    private readonly Func<RecurringSetupPrincipal?> _principal;
    private readonly Func<DateTimeOffset?> _clock;
    private readonly Func<string, bool> _unattended;
    private readonly Func<bool> _executionSupported;
    private readonly Func<IReadOnlyList<StandingLeaseDisplayMetadata>> _displays;
    private readonly IRecurringSetupOutputReadinessProvider _output;
    private readonly RecurringSetupIntentService _intents;
    private readonly RecurringSetupPreparationService _preparation;
    private readonly RecurringLeasePreparedIntentApprovalActivationService _activation;
    private readonly RecurringPlanSetupTerminalService _terminal;
    private readonly RecurringPlanSetupQueryService _query;
    private readonly Func<Task>? _beforeCommit;
    private readonly Action? _commitLinearized;
    private readonly Action? _shutdownLinearized;
    private DateTimeOffset? _lastNow;
    private bool _shutdown;
    private int _disposeRequested;

    internal RecurringPlanSetupCoordinator(SqliteOperationalStore store, AuditLogger audit, IRecurringPlanSetupUi ui,
        Func<RecurringSetupPrincipal?>? principalForTest = null, Func<DateTimeOffset?>? utcNowForTest = null,
        Func<IReadOnlyList<StandingLeaseDisplayMetadata>>? displaysForTest = null,
        IRecurringSetupOutputReadinessProvider? outputForTest = null,
        Func<Task>? beforeApprovalCommitForTest = null, Action? approvalCommitLinearizedForTest = null,
        Action? shutdownLinearizedForTest = null,
        Func<bool>? executionSupportedProvider = null)
    {
        _audit = audit; _ui = ui;
        _principal = principalForTest ?? CurrentPrincipal;
        _clock = utcNowForTest ?? (() => DateTimeOffset.UtcNow);
        _token = _lifetime.Token;
        _displays = displaysForTest ?? (() => SystemQueryDisplayTopologyProvider.Instance.GetCurrentExecutionMetadata());
        _output = outputForTest ?? new RecurringSetupOutputReadinessProvider();
        _unattended = new StandingLeaseSafetyControlService(store).IsUnattendedEnabled;
        _executionSupported = executionSupportedProvider ?? (() => false);
        _intents = new(store, TrustedNow);
        _preparation = new(store, TrustedNow);
        _activation = new(store, TrustedNow);
        _terminal = new(store, TrustedNow);
        _query = new(store, (id, reason) => Log(id, "blocked", reason));
        _beforeCommit = beforeApprovalCommitForTest;
        _commitLinearized = approvalCommitLinearizedForTest;
        _shutdownLinearized = shutdownLinearizedForTest;
        var principal = ReadPrincipal();
        if (principal is not null)
            foreach (var record in _query.ListRecoverable(principal.Sid, principal.Session))
            {
                if (IsExecutionSupported)
                    StartFlight(record.IntentId, principal);
                else
                {
                    try
                    {
                        _terminal.Settle(record.IntentId, principal.Sid, principal.Session,
                            RecurringSetupIntentStatus.Rejected, "recurring_execution_unavailable");
                    }
                    catch (Exception exception)
                    {
                        Log(record.IntentId, "blocked", "recurring_execution_unavailable", exception);
                    }
                }
            }
    }

    public bool IsInteractiveDesktopAvailable
    {
        get { try { return _ui.IsInteractiveDesktopAvailable; } catch { return false; } }
    }

    public bool IsUnattendedEnabled
    {
        get
        {
            var principal = ReadPrincipal();
            return principal is not null && IsUnattendedEnabledFor(principal.Sid);
        }
    }

    public bool IsExecutionSupported
    {
        get { try { return _executionSupported(); } catch { return false; } }
    }

    public RecurringPlanSetupCreateResult CreateOrGet(RecurringPlanApiRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        // Also fences flight registration against Dispose's task snapshot.
        lock (_commitGate)
        {
            if (_shutdown) return new(RecurringPlanSetupCreateStatus.Rejected, null, "host_shutdown");
            if (!IsExecutionSupported)
                return new(RecurringPlanSetupCreateStatus.Rejected, null, "recurring_execution_unavailable");
            var principal = ReadPrincipal();
            var now = TrustedNow();
            if (principal is null || now is null)
                return new(RecurringPlanSetupCreateStatus.Rejected, null, "recurring_setup_environment_unavailable");
            try
            {
                var snapshot = RecurringSetupIntentSnapshot.CreateForTrustedSetupAdapter(
                    "recurring-setup-" + Guid.NewGuid().ToString("N"), request.IdempotencyKey, principal.Sid, principal.Session,
                    now.Value, request.LeaseValidUntilUtc, request.Schedule, request.MaxRuns, request.MaxTotalDuration,
                    request.LeaseValidUntilUtc, request.OutputDirectory, request.FilenamePrefix);
                var result = _intents.CreateOrGet(snapshot);
                if (result.Result is not (RecurringSetupIntentResultStatus.Created or RecurringSetupIntentResultStatus.Existing or RecurringSetupIntentResultStatus.Expired))
                    return new(ToApiCreateStatus(result.Result), null, result.Reason);
                var record = _query.Get(result.IntentId, principal.Sid, principal.Session);
                if (record is null) return new(RecurringPlanSetupCreateStatus.Rejected, null, "recurring_setup_query_unavailable");
                if (IsPending(record)) StartFlight(record.IntentId, principal);
                return new(ToApiCreateStatus(result.Result), ToState(record), result.Reason);
            }
            catch (Exception exception)
            {
                Log(null, "blocked", "setup_conflict", exception);
                return new(RecurringPlanSetupCreateStatus.Rejected, null, "setup_conflict");
            }
        }
    }

    public RecurringPlanSetupState? Get(string intentId)
    {
        var principal = ReadPrincipal();
        var record = principal is null ? null : _query.Get(intentId, principal.Sid, principal.Session);
        return record is null ? null : ToState(record);
    }

    private static RecurringPlanSetupCreateStatus ToApiCreateStatus(RecurringSetupIntentResultStatus status) => status switch
    {
        RecurringSetupIntentResultStatus.Created => RecurringPlanSetupCreateStatus.Created,
        RecurringSetupIntentResultStatus.Existing => RecurringPlanSetupCreateStatus.Existing,
        RecurringSetupIntentResultStatus.Conflict => RecurringPlanSetupCreateStatus.Conflict,
        RecurringSetupIntentResultStatus.Expired => RecurringPlanSetupCreateStatus.Expired,
        _ => RecurringPlanSetupCreateStatus.Rejected,
    };

    private void StartFlight(string id, RecurringSetupPrincipal principal)
    {
        lock (_commitGate)
        {
            if (_shutdown) return;
            // Task.Run prevents synchronous completion/removal before Lazy has
            // been installed and evaluated; late duplicate callers only reread.
            var flight = _flights.GetOrAdd(id, key => new Lazy<Task>(
                () => Task.Run(() => RunFlightAsync(key, principal)), LazyThreadSafetyMode.ExecutionAndPublication));
            _ = flight.Value;
        }
    }

    internal Task WaitForIdleAsync() => Task.WhenAll(_flights.Values.Select(value => value.Value));
    internal int FlightCountForTests => _flights.Count;

    private async Task RunFlightAsync(string id, RecurringSetupPrincipal principal)
    {
        LocalSetupUiSerializationGate.Lease? uiLease = null;
        try
        {
            uiLease = await LocalSetupUiSerializationGate.Instance
                .WaitAsync(_token).ConfigureAwait(false);
            await RunUnderUiGateAsync(id, principal).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (_token.IsCancellationRequested) { }
        catch (Exception exception)
        {
            Log(id, "blocked", "setup_conflict", exception);
            Reject(id, principal, "setup_conflict");
        }
        finally
        {
            uiLease?.Dispose();
            lock (_commitGate) _flights.TryRemove(id, out _);
        }
    }

    private async Task RunUnderUiGateAsync(string id, RecurringSetupPrincipal principal)
    {
        _token.ThrowIfCancellationRequested();
        var record = _query.Get(id, principal.Sid, principal.Session);
        if (record is null || !IsPending(record)) return;
        if (!CheckEnvironment(id, principal) || ExpireIfDue(record, principal)) return;
        if (!CheckOutput(record.Request, id)) { Reject(id, principal, "setup_conflict"); return; }

        if (record.Status == RecurringSetupIntentStatus.RegionSelectionPending)
        {
            var selection = await SelectAsync().ConfigureAwait(false);
            if (_token.IsCancellationRequested || selection.Status == RecurringRegionSelectionStatus.HostShutdown) return;
            if (selection.Status != RecurringRegionSelectionStatus.Selected)
            {
                Reject(id, principal, SelectionReason(selection.Status)); return;
            }
            if (!CheckEnvironment(id, principal)) return;
            var snapshot = BuildSelection(id, principal, selection);
            if (snapshot is null) { Reject(id, principal, "region_selection_invalid"); return; }
            // Shutdown preserves pending, including when it races selection.
            lock (_commitGate)
            {
                if (_shutdown) return;
                var prepared = _preparation.Prepare(snapshot);
                if (prepared.Status is not (RecurringSetupPreparationResultStatus.Prepared or RecurringSetupPreparationResultStatus.Existing))
                {
                    Reject(id, principal, "setup_conflict"); return;
                }
            }
        }

        record = _query.Get(id, principal.Sid, principal.Session);
        var shown = record?.Prepared;
        if (shown is null || record is null || ExpireIfDue(record, principal)) return;
        var approval = await ApproveAsync(RecurringLeaseApprovalDetails.FromPrepared(shown)).ConfigureAwait(false);
        if (_token.IsCancellationRequested || approval == RecurringLeaseApprovalResult.HostShutdown) return;
        if (approval != RecurringLeaseApprovalResult.Approved)
        {
            Reject(id, principal, approval switch
            {
                RecurringLeaseApprovalResult.Rejected => "lease_rejected_by_user",
                RecurringLeaseApprovalResult.TimedOut => "lease_approval_timed_out",
                _ => "interactive_desktop_unavailable",
            });
            return;
        }
        if (_beforeCommit is not null) await _beforeCommit().ConfigureAwait(false);

        lock (_commitGate)
        {
            if (_shutdown) return;
            // Every safety check is after the UI callback and within the same
            // local shutdown/approval fence as receipt creation and activation.
            if (!CheckEnvironment(id, principal)) return;
            var current = _query.Get(id, principal.Sid, principal.Session);
            var prepared = current?.Prepared;
            if (current is null || prepared is null) { Reject(id, principal, "setup_conflict"); return; }
            if (ExpireIfDue(current, principal)) return;
            if (!SameApprovalBinding(prepared, shown) || !CheckOutput(current.Request, id))
            { Reject(id, principal, "setup_conflict"); return; }
            var now = TrustedNow();
            if (now is null) { Reject(id, principal, "setup_conflict"); return; }
            _commitLinearized?.Invoke();
            var receipt = RecurringLeaseLocalApprovalReceipt.CreateForTrustedLocalApprovalAdapter(
                "recurring-approval-" + Guid.NewGuid().ToString("N"), prepared.LeaseId, prepared.PlanId,
                prepared.ConfigurationDigest, prepared.AuthorizationDigest, principal.Sid, principal.Session, now.Value);
            var activated = _activation.Activate(id, receipt);
            if (activated.Status is RecurringLeaseLocalApprovalActivationStatus.Activated or RecurringLeaseLocalApprovalActivationStatus.AlreadyActive)
                Log(id, "scheduled", "scheduled", planId: prepared.PlanId, leaseId: prepared.LeaseId);
            else
                Reject(id, principal, activated.Reason == "unattended_disabled" ? "unattended_disabled" : "setup_conflict");
        }
    }

    private static bool SameApprovalBinding(RecurringPlanPreparedRecord left, RecurringPlanPreparedRecord right) =>
        left.IntentId == right.IntentId && left.PlanId == right.PlanId && left.LeaseId == right.LeaseId &&
        left.CurrentUserSid == right.CurrentUserSid && left.SessionBinding == right.SessionBinding &&
        left.ConfigurationDigest == right.ConfigurationDigest && left.AuthorizationDigest == right.AuthorizationDigest &&
        left.Request.RequestDigest == right.Request.RequestDigest;

    private async Task<RecurringPlanSetupSelection> SelectAsync()
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_token);
        timeout.CancelAfter(TimeSpan.FromSeconds(300));
        try
        {
            var result = await _ui.SelectFreshRegionAsync(timeout.Token).ConfigureAwait(false);
            return timeout.IsCancellationRequested ? new(_token.IsCancellationRequested
                ? RecurringRegionSelectionStatus.HostShutdown : RecurringRegionSelectionStatus.TimedOut) : result;
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested)
        { return new(_token.IsCancellationRequested ? RecurringRegionSelectionStatus.HostShutdown : RecurringRegionSelectionStatus.TimedOut); }
    }

    private async Task<RecurringLeaseApprovalResult> ApproveAsync(RecurringLeaseApprovalDetails details)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_token);
        timeout.CancelAfter(TimeSpan.FromSeconds(300));
        try
        {
            var result = await _ui.ShowApprovalAsync(details, timeout.Token).ConfigureAwait(false);
            return timeout.IsCancellationRequested ? (_token.IsCancellationRequested
                ? RecurringLeaseApprovalResult.HostShutdown : RecurringLeaseApprovalResult.TimedOut) : result;
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested)
        { return _token.IsCancellationRequested ? RecurringLeaseApprovalResult.HostShutdown : RecurringLeaseApprovalResult.TimedOut; }
    }

    private bool CheckEnvironment(string id, RecurringSetupPrincipal expected)
    {
        if (ReadPrincipal() != expected) { Reject(id, expected, "setup_conflict"); return false; }
        if (!IsExecutionSupported) { Reject(id, expected, "recurring_execution_unavailable"); return false; }
        if (!_unattended(expected.Sid)) { Reject(id, expected, "unattended_disabled"); return false; }
        if (!_ui.IsInteractiveDesktopAvailable) { Reject(id, expected, "interactive_desktop_unavailable"); return false; }
        return !_token.IsCancellationRequested;
    }

    private bool ExpireIfDue(RecurringPlanSetupRecord record, RecurringSetupPrincipal principal)
    {
        var now = TrustedNow();
        if (now is null) { Log(record.IntentId, "blocked", "recurring_setup_clock_unavailable"); return true; }
        if (now < record.Request.ExpiresAtUtc) return false;
        lock (_commitGate)
        {
            if (!_shutdown) _terminal.Settle(record.IntentId, principal.Sid, principal.Session,
                RecurringSetupIntentStatus.Expired, RecurringSetupTerminalReasonPolicy.IntentExpired);
        }
        return true;
    }

    private void Reject(string id, RecurringSetupPrincipal principal, string reason)
    {
        lock (_commitGate)
        {
            if (_shutdown) return;
            var record = _query.Get(id, principal.Sid, principal.Session);
            if (record is null || !IsPending(record)) return;
            if (ExpireIfDue(record, principal)) return;
            var result = _terminal.Settle(id, principal.Sid, principal.Session, RecurringSetupIntentStatus.Rejected, reason);
            Log(id, result.Status.ToString(), reason, planId: record.PlanId, leaseId: record.LeaseId);
        }
    }

    private bool CheckOutput(RecurringSetupIntentSnapshot request, string id)
    {
        RecurringSetupOutputReadiness result;
        try { result = _output.Check(request.OutputDirectory, request.Schedule.RecordingDuration); }
        catch (Exception exception) { Log(id, "blocked", "setup_output_unavailable", exception); return false; }
        if (result == RecurringSetupOutputReadiness.Ready) return true;
        Log(id, "blocked", result switch
        {
            RecurringSetupOutputReadiness.DirectoryUnavailable => "setup_output_directory_unavailable",
            RecurringSetupOutputReadiness.DirectoryUnwritable => "setup_output_directory_unwritable",
            RecurringSetupOutputReadiness.InsufficientSpace => "setup_output_space_insufficient",
            _ => "setup_output_space_unavailable",
        });
        return false;
    }

    private RecurringFixedRegionSelectionSnapshot? BuildSelection(string id, RecurringSetupPrincipal principal, RecurringPlanSetupSelection selection)
    {
        if (selection.CoordinateSpace != "virtual_screen" || selection.Width <= 0 || selection.Height <= 0) return null;
        try
        {
            var region = new AuthorizedPhysicalRectangle(selection.X, selection.Y, selection.Width, selection.Height);
            var displays = _displays();
            if (!StandingLeaseDisplayTopologyDigest.TryCompute(displays, out var digest) ||
                displays.Select(d => d.StableDisplayFingerprint).Distinct(StringComparer.Ordinal).Count() != displays.Count) return null;
            var matches = displays.Where(d => d.PhysicalBounds is { } b &&
                (long)region.X >= b.X && (long)region.Y >= b.Y &&
                (long)region.X + region.Width <= (long)b.X + b.Width &&
                (long)region.Y + region.Height <= (long)b.Y + b.Height).ToArray();
            if (matches.Length != 1) return null;
            var display = matches[0];
            var bounds = display.PhysicalBounds!.Value;
            var now = TrustedNow();
            if (now is null) return null;
            return RecurringFixedRegionSelectionSnapshot.CreateForTrustedLocalSelectionAdapter(id, principal.Sid, principal.Session,
                now.Value, display.StableDisplayFingerprint!, bounds,
                new(checked(region.X - bounds.X), checked(region.Y - bounds.Y), region.Width, region.Height),
                display.DpiX!.Value, display.DpiY!.Value, display.PhysicalWidth!.Value, display.PhysicalHeight!.Value,
                display.Orientation!.Value, digest);
        }
        catch { return null; }
    }

    private DateTimeOffset? TrustedNow()
    {
        lock (_clockGate)
        {
            DateTimeOffset? now;
            try { now = _clock(); } catch { return null; }
            if (now is null || now.Value.Offset != TimeSpan.Zero || now < _lastNow) return null;
            return _lastNow = now;
        }
    }

    private RecurringSetupPrincipal? ReadPrincipal()
    {
        try
        {
            var value = _principal();
            return value is not null && !string.IsNullOrWhiteSpace(value.Sid) && !string.IsNullOrWhiteSpace(value.Session) ? value : null;
        }
        catch { return null; }
    }

    private bool IsUnattendedEnabledFor(string sid)
    {
        if (string.IsNullOrWhiteSpace(sid)) return false;
        try { return _unattended(sid); }
        catch { return false; }
    }

    private static RecurringSetupPrincipal? CurrentPrincipal()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return identity.User?.Value is { } sid ? new(sid, CaptureAuthorizationSessionBinding.Current) : null;
    }

    private static bool IsPending(RecurringPlanSetupRecord record) =>
        record.Status is RecurringSetupIntentStatus.RegionSelectionPending or RecurringSetupIntentStatus.LeaseApprovalPending;

    private static RecurringPlanSetupState ToState(RecurringPlanSetupRecord record)
    {
        var status = record.Status switch
        {
            RecurringSetupIntentStatus.RegionSelectionPending => "setup_pending",
            RecurringSetupIntentStatus.LeaseApprovalPending => "pending_lease_approval",
            RecurringSetupIntentStatus.Activated => "scheduled",
            RecurringSetupIntentStatus.Rejected => "rejected",
            RecurringSetupIntentStatus.Expired => "expired",
            _ => "blocked",
        };
        return new(record.IntentId, status, record.Version, "recurring-setup/v1:" + record.Version.ToString(CultureInfo.InvariantCulture),
            record.PlanId, record.LeaseId, IsPending(record), status switch
            { "setup_pending" => "local_region_selection", "pending_lease_approval" => "local_lease_approval", "scheduled" => "natural_wake", _ => null },
            record.TerminalReasonCode);
    }

    private static string SelectionReason(RecurringRegionSelectionStatus status) => status switch
    {
        RecurringRegionSelectionStatus.Cancelled => "region_selection_cancelled",
        RecurringRegionSelectionStatus.TimedOut => "region_selection_timed_out",
        RecurringRegionSelectionStatus.Invalid => "region_selection_invalid",
        RecurringRegionSelectionStatus.Unavailable => "interactive_desktop_unavailable",
        _ => "setup_conflict",
    };

    private void Log(string? id, string status, string reason, Exception? exception = null, string? planId = null, string? leaseId = null)
    {
        try { _audit.Log("recurring_setup.state", new { intent_id = id, plan_id = planId, lease_id = leaseId,
            status, reason_code = reason, exception_type = exception?.GetType().Name }); } catch { }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposeRequested, 1) != 0) return;
        Task[] flights;
        lock (_commitGate)
        {
            _shutdown = true;
            _shutdownLinearized?.Invoke();
            flights = _flights.Values.Select(f => f.Value).ToArray();
        }
        try { _lifetime.Cancel(); } catch (AggregateException exception) { Log(null, "blocked", "setup_shutdown_incomplete", exception); }
        var all = Task.WhenAll(flights);
        try { all.Wait(TimeSpan.FromSeconds(5)); } catch (AggregateException) { }
        if (all.IsCompleted) _lifetime.Dispose();
        else
        {
            Log(null, "blocked", "setup_shutdown_incomplete");
            // The UI gate is static; lifetime resources remain alive until the
            // last flight really exits, even if an adapter ignores cancellation.
            _ = all.ContinueWith(_ => _lifetime.Dispose(), CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        }
    }
}
