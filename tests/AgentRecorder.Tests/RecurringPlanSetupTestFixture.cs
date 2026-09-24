using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using AgentRecorder.Api;
using AgentRecorder.App;
using AgentRecorder.Core;
using AgentRecorder.Core.Automation;
using AgentRecorder.Infrastructure;
using AgentRecorder.Logging;
using AgentRecorder.Persistence;
using AgentRecorder.Windows;
using Microsoft.Data.Sqlite;
using Xunit;

namespace AgentRecorder.Tests;

internal sealed class RecurringPlanSetupTestFixture : IDisposable
{
    internal const string Sid = "S-1-5-21-recurring-setup";
    internal const string Session = "recurring-setup-session";
    internal static DateTimeOffset At(int seconds) => new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero).AddSeconds(seconds);
    internal string Root { get; } = Path.Combine(Path.GetTempPath(), "AgentRecorderRecurringSetupCoordinator_" + Guid.NewGuid().ToString("N"));
    internal SqliteOperationalStore Store { get; }
    internal DateTimeOffset? Now = At(10);
    internal RecurringSetupPrincipal? Principal = new(Sid, Session);
    internal SetupAudit Audit { get; }
    internal ReadyOutput Output { get; } = new();
    internal IReadOnlyList<StandingLeaseDisplayMetadata> Displays = new[]
    {
        new StandingLeaseDisplayMetadata("api-token-not-identity", "stable-display", DisplayIdentityResolutionStatus.Resolved,
            new AuthorizedPhysicalRectangle(100, 200, 1920, 1080), 96, 96, 1920, 1080, AuthorizedDisplayOrientation.Landscape),
    };
    internal RecurringPlanSetupTestFixture()
    {
        Directory.CreateDirectory(Root);
        Store = new SqliteOperationalStore(Path.Combine(Root, "state.db")); Store.Initialize();
        Execute("UPDATE unattended_safety_state SET unattended_mode_code = 'enabled';");
        Audit = new SetupAudit(Path.Combine(Root, "audit.log"));
    }

    internal RecurringPlanApiRequest Request(string key = "key") => new(key,
        RecurringPlanSchedule.CreateDaily("China Standard Time", new(2026, 1, 1), new(2026, 1, 2), new(9, 30),
            2, TimeSpan.FromSeconds(30), TimeSpan.FromMinutes(5)), At(172800), 2, TimeSpan.FromSeconds(60),
        AuthorizedFixedRegionScope.NormalizeOutputDirectoryForAuthorization(Path.Combine(Root, "must-not-be-created")), "private-prefix");

    internal void Create(string id, bool prepared = false, string sid = Sid, string session = Session)
    {
        var r = Request(id + "-key");
        var snapshot = RecurringSetupIntentSnapshot.CreateForTrustedSetupAdapter(id, r.IdempotencyKey, sid, session,
            Now!.Value, r.LeaseValidUntilUtc, r.Schedule, r.MaxRuns, r.MaxTotalDuration, r.LeaseValidUntilUtc, r.OutputDirectory, r.FilenamePrefix);
        Assert.Equal(RecurringSetupIntentResultStatus.Created, new RecurringSetupIntentService(Store, () => Now).CreateOrGet(snapshot).Result);
        if (prepared)
        {
            var selection = RecurringFixedRegionSelectionSnapshot.CreateForTrustedLocalSelectionAdapter(id, sid, session, Now.Value,
                "stable-display", new(100, 200, 1920, 1080), new(10, 20, 640, 480), 96, 96, 1920, 1080,
                AuthorizedDisplayOrientation.Landscape, new string('a', 64));
            Assert.Equal(RecurringSetupPreparationResultStatus.Prepared, new RecurringSetupPreparationService(Store, () => Now).Prepare(selection).Status);
        }
    }

    internal RecurringPlanSetupQueryService Query => new(Store);
    internal void Activate(string id, string sid = Sid, string session = Session)
    {
        var p = Query.GetPrepared(id, sid, session)!;
        var receipt = RecurringLeaseLocalApprovalReceipt.CreateForTrustedLocalApprovalAdapter("approval-" + id, p.LeaseId, p.PlanId,
            p.ConfigurationDigest, p.AuthorizationDigest, sid, session, Now!.Value);
        Assert.Equal(RecurringLeaseLocalApprovalActivationStatus.Activated,
            new RecurringLeasePreparedIntentApprovalActivationService(Store, () => Now).Activate(id, receipt).Status);
    }
    internal RecurringSetupTerminalResult Reject(string id) => new RecurringPlanSetupTerminalService(Store, () => Now)
        .Settle(id, Sid, Session, RecurringSetupIntentStatus.Rejected, "setup_conflict");
    internal RecurringPlanSetupCoordinator Coordinator(IRecurringPlanSetupUi ui, Func<Task>? beforeCommit = null,
        Action? commit = null, Action? shutdown = null, Func<bool>? executionSupported = null) => new(Store, Audit, ui, () => Principal, () => Now, () => Displays, Output, beforeCommit, commit, shutdown,
            executionSupportedProvider: executionSupported ?? (() => true));
    internal StandingPlanSetupCoordinator StandingCoordinator(IStandingPlanSetupUi ui) => new(
        Store,
        Audit,
        ui,
        new StandingLeaseSafetyControlService(Store),
        () => Displays,
        outputReadinessProviderForTest: new ReadyStandingOutput());
    internal StandingPlanApiRequest StandingRequest(string key)
    {
        var now = DateTimeOffset.UtcNow;
        return new StandingPlanApiRequest(key, now.AddMinutes(2), now.AddMinutes(3), now.AddMinutes(5),
            now.AddMinutes(10), TimeSpan.FromSeconds(30), Path.Combine(Root, "standing-output"), "capture.mp4");
    }
    internal long Scalar(string sql) => long.Parse(Text(sql), CultureInfo.InvariantCulture);
    internal string Text(string sql)
    {
        using var c = Store.OpenConnection(); using var command = c.CreateCommand(); command.CommandText = sql;
        return Convert.ToString(command.ExecuteScalar(), CultureInfo.InvariantCulture)!;
    }
    internal void Execute(string sql) { using var c = Store.OpenConnection(); Execute(c, sql); }
    private static void Execute(SqliteConnection c, string sql) { using var command = c.CreateCommand(); command.CommandText = sql; command.ExecuteNonQuery(); }
    internal string State()
    {
        using var c = Store.OpenConnection();
        var tables = new List<string>();
        using (var command = c.CreateCommand())
        {
            command.CommandText = "SELECT name FROM sqlite_master WHERE type = 'table' AND name NOT LIKE 'sqlite_%' ORDER BY name;";
            using var reader = command.ExecuteReader(); while (reader.Read()) tables.Add(reader.GetString(0));
        }
        var rows = new List<string>();
        foreach (var table in tables)
        {
            using var command = c.CreateCommand(); command.CommandText = $"SELECT * FROM {table} ORDER BY 1;";
            using var reader = command.ExecuteReader();
            while (reader.Read()) rows.Add(table + ":" + string.Join("|", Enumerable.Range(0, reader.FieldCount).Select(i => Convert.ToString(reader.GetValue(i), CultureInfo.InvariantCulture))));
        }
        return string.Join("\n", rows);
    }
    internal void Corrupt(string sql)
    {
        using var c = Store.OpenConnection();
        var triggers = new List<(string Name, string Sql)>();
        using (var command = c.CreateCommand())
        {
            command.CommandText = "SELECT name, sql FROM sqlite_master WHERE type = 'trigger';";
            using var reader = command.ExecuteReader(); while (reader.Read()) triggers.Add((reader.GetString(0), reader.GetString(1)));
        }
        Execute(c, "PRAGMA foreign_keys = OFF; PRAGMA ignore_check_constraints = ON;");
        try
        {
            foreach (var t in triggers) Execute(c, $"DROP TRIGGER \"{t.Name}\";");
            Execute(c, sql);
        }
        finally
        {
            foreach (var t in triggers) Execute(c, t.Sql);
            Execute(c, "PRAGMA foreign_keys = ON; PRAGMA ignore_check_constraints = OFF;");
        }
    }
    internal void AssertNoExecution()
    {
        foreach (var table in new[] { "plan_occurrences", "recording_runs", "recurring_occurrence_slots", "recurring_lease_uses", "recurring_occurrence_execution_specs", "lease_uses" })
            Assert.Equal(0, Scalar($"SELECT COUNT(*) FROM {table};"));
        Assert.False(Directory.Exists(Request().OutputDirectory));
        Assert.Empty(Directory.GetFiles(Root, "*.mp4", SearchOption.AllDirectories));
    }
    public void Dispose() => Directory.Delete(Root, recursive: true);

    internal sealed class SetupAudit(string path) : AuditLogger(path)
    {
        internal ConcurrentQueue<string> Events { get; } = new();
        public override void Log(string evt, object payload) => Events.Enqueue(evt + JsonSerializer.Serialize(payload));
    }
    internal sealed class ReadyOutput : IRecurringSetupOutputReadinessProvider
    {
        internal RecurringSetupOutputReadiness Result = RecurringSetupOutputReadiness.Ready;
        internal ConcurrentQueue<(string Directory, TimeSpan Duration)> Calls { get; } = new();
        public RecurringSetupOutputReadiness Check(string outputDirectory, TimeSpan singleRunDuration)
        { Calls.Enqueue((outputDirectory, singleRunDuration)); return Result; }
    }
    private sealed class ReadyStandingOutput : IStandingLeaseOutputReadinessProvider
    {
        public StandingLeaseOutputReadinessResult Check(string outputDirectory, string frozenFileName, TimeSpan reservedDuration) =>
            StandingLeaseOutputReadinessResult.Ready(new StandingLeaseOutputFileSystemSnapshot(
                outputDirectory, Path.Combine(outputDirectory, frozenFileName), true, false, true, true, long.MaxValue, 1));
    }
    internal sealed class FakeUi : IRecurringPlanSetupUi
    {
        internal int Selections;
        internal int Approvals;
        internal bool Available = true;
        internal Func<CancellationToken, Task<RecurringPlanSetupSelection>>? Select;
        internal Func<RecurringLeaseApprovalDetails, CancellationToken, Task<RecurringLeaseApprovalResult>>? Approve;
        public bool IsInteractiveDesktopAvailable => Available;
        public Task<RecurringPlanSetupSelection> SelectFreshRegionAsync(CancellationToken token)
        {
            Interlocked.Increment(ref Selections);
            return Select?.Invoke(token) ?? Task.FromResult(Selected());
        }
        public Task<RecurringLeaseApprovalResult> ShowApprovalAsync(RecurringLeaseApprovalDetails details, CancellationToken token)
        {
            Interlocked.Increment(ref Approvals);
            return Approve?.Invoke(details, token) ?? Task.FromResult(RecurringLeaseApprovalResult.Approved);
        }
        internal static RecurringPlanSetupSelection Selected() => new(RecurringRegionSelectionStatus.Selected, 110, 220, 640, 480);
    }
}
