using System.Diagnostics;
using System.Security.Principal;
using System.Text;
using AgentRecorder.Capture;
using AgentRecorder.Core;
using AgentRecorder.Core.Automation;
using AgentRecorder.Infrastructure;
using Microsoft.Data.Sqlite;

namespace AgentRecorder.Persistence;

/// <summary>
/// Exact, single-chain recovery for the owner of one recurring first commit.
/// It is deliberately separate from the startup page query: the source
/// identity comes only from the immutable post-commit handoff chain, while
/// the current rows and versions are re-read before the recovery transaction.
/// </summary>
internal sealed class RecurringLeaseImmediateRecoveryService : SqliteRepositoryBase
{
    private readonly SqliteRecurringLeaseRestartRecoveryTransaction _transaction;
    private readonly Func<string> _currentUserSid;
    private readonly Func<string> _sessionBinding;
    private readonly Action<SqliteConnection, SqliteTransaction>? _afterExactReadForTest;

    internal RecurringLeaseImmediateRecoveryService(
        SqliteOperationalStore store,
        Func<string>? currentUserSidForTest = null,
        Func<string>? sessionBindingForTest = null,
        Action<RecurringLeaseRestartRecoveryFailurePoint>? failureHookForTest = null,
        Action<SqliteConnection, SqliteTransaction>? beforeCommitForTest = null,
        Action<SqliteConnection, SqliteTransaction>? afterExactReadForTest = null)
        : base(store)
    {
        _transaction = new SqliteRecurringLeaseRestartRecoveryTransaction(
            store,
            failureHookForTest,
            beforeCommitForTest);
        _currentUserSid = currentUserSidForTest ?? ReadCurrentUserSid;
        _sessionBinding = sessionBindingForTest ?? ReadCurrentSessionBinding;
        _afterExactReadForTest = afterExactReadForTest;
    }

    internal RecurringLeaseRestartRecoveryResult Recover(
        RecurringStartCommitReceipt? receipt,
        DateTimeOffset trustedNowUtc)
    {
        if (receipt is null)
            return Rejected("recovery_evidence_missing");

        return RecoverExact(
            RecurringLeaseImmediateRecoveryEvidence.FromReceipt(receipt),
            trustedNowUtc);
    }

    internal RecurringLeaseRestartRecoveryResult Recover(
        RecurringLeaseUseProof? proof,
        DateTimeOffset trustedNowUtc)
    {
        if (proof is null)
            return Rejected("recovery_evidence_missing");

        return RecoverExact(
            RecurringLeaseImmediateRecoveryEvidence.FromProof(proof),
            trustedNowUtc);
    }

    internal RecurringLeaseRestartRecoveryResult Recover(
        RecurringLeaseCaptureAuthorization? authorization,
        DateTimeOffset trustedNowUtc)
    {
        if (authorization is null)
            return Rejected("recovery_evidence_missing");

        return RecoverExact(
            RecurringLeaseImmediateRecoveryEvidence.FromAuthorization(authorization),
            trustedNowUtc);
    }

    internal RecurringLeaseRestartRecoveryResult Recover(
        RecurringLeaseCaptureExecutionTicket? ticket,
        DateTimeOffset trustedNowUtc)
    {
        if (ticket is null)
            return Rejected("recovery_evidence_missing");

        return RecoverExact(
            RecurringLeaseImmediateRecoveryEvidence.FromTicket(ticket),
            trustedNowUtc);
    }

