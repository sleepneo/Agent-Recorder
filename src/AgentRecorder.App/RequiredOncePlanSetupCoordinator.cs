using System.Collections.Concurrent;
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

/// <summary>
/// Creates and locally approves required-mode one-time plans. This coordinator
/// deliberately has no natural-wake dispatcher, proof issuer, or recorder.
/// </summary>
internal sealed class RequiredOncePlanSetupCoordinator : IRequiredOncePlanSetupGateway, IDisposable
{
    private readonly SqliteRequiredOncePlanSetupRepository _repository;
    private readonly AuditLogger _audit;
    private readonly IRequiredOncePlanSetupUi _ui;
    private readonly Func<IReadOnlyList<StandingLeaseDisplayMetadata>> _currentDisplays;
    private readonly Func<RequiredOncePrincipal?> _principal;
    private readonly Func<DateTimeOffset> _clock;
    private readonly IStandingLeaseOutputReadinessProvider _outputReadiness;
    private readonly Func<bool> _executionSupported;
    private readonly ConcurrentDictionary<string, Lazy<Task>> _flights = new(StringComparer.Ordinal);
    private readonly CancellationTokenSource _lifetime = new();
    private readonly object _commitGate = new();
    private readonly Func<Task>? _beforeApprovalCommitForTest;
    private readonly Action? _approvalCommitLinearizedForTest;
    private bool _shutdown;
    private int _disposeRequested;

    internal RequiredOncePlanSetupCoordinator(
        SqliteOperationalStore store,
        AuditLogger audit,
        TrayContext tray,
        Func<IReadOnlyList<StandingLeaseDisplayMetadata>>? currentDisplaysForTest = null,
        Func<RequiredOncePrincipal?>? principalForTest = null,
        Func<DateTimeOffset>? clockForTest = null,
        IStandingLeaseOutputReadinessProvider? outputReadinessForTest = null,
        Func<Task>? beforeApprovalCommitForTest = null,
        Action? approvalCommitLinearizedForTest = null,
        Action<string>? failureHookForTest = null,
        Func<bool>? executionSupportedProvider = null)
        : this(store, audit, new TrayRequiredOncePlanSetupUi(tray), currentDisplaysForTest,
            principalForTest, clockForTest, outputReadinessForTest, beforeApprovalCommitForTest,
            approvalCommitLinearizedForTest, failureHookForTest, executionSupportedProvider)
    {
    }

    internal RequiredOncePlanSetupCoordinator(
        SqliteOperationalStore store,
        AuditLogger audit,
        IRequiredOncePlanSetupUi ui,
        Func<IReadOnlyList<StandingLeaseDisplayMetadata>>? currentDisplaysForTest = null,
        Func<RequiredOncePrincipal?>? principalForTest = null,
        Func<DateTimeOffset>? clockForTest = null,
        IStandingLeaseOutputReadinessProvider? outputReadinessForTest = null,
        Func<Task>? beforeApprovalCommitForTest = null,
        Action? approvalCommitLinearizedForTest = null,
        Action<string>? failureHookForTest = null,
        Func<bool>? executionSupportedProvider = null)
    {
        _repository = new SqliteRequiredOncePlanSetupRepository(store, failureHookForTest);
        _audit = audit ?? throw new ArgumentNullException(nameof(audit));
        _ui = ui ?? throw new ArgumentNullException(nameof(ui));
        _currentDisplays = currentDisplaysForTest ?? (() => SystemQueryDisplayTopologyProvider.Instance.GetCurrentExecutionMetadata());
        _principal = principalForTest ?? ReadCurrentPrincipal;
        _clock = clockForTest ?? (() => DateTimeOffset.UtcNow);
        _outputReadiness = outputReadinessForTest ?? SystemQueryStandingLeaseOutputReadinessProvider.Instance;
        _beforeApprovalCommitForTest = beforeApprovalCommitForTest;
        _approvalCommitLinearizedForTest = approvalCommitLinearizedForTest;
        _executionSupported = executionSupportedProvider ?? (() => false);
        RecoverPendingSetups();
    }

    public bool IsInteractiveDesktopAvailable => SafeUiAvailable();

    public bool IsExecutionSupported => ExecutionSupported();

