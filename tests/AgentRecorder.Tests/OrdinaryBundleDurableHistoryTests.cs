using System.Data;
using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using AgentRecorder.Api;
using AgentRecorder.Capture;
using AgentRecorder.Core;
using AgentRecorder.Infrastructure;
using AgentRecorder.Logging;
using AgentRecorder.Persistence;
using AgentRecorder.Windows;
using Microsoft.Data.Sqlite;
using Xunit;
using Xunit.Abstractions;

namespace AgentRecorder.Tests;

[Collection("NonParallel-AgentRecorderDataDir")]
public sealed class OrdinaryBundleDurableHistoryTests(ITestOutputHelper output)
{
    private sealed class Fixture : IDisposable
    {
        internal readonly string Root = Path.GetFullPath(Path.Combine(TestHelper.ProjectRoot, ".local-data/temp/task329", Guid.NewGuid().ToString("N")));
        internal readonly SqliteOperationalStore Store;
        internal SqliteOrdinaryRecordingHistoryStore Writer;
        internal readonly string? PriorMode, PriorData;
        internal readonly RetentionSharedOutputPathTests.QuietAudit Audit = new();
        internal RecordingEngine? Engine; internal FfmpegCaptureBackend? Backend; internal ApiServer? Api;
        internal readonly Tray Tray = new();
        internal Fixture()
        {
            Directory.CreateDirectory(Root); PriorMode = Environment.GetEnvironmentVariable("AGENT_RECORDER_TEST_MODE"); PriorData = Environment.GetEnvironmentVariable("AGENT_RECORDER_DATA_DIR");
            Environment.SetEnvironmentVariable("AGENT_RECORDER_TEST_MODE", "1"); Environment.SetEnvironmentVariable("AGENT_RECORDER_DATA_DIR", Root); DataDirResolver.SetOverride(Root);
            SystemQuery.SetDisplayTopologyProvider(() => new() { new("display_1", "Synthetic", true, new SystemQuery.Bounds(0,0,1920,1080), 1.0, "synthetic-test-display:display_1", DisplayIdentityResolutionStatus.Resolved) });
            Store = new(Path.Combine(Root, "history.db")); Store.Initialize(); Writer = new(Store); ApiKeyAuth.InitializeForTesting(Root);
        }
        internal OrdinaryRecordingHistorySnapshot Row(string id = "recording")
        {
            var at = DateTimeOffset.UtcNow.AddMinutes(-1);
            return new(id, at, "completed", "display", "display_capture", "Synthetic", 1, "none", null,null,null,null,
                Path.Combine(Root,id+".mp4"),null,null,null,null,null,null,at.AddSeconds(1),at.AddSeconds(2),"duration_reached",100,1);
        }
        internal (OrdinaryRecordingHistorySnapshot Row, OrdinaryRecordingBundleOperation Op) Seed(string id = "recording")
        {
            var row = Row(id); var op = OrdinaryRecordingBundleOperation.Create(id,row.OutputPath); row = row with { BundleRegistration = new(op) }; Writer.Register(row); return (row,op);
        }
        internal void Execute(string sql) { using var c=Store.OpenConnection(); using var cmd=c.CreateCommand();cmd.CommandText=sql;cmd.ExecuteNonQuery(); }
        internal string Scalar(string sql) { using var c=Store.OpenReadOnlyConnection();using var cmd=c.CreateCommand();cmd.CommandText=sql;return Convert.ToString(cmd.ExecuteScalar(),System.Globalization.CultureInfo.InvariantCulture)!; }
        internal string Anchors() => Scalar("SELECT COALESCE(group_concat(recording_id||':'||output_path||':'||output_size_bytes||':'||observation_version||':'||source||':'||hex(volume_id)||':'||hex(file_id)),'') FROM ordinary_publication_anchors;");
        internal UnifiedHistoryQueryService Query => new(Store,Writer);
        internal void StartApi()
        {
            if (Engine is null) { Engine = new RecordingEngine(Audit); Engine.ConfigureOrdinaryRecordingHistory(Writer);Engine.SetTray(Tray); }
            Api = new(Engine,Audit,Tray,null,null,null,null,null,null,null,null,null,0,unifiedHistoryGateway:new SqliteUnifiedHistoryGateway(Query,()=> (UnifiedHistoryTestDatabase.Sid,UnifiedHistoryTestDatabase.Session)));Api.Start();
        }
        internal HttpClient Client(bool auth = true)
        {
            var client=new HttpClient { BaseAddress=new Uri($"http://127.0.0.1:{Api!.BoundPort}"),Timeout=TimeSpan.FromSeconds(5) };
            if(auth)client.DefaultRequestHeaders.Add("X-Agent-Recorder-Key",ApiKeyAuth.CurrentApiKey); return client;
        }
        internal async Task<Recording> Capture(IRecordingBundleGenerator? generator, bool manual = false, bool failMedia = false)
        {
            Engine = new RecordingEngine(Audit,bundleGenerator:generator);Engine.ConfigureOrdinaryRecordingHistory(Writer);Engine.SetTray(Tray);
            Backend = new FfmpegCaptureBackend { VideoInputArgumentsForTests = _ => new[] { "-re","-f","lavfi","-t","1","-i","color=c=black:s=64x64:r=15" } };
            if(failMedia)Backend.VideoOnlyProberForTests=new BadFinalProbe();
            Engine.BackendFactory=_=>(Backend,"ffmpeg"); var first=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);Backend.FirstFrameObserved+=_=>first.TrySetResult();
            Engine.CreateRecording(new JsonObject { ["source"]=new JsonObject { ["type"]="display",["display_id"]="display_1" },["video"]=new JsonObject { ["fps"]=15 },["duration_seconds"]=1,["countdown_seconds"]=0,["output"]=new JsonObject { ["directory"]=Root,["filename"]="capture.mp4",["conflict_policy"]="fail" } },"task329-synthetic",Tray);
            Tray.Confirm!(ConfirmationDecision.Approve());var rec=Assert.Single(Engine._recs.Values);
            if(manual){ await first.Task.WaitAsync(TimeSpan.FromSeconds(5));await Until(()=>rec.State==RecState.recording);Engine.Stop(rec.Id,"user"); }
            await Until(()=>rec.IsFinalized);return rec;
        }
        internal void Reopen()
        {
            Assert.True(Writer.DrainAndDispose(TimeSpan.FromSeconds(3)));var reopened=new SqliteOperationalStore(Store.DatabasePath);reopened.Initialize();Writer=new(reopened);
        }
        public void Dispose()
        {
            Api?.Stop();Engine?.Dispose();Backend?.Dispose();Writer.Dispose();SqliteConnection.ClearAllPools();SystemQuery.SetDisplayTopologyProvider(null);DataDirResolver.ClearOverride();
            Environment.SetEnvironmentVariable("AGENT_RECORDER_TEST_MODE",PriorMode);Environment.SetEnvironmentVariable("AGENT_RECORDER_DATA_DIR",PriorData);
            Assert.Equal(Path.GetFullPath(Path.Combine(TestHelper.ProjectRoot,".local-data/temp/task329")),Path.GetDirectoryName(Root));Directory.Delete(Root,true);
        }
    }
    private sealed class Tray : ITrayContext
    {
        internal Action<ConfirmationDecision>? Confirm;public string HostMode=>"tray";public bool SupportsRegionSelectionUi=>false;
        public void RequestConfirmation(RecordingConfirmationPresentation p,Action<ConfirmationDecision> c)=>Confirm=c;
        public void RequestRegionSelection(int t,Action<string,int,int,int,int,string,string> c)=>throw new InvalidOperationException();
        public void SetRecording(RecordingUiPresentation r){} public void SetIdle(RecordingUiPresentation r){} public void SetAllIdle(){} public void ShowError(string s){}
    }
    private sealed class BadFinalProbe : IOutputProber
    {
        private int calls;public OutputMeta Probe(string p){var m=FfmpegCaptureBackend.Probe(p);if(Interlocked.Increment(ref calls)==2)m.DurationSeconds=0;return m;}
    }
    private sealed class Generator(Func<RecordingBundleRequest,Task<RecordingBundleGenerationResult>> generate) : IRecordingBundleGenerator
    {
        internal int Calls;internal readonly TaskCompletionSource<RecordingBundleRequest> Started=new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<RecordingBundleGenerationResult> GenerateAsync(RecordingBundleRequest request,CancellationToken token=default)
        { Interlocked.Increment(ref Calls);Started.TrySetResult(request);return generate(request); }
    }
    private static OrdinaryRecordingBundleUpdate Ready(OrdinaryRecordingBundleOperation op)=>new(op,new("ready",op.BundlePath,null));
    private static async Task Until(Func<bool> predicate,int seconds=8)
    {
        var deadline=DateTime.UtcNow.AddSeconds(seconds);while(!predicate()&&DateTime.UtcNow<deadline)await Task.Delay(10);Assert.True(predicate(),"Bounded asynchronous condition did not settle.");
    }
    private static async Task<JsonElement> Get(HttpClient c,string path)
    { using var response=await c.GetAsync(path);Assert.Equal(HttpStatusCode.OK,response.StatusCode);using var doc=JsonDocument.Parse(await response.Content.ReadAsStringAsync());return doc.RootElement.GetProperty("data").Clone(); }

    [Theory]
    [InlineData(false,false)] [InlineData(true,false)] [InlineData(false,true)] [InlineData(true,true)]
    public async Task FormalCoreGeneratorWriterApisAndReopenKeepMediaAnchorIndependent(bool manual,bool failed)
    {
        using var f=new Fixture();var release=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var real=new FfmpegRecordingBundleGenerator();var gen=new Generator(async r=>{await release.Task;return failed?RecordingBundleGenerationResult.Failed(RecordingBundleErrorCodes.FrameExtractFailed,"secret-stderr-api-key"):await real.GenerateAsync(r);});
        var rec=await f.Capture(gen,manual);await gen.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var generating=f.Writer.Read(rec.Id)!;Assert.Equal("completed",generating.Status);Assert.Equal("generating",generating.Bundle!.Status);var anchors=f.Anchors();Assert.NotEmpty(anchors);Assert.Equal("1",f.Scalar("SELECT COUNT(*) FROM ordinary_publication_anchors;"));
        release.TrySetResult();await Until(()=>f.Writer.Read(rec.Id)?.Bundle?.Status==(failed?"failed":"ready"));var settled=f.Writer.Read(rec.Id)!;
        Assert.Equal(generating with { Bundle=null },settled with { Bundle=null });Assert.Equal(anchors,f.Anchors());Assert.Equal(1,gen.Calls);Assert.Equal(RecState.completed,rec.State);Assert.NotNull(rec.LastMeta!.PublicationReceipt);
        Assert.Equal(failed?RecordingBundleErrorCodes.FrameExtractFailed:null,settled.Bundle!.ErrorCode);
        if(!failed)Assert.Equal(new[]{"first_frame.png","last_frame.png","marks.json","metadata.json","thumbnail.jpg"},Directory.GetFiles(settled.Bundle.Path!).Select(Path.GetFileName).OrderBy(n=>n));
        f.StartApi();using var client=f.Client();using var anonymous=f.Client(false);Assert.Equal(HttpStatusCode.Unauthorized,(await anonymous.GetAsync("/api/v1/recording-history")).StatusCode);
        foreach(var path in new[]{"/api/v1/recording-history","/api/v1/recording-history/"+rec.Id,"/api/v1/history?source=ordinary"})
        {
            var data=await Get(client,path);var row=path.Contains("/recording-history/")?data.GetProperty("recording"):data.GetProperty("items")[0];var bundle=row.GetProperty("bundle");
            Assert.Equal(failed?"failed":"ready",bundle.GetProperty("status").GetString());Assert.Equal(settled.Bundle.Path,bundle.GetProperty("path").GetString());Assert.Equal(5,bundle.EnumerateObject().Count());Assert.DoesNotContain("secret-stderr",bundle.ToString());Assert.DoesNotContain("operation",bundle.ToString());
        }
        f.Api!.Stop();f.Api=null;f.Engine!.Dispose();f.Engine=null;f.Reopen();Assert.Equal(settled.Bundle,f.Writer.Read(rec.Id)!.Bundle);Assert.Equal(anchors,f.Anchors());
        if(!failed){Directory.Delete(settled.Bundle.Path!,true);Directory.CreateDirectory(settled.Bundle.Path!);File.WriteAllText(Path.Combine(settled.Bundle.Path!,"unrelated.txt"),"replacement");Assert.Equal("ready",f.Writer.Read(rec.Id)!.Bundle!.Status);}
        output.WriteLine($"Formal lavfi FFmpeg -> Core -> {(failed?"fake generation fault":"actual FfmpegRecordingBundleGenerator five files")} -> writer -> SQLite -> three APIs -> reopen: media completed, Bundle {settled.Bundle.Status}, native media anchor unchanged; manual={manual}.");
    }

    [Fact]
    public async Task FastResultWaitsBehindAtomicGeneratingAndLateMediaReplayCannotResetReady()
    {
        using var f=new Fixture();using var mediaRelease=new ManualResetEventSlim();using var bundleRelease=new ManualResetEventSlim();var mediaEntered=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);var bundleEntered=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        f.Writer.BeforePersistForTests=s=>{if(s.Status=="completed"){mediaEntered.TrySetResult();Assert.True(mediaRelease.Wait(TimeSpan.FromSeconds(5)));}};
        f.Writer.BeforeBundlePersistForTests=_=>{bundleEntered.TrySetResult();Assert.True(bundleRelease.Wait(TimeSpan.FromSeconds(5)));};
        var gen=new Generator(r=>Task.FromResult(RecordingBundleGenerationResult.Ready(OrdinaryRecordingBundleOperation.DerivePath(r.MediaPath))));
        var rec=await f.Capture(gen);
        try
        {
            await mediaEntered.Task.WaitAsync(TimeSpan.FromSeconds(3));await Until(()=>rec.BundleSnapshot.Status=="ready");Assert.Equal("0",f.Scalar("SELECT COUNT(*) FROM ordinary_recording_bundles;"));Assert.Equal("0",f.Scalar("SELECT COUNT(*) FROM ordinary_publication_anchors;"));Assert.True(rec.IsFinalized);
            mediaRelease.Set();await bundleEntered.Task.WaitAsync(TimeSpan.FromSeconds(3));Assert.Equal("completed",f.Scalar("SELECT status_code FROM ordinary_recording_history;"));Assert.Equal("generating",f.Scalar("SELECT status_code FROM ordinary_recording_bundles;"));Assert.Equal("1",f.Scalar("SELECT COUNT(*) FROM ordinary_publication_anchors;"));Assert.Throws<OrdinaryRecordingHistoryUnavailableException>(()=>f.Writer.Read(rec.Id));
        }
        finally {mediaRelease.Set();bundleRelease.Set();}
        await Until(()=>f.Writer.Read(rec.Id)?.Bundle?.Status=="ready");var row=f.Writer.Read(rec.Id)!;
        var replay=row with { PublicationReceipt=rec.LastMeta!.PublicationReceipt,BundleRegistration=new(rec.OrdinaryBundleOperation) };Assert.True(f.Writer.TryPublish(replay));Assert.Equal("ready",f.Writer.Read(rec.Id)!.Bundle!.Status);Assert.Equal(1,gen.Calls);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task MediaFailureOrNoGeneratorRecordsActualNotApplicableWithoutGeneration(bool mediaFailed)
    {
        using var f=new Fixture();var gen=new Generator(_=>throw new InvalidOperationException("must not generate"));var rec=await f.Capture(mediaFailed?gen:null,failMedia:mediaFailed);
        Assert.Equal(mediaFailed?RecState.failed:RecState.completed,rec.State);Assert.Equal("not_applicable",f.Writer.Read(rec.Id)!.Bundle!.Status);Assert.Equal(0,gen.Calls);f.Reopen();Assert.Equal("not_applicable",f.Writer.Read(rec.Id)!.Bundle!.Status);
    }

    [Theory]
    [InlineData("recording")] [InlineData("operation")] [InlineData("media")] [InlineData("path")] [InlineData("error")] [InlineData("status")]
    public void InvalidBindingsAndResultPayloadCannotMakeReady(string fault)
    {
        using var f=new Fixture();var (row,op)=f.Seed();var before=f.Writer.Read(row.RecordingId)!;var wrong=op;
        if(fault=="recording")wrong=op with { RecordingId="unregistered" };
        if(fault=="operation")wrong=op with { OperationId=Guid.NewGuid().ToString("N") };
        if(fault=="media")wrong=OrdinaryRecordingBundleOperation.Create(op.RecordingId,Path.Combine(f.Root,"different.mp4"));
        var update=Ready(wrong);
        if(fault=="path")update=update with { Result=update.Result with { Path=Path.Combine(f.Root,"unapproved.bundle") } };
        if(fault=="error")update=update with { Result=update.Result with { Status="failed",ErrorCode="api-key-secret-exception" } };
        if(fault=="status")update=update with { Result=update.Result with { Status="generating" } };
        Assert.Equal(fault is "recording" or "operation" or "media",f.Writer.TryPublishBundle(update));Assert.Equal(before,f.Writer.Read(row.RecordingId));
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void FirstLegalTerminalOnlyAcceptsIdenticalReplayAndNeverMutatesMedia(bool failedFirst)
    {
        using var f=new Fixture();var (row,op)=f.Seed();var initial=f.Writer.Read(row.RecordingId)!;
        var first=failedFirst?new OrdinaryRecordingBundleUpdate(op,new("failed",op.BundlePath,RecordingBundleErrorCodes.PublishFailed)):Ready(op);
        Assert.True(f.Writer.TryPublishBundle(first));var winner=f.Writer.Read(row.RecordingId)!;Assert.Equal(first.Result,winner.Bundle);
        Assert.True(f.Writer.TryPublishBundle(first));Assert.Equal(winner,f.Writer.Read(row.RecordingId));
        var conflicting=failedFirst?Ready(op):new OrdinaryRecordingBundleUpdate(op,new("failed",op.BundlePath,RecordingBundleErrorCodes.HashFailed));
        Assert.True(f.Writer.TryPublishBundle(conflicting));Assert.Equal(winner,f.Writer.Read(row.RecordingId));
        Assert.True(f.Writer.TryPublish(row));Assert.Equal(winner,f.Writer.Read(row.RecordingId));
        Assert.True(f.Writer.TryPublish(row with { OutputSizeBytes=999 }));Assert.Equal(winner,f.Writer.Read(row.RecordingId));Assert.Equal(initial with { Bundle=null },winner with { Bundle=null });
    }

    [Fact]
    public void StartupRecoveryRevokesOldOperationAndDoesNotInterruptLiveSameWriter()
    {
        using var f=new Fixture();var (row,op)=f.Seed();f.Writer.RecoverIncomplete(DateTimeOffset.UtcNow);Assert.Equal("generating",f.Writer.Read(row.RecordingId)!.Bundle!.Status);
        var files=Directory.GetFiles(f.Root).OrderBy(p=>p).ToArray();var anchors=f.Anchors();f.Reopen();f.Writer.RecoverIncomplete(DateTimeOffset.UtcNow);
        var recovered=f.Writer.Read(row.RecordingId)!;Assert.Equal("completed",recovered.Status);Assert.Equal("interrupted_unknown",recovered.Bundle!.Status);Assert.Equal("bundle_interrupted_unknown",recovered.Bundle.ErrorCode);Assert.Equal(op.BundlePath,recovered.Bundle.Path);
        Assert.Equal("",f.Scalar("SELECT COALESCE(operation_id,'') FROM ordinary_recording_bundles;"));f.Writer.RecoverIncomplete(DateTimeOffset.UtcNow.AddSeconds(1));Assert.Equal(recovered,f.Writer.Read(row.RecordingId));
        Assert.True(f.Writer.TryPublishBundle(Ready(op)));Assert.Equal(recovered,f.Writer.Read(row.RecordingId));Assert.Equal(anchors,f.Anchors());Assert.False(Directory.Exists(op.BundlePath));
        Assert.DoesNotContain(Directory.GetFiles(f.Root),p=>p.EndsWith(".mp4",StringComparison.Ordinal));
    }

    [Fact]
    public async Task BundleWriteFailureCannotBeClearedByCompletedMediaReplayAndAllApisReturn503()
    {
        using var f=new Fixture();var (row,op)=f.Seed();var attempts=0;f.Writer.BeforeBundlePersistForTests=_=>{Interlocked.Increment(ref attempts);throw new IOException("private injected failure");};
        Assert.True(f.Writer.TryPublishBundle(Ready(op)));Assert.Throws<OrdinaryRecordingHistoryUnavailableException>(()=>f.Writer.Read(row.RecordingId));Assert.Equal(2,attempts);Assert.Equal("generating",f.Scalar("SELECT status_code FROM ordinary_recording_bundles;"));
        Assert.True(f.Writer.TryPublish(row));Assert.Throws<OrdinaryRecordingHistoryUnavailableException>(()=>f.Writer.Read(row.RecordingId));f.StartApi();using var c=f.Client();
        foreach(var path in new[]{"/api/v1/recording-history","/api/v1/recording-history/"+row.RecordingId,"/api/v1/history?source=ordinary"})
        {using var response=await c.GetAsync(path);Assert.Equal(HttpStatusCode.ServiceUnavailable,response.StatusCode);Assert.DoesNotContain("private injected",await response.Content.ReadAsStringAsync());}
        Assert.False(f.Writer.DrainAndDispose(TimeSpan.FromSeconds(3)));Assert.Throws<OrdinaryRecordingHistoryUnavailableException>(()=>f.Writer.Read(row.RecordingId));
        f.Api!.Stop();f.Api=null;f.Engine!.Dispose();f.Engine=null;f.Writer=new(f.Store);f.Writer.RecoverIncomplete(DateTimeOffset.UtcNow);Assert.Equal("interrupted_unknown",f.Writer.Read(row.RecordingId)!.Bundle!.Status);
    }

    [Fact]
    public void MatchingResultRepairClearsFailureButConflictingLostResultsDoNot()
    {
        using var f=new Fixture();var (row,op)=f.Seed();f.Writer.BeforeBundlePersistForTests=_=>throw new IOException();Assert.True(f.Writer.TryPublishBundle(Ready(op)));Assert.Throws<OrdinaryRecordingHistoryUnavailableException>(()=>f.Writer.Read(row.RecordingId));
        f.Writer.BeforeBundlePersistForTests=null;Assert.True(f.Writer.TryPublishBundle(Ready(op)));Assert.Equal("ready",f.Writer.Read(row.RecordingId)!.Bundle!.Status);
        var (other,otherOp)=f.Seed("other");f.Writer.BeforeBundlePersistForTests=_=>throw new IOException();Assert.True(f.Writer.TryPublishBundle(Ready(otherOp)));Assert.Throws<OrdinaryRecordingHistoryUnavailableException>(()=>f.Writer.Read(other.RecordingId));
        Assert.True(f.Writer.TryPublishBundle(new(otherOp,new("failed",otherOp.BundlePath,RecordingBundleErrorCodes.HashFailed))));Assert.Throws<OrdinaryRecordingHistoryUnavailableException>(()=>f.Writer.Read(other.RecordingId));
        f.Writer.BeforeBundlePersistForTests=null;Assert.True(f.Writer.TryPublishBundle(Ready(otherOp)));Assert.Throws<OrdinaryRecordingHistoryUnavailableException>(()=>f.Writer.Read(other.RecordingId));Assert.False(f.Writer.DrainAndDispose(TimeSpan.FromSeconds(3)));
    }

    [Fact]
    public async Task ShutdownAndResultCompetitionIsBoundedAndReportsUndrainedWrites()
    {
        using var f=new Fixture();var (row,op)=f.Seed();using var release=new ManualResetEventSlim();var entered=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        f.Writer.BeforeBundlePersistForTests=_=>{entered.TrySetResult();Assert.True(release.Wait(TimeSpan.FromSeconds(5)));};Assert.True(f.Writer.TryPublishBundle(Ready(op)));await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        try {Assert.False(f.Writer.DrainAndDispose(TimeSpan.FromMilliseconds(20)));Assert.Throws<OrdinaryRecordingHistoryUnavailableException>(()=>f.Writer.Read(row.RecordingId));}
        finally {release.Set();}
        Assert.True(f.Writer.DrainAndDispose(TimeSpan.FromSeconds(3)));Assert.Equal("ready",f.Writer.Read(row.RecordingId)!.Bundle!.Status);
        Assert.False(f.Writer.TryPublishBundle(Ready(op)));Assert.Throws<OrdinaryRecordingHistoryUnavailableException>(()=>f.Writer.Read(row.RecordingId));f.Writer=new(f.Store);Assert.Equal("ready",f.Writer.Read(row.RecordingId)!.Bundle!.Status);
    }

    [Fact]
    public async Task ConcurrentBundleCommitUsesSameUnifiedSnapshotAndBoundedCandidates()
    {
        using var f=new Fixture();var (row,op)=f.Seed();var query=f.Query;var committed=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);f.Writer.AfterBundlePersistForTests=(_,settled)=>{Assert.True(settled);committed.TrySetResult();};
        var entered=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);using var release=new ManualResetEventSlim();query.SnapshotEstablishedForTests=()=>{entered.TrySetResult();Assert.True(release.Wait(TimeSpan.FromSeconds(5)));};var selected=0;query.CandidatesSelectedForTests=(_,n)=>selected+=n;
        var reading=Task.Run(()=>query.GetPage(UnifiedHistoryTestDatabase.Sid,UnifiedHistoryTestDatabase.Session,1,new("ordinary")));await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        try {Assert.True(f.Writer.TryPublishBundle(Ready(op)));await committed.Task.WaitAsync(TimeSpan.FromSeconds(3));}
        finally {release.Set();}
        Assert.Equal("generating",Assert.Single((await reading).Items).Bundle!.Status);Assert.True(selected<=2);query.SnapshotEstablishedForTests=null;Assert.Equal("ready",Assert.Single(query.GetPage(UnifiedHistoryTestDatabase.Sid,UnifiedHistoryTestDatabase.Session,1,new("ordinary")).Items).Bundle!.Status);
        using var c=f.Store.OpenReadOnlyConnection();using var cmd=c.CreateCommand();cmd.CommandText="EXPLAIN QUERY PLAN SELECT * FROM ordinary_recording_bundles WHERE recording_id='recording';";using var r=cmd.ExecuteReader();var plan=new List<string>();while(r.Read())plan.Add(r.GetString(3));Assert.Contains(plan,s=>s.Contains("USING INDEX",StringComparison.Ordinal));
    }

    [Fact]
    public void V30FixedMigrationsUpgradeWithoutInferringLegacyFactsAndRepeatInitializeIsIdempotent()
    {
        using var f=new Fixture();var path=Path.Combine(f.Root,"v30.db");
        using(var c=new SqliteConnection($"Data Source={path};Pooling=False"))
        {
            c.Open();SqliteOutputPathCollation.Register(c);using var cmd=c.CreateCommand();foreach(var m in SqliteSchemaCatalog.Migrations.Where(m=>m.Version<=30))
            {cmd.CommandText=m.Definition;cmd.ExecuteNonQuery();cmd.CommandText="INSERT INTO schema_migrations VALUES($v,$n,$c,$at);";cmd.Parameters.AddWithValue("$v",m.Version);cmd.Parameters.AddWithValue("$n",m.Name);cmd.Parameters.AddWithValue("$c",m.Checksum);cmd.Parameters.AddWithValue("$at",1000+m.Version);cmd.ExecuteNonQuery();cmd.Parameters.Clear();}
            cmd.CommandText="SELECT MAX(version) FROM schema_migrations;";Assert.Equal(30L,cmd.ExecuteScalar());
        }
        var oldReceipts=UnifiedHistoryProfileFilterTests.RawSnapshot(path,schemaThrough:30);var store=new SqliteOperationalStore(path);store.Initialize();store.Initialize();Assert.Equal(44,SqliteOperationalStore.CurrentSchemaVersion);Assert.Equal(oldReceipts,UnifiedHistoryProfileFilterTests.RawSnapshot(path,schemaThrough:30));
        using var reader=new SqliteOrdinaryRecordingHistoryStore(store);var legacy=f.Row("legacy");reader.Register(legacy);Assert.Null(reader.Read("legacy")!.Bundle);Assert.Null(Assert.Single(reader.ReadPage(1,null).Items).Bundle);Assert.Null(Assert.Single(new UnifiedHistoryQueryService(store,reader).GetPage(UnifiedHistoryTestDatabase.Sid,UnifiedHistoryTestDatabase.Session,1,new("ordinary")).Items).Bundle);
    }

    [Theory]
    [InlineData("schema")] [InlineData("receipt")]
    public void WrongNewSchemaOrMigrationReceiptFailClosed(string fault)
    {
        using var f=new Fixture();f.Execute(fault=="schema"?"ALTER TABLE ordinary_recording_bundles ADD COLUMN unexpected TEXT;":"UPDATE schema_migrations SET definition_checksum='ffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff' WHERE version=31;");if(fault=="schema") { Assert.Throws<InvalidDataException>(()=>f.Store.Initialize());Assert.Throws<OrdinaryRecordingHistoryUnavailableException>(()=>f.Writer.ReadPage(1,null)); }
        else { Assert.Throws<SqliteOperationalStoreException>(()=>f.Store.Initialize());Assert.Throws<SqliteOperationalStoreException>(()=>f.Writer.ReadPage(1,null)); }
    }

    [Theory]
    [InlineData("bundle_insert")] [InlineData("anchor_insert")]
    public async Task BundleRegistrationAndPublicationAnchorRollbackWithMediaTerminal(string fault)
    {
        using var f=new Fixture();f.Execute(fault=="bundle_insert"?"CREATE TRIGGER fail_insert BEFORE INSERT ON ordinary_recording_bundles BEGIN SELECT RAISE(ABORT,'bundle insert fault');END;":"CREATE TRIGGER fail_insert BEFORE INSERT ON ordinary_publication_anchors BEGIN SELECT RAISE(ABORT,'anchor insert fault');END;");
        var gen=new Generator(r=>Task.FromResult(RecordingBundleGenerationResult.Ready(OrdinaryRecordingBundleOperation.DerivePath(r.MediaPath))));var rec=await f.Capture(gen);await Until(()=>rec.BundleSnapshot.Status=="ready");Assert.Equal(RecState.completed,rec.State);Assert.Throws<OrdinaryRecordingHistoryUnavailableException>(()=>f.Writer.Read(rec.Id));Assert.Equal("0",f.Scalar("SELECT COUNT(*) FROM ordinary_recording_bundles;"));Assert.Equal("0",f.Scalar("SELECT COUNT(*) FROM ordinary_publication_anchors;"));Assert.NotEqual("completed",f.Scalar("SELECT status_code FROM ordinary_recording_history;"));
        Assert.False(f.Writer.DrainAndDispose(TimeSpan.FromSeconds(3)));Assert.Throws<OrdinaryRecordingHistoryUnavailableException>(()=>f.Writer.Read(rec.Id));
    }

    [Fact]
    public void MediaRegistrationRepairCannotClearItsEarlierLostBundleResult()
    {
        using var f = new Fixture();
        var row = f.Row();
        var op = OrdinaryRecordingBundleOperation.Create(row.RecordingId, row.OutputPath);
        f.Writer.Register(row with { Status = "pending_confirmation", StartedAtUtc = null,
            CompletedAtUtc = null, ReasonCode = null, OutputSizeBytes = null, OutputDurationSeconds = null });
        row = row with { BundleRegistration = new(op) };
        f.Execute("CREATE TRIGGER fail_bundle BEFORE INSERT ON ordinary_recording_bundles BEGIN SELECT RAISE(ABORT,'injected'); END;");
        Assert.True(f.Writer.TryPublish(row));
        Assert.True(f.Writer.TryPublishBundle(Ready(op)));
        Assert.Throws<OrdinaryRecordingHistoryUnavailableException>(() => f.Writer.Read(row.RecordingId));
        f.Execute("DROP TRIGGER fail_bundle;");
        Assert.True(f.Writer.TryPublish(row));
        Assert.Throws<OrdinaryRecordingHistoryUnavailableException>(() => f.Writer.Read(row.RecordingId));
        Assert.Equal("completed", f.Scalar("SELECT status_code FROM ordinary_recording_history;"));
        Assert.Equal("generating", f.Scalar("SELECT status_code FROM ordinary_recording_bundles;"));
        Assert.True(f.Writer.TryPublishBundle(Ready(op)));
        Assert.Equal("ready", f.Writer.Read(row.RecordingId)!.Bundle!.Status);
        Assert.True(f.Writer.DrainAndDispose(TimeSpan.FromSeconds(3)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FormalCoreRejectsUnfrozenSuccessPathAndUnstableFailureCode(bool wrongSuccessPath)
    {
        using var f = new Fixture();
        var gen = new Generator(r => Task.FromResult(wrongSuccessPath
            ? RecordingBundleGenerationResult.Ready(Path.Combine(f.Root, "unregistered.bundle"))
            : RecordingBundleGenerationResult.Failed("private-unbounded-error", "secret-stderr")));
        var rec = await f.Capture(gen);
        await Until(() => f.Writer.Read(rec.Id)?.Bundle?.Status == "failed");
        var history = f.Writer.Read(rec.Id)!;
        Assert.Equal("completed", history.Status);
        Assert.Equal(RecState.completed, rec.State);
        Assert.Equal(OrdinaryRecordingBundleOperation.DerivePath(rec.OutputPath), history.Bundle!.Path);
        Assert.Equal(RecordingBundleErrorCodes.GenerationFailed, history.Bundle.ErrorCode);
        Assert.Equal("failed", rec.BundleSnapshot.Status);
        Assert.Equal(1, gen.Calls);
        Assert.NotEmpty(f.Anchors());
        f.Reopen();
        Assert.Equal(history.Bundle, f.Writer.Read(rec.Id)!.Bundle);
    }

    [Fact]
    public async Task BundleQueueCapacityIsBoundedAndLostResultNeedsMatchingReplay()
    {
        using var f = new Fixture();
        var (row, op) = f.Seed();
        using var release = new ManualResetEventSlim();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        f.Writer.BeforeBundlePersistForTests = _ =>
        {
            entered.TrySetResult();
            Assert.True(release.Wait(TimeSpan.FromSeconds(5)));
        };
        var update = Ready(op);
        Assert.True(f.Writer.TryPublishBundle(update));
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        try
        {
            for (var i = 0; i < 128; i++) Assert.True(f.Writer.TryPublishBundle(update));
            Assert.False(f.Writer.TryPublishBundle(update));
            Assert.Throws<OrdinaryRecordingHistoryUnavailableException>(() => f.Writer.Read(row.RecordingId));
        }
        finally { release.Set(); }
        f.Writer.BeforeBundlePersistForTests = null;
        await Until(() => f.Scalar("SELECT status_code FROM ordinary_recording_bundles;") == "ready");
        Assert.Throws<OrdinaryRecordingHistoryUnavailableException>(() => f.Writer.Read(row.RecordingId));
        Assert.True(f.Writer.TryPublish(row));
        Assert.Throws<OrdinaryRecordingHistoryUnavailableException>(() => f.Writer.Read(row.RecordingId));
        Assert.True(f.Writer.TryPublishBundle(update));
        Assert.Equal("ready", f.Writer.Read(row.RecordingId)!.Bundle!.Status);
        Assert.True(f.Writer.DrainAndDispose(TimeSpan.FromSeconds(3)));
    }

    [Fact]
    public async Task OldRowsAndOtherUnifiedSourcesHaveNullBundleWithoutDirectoryInference()
    {
        using var f=new Fixture();using var d=new UnifiedHistoryTestDatabase(Path.Combine(f.Root,"mixed"));d.SeedOrdinary("legacy",completed:true);d.SeedPlan("plan");d.SeedOccurrence("plan","occ");d.SeedFuture("future");Directory.CreateDirectory(Path.Combine(d.Root,"legacy.bundle"));File.WriteAllText(Path.Combine(d.Root,"legacy.bundle","metadata.json"),"unrelated");var entries=d.Page().Items;Assert.Equal(3,entries.Count);Assert.All(entries,e=>Assert.Null(e.Bundle));
        var (row,op)=f.Seed();Assert.True(f.Writer.TryPublishBundle(Ready(op) with { Result=new("ready",op.BundlePath,null) }));Assert.Equal("ready",f.Writer.Read(row.RecordingId)!.Bundle!.Status);f.StartApi();using var client=f.Client();using var denied=f.Client(false);Assert.Equal(HttpStatusCode.Unauthorized,(await denied.GetAsync("/api/v1/history")).StatusCode);Assert.Equal("ready",(await Get(client,"/api/v1/history?source=ordinary")).GetProperty("items")[0].GetProperty("bundle").GetProperty("status").GetString());
    }
}
