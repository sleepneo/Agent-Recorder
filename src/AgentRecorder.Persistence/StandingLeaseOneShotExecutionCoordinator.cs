using AgentRecorder.Capture;
using AgentRecorder.Core;
using AgentRecorder.Core.Automation;
using AgentRecorder.Infrastructure;

namespace AgentRecorder.Persistence;

/// <summary>
/// Internal one-shot orchestration for the first standing execution slice.
/// The coordinator owns ordering only: the existing start gate, atomic
/// snapshot loader, Core specification/ticket factories, and execution bridge
/// remain the authorities for their respective invariants.
/// </summary>
internal sealed class StandingLeaseOneShotExecutionCoordinator
{
    private readonly SqliteOperationalStore _store;
    private readonly SqliteAuthorizedCaptureScopeRepository _scopeRepository;
    private readonly SqlitePhase3StartGateTransaction _startGate;
    private readonly SqliteStandingLeaseExecutionSnapshotLoader _snapshotLoader;
    private readonly StandingLeaseRestartRecoveryService _recovery;
    private readonly StandingLeaseCaptureExecutionBridge _executionBridge;
    private readonly Func<DateTimeOffset?> _utcNow;
    private DateTimeOffset? _lastTrustedUtcNow;

    internal StandingLeaseOneShotExecutionCoordinator(SqliteOperationalStore store)
        : this(
            store,
            environmentProviderForTest: null,
            backendFactoryForTest: null,
            delayForTest: null,
            utcNowForTest: null,
            executionStarterForProduction: null)
    {
    }

    // The optional seams are internal and deterministic-test-only. Production
    // supplies the RecordingEngine starter from the App composition root;
    // without that host the bridge fails closed before backend construction.
    internal StandingLeaseOneShotExecutionCoordinator(
        SqliteOperationalStore store,
        IStandingLeaseExecutionEnvironmentProvider? environmentProviderForTest,
        Func<StandingLeaseCaptureSpecification, ICaptureBackend?>? backendFactoryForTest,
        Func<TimeSpan, CancellationToken, Task>? delayForTest = null,
        Func<DateTimeOffset?>? utcNowForTest = null,
        Func<StandingLeaseCaptureExecutionTicket, CancellationToken, Task<StandingLeaseCaptureExecutionResult>>? executionStarterForProduction = null)
    {
        ArgumentNullException.ThrowIfNull(store);

        _store = store;
        _scopeRepository = new SqliteAuthorizedCaptureScopeRepository(store);
        _startGate = new SqlitePhase3StartGateTransaction(store);
        _snapshotLoader = new SqliteStandingLeaseExecutionSnapshotLoader(
            store,
            snapshotLoadedBeforeCoreGateForTest: null,
            beforeCommitForTest: null,
            environmentProviderForTest);
        _utcNow = utcNowForTest ?? (() => DateTimeOffset.UtcNow);
        _recovery = new StandingLeaseRestartRecoveryService(
            store,
            () => _lastTrustedUtcNow ?? DateTimeOffset.UtcNow);
        _executionBridge = new StandingLeaseCaptureExecutionBridge(
            environmentProviderForTest,
            backendFactoryForTest,
            delayForTest,
            _utcNow,
            (specification, backend) => new StandingLeaseOneShotExecutionSession(
                _store,
                specification,
                backend,
                _utcNow),
            executionStarterForProduction);
    }

