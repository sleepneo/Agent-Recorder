using AgentRecorder.Core;
using AgentRecorder.Core.Automation;
using AgentRecorder.Infrastructure;
using Microsoft.Data.Sqlite;

namespace AgentRecorder.Persistence;

public static class RecurringLeaseLocalApprovalPersistenceReasonCodes
{
    public const string InvalidArgument = "recurring_lease_approval_persistence_invalid_argument";
    public const string NotFound = "recurring_lease_approval_persistence_not_found";
    public const string PersistedDataInvalid = "recurring_lease_approval_persistence_data_invalid";
    public const string StorageFailure = "recurring_lease_approval_persistence_storage_failure";
}

/// <summary>
/// Read-only projection.  There is deliberately no public insert/update API;
/// the activation transaction is the only writer of this immutable table.
/// </summary>
public sealed class SqliteRecurringLeaseLocalApprovalEvidenceReader : SqliteRepositoryBase, IRecurringLeaseLocalApprovalEvidenceReader
{
    public SqliteRecurringLeaseLocalApprovalEvidenceReader(SqliteOperationalStore store)
        : base(store)
    {
    }

    public RecurringLeaseLocalApprovalEvidence? TryGetByLease(string leaseId)
    {
        try
        {
            leaseId = RequiredInput(leaseId);
        }
        catch (Phase3PersistenceException exception)
        {
            throw new Phase3PersistenceException(RecurringLeaseLocalApprovalPersistenceReasonCodes.InvalidArgument, "The recurring lease identifier is invalid.", exception);
        }

        using var connection = OpenBusinessConnection();
        using var transaction = BeginReadTransaction(connection);
        try
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                SELECT approval_id, lease_id, plan_id, configuration_digest,
                       authorization_digest, current_user_sid, session_binding,
                       approved_at_utc, approval_kind_code, approval_version,
                       approval_digest
                FROM recurring_lease_local_approvals
                WHERE lease_id = $lease_id
                LIMIT 1;
                """;
            Add(command, "$lease_id", leaseId);
            using var reader = command.ExecuteReader();
            if (!reader.Read())
            {
                transaction.Commit();
                return null;
            }

            var evidence = ReadEvidence(reader);
            reader.Close();
            var lease = SqliteRecurringConsentLeaseRepository.ReadWithinTransaction(connection, transaction, leaseId)
                ?? throw new PersistedSnapshotException("The approval evidence points to a missing recurring lease.");
            if (!string.Equals(evidence.LeaseId, lease.LeaseId, StringComparison.Ordinal) ||
                !string.Equals(evidence.PlanId, lease.PlanId, StringComparison.Ordinal) ||
                !string.Equals(evidence.ConfigurationDigest, lease.ConfigurationRef.ConfigurationDigest, StringComparison.Ordinal) ||
                !string.Equals(evidence.AuthorizationDigest, lease.AuthorizationDigest, StringComparison.Ordinal))
            {
                throw new PersistedSnapshotException("The approval evidence is not bound to the exact recurring lease authorization.");
            }

            transaction.Commit();
            return evidence;
        }
        catch (Phase3PersistenceException)
        {
            TryRollback(transaction);
            throw;
        }
        catch (SqliteException exception)
        {
            TryRollback(transaction);
            throw new Phase3PersistenceException(RecurringLeaseLocalApprovalPersistenceReasonCodes.StorageFailure, "The recurring approval evidence could not be read.", exception);
        }
        catch (Exception exception) when (exception is PersistedSnapshotException or Phase3DomainException or InvalidCastException or FormatException or OverflowException or ArgumentException)
        {
            TryRollback(transaction);
            throw new Phase3PersistenceException(RecurringLeaseLocalApprovalPersistenceReasonCodes.PersistedDataInvalid, "The persisted recurring approval evidence is invalid.", exception);
        }
    }

    internal static RecurringLeaseLocalApprovalEvidence? ReadWithinTransaction(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string leaseId)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT approval_id, lease_id, plan_id, configuration_digest,
                   authorization_digest, current_user_sid, session_binding,
                   approved_at_utc, approval_kind_code, approval_version,
                   approval_digest
            FROM recurring_lease_local_approvals
            WHERE lease_id = $lease_id
            LIMIT 1;
            """;
        Add(command, "$lease_id", leaseId);
        using var reader = command.ExecuteReader();
        return reader.Read() ? ReadEvidence(reader) : null;
    }

    private static RecurringLeaseLocalApprovalEvidence ReadEvidence(SqliteDataReader reader)
    {
        var approvalId = ReadRequiredText(reader, 0);
        var leaseId = ReadRequiredText(reader, 1);
        var planId = ReadRequiredText(reader, 2);
        var configurationDigest = ReadRequiredText(reader, 3);
        var authorizationDigest = ReadRequiredText(reader, 4);
        var currentUserSid = ReadRequiredText(reader, 5);
        var sessionBinding = ReadRequiredText(reader, 6);
        var approvedAtUtc = ReadUtcDateTimeOffset(reader, 7);
        var approvalKind = ReadRequiredText(reader, 8);
        var approvalVersion = ReadInt64(reader, 9);
        var approvalDigest = ReadRequiredText(reader, 10);
        if (approvalVersion is < int.MinValue or > int.MaxValue)
        {
            throw new PersistedSnapshotException("The recurring approval version is outside the Int32 range.");
        }

        return RecurringLeaseLocalApprovalEvidence.Rehydrate(
            approvalId, leaseId, planId, configurationDigest, authorizationDigest,
            currentUserSid, sessionBinding, approvedAtUtc, approvalKind, (int)approvalVersion, approvalDigest);
    }

    private static void TryRollback(SqliteTransaction transaction)
    {
        try { transaction.Rollback(); } catch { }
    }
}
