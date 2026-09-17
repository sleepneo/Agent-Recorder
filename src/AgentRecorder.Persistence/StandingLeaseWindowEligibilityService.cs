using AgentRecorder.Core;
using AgentRecorder.Core.Automation;
using AgentRecorder.Infrastructure;
using Microsoft.Data.Sqlite;

namespace AgentRecorder.Persistence;

/// <summary>
/// Internal pre-dispatch window boundary. It evaluates durable standing
/// authorization only; it does not call the one-shot coordinator or capture.
/// </summary>
internal sealed class StandingLeaseWindowEligibilityService
{
    private readonly SqliteStandingLeaseWindowEligibilityTransaction _transaction;
    private readonly Func<DateTimeOffset?> _utcNow;
    private readonly object _clockSync = new();
    private DateTimeOffset? _lastUtcNow;

    internal StandingLeaseWindowEligibilityService(SqliteOperationalStore store)
        : this(store, utcNowForTest: null, beforeCommitForTest: null)
    {
    }

    internal StandingLeaseWindowEligibilityService(
        SqliteOperationalStore store,
        Func<DateTimeOffset?>? utcNowForTest,
        Action<SqliteConnection, SqliteTransaction>? beforeCommitForTest = null)
    {
        ArgumentNullException.ThrowIfNull(store);
        _utcNow = utcNowForTest ?? (() => DateTimeOffset.UtcNow);
        _transaction = new SqliteStandingLeaseWindowEligibilityTransaction(store, beforeCommitForTest);
    }

    internal StandingLeaseWindowEligibilityResult Evaluate(StandingLeaseWindowEligibilityRequest? request)
    {
        if (!IsValidRequest(request))
        {
            return StandingLeaseWindowEligibilityResult.Rejected("eligibility_request_invalid");
        }

        if (!TryReadTrustedUtcNow(out var nowUtc, out var failureReason))
        {
            return StandingLeaseWindowEligibilityResult.Rejected(request!, failureReason);
        }

        try
        {
            return _transaction.Evaluate(request!, nowUtc);
        }
        catch (Phase3PersistenceException exception)
        {
            return StandingLeaseWindowEligibilityResult.Rejected(
                request!,
                NormalizeFailure(exception.Code));
        }
        catch (Exception)
        {
            return StandingLeaseWindowEligibilityResult.Rejected(request!, "eligibility_sqlite_failure");
        }
    }

    internal StandingLeaseWindowEligibilityResult EvaluateForPreparedIntent(
        string? intentId,
        StandingLeaseWindowEligibilityRequest? request)
    {
        if (!IsCanonicalId(intentId))
        {
            return StandingLeaseWindowEligibilityResult.Rejected("eligibility_intent_request_invalid");
        }

        if (!IsValidRequest(request))
        {
            return StandingLeaseWindowEligibilityResult.Rejected("eligibility_request_invalid");
        }

        if (!TryReadTrustedUtcNow(out var nowUtc, out var failureReason))
        {
            return StandingLeaseWindowEligibilityResult.Rejected(request!, failureReason);
        }

        try
        {
            return _transaction.Evaluate(request!, nowUtc, intentId);
        }
        catch (Phase3PersistenceException exception)
        {
            return StandingLeaseWindowEligibilityResult.Rejected(
                request!,
                NormalizeFailure(exception.Code));
        }
        catch (Exception)
        {
            return StandingLeaseWindowEligibilityResult.Rejected(request!, "eligibility_sqlite_failure");
        }
    }

    internal StandingLeaseWindowEligibilityResult Check(StandingLeaseWindowEligibilityRequest? request) => Evaluate(request);

    /// <summary>
    /// Revalidates a start-gate concurrency result without attempting another
    /// start. This is deliberately a separate read-only decision from the
    /// eligibility mutation path: only durable evidence that proves revocation
    /// or an existing claim may replace the original concurrency result.
    /// </summary>
    internal StandingLeaseNaturalWakeConcurrencyRevalidationResult RevalidateAfterConcurrency(
        StandingLeaseOneShotExecutionRequest? request)
    {
        if (!IsValidConcurrencyRevalidationRequest(request))
        {
            return StandingLeaseNaturalWakeConcurrencyRevalidationResult.Rejected(
                "natural_wake_concurrency_revalidation_request_invalid");
        }

        try
        {
            return _transaction.RevalidateAfterConcurrency(request!);
        }
        catch (Phase3PersistenceException exception)
        {
            return StandingLeaseNaturalWakeConcurrencyRevalidationResult.Rejected(
                NormalizeRevalidationFailure(exception.Code));
        }
        catch (PersistedSnapshotException)
        {
            return StandingLeaseNaturalWakeConcurrencyRevalidationResult.Rejected(
                "natural_wake_concurrency_revalidation_snapshot_invalid");
        }
        catch (Phase3DomainException)
        {
            return StandingLeaseNaturalWakeConcurrencyRevalidationResult.Rejected(
                "natural_wake_concurrency_revalidation_snapshot_invalid");
        }
        catch (Exception)
        {
            return StandingLeaseNaturalWakeConcurrencyRevalidationResult.Rejected(
                "natural_wake_concurrency_revalidation_sqlite_failure");
        }
    }

    private bool TryReadTrustedUtcNow(out DateTimeOffset nowUtc, out string failureReason)
    {
        nowUtc = default;
        failureReason = "eligibility_clock_unavailable";

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
            failureReason = "eligibility_time_not_utc";
            return false;
        }

