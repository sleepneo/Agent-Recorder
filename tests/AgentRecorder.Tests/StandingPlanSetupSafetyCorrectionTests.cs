using AgentRecorder.Core;
using AgentRecorder.Core.Automation;
using AgentRecorder.Infrastructure;
using AgentRecorder.Persistence;
using Microsoft.Data.Sqlite;
using Xunit;

namespace AgentRecorder.Tests;

[Collection("NonParallel-AgentRecorderDataDir")]
public sealed class StandingPlanSetupSafetyCorrectionTests
{
    [Fact]
    public void LostSetupCompareAndSwapDoesNotWritePreparedChildren()
    {
        using var database = CreatePreparedDatabase();
        var terminal = new StandingPlanSetupTerminalService(
            database.Store,
            (connection, transaction) =>
            {
                using var command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText = "UPDATE setup_intents SET version = version + 1 WHERE intent_id = 'intent-1';";
                command.ExecuteNonQuery();
            });

        var result = terminal.TrySetTerminal("intent-1", "region_selection_cancelled");

        Assert.False(result);
        Assert.Equal("lease_approval_pending", ReadText(database.Store, "SELECT status_code FROM setup_intents;"));
        Assert.Equal(1L, Scalar(database.Store, "SELECT version FROM setup_intents;"));
        Assert.Equal("draft", ReadText(database.Store, "SELECT status_code FROM plans;"));
        Assert.Equal("pending_lease_approval", ReadText(database.Store, "SELECT status_code FROM plan_occurrences;"));
        Assert.Equal("pending", ReadText(database.Store, "SELECT status_code FROM consent_leases;"));
    }

    [Fact]
    public void AnyChildUpdateCountOtherThanOneRollsBackTheWholeTerminalSettlement()
    {
        using var database = CreatePreparedDatabase();
        Execute(database.Store, "UPDATE plans SET status_code = 'active' WHERE id = 'plan-1';");

        var terminal = new StandingPlanSetupTerminalService(database.Store);

        Assert.Throws<Phase3PersistenceException>(() =>
            terminal.TrySetTerminal("intent-1", "lease_rejected_by_user"));

        Assert.Equal("lease_approval_pending", ReadText(database.Store, "SELECT status_code FROM setup_intents;"));
        Assert.Equal(1L, Scalar(database.Store, "SELECT version FROM setup_intents;"));
        Assert.Equal("active", ReadText(database.Store, "SELECT status_code FROM plans;"));
        Assert.Equal("pending_lease_approval", ReadText(database.Store, "SELECT status_code FROM plan_occurrences;"));
        Assert.Equal("pending", ReadText(database.Store, "SELECT status_code FROM consent_leases;"));
    }

    [Fact]
    public async Task ConcurrentTerminalRequestsSettleChildrenOnce()
    {
        using var database = CreatePreparedDatabase();
        var first = new StandingPlanSetupTerminalService(database.Store);
        var second = new StandingPlanSetupTerminalService(database.Store);

        var results = await Task.WhenAll(
            Task.Run(() => first.TrySetTerminal("intent-1", "region_selection_cancelled")),
            Task.Run(() => second.TrySetTerminal("intent-1", "region_selection_cancelled")));

        Assert.All(results, Assert.True);
        Assert.Equal("rejected", ReadText(database.Store, "SELECT status_code FROM setup_intents;"));
        Assert.Equal(2L, Scalar(database.Store, "SELECT version FROM setup_intents;"));
        Assert.Equal("cancelled", ReadText(database.Store, "SELECT status_code FROM plan_occurrences;"));
        Assert.Equal("rejected", ReadText(database.Store, "SELECT status_code FROM consent_leases;"));
        Assert.Equal("cancelled", ReadText(database.Store, "SELECT status_code FROM plans;"));
        Assert.Equal(1L, Scalar(database.Store, "SELECT COUNT(*) FROM plan_occurrences WHERE version = 1;"));
        Assert.Equal(1L, Scalar(database.Store, "SELECT COUNT(*) FROM consent_leases WHERE version = 1;"));
        Assert.Equal(1L, Scalar(database.Store, "SELECT COUNT(*) FROM plans WHERE version = 1;"));
    }

    private static PreparedDatabase CreatePreparedDatabase()
    {
        var database = new PreparedDatabase();
        var intent = StandingSetupIntentSnapshot.CreateForTrustedSetupAdapter(
            "intent-1", "request-1", "S-1-5-21-test", "session-1", At(0), At(300),
            At(10), At(20), At(80), TimeSpan.FromSeconds(30), At(3600),
            Path.Combine(Path.GetTempPath(), "AgentRecorderTerminalSafety", "captures"), "capture.mp4");
        Assert.Equal(StandingSetupIntentResultStatus.Created,
            new StandingSetupIntentService(database.Store, () => At(1)).CreateOrGet(intent).Result);

        var selection = StandingFixedRegionSelectionSnapshot.CreateForTrustedLocalSelectionAdapter(
            "intent-1", "S-1-5-21-test", "session-1", At(2), "display-a",
            new AuthorizedPhysicalRectangle(0, 0, 1920, 1080),
            new AuthorizedPhysicalRectangle(10, 20, 640, 480),
            96, 96, 1920, 1080, AuthorizedDisplayOrientation.Landscape, new string('a', 64));
        var prepared = new StandingLeasePreparationService(database.Store, () => At(2), new FixedIds()).Prepare(selection);
        Assert.Equal(StandingLeasePreparationResultStatus.Prepared, prepared.Status);
        return database;
    }

    private static long Scalar(SqliteOperationalStore store, string sql)
    {
        using var connection = store.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt64(command.ExecuteScalar());
    }

    private static string ReadText(SqliteOperationalStore store, string sql)
    {
        using var connection = store.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToString(command.ExecuteScalar())!;
    }

    private static void Execute(SqliteOperationalStore store, string sql)
    {
        using var connection = store.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private static DateTimeOffset At(int seconds) =>
        new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero).AddSeconds(seconds);

    private sealed class FixedIds : IStandingLeasePreparationIdProvider
    {
        public string CreatePlanId() => "plan-1";
        public string CreateOccurrenceId() => "occurrence-1";
        public string CreateLeaseId() => "lease-1";
        public string CreateScopeId() => "scope-1";
    }

    private sealed class PreparedDatabase : IDisposable
    {
        private readonly string _directory = Path.Combine(Path.GetTempPath(), "AgentRecorderTerminalSafety_" + Guid.NewGuid().ToString("N"));

        internal PreparedDatabase()
        {
            Directory.CreateDirectory(_directory);
            Store = new SqliteOperationalStore(Path.Combine(_directory, "state", "agent-recorder.db"));
            Store.Initialize();
            Execute(Store, "UPDATE unattended_safety_state SET unattended_mode_code = 'enabled' WHERE state_id = 'global';");
        }

        internal SqliteOperationalStore Store { get; }

        public void Dispose()
        {
            try { Directory.Delete(_directory, recursive: true); }
            catch (DirectoryNotFoundException) { }
            catch (IOException) { }
        }
    }
}
