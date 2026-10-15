using AgentRecorder.Core;
using AgentRecorder.Core.Automation;
using AgentRecorder.Infrastructure;
using AgentRecorder.Persistence;
using Microsoft.Data.Sqlite;
using Xunit;
using static AgentRecorder.Tests.UnifiedHistoryTestDatabase;

namespace AgentRecorder.Tests;

internal sealed class RetentionGuardFixture : IDisposable
{
    internal readonly UnifiedHistoryTestDatabase Db = new(Path.Combine(TestHelper.ProjectRoot, ".local-data/temp/task356", Guid.NewGuid().ToString("N")));
    internal readonly RetentionGuardAdmission Admission = new();
    internal readonly OrdinaryRecordingHistorySnapshot Row;
    internal PublicationIdentityAnchor Anchor => Row.PublicationReceipt!.Anchor;
    internal DateTimeOffset Cutoff => At.AddHours(1);
    internal RetentionGuardFixture(string id = "candidate", string? name = null)
    {
        Row = PublicationAnchorTestFixture.Anchored(Db.Recording(id, completed: true) with
        { OutputPath = Path.Combine(Db.Root, "Videos", name ?? "中-Ä.mp4"), BundleRegistration = new(null) });
        Db.Ordinary.Register(Row);
    }
    internal RetentionReferenceGuard Guard(RetentionGuardDatabase? adapter = null, TimeProvider? clock = null,
        string sid = Sid, string binding = Session)
        => new(Db.Store, Db.Ordinary, sid, binding, Admission, adapter, clock);
    internal RetentionReferenceGuard.Session Execute(RetentionReferenceGuard guard,
        Func<RetentionReferenceGuard.ScopeContext, RetentionGuardContinuationResult>? continuation = null,
        CancellationToken cancellation = default)
        => guard.Execute(Row.RecordingId, Anchor, Cutoff, continuation ?? (_ => RetentionGuardContinuationResult.Completed), cancellation);
    internal static async Task<RetentionGuardReason> Finish(RetentionReferenceGuard.Session session)
    {
        var response = await session.Response.WaitAsync(TimeSpan.FromSeconds(8));
        var retired = await session.Retirement.WaitAsync(TimeSpan.FromSeconds(8));
        Assert.True(retired.Retired); Assert.Equal(RetentionGuardCleanup.Closed, retired.Cleanup);
        return response.Reason;
    }
    internal SqliteConnection Writer()
    {
        var c = new SqliteConnection(new SqliteConnectionStringBuilder
        { DataSource = Db.Store.DatabasePath, Mode = SqliteOpenMode.ReadWrite, Pooling = false, Cache = SqliteCacheMode.Private, DefaultTimeout = 1 }.ToString());
        c.Open(); c.DefaultTimeout = 1; SqliteOperationalStore.ConfigureRetentionGuardConnection(c); return c;
    }
    internal static void Write(SqliteConnection c, string sql, params (string Name, object Value)[] parameters)
    { using var cmd = c.CreateCommand(); cmd.CommandTimeout = 1; cmd.CommandText = sql; foreach (var p in parameters) cmd.Parameters.AddWithValue(p.Name, p.Value); cmd.ExecuteNonQuery(); }
    internal void Fixed(string id, string name)
    {
        var request = StandingSetupIntentSnapshot.CreateForTrustedSetupAdapter(id, id + "-key", "foreign-sid", "foreign-session", At,
            At.AddMinutes(5), At.AddSeconds(10), At.AddSeconds(20), At.AddSeconds(80), TimeSpan.FromSeconds(30), At.AddHours(1),
            Path.GetDirectoryName(Row.OutputPath)!, name);
        Assert.Equal(StandingSetupIntentResultStatus.Created, new StandingSetupIntentService(Db.Store, () => At).CreateOrGet(request).Result);
    }
    public void Dispose() => Db.Dispose();

    internal sealed class Clock : TimeProvider
    {
        private long _tick;
        private Action? _fire;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => Interlocked.Read(ref _tick);
        public override DateTimeOffset GetUtcNow() => At.AddDays(2);
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        { _fire = () => callback(state); return new Timer(); }
        internal void Expire() { Interlocked.Add(ref _tick, TimeSpan.FromSeconds(3).Ticks); _fire?.Invoke(); }
        private sealed class Timer : ITimer
        { public bool Change(TimeSpan dueTime, TimeSpan period) => true; public void Dispose() { } public ValueTask DisposeAsync() => ValueTask.CompletedTask; }
    }
    internal sealed class TraceDatabase : RetentionGuardDatabase
    {
        internal Action? BeforeOpen, BeforeRollback;
        internal string? Fault;
        internal readonly List<int> Threads = new();
        internal SqliteConnection? Connection;
        internal int Begins, Rollbacks, TransactionDisposes, Closes, ConnectionDisposes;
        private void Step(string name) { Threads.Add(Environment.CurrentManagedThreadId); if (Fault == name) throw new IOException("PRIVATE_DATABASE_FAULT"); }
        internal override void Open(SqliteConnection c) { Step("open"); Connection = c; BeforeOpen?.Invoke(); base.Open(c); }
        internal override SqliteTransaction Begin(SqliteConnection c)
        {
            Step("begin"); Begins++; Assert.Equal(1, c.DefaultTimeout);
            using (var cmd = c.CreateCommand()) { Assert.Equal(1, cmd.CommandTimeout); cmd.CommandText = "PRAGMA busy_timeout;"; Assert.Equal(1000L, cmd.ExecuteScalar()); }
            var t = base.Begin(c); Assert.Equal(System.Data.IsolationLevel.Serializable, t.IsolationLevel);
            Assert.Equal(0, SQLitePCL.raw.sqlite3_get_autocommit(c.Handle)); return t;
        }
        internal override void Rollback(SqliteTransaction t) { Rollbacks++; Step("rollback"); BeforeRollback?.Invoke(); base.Rollback(t); }
        internal override void DisposeTransaction(SqliteTransaction t) { TransactionDisposes++; Step("transaction_dispose"); base.DisposeTransaction(t); }
        internal override void Close(SqliteConnection c) { Closes++; Step("close"); base.Close(c); }
        internal override void DisposeConnection(SqliteConnection c) { ConnectionDisposes++; Step("connection_dispose"); base.DisposeConnection(c); }
    }
}
