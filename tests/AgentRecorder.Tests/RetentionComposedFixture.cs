using System.Reflection;
using System.Text.Json.Nodes;
using AgentRecorder.Capture;
using AgentRecorder.Core;
using AgentRecorder.Infrastructure;
using AgentRecorder.Persistence;
using AgentRecorder.Windows;
using Xunit;
using static AgentRecorder.Tests.UnifiedHistoryTestDatabase;

namespace AgentRecorder.Tests;

internal sealed class RetentionComposedFixture : IDisposable
{
    internal readonly UnifiedHistoryTestDatabase Db;
    internal readonly RecordingEngine Engine;
    internal readonly OriginalFileScopeFixture Files = new();
    internal readonly RetentionGuardAdmission DatabaseAdmission = new();
    internal readonly RetentionComposedAdmission Admission = new();
    internal readonly OrdinaryPreparationEngineFixture.Tray Ui = new();
    internal readonly OrdinaryPreparationEngineFixture.Backend Capture = new();
    internal string Videos => Path.Combine(Db.Root, "Videos");
    internal string Media => Path.Combine(Videos, "原始视频.mp4");
    internal readonly OrdinaryRecordingHistorySnapshot Row;
    internal PublicationIdentityAnchor Anchor => Row.PublicationReceipt!.Anchor;
    internal DateTimeOffset Cutoff => At.AddHours(1);
    private readonly bool _native;
    private readonly string? _mode = Environment.GetEnvironmentVariable("AGENT_RECORDER_TEST_MODE");
    private readonly string? _directory = DataDirResolver.HasOverride ? DataDirResolver.Resolve() : null;
    internal RetentionComposedFixture(bool native = false)
    {
        _native = native;
        Db = new(Path.Combine(TestHelper.ProjectRoot, ".local-data/temp/task357", Guid.NewGuid().ToString("N")));
        Environment.SetEnvironmentVariable("AGENT_RECORDER_TEST_MODE", "1"); DataDirResolver.SetOverride(Db.Root);
        SystemQuery.SetDisplayProvider(() => new() { new("display_1", "Synthetic", true, new(0, 0, 1920, 1080), 1.0) });
        Engine = new(new RetentionSharedOutputPathTests.QuietAudit()) { CountdownSteps = 1, CountdownInterval = TimeSpan.FromMinutes(1) };
        Engine.SetTray(Ui); Engine.BackendFactory = _ => (Capture, "fake"); Engine.ConfigureOrdinaryRecordingHistory(Db.Ordinary);
        var identity = new ObservedFileIdentity(123, 456, 789);
        if (native)
        {
            Directory.CreateDirectory(Videos); File.WriteAllBytes(Media, Enumerable.Range(0, 12).Select(i => (byte)i).ToArray());
            identity = WindowsReadOnlyFileMetadataProbe.Instance.Observe(Media).Before!.Identity!;
        }
        Row = Db.Recording("candidate", completed: true) with
        { OutputPath = Media, BundleRegistration = new(null), PublicationReceipt = PublicationReceipt.IssueForTests(Media, 12, identity) };
        Db.Ordinary.Register(Row); Files.Root = Videos; Files.Anchor = Anchor; Files.Api.Anchor = Anchor;
    }
    internal RetentionComposedScope Service(RetentionGuardDatabase? database = null, TimeProvider? clock = null,
        Func<Action, Task>? dispatch = null, bool hostAdmissions = false, SqliteOperationalStore? store = null,
        SqliteOrdinaryRecordingHistoryStore? writer = null)
        => new(Engine, store ?? Db.Store, writer ?? Db.Ordinary, Sid, Session, Videos, _native ? null : Files.Api,
            hostAdmissions ? null : Files.Admission, hostAdmissions ? null : DatabaseAdmission,
            hostAdmissions ? null : Admission, database, clock, dispatch);
    internal RetentionComposedScope.Session Execute(RetentionComposedScope service,
        Func<RetentionComposedScope.ScopeContext, RetentionComposedContinuationResult>? continuation = null, CancellationToken cancellation = default)
        => service.Execute(Row.RecordingId, Anchor, Cutoff, continuation ?? (_ => RetentionComposedContinuationResult.Completed), cancellation);
    internal OrdinaryRetentionPreparationOwner PreparedOwner()
        => new((OrdinaryRetentionPreparationSession)typeof(RecordingEngine).GetField("_ordinaryRetentionPreparation", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(Engine)!);
    internal void AssertProtected() => OrdinaryPreparationFixture.AssertConflict(Engine, Media);
    internal Recording Create(string policy = "fail", string? directory = null)
    {
        Engine.CreateRecording(new JsonObject
        {
            ["source"] = new JsonObject { ["type"] = "display", ["display_id"] = "display_1" }, ["video"] = new JsonObject { ["fps"] = 15 },
            ["duration_seconds"] = 60, ["countdown_seconds"] = 1,
            ["output"] = new JsonObject { ["directory"] = directory ?? Videos, ["filename"] = Path.GetFileName(Media), ["conflict_policy"] = policy }
        }, "composition-fixture", Ui);
        return Assert.Single(Engine._recs.Values);
    }
    internal static async Task<RetentionComposedResult> Finish(RetentionComposedScope.Session session)
    {
        var result = await session.Response.WaitAsync(TimeSpan.FromSeconds(8));
        var retirement = await session.Retirement.WaitAsync(TimeSpan.FromSeconds(8));
        Assert.True(retirement.Retired); return result;
    }
    public void Dispose()
    {
        // Preserve this task's owned DB/media as evidence; the shared historical
        // fixture's Dispose recursively deletes its root, including the MP4.
        try { Engine.Dispose(); Db.Ordinary.Dispose(); Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); }
        finally
        {
            SystemQuery.SetDisplayProvider(null); Environment.SetEnvironmentVariable("AGENT_RECORDER_TEST_MODE", _mode);
            if (_directory is null) DataDirResolver.ClearOverride(); else DataDirResolver.SetOverride(_directory);
        }
    }
    internal sealed class Clock : TimeProvider
    {
        private long _ticks;
        private readonly List<Timer> _timers = new();
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => Interlocked.Read(ref _ticks);
        public override DateTimeOffset GetUtcNow() => At.AddDays(2);
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        { var timer = new Timer(this, callback, state, dueTime); lock (_timers) _timers.Add(timer); return timer; }
        internal void Advance(TimeSpan amount)
        {
            Interlocked.Add(ref _ticks, amount.Ticks); Timer[] timers; lock (_timers) timers = _timers.ToArray();
            foreach (var timer in timers) timer.Fire(GetTimestamp());
        }
        private sealed class Timer(Clock clock, TimerCallback callback, object? state, TimeSpan due) : ITimer
        {
            private long _due = clock.GetTimestamp() + due.Ticks;
            private int _closed;
            internal void Fire(long now) { if (now >= _due && Interlocked.Exchange(ref _closed, 1) == 0) callback(state); }
            public bool Change(TimeSpan dueTime, TimeSpan period) { _due = clock.GetTimestamp() + dueTime.Ticks; return true; }
            public void Dispose() => Interlocked.Exchange(ref _closed, 1);
            public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
        }
    }
}