    public RequiredOncePlanSetupCreateResult CreateOrGet(RequiredOncePlanApiRequest request)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposeRequested) != 0, this);
        ArgumentNullException.ThrowIfNull(request);
        var principal = ReadPrincipal();
        if (principal is null || !SafeUiAvailable())
            return Result(RequiredOncePlanSetupWriteStatus.Rejected, null, "interactive_desktop_unavailable");

        var now = TrustedNow();
        if (now is null)
            return Result(RequiredOncePlanSetupWriteStatus.Rejected, null, "setup_clock_unavailable");
        DateTimeOffset expiresAt;
        try
        {
            var defaultExpiry = now.Value.AddMinutes(10);
            expiresAt = request.LatestStartUtc > now.Value && request.LatestStartUtc < defaultExpiry
                ? request.LatestStartUtc
                : defaultExpiry;
        }
        catch (ArgumentOutOfRangeException)
        {
            return Result(RequiredOncePlanSetupWriteStatus.Rejected, null, "setup_request_invalid");
        }

        var snapshot = new RequiredOncePlanSetupRequestSnapshot(
            "required-once-setup-" + Guid.NewGuid().ToString("N"), request.IdempotencyKey,
            principal.Sid, principal.Session, now.Value, expiresAt,
            request.ScheduledStartUtc, request.LatestStartUtc, request.PlannedEndUtc,
            request.Duration, request.OutputDirectory, request.FrozenFileName);

        RequiredOncePlanSetupWriteResult created;
        try
        {
            created = _repository.CreateOrGet(snapshot, now.Value);
        }
        catch (Exception exception)
        {
            Log(null, "blocked", "setup_persistence_failed", exception);
            throw;
        }
        if (created.State is { } state && IsPending(state.StatusCode))
            StartFlight(state.Request.SetupIntentId, principal);
        return Result(created.Status, created.State, created.ReasonCode);
    }

    public StandingPlanSetupState? Get(string setupIntentId)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposeRequested) != 0, this);
        var principal = ReadPrincipal();
        if (principal is null) return null;
        var state = _repository.Get(setupIntentId, principal.Sid, principal.Session);
        if (state is null) return null;
        if (IsPending(state.StatusCode))
        {
            var now = TrustedNow();
            if (now is null)
                return ToApiState(state, "blocked", "setup_clock_unavailable");
            if (now >= state.Request.LatestStartUtc || now >= state.Request.ExpiresAtUtc)
            {
                var reason = now >= state.Request.LatestStartUtc ? "latest_start_window_missed" : "setup_intent_expired";
                state = _repository.SetTerminal(state.Request.SetupIntentId, principal.Sid, principal.Session,
                    "expired", reason, now.Value).State ?? state;
            }
        }
        return ToApiState(state);
    }

    internal async Task WaitForIdleAsync() => await Task.WhenAll(_flights.Values.Select(flight => flight.Value)).ConfigureAwait(false);

    internal int FlightCountForTests => _flights.Count;

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposeRequested, 1) != 0) return;
        lock (_commitGate) _shutdown = true;
        _lifetime.Cancel();
        var flights = _flights.Values.Select(flight =>
        {
            try { return flight.Value; } catch { return Task.CompletedTask; }
        }).Distinct().ToArray();
        try { Task.WaitAll(flights, TimeSpan.FromSeconds(5)); } catch (AggregateException) { }
        if (flights.All(flight => flight.IsCompleted)) _lifetime.Dispose();
    }

    private void RecoverPendingSetups()
    {
        var principal = ReadPrincipal();
        if (principal is null || !SafeUiAvailable()) return;
        try
        {
            foreach (var id in _repository.ListRecoverable(principal.Sid, principal.Session, _clock()))
                StartFlight(id, principal);
        }
        catch (Exception exception)
        {
            Log(null, "blocked", "setup_recovery_scan_failed", exception);
        }
    }

    private void StartFlight(string id, RequiredOncePrincipal principal)
    {
        lock (_commitGate)
        {
            if (_shutdown) return;
            var flight = _flights.GetOrAdd(id, key => new Lazy<Task>(
                () => Task.Run(() => RunFlightAsync(key, principal)), LazyThreadSafetyMode.ExecutionAndPublication));
            _ = flight.Value;
        }
    }

    private async Task RunFlightAsync(string id, RequiredOncePrincipal principal)
    {
        LocalSetupUiSerializationGate.Lease? uiLease = null;
        try
        {
            uiLease = await LocalSetupUiSerializationGate.Instance.WaitAsync(_lifetime.Token).ConfigureAwait(false);
            await RunUnderUiGateAsync(id, principal).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception exception)
        {
            Log(id, "blocked", "setup_conflict", exception);
            Terminal(id, principal, "rejected", "setup_conflict");
        }
        finally
        {
            uiLease?.Dispose();
            _flights.TryRemove(id, out _);
        }
    }

    private async Task RunUnderUiGateAsync(string id, RequiredOncePrincipal principal)
    {
        _lifetime.Token.ThrowIfCancellationRequested();
        if (!SafeUiAvailable() || ReadPrincipal() != principal)
        {
            Terminal(id, principal, "rejected", "interactive_desktop_unavailable");
            return;
        }
        var state = _repository.Get(id, principal.Sid, principal.Session);
        if (state is null || !IsPending(state.StatusCode)) return;
        if (ExpireIfDue(state, principal)) return;
        if (!CheckOutput(state.Request, out var outputReason))
        {
            Terminal(id, principal, "rejected", outputReason);
            return;
        }

        if (state.StatusCode == "region_selection_pending")
        {
            var selectionResult = await SelectWithTimeoutAsync().ConfigureAwait(false);
            if (_lifetime.IsCancellationRequested) return;
            if (selectionResult.Status != "selected")
            {
                Terminal(id, principal,
                    selectionResult.Status is "selection_timeout" ? "expired" : "rejected",
                    SelectionReason(selectionResult.Status));
                return;
            }
            if (ReadPrincipal() != principal || !SafeUiAvailable())
            {
                Terminal(id, principal, "rejected", "setup_identity_or_desktop_changed");
                return;
            }
            if (!TryBuildSelection(selectionResult, out var frozenSelection, out var selectionReason))
            {
                Terminal(id, principal, "rejected", selectionReason!);
                return;
            }
            var selectionNow = TrustedNow();
            if (selectionNow is null)
            {
                Log(id, "blocked", "setup_clock_unavailable");
                return;
            }
            var saved = _repository.SaveSelection(id, principal.Sid, principal.Session, frozenSelection!, selectionNow.Value);
            if (saved.Status is not (RequiredOncePlanSetupWriteStatus.Updated or RequiredOncePlanSetupWriteStatus.Existing) || saved.State is null)
            {
                if (saved.Status == RequiredOncePlanSetupWriteStatus.Expired && saved.State is not null) return;
                Terminal(id, principal, "rejected", saved.ReasonCode ?? "stale_selection_callback");
                return;
            }
            state = saved.State;
        }

        if (state.StatusCode != "creation_approval_pending" || state.Selection is null) return;
        if (!ValidatePrincipalAndDisplay(state.Selection, principal))
        {
            Terminal(id, principal, "rejected", "display_topology_changed");
            return;
        }
        if (!CheckOutput(state.Request, out outputReason))
        {
            Terminal(id, principal, "rejected", outputReason);
            return;
        }
        if (ExpireIfDue(state, principal)) return;
        var details = BuildApprovalDetails(state);
        var approval = await ShowApprovalWithTimeoutAsync(details).ConfigureAwait(false);
        if (_lifetime.IsCancellationRequested) return;
        if (approval != RequiredOnceCreationApprovalResult.Approved)
        {
            var reason = approval switch
            {
                RequiredOnceCreationApprovalResult.Rejected => "plan_creation_rejected_by_user",
                RequiredOnceCreationApprovalResult.TimedOut => "plan_creation_approval_timed_out",
                _ => "interactive_desktop_unavailable",
            };
            Terminal(id, principal, approval == RequiredOnceCreationApprovalResult.TimedOut ? "expired" : "rejected", reason);
            return;
        }

        if (_beforeApprovalCommitForTest is not null)
            await _beforeApprovalCommitForTest().ConfigureAwait(false);
        lock (_commitGate)
        {
            if (_shutdown || _lifetime.IsCancellationRequested) return;
            var approvalNow = TrustedNow();
            if (approvalNow is null)
            {
                Terminal(id, principal, "rejected", "setup_clock_unavailable");
                return;
            }
            var current = _repository.Get(id, principal.Sid, principal.Session);
            if (current is null || current.StatusCode != "creation_approval_pending" ||
                current.Version != state.Version || current.Selection != state.Selection)
            {
                Terminal(id, principal, "rejected", "stale_creation_approval_callback");
                return;
            }
            if (ExpireIfDue(current, principal, approvalNow.Value)) return;
            if (!SafeUiAvailable() || ReadPrincipal() != principal)
            {
                Terminal(id, principal, "rejected", "setup_identity_or_desktop_changed");
                return;
            }
            if (!ValidatePrincipalAndDisplay(current.Selection!, principal))
            {
                Terminal(id, principal, "rejected", "display_topology_changed");
                return;
            }
            if (!CheckOutput(current.Request, out outputReason))
            {
                Terminal(id, principal, "rejected", outputReason);
                return;
            }
            _approvalCommitLinearizedForTest?.Invoke();
            var activated = _repository.Activate(id, principal.Sid, principal.Session, current.Version,
                "required-once-creation-approval-" + Guid.NewGuid().ToString("N"), approvalNow.Value);
            if (activated.Status is RequiredOncePlanSetupWriteStatus.Updated or RequiredOncePlanSetupWriteStatus.Existing)
                Log(id, "scheduled", "scheduled", planId: activated.State?.PlanId, occurrenceId: activated.State?.OccurrenceId);
            else
                Log(id, activated.State?.StatusCode ?? "blocked", activated.ReasonCode ?? "setup_conflict");
        }
    }

    private async Task<StandingPlanSetupSelection> SelectWithTimeoutAsync()
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        timeout.CancelAfter(TimeSpan.FromSeconds(300));
        try
        {
            var result = await _ui.SelectFreshRegionAsync(timeout.Token).ConfigureAwait(false);
            return timeout.IsCancellationRequested
                ? new(_lifetime.IsCancellationRequested ? "host_shutdown" : "selection_timeout", 0, 0, 0, 0, "", "virtual_screen")
                : result;
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested)
        {
            return new(_lifetime.IsCancellationRequested ? "host_shutdown" : "selection_timeout", 0, 0, 0, 0, "", "virtual_screen");
        }
    }

    private async Task<RequiredOnceCreationApprovalResult> ShowApprovalWithTimeoutAsync(RequiredOnceCreationApprovalDetails details)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        timeout.CancelAfter(TimeSpan.FromSeconds(300));
        try
        {
            var result = await _ui.ShowCreationApprovalAsync(details, timeout.Token).ConfigureAwait(false);
            return timeout.IsCancellationRequested
                ? (_lifetime.IsCancellationRequested ? RequiredOnceCreationApprovalResult.HostShutdown : RequiredOnceCreationApprovalResult.TimedOut)
                : result;
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested)
        {
            return _lifetime.IsCancellationRequested ? RequiredOnceCreationApprovalResult.HostShutdown : RequiredOnceCreationApprovalResult.TimedOut;
        }
    }

    private bool TryBuildSelection(StandingPlanSetupSelection selected, out RequiredOncePlanRegionSelection? selection, out string? failure)
    {
        selection = null;
        failure = "region_selection_invalid";
        if (selected.CoordinateSpace != "virtual_screen" || selected.Width <= 0 || selected.Height <= 0) return false;
        try
        {
            var absolute = new AuthorizedPhysicalRectangle(selected.X, selected.Y, selected.Width, selected.Height);
            var displays = _currentDisplays();
            if (!StandingLeaseDisplayTopologyDigest.TryCompute(displays, out var topologyDigest))
            {
                failure = "display_identity_unresolved";
                return false;
            }
            var matching = displays.Where(display => display.IdentityStatus == DisplayIdentityResolutionStatus.Resolved &&
                !string.IsNullOrWhiteSpace(display.StableDisplayFingerprint) && display.PhysicalBounds is { } bounds && IsContained(absolute, bounds)).ToArray();
            if (matching.Length != 1)
            {
                failure = "region_spans_displays";
                return false;
            }
            var display = matching[0];
            if (display.PhysicalBounds is not { } displayBounds || display.DpiX is not > 0 || display.DpiY is not > 0 ||
                display.PhysicalWidth is not > 0 || display.PhysicalHeight is not > 0 || display.Orientation is null)
            {
                failure = "display_identity_unresolved";
                return false;
            }
            selection = new RequiredOncePlanRegionSelection(
                TrustedNow() ?? throw new InvalidOperationException("The trusted UTC clock is unavailable."),
                display.StableDisplayFingerprint!, displayBounds,
                new AuthorizedPhysicalRectangle(checked(absolute.X - displayBounds.X), checked(absolute.Y - displayBounds.Y), absolute.Width, absolute.Height),
                display.DpiX.Value, display.DpiY.Value, display.PhysicalWidth.Value, display.PhysicalHeight.Value,
                display.Orientation.Value, topologyDigest);
            return true;
        }
        catch (OverflowException) { failure = "region_selection_invalid"; return false; }
        catch (Phase3DomainException exception) { failure = exception.ReasonCode; return false; }
        catch { failure = "display_identity_unresolved"; return false; }
    }

    private bool ValidatePrincipalAndDisplay(RequiredOncePlanRegionSelection selection, RequiredOncePrincipal expected)
    {
        if (!SafeUiAvailable() || ReadPrincipal() != expected) return false;
        try
        {
            var displays = _currentDisplays();
            if (!StandingLeaseDisplayTopologyDigest.TryCompute(displays, out var digest) || digest != selection.TopologyDigest) return false;
            var match = displays.Where(display => display.IdentityStatus == DisplayIdentityResolutionStatus.Resolved &&
                string.Equals(display.StableDisplayFingerprint, selection.StableDisplayFingerprint, StringComparison.Ordinal)).ToArray();
            if (match.Length != 1 || match[0].PhysicalBounds != selection.DisplayBounds || match[0].DpiX != selection.DpiX ||
                match[0].DpiY != selection.DpiY || match[0].PhysicalWidth != selection.PhysicalWidth ||
                match[0].PhysicalHeight != selection.PhysicalHeight || match[0].Orientation != selection.Orientation)
                return false;
            var absolute = new AuthorizedPhysicalRectangle(
                checked(selection.DisplayBounds.X + selection.RegionWithinDisplay.X),
                checked(selection.DisplayBounds.Y + selection.RegionWithinDisplay.Y),
                selection.RegionWithinDisplay.Width, selection.RegionWithinDisplay.Height);
            return IsContained(absolute, match[0].PhysicalBounds!.Value);
        }
        catch { return false; }
    }

    private bool CheckOutput(RequiredOncePlanSetupRequestSnapshot request, out string reasonCode)
    {
        reasonCode = "execution_output_environment_unavailable";
        try
        {
            var result = _outputReadiness.Check(request.OutputDirectory, request.FrozenFileName, request.Duration);
            if (!result.IsReady)
            {
                reasonCode = string.IsNullOrWhiteSpace(result.ReasonCode) ? reasonCode : result.ReasonCode;
                return false;
            }
            var expectedDirectory = StandingLeaseOutputPath.NormalizeDirectory(request.OutputDirectory);
            if (!string.Equals(result.Snapshot.NormalizedOutputDirectory, expectedDirectory, StringComparison.OrdinalIgnoreCase))
            {
                reasonCode = "execution_output_directory_changed";
                return false;
            }
            var expectedFile = Path.GetFullPath(Path.Combine(expectedDirectory, request.FrozenFileName));
            if (!string.Equals(result.Snapshot.FrozenOutputFilePath, expectedFile, StringComparison.OrdinalIgnoreCase))
            {
                reasonCode = "execution_output_file_target_changed";
                return false;
            }
            return true;
        }
        catch { return false; }
    }

    private RequiredOnceCreationApprovalDetails BuildApprovalDetails(RequiredOncePlanSetupReadback state)
    {
        var selection = state.Selection!;
        var displayName = "已解析的固定显示器 / Resolved fixed display";
        try
        {
            displayName = _currentDisplays().FirstOrDefault(item => item.StableDisplayFingerprint == selection.StableDisplayFingerprint)?.PublicId ?? displayName;
        }
        catch { }
        return new(displayName, selection.StableDisplayFingerprint, selection.DisplayBounds, selection.RegionWithinDisplay,
            state.Request.ScheduledStartUtc, state.Request.LatestStartUtc, state.Request.PlannedEndUtc,
            state.Request.Duration, state.Request.OutputDirectory, state.Request.FrozenFileName);
    }

    private bool ExpireIfDue(RequiredOncePlanSetupReadback state, RequiredOncePrincipal principal, DateTimeOffset? suppliedNow = null)
    {
        var now = suppliedNow ?? TrustedNow();
        if (now is null) return true;
        if (now < state.Request.LatestStartUtc && now < state.Request.ExpiresAtUtc) return false;
        Terminal(state.Request.SetupIntentId, principal, "expired",
            now >= state.Request.LatestStartUtc ? "latest_start_window_missed" : "setup_intent_expired", now);
        return true;
    }

    private void Terminal(string id, RequiredOncePrincipal principal, string status, string reason, DateTimeOffset? now = null)
    {
        try
        {
            var trustedNow = now ?? TrustedNow();
            if (trustedNow is null) return;
            _repository.SetTerminal(id, principal.Sid, principal.Session, status, reason, trustedNow.Value);
            Log(id, status, reason);
        }
        catch (Exception exception) { Log(id, "blocked", reason, exception); }
    }

    private RequiredOncePrincipal? ReadPrincipal()
    {
        try
        {
            var principal = _principal();
            return principal is not null && Canonical(principal.Sid) && Canonical(principal.Session) ? principal : null;
        }
        catch { return null; }
    }

    private DateTimeOffset? TrustedNow()
    {
        try
        {
            var value = _clock();
            return value.Offset == TimeSpan.Zero ? value : null;
        }
        catch { return null; }
    }

    private bool SafeUiAvailable()
    {
        try { return _ui.IsInteractiveDesktopAvailable; } catch { return false; }
    }

    private static RequiredOncePrincipal? ReadCurrentPrincipal()
    {
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            var sid = identity.User?.Value;
            var session = CaptureAuthorizationSessionBinding.Current;
            return string.IsNullOrWhiteSpace(sid) || string.IsNullOrWhiteSpace(session) ? null : new(sid, session);
        }
        catch { return null; }
    }

    private static bool IsPending(string status) => status is "region_selection_pending" or "creation_approval_pending";

    private static bool Canonical(string? value) => !string.IsNullOrWhiteSpace(value) && value == value.Trim();

    private static bool IsContained(AuthorizedPhysicalRectangle region, AuthorizedPhysicalRectangle display) =>
        (long)region.X >= display.X && (long)region.Y >= display.Y &&
        (long)region.X + region.Width <= (long)display.X + display.Width &&
        (long)region.Y + region.Height <= (long)display.Y + display.Height;

    private static string SelectionReason(string status) => status switch
    {
        "selection_cancelled" => "region_selection_cancelled",
        "selection_timeout" => "region_selection_timed_out",
        "display_unavailable" => "display_identity_unresolved",
        "host_shutdown" => "host_shutdown",
        _ => "region_selection_failed",
    };

    private static RequiredOncePlanSetupCreateResult Result(
        RequiredOncePlanSetupWriteStatus status,
        RequiredOncePlanSetupReadback? state,
        string? reason) => new(
            status switch
            {
                RequiredOncePlanSetupWriteStatus.Created or RequiredOncePlanSetupWriteStatus.Updated => StandingPlanSetupCreateStatus.Created,
                RequiredOncePlanSetupWriteStatus.Existing => StandingPlanSetupCreateStatus.Existing,
                RequiredOncePlanSetupWriteStatus.Conflict => StandingPlanSetupCreateStatus.Conflict,
                RequiredOncePlanSetupWriteStatus.Expired => StandingPlanSetupCreateStatus.Expired,
                _ => StandingPlanSetupCreateStatus.Rejected,
            },
            state?.Request.SetupIntentId,
            state?.StatusCode ?? "rejected",
            state?.Version ?? 0,
            state?.PlanId,
            state?.OccurrenceId,
            reason,
            state is null ? null : "required-once-setup/v1:" + state.Version.ToString(CultureInfo.InvariantCulture));

    private StandingPlanSetupState ToApiState(
        RequiredOncePlanSetupReadback state,
        string? projectedStatus = null,
        string? reasonOverride = null)
    {
        var executionSupported = ExecutionSupported();
        var status = projectedStatus ?? state.StatusCode switch
        {
            "region_selection_pending" => "pending_selection",
            "creation_approval_pending" => "pending_creation_approval",
            "scheduled" => "scheduled",
            "rejected" => "rejected",
            "expired" => "expired",
            _ => "blocked",
        };
        return new StandingPlanSetupState(
            state.Request.SetupIntentId, status, state.Version, state.PlanId, state.OccurrenceId,
            null, status is "pending_selection" or "pending_creation_approval",
            status switch
            {
                "pending_selection" => "local_region_selection",
                "pending_creation_approval" => "local_plan_creation_approval",
                "scheduled" => executionSupported ? "required_once_natural_wake_execution" : "execution_runtime_unavailable",
                _ => null,
            },
            reasonOverride ?? state.ReasonCode,
            StatusVersionCursor: "required-once-setup/v1:" + state.Version.ToString(CultureInfo.InvariantCulture),
            AuthorizationMode: "required",
            RequiresExecutionConfirmation: true,
            ExecutionSupported: executionSupported);
    }

    private bool ExecutionSupported()
    {
        try { return _executionSupported(); } catch { return false; }
    }

    private void Log(string? id, string status, string reason, Exception? exception = null, string? planId = null, string? occurrenceId = null)
    {
        try { _audit.Log("required_once_setup.state", new { setup_intent_id = id, plan_id = planId,
            occurrence_id = occurrenceId, status, reason_code = reason, exception_type = exception?.GetType().Name }); }
        catch { }
    }
}

