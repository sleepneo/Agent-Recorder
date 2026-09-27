using System.Security.Principal;
using AgentRecorder.Api;
using AgentRecorder.Capture;
using AgentRecorder.Core;
using AgentRecorder.Infrastructure;
using AgentRecorder.Logging;
using AgentRecorder.Persistence;
using AgentRecorder.Windows;

namespace AgentRecorder.App;

/// <summary>
/// Bounded SID/session-scoped natural wake for required once plans. The
/// durable claim and start commit are SQLite-owned; every occurrence gets a
/// distinct local execution approval before RecordingEngine can construct a
/// backend.
/// </summary>
internal sealed class RequiredOncePlanExecutionCoordinator : IDisposable
{
    internal const int RecoveryBatchLimit = 32;
    internal const int MaximumRecoveryBatches = 8;

    private readonly SqliteOperationalStore _store;
    private readonly SqliteRequiredOnceExecutionRepository _repository;
    private readonly RecordingEngine _engine;
    private readonly ITrayContext _tray;
    private readonly AuditLogger _audit;
    private readonly IRequiredOncePlanExecutionUi _ui;
    private readonly Func<IReadOnlyList<StandingLeaseDisplayMetadata>> _currentDisplays;
    private readonly Func<RequiredOncePrincipal?> _principal;
    private readonly Func<DateTimeOffset?> _utcNow;
    private readonly IStandingLeaseOutputReadinessProvider _outputReadiness;
    private readonly TimeSpan _pollInterval;
    private readonly Func<RequiredOnceCaptureExecutionTicket, RequiredOnceCaptureExecutionResult>? _engineStartForTest;
    private readonly Func<Task>? _beforeApprovalCommitForTest;
    private readonly Action? _beforeAtomicEnvironmentRecheckForTest;
    private readonly Action<string>? _failureHookForTest;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly SemaphoreSlim _pollGate = new(1, 1);
    private readonly object _sync = new();
    private Task? _loop;
    private int _started;
    private int _healthy;
    private int _disposed;

