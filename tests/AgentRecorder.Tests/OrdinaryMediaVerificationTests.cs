using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Runtime.InteropServices;
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
public sealed class OrdinaryMediaVerificationTests(ITestOutputHelper output)
{
    private const string Endpoint="/api/v1/recording-history/r/artifacts/verify-media";
    private static TaskCompletionSource Signal()=>new(TaskCreationOptions.RunContinuationsAsynchronously);
    private sealed class Fixture : IDisposable
    {
        internal readonly string Root=Path.GetFullPath(Path.Combine(TestHelper.ProjectRoot,".local-data/temp/task341",Guid.NewGuid().ToString("N")));
        internal readonly SqliteOperationalStore Store;
        internal SqliteOrdinaryRecordingHistoryStore Writer;
        internal readonly OrdinaryRecordingBundleOperation Op;
        internal readonly PublicationReceipt Receipt;
        internal Fixture()
        {
            Directory.CreateDirectory(Path.Combine(Root,"Videos"));var media=Path.Combine(Root,"Videos","media.mp4");
            File.WriteAllBytes(media,Enumerable.Range(0,160003).Select(i=>(byte)i).ToArray());
            using(var stream=File.OpenRead(media))
            {var sample=PublicationHandleObservation.Read(WindowsFileMetadataApi.Instance,stream.SafeFileHandle,media)!;Assert.NotNull(sample);Receipt=PublicationReceipt.IssueForTests(media,stream.Length,sample.Snapshot.Identity!);}
            Store=new(Path.Combine(Root,"history.db"));Store.Initialize();Writer=new(Store);Op=OrdinaryRecordingBundleOperation.Create("r",media);
            Writer.Register(Row("r",media,Receipt.Anchor.Length) with {PublicationReceipt=Receipt,BundleRegistration=new(Op)});
        }
        internal async Task Produce(bool real=false,bool trusted=true)
        {
            var request=BundlePublicationIdentityTests.RequestFor(Op.MediaPath,"r",Op.OperationId);if(trusted)request.BindMediaPublication(Receipt);
            var generator=real?new FfmpegRecordingBundleGenerator():new FfmpegRecordingBundleGenerator(new Frames(),ffmpegPathProvider:()=>"owned-frame-fixture");
            var result=await generator.GenerateAsync(request);Assert.True(result.Success,result.ErrorDetail);
            Assert.True(Writer.TryPublishBundle(new(Op,new("ready",Op.BundlePath,null,result.ContentsEvidence) {
                IdentityEvidence=result.PublicationReceipt?.Evidence,MediaHashEvidence=result.MediaHashEvidence})));
            Assert.Equal(trusted,Writer.Read("r")!.MediaHash is not null);
        }
        internal void Sql(string sql) {using var c=new SqliteConnection($"Data Source={Store.DatabasePath};Pooling=False");c.Open();SqliteOutputPathCollation.Register(c);using var q=c.CreateCommand();q.CommandText=sql;q.ExecuteNonQuery();}
        internal string Snapshot()
        {using var c=Store.OpenReadOnlyConnection();using var q=c.CreateCommand();q.CommandText="SELECT name FROM sqlite_master WHERE type='table' ORDER BY name";var names=new List<string>();using(var reader=q.ExecuteReader())while(reader.Read())names.Add(reader.GetString(0));var text=new StringBuilder();foreach(var name in names){q.CommandText="SELECT * FROM \""+name+"\" ORDER BY rowid";using var reader=q.ExecuteReader();while(reader.Read()){text.Append(name);for(var i=0;i<reader.FieldCount;i++)text.Append('|').Append(reader.GetValue(i) is byte[] b?Convert.ToHexString(b):reader.GetValue(i));text.AppendLine();}}return text.ToString();}
        internal void Reopen() {Assert.True(Writer.DrainAndDispose(TimeSpan.FromSeconds(3)));Writer=new(Store);}
        public void Dispose()=>Writer.Dispose();
    }
    private static OrdinaryRecordingHistorySnapshot Row(string id,string path,long size)=>new(id,At,"completed","display","display_capture","Synthetic",1,"none",null,null,null,null,path,null,null,null,null,null,null,At.AddSeconds(1),At.AddSeconds(2),"duration_reached",size,1);
    private sealed class Frames : IExternalProcessRunner
    {public Task<ExternalProcessResult> RunAsync(string file,IReadOnlyList<string> args,TimeSpan timeout,bool captureStderr=true,Encoding? stderrEncoding=null,CancellationToken cancellationToken=default)
        {cancellationToken.ThrowIfCancellationRequested();File.WriteAllBytes(args.Last(),(args.Last().EndsWith(".png")?new byte[]{137,80,78,71,13,10,26,10}:new byte[]{255,216}).Concat(new byte[100]).ToArray());return Task.FromResult(new ExternalProcessResult(0,false,""));}}
    private sealed class Ancestors : IReadOnlyPathMetadata
    {
        internal readonly List<string> Calls=new(); internal Func<string,FileAttributes> Read=File.GetAttributes;
        public FileAttributes GetAttributes(string path) {Calls.Add(path);return Read(path);}
        public long GetLength(string path)=>throw new InvalidOperationException("No ancestor length or content reads permitted.");
    }
    private sealed class Native(string fault="") : IWindowsFileMetadataApi
    {
        private readonly IWindowsFileMetadataApi real=WindowsFileMetadataApi.Instance;
        internal readonly List<SafeFileHandle> Handles=new();internal int IdCalls,Basics;internal long? Length;
        public bool Supported=>fault!="unsupported";
        public uint DriveType(string root)=>fault=="network"?4u:real.DriveType(root);
        public SafeFileHandle Open(string path,uint access,uint share,uint creation,uint flags,out int error)
        {
            Assert.Equal(0x80000080u,access);Assert.Equal(1u,share);Assert.Equal(3u,creation);Assert.Equal(0x48200000u,flags);
            if(fault=="denied") {error=5;var invalid=new SafeFileHandle(new IntPtr(-1),true);Handles.Add(invalid);return invalid;}
            var handle=real.Open(path,access,share,creation,flags,out error);Handles.Add(handle);return handle;
        }
        public bool Basic(SafeFileHandle h,out FileBasicInformation b,out int e)
        {Assert.Same(Handles.Single(),h);var ok=real.Basic(h,out b,out e);Basics++;if(fault=="unknown")return false;if(fault=="reparse")b.Attributes|=(uint)FileAttributes.ReparsePoint;if(fault=="directory")b.Attributes|=(uint)FileAttributes.Directory;if(fault=="drift"&&Basics==2)b.LastWriteTime++;return ok;}
        public bool Standard(SafeFileHandle h,out FileStandardInformation s,out int e)
        {Assert.Same(Handles.Single(),h);var ok=real.Standard(h,out s,out e);if(fault=="links")s.NumberOfLinks=2;if(Length is { } size)s.EndOfFile=size;return ok;}
        public bool Identity(SafeFileHandle h,out FileIdentityInformation i,out int e)
        {Assert.Same(Handles.Single(),h);IdCalls++;var ok=real.Identity(h,out i,out e);if(fault=="identity")i.IdHigh++;return ok;}
        public uint FinalPath(SafeFileHandle h,StringBuilder b,int c,out int e)
        {Assert.Same(Handles.Single(),h);var n=real.FinalPath(h,b,c,out e);if(fault=="path"){b.Clear().Append(@"C:\foreign.mp4");n=(uint)b.Length;}return n;}
    }
    private sealed class Tray(bool headless=false) : ITrayContext
    {
        internal Action<ConfirmationDecision>? Confirm;
        public string HostMode=>headless?"headless":"tray";public bool SupportsRegionSelectionUi=>false;
        public void RequestConfirmation(RecordingConfirmationPresentation p,Action<ConfirmationDecision> c)=>Confirm=c;
        public void RequestRegionSelection(int t,Action<string,int,int,int,int,string,string> c)=>throw new InvalidOperationException("No UI");
        public void SetRecording(RecordingUiPresentation p){}public void SetIdle(RecordingUiPresentation p){}public void SetAllIdle(){}public void ShowError(string s){}
    }
    private sealed class Host : IDisposable
    {
        private readonly string? prior=Environment.GetEnvironmentVariable("AGENT_RECORDER_DATA_DIR");
        internal readonly RecordingEngine Engine; internal readonly ApiServer Api;
        internal Host(Fixture f,OrdinaryMediaVerificationService? service,bool headless=false,RecordingEngine? engine=null)
        {
            Environment.SetEnvironmentVariable("AGENT_RECORDER_DATA_DIR",f.Root);ApiKeyAuth.InitializeForTesting(f.Root);
            var audit=new RetentionSharedOutputPathTests.QuietAudit();Engine=engine??new(audit);Engine.ConfigureOrdinaryRecordingHistory(f.Writer);
            if(engine is null)Engine.BackendFactory=_=>throw new InvalidOperationException("Capture forbidden");
            var q=new UnifiedHistoryQueryService(f.Store,f.Writer,headless);var files=new RetentionPreviewQueryTests.MetadataFake();
            var retention=new RetentionPreviewQueryService(q.GetOrdinaryEvidencePage,f.Root,files,files,()=>DateTimeOffset.UtcNow.AddDays(1));
            Api=new(Engine,audit,new Tray(headless),null,null,null,null,null,null,null,null,null,0,
                unifiedHistoryGateway:new SqliteUnifiedHistoryGateway(q,()=>(Sid,Session)),
                artifactCheckGateway:new SqliteOrdinaryArtifactCheckGateway(new(f.Writer)),
                retentionPreviewGateway:new SqliteRetentionPreviewGateway(retention,()=>(Sid,Session)),
                mediaVerificationGateway:service is null?null:new SqliteOrdinaryMediaVerificationGateway(service));Api.Start();
        }
        internal HttpClient Client(bool auth=true)
        {var c=new HttpClient(new HttpClientHandler{UseProxy=false}){BaseAddress=new($"http://127.0.0.1:{Api.BoundPort}"),Timeout=TimeSpan.FromSeconds(8)};if(auth)c.DefaultRequestHeaders.Add("X-Agent-Recorder-Key",ApiKeyAuth.CurrentApiKey);return c;}
        public void Dispose(){Api.Stop();Engine.Dispose();ApiKeyAuth.ResetForTesting(null);Environment.SetEnvironmentVariable("AGENT_RECORDER_DATA_DIR",prior);}
    }
    private static async Task<JsonElement> Data(HttpResponseMessage response)
    {using(response){var text=await response.Content.ReadAsStringAsync();Assert.Equal(HttpStatusCode.OK,response.StatusCode);foreach(var secret in new[]{"file_id","volume_id","operation_id","receipt","anchor","authority","nonce"})Assert.DoesNotContain("\""+secret+"\":",text,StringComparison.OrdinalIgnoreCase);using var doc=JsonDocument.Parse(text);return doc.RootElement.GetProperty("data").Clone();}}
    private static Task<JsonElement> Verify(HttpClient c,string path=Endpoint,string body="{}")=>Post(c,path,body);
    private static async Task<JsonElement> Post(HttpClient c,string path,string body)=>await Data(await c.PostAsync(path,new StringContent(body,Encoding.UTF8,"application/json")));
    private static string Hash(string path) {using var stream=File.OpenRead(path);return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();}
    private static void Unverified(OrdinaryMediaVerificationResult r)
    {Assert.False(r.ContentVerified);Assert.Equal("not_checked",r.Comparison);Assert.Null(r.ObservedHash);}