    internal async Task<StandingLeaseOneShotExecutionResult> ExecuteAsync(
        StandingLeaseOneShotExecutionRequest? request,
        CancellationToken cancellationToken = default)
    {
        if (!IsValidRequest(request))
            return StandingLeaseOneShotExecutionResult.Rejected("standing_execution_request_invalid");

        var validRequest = request!;

        if (!TryReadUtcNow(out var commitAtUtc, out var clockFailure))
            return StandingLeaseOneShotExecutionResult.Rejected(clockFailure);

        AuthorizedFixedRegionScope scope;
        try
        {
            // This read supplies only the already-authorized scope values that
            // the existing start gate requires. The start gate re-reads and
            // revalidates the scope in its own immediate transaction.
            scope = _scopeRepository.GetByOccurrence(validRequest.OccurrenceId);
        }
        catch (Phase3PersistenceException exception)
        {
            return StandingLeaseOneShotExecutionResult.Rejected(exception.Code);
        }
        catch (Exception)
        {
            return StandingLeaseOneShotExecutionResult.Rejected("sqlite_failure");
        }

        if (!string.Equals(scope.PlanId, validRequest.PlanId, StringComparison.Ordinal) ||
            !string.Equals(scope.OccurrenceId, validRequest.OccurrenceId, StringComparison.Ordinal) ||
            !string.Equals(scope.LeaseId, validRequest.LeaseId, StringComparison.Ordinal) ||
            (validRequest.ScopeId is not null && !string.Equals(scope.ScopeId, validRequest.ScopeId, StringComparison.Ordinal)) ||
            (validRequest.ScopeDigest is not null && !string.Equals(scope.ScopeDigest, validRequest.ScopeDigest, StringComparison.Ordinal)))
        {
            return StandingLeaseOneShotExecutionResult.Rejected("start_gate_conflict");
        }

        Phase3StartGateCommitResult commitResult;
        try
        {
            // CommitAtUtc, scope digest, and exact authorized duration are
            // coordinator-derived. Caller input contributes only stable
            // aggregate identities and expected versions.
            commitResult = _startGate.Commit(
                new Phase3StartGateRequest(
                    scope.PlanId,
                    scope.OccurrenceId,
                    scope.LeaseId,
                    scope.ScopeId,
                    scope.ScopeDigest,
                    validRequest.RunId,
                    validRequest.LeaseUseId,
                    validRequest.ExpectedPlanVersion,
                    validRequest.ExpectedOccurrenceVersion,
                    validRequest.ExpectedLeaseVersion,
                    scope.ReservedDuration,
                    commitAtUtc));
        }
        catch (Phase3PersistenceException exception)
        {
            return StandingLeaseOneShotExecutionResult.Rejected(exception.Code);
        }
        catch (Exception)
        {
            return StandingLeaseOneShotExecutionResult.Rejected("sqlite_failure");
        }

        if (commitResult.Status == Phase3StartGateCommitStatus.AlreadyCommitted)
        {
            return StandingLeaseOneShotExecutionResult.AlreadyCommitted(
                commitResult.RunId,
                commitResult.LeaseUseId);
        }

        if (commitResult.Status != Phase3StartGateCommitStatus.Committed ||
            commitResult.FirstCommitProof is null)
        {
            return StandingLeaseOneShotExecutionResult.CommittedNotStarted(
                "standing_execution_first_commit_proof_missing",
                commitResult.RunId,
                commitResult.LeaseUseId);
        }

        // No authorization object, specification, ticket, or backend is
        // created before the durable start-gate transaction has committed.
        if (!_snapshotLoader.TryAuthorizeAndConsumeStandingLeaseUse(
                commitResult.FirstCommitProof,
                out var authorization,
                out var authorizationFailure) ||
            authorization is null)
        {
            return StandingLeaseOneShotExecutionResult.CommittedNotStarted(
                RecoverDurableHandoff(
                    new StandingLeaseRecoveryRequest(
                        scope.PlanId,
                        scope.OccurrenceId,
                        scope.LeaseId,
                        commitResult.RunId,
                        commitResult.LeaseUseId,
                        scope.ScopeId,
                        scope.ScopeDigest),
                    authorizationFailure),
                commitResult.RunId,
                commitResult.LeaseUseId);
        }

        if (!StandingLeaseCaptureSpecification.TryCreate(
                authorization,
                out var specification,
                out var specificationFailure) ||
            specification is null)
        {
            return StandingLeaseOneShotExecutionResult.CommittedNotStarted(
                RecoverDurableHandoff(
                    CreateRecoveryRequest(scope, commitResult.RunId, commitResult.LeaseUseId),
                    specificationFailure),
                commitResult.RunId,
                commitResult.LeaseUseId);
        }

        if (!StandingLeaseCaptureExecutionTicket.TryCreate(
                authorization,
                specification,
                out var ticket,
                out var ticketFailure) ||
            ticket is null)
        {
            return StandingLeaseOneShotExecutionResult.CommittedNotStarted(
                RecoverDurableHandoff(
                    CreateRecoveryRequest(scope, commitResult.RunId, commitResult.LeaseUseId),
                    ticketFailure),
                commitResult.RunId,
                commitResult.LeaseUseId);
        }

        var bridgeResult = await _executionBridge
            .ExecuteAsync(ticket, cancellationToken)
            .ConfigureAwait(false);
        if (bridgeResult.Status == StandingLeaseCaptureExecutionStatus.Started &&
            bridgeResult.LifecycleSession is not null)
        {
            return StandingLeaseOneShotExecutionResult.Started(
                commitResult.RunId,
                commitResult.LeaseUseId,
                bridgeResult.LifecycleSession);
        }

        return StandingLeaseOneShotExecutionResult.CommittedNotStarted(
            RecoverDurableHandoff(
                CreateRecoveryRequest(specification),
                bridgeResult.Reason),
            commitResult.RunId,
            commitResult.LeaseUseId);
    }

    private static StandingLeaseRecoveryRequest CreateRecoveryRequest(
        AuthorizedFixedRegionScope scope,
        string runId,
        string leaseUseId) =>
        new(
            scope.PlanId,
            scope.OccurrenceId,
            scope.LeaseId,
            runId,
            leaseUseId,
            scope.ScopeId,
            scope.ScopeDigest);