    internal RequiredOncePlanExecutionCoordinator(
        SqliteOperationalStore store,
        RecordingEngine engine,
        ITrayContext tray,
        AuditLogger audit,
        IRequiredOncePlanExecutionUi ui,
        Func<IReadOnlyList<StandingLeaseDisplayMetadata>>? currentDisplaysForTest = null,
        Func<RequiredOncePrincipal?>? principalForTest = null,
        Func<DateTimeOffset?>? utcNowForTest = null,
        IStandingLeaseOutputReadinessProvider? outputReadinessForTest = null,
        TimeSpan? pollIntervalForTest = null,
        Func<RequiredOnceCaptureExecutionTicket, RequiredOnceCaptureExecutionResult>? engineStartForTest = null,
        Func<Task>? beforeApprovalCommitForTest = null,
        Action? beforeAtomicEnvironmentRecheckForTest = null,
        Action<string>? failureHookForTest = null)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _repository = new SqliteRequiredOnceExecutionRepository(_store, failureHookForTest);
        _engine = engine ?? throw new ArgumentNullException(nameof(engine));
        _tray = tray ?? throw new ArgumentNullException(nameof(tray));
        _audit = audit ?? throw new ArgumentNullException(nameof(audit));
        _ui = ui ?? throw new ArgumentNullException(nameof(ui));
        _currentDisplays = currentDisplaysForTest ?? (() => SystemQueryDisplayTopologyProvider.Instance.GetCurrentExecutionMetadata());
        _principal = principalForTest ?? ReadCurrentPrincipal;
        _utcNow = utcNowForTest ?? (() => DateTimeOffset.UtcNow);
        _outputReadiness = outputReadinessForTest ?? SystemQueryStandingLeaseOutputReadinessProvider.Instance;
        _pollInterval = pollIntervalForTest.GetValueOrDefault(TimeSpan.FromSeconds(1));
        if (_pollInterval <= TimeSpan.Zero || _pollInterval > TimeSpan.FromMinutes(1))
            throw new ArgumentOutOfRangeException(nameof(pollIntervalForTest));
        _engineStartForTest = engineStartForTest;
        _beforeApprovalCommitForTest = beforeApprovalCommitForTest;
        _beforeAtomicEnvironmentRecheckForTest = beforeAtomicEnvironmentRecheckForTest;
        _failureHookForTest = failureHookForTest;
    }

    internal bool ExecutionSupported =>
        Volatile.Read(ref _started) != 0 && Volatile.Read(ref _healthy) != 0 &&
        Volatile.Read(ref _disposed) == 0 && SafeUiAvailable();

    internal Task? LoopTaskForTests => _loop;

    internal bool Start()
    {
        lock (_sync)
        {
            if (Volatile.Read(ref _disposed) != 0) return false;
            if (Volatile.Read(ref _started) != 0) return ExecutionSupported;
            if (!RecoverAtStartup()) return false;
            try
            {
                _ = ListDueForCurrentIdentity();
                Volatile.Write(ref _healthy, 1);
                _loop = Task.Run(() => RunAsync(_lifetime.Token));
                Volatile.Write(ref _started, 1);
                SafeAudit("required_once_execution.runtime_started", new { execution_supported = ExecutionSupported });
                return true;
            }
            catch
            {
                Volatile.Write(ref _healthy, 0);
                SafeAudit("required_once_execution.runtime_blocked", new
                {
                    reason_code = "required_once_execution_startup_query_failed",
                    execution_supported = false,
                });
                return false;
            }
        }
    }

    internal async Task<int> ProcessDueOnceAsync(CancellationToken cancellationToken = default)
    {
        if (Volatile.Read(ref _disposed) != 0) return 0;
        await _pollGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var now = ReadUtcNow();
            var principal = ReadPrincipal();
            if (principal is null)
            {
                Volatile.Write(ref _healthy, 0);
                return 0;
            }
            var candidates = _repository.ListDue(principal.Sid, principal.Session, now);
            Volatile.Write(ref _healthy, 1);
            var processed = 0;
            foreach (var candidate in candidates.Take(SqliteRequiredOnceExecutionRepository.MaximumCandidates))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (Volatile.Read(ref _disposed) != 0) break;
                await DispatchOneAsync(candidate, principal, cancellationToken).ConfigureAwait(false);
                processed++;
            }
            return processed;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            Volatile.Write(ref _healthy, 0);
            SafeAudit("required_once_execution.poll_failed", new
            {
                reason_code = "required_once_natural_wake_tick_failed",
                exception_type = exception.GetType().Name,
            });
            return 0;
        }
        finally { _pollGate.Release(); }
    }

    private async Task DispatchOneAsync(
        RequiredOnceExecutionCandidate candidate,
        RequiredOncePrincipal expectedPrincipal,
        CancellationToken cancellationToken)
    {
        var specification = candidate.Specification;
        var now = ReadUtcNow();
        if (now >= specification.LatestStartUtc)
        {
            _repository.MarkMissed(candidate, now);
            SafeAudit("required_once_execution.terminal", Event(specification, "missed", "latest_start_window_missed"));
            return;
        }
        if (ReadPrincipal() != expectedPrincipal)
        {
            _repository.MarkUnavailable(candidate, "execution_identity_changed", now);
            return;
        }
        if (!SafeUiAvailable())
        {
            _repository.MarkUnavailable(candidate, "interactive_desktop_unavailable", now);
            return;
        }
        if (_engine.HasActiveRecording()) return;

        using var uiLease = await LocalSetupUiSerializationGate.Instance.WaitAsync(cancellationToken).ConfigureAwait(false);
        now = ReadUtcNow();
        if (now >= specification.LatestStartUtc)
        {
            _repository.MarkMissed(candidate, now);
            SafeAudit("required_once_execution.terminal", Event(specification, "missed", "latest_start_window_missed"));
            return;
        }
        var beforeDialogFailure = ValidateEnvironment(specification, expectedPrincipal, now);
        if (beforeDialogFailure is not null)
        {
            _repository.MarkUnavailable(candidate, beforeDialogFailure, now);
            SafeAudit("required_once_execution.terminal", Event(specification, "blocked", beforeDialogFailure));
            return;
        }

        var executionId = "required-once-execution-" + Guid.NewGuid().ToString("N");
        var claimed = _repository.ClaimForConfirmation(candidate, executionId, now);
        if (!claimed.Succeeded)
        {
            SafeAudit("required_once_execution.claim", Event(specification, "not_claimed", claimed.ReasonCode));
            return;
        }
        SafeAudit("required_once_execution.confirmation_claimed", new
        {
            plan_id = specification.PlanId,
            occurrence_id = specification.OccurrenceId,
            status = "pending_confirmation",
            reason_code = "local_execution_confirmation_required",
        });

        var approvalDetails = BuildApprovalDetails(specification);
        var approval = await ShowApprovalWithTimeoutAsync(approvalDetails, specification.LatestStartUtc, cancellationToken)
            .ConfigureAwait(false);
        if (approval != RequiredOnceExecutionApprovalResult.Approved)
        {
            var settledAt = SafeNowOr(now);
            var latestStartMissed = settledAt >= specification.LatestStartUtc;
            var expired = latestStartMissed || approval == RequiredOnceExecutionApprovalResult.TimedOut ||
                approval == RequiredOnceExecutionApprovalResult.HostShutdown && _lifetime.IsCancellationRequested;
            var reason = latestStartMissed ? "latest_start_window_missed" : approval switch
            {
                RequiredOnceExecutionApprovalResult.Rejected => "execution_confirmation_rejected_by_user",
                RequiredOnceExecutionApprovalResult.TimedOut => "execution_confirmation_timed_out",
                RequiredOnceExecutionApprovalResult.HostShutdown => "execution_confirmation_host_shutdown",
                _ => "interactive_desktop_unavailable",
            };
            var status = expired ? "expired" : approval == RequiredOnceExecutionApprovalResult.Unavailable ? "blocked" : "rejected";
            _repository.SettleBeforeStart(executionId, status, reason, settledAt);
            SafeAudit("required_once_execution.terminal", Event(specification, status, reason));
            return;
        }

        if (_beforeApprovalCommitForTest is not null)
            await _beforeApprovalCommitForTest().ConfigureAwait(false);

        var approvalNow = ReadUtcNow();
        var afterDialogFailure = ValidateEnvironment(specification, expectedPrincipal, approvalNow);
        if (afterDialogFailure is not null)
        {
            var status = afterDialogFailure == "latest_start_window_missed" ? "expired" : "blocked";
            _repository.SettleBeforeStart(executionId, status, afterDialogFailure, approvalNow);
            SafeAudit("required_once_execution.terminal", Event(specification, status, afterDialogFailure));
            return;
        }

        var confirmation = new Confirmation
        {
            RecordingId = "pending-required-once-run",
            CreatedAtUtc = approvalNow.UtcDateTime,
            TimeoutSeconds = 0,
        };
        var proofId = "required-once-proof-" + Guid.NewGuid().ToString("N");
        var proofNonce = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
        var receipt = _repository.CommitStart(
            executionId,
            specification,
            confirmation.Id,
            proofId,
            proofNonce,
            approvalNow,
            approvalNow,
            expectedPrincipal.Sid,
            expectedPrincipal.Session,
            () =>
            {
                _beforeAtomicEnvironmentRecheckForTest?.Invoke();
                return ValidateEnvironment(specification, expectedPrincipal, ReadUtcNow());
            });
        if (receipt is null)
        {
            var failedAt = SafeNowOr(approvalNow);
            var terminalStatus = failedAt >= specification.LatestStartUtc ? "expired" : "blocked";
            _repository.SettleBeforeStart(executionId, terminalStatus,
                terminalStatus == "expired" ? "latest_start_window_missed" : "execution_start_commit_rejected", failedAt);
            return;
        }

        confirmation.RecordingId = receipt.RunId;
        if (!confirmation.TryDecide("approved"))
        {
            _repository.MarkPostCommitFailure(executionId, "execution_confirmation_decision_conflict", SafeNowOr(receipt.CommittedAtUtc),
                startOutcomeKnownFailed: true);
            return;
        }

        var ticket = new RequiredOnceCaptureExecutionTicket(
            receipt.Specification,
            receipt.ExecutionId,
            receipt.RunId,
            receipt.ExecutionApprovalId,
            receipt.ProofId,
            receipt.ProofNonce,
            receipt.CommittedAtUtc,
            confirmation);
        RequiredOnceCaptureExecutionResult startResult;
        try
        {
            startResult = _engineStartForTest is not null
                ? _engineStartForTest(ticket)
                : _engine.StartRequiredOnceCapture(ticket, _tray,
                    backend => new SqliteRequiredOnceCaptureExecutionSession(_store, ticket, backend, _utcNow));
        }
        catch
        {
            startResult = RequiredOnceCaptureExecutionResult.Failed("required_once_engine_start_failed");
        }
        if (startResult.Status != RequiredOnceCaptureExecutionStatus.Started)
            _repository.MarkPostCommitFailure(executionId,
                string.IsNullOrWhiteSpace(startResult.Reason) ? "required_once_engine_start_failed" : startResult.Reason,
                SafeNowOr(receipt.CommittedAtUtc), startOutcomeKnownFailed: true);
        SafeAudit("required_once_execution.dispatch", Event(specification,
            startResult.Status == RequiredOnceCaptureExecutionStatus.Started ? "started" : "failed",
            startResult.Reason, receipt.RunId));
    }

    private async Task<RequiredOnceExecutionApprovalResult> ShowApprovalWithTimeoutAsync(
        RequiredOnceExecutionApprovalDetails details,
        DateTimeOffset latestStartUtc,
        CancellationToken cancellationToken)
    {
        var secondsToLatest = Math.Max(1, (int)Math.Ceiling((latestStartUtc - ReadUtcNow()).TotalSeconds));
        var timeoutSeconds = Math.Min(60, secondsToLatest);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSeconds));
        using var registration = timeout.Token.Register(() =>
        {
            try { linked.Cancel(); } catch { }
        });
        Task<RequiredOnceExecutionApprovalResult>? approvalTask = null;
        try
        {
            approvalTask = _ui.ShowExecutionApprovalAsync(details, linked.Token);
            var result = await approvalTask.WaitAsync(linked.Token).ConfigureAwait(false);
            if (timeout.IsCancellationRequested && !_lifetime.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
                return RequiredOnceExecutionApprovalResult.TimedOut;
            if (_lifetime.IsCancellationRequested || cancellationToken.IsCancellationRequested)
                return RequiredOnceExecutionApprovalResult.HostShutdown;
            return result;
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested)
        {
            return RequiredOnceExecutionApprovalResult.TimedOut;
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested || cancellationToken.IsCancellationRequested)
        {
            return RequiredOnceExecutionApprovalResult.HostShutdown;
        }
        catch { return RequiredOnceExecutionApprovalResult.Unavailable; }
        finally
        {
            if (approvalTask is { IsCompleted: false })
                _ = approvalTask.ContinueWith(task => _ = task.Exception,
                    CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                    TaskScheduler.Default);
        }
    }

    private string? ValidateEnvironment(
        RequiredOnceCaptureExecutionSpecification specification,
        RequiredOncePrincipal expected,
        DateTimeOffset now)
    {
        if (now.Offset != TimeSpan.Zero) return "execution_clock_unavailable";
        if (now >= specification.LatestStartUtc) return "latest_start_window_missed";
        if (_engine.HasActiveRecording()) return "recording_conflict";
        if (!SafeUiAvailable()) return "interactive_desktop_unavailable";
        if (ReadPrincipal() != expected || expected.Sid != specification.CurrentUserSid ||
            expected.Session != specification.SessionBinding)
            return "execution_identity_changed";
        try
        {
            var displays = _currentDisplays();
            if (!StandingLeaseDisplayTopologyDigest.TryCompute(displays, out var topology) || topology != specification.TopologyDigest)
                return "execution_display_topology_changed";
            var matches = displays.Where(display => display.IdentityStatus == DisplayIdentityResolutionStatus.Resolved &&
                string.Equals(display.StableDisplayFingerprint, specification.StableDisplayFingerprint, StringComparison.Ordinal)).ToArray();
            if (matches.Length != 1) return "execution_display_identity_changed";
            var display = matches[0];
            if (display.PhysicalBounds != specification.DisplayBounds || display.DpiX != specification.DpiX ||
                display.DpiY != specification.DpiY || display.PhysicalWidth != specification.PhysicalWidth ||
                display.PhysicalHeight != specification.PhysicalHeight || display.Orientation != specification.Orientation)
                return "execution_display_geometry_changed";
            var output = _outputReadiness.Check(specification.OutputDirectory, specification.FrozenFileName, specification.Duration);
            if (!output.IsReady || output.Snapshot.FrozenFileExists || !output.Snapshot.DirectoryWritable ||
                !output.Snapshot.FreeSpaceAvailable)
                return string.IsNullOrWhiteSpace(output.ReasonCode)
                ? "execution_output_unavailable" : output.ReasonCode;
            if (!string.Equals(output.Snapshot.NormalizedOutputDirectory,
                    StandingLeaseOutputPath.NormalizeDirectory(specification.OutputDirectory), StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(output.Snapshot.FrozenOutputFilePath, specification.FrozenOutputFilePath, StringComparison.OrdinalIgnoreCase))
                return "execution_output_target_changed";
            return null;
        }
        catch { return "execution_environment_revalidation_failed"; }
    }

    private RequiredOnceExecutionApprovalDetails BuildApprovalDetails(RequiredOnceCaptureExecutionSpecification specification)
    {
        var displayName = "已解析的固定显示器 / Resolved fixed display";
        try
        {
            displayName = _currentDisplays().FirstOrDefault(display =>
                display.StableDisplayFingerprint == specification.StableDisplayFingerprint)?.PublicId ?? displayName;
        }
        catch { }
        return new(displayName, specification.StableDisplayFingerprint, specification.DisplayBounds,
            specification.RegionWithinDisplay, specification.ScheduledStartUtc, specification.LatestStartUtc,
            specification.Duration, specification.FrozenOutputFilePath);
    }

    private bool RecoverAtStartup()
    {
        try
        {
            for (var batch = 0; batch < MaximumRecoveryBatches; batch++)
            {
                var recovered = _repository.RecoverOutstanding(RecoveryBatchLimit, ReadUtcNow());
                if (!recovered.HasMore) return true;
            }
            SafeAudit("required_once_execution.recovery_incomplete", new { reason_code = "required_once_recovery_bound_exceeded" });
            return false;
        }
        catch (Exception exception)
        {
            SafeAudit("required_once_execution.recovery_failed", new
            {
                reason_code = "required_once_recovery_failed",
                exception_type = exception.GetType().Name,
            });
            return false;
        }
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            _ = await ProcessDueOnceAsync(cancellationToken).ConfigureAwait(false);
            try { await Task.Delay(_pollInterval, cancellationToken).ConfigureAwait(false); }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { break; }
        }
    }

    private IReadOnlyList<RequiredOnceExecutionCandidate> ListDueForCurrentIdentity()
    {
        var principal = ReadPrincipal();
        return principal is null ? Array.Empty<RequiredOnceExecutionCandidate>() :
            _repository.ListDue(principal.Sid, principal.Session, ReadUtcNow());
    }

    private RequiredOncePrincipal? ReadPrincipal()
    {
        try
        {
            var value = _principal();
            return value is not null && Canonical(value.Sid) && Canonical(value.Session) ? value : null;
        }
        catch { return null; }
    }

    private DateTimeOffset ReadUtcNow()
    {
        var value = _utcNow();
        if (value is null || value.Value.Offset != TimeSpan.Zero)
            throw new InvalidOperationException("required_once_execution_clock_invalid");
        return value.Value;
    }

    private DateTimeOffset SafeNowOr(DateTimeOffset fallback)
    {
        try { return ReadUtcNow(); } catch { return fallback; }
    }

    private bool SafeUiAvailable()
    {
        try { return _ui.IsInteractiveDesktopAvailable; } catch { return false; }
    }

    private void SafeAudit(string eventName, object payload)
    {
        try { _audit.Log(eventName, payload); } catch { }
    }

    private static object Event(
        RequiredOnceCaptureExecutionSpecification specification,
        string status,
        string reason,
        string? runId = null) => new
    {
        plan_id = specification.PlanId,
        occurrence_id = specification.OccurrenceId,
        run_id = runId,
        status,
        reason_code = reason,
    };

    private static bool Canonical(string value) =>
        !string.IsNullOrWhiteSpace(value) && value == value.Trim() && !value.Any(char.IsControl);

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

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        Volatile.Write(ref _healthy, 0);
        try { _lifetime.Cancel(); } catch { }
        var loop = _loop;
        if (loop is not null)
        {
            try { loop.Wait(TimeSpan.FromSeconds(3)); } catch { }
        }
        if (loop is null || loop.IsCompleted)
        {
            _pollGate.Dispose();
            _lifetime.Dispose();
        }
    }
}
