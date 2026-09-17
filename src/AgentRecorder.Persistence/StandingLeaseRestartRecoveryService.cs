using AgentRecorder.Infrastructure;

namespace AgentRecorder.Persistence;

/// <summary>
/// Internal restart boundary for a standing execution that has already crossed
/// the durable start gate. Recovery only reconciles persisted state; it has no
/// capture backend, factory, proof, or configuration input.
/// </summary>
internal sealed class StandingLeaseRestartRecoveryService
{
    private readonly SqliteStandingLeaseRecoveryTransaction _transaction;
    private readonly Func<DateTimeOffset?> _utcNow;

    internal StandingLeaseRestartRecoveryService(SqliteOperationalStore store)
        : this(store, utcNowForTest: null, beforeCommitForTest: null)
    {
    }

    internal StandingLeaseRestartRecoveryService(
        SqliteOperationalStore store,
        Func<DateTimeOffset?>? utcNowForTest,
        Action<Microsoft.Data.Sqlite.SqliteConnection, Microsoft.Data.Sqlite.SqliteTransaction>? beforeCommitForTest = null)
    {
        ArgumentNullException.ThrowIfNull(store);
        _utcNow = utcNowForTest ?? (() => DateTimeOffset.UtcNow);
        _transaction = new SqliteStandingLeaseRecoveryTransaction(store, beforeCommitForTest);
    }

    internal StandingLeaseRecoveryResult Recover(StandingLeaseRecoveryRequest? request)
    {
        if (!IsValidRequest(request))
        {
            return StandingLeaseRecoveryResult.Rejected("recovery_request_invalid");
        }

        if (!TryReadUtcNow(out var nowUtc, out var failureReason))
        {
            return StandingLeaseRecoveryResult.Rejected(failureReason);
        }

        try
        {
            return _transaction.Reconcile(request!, nowUtc);
        }
        catch (Phase3PersistenceException exception)
        {
            return StandingLeaseRecoveryResult.Rejected(NormalizeFailure(exception.Code));
        }
        catch (Exception)
        {
            return StandingLeaseRecoveryResult.Rejected("recovery_sqlite_failure");
        }
    }

    internal StandingLeaseRecoveryResult Reconcile(StandingLeaseRecoveryRequest? request) => Recover(request);

    private bool TryReadUtcNow(out DateTimeOffset nowUtc, out string failureReason)
    {
        nowUtc = default;
        failureReason = "recovery_clock_unavailable";

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
        {
            return false;
        }

        nowUtc = sampled.Value;
        if (nowUtc.Offset != TimeSpan.Zero)
        {
            failureReason = "recovery_time_not_utc";
            return false;
        }

        return true;
    }

    private static string NormalizeFailure(string code) => code switch
    {
        "not_found" or "persisted_snapshot_invalid" or "invalid_version" or "immutable_mismatch" => "recovery_snapshot_invalid",
        "concurrency_conflict" => "recovery_concurrency_conflict",
        "sqlite_failure" or "sqlite_corrupt" or "sqlite_not_initialized" or "sqlite_migration_checksum_mismatch" => "recovery_sqlite_failure",
        _ => code,
    };

    private static bool IsValidRequest(StandingLeaseRecoveryRequest? request) =>
        request is not null &&
        IsCanonicalId(request.PlanId) &&
        IsCanonicalId(request.OccurrenceId) &&
        IsCanonicalId(request.LeaseId) &&
        IsCanonicalId(request.RunId) &&
        IsCanonicalId(request.LeaseUseId) &&
        IsCanonicalId(request.ScopeId) &&
        IsCanonicalSha256(request.ScopeDigest);

    private static bool IsCanonicalId(string? value) =>
        !string.IsNullOrWhiteSpace(value) &&
        string.Equals(value, value.Trim(), StringComparison.Ordinal);

    private static bool IsCanonicalSha256(string? value) =>
        IsCanonicalId(value) &&
        value!.Length == 64 &&
        value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');
}

/// <summary>
/// Durable identity only. No time, duration, target, output, environment,
/// proof, backend, or native handle can enter restart recovery through this
/// request.
/// </summary>
internal sealed record StandingLeaseRecoveryRequest(
    string PlanId,
    string OccurrenceId,
    string LeaseId,
    string RunId,
    string LeaseUseId,
    string ScopeId,
    string ScopeDigest);

internal enum StandingLeaseRecoveryStatus
{
    Recovered,
    AlreadyReconciled,
    Rejected,
}

internal sealed class StandingLeaseRecoveryResult
{
    private StandingLeaseRecoveryResult(
        StandingLeaseRecoveryStatus status,
        string reason,
        string? planId,
        string? occurrenceId,
        string? leaseId,
        string? runId,
        string? leaseUseId,
        bool changed)
    {
        Status = status;
        Reason = reason;
        PlanId = planId;
        OccurrenceId = occurrenceId;
        LeaseId = leaseId;
        RunId = runId;
        LeaseUseId = leaseUseId;
        Changed = changed;
    }

    internal StandingLeaseRecoveryStatus Status { get; }

    internal string Reason { get; }

    internal string? PlanId { get; }

    internal string? OccurrenceId { get; }

    internal string? LeaseId { get; }

    internal string? RunId { get; }

    internal string? LeaseUseId { get; }

    internal bool Changed { get; }

    internal static StandingLeaseRecoveryResult Recovered(
        StandingLeaseRecoveryRequest request,
        string reason) =>
        new(
            StandingLeaseRecoveryStatus.Recovered,
            reason,
            request.PlanId,
            request.OccurrenceId,
            request.LeaseId,
            request.RunId,
            request.LeaseUseId,
            changed: true);

    internal static StandingLeaseRecoveryResult AlreadyReconciled(
        StandingLeaseRecoveryRequest request,
        string reason = "recovery_idempotent_noop") =>
        new(
            StandingLeaseRecoveryStatus.AlreadyReconciled,
            reason,
            request.PlanId,
            request.OccurrenceId,
            request.LeaseId,
            request.RunId,
            request.LeaseUseId,
            changed: false);

    internal static StandingLeaseRecoveryResult Rejected(string reason) =>
        new(StandingLeaseRecoveryStatus.Rejected, reason, null, null, null, null, null, changed: false);
}
