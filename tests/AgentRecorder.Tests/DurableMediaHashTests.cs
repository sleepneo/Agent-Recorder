using System.Diagnostics;
using System.Net;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AgentRecorder.Api;
using AgentRecorder.Capture;
using AgentRecorder.Core;
using AgentRecorder.Infrastructure;
using AgentRecorder.Persistence;
using AgentRecorder.Windows;
using Microsoft.Data.Sqlite;
using Microsoft.Win32.SafeHandles;
using Xunit;
using Xunit.Abstractions;
using static AgentRecorder.Tests.UnifiedHistoryTestDatabase;

namespace AgentRecorder.Tests;

[Collection("NonParallel-AgentRecorderDataDir")]
public sealed class DurableMediaHashTests(ITestOutputHelper output)
{
    private sealed class Fixture : IDisposable
    {
        internal readonly string Root = NewRoot();
        internal readonly SqliteOperationalStore Store;
        internal SqliteOrdinaryRecordingHistoryStore Writer;
        internal readonly OrdinaryRecordingBundleOperation Op;
        internal readonly RecordingBundleRequest Request;
        internal readonly PublicationReceipt Receipt;
        internal readonly FfmpegRecordingBundleGenerator Generator = new(new Frames(), ffmpegPathProvider: () => "owned-fake-ffmpeg.exe");
        internal Fixture(bool anchor = true, bool upperReceiptPath = false)
        {
            Directory.CreateDirectory(Path.Combine(Root,"Videos")); var media = Path.Combine(Root,"Videos","media.mp4"); File.WriteAllBytes(media, Enumerable.Range(0, 160003).Select(i => (byte)i).ToArray());
            using (var stream = File.OpenRead(media))
            {
                var native = PublicationHandleObservation.Read(WindowsFileMetadataApi.Instance,stream.SafeFileHandle,media)!;
                Assert.NotNull(native); Receipt = PublicationReceipt.IssueForTests(upperReceiptPath ? media.ToUpperInvariant() : media,stream.Length,native.Snapshot.Identity!);
            }
            Store = new(Path.Combine(Root,"history.db")); Store.Initialize(); Writer = new(Store);
            Op = OrdinaryRecordingBundleOperation.Create("r",media); Request = BundlePublicationIdentityTests.RequestFor(media,"r",Op.OperationId);
            if (anchor) Request.BindMediaPublication(Receipt);
            Writer.Register(Row("r",media,Receipt.Anchor.Length) with { PublicationReceipt = anchor ? Receipt : null, BundleRegistration = new(Op) });
        }
        internal async Task<OrdinaryRecordingBundleUpdate> Produce()
        {
            var result = await Generator.GenerateAsync(Request); Assert.True(result.Success,result.ErrorDetail);
            return new(Op,new("ready",Op.BundlePath,null,result.ContentsEvidence) { IdentityEvidence = result.PublicationReceipt?.Evidence, MediaHashEvidence = result.MediaHashEvidence });
        }
        internal void Accept(OrdinaryRecordingBundleUpdate update)
        { Assert.True(Writer.TryPublishBundle(update)); Assert.Equal(update.Result,Writer.Read("r")!.Bundle); }
        internal void Execute(string sql)
        { using var c = new SqliteConnection($"Data Source={Store.DatabasePath};Pooling=False"); c.Open(); SqliteOutputPathCollation.Register(c); using var cmd=c.CreateCommand();cmd.CommandText=sql;cmd.ExecuteNonQuery(); }
        internal long Scalar(string sql)
        { using var c=Store.OpenReadOnlyConnection();using var cmd=c.CreateCommand();cmd.CommandText=sql;return Convert.ToInt64(cmd.ExecuteScalar()); }
        internal void Reopen() { Assert.True(Writer.DrainAndDispose(TimeSpan.FromSeconds(3))); Store.Initialize(); Writer=new(Store); }
        public void Dispose() => Writer.Dispose();
    }
    private static string NewRoot() { var p=Path.GetFullPath(Path.Combine(TestHelper.ProjectRoot,".local-data/temp/task340",Guid.NewGuid().ToString("N")));Directory.CreateDirectory(p);return p; }
    private static OrdinaryRecordingHistorySnapshot Row(string id,string media,long size) => new(id,At,"completed","display","display_capture","Synthetic",1,"none",null,null,null,null,media,null,null,null,null,null,null,At.AddSeconds(1),At.AddSeconds(2),"duration_reached",size,1);
    private sealed class Frames : IExternalProcessRunner
    {
        public Task<ExternalProcessResult> RunAsync(string filename,IReadOnlyList<string> args,TimeSpan timeout,bool captureStderr=true,Encoding? stderrEncoding=null,CancellationToken cancellationToken=default)
        { cancellationToken.ThrowIfCancellationRequested(); var path=args.Last();File.WriteAllBytes(path,(path.EndsWith(".png") ? new byte[]{137,80,78,71,13,10,26,10} : new byte[]{255,216}).Concat(new byte[100]).ToArray());return Task.FromResult(new ExternalProcessResult(0,false,"")); }
    }
    private static string Independent(string path) { using var f=File.OpenRead(path);return Convert.ToHexString(SHA256.HashData(f)).ToLowerInvariant(); }