    [Fact]
    public async Task RealSingleHandleFixedBufferAndSharedWriteDeleteRestrictionsMatchAfterReopen()
    {
        using var f=new Fixture();await f.Produce();f.Reopen();var native=new Native();var reads=0;SafeFileHandle? opened=null;var before=f.Snapshot();
        var service=new OrdinaryMediaVerificationService(f.Writer.ReadArtifactFacts,new Ancestors(),native);
        service.OpenedForTests=s=> {opened=s.SafeFileHandle;Assert.Same(native.Handles.Single(),opened);
            Assert.Throws<IOException>(()=> {using var writer=new FileStream(f.Op.MediaPath,FileMode.Open,FileAccess.Write,FileShare.ReadWrite|FileShare.Delete);});
            Assert.Throws<IOException>(()=>File.Delete(f.Op.MediaPath));Assert.Throws<IOException>(()=>File.Move(f.Op.MediaPath,f.Op.MediaPath+".moved"));};
        service.ReadForTests=async(s,b,t)=> {Assert.Same(opened,s.SafeFileHandle);Assert.Equal(reads<2?65536:28931,b.Length);reads++;return await s.ReadAsync(b,t);};
        var result=await service.VerifyAsync("r");Assert.True(result!.ContentVerified);Assert.Equal("completed",result.CheckState);Assert.Equal("match",result.Comparison);
        Assert.Equal(160003,result.BytesRead);Assert.Equal(Hash(f.Op.MediaPath),result.RecordedHash!.Value);Assert.Equal(result.RecordedHash.Value,result.ObservedHash!.Value);
        Assert.Equal(2,native.IdCalls);Assert.Equal(3,reads);Assert.True(opened!.IsClosed);Assert.Equal(before,f.Snapshot());
        File.WriteAllBytes(f.Op.MediaPath,new byte[160003]);Assert.True(result.ContentVerified);Assert.Equal("mismatch",(await new OrdinaryMediaVerificationService(f.Writer).VerifyAsync("r"))!.Comparison);
        output.WriteLine("Actual OPEN_REPARSE_POINT read handle: share READ only; pre/post native same handle, 64KiB allocation, exact remaining final read. Result freezes this observation only.");
    }
    [Theory]
    [InlineData("inplace","digest_mismatch",true)][InlineData("replace","publication_identity_mismatch",false)]
    [InlineData("length","file_size_mismatch",false)][InlineData("writer","file_inaccessible",false)][InlineData("missing","file_missing",false)]
    public async Task PhysicalObjectAndBytesHaveDistinctReasons(string fault,string reason,bool observed)
    {
        using var f=new Fixture();await f.Produce();var before=f.Snapshot();FileStream? writer=null;
        if(fault=="inplace")File.WriteAllBytes(f.Op.MediaPath,new byte[160003]);
        if(fault=="replace"){File.Move(f.Op.MediaPath,f.Op.MediaPath+".original");File.WriteAllBytes(f.Op.MediaPath,new byte[160003]);}
        if(fault=="length")File.AppendAllText(f.Op.MediaPath,"x");if(fault=="missing")File.Delete(f.Op.MediaPath);
        if(fault=="writer")writer=new(f.Op.MediaPath,FileMode.Open,FileAccess.Write,FileShare.ReadWrite|FileShare.Delete);
        try {var result=(await new OrdinaryMediaVerificationService(f.Writer).VerifyAsync("r"))!;Assert.Equal(reason,result.ReasonCode);Assert.False(result.ContentVerified);
            if(observed){Assert.Equal("mismatch",result.Comparison);Assert.NotNull(result.ObservedHash);Assert.Equal(160003,result.BytesRead);}else {Unverified(result);Assert.Equal(0,result.BytesRead);}Assert.Equal(before,f.Snapshot());}
        finally {writer?.Dispose();}
    }
    [Theory]
    [InlineData("unsupported")][InlineData("network")][InlineData("denied")][InlineData("unknown")][InlineData("reparse")]
    [InlineData("directory")][InlineData("links")][InlineData("identity")][InlineData("path")]
    public async Task UnsafeOrUnknownNativeFactsNeverReadContent(string fault)
    {
        using var f=new Fixture();await f.Produce();var native=new Native(fault);var reads=0;
        var s=new OrdinaryMediaVerificationService(f.Writer.ReadArtifactFacts,new Ancestors(),native){ReadForTests=(_,_,_)=> {reads++;throw new IOException("SECRET");}};
        var result=(await s.VerifyAsync("r"))!;Unverified(result);Assert.Equal(0,reads);Assert.Equal(0,result.BytesRead);Assert.All(native.Handles,h=>Assert.True(h.IsClosed));
    }
    [Theory]
    [InlineData("reparse")][InlineData("file")][InlineData("missing")][InlineData("denied")]
    public async Task AncestorFaultsStopBeforeOpeningLeaf(string fault)
    {
        using var f=new Fixture();await f.Produce();var native=new Native();var a=new Ancestors {Read=_=>fault switch {
            "reparse"=>FileAttributes.Directory|FileAttributes.ReparsePoint,"file"=>FileAttributes.Normal,
            "missing"=>throw new DirectoryNotFoundException("SECRET"),_=>throw new UnauthorizedAccessException("SECRET")}};
        Unverified((await new OrdinaryMediaVerificationService(f.Writer.ReadArtifactFacts,a,native).VerifyAsync("r"))!);Assert.Empty(native.Handles);Assert.Single(a.Calls);
    }
    [Theory]
    [InlineData("ads")][InlineData("unc")][InlineData("device")][InlineData("relative")][InlineData("reserved")][InlineData("ancestor_limit")][InlineData("path_limit")]
    public async Task FrozenUnsafePathNeverOpens(string fault)
    {
        using var f=new Fixture();await f.Produce();var facts=f.Writer.ReadArtifactFacts("r")!;
        var path=fault switch {"ads"=>@"C:\media.mp4:stream.mp4","unc"=>@"\\server\share\media.mp4","device"=>@"\\?\C:\media.mp4","relative"=>"media.mp4","reserved"=>@"C:\CON\media.mp4","ancestor_limit"=>@"C:\"+string.Concat(Enumerable.Repeat("d\\",64))+"media.mp4",_=>@"C:\"+new string('a',1025)+".mp4"};
        var native=new Native();var a=new Ancestors();Unverified((await new OrdinaryMediaVerificationService(_=>facts with {Media=facts.Media with {OutputPath=path},Anchor=facts.Anchor! with {Path=path}},a,native).VerifyAsync("r"))!);
        Assert.Empty(native.Handles);Assert.Empty(a.Calls);
    }
    [Theory]
    [InlineData("no_hash")][InlineData("public_ready")][InlineData("unfinished")][InlineData("not_mp4")][InlineData("no_anchor")][InlineData("over_budget")]
    public async Task IneligibleFactsNeverOpenAndNeverBackfill(string fault)
    {
        using var f=new Fixture();if(fault=="public_ready")await f.Produce(trusted:false);else if(fault!="no_hash")await f.Produce();
        var facts=f.Writer.ReadArtifactFacts("r")!;var before=f.Snapshot();
        if(fault=="unfinished")facts=facts with {Media=facts.Media with {Status="cancelled"}};
        if(fault=="not_mp4")facts=facts with {Media=facts.Media with {OutputPath=f.Op.MediaPath+".mkv"}};
        if(fault=="no_anchor")facts=facts with {Anchor=null};
        if(fault=="over_budget") {var size=OrdinaryMediaVerificationService.MaxBytes+1;var hash=facts.Media.Bundle!.MediaHashEvidence!;facts=facts with {
            Anchor=facts.Anchor! with {Length=size},Media=facts.Media with {OutputSizeBytes=size,Bundle=facts.Media.Bundle with {MediaHashEvidence=BundleMediaHashEvidence.FromStored("r",f.Op.OperationId,f.Op.MediaPath,f.Op.BundlePath,hash.Value,hash.Anchor with {Length=size})}}};}
        var native=new Native();var a=new Ancestors();var result=(await new OrdinaryMediaVerificationService(_=>facts,a,native).VerifyAsync("r"))!;Unverified(result);
        Assert.Equal(fault is "unfinished" or "not_mp4"?"media_not_applicable":fault=="no_anchor"?"publication_anchor_unavailable":fault=="over_budget"?"media_byte_limit":"recorded_hash_unavailable",result.ReasonCode);
        Assert.Empty(native.Handles);Assert.Empty(a.Calls);Assert.Equal(before,f.Snapshot());
    }
    [Theory]
    [InlineData("read_error")][InlineData("short_read")][InlineData("cancel")][InlineData("drift")]
    public async Task PartialOrUnstableReadDoesNotPublishObservedDigest(string fault)
    {
        using var f=new Fixture();await f.Produce();var native=new Native(fault=="drift"?"drift":"");using var cancel=new CancellationTokenSource();var reads=0;
        var exited=Signal();var service=new OrdinaryMediaVerificationService(f.Writer.ReadArtifactFacts,new Ancestors(),native){WorkerExitedForTests=()=>exited.TrySetResult()};
        service.ReadForTests=async(s,b,t)=> {reads++;if(fault=="read_error"&&reads==2)throw new IOException("SECRET");if(fault=="short_read"&&reads==2)return 0;
            var n=await s.ReadAsync(b,t);if(fault=="cancel")cancel.Cancel();return n;};
        var r=(await service.VerifyAsync("r",cancel.Token))!;await exited.Task.WaitAsync(TimeSpan.FromSeconds(3));Unverified(r);Assert.DoesNotContain("SECRET",JsonSerializer.Serialize(r));
        Assert.Equal(fault=="cancel"?"cancelled":fault=="read_error"?"failed":"completed",r.CheckState);Assert.All(native.Handles,h=>Assert.True(h.IsClosed));
    }
    [Theory]
    [InlineData("database")][InlineData("native")][InlineData("read")]
    public async Task DeadlineKeepsLeaseUntilWorkerActuallyExitsAcrossServiceRecreation(string stage)
    {
        using var f=new Fixture();await f.Produce();var entered=Signal();var release=Signal();var exited=Signal();var deadline=Signal();var facts=f.Writer.ReadArtifactFacts("r");
        var a=new Ancestors();if(stage=="native")a.Read=p=> {entered.TrySetResult();release.Task.GetAwaiter().GetResult();return File.GetAttributes(p);};
        var service=new OrdinaryMediaVerificationService(_=> {if(stage=="database"){entered.TrySetResult();release.Task.GetAwaiter().GetResult();}return facts;},a,new Native()) {
            DeadlineForTests=_=>deadline.Task,WorkerExitedForTests=()=>exited.TrySetResult()};
        if(stage=="read")service.ReadForTests=async(s,b,t)=> {entered.TrySetResult();await release.Task;return await s.ReadAsync(b,CancellationToken.None);};
        var work=service.VerifyAsync("r");
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));deadline.SetResult();var result=(await work.WaitAsync(TimeSpan.FromSeconds(3)))!;Unverified(result);Assert.Equal("timeout",result.CheckState);
            var next=(await new OrdinaryMediaVerificationService(f.Writer).VerifyAsync("r"))!;Assert.Equal("busy",next.CheckState);Unverified(next);
        }
        finally {release.TrySetResult();await exited.Task.WaitAsync(TimeSpan.FromSeconds(3));}
        Assert.True((await new OrdinaryMediaVerificationService(f.Writer).VerifyAsync("r"))!.ContentVerified);
    }
    [Fact]
    public async Task OneGiBBoundaryStreamsWithinLimitWithoutCreatingLargeFiles()
    {
        using var f=new Fixture();await f.Produce();var facts=f.Writer.ReadArtifactFacts("r")!;var size=OrdinaryMediaVerificationService.MaxBytes;var hash=facts.Media.Bundle!.MediaHashEvidence!;
        facts=facts with {Anchor=facts.Anchor! with {Length=size},Media=facts.Media with {OutputSizeBytes=size,Bundle=facts.Media.Bundle with {MediaHashEvidence=BundleMediaHashEvidence.FromStored("r",f.Op.OperationId,f.Op.MediaPath,f.Op.BundlePath,hash.Value,hash.Anchor with {Length=size})}}};
        var native=new Native {Length=size};var calls=0;var service=new OrdinaryMediaVerificationService(_=>facts,new Ancestors(),native) {
            ReadForTests=(_,b,_)=> {Assert.Equal(65536,b.Length);calls++;return ValueTask.FromResult(b.Length);}};
        var result=(await service.VerifyAsync("r"))!;Assert.Equal(size,result.BytesRead);Assert.Equal(16384,calls);Unverified(result);Assert.Equal("metadata_changed",result.ReasonCode);
        Assert.Equal(160003,new FileInfo(f.Op.MediaPath).Length);output.WriteLine("Controlled read/native seam simulated exactly 1GiB (16384x64KiB), actual file remains 160003 bytes; no prefix digest exposed as complete.");
    }

    [Theory]
    [InlineData("DELETE FROM ordinary_media_hash;")]
    [InlineData("UPDATE ordinary_media_hash SET hash_value='abcd';")]
    [InlineData("UPDATE ordinary_media_hash SET algorithm='sha512';")]
    [InlineData("UPDATE ordinary_media_hash SET size_bytes='wrong';")]
    [InlineData("UPDATE ordinary_media_hash SET basis='capture';")]
    [InlineData("UPDATE ordinary_media_hash SET operation_id='ffffffffffffffffffffffffffffffff';")]
    [InlineData("UPDATE ordinary_media_hash SET bundle_path=bundle_path||'.foreign';")]
    [InlineData("UPDATE ordinary_media_hash SET file_id=zeroblob(16);")]
    [InlineData("DELETE FROM ordinary_publication_anchors;")]
    [InlineData("UPDATE ordinary_publication_anchors SET source='foreign';")]
    [InlineData("DELETE FROM ordinary_bundle_contents_items WHERE ordinal=2;")]
    public async Task CorruptFactsFailClosedOverAuthenticatedHttpBeforeAnyNativeOpen(string sql)
    {
        using var f=new Fixture();await f.Produce();f.Sql("PRAGMA foreign_keys=OFF;PRAGMA ignore_check_constraints=ON;"+sql);var before=f.Snapshot();var native=new Native();var a=new Ancestors();
        using var host=new Host(f,new(f.Writer.ReadArtifactFacts,a,native));using var c=host.Client();using var r=await c.PostAsync(Endpoint,new StringContent("{}"));
        Assert.Equal(HttpStatusCode.ServiceUnavailable,r.StatusCode);var text=await r.Content.ReadAsStringAsync();Assert.DoesNotContain(f.Root,text);Assert.DoesNotContain("foreign",text);Assert.Empty(native.Handles);Assert.Empty(a.Calls);Assert.Equal(before,f.Snapshot());
    }
    [Theory]
    [InlineData("{\"path\":\"C:\\\\foreign.mp4\"}")][InlineData("{\"algorithm\":\"sha256\"}")][InlineData("{\"hash\":\"forged\"}")]
    [InlineData("{\"max_bytes\":1}")][InlineData("{\"deadline_ms\":1}")][InlineData("null")][InlineData("[]")][InlineData("1")][InlineData("{")]
    public async Task UntrustedRequestBodiesAreRejectedBeforeDatabaseOrContentRead(string body)
    {
        using var f=new Fixture();var db=0;var native=new Native();using var host=new Host(f,new(_=> {db++;return null;},new Ancestors(),native));using var c=host.Client();
        using var r=await c.PostAsync(Endpoint,new StringContent(body));Assert.Equal(HttpStatusCode.BadRequest,r.StatusCode);Assert.Equal(0,db);Assert.Empty(native.Handles);
    }
    [Theory]
    [InlineData("?path=foreign")][InlineData("?force=true")][InlineData("?algorithm=sha256")][InlineData("?max_bytes=1")]
    [InlineData("invalid_space")][InlineData("invalid_slash")][InlineData("invalid_control")]
    public async Task InvalidIdsAndQueryParametersNeverDispatch(string kind)
    {
        using var f=new Fixture();var db=0;var native=new Native();using var host=new Host(f,new(_=> {db++;return null;},new Ancestors(),native));using var c=host.Client();
        var path=kind.StartsWith('?')?Endpoint+kind:Endpoint.Replace("/r/",kind=="invalid_space"?"/%20/":kind=="invalid_slash"?"/bad%2Fid/":"/bad%0Aid/");
        using var r=await c.PostAsync(path,new StringContent("{}"));Assert.Equal(HttpStatusCode.BadRequest,r.StatusCode);Assert.Equal(0,db);Assert.Empty(native.Handles);
    }
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task AuthDisabledMissingAndWrongKeysUnknownIdAndUnconfiguredAreBounded(bool headless)
    {
        using var f=new Fixture();var db=0;var native=new Native();using(var host=new Host(f,new(_=> {db++;return null;},new Ancestors(),native),headless))
        {
            using var denied=host.Client(false);using(var r=await denied.PostAsync(Endpoint,new StringContent("{}")))Assert.Equal(HttpStatusCode.Unauthorized,r.StatusCode);
            denied.DefaultRequestHeaders.Add("X-Agent-Recorder-Key","wrong");using(var r=await denied.PostAsync(Endpoint,new StringContent("{}")))Assert.Equal(HttpStatusCode.Unauthorized,r.StatusCode);
            using var c=host.Client();using(var r=await c.PostAsync(Endpoint.Replace("/r/","/absent/"),new StringContent("{}")))Assert.Equal(HttpStatusCode.NotFound,r.StatusCode);
            Assert.Equal(1,db);ApiKeyAuth.ResetForTesting(null);using(var r=await c.PostAsync(Endpoint,new StringContent("{}")))Assert.Equal(HttpStatusCode.Unauthorized,r.StatusCode);Assert.Equal(1,db);Assert.Empty(native.Handles);
        }
        using(var host=new Host(f,null,headless)) {using var c=host.Client();using(var r=await c.PostAsync(Endpoint,new StringContent("{}")))Assert.Equal(HttpStatusCode.ServiceUnavailable,r.StatusCode);
            var caps=await Data(await c.GetAsync("/api/v1/capabilities"));Assert.False(caps.GetProperty("ordinary_media_verification").GetProperty("supported").GetBoolean());}
    }
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task SyntheticFormalFfmpegPublicationAndRestartVerifyThroughHttpWhileOldReadsStayCheap(bool headless)
    {
        using var f=new Fixture();var priorMode=Environment.GetEnvironmentVariable("AGENT_RECORDER_TEST_MODE");var priorDir=Environment.GetEnvironmentVariable("AGENT_RECORDER_DATA_DIR");
        RecordingEngine? engine=null;FfmpegCaptureBackend? backend=null;
        try
        {
            Environment.SetEnvironmentVariable("AGENT_RECORDER_DATA_DIR",f.Root);Environment.SetEnvironmentVariable("AGENT_RECORDER_TEST_MODE","1");DataDirResolver.SetOverride(f.Root);
            SystemQuery.SetDisplayTopologyProvider(()=>new(){new("display_1","Synthetic",true,new(0,0,1920,1080),1.0,"synthetic-test-display:display_1",DisplayIdentityResolutionStatus.Resolved)});
            var audit=new RetentionSharedOutputPathTests.QuietAudit();var tray=new Tray(headless);engine=new(audit,bundleGenerator:new FfmpegRecordingBundleGenerator());engine.ConfigureOrdinaryRecordingHistory(f.Writer);engine.SetTray(tray);
            backend=new(){VideoInputArgumentsForTests=_=>new[]{"-re","-f","lavfi","-t","1","-i","color=c=black:s=64x64:r=15"}};engine.BackendFactory=_=>(backend,"ffmpeg");
            engine.CreateRecording(new JsonObject { ["source"]=new JsonObject {["type"]="display",["display_id"]="display_1"},["video"]=new JsonObject {["fps"]=15},["duration_seconds"]=1,["countdown_seconds"]=0,
                ["output"]=new JsonObject {["directory"]=Path.GetDirectoryName(f.Op.MediaPath),["filename"]="formal.mp4",["conflict_policy"]="fail"}},"task341-synthetic",tray);
            tray.Confirm!(ConfirmationDecision.Approve());var rec=Assert.Single(engine._recs.Values);await Until(()=>f.Writer.Read(rec.Id)?.Bundle?.Status is "ready" or "failed");
            Assert.Equal(RecState.completed,rec.State);var row=f.Writer.Read(rec.Id)!;Assert.Equal("ready",row.Bundle!.Status);Assert.NotNull(rec.LastMeta!.PublicationReceipt);
            var expected=Hash(rec.OutputPath);Assert.Equal(expected,row.MediaHash!.Value);engine.Dispose();engine=null;backend.Dispose();backend=null;f.Reopen();
            var native=new Native();var service=new OrdinaryMediaVerificationService(f.Writer.ReadArtifactFacts,new Ancestors(),native);var reads=0;service.ReadForTests=async(s,b,t)=> {reads++;return await s.ReadAsync(b,t);};
            using var host=new Host(f,service,headless);using var c=host.Client();var before=f.Snapshot();
            foreach(var path in new[]{"/api/v1/recording-history","/api/v1/recording-history/"+rec.Id,"/api/v1/history?source=ordinary","/api/v1/recording-history/"+rec.Id+"/artifacts/check",
                "/api/v1/retention/preview?completed_before="+Uri.EscapeDataString(DateTimeOffset.UtcNow.ToString("O"))})await Data(await c.GetAsync(path));
            Assert.Equal(0,reads);Assert.Empty(native.Handles);
            var caps=await Data(await c.GetAsync("/api/v1/capabilities"));var v=caps.GetProperty("ordinary_media_verification");Assert.True(v.GetProperty("supported").GetBoolean());Assert.Equal(1_073_741_824,v.GetProperty("max_bytes").GetInt64());Assert.Equal(30000,v.GetProperty("deadline_ms").GetInt32());Assert.Equal(1,v.GetProperty("max_concurrent").GetInt32());
            Assert.Equal("main_media",v.GetProperty("scope").GetString());Assert.Equal("bundle_generation",v.GetProperty("reference_basis").GetString());Assert.True(v.GetProperty("read_only").GetBoolean());Assert.False(v.GetProperty("deletion_supported").GetBoolean());
            var artifact=caps.GetProperty("ordinary_artifact_check");Assert.True(artifact.GetProperty("metadata_only").GetBoolean());Assert.False(artifact.GetProperty("content_verification").GetBoolean());
            var result=await Verify(c,Endpoint.Replace("/r/","/"+rec.Id+"/"),"");Assert.Equal("match",result.GetProperty("comparison").GetString());Assert.True(result.GetProperty("content_verified").GetBoolean());
            Assert.Equal(expected,result.GetProperty("recorded_hash").GetProperty("value").GetString());Assert.Equal(expected,result.GetProperty("observed_hash").GetProperty("value").GetString());Assert.Equal(row.OutputSizeBytes,result.GetProperty("bytes_read").GetInt64());Assert.Equal(before,f.Snapshot());
            output.WriteLine("Formal approved Engine -> lavfi backend -> original media receipt -> real five-file Bundle -> persisted SHA -> reopened writer -> authenticated loopback HTTP; host mode="+tray.HostMode);
        }
        finally {engine?.Dispose();backend?.Dispose();SystemQuery.SetDisplayTopologyProvider(null);DataDirResolver.ClearOverride();Environment.SetEnvironmentVariable("AGENT_RECORDER_DATA_DIR",priorDir);Environment.SetEnvironmentVariable("AGENT_RECORDER_TEST_MODE",priorMode);}
    }
    [Fact]
    public async Task SqliteSnapshotFreezesHashAndOriginalAnchorTogetherWithoutRepairingChanges()
    {
        using var f=new Fixture();await f.Produce();var hash=f.Writer.Read("r")!.MediaHash!;var once=0;
        f.Writer.ArtifactSnapshotEstablishedForTests=()=> {if(Interlocked.Increment(ref once)==1)f.Sql("PRAGMA foreign_keys=OFF;DELETE FROM ordinary_media_hash;");};
        var service=new OrdinaryMediaVerificationService(f.Writer);var result=(await service.VerifyAsync("r"))!;Assert.True(result.ContentVerified);Assert.Equal(hash,result.RecordedHash);
        await Assert.ThrowsAsync<OrdinaryRecordingHistoryUnavailableException>(()=>service.VerifyAsync("r"));using var c=f.Store.OpenReadOnlyConnection();using var q=c.CreateCommand();q.CommandText="SELECT COUNT(*) FROM ordinary_media_hash";Assert.Equal(0L,q.ExecuteScalar());
    }
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task HttpDeadlineOrDisconnectRetainsBusyWorkerAndOtherApisRemainResponsive(bool disconnect)
    {
        using var f=new Fixture();await f.Produce();var entered=Signal();var release=Signal();var exited=Signal();var deadline=Signal();var cancelled=Signal();var calls=0;
        var service=new OrdinaryMediaVerificationService(f.Writer) {DeadlineForTests=t=>Interlocked.Increment(ref calls)==1?deadline.Task.WaitAsync(t):Task.Delay(30000,t),WorkerExitedForTests=()=>exited.TrySetResult()};
        service.ReadForTests=async(s,b,t)=> {using var registration=t.Register(()=>cancelled.TrySetResult());entered.TrySetResult();await release.Task;return await s.ReadAsync(b,CancellationToken.None);};
        using var host=new Host(f,service);using var c=host.Client();using var raw=new TcpClient();await raw.ConnectAsync(IPAddress.Loopback,host.Api.BoundPort);
        await raw.GetStream().WriteAsync(Encoding.UTF8.GetBytes($"POST {Endpoint} HTTP/1.1\r\nHost: localhost\r\nX-Agent-Recorder-Key: {ApiKeyAuth.CurrentApiKey}\r\nContent-Length: 2\r\nConnection: close\r\n\r\n{{}}"));
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));if(disconnect){raw.Client.LingerState=new(true,0);raw.Client.Close(0);}else deadline.SetResult();
            await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(3));
            if(!disconnect){using var buffer=new MemoryStream();using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(3));await raw.GetStream().CopyToAsync(buffer,timeout.Token);var text=Encoding.UTF8.GetString(buffer.ToArray());using var json=JsonDocument.Parse(text[(text.IndexOf("\r\n\r\n",StringComparison.Ordinal)+4)..]);var data=json.RootElement.GetProperty("data");Assert.Equal("timeout",data.GetProperty("check_state").GetString());Assert.False(data.GetProperty("content_verified").GetBoolean());Assert.Equal(JsonValueKind.Null,data.GetProperty("observed_hash").ValueKind);}
            var busy=await Verify(c);Assert.Equal("busy",busy.GetProperty("check_state").GetString());
            using(var health=await c.GetAsync("/api/v1/health").WaitAsync(TimeSpan.FromSeconds(2)))Assert.NotEqual(HttpStatusCode.InternalServerError,health.StatusCode);
            await Data(await c.GetAsync("/api/v1/recording-history").WaitAsync(TimeSpan.FromSeconds(2)));
        }
        finally {release.TrySetResult();await exited.Task.WaitAsync(TimeSpan.FromSeconds(3));}
        Assert.True((await Verify(c)).GetProperty("content_verified").GetBoolean());
    }
    [Fact]
    public async Task PreCancelledAndInvalidIdNeverDispatchOrAcquireBusyWorker()
    {
        var db=0;var native=new Native();var s=new OrdinaryMediaVerificationService(_=> {db++;return null;},new Ancestors(),native);using var ct=new CancellationTokenSource();ct.Cancel();
        var result=(await s.VerifyAsync("r",ct.Token))!;Assert.Equal("cancelled",result.CheckState);Unverified(result);await Assert.ThrowsAsync<ArgumentException>(()=>s.VerifyAsync("bad/id"));Assert.Equal(0,db);Assert.Empty(native.Handles);Assert.Null(await s.VerifyAsync("absent"));
    }
    [Fact]
    public async Task RealHardLinkRejectsOriginalIdentityBeforeReadingAndReleasesHandle()
    {
        using var f=new Fixture();await f.Produce();var alias=f.Op.MediaPath+".alias";
        Assert.True(CreateHardLinkW(alias,f.Op.MediaPath,IntPtr.Zero),"Owned hardlink preparation failed: "+Marshal.GetLastWin32Error());
        try
        {
            var native=new Native();var reads=0;var service=new OrdinaryMediaVerificationService(f.Writer.ReadArtifactFacts,new Ancestors(),native) {ReadForTests=(_,_,_)=> {reads++;throw new IOException("No read permitted");}};
            var result=(await service.VerifyAsync("r"))!;Unverified(result);Assert.Equal("unsafe_or_unknown_handle_metadata",result.ReasonCode);Assert.Equal(0,reads);Assert.True(native.Handles.Single().IsClosed);
        }
        finally {File.Delete(alias);}
        Assert.True((await new OrdinaryMediaVerificationService(f.Writer).VerifyAsync("r"))!.ContentVerified);
    }
    [Fact]
    public async Task RealLeafSymbolicLinkNeverFollowsTargetWhenPlatformAllowsOwnedPreparation()
    {
        using var f=new Fixture();await f.Produce();var original=f.Op.MediaPath+".original";File.Move(f.Op.MediaPath,original);
        if(!CreateSymbolicLinkW(f.Op.MediaPath,original,2))
        {
            var error=Marshal.GetLastWin32Error();File.Move(original,f.Op.MediaPath);Assert.True(error is 1314 or 5 or 50 or 87,"Unexpected symlink preparation failure.");
            output.WriteLine("Native symlink preparation unavailable; injected reparse case executed, actual branch not covered. error="+error);return;
        }
        try
        {
            var native=new Native();var reads=0;var s=new OrdinaryMediaVerificationService(f.Writer.ReadArtifactFacts,new Ancestors(),native) {ReadForTests=(_,_,_)=> {reads++;throw new IOException("No target read");}};
            Unverified((await s.VerifyAsync("r"))!);Assert.Equal(0,reads);Assert.True(native.Handles.Single().IsClosed);Assert.Equal(160003,new FileInfo(original).Length);
            output.WriteLine("Actual leaf symlink rejected using OPEN_REPARSE_POINT on content handle; target never read.");
        }
        finally {File.Delete(f.Op.MediaPath);File.Move(original,f.Op.MediaPath);}
    }
    [Fact]
    public async Task PublicReadyAndGenuineV35LegacyRemainUnknownWithoutOpeningOrBackfill()
    {
        using(var f=new Fixture())
        {
            var ready=RecordingBundleGenerationResult.Ready(f.Op.BundlePath,BundleContentsGenerationTests.Items());
            Assert.True(f.Writer.TryPublishBundle(new(f.Op,new("ready",ready.BundlePath,null,ready.ContentsEvidence))));Assert.Null(f.Writer.Read("r")!.MediaHash);
            var native=new Native();var before=f.Snapshot();var result=(await new OrdinaryMediaVerificationService(f.Writer.ReadArtifactFacts,new Ancestors(),native).VerifyAsync("r"))!;
            Assert.Equal("recorded_hash_unavailable",result.ReasonCode);Unverified(result);Assert.Empty(native.Handles);Assert.Equal(before,f.Snapshot());
        }
        var root=Path.GetFullPath(Path.Combine(TestHelper.ProjectRoot,".local-data/temp/task341",Guid.NewGuid().ToString("N")));Directory.CreateDirectory(root);var store=new SqliteOperationalStore(Path.Combine(root,"legacy.db"));
        using(var c=new SqliteConnection($"Data Source={store.DatabasePath};Pooling=False"))
        {
            c.Open();SqliteOutputPathCollation.Register(c);using var q=c.CreateCommand();foreach(var m in SqliteSchemaCatalog.Migrations.Where(m=>m.Version<=35)) {
                q.CommandText=m.Definition;q.ExecuteNonQuery();q.CommandText="INSERT INTO schema_migrations VALUES($v,$name,$hash,0)";
                q.Parameters.AddWithValue("$v",m.Version);q.Parameters.AddWithValue("$name",m.Name);q.Parameters.AddWithValue("$hash",m.Checksum);q.ExecuteNonQuery();q.Parameters.Clear();}
        }
        // Register while V36 is not present with the same existing legacy test insert helper.
        using(var c=new SqliteConnection($"Data Source={store.DatabasePath};Pooling=False"))
        {
            c.Open();SqliteOutputPathCollation.Register(c);using var q=c.CreateCommand();q.CommandText="INSERT INTO ordinary_recording_history(recording_id,history_sequence,created_at_utc,status_code,source_type,capture_semantics,source_title,requested_duration_seconds,audio_mode,output_path,completed_at_utc,output_size_bytes,updated_at_utc) VALUES('legacy',1,$at,'completed','display','display_capture','Synthetic',1,'none',$path,$at,12,$at);";
            q.Parameters.AddWithValue("$at",At.UtcTicks);q.Parameters.AddWithValue("$path",Path.Combine(root,"legacy.mp4"));q.ExecuteNonQuery();
        }
        store.Initialize();using var writer=new SqliteOrdinaryRecordingHistoryStore(store);var api=new Native();var r=(await new OrdinaryMediaVerificationService(writer.ReadArtifactFacts,new Ancestors(),api).VerifyAsync("legacy"))!;
        Assert.Equal("recorded_hash_unavailable",r.ReasonCode);Unverified(r);Assert.Empty(api.Handles);using var connection=store.OpenReadOnlyConnection();using var cmd=connection.CreateCommand();cmd.CommandText="SELECT COUNT(*) FROM ordinary_media_hash";Assert.Equal(0L,cmd.ExecuteScalar());
    }
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task CallerCancellationDuringBlockedDatabaseOrReadKeepsHostLease(bool reading)
    {
        using var f=new Fixture();await f.Produce();var entered=Signal();var release=Signal();var exited=Signal();using var ct=new CancellationTokenSource();
        var s=new OrdinaryMediaVerificationService(id=> {if(!reading){entered.TrySetResult();release.Task.GetAwaiter().GetResult();}return f.Writer.ReadArtifactFacts(id);},new Ancestors(),new Native()) {WorkerExitedForTests=()=>exited.TrySetResult()};
        if(reading)s.ReadForTests=async(stream,b,_)=> {entered.TrySetResult();await release.Task;throw new IOException("SECRET late worker exception");};
        var pending=s.VerifyAsync("r",ct.Token);
        try {await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));ct.Cancel();var result=(await pending.WaitAsync(TimeSpan.FromSeconds(3)))!;Unverified(result);Assert.Equal("cancelled",result.CheckState);Assert.Equal("busy",(await new OrdinaryMediaVerificationService(f.Writer).VerifyAsync("r"))!.CheckState);}
        finally {release.TrySetResult();await exited.Task.WaitAsync(TimeSpan.FromSeconds(3));}
        Assert.True((await new OrdinaryMediaVerificationService(f.Writer).VerifyAsync("r"))!.ContentVerified);
    }
    [Fact]
    public async Task HttpSendHalfCloseIsPreservedAndApiStopCancelsWithoutReleasingWorker()
    {
        using var f=new Fixture();await f.Produce();var entered=Signal();var release=Signal();var exited=Signal();var cancelled=Signal();var eof=Signal();
        var s=new OrdinaryMediaVerificationService(f.Writer){WorkerExitedForTests=()=>exited.TrySetResult()};
        s.ReadForTests=async(stream,b,ct)=> {using var reg=ct.Register(()=>cancelled.TrySetResult());entered.TrySetResult();await release.Task;return await stream.ReadAsync(b,CancellationToken.None);};
        using var host=new Host(f,s);host.Api.ArtifactTransportObservedForTests=value=> {if(value&&entered.Task.IsCompleted)eof.TrySetResult();};using var raw=new TcpClient();await raw.ConnectAsync(IPAddress.Loopback,host.Api.BoundPort);
        await raw.GetStream().WriteAsync(Encoding.UTF8.GetBytes($"POST {Endpoint} HTTP/1.1\r\nHost: localhost\r\nX-Agent-Recorder-Key: {ApiKeyAuth.CurrentApiKey}\r\nContent-Length: 0\r\n\r\n"));
        try {await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));raw.Client.Shutdown(SocketShutdown.Send);await eof.Task.WaitAsync(TimeSpan.FromSeconds(3));Assert.False(cancelled.Task.IsCompleted);Assert.True(host.Api.Stop(TimeSpan.FromSeconds(2)));await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(3));Assert.Equal("busy",(await new OrdinaryMediaVerificationService(f.Writer).VerifyAsync("r"))!.CheckState);}
        finally {release.TrySetResult();await exited.Task.WaitAsync(TimeSpan.FromSeconds(3));}
        Assert.True((await new OrdinaryMediaVerificationService(f.Writer).VerifyAsync("r"))!.ContentVerified);
    }
    [DllImport("kernel32.dll",CharSet=CharSet.Unicode,ExactSpelling=true,SetLastError=true)]
    [return:MarshalAs(UnmanagedType.Bool)] private static extern bool CreateHardLinkW(string link,string target,IntPtr security);
    [DllImport("kernel32.dll",CharSet=CharSet.Unicode,ExactSpelling=true,SetLastError=true)]
    [return:MarshalAs(UnmanagedType.U1)] private static extern bool CreateSymbolicLinkW(string link,string target,uint flags);
    private static async Task Until(Func<bool> done)
    {var stop=DateTime.UtcNow.AddSeconds(15);while(!done() && DateTime.UtcNow<stop)await Task.Delay(20);Assert.True(done(),"Synthetic producer did not finish in its owned deadline.");}
}
