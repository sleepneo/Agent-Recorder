using AgentRecorder.Core;
using AgentRecorder.Core.Automation;
using AgentRecorder.Infrastructure;
using Microsoft.Data.Sqlite;

namespace AgentRecorder.Persistence;

/// <summary>
/// Read-only current-safety boundary for recurring execution. The caller is
/// responsible for holding StandingLeaseStartSafetyInterlock; this type only
/// reads one exact durable snapshot inside one SQLite immediate transaction.
/// </summary>
internal sealed class SqliteRecurringLeaseCurrentSafetyValidator : SqlitePeriodicOccurrenceDueRepositoryBase
{
    internal SqliteRecurringLeaseCurrentSafetyValidator(SqliteOperationalStore store)
        : base(store)
    {
    }

    internal RecurringLeaseCurrentSafetyDecision Validate(
        RecurringLeaseCaptureExecutionTicket? ticket,
        DateTimeOffset nowUtc)
    {
        if (ticket is null || nowUtc.Offset != TimeSpan.Zero)
            return Invalid();

        SqliteTransaction? transaction = null;
        try
        {
            using var connection = OpenBusinessConnection();
            transaction = BeginWriteTransaction(connection);

            var snapshot = SqliteRecurringLeaseExecutionSnapshotLoader
                .ReadExactSnapshotWithinTransaction(
                    connection,
                    transaction,
                    RecurringLeaseExecutionSnapshotLookup.FromTicket(ticket));

            // Durable shape and canonical digest validation run before any
            // current-safety classification. A malformed snapshot must never
            // be softened into a user-facing safety status.
            if (!RecurringLeaseExecutionGate.TryValidateDurableSnapshot(
                    snapshot,
                    out _,
                    includeCurrentSafety: false) ||
                !RecurringLeaseExecutionGate.TryValidateTicketSnapshotBinding(
                    ticket,
                    snapshot,
                    nowUtc,
                    out _) ||
                !RecurringLeaseExecutionGate.TryValidateCurrentExecutionWindow(
                    snapshot,
                    nowUtc,
                    out _))
            {
                return Invalid();
            }

            var decision = RecurringLeaseExecutionGate.EvaluateCurrentSafety(snapshot);
            transaction.Commit();
            return decision;
        }
        catch (Phase3PersistenceException)
        {
            return Invalid();
        }
        catch (PersistedSnapshotException)
        {
            return Invalid();
        }
        catch (Phase3DomainException)
        {
            return Invalid();
        }
        catch (SqliteException)
        {
            return Invalid();
        }
        catch (Exception)
        {
            return Invalid();
        }
        finally
        {
            try
            {
                transaction?.Dispose();
            }
            catch
            {
                // A read-only validator remains fail-closed if cleanup fails.
            }
        }
    }

    private static RecurringLeaseCurrentSafetyDecision Invalid() =>
        new(RecurringLeaseCurrentSafetyStatus.StateInvalid);
}