    private RecurringLeaseRestartRecoveryResult RecoverExact(
        RecurringLeaseImmediateRecoveryEvidence evidence,
        DateTimeOffset trustedNowUtc)
    {
        if (trustedNowUtc.Offset != TimeSpan.Zero ||
            evidence is null ||
            !IsCanonicalId(evidence.PlanId, allowNull: true) ||
            !IsCanonicalId(evidence.LeaseId) ||
            !IsCanonicalId(evidence.OccurrenceIdentity) ||
            !IsCanonicalId(evidence.OccurrenceId, allowNull: true) ||
            !IsCanonicalId(evidence.RunId) ||
            !IsCanonicalId(evidence.UseId) ||
            !IsCanonicalId(evidence.EvidenceUserSid) ||
            !IsCanonicalId(evidence.EvidenceSessionBinding) ||
            !IsCanonicalId(evidence.SpecificationDigest) ||
            evidence.EvidenceUserSessionBinding is not null &&
            !string.Equals(
                evidence.EvidenceUserSessionBinding,
                evidence.EvidenceUserSid + "|" + evidence.EvidenceSessionBinding,
                StringComparison.Ordinal))
        {
            return Rejected("recovery_identity_mismatch");
        }

        string currentUserSid;
        string currentSessionBinding;
        try
        {
            currentUserSid = _currentUserSid();
            currentSessionBinding = _sessionBinding();
        }
        catch
        {
            return Rejected("recovery_identity_mismatch");
        }

        if (!IsCanonicalId(currentUserSid) ||
            !IsCanonicalId(currentSessionBinding) ||
            !string.Equals(currentUserSid, evidence.EvidenceUserSid, StringComparison.Ordinal) ||
            !string.Equals(currentSessionBinding, evidence.EvidenceSessionBinding, StringComparison.Ordinal))
        {
            return Rejected("recovery_identity_mismatch");
        }

        RecurringLeaseRestartRecoveryCandidate candidate;
        try
        {
            using var connection = OpenBusinessConnection();
            using var transaction = BeginReadTransaction(connection);
            var snapshot = SqliteRecurringLeaseExecutionSnapshotLoader.ReadExactSnapshotWithinTransaction(
                connection,
                transaction,
                new RecurringLeaseExecutionSnapshotLookup(
                    evidence.LeaseId,
                    evidence.PlanId,
                    evidence.OccurrenceIdentity,
                    evidence.OccurrenceId,
                    evidence.RunId,
                    evidence.UseId));

            if (!evidence.Matches(snapshot, out var evidenceFailure))
            {
                return Rejected(evidenceFailure);
            }

            candidate = new RecurringLeaseRestartRecoveryCandidate(
                snapshot.Plan.Id,
                snapshot.Lease.LeaseId,
                snapshot.Specification.OccurrenceIdentity,
                snapshot.Occurrence.Id,
                snapshot.Run.Id,
                snapshot.Use.Id,
                snapshot.Run.StatusCode,
                snapshot.Use.StatusCode,
                snapshot.Occurrence.StatusCode,
                snapshot.Run.Version,
                snapshot.Use.Version,
                snapshot.Occurrence.Version,
                snapshot.Lease.Version,
                snapshot.Plan.Version);
            _afterExactReadForTest?.Invoke(connection, transaction);
            transaction.Commit();
        }
        catch (Phase3PersistenceException exception)
        {
            return Rejected(NormalizeReadFailure(exception.Code));
        }
        catch (PersistedSnapshotException)
        {
            return Rejected("recovery_snapshot_invalid");
        }
        catch (Phase3DomainException)
        {
            return Rejected("recovery_snapshot_invalid");
        }
        catch (SqliteException)
        {
            return Rejected("recovery_sqlite_failure");
        }
        catch
        {
            return Rejected("recovery_sqlite_failure");
        }

        // The second transaction is the only mutation boundary. It re-reads
        // every row/version and is also responsible for legal terminal no-op
        // handling and CAS rollback on any concurrent change.
        return _transaction.Reconcile(candidate, currentUserSid, currentSessionBinding, trustedNowUtc);
    }

    private static RecurringLeaseRestartRecoveryResult Rejected(string reason) =>
        RecurringLeaseRestartRecoveryResult.Rejected(reason);

    private static string NormalizeReadFailure(string code) => code switch
    {
        "not_found" or
        "persisted_snapshot_invalid" or
        "invalid_version" or
        "immutable_mismatch" => "recovery_snapshot_invalid",
        "concurrency_conflict" => "recovery_concurrency_conflict",
        "sqlite_failure" or
        "sqlite_corrupt" or
        "sqlite_not_initialized" or
        "sqlite_migration_checksum_mismatch" => "recovery_sqlite_failure",
        _ => "recovery_snapshot_invalid",
    };

    private static bool IsCanonicalId(string? value, bool allowNull = false) =>
        allowNull && value is null ||
        !string.IsNullOrWhiteSpace(value) &&
        string.Equals(value, value.Trim(), StringComparison.Ordinal) &&
        !value.Any(char.IsControl);

    private static string ReadCurrentUserSid()
    {
        try
        {
            return WindowsIdentity.GetCurrent().User?.Value ?? "";
        }
        catch
        {
            return "";
        }
    }

    private static string ReadCurrentSessionBinding()
    {
        var user = string.IsNullOrWhiteSpace(Environment.UserName) ? "unknown-user" : Environment.UserName;
        var domain = string.IsNullOrWhiteSpace(Environment.UserDomainName) ? "unknown-domain" : Environment.UserDomainName;
        int sessionId;
        try
        {
            sessionId = Process.GetCurrentProcess().SessionId;
        }
        catch
        {
            sessionId = -1;
        }

        var rawBinding = $"{domain}\\{user}|session:{sessionId}";
        return Convert.ToBase64String(Encoding.UTF8.GetBytes(rawBinding))
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
    }
}
