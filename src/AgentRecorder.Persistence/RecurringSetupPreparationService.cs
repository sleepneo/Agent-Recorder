using AgentRecorder.Core.Automation;
using AgentRecorder.Infrastructure;
using Microsoft.Data.Sqlite;

namespace AgentRecorder.Persistence;

internal enum RecurringSetupPreparationResultStatus
{
    Prepared,
    Existing,
    Conflict,
    Rejected,
    Expired,
}

internal interface IRecurringSetupPreparationIdProvider
{
    string CreateProfileId();
    string CreatePlanId();
    string CreateLeaseId();
}

internal sealed class GuidRecurringSetupPreparationIdProvider : IRecurringSetupPreparationIdProvider
{
    public string CreateProfileId() => "recurring-profile-" + Guid.NewGuid().ToString("N");
    public string CreatePlanId() => "recurring-plan-" + Guid.NewGuid().ToString("N");
    public string CreateLeaseId() => "recurring-lease-" + Guid.NewGuid().ToString("N");
}

internal sealed class RecurringSetupPreparationService
{
    private readonly SqliteRecurringSetupPreparationTransaction _transaction;
    private readonly Func<DateTimeOffset?> _utcNow;
    private readonly object _clockSync = new();
    private DateTimeOffset? _lastUtcNow;

    internal RecurringSetupPreparationService(SqliteOperationalStore store)
        : this(store, null, null, null, null, null)
    {
    }

    internal RecurringSetupPreparationService(
        SqliteOperationalStore store,
        Func<DateTimeOffset?>? utcNowForTest,
        IRecurringSetupPreparationIdProvider? idProviderForTest = null,
        Action<SqliteConnection, SqliteTransaction>? beforeWritesForTest = null,
        Action<SqliteConnection, SqliteTransaction>? beforeCommitForTest = null,
        Action<RecurringSetupPreparationFailurePoint>? failureHookForTest = null)
    {
        ArgumentNullException.ThrowIfNull(store);
        _utcNow = utcNowForTest ?? (() => DateTimeOffset.UtcNow);
        _transaction = new SqliteRecurringSetupPreparationTransaction(
            store,
            idProviderForTest ?? new GuidRecurringSetupPreparationIdProvider(),
            beforeWritesForTest,
            beforeCommitForTest,
            failureHookForTest);
    }

    internal RecurringSetupPreparationResult Prepare(RecurringFixedRegionSelectionSnapshot? selection)
    {
        if (selection is null)
        {
            return RecurringSetupPreparationResult.Rejected(null, "recurring_setup_preparation_selection_invalid");
        }

        if (!TryReadTrustedUtcNow(out var nowUtc, out var clockFailure))
        {
            return RecurringSetupPreparationResult.Rejected(selection.IntentId, clockFailure);
        }

        try
        {
            return _transaction.Prepare(selection, nowUtc);
        }
        catch (Phase3PersistenceException exception)
        {
            return MapFailure(selection.IntentId, exception.Code);
        }
        catch (Exception)
        {
            return RecurringSetupPreparationResult.Rejected(selection.IntentId, "recurring_setup_preparation_sqlite_failure");
        }
    }

    private bool TryReadTrustedUtcNow(out DateTimeOffset nowUtc, out string failureReason)
    {
        nowUtc = default;
        failureReason = "recurring_setup_preparation_clock_unavailable";
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
            failureReason = "recurring_setup_preparation_time_not_utc";
            return false;
        }

        lock (_clockSync)
        {
            if (_lastUtcNow is not null && nowUtc < _lastUtcNow.Value)
            {
                failureReason = "recurring_setup_preparation_time_non_monotonic";
                return false;
            }

            _lastUtcNow = nowUtc;
        }

        return true;
    }

    private static RecurringSetupPreparationResult MapFailure(string? intentId, string code) => code switch
    {
        "recurring_setup_preparation_selection_conflict" or
        "recurring_setup_preparation_identity_conflict" or
        "recurring_setup_preparation_partial_binding" or
        "recurring_setup_preparation_chain_conflict" or
        "recurring_setup_preparation_concurrency_conflict" =>
            RecurringSetupPreparationResult.Conflict(intentId, code),
        "recurring_setup_preparation_expired" =>
            RecurringSetupPreparationResult.Expired(intentId ?? string.Empty),
        "recurring_setup_preparation_sqlite_failure" or
        "recurring_setup_preparation_constraint_violation" or
        "sqlite_failure" or
        "sqlite_corrupt" or
        "sqlite_not_initialized" or
        "sqlite_migration_checksum_mismatch" =>
            RecurringSetupPreparationResult.Rejected(intentId, "recurring_setup_preparation_sqlite_failure"),
        _ => RecurringSetupPreparationResult.Rejected(intentId, code),
    };
}

internal sealed class RecurringSetupPreparationResult
{
    private RecurringSetupPreparationResult(
        RecurringSetupPreparationResultStatus status,
        string reason,
        string? intentId,
        string? planId,
        string? profileId,
        long? profileVersion,
        string? profileDigest,
        string? leaseId,
        string? configurationDigest,
        string? selectionDigest,
        bool changed)
    {
        Status = status;
        Reason = reason;
        IntentId = intentId;
        PlanId = planId;
        ProfileId = profileId;
        ProfileVersion = profileVersion;
        ProfileDigest = profileDigest;
        LeaseId = leaseId;
        ConfigurationDigest = configurationDigest;
        SelectionDigest = selectionDigest;
        Changed = changed;
    }

    internal RecurringSetupPreparationResultStatus Status { get; }
    internal string Reason { get; }
    internal string? IntentId { get; }
    internal string? PlanId { get; }
    internal string? ProfileId { get; }
    internal long? ProfileVersion { get; }
    internal string? ProfileDigest { get; }
    internal string? LeaseId { get; }
    internal string? ConfigurationDigest { get; }
    internal string? SelectionDigest { get; }
    internal bool Changed { get; }

    internal static RecurringSetupPreparationResult Prepared(RecurringSetupPreparationSnapshot snapshot) =>
        FromSnapshot(RecurringSetupPreparationResultStatus.Prepared, "prepared", snapshot, true);

    internal static RecurringSetupPreparationResult Existing(RecurringSetupPreparationSnapshot snapshot) =>
        FromSnapshot(RecurringSetupPreparationResultStatus.Existing, "existing", snapshot, false);

    internal static RecurringSetupPreparationResult Conflict(string? intentId, string reason) =>
        new(RecurringSetupPreparationResultStatus.Conflict, reason, intentId, null, null, null, null, null, null, null, false);

    internal static RecurringSetupPreparationResult Rejected(string? intentId, string reason) =>
        new(RecurringSetupPreparationResultStatus.Rejected, reason, intentId, null, null, null, null, null, null, null, false);

    internal static RecurringSetupPreparationResult Expired(string intentId) =>
        new(RecurringSetupPreparationResultStatus.Expired, "expired", intentId, null, null, null, null, null, null, null, false);

    private static RecurringSetupPreparationResult FromSnapshot(
        RecurringSetupPreparationResultStatus status,
        string reason,
        RecurringSetupPreparationSnapshot snapshot,
        bool changed) =>
        new(
            status,
            reason,
            snapshot.IntentId,
            snapshot.PlanId,
            snapshot.ProfileId,
            snapshot.ProfileVersion,
            snapshot.ProfileDigest,
            snapshot.LeaseId,
            snapshot.ConfigurationDigest,
            snapshot.SelectionDigest,
            changed);
}
