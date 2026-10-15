using AgentRecorder.Core;
using AgentRecorder.Capture;
using AgentRecorder.Infrastructure;
using AgentRecorder.Persistence;
using AgentRecorder.Windows;
using Xunit;
using static AgentRecorder.Tests.UnifiedHistoryTestDatabase;

namespace AgentRecorder.Tests;

internal sealed class LocalRetentionApprovalFixture : IDisposable
{
    internal readonly UnifiedHistoryTestDatabase Db = new(Path.Combine(TestHelper.ProjectRoot, ".local-data/temp/task358", Guid.NewGuid().ToString("N")));
    internal readonly RecordingEngine Engine;
    internal readonly OriginalFileScopeFixture Files = new();
    internal readonly RetentionGuardAdmission DatabaseAdmission = new();
    internal readonly RetentionComposedAdmission ComposedAdmission = new();
    internal readonly LocalRetentionAdmission Admission = new();
    internal readonly Ui Adapter = new();
    internal readonly Clock Time = new();
    internal string Videos => Path.Combine(Db.Root, "Videos");
    internal string Media => Path.Combine(Videos, "本次原始视频.mp4");
    internal readonly OrdinaryRecordingHistorySnapshot Row;
    private readonly bool _native;
    private readonly string? _mode = Environment.GetEnvironmentVariable("AGENT_RECORDER_TEST_MODE");
    private readonly string? _directory = DataDirResolver.HasOverride ? DataDirResolver.Resolve() : null;
    internal LocalRetentionApprovalFixture(bool native = false)
    {
        _native = native; Environment.SetEnvironmentVariable("AGENT_RECORDER_TEST_MODE", "1"); DataDirResolver.SetOverride(Db.Root);
        SystemQuery.SetDisplayProvider(() => new() { new("display_1", "Synthetic", true, new(0, 0, 1920, 1080), 1.0) });
        Engine = new(new RetentionSharedOutputPathTests.QuietAudit()); Engine.ConfigureOrdinaryRecordingHistory(Db.Ordinary);
        Directory.CreateDirectory(Videos); File.WriteAllBytes(Media, Enumerable.Range(0,12).Select(i => (byte)i).ToArray());
        var identity = native ? WindowsReadOnlyFileMetadataProbe.Instance.Observe(Media).Before!.Identity! : new ObservedFileIdentity(123,456,789);
        Row = Db.Recording("candidate", completed: true) with { OutputPath = Media, BundleRegistration = new(null), PublicationReceipt = PublicationReceipt.IssueForTests(Media,12,identity) };
        Db.Ordinary.Register(Row); Files.Root = Videos; Files.Anchor = Row.PublicationReceipt.Anchor; Files.Api.Anchor = Files.Anchor;
    }
    internal LocalRetentionApproval Service(string sid = Sid, string binding = Session, string? root = null,
        SqliteOperationalStore? store = null, SqliteOrdinaryRecordingHistoryStore? writer = null, RetentionGuardDatabase? database = null,
        ILocalRetentionApprovalUi? ui = null, CancellationToken hostStopping = default)
        => new(Engine, store ?? Db.Store, writer ?? Db.Ordinary, sid, binding, root ?? Videos, ui ?? Adapter, Time, Admission,
            _native ? null : Files.Api, Files.Admission, DatabaseAdmission, ComposedAdmission, database, hostStopping);
    internal async Task<LocalRetentionApproval.Permit> Approve(LocalRetentionApproval service, LocalRetentionApproval.Session? session = null)
    {
        session ??= service.Request(Row.RecordingId); await Adapter.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Adapter.Decide(LocalRetentionUiChoice.Approve); Adapter.Retire.TrySetResult(true);
        var response = await session.Response.WaitAsync(TimeSpan.FromSeconds(5)); Assert.Equal(LocalRetentionState.Approved,response.State); return response.Permit!;
    }
    public void Dispose()
    {
        try { Engine.Dispose(); Db.Ordinary.Dispose(); Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); Assert.Equal(Enumerable.Range(0,12).Select(i => (byte)i), File.ReadAllBytes(Media)); }
        finally { SystemQuery.SetDisplayProvider(null); Environment.SetEnvironmentVariable("AGENT_RECORDER_TEST_MODE",_mode); if(_directory is null) DataDirResolver.ClearOverride(); else DataDirResolver.SetOverride(_directory); }
    }
    internal sealed class Ui : ILocalRetentionApprovalUi
    {
        internal readonly TaskCompletionSource Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource<LocalRetentionDecision> Result = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource<bool> Retire = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal LocalRetentionIntent Intent = null!;
        internal CancellationToken Token;
        internal Action? OnBegin;
        internal int Begins;
        public LocalRetentionUiSession Begin(LocalRetentionIntent intent,CancellationToken cancellation)
        { Intent=intent; Token=cancellation; Begins++; OnBegin?.Invoke(); Entered.TrySetResult(); return new(Result.Task,Retire.Task); }
        internal bool Decide(LocalRetentionUiChoice choice,LocalRetentionIntent? intent=null) => Result.TrySetResult(new(intent ?? Intent,choice));
    }
    internal sealed class Clock : TimeProvider
    {
        private long _ticks, _utc;
        private readonly List<Alarm> _alarms = new();
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => Interlocked.Read(ref _ticks);
        public override DateTimeOffset GetUtcNow() => At.AddDays(2).AddTicks(Interlocked.Read(ref _utc));
        public override ITimer CreateTimer(TimerCallback callback,object? state,TimeSpan dueTime,TimeSpan period)
        { var a=new Alarm(this,callback,state,dueTime);lock(_alarms)_alarms.Add(a);return a; }
        internal void Advance(double seconds) { ShiftUtc(seconds); Interlocked.Add(ref _ticks,TimeSpan.FromSeconds(seconds).Ticks);Alarm[] all;lock(_alarms)all=_alarms.ToArray();foreach(var a in all)a.Fire(GetTimestamp()); }
        internal void ShiftUtc(double seconds)=>Interlocked.Add(ref _utc,TimeSpan.FromSeconds(seconds).Ticks);
        private sealed class Alarm(Clock clock,TimerCallback callback,object? state,TimeSpan due) : ITimer
        {
            private long _due=clock.GetTimestamp()+due.Ticks;private int _closed;
            internal void Fire(long now){if(now>=_due&&Interlocked.Exchange(ref _closed,1)==0)callback(state);}
            public bool Change(TimeSpan dueTime,TimeSpan period){_due=clock.GetTimestamp()+dueTime.Ticks;return true;}
            public void Dispose()=>Interlocked.Exchange(ref _closed,1);
            public ValueTask DisposeAsync(){Dispose();return ValueTask.CompletedTask;}
        }
    }
}
