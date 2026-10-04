using System.Collections.Concurrent;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AgentRecorder.Api;
using AgentRecorder.Capture;
using AgentRecorder.Core;
using AgentRecorder.Infrastructure;
using AgentRecorder.Logging;
using AgentRecorder.Persistence;
using AgentRecorder.Windows;

namespace AgentRecorder.App;

/// <summary>
/// Tray-owned future-window authorization coordinator. HTTP can only enqueue
/// pending setup; only the local approval dialog can activate it.
/// </summary>
internal sealed class FutureWindowOneShotAuthorizationCoordinator :
    IFutureWindowOneShotGateway,
    IFutureWindowGlobalSafetyRevocationObserver,
    IDisposable
{
    internal static readonly TimeSpan ApprovalUiWaitLimit = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan ApprovalUiCloseGrace = TimeSpan.FromSeconds(5);
    private readonly SqliteFutureWindowAuthorizationRepository _repository;
    private readonly RecordingEngine _engine;
    private readonly ITrayContext _tray;
    private readonly Func<IUiTextProvider> _approvalTextProvider;
    private readonly IFutureWindowAuthorizationApprovalUi _approvalUi;
    private readonly Func<bool> _approvalDesktopAvailable;
    private readonly TimeSpan _approvalUiWaitLimit;
    private readonly AuditLogger _audit;
    private readonly StandingLeaseStartSafetyInterlock _startInterlock;
    private readonly object _approvalFlightSync = new();
    private readonly Dictionary<string, ApprovalFlight> _approvalFlights = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, FutureWindowExecutablePin> _pins = new(StringComparer.Ordinal);
    private readonly CancellationTokenSource _lifetime = new();
    private readonly SemaphoreSlim _startGate = new(1, 1);
    private int _disposed;

    internal FutureWindowOneShotAuthorizationCoordinator(
        SqliteOperationalStore store,
        RecordingEngine engine,
        ITrayContext tray,
        AuditLogger audit,
        StandingLeaseStartSafetyInterlock startInterlock,
        Func<IUiTextProvider>? approvalTextProvider = null,
        IFutureWindowAuthorizationApprovalUi? approvalUi = null,
        TimeSpan? approvalUiWaitLimit = null,
        bool recoverPending = true,
        Func<bool>? approvalDesktopAvailableForTests = null)
    {
        _repository = new SqliteFutureWindowAuthorizationRepository(store);
        _engine = engine ?? throw new ArgumentNullException(nameof(engine));
        _tray = tray ?? throw new ArgumentNullException(nameof(tray));
        _audit = audit ?? throw new ArgumentNullException(nameof(audit));
        _startInterlock = startInterlock ?? throw new ArgumentNullException(nameof(startInterlock));
        _approvalTextProvider = approvalTextProvider ?? (() => new UiTextProvider(UiLanguageStore.LoadOrDefault()));
        _approvalUi = approvalUi ?? new FutureWindowAuthorizationApprovalUi();
        _approvalDesktopAvailable = approvalDesktopAvailableForTests ?? FutureWindowProcessIdentity.IsInteractiveDesktopAvailable;
        _approvalUiWaitLimit = approvalUiWaitLimit ?? ApprovalUiWaitLimit;
        if (_approvalUiWaitLimit <= TimeSpan.Zero || _approvalUiWaitLimit > TimeSpan.FromMinutes(10))
            throw new ArgumentOutOfRangeException(nameof(approvalUiWaitLimit));
        if (recoverPending)
            Recover();
    }

    public bool IsInteractiveDesktopAvailable =>
        Volatile.Read(ref _disposed) == 0 && _tray.SupportsRegionSelectionUi &&
        FutureWindowProcessIdentity.IsInteractiveDesktopAvailable() &&
        !string.IsNullOrWhiteSpace(CurrentUserSid()) && !string.IsNullOrWhiteSpace(CaptureAuthorizationSessionBinding.Current);

    public bool IsSetupSupported => OperatingSystem.IsWindows() && IsInteractiveDesktopAvailable;

    public bool IsExecutionSupported
    {
        get
        {
            if (!IsSetupSupported || !FfmpegRuntimeAvailable()) return false;
            try { return CaptureBackendSelector.ProbeWindowSurfaceAvailability().Available; }
            catch { return false; }
        }
    }

    public FutureWindowAuthorizationCreateResponse CreateOrGet(FutureWindowOneShotCreateRequest request)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        ArgumentNullException.ThrowIfNull(request);
        ValidateRequest(request);
        if (!IsInteractiveDesktopAvailable)
            throw new ApiException(409, "INTERACTIVE_DESKTOP_REQUIRED", "A local interactive desktop is required for approval.",
                new { reason_code = "interactive_desktop_required" });

        var sid = CurrentUserSid();
        var session = CaptureAuthorizationSessionBinding.Current;
        if (string.IsNullOrWhiteSpace(sid) || string.IsNullOrWhiteSpace(session))
            throw new ApiException(409, "INTERACTIVE_DESKTOP_REQUIRED", "The current Windows user/session could not be bound.",
                new { reason_code = "future_window_user_session_unavailable" });

        if (!FutureWindowProcessIdentity.TryResolveExecutable(request.ExecutablePath, out var executable, out var executableFailure) || executable is null)
            throw new ApiException(409, "FUTURE_WINDOW_EXECUTABLE_UNAVAILABLE",
                "The requested executable identity could not be verified locally.", new { reason_code = executableFailure });
        if (!TryCanonicalOutputDirectory(request.OutputDirectory, out var outputDirectory, out var outputFailure))
            throw new ApiException(409, "FUTURE_WINDOW_OUTPUT_UNAVAILABLE",
                "The output directory is not an available local fixed target.", new { reason_code = outputFailure });

        string? endpointId = null;
        string? endpointName = null;
        if (request.SystemAudioEndpointId is { } requestedEndpoint)
        {
            SystemAudioEndpointInfo? endpoint;
            try
            {
                endpoint = _engine.SystemAudioEndpointProvider.GetEndpointAsync(requestedEndpoint)
                    .WaitAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
            }
            catch
            {
                throw new ApiException(409, "FUTURE_WINDOW_AUDIO_ENDPOINT_UNAVAILABLE",
                    "The exact requested render endpoint could not be checked.",
                    new { reason_code = "future_window_audio_endpoint_unavailable" });
            }
            var validatedEndpoint = FutureWindowAudioEndpointPolicy.Validate(requestedEndpoint, null, endpoint, out _);
            if (validatedEndpoint is null)
                throw new ApiException(409, "FUTURE_WINDOW_AUDIO_ENDPOINT_UNAVAILABLE",
                    "The exact requested render endpoint is not active.",
                    new { reason_code = "future_window_audio_endpoint_unavailable" });
            endpointId = validatedEndpoint.Id;
            endpointName = validatedEndpoint.Name;
        }

        var now = DateTimeOffset.UtcNow;
        var authorizationId = "fwa_" + Guid.NewGuid().ToString("N");
        var filename = "future-window-" + authorizationId[4..] + ".mp4";
        var scope = new FutureWindowAuthorizationScope(
            authorizationId, request.IdempotencyKey,
            ComputeRequestDigest(sid, session, executable, endpointId, endpointName,
                request.MaximumDurationSeconds, request.ValiditySeconds, outputDirectory),
            sid, session, executable, endpointId, endpointName,
            request.MaximumDurationSeconds, request.ValiditySeconds,
            outputDirectory, filename, "pending", now, null, null, null, null, 0);

        if (!FutureWindowProcessIdentity.TryPinExecutable(executable, out var pin, out var pinFailure) || pin is null)
            throw new ApiException(409, "FUTURE_WINDOW_EXECUTABLE_UNAVAILABLE",
                "The approved executable cannot be pinned against replacement for the grant lifetime.",
                new { reason_code = pinFailure });

        var create = _repository.CreateOrGet(scope);
        if (create.Row is null)
        {
            pin.Dispose();
            throw new ApiException(500, "FUTURE_WINDOW_SETUP_FAILED", "The authorization could not be read back after persistence.");
        }
        if (create.Disposition == FutureWindowCreateDisposition.Conflict)
        {
            pin.Dispose();
            return new FutureWindowAuthorizationCreateResponse("conflict", ToState(create.Row));
        }
        if (create.Disposition == FutureWindowCreateDisposition.Created)
            HoldPin(create.Row.AuthorizationId, pin);
        else
        {
            pin.Dispose();
            if (!EnsurePin(create.Row))
            {
                _repository.SetTerminalWithoutRun(create.Row.AuthorizationId, sid, session,
                    "blocked", "executable_identity_changed", DateTimeOffset.UtcNow);
                var blocked = _repository.Get(create.Row.AuthorizationId, sid, session);
                if (blocked is null)
                    throw new ApiException(500, "FUTURE_WINDOW_SETUP_FAILED", "The authorization could not be read back after identity blocking.");
                create = (FutureWindowCreateDisposition.Existing, blocked);
            }
        }

        var row = create.Row!;
        if (row.StatusCode == "pending") StartApprovalFlight(row.AuthorizationId);
        return new FutureWindowAuthorizationCreateResponse(
            create.Disposition == FutureWindowCreateDisposition.Created ? "created" : "existing", ToState(row));
    }

    public FutureWindowAuthorizationState? Get(string authorizationId)
    {
        if (!IsValidAuthorizationId(authorizationId)) return null;
        var row = _repository.Get(authorizationId, CurrentUserSid() ?? "", CaptureAuthorizationSessionBinding.Current);
        if (row is null) return null;
        if (row.StatusCode == "active" && row.ExpiresAtUtc is { } expires && DateTimeOffset.UtcNow >= expires)
        {
            _repository.SetTerminalWithoutRun(row.AuthorizationId, row.CurrentUserSid, row.SessionBinding,
                "expired", "authorization_expired", DateTimeOffset.UtcNow);
            row = _repository.Get(authorizationId, row.CurrentUserSid, row.SessionBinding) ?? row;
            ReleasePinIfTerminal(row);
        }
        return ToState(row);
    }

    public FutureWindowAuthorizationStartResult Start(string authorizationId, string windowId)
    {
        if (!IsValidAuthorizationId(authorizationId)) return new(false, null, "future_window_not_found");
        if (!IsExecutionSupported) return new(false, null, "future_window_execution_unavailable");
        _startGate.Wait();
        try
        {
            var sid = CurrentUserSid();
            var session = CaptureAuthorizationSessionBinding.Current;
            if (string.IsNullOrWhiteSpace(sid) || string.IsNullOrWhiteSpace(session))
                return new(false, null, "future_window_user_session_unavailable");
            var row = _repository.Get(authorizationId, sid, session);
            if (row is null) return new(false, null, "future_window_not_found");
            if (row.StatusCode != "active") return new(false, null, row.StatusCode == "expired" ? "future_window_expired" : "future_window_not_active");
            var now = DateTimeOffset.UtcNow;
            if (row.ExpiresAtUtc is null || now >= row.ExpiresAtUtc.Value)
            {
                _repository.SetTerminalWithoutRun(row.AuthorizationId, sid, session, "expired", "authorization_expired", now);
                ReleasePinIfTerminal(row);
                return new(false, null, "future_window_expired");
            }
            if (!_pins.ContainsKey(row.AuthorizationId) && !EnsurePin(row))
            {
                _repository.SetTerminalWithoutRun(row.AuthorizationId, sid, session, "blocked", "executable_identity_changed", now);
                return new(false, null, "executable_identity_changed");
            }
            if (_engine.HasActiveRecording()) return new(false, null, "recording_conflict");

            var identity = FutureWindowProcessIdentity.GetCurrentUserSid();
            var sessionId = FutureWindowProcessIdentity.GetCurrentSessionId();
            if (identity is null || sessionId is null || identity != sid || row.SessionBinding != session)
                return new(false, null, "future_window_user_session_changed");

            var validation = FutureWindowProcessIdentity.ValidateOneEligibleWindow(
                row.ExecutableIdentity, row.ApprovedAtUtc!.Value, windowId, sid, sessionId.Value);
            if (!FutureWindowProcessIdentity.TryGetValidatedHold(
                    validation, out var validatedHold, out var validationFailure) || validatedHold is null)
            {
                if (validationFailure is "multiple_eligible_windows" or "executable_identity_mismatch" or "process_identity_unavailable")
                    _repository.SetTerminalWithoutRun(row.AuthorizationId, sid, session, "blocked", validationFailure, now);
                return new(false, null, validationFailure);
            }

            var processHold = validatedHold;
            try
            {
                if (FutureWindowOutputPolicy.ValidateAndProbe(row.OutputDirectory, row.ToScope().OutputFilePath) is { } outputFailure)
                {
                    _repository.SetTerminalWithoutRun(row.AuthorizationId, sid, session, "blocked", outputFailure, now);
                    return new(false, null, outputFailure);
                }

                var endpoint = ResolveEndpointForStart(row, out var endpointFailure);
                if (endpointFailure is not null)
                {
                    _repository.SetTerminalWithoutRun(row.AuthorizationId, sid, session, "blocked", endpointFailure, now);
                    return new(false, null, endpointFailure);
                }

                var config = BuildExactConfig(row, processHold.Snapshot, endpoint);
                var plan = CaptureBackendSelector.BuildPlan(config);
                if (plan.FallbackOccurred || plan.CaptureSemantics != "window_surface" ||
                    plan.SourceKind != "window" || plan.WindowHandle != processHold.Snapshot.WindowHandle ||
                    plan.TargetWindowProcessId != processHold.Snapshot.ProcessId ||
                    plan.PlannedBackend is not ("wgc-continuous" or "wgc-window-av-split") ||
                    (endpoint is null) != (plan.AudioSourceKind == AudioCaptureSourceKind.None))
                {
                    _repository.SetTerminalWithoutRun(row.AuthorizationId, sid, session,
                        "blocked", "strict_window_capture_plan_unavailable", now);
                    return new(false, null, "strict_window_capture_plan_unavailable");
                }

                FutureWindowStartCommitReceipt? receipt = null;
                string commitFailure = "future_window_start_commit_failed";
                _startInterlock.Execute("future_window_start_commit", () =>
                    receipt = _repository.TryCommitStart(authorizationId, sid, session,
                        processHold.Snapshot, DateTimeOffset.UtcNow, out commitFailure));
                if (receipt is null) return new(false, null, commitFailure);

                var start = _engine.StartFutureWindowCapture(receipt, config, plan, processHold, _tray);
                processHold = null!;
                if (!start.Accepted)
                {
                    _repository.CompleteRun(authorizationId, receipt.RunId, false,
                        start.ReasonCode ?? "future_window_engine_start_failed", null, null, null, DateTimeOffset.UtcNow);
                    ReleasePin(authorizationId);
                    return new(false, receipt.RunId, start.ReasonCode ?? "future_window_engine_start_failed");
                }
                _ = TrackRunAsync(receipt);
                _audit.Log("future_window_authorization.start_committed", new
                {
                    authorization_id = authorizationId,
                    run_id = receipt.RunId,
                    window_id = windowId,
                    process_id = receipt.Process.ProcessId,
                    executable_sha256 = row.ExecutableIdentity.Sha256,
                    maximum_duration_seconds = row.MaximumDurationSeconds,
                });
                return new(true, receipt.RunId, null);
            }
            catch (ApiException exception)
            {
                if (exception.Code == "WINDOW_SURFACE_UNAVAILABLE")
                    _repository.SetTerminalWithoutRun(row.AuthorizationId, sid, session, "blocked", "strict_window_capture_unavailable", DateTimeOffset.UtcNow);
                return new(false, null, exception.Code == "WINDOW_SURFACE_UNAVAILABLE"
                    ? "strict_window_capture_unavailable" : "future_window_start_preflight_failed");
            }
            catch
            {
                return new(false, null, "future_window_start_preflight_failed");
            }
            finally { processHold?.Dispose(); }
        }
        finally { _startGate.Release(); }
    }

    public FutureWindowAuthorizationState? Revoke(string authorizationId)
    {
        if (!IsValidAuthorizationId(authorizationId)) return null;
        var sid = CurrentUserSid();
        var session = CaptureAuthorizationSessionBinding.Current;
        if (string.IsNullOrWhiteSpace(sid) || string.IsNullOrWhiteSpace(session)) return null;
        FutureWindowAuthorizationRow? row = null;
        _startInterlock.Execute("future_window_revoke", () =>
            row = _repository.Revoke(authorizationId, sid, session, DateTimeOffset.UtcNow));
        if (row is null) return null;
        if (row.StatusCode == "revoked")
            CancelApprovalFlight(authorizationId, "authorization_revoked", waitForUiClose: true);
        if (row.StatusCode == "revoked" && row.RunId is { } runId && _engine.HasRecording(runId))
        {
            try
            {
                _engine.Stop(runId, "authorization_revoked");
                _repository.MarkRevokedRunStopped(authorizationId, runId, DateTimeOffset.UtcNow);
            }
            catch (Exception exception)
            {
                _audit.Log("future_window_authorization.revoked_stop_unconfirmed", new
                { authorization_id = authorizationId, run_id = runId, exception_type = exception.GetType().Name });
            }
        }
        else if (row.StatusCode == "revoked" && row.RunId is { } notRegisteredRunId)
        {
            // If revoke wins after the durable consume but before the engine
            // registers its in-memory Run, the final gate will reject it. Record
            // that no capture was left running instead of leaving start_committed.
            _repository.MarkRevokedRunStopped(authorizationId, notRegisteredRunId, DateTimeOffset.UtcNow);
        }
        ReleasePinIfTerminal(row);
        _audit.Log("future_window_authorization.revoked", new { authorization_id = authorizationId, run_id = row.RunId });
        return ToState(_repository.Get(authorizationId, sid, session) ?? row);
    }

    public IReadOnlyList<FutureWindowAuthorizationState> ListForSafetyCenter()
    {
        var sid = CurrentUserSid();
        var session = CaptureAuthorizationSessionBinding.Current;
        if (string.IsNullOrWhiteSpace(sid) || string.IsNullOrWhiteSpace(session)) return Array.Empty<FutureWindowAuthorizationState>();
        return _repository.List(sid, session).Select(ToState).ToArray();
    }

    public void CancelGloballyRevokedPendingApprovals(string userSid, string sessionBinding)
    {
        if (Volatile.Read(ref _disposed) != 0 ||
            string.IsNullOrWhiteSpace(userSid) || string.IsNullOrWhiteSpace(sessionBinding) ||
            !string.Equals(userSid, CurrentUserSid(), StringComparison.Ordinal) ||
            !string.Equals(sessionBinding, CaptureAuthorizationSessionBinding.Current, StringComparison.Ordinal))
            return;

        string[] flightIds;
        lock (_approvalFlightSync)
            flightIds = _approvalFlights.Keys.ToArray();

        var revokedPendingFlightIds = new List<string>();
        foreach (var authorizationId in flightIds)
        {
            try
            {
                var row = _repository.Get(authorizationId, userSid, sessionBinding);
                if (row is { StatusCode: "revoked", ApprovedAtUtc: null, RunId: null })
                {
                    ReleasePinIfTerminal(row);
                    revokedPendingFlightIds.Add(authorizationId);
                }
            }
            catch
            {
                AuditApprovalUi(authorizationId, "global_safety_state_unavailable", new
                { reason_code = "revoked_authorization_read_failed" });
            }
        }

        CancelApprovalFlights(revokedPendingFlightIds, "global_safety_revocation", waitForUiClose: true);
    }

    internal string? ValidateBackendStart(FutureWindowOneShotExecutionTicket ticket, DateTimeOffset nowUtc) =>
        _repository.ValidateAtBackendStart(ticket, nowUtc);

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _lifetime.Cancel();
        Task[] approvalTasks;
        lock (_approvalFlightSync)
        {
            approvalTasks = _approvalFlights.Values.Select(flight => flight.Task).ToArray();
            foreach (var flight in _approvalFlights.Values)
                flight.Cancel("host_shutdown");
        }
        foreach (var pair in _pins) if (_pins.TryRemove(pair.Key, out var pin)) pin.Dispose();
        var allApprovals = Task.WhenAll(approvalTasks);
        if (allApprovals.IsCompleted)
            _lifetime.Dispose();
        else
            _ = allApprovals.ContinueWith(_ => _lifetime.Dispose(), CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }

    private void Recover()
    {
        var now = DateTimeOffset.UtcNow;
        _repository.RecoverUncertainStarts(now);
        var sid = CurrentUserSid();
        var session = CaptureAuthorizationSessionBinding.Current;
        if (string.IsNullOrWhiteSpace(sid) || string.IsNullOrWhiteSpace(session)) return;
        foreach (var row in _repository.List(sid, session))
        {
            if (row.StatusCode is "pending" or "active")
            {
                if (!EnsurePin(row))
                    _repository.SetTerminalWithoutRun(row.AuthorizationId, sid, session,
                        "blocked", "executable_identity_changed", now);
                else if (row.StatusCode == "pending") StartApprovalFlight(row.AuthorizationId);
            }
            else ReleasePinIfTerminal(row);
        }
    }

    private Task StartApprovalFlight(string authorizationId)
    {
        lock (_approvalFlightSync)
        {
            if (_approvalFlights.TryGetValue(authorizationId, out var existing))
                return existing.Task;
            if (Volatile.Read(ref _disposed) != 0)
                return Task.CompletedTask;

            var flight = new ApprovalFlight();
            _approvalFlights.Add(authorizationId, flight);
            flight.Task = Task.Run(() => ApprovePendingAsync(authorizationId, flight));
            return flight.Task;
        }
    }

    internal Task StartApprovalFlightForTests(string authorizationId) => StartApprovalFlight(authorizationId);

    private async Task ApprovePendingAsync(string authorizationId, ApprovalFlight flight)
    {
        var elapsed = Stopwatch.StartNew();
        using var deadline = new CancellationTokenSource(_approvalUiWaitLimit);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(
            _lifetime.Token, flight.Revocation.Token, deadline.Token);
        LocalSetupUiSerializationGate.Lease? uiLease = null;
        Task<FutureWindowApprovalOutcome>? presentation = null;
        string? sid = null;
        string? session = null;
        try
        {
            // Bind the durable row before waiting on the process-wide UI gate.
            // A gate timeout must still be able to publish a queryable terminal
            // failure instead of leaving the authorization pending indefinitely.
            sid = CurrentUserSid();
            session = CaptureAuthorizationSessionBinding.Current;
            AuditApprovalUi(authorizationId, "gate_waiting", new
            {
                process_id = Environment.ProcessId,
                thread_id = Environment.CurrentManagedThreadId,
                wait_limit_seconds = checked((int)_approvalUiWaitLimit.TotalSeconds),
            });
            try
            {
                uiLease = await LocalSetupUiSerializationGate.Instance
                    .WaitAsync(cancellation.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                await HandleApprovalCancellationAsync(authorizationId, flight, deadline,
                    sid, session, elapsed.ElapsedMilliseconds).ConfigureAwait(false);
                return;
            }

            AuditApprovalUi(authorizationId, "gate_acquired", new
            {
                process_id = Environment.ProcessId,
                thread_id = Environment.CurrentManagedThreadId,
                elapsed_ms = elapsed.ElapsedMilliseconds,
            });
            if (string.IsNullOrWhiteSpace(sid) || string.IsNullOrWhiteSpace(session))
            {
                FailPendingApproval(authorizationId, sid, session,
                    "approval_ui_unavailable", "future_window_user_session_unavailable");
                return;
            }

            var row = _repository.Get(authorizationId, sid, session);
            if (row is null || row.StatusCode != "pending") return;
            if (!EnsurePin(row))
            {
                FailPendingApproval(authorizationId, sid, session,
                    "executable_identity_changed", "executable_identity_changed", "blocked");
                return;
            }
            if (!IsOutputDirectoryAvailable(row.OutputDirectory))
            {
                FailPendingApproval(authorizationId, sid, session,
                    "future_window_output_environment_unavailable", "future_window_output_environment_unavailable", "blocked");
                return;
            }
            if (!_approvalDesktopAvailable())
            {
                FailPendingApproval(authorizationId, sid, session,
                    "approval_ui_unavailable", "interactive_desktop_unavailable");
                return;
            }

            string? endpointFailure = null;
            var endpoint = row.SystemAudioEndpointId is null
                ? null : ResolveEndpointForStart(row, out endpointFailure);
            if (row.SystemAudioEndpointId is not null && endpoint is null)
            {
                FailPendingApproval(authorizationId, sid, session,
                    endpointFailure ?? "future_window_audio_endpoint_unavailable",
                    endpointFailure ?? "future_window_audio_endpoint_unavailable", "blocked");
                return;
            }

            var details = new FutureWindowAuthorizationApprovalDetails(
                row.ExecutableIdentity.CanonicalPath,
                row.ExecutableIdentity.FileIdentity,
                row.ExecutableIdentity.Sha256,
                row.ExecutableIdentity.SignerSubject,
                row.ExecutableIdentity.SignerCertificateSha256,
                endpoint?.Name,
                endpoint?.Id,
                row.MaximumDurationSeconds,
                row.ValiditySeconds,
                row.ToScope().OutputFilePath,
                sid,
                session);
            var languageSnapshot = _approvalTextProvider().Language;
            var textSnapshot = new UiTextProvider(languageSnapshot);
            var audit = new Action<string, object>((stage, payload) =>
                AuditApprovalUi(authorizationId, stage, payload));
            presentation = _approvalUi.ShowAsync(
                authorizationId, details, textSnapshot, audit, cancellation.Token);

            FutureWindowApprovalOutcome outcome;
            try
            {
                outcome = await presentation.WaitAsync(cancellation.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                await ObservePresentationCloseAsync(authorizationId, presentation).ConfigureAwait(false);
                await HandleApprovalCancellationAsync(authorizationId, flight, deadline,
                    sid, session, elapsed.ElapsedMilliseconds).ConfigureAwait(false);
                return;
            }

            if (cancellation.IsCancellationRequested)
            {
                await ObservePresentationCloseAsync(authorizationId, presentation).ConfigureAwait(false);
                await HandleApprovalCancellationAsync(authorizationId, flight, deadline,
                    sid, session, elapsed.ElapsedMilliseconds).ConfigureAwait(false);
                return;
            }

            if (outcome == FutureWindowApprovalOutcome.Cancelled)
            {
                FailPendingApproval(authorizationId, sid, session,
                    "approval_ui_unavailable", "approval_ui_cancelled_without_owner");
                return;
            }
            if (outcome == FutureWindowApprovalOutcome.Unavailable)
            {
                FailPendingApproval(authorizationId, sid, session,
                    "approval_ui_display_failed", "approval_ui_display_failed");
                return;
            }
            if (outcome == FutureWindowApprovalOutcome.Rejected)
            {
                _repository.SetTerminalWithoutRun(authorizationId, sid, session,
                    "blocked", "local_approval_declined", DateTimeOffset.UtcNow);
                ReleasePin(authorizationId);
                AuditApprovalUi(authorizationId, "decision_terminal", new
                { decision = "rejected", reason_code = "local_approval_declined" });
                return;
            }

            if (!_approvalDesktopAvailable())
            {
                FailPendingApproval(authorizationId, sid, session,
                    "approval_ui_unavailable", "interactive_desktop_lost_before_activation");
                return;
            }

            var now = DateTimeOffset.UtcNow;
            FutureWindowAuthorizationRow? activated = null;
            _startInterlock.Execute("future_window_approval", () =>
                activated = _repository.Approve(authorizationId, sid, session,
                    "future-approval-" + Guid.NewGuid().ToString("N"), now));
            if (activated is null)
            {
                var latest = _repository.Get(authorizationId, sid, session);
                if (latest?.StatusCode == "pending")
                    _repository.SetTerminalWithoutRun(authorizationId, sid, session,
                        "blocked", "unattended_disabled_or_grant_revoked", now);
                ReleasePin(authorizationId);
                AuditApprovalUi(authorizationId, "activation_not_committed", new
                { reason_code = latest?.StatusCode == "revoked" ? "authorization_revoked" : "authorization_changed" });
                return;
            }
            _audit.Log("future_window_authorization.approved_locally", new
            {
                authorization_id = authorizationId,
                executable_sha256 = activated.ExecutableIdentity.Sha256,
                system_audio_endpoint_id = activated.SystemAudioEndpointId,
                maximum_duration_seconds = activated.MaximumDurationSeconds,
                expires_at_utc = activated.ExpiresAtUtc,
            });
        }
        catch (OperationCanceledException) when (flight.Revocation.IsCancellationRequested || _lifetime.IsCancellationRequested || deadline.IsCancellationRequested)
        {
            await ObservePresentationCloseAsync(authorizationId, presentation).ConfigureAwait(false);
            await HandleApprovalCancellationAsync(authorizationId, flight, deadline,
                sid, session, elapsed.ElapsedMilliseconds).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            FailPendingApproval(authorizationId, sid, session,
                "approval_ui_unavailable", "local_approval_unavailable");
            _audit.Log("future_window_authorization.approval_failed", new
            { authorization_id = authorizationId, exception_type = exception.GetType().Name });
        }
        finally
        {
            if (uiLease is not null)
            {
                uiLease.Dispose();
                AuditApprovalUi(authorizationId, "gate_released", new
                {
                    process_id = Environment.ProcessId,
                    thread_id = Environment.CurrentManagedThreadId,
                    elapsed_ms = elapsed.ElapsedMilliseconds,
                });
            }
            FinishApprovalFlight(authorizationId, flight);
        }
    }

    private async Task ObservePresentationCloseAsync(
        string authorizationId,
        Task<FutureWindowApprovalOutcome>? presentation)
    {
        if (presentation is null) return;
        try
        {
            _ = await presentation.WaitAsync(ApprovalUiCloseGrace).ConfigureAwait(false);
            AuditApprovalUi(authorizationId, "closed_after_cancellation", new
            { elapsed_grace_ms = (int)ApprovalUiCloseGrace.TotalMilliseconds });
        }
        catch (TimeoutException)
        {
            AuditApprovalUi(authorizationId, "close_unconfirmed", new
            { reason_code = "approval_ui_close_timeout" });
        }
        catch
        {
            AuditApprovalUi(authorizationId, "closed_after_cancellation", new
            { reason_code = "approval_ui_task_ended" });
        }
    }

    private async Task HandleApprovalCancellationAsync(
        string authorizationId,
        ApprovalFlight flight,
        CancellationTokenSource deadline,
        string? sid,
        string? session,
        long elapsedMilliseconds)
    {
        if (flight.Revocation.IsCancellationRequested)
        {
            AuditApprovalUi(authorizationId, "cancelled", new
            { reason_code = flight.CancelReason, elapsed_ms = elapsedMilliseconds });
            return;
        }
        if (_lifetime.IsCancellationRequested)
        {
            AuditApprovalUi(authorizationId, "cancelled", new
            { reason_code = "host_shutdown", elapsed_ms = elapsedMilliseconds });
            return;
        }
        if (deadline.IsCancellationRequested)
        {
            if (!string.IsNullOrWhiteSpace(sid) && !string.IsNullOrWhiteSpace(session))
                _repository.SetTerminalWithoutRun(authorizationId, sid, session,
                    "failed", "approval_ui_timeout", DateTimeOffset.UtcNow);
            ReleasePin(authorizationId);
            AuditApprovalUi(authorizationId, "timeout", new
            { reason_code = "approval_ui_timeout", elapsed_ms = elapsedMilliseconds });
            return;
        }
        await Task.CompletedTask.ConfigureAwait(false);
    }

    private void FailPendingApproval(
        string authorizationId,
        string? sid,
        string? session,
        string statusReason,
        string auditReason,
        string status = "failed")
    {
        if (!string.IsNullOrWhiteSpace(sid) && !string.IsNullOrWhiteSpace(session))
            _repository.SetTerminalWithoutRun(authorizationId, sid, session, status, statusReason, DateTimeOffset.UtcNow);
        ReleasePin(authorizationId);
        AuditApprovalUi(authorizationId, "unavailable", new { reason_code = auditReason, status });
    }

    private void AuditApprovalUi(string authorizationId, string stage, object payload)
    {
        try
        {
            _audit.Log("future_window_authorization.approval_ui_" + stage, new
            {
                authorization_id = authorizationId,
                details = payload,
            });
        }
        catch { }
    }

    private void CancelApprovalFlight(string authorizationId, string reasonCode, bool waitForUiClose)
    {
        CancelApprovalFlights([authorizationId], reasonCode, waitForUiClose);
    }

    private void CancelApprovalFlights(
        IEnumerable<string> authorizationIds,
        string reasonCode,
        bool waitForUiClose)
    {
        var flights = new List<(string AuthorizationId, ApprovalFlight Flight)>();
        var cancelFailures = new List<string>();
        lock (_approvalFlightSync)
        {
            foreach (var authorizationId in authorizationIds.Distinct(StringComparer.Ordinal))
            {
                if (!_approvalFlights.TryGetValue(authorizationId, out var flight)) continue;
                try { flight.Cancel(reasonCode); }
                catch { cancelFailures.Add(authorizationId); }
                flights.Add((authorizationId, flight));
            }
        }

        foreach (var authorizationId in cancelFailures)
            AuditApprovalUi(authorizationId, "cancel_request_failed", new { reason_code = reasonCode });
        if (flights.Count == 0) return;
        foreach (var (authorizationId, _) in flights)
            AuditApprovalUi(authorizationId, "cancel_requested", new { reason_code = reasonCode });
        if (!waitForUiClose) return;

        try
        {
            if (!Task.WhenAll(flights.Select(item => item.Flight.Task)).Wait(ApprovalUiCloseGrace))
            {
                foreach (var (authorizationId, flight) in flights.Where(item => !item.Flight.Task.IsCompleted))
                    AuditApprovalUi(authorizationId, "close_unconfirmed", new { reason_code = "approval_ui_close_timeout" });
            }
        }
        catch
        {
            foreach (var (authorizationId, flight) in flights.Where(item => !item.Flight.Task.IsCompleted))
                AuditApprovalUi(authorizationId, "close_unconfirmed", new { reason_code = "approval_ui_task_failed" });
        }
    }

    private void FinishApprovalFlight(string authorizationId, ApprovalFlight flight)
    {
        lock (_approvalFlightSync)
        {
            if (_approvalFlights.TryGetValue(authorizationId, out var current) && ReferenceEquals(current, flight))
                _approvalFlights.Remove(authorizationId);
            flight.Revocation.Dispose();
        }
    }

    private bool EnsurePin(FutureWindowAuthorizationRow row)
    {
        if (_pins.ContainsKey(row.AuthorizationId)) return true;
        if (!FutureWindowProcessIdentity.TryPinExecutable(row.ExecutableIdentity, out var pin, out _) || pin is null)
            return false;
        HoldPin(row.AuthorizationId, pin);
        return _pins.ContainsKey(row.AuthorizationId);
    }

    private void HoldPin(string authorizationId, FutureWindowExecutablePin pin)
    {
        if (!_pins.TryAdd(authorizationId, pin)) pin.Dispose();
    }

    private SystemAudioEndpointInfo? ResolveEndpointForStart(FutureWindowAuthorizationRow row, out string? failure)
    {
        failure = null;
        if (row.SystemAudioEndpointId is null) return null;
        try
        {
            var endpoint = _engine.SystemAudioEndpointProvider.GetEndpointAsync(row.SystemAudioEndpointId)
                .WaitAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
            return FutureWindowAudioEndpointPolicy.Validate(
                row.SystemAudioEndpointId, row.SystemAudioEndpointName, endpoint, out failure);
        }
        catch { failure = "future_window_audio_endpoint_unavailable"; return null; }
    }

    private CaptureConfig BuildExactConfig(
        FutureWindowAuthorizationRow row,
        FutureWindowProcessSnapshot process,
        SystemAudioEndpointInfo? endpoint)
    {
        var configNode = new JsonObject
        {
            ["required_capture_semantics"] = "window_surface",
            ["countdown_seconds"] = 0,
            ["source"] = new JsonObject { ["type"] = "window", ["window_id"] = process.WindowId },
            ["stop_condition"] = new JsonObject { ["type"] = "duration", ["seconds"] = row.MaximumDurationSeconds },
            ["audio"] = endpoint is null
                ? new JsonObject
                {
                    ["microphone"] = new JsonObject { ["enabled"] = false },
                    ["system_audio"] = new JsonObject { ["enabled"] = false }
                }
                : new JsonObject
                {
                    ["microphone"] = new JsonObject { ["enabled"] = false },
                    ["system_audio"] = new JsonObject { ["enabled"] = true, ["device_id"] = endpoint.Id }
                },
            ["output"] = new JsonObject
            {
                ["directory"] = row.OutputDirectory,
                ["filename"] = row.OutputFileName,
                ["conflict_policy"] = "fail"
            },
            ["video"] = new JsonObject { ["fps"] = 30, ["quality"] = "medium" }
        };
        var parsed = ConfigParser.Build(configNode, "future_window_one_shot", out _,
            _engine.MicrophoneProvider, _engine.MicrophoneStatusProvider,
            _engine.SystemAudioEndpointProvider, endpoint);
        parsed.Config.OutputConflictPolicy = "fail_if_exists";
        parsed.Config.OutputPath = row.ToScope().OutputFilePath;
        if (parsed.OutputPath != row.ToScope().OutputFilePath || parsed.Config.CountdownSeconds != 0 ||
            parsed.Config.DurationSeconds != row.MaximumDurationSeconds || parsed.Config.Microphone ||
            !string.Equals(parsed.Config.OutputPath, row.ToScope().OutputFilePath, StringComparison.Ordinal))
            throw new ApiException(409, "FUTURE_WINDOW_SPECIFICATION_MISMATCH", "The strict capture specification did not match the approved scope.");
        return parsed.Config;
    }

    internal Task TrackRunForTests(FutureWindowStartCommitReceipt receipt) => TrackRunAsync(receipt);

    private async Task TrackRunAsync(FutureWindowStartCommitReceipt receipt)
    {
        var row = receipt.Authorization;
        try
        {
            while (!_lifetime.IsCancellationRequested)
            {
                await Task.Delay(500, _lifetime.Token).ConfigureAwait(false);
                JsonElement status;
                try { status = JsonSerializer.SerializeToElement(_engine.GetStatus(receipt.RunId)); }
                catch
                {
                    _repository.CompleteRun(row.AuthorizationId, receipt.RunId, false,
                        "run_status_unavailable", null, null, null, DateTimeOffset.UtcNow);
                    ReleasePin(row.AuthorizationId);
                    return;
                }
                var state = status.TryGetProperty("status", out var statusElement) ? statusElement.GetString() : null;
                if (state is not ("completed" or "failed" or "stopped" or "cancelled" or "rejected")) continue;

                var outputPath = status.TryGetProperty("output", out var output) && output.TryGetProperty("path", out var path)
                    ? path.GetString() : null;
                var bytes = status.TryGetProperty("output", out output) && output.TryGetProperty("bytes_written", out var byteCount) && byteCount.TryGetInt64(out var count)
                    ? count : 0;
                var durationSeconds = status.TryGetProperty("output", out output) && output.TryGetProperty("duration_seconds", out var duration) && duration.TryGetDouble(out var seconds)
                    ? seconds : 0;
                var success = state == "completed" &&
                    string.Equals(outputPath, row.OutputFilePath, StringComparison.OrdinalIgnoreCase) &&
                    bytes > 0 && durationSeconds > 0 && durationSeconds <= row.MaximumDurationSeconds + 2;
                var current = _repository.Get(row.AuthorizationId, row.CurrentUserSid, row.SessionBinding);
                if (current?.StatusCode == "revoked")
                    _repository.MarkRevokedRunStopped(row.AuthorizationId, receipt.RunId, DateTimeOffset.UtcNow);
                else
                    _repository.CompleteRun(row.AuthorizationId, receipt.RunId, success,
                        success ? "" : _engine.GetTrustedStorageFailureReason(receipt.RunId) ??
                            (state == "stopped" ? "stopped_before_duration" : "recording_failed"),
                        success ? outputPath : null, success ? bytes : null,
                        success ? checked((long)Math.Round(durationSeconds * 1000)) : null,
                        DateTimeOffset.UtcNow);
                ReleasePin(row.AuthorizationId);
                return;
            }
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch { }
    }

    private bool TryCanonicalOutputDirectory(string input, out string canonical, out string reason)
    {
        canonical = string.Empty;
        reason = "future_window_output_directory_unavailable";
        try
        {
            canonical = Path.TrimEndingDirectorySeparator(Path.GetFullPath(input));
            if (!Path.IsPathFullyQualified(canonical) || !Directory.Exists(canonical) ||
                (File.GetAttributes(canonical) & FileAttributes.ReparsePoint) != 0)
                return false;
            reason = FutureWindowOutputPolicy.ValidateAndProbe(canonical,
                Path.Combine(canonical, ".agent-recorder-future-validation-never-create.mp4")) ?? string.Empty;
            return reason.Length == 0;
        }
        catch (UnauthorizedAccessException) { reason = "future_window_output_directory_unwritable"; return false; }
        catch (Exception) { reason = "future_window_output_directory_unavailable"; return false; }
    }

    private static bool IsOutputDirectoryAvailable(string path)
    {
        try
        {
            var canonical = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
            if (!Directory.Exists(canonical) || (File.GetAttributes(canonical) & FileAttributes.ReparsePoint) != 0) return false;
            var probePath = Path.Combine(canonical, ".agent-recorder-future-approval-probe-" + Guid.NewGuid().ToString("N"));
            using var stream = new FileStream(probePath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1, FileOptions.DeleteOnClose);
            stream.WriteByte(0x41);
            return true;
        }
        catch { return false; }
    }

    private static void ValidateRequest(FutureWindowOneShotCreateRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.IdempotencyKey) || request.IdempotencyKey.Length > 128 ||
            request.IdempotencyKey.Trim() != request.IdempotencyKey ||
            request.IdempotencyKey.Any(char.IsControl))
            throw new ApiException(400, "INVALID_ARGUMENT", "Idempotency-Key is invalid.");
        if (!Path.IsPathFullyQualified(request.ExecutablePath) || !Path.IsPathFullyQualified(request.OutputDirectory) ||
            request.MaximumDurationSeconds is < 1 or > 1800 || request.ValiditySeconds is < 1 or > 3600 ||
            request.MaximumDurationSeconds > request.ValiditySeconds ||
            request.SystemAudioEndpointId?.Any(char.IsControl) == true)
            throw new ApiException(400, "INVALID_ARGUMENT", "The future-window authorization scope is invalid.");
    }

    private static string ComputeRequestDigest(
        string sid, string session, FutureWindowExecutableIdentity executable,
        string? endpointId, string? endpointName, int duration, int validity, string outputDirectory)
    {
        var builder = new StringBuilder();
        void Field(string key, string? value)
        {
            builder.Append(key).Append('=');
            if (value is null) builder.Append("<null>");
            else builder.Append(value.Length).Append(':').Append(value);
            builder.Append('\n');
        }
        Field("schema", "future-window-request/v1"); Field("sid", sid); Field("session", session);
        Field("executable_path", executable.CanonicalPath); Field("executable_file", executable.FileIdentity);
        Field("executable_sha256", executable.Sha256); Field("signer", executable.SignerSubject);
        Field("signer_cert_sha256", executable.SignerCertificateSha256); Field("endpoint_id", endpointId);
        Field("endpoint_name", endpointName); Field("duration", duration.ToString(System.Globalization.CultureInfo.InvariantCulture));
        Field("validity", validity.ToString(System.Globalization.CultureInfo.InvariantCulture)); Field("output_directory", outputDirectory);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString()))).ToLowerInvariant();
    }

    private static FutureWindowAuthorizationState ToState(FutureWindowAuthorizationRow row) => new(
        row.AuthorizationId, row.StatusCode, row.ReasonCode,
        row.ExecutableIdentity.CanonicalPath, row.ExecutableIdentity.FileIdentity, row.ExecutableIdentity.Sha256,
        row.ExecutableIdentity.SignerSubject, row.ExecutableIdentity.SignerCertificateSha256,
        row.SystemAudioEndpointId is null ? "none" : "system_loopback", row.SystemAudioEndpointId,
        row.SystemAudioEndpointName, row.MaximumDurationSeconds, row.ValiditySeconds,
        row.ToScope().OutputFilePath, row.CreatedAtUtc, row.ApprovedAtUtc, row.ExpiresAtUtc,
        row.RunId, row.RunStatus, row.WindowId, row.ProcessId, row.OutputPath,
        row.OutputSizeBytes, row.ActualDurationMs, row.Version,
        row.ExpiresAtUtc?.AddSeconds(-row.MaximumDurationSeconds));

    private static bool IsValidAuthorizationId(string? value) =>
        !string.IsNullOrWhiteSpace(value) && value.Length == 36 && value.StartsWith("fwa_", StringComparison.Ordinal) &&
        Guid.TryParseExact(value.AsSpan(4), "N", out _);

    private static string? CurrentUserSid()
    {
        try { return WindowsIdentity.GetCurrent().User?.Value; }
        catch { return null; }
    }

    private static bool FfmpegRuntimeAvailable()
    {
        try { return File.Exists(FfmpegLocator.FfmpegPath) && File.Exists(FfmpegLocator.FfprobePath); }
        catch { return false; }
    }

    private void ReleasePinIfTerminal(FutureWindowAuthorizationRow row)
    {
        if (row.StatusCode is "revoked" or "expired" or "blocked" or "failed" or "completed")
            ReleasePin(row.AuthorizationId);
    }

    private void ReleasePin(string authorizationId)
    {
        if (_pins.TryRemove(authorizationId, out var pin)) pin.Dispose();
    }

    private sealed class ApprovalFlight
    {
        private string? _cancelReason;

        internal CancellationTokenSource Revocation { get; } = new();
        internal Task Task { get; set; } = Task.CompletedTask;
        internal string CancelReason => Volatile.Read(ref _cancelReason) ?? "approval_ui_cancelled";

        internal void Cancel(string reason)
        {
            Interlocked.CompareExchange(ref _cancelReason, reason, null);
            if (!Revocation.IsCancellationRequested)
                Revocation.Cancel();
        }
    }
}