    [Fact]
    public async Task ActualSingleHandleStreamsFixedBufferDeniesWritesAndDeletesAndPersistsAfterRestart()
    {
        using var f=new Fixture();SafeFileHandle? handle=null;var reads=0;var identityCalls=0;
        var api=new Metadata(() => identityCalls++);f.Generator.MediaHashMetadataApi=api;
        f.Generator.MediaHashOpenedForTests=s=> {
            handle=s.SafeFileHandle;
            Assert.Throws<IOException>(()=> { using var writer=new FileStream(f.Op.MediaPath,FileMode.Open,FileAccess.Write,FileShare.ReadWrite|FileShare.Delete); });
            Assert.Throws<IOException>(()=>File.Move(f.Op.MediaPath,f.Op.MediaPath+".moved"));
            Assert.Throws<IOException>(()=>File.Delete(f.Op.MediaPath));
        };
        f.Generator.MediaHashReadForTests=async(s,b,t)=> { Assert.Same(handle,s.SafeFileHandle);Assert.Equal(65536,b.Length);reads++;return await s.ReadAsync(b,t); };
        var update=await f.Produce();Assert.NotNull(update.Result.MediaHashEvidence);Assert.Equal(4,reads);Assert.Equal(2,identityCalls);Assert.True(handle!.IsClosed);
        var expected=Independent(f.Op.MediaPath);f.Accept(update);using var metadata=JsonDocument.Parse(File.ReadAllText(Path.Combine(f.Op.BundlePath,"metadata.json")));
        Assert.Equal(expected,metadata.RootElement.GetProperty("media").GetProperty("sha256").GetString());
        f.Reopen();var hash=f.Writer.Read("r")!.MediaHash;Assert.Equal(new OrdinaryMediaHashSnapshot("sha256",expected,"bundle_generation",f.Receipt.Anchor.Length),hash);
        Assert.Equal(hash,Assert.Single(f.Writer.ReadPage(100,null).Items).MediaHash);Assert.Equal(hash,Assert.Single(new UnifiedHistoryQueryService(f.Store,f.Writer).GetPage(Sid,Session,100,new("ordinary")).Items).MediaHash);
        // All historical projections remain available when the bytes and five attachments cannot be opened.
        using var block=new FileStream(f.Op.MediaPath,FileMode.Open,FileAccess.Read,FileShare.None);
        foreach(var name in new[]{"metadata.json","thumbnail.jpg","first_frame.png","last_frame.png","marks.json"})File.Delete(Path.Combine(f.Op.BundlePath,name));
        Assert.Equal(hash,f.Writer.Read("r")!.MediaHash);Assert.Equal(hash,Assert.Single(f.Writer.ReadPage(20,null).Items).MediaHash);
        Assert.Equal(hash,Assert.Single(new UnifiedHistoryQueryService(f.Store,f.Writer).GetPage(Sid,Session,20,new("ordinary")).Items).MediaHash);
        Assert.Equal(4,reads);output.WriteLine("One actual read handle, three fixed-buffer data reads + EOF; two native identity observations; same persisted generation digest after restart and inaccessible content.");
    }
    [Fact]
    public async Task WindowsCaseEquivalentReceiptPathRoundTripsAndReplaysWithoutChangingPhysicalIdentity()
    {
        using var f=new Fixture(upperReceiptPath:true);var update=await f.Produce();Assert.NotNull(update.Result.MediaHashEvidence);
        f.Accept(update);f.Reopen();f.Accept(update);Assert.Equal(Independent(f.Op.MediaPath),f.Writer.Read("r")!.MediaHash!.Value);
        Assert.Equal(f.Receipt.Anchor.Identity,update.Result.MediaHashEvidence!.Anchor.Identity);Assert.Equal(f.Op.MediaPath,update.Result.MediaHashEvidence.Anchor.Path);
        Assert.Equal(f.Writer.Read("r")!.MediaHash,Assert.Single(new UnifiedHistoryQueryService(f.Store,f.Writer).GetPage(Sid,Session,100,new("ordinary")).Items).MediaHash);
    }
    [Theory]
    [InlineData("replace")] [InlineData("length")] [InlineData("preexisting_writer")] [InlineData("read_error")] [InlineData("cancel")] [InlineData("metadata_changed")]
    public async Task HashStageFailuresNeverPublishTrustedReplacementOrRollbackMedia(string fault)
    {
        using var f=new Fixture();var original=File.ReadAllBytes(f.Op.MediaPath);FileStream? writer=null;using var cancel=new CancellationTokenSource();
        if(fault=="replace") { File.Move(f.Op.MediaPath,f.Op.MediaPath+".original");File.WriteAllBytes(f.Op.MediaPath,original.Select(b=>(byte)(b^0xff)).ToArray()); }
        if(fault=="length")File.AppendAllText(f.Op.MediaPath,"changed");
        if(fault=="preexisting_writer")writer=new(f.Op.MediaPath,FileMode.Open,FileAccess.Write,FileShare.ReadWrite|FileShare.Delete);
        if(fault=="read_error")f.Generator.MediaHashReadForTests=(_,_,_)=>throw new IOException("owned read failure");
        if(fault=="cancel")f.Generator.MediaHashOpenedForTests=_=>cancel.Cancel();
        if(fault=="metadata_changed")f.Generator.MediaHashMetadataApi=new Metadata(null,true);
        try
        {
            if(fault=="cancel")await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>f.Generator.GenerateAsync(f.Request,cancel.Token));
            else { var result=await f.Generator.GenerateAsync(f.Request);Assert.False(result.Success);Assert.Equal(RecordingBundleErrorCodes.HashFailed,result.ErrorCode);Assert.Null(result.MediaHashEvidence); }
            Assert.False(Directory.Exists(f.Op.BundlePath));Assert.True(File.Exists(f.Op.MediaPath));Assert.Equal("completed",f.Writer.Read("r")!.Status);Assert.Null(f.Writer.Read("r")!.MediaHash);
            if(fault=="replace")Assert.Equal(original,File.ReadAllBytes(f.Op.MediaPath+".original"));
        }
        finally { writer?.Dispose(); }
    }
    [Theory]
    [InlineData("missing_anchor")] [InlineData("no_native")] [InlineData("public_ready")]
    public async Task CompatibleUnknownCannotMintPrivateHashFromPublicReadyOrMetadata(string path)
    {
        using var f=new Fixture(path!="missing_anchor");if(path=="no_native")f.Generator.MediaHashMetadataApi=new BundlePublicationIdentityTests.NoIdentityApi();
        if(path=="public_ready")
        {
            var read=await BundleMediaHashEvidence.ReadAsync(f.Request,WindowsFileMetadataApi.Instance,default);
            var fake=RecordingBundleGenerationResult.Ready(f.Op.BundlePath,BundleContentsGenerationTests.Items()).WithMediaHash(read.Evidence);
            Assert.Null(fake.PublicationReceipt);Assert.Null(fake.MediaHashEvidence);
            f.Accept(new(f.Op,new("ready",f.Op.BundlePath,null,fake.ContentsEvidence)));
        }
        else { var update=await f.Produce();Assert.Null(update.Result.MediaHashEvidence);f.Accept(update); }
        Assert.Null(f.Writer.Read("r")!.MediaHash);Assert.Equal("ready",f.Writer.Read("r")!.Bundle!.Status);
    }
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task HashSqlOrRealCommitFailureRollsBackEntireBundleThenExactRetryWins(bool commit)
    {
        using var f=new Fixture();var update=await f.Produce();Assert.NotNull(update.Result.MediaHashEvidence);var before=f.Writer.Read("r")!;
        if(commit)
        { f.Execute("CREATE TABLE owned_fault(id TEXT REFERENCES ordinary_recording_history(recording_id) DEFERRABLE INITIALLY DEFERRED);");f.Writer.BeforeBundleCommitForTests=(c,t)=> {using var q=c.CreateCommand();q.Transaction=t;q.CommandText="INSERT INTO owned_fault VALUES('missing');";q.ExecuteNonQuery();}; }
        else f.Execute("CREATE TRIGGER owned_hash_fault BEFORE UPDATE ON ordinary_media_hash BEGIN SELECT RAISE(ABORT,'owned hash fault'); END;");
        Assert.True(f.Writer.TryPublishBundle(update));Assert.Throws<OrdinaryRecordingHistoryUnavailableException>(()=>f.Writer.Read("r"));
        Assert.Equal(0,f.Scalar("SELECT COUNT(*) FROM ordinary_media_hash WHERE evidence_version IS NOT NULL"));Assert.Equal(0,f.Scalar("SELECT COUNT(*) FROM ordinary_bundle_contents_items"));Assert.Equal(0,f.Scalar("SELECT COUNT(*) FROM ordinary_bundle_identity_items"));
        using(var independent=new SqliteOrdinaryRecordingHistoryStore(f.Store))Assert.Equal(before,independent.Read("r"));Assert.True(File.Exists(f.Op.MediaPath));
        if(commit)f.Writer.BeforeBundleCommitForTests=null;else f.Execute("DROP TRIGGER owned_hash_fault;");
        f.Accept(update);f.Accept(update);f.Reopen();Assert.NotNull(f.Writer.Read("r")!.MediaHash);
    }
    [Fact]
    public async Task FailedPublisherLateForeignRecoveryAndUnknownWinnerCannotAcquireDigest()
    {
        using(var f=new Fixture())
        {
            File.WriteAllText(f.Op.BundlePath,"owned blocker");var result=await f.Generator.GenerateAsync(f.Request);Assert.False(result.Success);Assert.Null(result.MediaHashEvidence);
            Assert.True(f.Writer.TryPublishBundle(new(f.Op,new("failed",f.Op.BundlePath,RecordingBundleErrorCodes.PublishFailed))));Assert.Null(f.Writer.Read("r")!.MediaHash);Assert.True(File.Exists(f.Op.MediaPath));
        }
        using(var f=new Fixture())
        {
            var update=await f.Produce();var foreign=f.Op with { OperationId=Guid.NewGuid().ToString("N") };
            Assert.Throws<ArgumentException>(()=>OrdinaryRecordingBundleStorage.ValidateUpdate(new(foreign,update.Result)));
            Assert.True(f.Writer.TryPublishBundle(new(foreign,new("ready",foreign.BundlePath,null))));Assert.Equal("generating",f.Writer.Read("r")!.Bundle!.Status);
            f.Reopen();f.Writer.RecoverIncomplete(At.AddHours(1));Assert.True(f.Writer.TryPublishBundle(update));Assert.Equal("interrupted_unknown",f.Writer.Read("r")!.Bundle!.Status);Assert.Null(f.Writer.Read("r")!.MediaHash);
        }
        using(var f=new Fixture())
        {
            var update=await f.Produce();f.Accept(update with { Result=update.Result with { MediaHashEvidence=null } });
            Assert.True(f.Writer.TryPublishBundle(update));Assert.Throws<OrdinaryRecordingHistoryUnavailableException>(()=>f.Writer.Read("r"));
            using var independent=new SqliteOrdinaryRecordingHistoryStore(f.Store);Assert.Null(independent.Read("r")!.MediaHash);
        }
    }
    [Theory]
    [InlineData("DELETE FROM ordinary_media_hash;")]
    [InlineData("UPDATE ordinary_media_hash SET algorithm='sha512';")]
    [InlineData("UPDATE ordinary_media_hash SET hash_value=upper(hash_value);")]
    [InlineData("UPDATE ordinary_media_hash SET hash_value=x'0102';")]
    [InlineData("UPDATE ordinary_media_hash SET hash_value='abcd';")]
    [InlineData("UPDATE ordinary_media_hash SET size_bytes=-1;")]
    [InlineData("UPDATE ordinary_media_hash SET size_bytes='wrong';")]
    [InlineData("UPDATE ordinary_media_hash SET basis='capture';")]
    [InlineData("UPDATE ordinary_media_hash SET operation_id='ffffffffffffffffffffffffffffffff';")]
    [InlineData("UPDATE ordinary_media_hash SET media_output_path=media_output_path||'.foreign';")]
    [InlineData("UPDATE ordinary_media_hash SET bundle_path=bundle_path||'.foreign';")]
    [InlineData("UPDATE ordinary_media_hash SET volume_id=zeroblob(8);")]
    [InlineData("UPDATE ordinary_media_hash SET file_id=zeroblob(16);")]
    [InlineData("UPDATE ordinary_media_hash SET evidence_version=NULL;")]
    [InlineData("DELETE FROM ordinary_recording_bundles;")]
    [InlineData("DELETE FROM ordinary_bundle_identity_items;DELETE FROM ordinary_bundle_identity;DELETE FROM ordinary_bundle_contents_items;DELETE FROM ordinary_bundle_contents;DELETE FROM ordinary_recording_bundles;")]
    public async Task CorruptedOrMissingModernDigestFailsAllProjections(string sql)
    {
        using var f=new Fixture();f.Accept(await f.Produce());f.Execute("PRAGMA foreign_keys=OFF;PRAGMA ignore_check_constraints=ON;"+sql);
        Assert.Throws<OrdinaryRecordingHistoryUnavailableException>(()=>f.Writer.Read("r"));Assert.Throws<OrdinaryRecordingHistoryUnavailableException>(()=>f.Writer.ReadPage(100,null));
        Assert.Throws<UnifiedHistoryUnavailableException>(()=>new UnifiedHistoryQueryService(f.Store,f.Writer).GetPage(Sid,Session,100,new("ordinary")));
    }
    [Theory]
    [InlineData(1)] [InlineData(20)] [InlineData(100)]
    public void IndexedBoundedBatchSnapshotAndIdentityCursorDoNotReadContent(int limit)
    {
        using var f=new Fixture(false);for(var i=0;i<101;i++) { var row=Row("r"+i.ToString("D3"),Path.Combine(f.Root,"missing"+i+".mp4"),100);f.Writer.Register(row with { BundleRegistration=new(OrdinaryRecordingBundleOperation.Create(row.RecordingId,row.OutputPath)) }); }
        var q=new UnifiedHistoryQueryService(f.Store,f.Writer);var calls=0;var plans=new List<string>();
        var existenceCalls=0;q.OrdinaryFileProbeForTests=_=> { existenceCalls++;return false; };
        q.MediaHashCommandForTests=(c,t,sql,args)=> {
            calls++;Assert.Equal(limit,args.Count);Assert.Contains("LIMIT 101",sql);using var explain=c.CreateCommand();explain.Transaction=t;explain.CommandText="EXPLAIN QUERY PLAN "+sql;foreach(var a in args)explain.Parameters.AddWithValue(a.Key,a.Value);
            using(var r=explain.ExecuteReader())while(r.Read())plans.Add(r.GetString(3));
            // WAL mutation from another connection after candidate selection must stay outside this snapshot.
            if(calls==1)f.Execute("PRAGMA foreign_keys=OFF;DELETE FROM ordinary_media_hash;");
        };
        var page=q.GetPage(Sid,Session,limit,new("ordinary"));Assert.Equal(limit,page.Items.Count);Assert.All(page.Items,i=>Assert.Null(i.MediaHash));Assert.Equal(1,calls);Assert.Equal(limit,existenceCalls);
        Assert.Contains(plans,p=>p.Contains("SEARCH h USING INDEX "+SqliteSchemaV36.IndexName));Assert.Contains(plans,p=>p.Contains("SEARCH a USING INDEX"));Assert.NotNull(page.NextCursor);
        Assert.Throws<UnifiedHistoryArgumentException>(()=>q.GetPage("foreign-sid",Session,limit,new("ordinary"),page.NextCursor));
        Assert.Throws<UnifiedHistoryArgumentException>(()=>q.GetPage(Sid,"foreign-session",limit,new("ordinary"),page.NextCursor));
        Assert.Throws<UnifiedHistoryUnavailableException>(()=>q.GetPage(Sid,Session,limit,new("ordinary")));output.WriteLine(string.Join("\n",plans));
    }
    [Fact]
    public void V35UpgradePreservesEveryOldReceiptAndLeavesLegacyUnknownWithoutBackfill()
    {
        var root=NewRoot();var db=Path.Combine(root,"legacy.db");var media=Path.Combine(root,"absent.mp4");
        using(var c=new SqliteConnection($"Data Source={db};Pooling=False"))
        {
            c.Open();SqliteOutputPathCollation.Register(c);using var cmd=c.CreateCommand();foreach(var m in SqliteSchemaCatalog.Migrations.Where(m=>m.Version<=35)) {
                cmd.CommandText=m.Definition;cmd.ExecuteNonQuery();cmd.CommandText="INSERT INTO schema_migrations VALUES($v,$name,$hash,0)";cmd.Parameters.AddWithValue("$v",m.Version);cmd.Parameters.AddWithValue("$name",m.Name);cmd.Parameters.AddWithValue("$hash",m.Checksum);cmd.ExecuteNonQuery();cmd.Parameters.Clear(); }
            cmd.CommandText="INSERT INTO ordinary_recording_history(recording_id,history_sequence,created_at_utc,status_code,source_type,capture_semantics,source_title,audio_mode,output_path,started_at_utc,completed_at_utc,output_size_bytes,output_duration_ms,updated_at_utc) VALUES('legacy',1,$at,'completed','display','display_capture','Synthetic','none',$media,$at,$at,100,1000,$at); INSERT INTO ordinary_recording_bundles VALUES('legacy',$media,'ffffffffffffffffffffffffffffffff','ready',$bundle,NULL);";
            cmd.Parameters.AddWithValue("$at",At.UtcTicks);cmd.Parameters.AddWithValue("$media",media);cmd.Parameters.AddWithValue("$bundle",OrdinaryRecordingBundleOperation.DerivePath(media));cmd.ExecuteNonQuery();
        }
        var before=UnifiedHistoryProfileFilterTests.RawSnapshot(db,schemaThrough:35);var store=new SqliteOperationalStore(db);store.Initialize();store.Initialize();Assert.Equal(before,UnifiedHistoryProfileFilterTests.RawSnapshot(db,schemaThrough:35));
        using var writer=new SqliteOrdinaryRecordingHistoryStore(store);Assert.Null(writer.Read("legacy")!.MediaHash);
        using(var c=store.OpenReadOnlyConnection()) {using var cmd=c.CreateCommand();cmd.CommandText="SELECT COUNT(*) FROM ordinary_media_hash;";Assert.Equal(0L,cmd.ExecuteScalar());}
        var fresh=Row("fresh",Path.Combine(root,"fresh.mp4"),100);writer.Register(fresh with { BundleRegistration=new(OrdinaryRecordingBundleOperation.Create("fresh",fresh.OutputPath)) });
        using(var c=store.OpenConnection()) {using var cmd=c.CreateCommand();cmd.CommandText="DELETE FROM ordinary_media_hash WHERE recording_id='fresh';";cmd.ExecuteNonQuery();}
        Assert.Throws<OrdinaryRecordingHistoryUnavailableException>(()=>writer.Read("fresh"));Assert.Null(writer.Read("legacy")!.MediaHash);
    }
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task SilentFormalFfmpegFinalizerAndRestartedAuthenticatedThreeApiProjections(bool headless)
    {
        using var f=new Fixture();var priorDir=Environment.GetEnvironmentVariable("AGENT_RECORDER_DATA_DIR");var priorMode=Environment.GetEnvironmentVariable("AGENT_RECORDER_TEST_MODE");
        RecordingEngine? engine=null;FfmpegCaptureBackend? backend=null;
        try
        {
            Environment.SetEnvironmentVariable("AGENT_RECORDER_DATA_DIR",f.Root);Environment.SetEnvironmentVariable("AGENT_RECORDER_TEST_MODE","1");DataDirResolver.SetOverride(f.Root);ApiKeyAuth.InitializeForTesting(f.Root);
            SystemQuery.SetDisplayTopologyProvider(()=>new() {new("display_1","Synthetic",true,new(0,0,1920,1080),1.0,"synthetic-test-display:display_1",DisplayIdentityResolutionStatus.Resolved)});
            var audit=new RetentionSharedOutputPathTests.QuietAudit();var tray=new Tray(headless);var generator=new FfmpegRecordingBundleGenerator();
            engine=new(audit,bundleGenerator:generator);engine.ConfigureOrdinaryRecordingHistory(f.Writer);engine.SetTray(tray);
            backend=new() { VideoInputArgumentsForTests=_=>new[]{"-re","-f","lavfi","-t","1","-i","color=c=black:s=64x64:r=15"} };engine.BackendFactory=_=>(backend,"ffmpeg");
            engine.CreateRecording(new JsonObject { ["source"]=new JsonObject {["type"]="display",["display_id"]="display_1"},["video"]=new JsonObject {["fps"]=15},["duration_seconds"]=1,["countdown_seconds"]=0,
                ["output"]=new JsonObject {["directory"]=Path.GetDirectoryName(f.Op.MediaPath),["filename"]="formal.mp4",["conflict_policy"]="fail"}},"task340-synthetic",tray);
            tray.Confirm!(ConfirmationDecision.Approve());var rec=Assert.Single(engine._recs.Values);
            await Until(()=>f.Writer.Read(rec.Id)?.Bundle?.Status is "ready" or "failed");Assert.Equal(RecState.completed,rec.State);var row=f.Writer.Read(rec.Id)!;
            Assert.Equal("ready",row.Bundle!.Status);Assert.NotNull(rec.LastMeta!.PublicationReceipt);Assert.NotNull(row.MediaHash);
            Assert.Equal(Independent(rec.OutputPath),row.MediaHash!.Value);Assert.False(new FfmpegOutputProber().Probe(rec.OutputPath).HasAudioStream);
            using(var metadata=JsonDocument.Parse(File.ReadAllText(Path.Combine(row.Bundle.Path!,"metadata.json"))))Assert.Equal(row.MediaHash.Value,metadata.RootElement.GetProperty("media").GetProperty("sha256").GetString());
            await AssertApis(engine,f.Store,f.Writer,row,headless);engine.Dispose();engine=null;backend.Dispose();backend=null;f.Reopen();
            engine=new(audit);engine.ConfigureOrdinaryRecordingHistory(f.Writer);engine.SetTray(tray);Assert.Equal(row.MediaHash,f.Writer.Read(rec.Id)!.MediaHash);await AssertApis(engine,f.Store,f.Writer,row,headless);
        }
        finally
        {
            engine?.Dispose();backend?.Dispose();ApiKeyAuth.ResetForTesting(null);SystemQuery.SetDisplayTopologyProvider(null);DataDirResolver.ClearOverride();
            Environment.SetEnvironmentVariable("AGENT_RECORDER_DATA_DIR",priorDir);Environment.SetEnvironmentVariable("AGENT_RECORDER_TEST_MODE",priorMode);
        }
    }
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task AvFormalMuxManualAndNaturalFinalizersCarryPrivateMediaReceipt(bool natural)
    {
        var root=NewRoot();var video=Path.Combine(root,"video.mp4");var audio=Path.Combine(root,"audio.wav");
        await Generate(new[]{"-y","-f","lavfi","-i","color=c=blue:s=64x64:r=15","-t","1","-c:v","libx264","-pix_fmt","yuv420p",video});
        await Generate(new[]{"-y","-f","lavfi","-i","sine=frequency=440:sample_rate=48000","-t","4","-c:a","pcm_s16le",audio});
        // Reuse the accepted exception-safe fixture without editing its code, budgets or restoration contract.
        var type=typeof(OrdinaryAvStorageSafetyTests).GetNestedType("Fixture",BindingFlags.NonPublic)!;
        using var owned=(IDisposable)type.GetConstructor(BindingFlags.Instance|BindingFlags.NonPublic,null,new[]{typeof(bool),typeof(string),typeof(string),typeof(int)},null)!.Invoke(new object?[]{true,audio,video,0});
        var history=Field<SqliteOrdinaryRecordingHistoryStore>(owned,"History");var engine=Field<RecordingEngine>(owned,"Engine");var store=Field<SqliteOperationalStore>(owned,"Store");
        using var response=await (Task<HttpResponseMessage>)type.GetMethod("Create",BindingFlags.Instance|BindingFlags.NonPublic)!.Invoke(owned,new object[]{"raw","display","system_audio",1})!;
        Assert.True(response.IsSuccessStatusCode,await response.Content.ReadAsStringAsync());var tray=Field<object>(owned,"Tray");tray.GetType().GetMethod("Approve",BindingFlags.Instance|BindingFlags.NonPublic)!.Invoke(tray,new object?[]{Path.Combine(Field<string>(owned,"Root"),"Videos")});
        var rec=Assert.Single(engine._recs.Values);await Until(()=>rec.State==RecState.recording);
        if(natural)Field<FakeVideoCaptureWorker>(owned,"Video").EmitNaturalExit(0,"");else engine.Stop(rec.Id,"user_requested");
        await Until(()=>history.Read(rec.Id)?.Bundle?.Status is "ready" or "failed");Assert.Equal(RecState.completed,rec.State);
        var row=history.Read(rec.Id)!;Assert.Equal("ready",row.Bundle!.Status);Assert.NotNull(row.MediaHash);Assert.NotNull(rec.LastMeta!.PublicationReceipt);
        Assert.True(new FfmpegOutputProber().Probe(rec.OutputPath).HasAudioStream);Assert.Equal(Independent(rec.OutputPath),row.MediaHash!.Value);
        await AssertApis(engine,store,history,row,false);AvMuxOwnershipTests.AssertNoOwnedMux(rec.OutputPath);
    }
    private static T Field<T>(object fixture,string name)=>(T)fixture.GetType().GetField(name,BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(fixture)!;
    private static async Task Until(Func<bool> done)
    { var stop=DateTime.UtcNow.AddSeconds(15);while(!done() && DateTime.UtcNow<stop)await Task.Delay(20);Assert.True(done(),"Synthetic producer did not finish in its owned deadline."); }
    private static async Task Generate(string[] args)
    {
        var info=new ProcessStartInfo(Path.Combine(TestHelper.ProjectRoot,"tools/ffmpeg/bin/ffmpeg.exe")){UseShellExecute=false,CreateNoWindow=true,RedirectStandardError=true};foreach(var arg in args)info.ArgumentList.Add(arg);
        using var p=Process.Start(info)!;var err=p.StandardError.ReadToEndAsync();await p.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30));Assert.True(p.ExitCode==0,await err);
    }
    private static async Task AssertApis(RecordingEngine engine,SqliteOperationalStore store,SqliteOrdinaryRecordingHistoryStore writer,OrdinaryRecordingHistorySnapshot row,bool headless)
    {
        var audit=new RetentionSharedOutputPathTests.QuietAudit();var q=new UnifiedHistoryQueryService(store,writer,headless);var files=new RetentionPreviewQueryTests.MetadataFake();
        var preview=new RetentionPreviewQueryService(q.GetOrdinaryEvidencePage,Path.GetDirectoryName(Path.GetDirectoryName(row.OutputPath))!,files,files,()=>DateTimeOffset.UtcNow.AddDays(1));
        var api=new ApiServer(engine,audit,new Tray(headless),null,null,null,null,null,null,null,null,null,0,
            unifiedHistoryGateway:new SqliteUnifiedHistoryGateway(q,()=>(Sid,Session)),artifactCheckGateway:new SqliteOrdinaryArtifactCheckGateway(new(writer)),retentionPreviewGateway:new SqliteRetentionPreviewGateway(preview,()=>(Sid,Session)));
        try
        {
        api.Start();using var client=new HttpClient(new HttpClientHandler {UseProxy=false}){BaseAddress=new($"http://127.0.0.1:{api.BoundPort}"),Timeout=TimeSpan.FromSeconds(10)};
        using(var denied=await client.GetAsync("/api/v1/recording-history/"+row.RecordingId))Assert.Equal(HttpStatusCode.Unauthorized,denied.StatusCode);client.DefaultRequestHeaders.Add("X-Agent-Recorder-Key",ApiKeyAuth.CurrentApiKey);
        foreach(var endpoint in new[]{"/api/v1/recording-history","/api/v1/recording-history/"+row.RecordingId,"/api/v1/history?source=ordinary"})
        {
            using var response=await client.GetAsync(endpoint);Assert.Equal(HttpStatusCode.OK,response.StatusCode);var text=await response.Content.ReadAsStringAsync();using var json=JsonDocument.Parse(text);var data=json.RootElement.GetProperty("data");
            var entry=data.TryGetProperty("items",out var items)?items.EnumerateArray().Single(i=>i.GetProperty("recording_id").GetString()==row.RecordingId):data.GetProperty("recording");var hash=entry.GetProperty("media_hash");
            Assert.Equal(4,hash.EnumerateObject().Count());Assert.Equal("sha256",hash.GetProperty("algorithm").GetString());Assert.Equal(row.MediaHash!.Value,hash.GetProperty("value").GetString());Assert.Equal("bundle_generation",hash.GetProperty("basis").GetString());Assert.Equal(row.OutputSizeBytes,hash.GetProperty("size_bytes").GetInt64());
            foreach(var secret in new[]{"file_id","volume_id","operation_id","receipt","anchor","integrity_verified"})Assert.DoesNotContain("\""+secret+"\":",text,StringComparison.OrdinalIgnoreCase);
        }
        using(var caps=await client.GetAsync("/api/v1/capabilities"))
        { using var json=JsonDocument.Parse(await caps.Content.ReadAsStringAsync());var data=json.RootElement.GetProperty("data");Assert.True(data.GetProperty("recording_history").GetProperty("media_hash_supported").GetBoolean());Assert.Equal("bundle_generation",data.GetProperty("unified_history").GetProperty("media_hash_basis").GetString());var artifact=data.GetProperty("ordinary_artifact_check");Assert.True(artifact.GetProperty("metadata_only").GetBoolean());Assert.False(artifact.GetProperty("content_verification").GetBoolean());Assert.False(artifact.GetProperty("deletion_supported").GetBoolean()); }
        using(var retention=await client.GetAsync("/api/v1/retention/preview?completed_before="+Uri.EscapeDataString(DateTimeOffset.UtcNow.ToString("O"))))
        { Assert.Equal(HttpStatusCode.OK,retention.StatusCode);using var json=JsonDocument.Parse(await retention.Content.ReadAsStringAsync());var data=json.RootElement.GetProperty("data");var item=data.GetProperty("items").EnumerateArray().Single(i=>i.GetProperty("recording_id").GetString()==row.RecordingId);Assert.Equal("protected",item.GetProperty("evaluation").GetString());Assert.Equal("bundle_artifact_reference",item.GetProperty("reason_code").GetString());Assert.True(data.GetProperty("read_only").GetBoolean());Assert.False(data.GetProperty("deletion_supported").GetBoolean());Assert.Empty(files.Observations); }
        }
        finally { api.Stop(); }
    }
    private sealed class Tray(bool headless) : ITrayContext
    {
        internal Action<ConfirmationDecision>? Confirm;
        public string HostMode=>headless?"headless":"tray";public bool SupportsRegionSelectionUi=>false;
        public void RequestConfirmation(RecordingConfirmationPresentation p,Action<ConfirmationDecision> c)=>Confirm=c;
        public void RequestRegionSelection(int t,Action<string,int,int,int,int,string,string> c)=>throw new InvalidOperationException("No UI");
        public void SetRecording(RecordingUiPresentation p){} public void SetIdle(RecordingUiPresentation p){}public void SetAllIdle(){}public void ShowError(string s){}
    }
    private sealed class Metadata(Action? identityRead,bool drift=false) : IWindowsFileMetadataApi
    {
        private readonly IWindowsFileMetadataApi real=WindowsFileMetadataApi.Instance;private int basics;
        public bool Supported=>real.Supported; public uint DriveType(string root)=>real.DriveType(root);
        public SafeFileHandle Open(string p,uint a,uint s,uint c,uint f,out int e)=>throw new InvalidOperationException("No reopen allowed");
        public bool Basic(SafeFileHandle h,out FileBasicInformation b,out int e) {var ok=real.Basic(h,out b,out e);if(drift && ++basics==2)b.LastWriteTime++;return ok;}
        public bool Standard(SafeFileHandle h,out FileStandardInformation s,out int e)=>real.Standard(h,out s,out e);
        public bool Identity(SafeFileHandle h,out FileIdentityInformation i,out int e) {identityRead?.Invoke();return real.Identity(h,out i,out e);}
        public uint FinalPath(SafeFileHandle h,StringBuilder b,int c,out int e)=>real.FinalPath(h,b,c,out e);
    }
}