internal sealed record RequiredOncePrincipal(string Sid, string Session);

internal sealed record RequiredOnceCreationApprovalDetails(
    string DisplayName,
    string StableDisplayFingerprint,
    AuthorizedPhysicalRectangle DisplayBounds,
    AuthorizedPhysicalRectangle RegionWithinDisplay,
    DateTimeOffset ScheduledStartUtc,
    DateTimeOffset LatestStartUtc,
    DateTimeOffset PlannedEndUtc,
    TimeSpan Duration,
    string OutputDirectory,
    string FrozenFileName);

internal enum RequiredOnceCreationApprovalResult
{
    Approved,
    Rejected,
    TimedOut,
    HostShutdown,
    Unavailable,
}

internal interface IRequiredOncePlanSetupUi
{
    bool IsInteractiveDesktopAvailable { get; }
    Task<StandingPlanSetupSelection> SelectFreshRegionAsync(CancellationToken cancellationToken);
    Task<RequiredOnceCreationApprovalResult> ShowCreationApprovalAsync(RequiredOnceCreationApprovalDetails details, CancellationToken cancellationToken);
}

internal sealed class TrayRequiredOncePlanSetupUi : IRequiredOncePlanSetupUi
{
    private readonly TrayContext _tray;

    internal TrayRequiredOncePlanSetupUi(TrayContext tray) => _tray = tray ?? throw new ArgumentNullException(nameof(tray));

    public bool IsInteractiveDesktopAvailable => _tray.SupportsRegionSelectionUi;

    public Task<StandingPlanSetupSelection> SelectFreshRegionAsync(CancellationToken cancellationToken)
    {
        var completion = new TaskCompletionSource<StandingPlanSetupSelection>(TaskCreationOptions.RunContinuationsAsynchronously);
        _tray.RequestStandingRegionSelection(300,
            (status, x, y, width, height, displayId, coordinateSpace) =>
                completion.TrySetResult(new(status, x, y, width, height, displayId, coordinateSpace)), cancellationToken);
        return completion.Task;
    }

    public Task<RequiredOnceCreationApprovalResult> ShowCreationApprovalAsync(RequiredOnceCreationApprovalDetails details, CancellationToken cancellationToken)
    {
        var completion = new TaskCompletionSource<RequiredOnceCreationApprovalResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try { completion.TrySetResult(RequiredPlanCreationApprovalForm.ShowModal(details, cancellationToken)); }
            catch { completion.TrySetResult(RequiredOnceCreationApprovalResult.Unavailable); }
        }) { IsBackground = true, Name = "Required plan creation approval" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return completion.Task;
    }
}