    private static StandingLeaseRecoveryRequest CreateRecoveryRequest(
        StandingLeaseCaptureSpecification specification) =>
        new(
            specification.PlanId,
            specification.OccurrenceId,
            specification.LeaseId,
            specification.RunId,
            specification.LeaseUseId,
            specification.ScopeId,
            specification.ScopeDigest);

    private string RecoverDurableHandoff(
        StandingLeaseRecoveryRequest request,
        string reason)
    {
        try
        {
            var recovery = _recovery.Recover(request);
            if (recovery.Status == StandingLeaseRecoveryStatus.Rejected)
                return reason + ":recovery_failed:" + recovery.Reason;
        }
        catch
        {
            return reason + ":recovery_failed";
        }

        return reason;
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

        _lastTrustedUtcNow = nowUtc;
        failureReason = "";
        return true;
    }

    private static bool IsValidRequest(StandingLeaseOneShotExecutionRequest? request)
    {
        if (request is null ||
            request.ExpectedPlanVersion < 0 ||
            request.ExpectedOccurrenceVersion < 0 ||
            request.ExpectedLeaseVersion < 0 ||
            request.ExpectedPlanVersion == long.MaxValue ||
            request.ExpectedOccurrenceVersion == long.MaxValue ||
            request.ExpectedLeaseVersion == long.MaxValue)
        {
            return false;
        }

        return IsCanonicalId(request.PlanId) &&
            IsCanonicalId(request.OccurrenceId) &&
            IsCanonicalId(request.LeaseId) &&
            IsCanonicalId(request.RunId) &&
            IsCanonicalId(request.LeaseUseId) &&
            (request.ScopeId is null || IsCanonicalId(request.ScopeId)) &&
            (request.ScopeDigest is null || IsCanonicalSha256(request.ScopeDigest));
    }

    private static bool IsCanonicalId(string? value) =>
        !string.IsNullOrWhiteSpace(value) &&
        string.Equals(value, value.Trim(), StringComparison.Ordinal);

    private static bool IsCanonicalSha256(string? value) =>
        !string.IsNullOrWhiteSpace(value) &&
        string.Equals(value, value.Trim(), StringComparison.Ordinal) &&
        value.Length == 64 &&
        value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');
}

/// <summary>
/// Internal request boundary. It deliberately has no caller-controlled time,
/// duration, output, target, display, backend, audio, window, WGC, series,
/// nested, or CaptureConfig field.
/// </summary>
internal sealed record StandingLeaseOneShotExecutionRequest(
    string PlanId,
    string OccurrenceId,
    string LeaseId,
    string RunId,
    string LeaseUseId,
    long ExpectedPlanVersion,
    long ExpectedOccurrenceVersion,
    long ExpectedLeaseVersion,
    string? ScopeId = null,
    string? ScopeDigest = null);

internal enum StandingLeaseOneShotExecutionStatus
{
    Started,
    AlreadyCommitted,
    CommittedNotStarted,
    Rejected,
}

/// <summary>
/// Internal coordinator result. It contains only stable status/reason/IDs and
/// the lifecycle session as the sole backend ownership boundary; no proof,
/// ticket, scope, environment snapshot, config, nonce, or native handle is
/// exposed.
/// </summary>
internal sealed class StandingLeaseOneShotExecutionResult
{
    private StandingLeaseOneShotExecutionResult(
        StandingLeaseOneShotExecutionStatus status,
        string reason,
        string? runId,
        string? leaseUseId,
        IStandingLeaseCaptureLifecycleSession? lifecycleSession)
    {
        Status = status;
        Reason = reason;
        RunId = runId;
        LeaseUseId = leaseUseId;
        LifecycleSession = lifecycleSession;
    }

    internal StandingLeaseOneShotExecutionStatus Status { get; }

    internal string Reason { get; }

    internal string? RunId { get; }

    internal string? LeaseUseId { get; }

    internal IStandingLeaseCaptureLifecycleSession? LifecycleSession { get; }

    internal static StandingLeaseOneShotExecutionResult Started(
        string runId,
        string leaseUseId,
        IStandingLeaseCaptureLifecycleSession? lifecycleSession = null) =>
        new(StandingLeaseOneShotExecutionStatus.Started, "", runId, leaseUseId, lifecycleSession);

    internal static StandingLeaseOneShotExecutionResult AlreadyCommitted(
        string runId,
        string leaseUseId) =>
        new(StandingLeaseOneShotExecutionStatus.AlreadyCommitted, "already_committed", runId, leaseUseId, null);

    internal static StandingLeaseOneShotExecutionResult CommittedNotStarted(
        string reason,
        string runId,
        string leaseUseId) =>
        new(StandingLeaseOneShotExecutionStatus.CommittedNotStarted, reason, runId, leaseUseId, null);

    internal static StandingLeaseOneShotExecutionResult Rejected(string reason) =>
        new(StandingLeaseOneShotExecutionStatus.Rejected, reason, null, null, null);
}