        lock (_clockSync)
        {
            if (_lastUtcNow is not null && nowUtc < _lastUtcNow.Value)
            {
                failureReason = "eligibility_time_non_monotonic";
                return false;
            }

            _lastUtcNow = nowUtc;
        }

        return true;
    }

    private static string NormalizeFailure(string code) => code switch
    {
        "not_found" or "persisted_snapshot_invalid" or "invalid_version" or "immutable_mismatch" => "eligibility_snapshot_invalid",
        "concurrency_conflict" => "eligibility_concurrency_conflict",
        "sqlite_failure" or "sqlite_corrupt" or "sqlite_not_initialized" or "sqlite_migration_checksum_mismatch" => "eligibility_sqlite_failure",
        _ => code,
    };

    private static string NormalizeRevalidationFailure(string code) => code switch
    {
        "not_found" or "persisted_snapshot_invalid" or "invalid_version" or "immutable_mismatch" or
            "eligibility_scope_identity_mismatch" => "natural_wake_concurrency_revalidation_snapshot_invalid",
        "sqlite_failure" or "sqlite_corrupt" or "sqlite_not_initialized" or "sqlite_migration_checksum_mismatch" =>
            "natural_wake_concurrency_revalidation_sqlite_failure",
        _ => "natural_wake_concurrency_revalidation_failed",
    };

    private static bool IsValidConcurrencyRevalidationRequest(
        StandingLeaseOneShotExecutionRequest? request) =>
        request is not null &&
        IsCanonicalId(request.PlanId) &&
        IsCanonicalId(request.OccurrenceId) &&
        IsCanonicalId(request.LeaseId) &&
        IsCanonicalId(request.RunId) &&
        IsCanonicalId(request.LeaseUseId) &&
        IsCanonicalId(request.ScopeId) &&
        IsCanonicalSha256(request.ScopeDigest);

    private static bool IsValidRequest(StandingLeaseWindowEligibilityRequest? request) =>
        request is not null &&
        IsCanonicalId(request.PlanId) &&
        IsCanonicalId(request.OccurrenceId) &&
        IsCanonicalId(request.LeaseId) &&
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
/// Durable identity only. Current time is sampled inside the service.
/// </summary>
internal sealed record StandingLeaseWindowEligibilityRequest(
    string PlanId,
    string OccurrenceId,
    string LeaseId,
    string ScopeId,
    string ScopeDigest);

internal sealed class StandingLeaseWindowEligibilityResult
{
    private StandingLeaseWindowEligibilityResult(
        StandingLeaseWindowEligibilityStatus status,
        string reason,
        StandingLeaseWindowEligibilityRequest? request,
        bool changed,
        StandingLeaseWindowEligibilityHandoff? handoff = null)
    {
        Status = status;
        Reason = reason;
        PlanId = request?.PlanId;
        OccurrenceId = request?.OccurrenceId;
        LeaseId = request?.LeaseId;
        ScopeId = request?.ScopeId;
        ScopeDigest = request?.ScopeDigest;
        Changed = changed;
        Handoff = handoff;
    }

    internal StandingLeaseWindowEligibilityStatus Status { get; }

    internal string Reason { get; }

    internal string? PlanId { get; }

    internal string? OccurrenceId { get; }

    internal string? LeaseId { get; }

    internal string? ScopeId { get; }

    internal string? ScopeDigest { get; }

    internal bool Changed { get; }

    /// <summary>
    /// Internal, read-only dispatch handoff for an Eligible decision. It is
    /// not an authorization proof or a start authorization; the coordinator's
    /// start gate must revalidate the versions and the complete chain.
    /// </summary>
    internal StandingLeaseWindowEligibilityHandoff? Handoff { get; }

    internal static StandingLeaseWindowEligibilityResult FromDecision(
        StandingLeaseWindowEligibilityRequest request,
        AgentRecorder.Core.StandingLeaseWindowEligibilityDecision decision,
        StandingLeaseWindowEligibilityHandoff? handoff = null) =>
        new(decision.Status, decision.Reason, request, changed: false, handoff);

    internal static StandingLeaseWindowEligibilityResult Expired(
        StandingLeaseWindowEligibilityRequest request,
        string reason) =>
        new(StandingLeaseWindowEligibilityStatus.Expired, reason, request, changed: true);

    internal static StandingLeaseWindowEligibilityResult AlreadyClaimed(
        StandingLeaseWindowEligibilityRequest request) =>
        new(StandingLeaseWindowEligibilityStatus.AlreadyClaimed, "occurrence_already_claimed", request, changed: false);

    internal static StandingLeaseWindowEligibilityResult Rejected(string reason) =>
        new(StandingLeaseWindowEligibilityStatus.Rejected, reason, null, changed: false);

    internal static StandingLeaseWindowEligibilityResult Rejected(
        StandingLeaseWindowEligibilityRequest request,
        string reason) =>
        new(StandingLeaseWindowEligibilityStatus.Rejected, reason, request, changed: false);
}

/// <summary>
/// Process-local handoff from the one-transaction eligibility decision to the
/// existing one-shot start gate. It contains only durable identity and the
/// expected aggregate versions observed in that decision.
/// </summary>
internal sealed record StandingLeaseWindowEligibilityHandoff(
    string PlanId,
    string OccurrenceId,
    string LeaseId,
    string ScopeId,
    string ScopeDigest,
    long ExpectedPlanVersion,
    long ExpectedOccurrenceVersion,
    long ExpectedLeaseVersion);
