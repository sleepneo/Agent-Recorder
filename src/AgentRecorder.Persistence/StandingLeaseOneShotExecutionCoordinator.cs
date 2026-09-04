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
    private readonly SqliteAuthorizedCaptureScopeRepository _scopeRepository;
    private readonly SqlitePhase3StartGateTransaction _startGate;
    private readonly SqliteStandingLeaseExecutionSnapshotLoader _snapshotLoader;
    private readonly StandingLeaseCaptureExecutionBridge _executionBridge;
    private readonly Func<DateTimeOffset?> _utcNow;

    internal StandingLeaseOneShotExecutionCoordinator(SqliteOperationalStore store)
        : this(
            store,
            environmentProviderForTest: null,
            backendFactoryForTest: null,
            delayForTest: null,
            utcNowForTest: null)
    {
    }

    // The optional seams are internal and deterministic-test-only. Production
    // uses the real UTC clock, system environment provider, and the bridge's
    // approved FFmpeg-region factory.
    internal StandingLeaseOneShotExecutionCoordinator(
        SqliteOperationalStore store,
        IStandingLeaseExecutionEnvironmentProvider? environmentProviderForTest,
        Func<StandingLeaseCaptureSpecification, ICaptureBackend?>? backendFactoryForTest,
        Func<TimeSpan, CancellationToken, Task>? delayForTest = null,
        Func<DateTimeOffset?>? utcNowForTest = null)
    {
        ArgumentNullException.ThrowIfNull(store);

        _scopeRepository = new SqliteAuthorizedCaptureScopeRepository(store);
        _startGate = new SqlitePhase3StartGateTransaction(store);
        _snapshotLoader = new SqliteStandingLeaseExecutionSnapshotLoader(
            store,
            snapshotLoadedBeforeCoreGateForTest: null,
            beforeCommitForTest: null,
            environmentProviderForTest);
        _utcNow = utcNowForTest ?? (() => DateTimeOffset.UtcNow);
        _executionBridge = new StandingLeaseCaptureExecutionBridge(
            environmentProviderForTest,
            backendFactoryForTest,
            delayForTest,
            _utcNow);
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
            !string.Equals(scope.LeaseId, validRequest.LeaseId, StringComparison.Ordinal))
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
                authorizationFailure,
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
                specificationFailure,
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
                ticketFailure,
                commitResult.RunId,
                commitResult.LeaseUseId);
        }

        var bridgeResult = await _executionBridge
            .ExecuteAsync(ticket, cancellationToken)
            .ConfigureAwait(false);
        if (bridgeResult.Status == StandingLeaseCaptureExecutionStatus.Started &&
            bridgeResult.Backend is not null)
        {
            return StandingLeaseOneShotExecutionResult.Started(
                bridgeResult.Backend,
                commitResult.RunId,
                commitResult.LeaseUseId);
        }

        return StandingLeaseOneShotExecutionResult.CommittedNotStarted(
            bridgeResult.Reason,
            commitResult.RunId,
            commitResult.LeaseUseId);
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
            IsCanonicalId(request.LeaseUseId);
    }

    private static bool IsCanonicalId(string? value) =>
        !string.IsNullOrWhiteSpace(value) &&
        string.Equals(value, value.Trim(), StringComparison.Ordinal);
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
    long ExpectedLeaseVersion);

internal enum StandingLeaseOneShotExecutionStatus
{
    Started,
    AlreadyCommitted,
    CommittedNotStarted,
    Rejected,
}

/// <summary>
/// Internal coordinator result. It contains only stable status/reason/IDs and
/// an internal backend handle for a later lifecycle bridge; no proof, ticket,
/// scope, environment snapshot, config, nonce, or native handle is public.
/// </summary>
internal sealed class StandingLeaseOneShotExecutionResult
{
    private StandingLeaseOneShotExecutionResult(
        StandingLeaseOneShotExecutionStatus status,
        string reason,
        string? runId,
        string? leaseUseId,
        ICaptureBackend? backend)
    {
        Status = status;
        Reason = reason;
        RunId = runId;
        LeaseUseId = leaseUseId;
        Backend = backend;
    }

    internal StandingLeaseOneShotExecutionStatus Status { get; }

    internal string Reason { get; }

    internal string? RunId { get; }

    internal string? LeaseUseId { get; }

    internal ICaptureBackend? Backend { get; }

    internal static StandingLeaseOneShotExecutionResult Started(
        ICaptureBackend backend,
        string runId,
        string leaseUseId) =>
        new(StandingLeaseOneShotExecutionStatus.Started, "", runId, leaseUseId, backend);

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
