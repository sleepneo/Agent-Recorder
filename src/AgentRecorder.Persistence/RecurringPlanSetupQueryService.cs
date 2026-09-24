using AgentRecorder.Core.Automation;
using AgentRecorder.Infrastructure;
using Microsoft.Data.Sqlite;

namespace AgentRecorder.Persistence;

internal sealed record RecurringPlanSetupRecord(
    string IntentId, RecurringSetupIntentStatus Status, long Version, string? TerminalReasonCode,
    DateTimeOffset CreatedAtUtc, DateTimeOffset UpdatedAtUtc,
    RecurringSetupIntentSnapshot Request, string? PlanId, string? LeaseId,
    RecurringPlanPreparedRecord? Prepared);

// Only immutable, validated values cross the persistence/UI boundary. Never
// expose the mutable Plan or Lease domain objects to the approval adapter.
internal sealed record RecurringPlanPreparedRecord(
    string IntentId, string PlanId, string LeaseId, string CurrentUserSid, string SessionBinding,
    string ConfigurationDigest, string AuthorizationDigest,
    string StableDisplayFingerprint, AuthorizedPhysicalRectangle DisplayBounds,
    AuthorizedPhysicalRectangle RegionWithinDisplay, int DpiX, int DpiY,
    int PhysicalWidth, int PhysicalHeight, AuthorizedDisplayOrientation Orientation,
    string TopologyDigest, RecurringSetupIntentSnapshot Request);

internal sealed class RecurringPlanSetupQueryService : SqliteRepositoryBase
{
    private readonly Action<string?, string>? _unavailable;

    internal RecurringPlanSetupQueryService(SqliteOperationalStore store, Action<string?, string>? unavailable = null) : base(store) =>
        _unavailable = unavailable;

    internal RecurringPlanSetupRecord? Get(string? intentId, string? sid, string? session)
    {
        if (!Canonical(intentId, 128) || !Canonical(sid, 256) || !Canonical(session, 256)) return null;
        try
        {
            using var connection = OpenBusinessConnection();
            SetReadOnly(connection);
            using var transaction = BeginReadTransaction(connection);
            var record = Read(connection, transaction, intentId!, sid!, session!);
            transaction.Commit();
            return record;
        }
        catch
        {
            Unavailable(intentId);
            return null;
        }
    }

    internal RecurringPlanPreparedRecord? GetPrepared(string? intentId, string? sid, string? session) =>
        Get(intentId, sid, session)?.Prepared;

    // The bound limits inspected candidates (including corrupt ones), not only
    // successes. All projected rows belong to one read-only SQLite snapshot.
    internal IReadOnlyList<RecurringPlanSetupRecord> ListRecoverable(string? sid, string? session, int maximumRows = 128)
    {
        if (!Canonical(sid, 256) || !Canonical(session, 256) || maximumRows is < 1 or > 256)
            return Array.Empty<RecurringPlanSetupRecord>();
        try
        {
            using var connection = OpenBusinessConnection();
            SetReadOnly(connection);
            using var transaction = BeginReadTransaction(connection);
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                SELECT intent_id FROM setup_intents
                WHERE intent_kind_code = $kind AND current_user_sid = $sid AND session_binding = $session
                  AND status_code IN ('region_selection_pending', 'lease_approval_pending')
                ORDER BY created_at_utc, intent_id LIMIT $limit;
                """;
            Add(command, "$kind", RecurringSetupIntentCodes.IntentKind);
            Add(command, "$sid", sid); Add(command, "$session", session); Add(command, "$limit", maximumRows);
            var ids = new List<string>();
            using (var reader = command.ExecuteReader())
                while (reader.Read()) ids.Add(reader.GetString(0));
            var results = new List<RecurringPlanSetupRecord>();
            foreach (var id in ids)
            {
                try
                {
                    var record = Read(connection, transaction, id, sid!, session!);
                    if (record?.Status is RecurringSetupIntentStatus.RegionSelectionPending or RecurringSetupIntentStatus.LeaseApprovalPending)
                        results.Add(record);
                }
                catch { Unavailable(Canonical(id, 128) ? id : null); }
            }
            transaction.Commit();
            return results.AsReadOnly();
        }
        catch
        {
            Unavailable(null);
            return Array.Empty<RecurringPlanSetupRecord>();
        }
    }

    private static RecurringPlanSetupRecord? Read(SqliteConnection connection, SqliteTransaction transaction, string id, string sid, string session)
    {
        var row = SqliteRecurringSetupIntentCreateOrGetTransaction.ReadByIntentId(connection, transaction, id);
        if (row is null || row.IntentKindCode != RecurringSetupIntentCodes.IntentKind || row.CurrentUserSid != sid || row.SessionBinding != session)
            return null;
        var valid = SqliteRecurringSetupIntentCreateOrGetTransaction.ValidatePersistedAndRehydrate(row, connection, transaction);
        var relation = SqliteRecurringSetupPreparationTransaction.ReadPreparationWithinTransaction(connection, transaction, id);
        RecurringPlanPreparedRecord? prepared = null;
        if (valid.Status == RecurringSetupIntentStatus.LeaseApprovalPending)
        {
            var chain = SqliteRecurringSetupPreparationTransaction.ValidatePreparedChainWithinTransaction(connection, transaction, row, valid.Snapshot);
            var profile = chain.Profile;
            prepared = new(id, chain.Plan.Id, chain.Lease.LeaseId, sid, session,
                chain.Configuration.ConfigurationDigest, chain.Lease.AuthorizationDigest,
                profile.StableDisplayFingerprint, profile.DisplayBounds, profile.RegionWithinDisplay,
                profile.DpiX, profile.DpiY, profile.PhysicalWidth, profile.PhysicalHeight, profile.Orientation,
                profile.TopologyDigest, valid.Snapshot);
        }
        return new(id, valid.Status, valid.Version, valid.TerminalReasonCode, row.CreatedAtUtc, row.UpdatedAtUtc,
            valid.Snapshot, relation?.PlanId, relation?.LeaseId, prepared);
    }

    private static void SetReadOnly(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA query_only = ON;";
        command.ExecuteNonQuery();
    }

    private void Unavailable(string? id)
    {
        try { _unavailable?.Invoke(id, "recurring_setup_query_unavailable"); } catch { }
    }

    private static bool Canonical(string? value, int maximum) =>
        !string.IsNullOrWhiteSpace(value) && value == value.Trim() && value.Length <= maximum &&
        !value.Any(char.IsControl) && !value.Contains('/') && !value.Contains('\\');
}
