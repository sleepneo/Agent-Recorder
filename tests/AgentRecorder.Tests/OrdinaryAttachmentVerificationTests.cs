using System.Reflection;
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
public sealed class OrdinaryAttachmentVerificationTests(ITestOutputHelper output)
{
    private const string Endpoint="/api/v1/recording-history/r/artifacts/verify-attachments";
    private static TaskCompletionSource Signal()=>new(TaskCreationOptions.RunContinuationsAsynchronously);
    private sealed class Fixture : IDisposable
    {
        internal readonly string Root=Path.GetFullPath(Path.Combine(TestHelper.ProjectRoot,".local-data/temp/task343",Guid.NewGuid().ToString("N")));
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
                IdentityEvidence=result.PublicationReceipt?.Evidence,MediaHashEvidence=result.MediaHashEvidence,ContentHashEvidence=trusted?result.PublicationReceipt?.ContentHashes:null})));
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
        {cancellationToken.ThrowIfCancellationRequested();File.WriteAllBytes(args.Last(),(args.Last().EndsWith(".png")?new byte[]{137,80,78,71,13,10,26,10}:new byte[]{255,216}).Concat(new byte[160003]).ToArray());return Task.FromResult(new ExternalProcessResult(0,false,""));}}
    private sealed class Ancestors : IReadOnlyPathMetadata
    {
        internal readonly List<string> Calls=new(); internal Func<string,FileAttributes> Read=File.GetAttributes;
        public FileAttributes GetAttributes(string path) {Calls.Add(path);return Read(path);}
        public long GetLength(string path)=>throw new InvalidOperationException("No ancestor length or content reads permitted.");
    }
    private sealed class Native(string fault="") : IWindowsFileMetadataApi
    {
        private readonly IWindowsFileMetadataApi real=WindowsFileMetadataApi.Instance;
        internal readonly List<SafeFileHandle> Handles=new();internal readonly List<string> Paths=new();internal int IdCalls,Basics;internal long? Length;internal Action? BeforeOpen;internal int DriftHandle=1;
        public bool Supported=>fault!="unsupported";
        public uint DriveType(string root)=>fault=="network"?4u:real.DriveType(root);
        public SafeFileHandle Open(string path,uint access,uint share,uint creation,uint flags,out int error)
        {
            Assert.Equal(0x80000080u,access);Assert.Equal(1u,share);Assert.Equal(3u,creation);Assert.Equal(0x48200000u,flags);
            if(fault=="denied") {error=5;var invalid=new SafeFileHandle(new IntPtr(-1),true);Handles.Add(invalid);return invalid;}
            BeforeOpen?.Invoke();Paths.Add(path);var handle=real.Open(path,access,share,creation,flags,out error);Handles.Add(handle);return handle;
        }
        public bool Basic(SafeFileHandle h,out FileBasicInformation b,out int e)
        {Assert.Contains(h,Handles);var ok=real.Basic(h,out b,out e);Basics++;if(fault=="unknown")return false;if(fault=="reparse")b.Attributes|=(uint)FileAttributes.ReparsePoint;if(fault=="directory")b.Attributes|=(uint)FileAttributes.Directory;if(fault=="drift"&&Handles.Count==DriftHandle&&Basics==DriftHandle*2)b.LastWriteTime++;return ok;}
        public bool Standard(SafeFileHandle h,out FileStandardInformation s,out int e)
        {Assert.Contains(h,Handles);var ok=real.Standard(h,out s,out e);if(fault=="links")s.NumberOfLinks=2;if(Length is { } size)s.EndOfFile=size;return ok;}
        public bool Identity(SafeFileHandle h,out FileIdentityInformation i,out int e)
        {Assert.Contains(h,Handles);IdCalls++;var ok=real.Identity(h,out i,out e);if(fault=="identity")i.IdHigh++;return ok;}
        public uint FinalPath(SafeFileHandle h,StringBuilder b,int c,out int e)
        {Assert.Contains(h,Handles);var n=real.FinalPath(h,b,c,out e);if(fault=="path"){b.Clear().Append(@"C:\foreign.mp4");n=(uint)b.Length;}return n;}
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
        internal Host(Fixture f,OrdinaryAttachmentVerificationService? service,bool headless=false,RecordingEngine? engine=null)
        {
            Environment.SetEnvironmentVariable("AGENT_RECORDER_DATA_DIR",f.Root);ApiKeyAuth.InitializeForTesting(f.Root);
            var audit=new RetentionSharedOutputPathTests.QuietAudit();Engine=engine??new(audit);Engine.ConfigureOrdinaryRecordingHistory(f.Writer);
            if(engine is null)Engine.BackendFactory=_=>throw new InvalidOperationException("Capture forbidden");
            var q=new UnifiedHistoryQueryService(f.Store,f.Writer,headless);var files=new RetentionPreviewQueryTests.MetadataFake();
            var retention=new RetentionPreviewQueryService(q.GetOrdinaryEvidencePage,f.Root,files,files,()=>DateTimeOffset.UtcNow.AddDays(1), TestRuntimeOutputUse.None);
            Api=new(Engine,audit,new Tray(headless),null,null,null,null,null,null,null,null,null,0,
                unifiedHistoryGateway:new SqliteUnifiedHistoryGateway(q,()=>(Sid,Session)),
                artifactCheckGateway:new SqliteOrdinaryArtifactCheckGateway(new(f.Writer)),
                retentionPreviewGateway:new SqliteRetentionPreviewGateway(retention,()=>(Sid,Session)),
                mediaVerificationGateway:new SqliteOrdinaryMediaVerificationGateway(new OrdinaryMediaVerificationService(f.Writer)),
                attachmentVerificationGateway:service is null?null:new SqliteOrdinaryAttachmentVerificationGateway(service));Api.Start();
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
    private static void Unverified(OrdinaryAttachmentVerificationResult r)
    {Assert.False(r.ContentVerified);Assert.Equal("not_checked",r.Comparison);Assert.All(r.Items.Where(i=>!i.ContentVerified),i=>Assert.Null(i.ObservedHash));}
    private static string First(Fixture f)=>Path.Combine(f.Op.BundlePath,"metadata.json");

    [Theory]
    [InlineData("unsupported")][InlineData("network")][InlineData("denied")][InlineData("unknown")][InlineData("reparse")]
    [InlineData("directory")][InlineData("links")][InlineData("identity")][InlineData("path")]
    public async Task UnsafeOrUnknownNativeFactsNeverReadContent(string fault)
    {
        using var f=new Fixture();await f.Produce();var native=new Native(fault);var reads=0;
        var s=new OrdinaryAttachmentVerificationService(f.Writer.ReadArtifactFacts,new Ancestors(),native){ReadForTests=(_,_,_)=> {reads++;throw new IOException("SECRET");}};
        var result=(await s.VerifyAsync("r"))!;Unverified(result);Assert.Equal(0,reads);Assert.Equal(0,result.BytesRead);Assert.All(native.Handles,h=>Assert.True(h.IsClosed));
    }

    [Theory]
    [InlineData("reparse")][InlineData("file")][InlineData("missing")][InlineData("denied")]
    public async Task AncestorFaultsStopBeforeOpeningLeaf(string fault)
    {
        using var f=new Fixture();await f.Produce();var native=new Native();var a=new Ancestors {Read=_=>fault switch {
            "reparse"=>FileAttributes.Directory|FileAttributes.ReparsePoint,"file"=>FileAttributes.Normal,
            "missing"=>throw new DirectoryNotFoundException("SECRET"),_=>throw new UnauthorizedAccessException("SECRET")}};
        Unverified((await new OrdinaryAttachmentVerificationService(f.Writer.ReadArtifactFacts,a,native).VerifyAsync("r"))!);Assert.Empty(native.Handles);Assert.Single(a.Calls);
    }

    [Theory]
    [InlineData("{\"path\":\"C:\\\\foreign.mp4\"}")][InlineData("{\"algorithm\":\"sha256\"}")][InlineData("{\"hash\":\"forged\"}")]
    [InlineData("{\"path\":1,\"path\":2}")][InlineData("{\"files\":[]}")][InlineData("{\"max_bytes\":1}")][InlineData("{\"deadline_ms\":1}")][InlineData("null")][InlineData("[]")][InlineData("1")][InlineData("{")]
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
            var caps=await Data(await c.GetAsync("/api/v1/capabilities"));Assert.False(caps.GetProperty("ordinary_attachment_verification").GetProperty("supported").GetBoolean());}
    }

    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task HttpDeadlineOrDisconnectRetainsBusyWorkerAndOtherApisRemainResponsive(bool disconnect)
    {
        using var f=new Fixture();await f.Produce();var entered=Signal();var release=Signal();var exited=Signal();var deadline=Signal();var cancelled=Signal();var calls=0;
        var service=new OrdinaryAttachmentVerificationService(f.Writer) {DeadlineForTests=t=>Interlocked.Increment(ref calls)==1?deadline.Task.WaitAsync(t):Task.Delay(30000,t),WorkerExitedForTests=()=>exited.TrySetResult()};
        service.ReadForTests=async(s,b,t)=> {using var registration=t.Register(()=>cancelled.TrySetResult());entered.TrySetResult();await release.Task;return await s.ReadAsync(b,CancellationToken.None);};
        using var host=new Host(f,service);using var c=host.Client();using var raw=new TcpClient();await raw.ConnectAsync(IPAddress.Loopback,host.Api.BoundPort);
        await raw.GetStream().WriteAsync(Encoding.UTF8.GetBytes($"POST {Endpoint} HTTP/1.1\r\nHost: localhost\r\nX-Agent-Recorder-Key: {ApiKeyAuth.CurrentApiKey}\r\nContent-Length: 2\r\nConnection: close\r\n\r\n{{}}"));
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));if(disconnect){raw.Client.LingerState=new(true,0);raw.Client.Close(0);}else deadline.SetResult();
            await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(3));
            if(!disconnect){using var buffer=new MemoryStream();using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(3));await raw.GetStream().CopyToAsync(buffer,timeout.Token);var text=Encoding.UTF8.GetString(buffer.ToArray());using var json=JsonDocument.Parse(text[(text.IndexOf("\r\n\r\n",StringComparison.Ordinal)+4)..]);var data=json.RootElement.GetProperty("data");Assert.Equal("timeout",data.GetProperty("check_state").GetString());Assert.False(data.GetProperty("content_verified").GetBoolean());Assert.All(data.GetProperty("items").EnumerateArray(),i=>Assert.Equal(JsonValueKind.Null,i.GetProperty("observed_hash").ValueKind));}
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
        var db=0;var native=new Native();var s=new OrdinaryAttachmentVerificationService(_=> {db++;return null;},new Ancestors(),native);using var ct=new CancellationTokenSource();ct.Cancel();
        var result=(await s.VerifyAsync("r",ct.Token))!;Assert.Equal("cancelled",result.CheckState);Unverified(result);await Assert.ThrowsAsync<ArgumentException>(()=>s.VerifyAsync("bad/id"));Assert.Equal(0,db);Assert.Empty(native.Handles);Assert.Null(await s.VerifyAsync("absent"));
    }

    [Fact]
    public async Task RealHardLinkRejectsOriginalIdentityBeforeReadingAndReleasesHandle()
    {
        using var f=new Fixture();await f.Produce();var alias=First(f)+".alias";
        Assert.True(CreateHardLinkW(alias,First(f),IntPtr.Zero),"Owned hardlink preparation failed: "+Marshal.GetLastWin32Error());
        try
        {
            var native=new Native();var reads=0;var service=new OrdinaryAttachmentVerificationService(f.Writer.ReadArtifactFacts,new Ancestors(),native) {ReadForTests=(_,_,_)=> {reads++;throw new IOException("No read permitted");}};
            var result=(await service.VerifyAsync("r"))!;Unverified(result);Assert.Equal("unsafe_or_unknown_handle_metadata",result.ReasonCode);Assert.Equal(0,reads);Assert.True(native.Handles.Single().IsClosed);
        }
        finally {File.Delete(alias);}
        Assert.True((await new OrdinaryAttachmentVerificationService(f.Writer).VerifyAsync("r"))!.ContentVerified);
    }

    [Fact]
    public async Task RealLeafSymbolicLinkNeverFollowsTargetWhenPlatformAllowsOwnedPreparation()
    {
        using var f=new Fixture();await f.Produce();var original=First(f)+".original";File.Move(First(f),original);
        if(!CreateSymbolicLinkW(First(f),original,2))
        {
            var error=Marshal.GetLastWin32Error();File.Move(original,First(f));Assert.True(error is 1314 or 5 or 50 or 87,"Unexpected symlink preparation failure.");
            output.WriteLine("Native symlink preparation unavailable; injected reparse case executed, actual branch not covered. error="+error);return;
        }
        try
        {
            var native=new Native();var reads=0;var s=new OrdinaryAttachmentVerificationService(f.Writer.ReadArtifactFacts,new Ancestors(),native) {ReadForTests=(_,_,_)=> {reads++;throw new IOException("No target read");}};
            Unverified((await s.VerifyAsync("r"))!);Assert.Equal(0,reads);Assert.True(native.Handles.Single().IsClosed);Assert.True(new FileInfo(original).Length>0);
            output.WriteLine("Actual leaf symlink rejected using OPEN_REPARSE_POINT on content handle; target never read.");
        }
        finally {File.Delete(First(f));File.Move(original,First(f));}
    }

    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task CallerCancellationDuringBlockedDatabaseOrReadKeepsHostLease(bool reading)
    {
        using var f=new Fixture();await f.Produce();var entered=Signal();var release=Signal();var exited=Signal();using var ct=new CancellationTokenSource();
        var s=new OrdinaryAttachmentVerificationService(id=> {if(!reading){entered.TrySetResult();release.Task.GetAwaiter().GetResult();}return f.Writer.ReadArtifactFacts(id);},new Ancestors(),new Native()) {WorkerExitedForTests=()=>exited.TrySetResult()};
        if(reading)s.ReadForTests=async(stream,b,_)=> {entered.TrySetResult();await release.Task;throw new IOException("SECRET late worker exception");};
        var pending=s.VerifyAsync("r",ct.Token);
        try {await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));ct.Cancel();var result=(await pending.WaitAsync(TimeSpan.FromSeconds(3)))!;Unverified(result);Assert.Equal("cancelled",result.CheckState);Assert.Equal("busy",(await new OrdinaryAttachmentVerificationService(f.Writer).VerifyAsync("r"))!.CheckState);}
        finally {release.TrySetResult();await exited.Task.WaitAsync(TimeSpan.FromSeconds(3));}
        Assert.True((await new OrdinaryAttachmentVerificationService(f.Writer).VerifyAsync("r"))!.ContentVerified);
    }

    [Fact]
    public async Task HttpSendHalfCloseIsPreservedAndApiStopCancelsWithoutReleasingWorker()
    {
        using var f=new Fixture();await f.Produce();var entered=Signal();var release=Signal();var exited=Signal();var cancelled=Signal();var eof=Signal();
        var s=new OrdinaryAttachmentVerificationService(f.Writer){WorkerExitedForTests=()=>exited.TrySetResult()};
        s.ReadForTests=async(stream,b,ct)=> {using var reg=ct.Register(()=>cancelled.TrySetResult());entered.TrySetResult();await release.Task;return await stream.ReadAsync(b,CancellationToken.None);};
        using var host=new Host(f,s);host.Api.ArtifactTransportObservedForTests=value=> {if(value&&entered.Task.IsCompleted)eof.TrySetResult();};using var raw=new TcpClient();await raw.ConnectAsync(IPAddress.Loopback,host.Api.BoundPort);
        await raw.GetStream().WriteAsync(Encoding.UTF8.GetBytes($"POST {Endpoint} HTTP/1.1\r\nHost: localhost\r\nX-Agent-Recorder-Key: {ApiKeyAuth.CurrentApiKey}\r\nContent-Length: 0\r\n\r\n"));
        try {await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));raw.Client.Shutdown(SocketShutdown.Send);await eof.Task.WaitAsync(TimeSpan.FromSeconds(3));Assert.False(cancelled.Task.IsCompleted);Assert.True(host.Api.Stop(TimeSpan.FromSeconds(2)));await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(3));Assert.Equal("busy",(await new OrdinaryAttachmentVerificationService(f.Writer).VerifyAsync("r"))!.CheckState);}
        finally {release.TrySetResult();await exited.Task.WaitAsync(TimeSpan.FromSeconds(3));}
        Assert.True((await new OrdinaryAttachmentVerificationService(f.Writer).VerifyAsync("r"))!.ContentVerified);
    }
    [Fact]
    public async Task FiveActualHandlesCompleteInOrderCloseBeforeConfirmationAndNeverWriteDatabase()
    {
        using var f=new Fixture();await f.Produce();f.Reopen();var before=f.Snapshot();var facts=f.Writer.ReadArtifactFacts("r")!;
        var native=new Native();var ancestors=new Ancestors();var reads=0;var closed=0;var names=new List<string>();
        var service=new OrdinaryAttachmentVerificationService(f.Writer.ReadArtifactFacts,ancestors,native);
        service.OpenedForTests=stream=> {
            Assert.Same(native.Handles.Last(),stream.SafeFileHandle);Assert.All(native.Handles.SkipLast(1),h=>Assert.True(h.IsClosed));
            var path=native.Paths.Last()[4..];names.Add(Path.GetFileName(path));
            Assert.Throws<IOException>(()=>File.Delete(path));Assert.Throws<IOException>(()=>File.Move(path,path+".owned"));
            Assert.Throws<IOException>(()=>{using var writer=new FileStream(path,FileMode.Open,FileAccess.Write,FileShare.ReadWrite|FileShare.Delete);});
        };
        service.ReadForTests=async(stream,buffer,ct)=> {Assert.InRange(buffer.Length,1,65536);Assert.Same(native.Handles.Last(),stream.SafeFileHandle);reads++;return await stream.ReadAsync(buffer,ct);};
        service.ClosedForTests=()=>{Assert.True(native.Handles.Last().IsClosed);closed++;};
        var result=(await service.VerifyAsync("r"))!;Assert.True(result.ContentVerified);Assert.Equal("match",result.Comparison);Assert.Equal(5,closed);
        Assert.Equal(facts.Media.Bundle!.ContentsEvidence!.Contents.Select(i=>i.Name),names);Assert.Equal(5,native.Handles.Count);Assert.Equal(10,native.IdCalls);
        Assert.Equal(ancestors.Calls.Count,ancestors.Calls.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.Equal(facts.Media.Bundle.ContentsEvidence.Contents.Sum(i=>i.SizeBytes),result.BytesRead);
        Assert.Equal(facts.Media.Bundle.ContentsEvidence.Contents.Sum(i=>(i.SizeBytes+65535)/65536),reads);
        foreach(var item in result.Items){Assert.Equal(Hash(Path.Combine(f.Op.BundlePath,item.Name)),item.ObservedHash!.Value);Assert.Equal(item.RecordedHash!.Value,item.ObservedHash.Value);Assert.True(item.ContentVerified);Assert.True(item.CheckCompletedAtUtc>=item.CheckStartedAtUtc);}
        for(var i=1;i<5;i++)Assert.True(result.Items[i].CheckStartedAtUtc>=result.Items[i-1].CheckCompletedAtUtc);
        Assert.Equal(before,f.Snapshot());
        // Earlier observations remain immutable even after the original bytes change.
        File.WriteAllBytes(First(f),new byte[result.Items[0].BytesRead]);Assert.True(result.ContentVerified);
        Assert.Equal("digest_mismatch",(await service.VerifyAsync("r"))!.ReasonCode);
    }

    [Theory]
    [InlineData("inplace","digest_mismatch")][InlineData("replace","publication_identity_mismatch")]
    [InlineData("short","file_size_mismatch")][InlineData("long","file_size_mismatch")]
    [InlineData("missing","file_missing")][InlineData("writer","file_inaccessible")][InlineData("directory","file_inaccessible")]
    public async Task RealPhysicalFaultAtThirdItemPreservesOnlyCompletedPrefix(string fault,string reason)
    {
        using var f=new Fixture();await f.Produce();var facts=f.Writer.ReadArtifactFacts("r")!;var path=Path.Combine(f.Op.BundlePath,"first_frame.png");
        var size=(int)facts.Media.Bundle!.ContentsEvidence!.Contents[2].SizeBytes;var before=f.Snapshot();FileStream? writer=null;
        if(fault=="inplace")File.WriteAllBytes(path,new byte[size]);
        if(fault=="replace"){File.Move(path,path+".original");File.WriteAllBytes(path,new byte[size]);}
        if(fault=="short")File.WriteAllBytes(path,new byte[1]);if(fault=="long")File.AppendAllText(path,"x");
        if(fault=="missing")File.Delete(path);if(fault=="directory"){File.Delete(path);Directory.CreateDirectory(path);}
        if(fault=="writer")writer=new(path,FileMode.Open,FileAccess.Write,FileShare.ReadWrite|FileShare.Delete);
        var native=new Native();var readNames=new List<string>();var s=new OrdinaryAttachmentVerificationService(f.Writer.ReadArtifactFacts,new Ancestors(),native);
        s.ReadForTests=async(stream,buffer,ct)=>{readNames.Add(Path.GetFileName(native.Paths.Last()));return await stream.ReadAsync(buffer,ct);};
        try
        {
            var result=(await s.VerifyAsync("r"))!;Assert.False(result.ContentVerified);Assert.Equal(reason,result.ReasonCode);Assert.Equal(3,native.Handles.Count);
            Assert.All(result.Items.Take(2),item=>Assert.True(item.ContentVerified));Assert.All(result.Items.Skip(3),item=>{Assert.Equal("not_checked",item.Comparison);Assert.Null(item.ObservedHash);Assert.Equal(0,item.BytesRead);});
            var third=result.Items[2];Assert.False(third.ContentVerified);
            if(fault=="inplace"){Assert.NotNull(third.ObservedHash);Assert.Equal("mismatch",result.Comparison);Assert.Equal(Hash(path),third.ObservedHash!.Value);}
            else {Assert.Null(third.ObservedHash);Assert.Equal(0,third.BytesRead);Assert.DoesNotContain("first_frame.png",readNames);Assert.Equal("not_checked",result.Comparison);}
            Assert.All(native.Handles,h=>Assert.True(h.IsClosed));Assert.Equal(before,f.Snapshot());
        }
        finally {writer?.Dispose();}
    }

    [Theory] [InlineData("short")][InlineData("error")][InlineData("drift")][InlineData("cancel")]
    public async Task ThirdItemPartialReadNeverExposesPrefixHash(string fault)
    {
        using var f=new Fixture();await f.Produce();var native=new Native(fault=="drift"?"drift":"");var calls=0;using var ct=new CancellationTokenSource();
        // Drift on the third handle's post-observation only.
        var s=new OrdinaryAttachmentVerificationService(f.Writer.ReadArtifactFacts,new Ancestors(),native);
        s.ReadForTests=async(stream,buffer,token)=> {
            if(native.Handles.Count==3)
            {
                if(++calls==1){var count=await stream.ReadAsync(buffer[..3],token);if(fault=="cancel")ct.Cancel();return count;}
                if(fault=="short")return 0;if(fault=="error")throw new IOException("PRIVATE raw native path");
            }
            return await stream.ReadAsync(buffer,token);
        };
        if(fault=="drift")native.DriftHandle=3;
        var result=(await s.VerifyAsync("r",ct.Token))!;Assert.False(result.ContentVerified);Assert.Equal(5,result.Items.Count);
        Assert.All(result.Items.Take(2),i=>Assert.True(i.ContentVerified));Assert.Null(result.Items[2].ObservedHash);Assert.False(result.Items[2].ContentVerified);
        Assert.All(result.Items.Skip(3),i=>{Assert.Null(i.ObservedHash);Assert.Equal(0,i.BytesRead);});
        Assert.All(native.Handles,h=>Assert.True(h.IsClosed));
    }

    [Theory]
    [InlineData("DELETE FROM ordinary_bundle_attachment_hash;")]
    [InlineData("DELETE FROM ordinary_bundle_attachment_hash_items WHERE ordinal=2;")]
    [InlineData("UPDATE ordinary_bundle_attachment_hash SET operation_id='ffffffffffffffffffffffffffffffff';")]
    [InlineData("UPDATE ordinary_bundle_attachment_hash_items SET hash_value='abcd' WHERE ordinal=0;")]
    [InlineData("UPDATE ordinary_bundle_attachment_hash_items SET algorithm='sha512' WHERE ordinal=0;")]
    [InlineData("UPDATE ordinary_bundle_attachment_hash_items SET file_id=zeroblob(16) WHERE ordinal=0;")]
    [InlineData("UPDATE ordinary_bundle_attachment_hash_items SET output_size_bytes='wrong' WHERE ordinal=0;")]
    [InlineData("UPDATE ordinary_bundle_attachment_hash_items SET basis='capture' WHERE ordinal=0;")]
    [InlineData("UPDATE ordinary_bundle_attachment_hash_items SET output_path=output_path||'.foreign' WHERE ordinal=0;")]
    public async Task CorruptV37RejectsAuthenticatedHttpBeforeAnyContentOpen(string sql)
    {
        using var f=new Fixture();await f.Produce();f.Sql("PRAGMA foreign_keys=OFF;PRAGMA ignore_check_constraints=ON;"+sql);var before=f.Snapshot();var native=new Native();var a=new Ancestors();
        using var host=new Host(f,new(f.Writer.ReadArtifactFacts,a,native));using var c=host.Client();using var r=await c.PostAsync(Endpoint,new StringContent("{}"));
        Assert.Equal(HttpStatusCode.ServiceUnavailable,r.StatusCode);var text=await r.Content.ReadAsStringAsync();Assert.DoesNotContain(f.Root,text);Assert.DoesNotContain("foreign",text);Assert.Empty(native.Handles);Assert.Empty(a.Calls);Assert.Equal(before,f.Snapshot());
    }

    [Fact]
    public async Task WalSnapshotFreezesAllFiveHashesAndIdentitiesTogetherWithoutRepair()
    {
        using var f=new Fixture();await f.Produce();var original=f.Writer.Read("r")!.Bundle;var calls=0;
        f.Writer.ArtifactSnapshotEstablishedForTests=()=>{if(Interlocked.Increment(ref calls)==1)f.Sql("PRAGMA foreign_keys=OFF;DELETE FROM ordinary_bundle_attachment_hash_items;");};
        var native=new Native();var s=new OrdinaryAttachmentVerificationService(f.Writer.ReadArtifactFacts,new Ancestors(),native);
        var result=(await s.VerifyAsync("r"))!;Assert.True(result.ContentVerified);Assert.Equal(5,native.Handles.Count);
        for(var i=0;i<5;i++)Assert.Equal(original!.ContentHash(i),result.Items[i].RecordedHash);
        await Assert.ThrowsAsync<OrdinaryRecordingHistoryUnavailableException>(()=>s.VerifyAsync("r"));Assert.Equal(5,native.Handles.Count);
        using var c=f.Store.OpenReadOnlyConnection();using var q=c.CreateCommand();q.CommandText="SELECT COUNT(*) FROM ordinary_bundle_attachment_hash_items";Assert.Equal(0L,q.ExecuteScalar());
    }

    [Theory] [InlineData("unready")][InlineData("unknown")][InlineData("not_ordinary")][InlineData("unfinished")]
    public async Task IneligibleFrozenFactsNeverTouchFilesOrBackfill(string fault)
    {
        using var f=new Fixture();if(fault=="unknown")await f.Produce(trusted:false);else if(fault!="unready")await f.Produce();
        var facts=f.Writer.ReadArtifactFacts("r")!;
        if(fault=="not_ordinary")facts=facts with {Media=facts.Media with {OutputPath="owned.wav"}};
        if(fault=="unfinished")facts=facts with {Media=facts.Media with {Status="cancelled"}};
        var before=f.Snapshot();var native=new Native();var a=new Ancestors();var r=(await new OrdinaryAttachmentVerificationService(_=>facts,a,native).VerifyAsync("r"))!;
        Unverified(r);Assert.Equal(5,r.Items.Count);Assert.Empty(a.Calls);Assert.Empty(native.Handles);Assert.Equal(before,f.Snapshot());
    }

    private static OrdinaryArtifactFacts Resized(OrdinaryArtifactFacts facts,IReadOnlyList<long> sizes,string? media=null)
    {
        var b=facts.Media.Bundle!;var old=b.IdentityEvidence!;media??=old.MediaPath;
        var bundle=Path.Combine(Path.GetDirectoryName(media)!,Path.GetFileNameWithoutExtension(media)+".bundle");
        var contents=new RecordingBundleContentsEvidence(b.ContentsEvidence!.Contents.Select((item,i)=>new RecordingBundleContentItem(item.Name,item.MediaType,sizes[i])));
        var identity=new BundleIdentityEvidence(old.RecordingId,old.OperationId,media,bundle,contents,old.Anchors.Select((a,i)=>a with {Path=Path.Combine(bundle,contents.Contents[i].Name),Length=sizes[i]}));
        return facts with {Media=facts.Media with {OutputPath=media,Bundle=b with {Path=bundle,ContentsEvidence=contents,IdentityEvidence=identity,ContentHashEvidence=BundlePublicationReceipt.AttachmentHashes.FromStored(identity,b.ContentHashEvidence!.Values)}}};
    }

    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task OversizeAndCheckedSumOverflowRefusedBeforeAncestorsOrOpen(bool overflow)
    {
        using var f=new Fixture();await f.Produce();var sizes=overflow?new[]{long.MaxValue,1L,1,1,1}:new[]{OrdinaryAttachmentVerificationService.MaxBytes-3,1L,1,1,1};
        var facts=Resized(f.Writer.ReadArtifactFacts("r")!,sizes);var native=new Native();var a=new Ancestors();
        var r=(await new OrdinaryAttachmentVerificationService(_=>facts,a,native).VerifyAsync("r"))!;Unverified(r);Assert.Equal("attachment_byte_limit",r.ReasonCode);Assert.Empty(native.Handles);Assert.Empty(a.Calls);Assert.Equal(0,r.BytesRead);
    }

    [Fact]
    public async Task Exactly64MiBIsOneRequestBudgetButSimulatedReadCannotMintActualDigest()
    {
        using var f=new Fixture();await f.Produce();var original=f.Writer.ReadArtifactFacts("r")!;
        var sizes=original.Media.Bundle!.ContentsEvidence!.Contents.Select(i=>i.SizeBytes).ToArray();sizes[4]=OrdinaryAttachmentVerificationService.MaxBytes-sizes.Take(4).Sum();
        var facts=Resized(original,sizes);var native=new Native();var simulated=0;
        native.BeforeOpen=()=>{if(native.Handles.Count==4)native.Length=sizes[4];};
        var s=new OrdinaryAttachmentVerificationService(_=>facts,new Ancestors(),native) {ReadForTests=async(stream,buffer,ct)=>{
            if(native.Handles.Count==5){simulated++;return buffer.Length;}return await stream.ReadAsync(buffer,ct);}};
        var r=(await s.VerifyAsync("r"))!;Assert.Equal(OrdinaryAttachmentVerificationService.MaxBytes,r.BytesRead);Assert.Equal((sizes[4]+65535)/65536,simulated);Assert.Equal("metadata_changed",r.ReasonCode);Assert.False(r.ContentVerified);Assert.Equal(5,native.Handles.Count);
        Assert.All(r.Items.Take(4),i=>Assert.True(i.ContentVerified));Assert.Null(r.Items[4].ObservedHash);
        output.WriteLine("Trusted sum exactly 64MiB admitted; first four actual files compared, last simulated reads bring total to 64MiB but fail actual Position/Length. No large-file success claim.");
    }

    [Theory] [InlineData(62,true)] [InlineData(63,false)]
    public async Task WholeRequestDeduplicatesAtMost64Ancestors(int directories,bool admitted)
    {
        using var f=new Fixture();await f.Produce();var media=@"C:\"+string.Concat(Enumerable.Repeat("d\\",directories))+"media.mp4";
        var facts=Resized(f.Writer.ReadArtifactFacts("r")!,f.Writer.Read("r")!.Bundle!.ContentsEvidence!.Contents.Select(i=>i.SizeBytes).ToArray(),media);
        var a=new Ancestors {Read=_=>FileAttributes.Directory};var native=new Native("denied");
        var r=(await new OrdinaryAttachmentVerificationService(_=>facts,a,native).VerifyAsync("r"))!;Unverified(r);
        if(admitted){Assert.Equal(64,a.Calls.Count);Assert.Single(native.Handles);}else {Assert.Empty(a.Calls);Assert.Empty(native.Handles);Assert.Equal("ancestor_check_limit",r.ReasonCode);}
    }

    [Theory] [InlineData("reserved")][InlineData("long")][InlineData("binding")]
    public async Task UnsafeOrUnboundFrozenPathsNeverOpen(string fault)
    {
        using var f=new Fixture();await f.Produce();var facts=f.Writer.ReadArtifactFacts("r")!;
        if(fault=="long")
        {
            var path=@"C:\"+new string('a',1025)+".mp4";
            Assert.Equal("path_check_limit",OrdinaryArtifactMetadataSampler.PathReason(path,out _));
            Assert.Throws<InvalidDataException>(()=>Resized(facts,facts.Media.Bundle!.ContentsEvidence!.Contents.Select(i=>i.SizeBytes).ToArray(),path));return;
        }
        if(fault=="binding")facts=facts with {Media=facts.Media with {OutputPath=@"C:\foreign.mp4"}};
        else facts=Resized(facts,facts.Media.Bundle!.ContentsEvidence!.Contents.Select(i=>i.SizeBytes).ToArray(),fault=="reserved"?@"C:\CON\media.mp4":@"C:\"+new string('a',1025)+".mp4");
        var a=new Ancestors();var native=new Native();Unverified((await new OrdinaryAttachmentVerificationService(_=>facts,a,native).VerifyAsync("r"))!);Assert.Empty(a.Calls);Assert.Empty(native.Handles);
    }

    [Theory] [InlineData(false,"database")][InlineData(false,"native")][InlineData(false,"read")][InlineData(false,"close")]
    [InlineData(true,"database")][InlineData(true,"native")][InlineData(true,"read")]
    public async Task SharedSlotBothDirectionsAcrossRecreationAndLateTimeoutUntilActualExit(bool mainFirst,string stage)
    {
        using var f=new Fixture();await f.Produce();var entered=Signal();var release=Signal();var exited=Signal();var deadline=Signal();var facts=f.Writer.ReadArtifactFacts("r");
        var native=new Native();if(stage=="native")native.BeforeOpen=()=>{entered.TrySetResult();release.Task.GetAwaiter().GetResult();};
        OrdinaryArtifactFacts? Read(string _) {if(stage=="database"){entered.TrySetResult();release.Task.GetAwaiter().GetResult();}return facts;}
        var attachment=new OrdinaryAttachmentVerificationService(Read,new Ancestors(),native){DeadlineForTests=_=>deadline.Task,WorkerExitedForTests=()=>exited.TrySetResult()};
        var main=new OrdinaryMediaVerificationService(Read,new Ancestors(),native){DeadlineForTests=_=>deadline.Task,WorkerExitedForTests=()=>exited.TrySetResult()};
        async ValueTask<int> ReadBytes(FileStream stream,Memory<byte> buffer,CancellationToken _) {entered.TrySetResult();await release.Task;return await stream.ReadAsync(buffer,CancellationToken.None);}
        if(stage=="read"){attachment.ReadForTests=ReadBytes;main.ReadForTests=ReadBytes;}
        if(stage=="close")attachment.ClosedForTests=()=>{entered.TrySetResult();release.Task.GetAwaiter().GetResult();};
        var work=mainFirst?(Task)main.VerifyAsync("r"):attachment.VerifyAsync("r");
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
            Assert.Equal("busy",(await new OrdinaryMediaVerificationService(f.Writer).VerifyAsync("r"))!.CheckState);
            Assert.Equal("busy",(await new OrdinaryAttachmentVerificationService(f.Writer).VerifyAsync("r"))!.CheckState);
            deadline.SetResult();await work.WaitAsync(TimeSpan.FromSeconds(3));
            if(mainFirst)Assert.Equal("timeout",(await (Task<OrdinaryMediaVerificationResult?>)work)!.CheckState);
            else {var r=(await (Task<OrdinaryAttachmentVerificationResult?>)work)!;Assert.Equal("timeout",r.CheckState);Unverified(r);}
            Assert.Equal("busy",(await new OrdinaryMediaVerificationService(f.Writer).VerifyAsync("r"))!.CheckState);
            Assert.Equal("busy",(await new OrdinaryAttachmentVerificationService(f.Writer).VerifyAsync("r"))!.CheckState);
        }
        finally {release.TrySetResult();await exited.Task.WaitAsync(TimeSpan.FromSeconds(3));}
        Assert.All(native.Handles,h=>Assert.True(h.IsClosed));Assert.True((await new OrdinaryMediaVerificationService(f.Writer).VerifyAsync("r"))!.ContentVerified);
        Assert.True((await new OrdinaryAttachmentVerificationService(f.Writer).VerifyAsync("r"))!.ContentVerified);
    }

    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task TimeoutOrCancellationFreezesPrefixAcrossLateReadCompletion(bool cancel)
    {
        using var f=new Fixture();await f.Produce();var native=new Native();var entered=Signal();var release=Signal();var exited=Signal();var deadline=Signal();using var ct=new CancellationTokenSource();
        var s=new OrdinaryAttachmentVerificationService(f.Writer.ReadArtifactFacts,new Ancestors(),native){DeadlineForTests=_=>deadline.Task.WaitAsync(ct.Token),WorkerExitedForTests=()=>exited.TrySetResult()};
        s.ReadForTests=async(stream,buffer,_)=>{if(native.Handles.Count==3){entered.TrySetResult();await release.Task;}return await stream.ReadAsync(buffer,CancellationToken.None);};
        var work=s.VerifyAsync("r",ct.Token);OrdinaryAttachmentVerificationResult? result=null;string? serialized=null;
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));if(cancel)ct.Cancel();else deadline.SetResult();result=await work.WaitAsync(TimeSpan.FromSeconds(3));
            Assert.Equal(cancel?"cancelled":"timeout",result!.CheckState);Assert.False(result.ContentVerified);Assert.All(result.Items.Take(2),i=>Assert.True(i.ContentVerified));Assert.All(result.Items.Skip(2),i=>Assert.Null(i.ObservedHash));serialized=JsonSerializer.Serialize(result);
            Assert.Equal("busy",(await new OrdinaryMediaVerificationService(f.Writer).VerifyAsync("r"))!.CheckState);
        }
        finally {release.TrySetResult();await exited.Task.WaitAsync(TimeSpan.FromSeconds(3));}
        Assert.Equal(serialized,JsonSerializer.Serialize(result));Assert.All(native.Handles,h=>Assert.True(h.IsClosed));
    }

    [Theory] [InlineData("database")][InlineData("open")][InlineData("read")][InlineData("close")][InlineData("dispatch")]
    public async Task ExceptionsReleaseSharedSlotAfterActualWorkerExit(string stage)
    {
        using var f=new Fixture();await f.Produce();var exited=Signal();var native=new Native();if(stage=="open")native.BeforeOpen=()=>throw new IOException("PRIVATE");
        var s=new OrdinaryAttachmentVerificationService(id=>stage=="database"?throw new InvalidOperationException("PRIVATE"):f.Writer.ReadArtifactFacts(id),new Ancestors(),native) {WorkerExitedForTests=()=>exited.TrySetResult()};
        if(stage=="read")s.ReadForTests=(_,_,_)=>throw new IOException("PRIVATE");if(stage=="close")s.ClosedForTests=()=>throw new IOException("PRIVATE");if(stage=="dispatch")s.DeadlineForTests=_=>throw new InvalidOperationException("PRIVATE");
        var r=(await s.VerifyAsync("r"))!;Unverified(r);Assert.Equal("failed",r.CheckState);if(stage!="dispatch")await exited.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.All(native.Handles,h=>Assert.True(h.IsClosed));Assert.True((await new OrdinaryMediaVerificationService(f.Writer).VerifyAsync("r"))!.ContentVerified);
    }

    [Fact]
    public async Task HistoryMetadataRetentionNeverDispatchContentAndCapabilitiesDeclareDistinctSharedBudgets()
    {
        using var f=new Fixture();await f.Produce();var db=0;var native=new Native();using var host=new Host(f,new(id=>{db++;return f.Writer.ReadArtifactFacts(id);},new Ancestors(),native));using var c=host.Client();
        foreach(var endpoint in new[]{"/api/v1/recording-history","/api/v1/recording-history/r","/api/v1/history?source=ordinary","/api/v1/recording-history/r/artifacts/check","/api/v1/retention/preview?completed_before="+Uri.EscapeDataString(DateTimeOffset.UtcNow.ToString("O"))})await Data(await c.GetAsync(endpoint));
        Assert.Equal(0,db);Assert.Empty(native.Handles);var caps=await Data(await c.GetAsync("/api/v1/capabilities"));var a=caps.GetProperty("ordinary_attachment_verification");
        Assert.True(a.GetProperty("supported").GetBoolean());Assert.Equal(5,a.GetProperty("max_files").GetInt32());Assert.Equal(64,a.GetProperty("max_distinct_ancestors").GetInt32());Assert.Equal(67108864,a.GetProperty("max_bytes").GetInt64());Assert.Equal(30000,a.GetProperty("deadline_ms").GetInt32());Assert.Equal(1,a.GetProperty("max_concurrent").GetInt32());
        Assert.Equal("bundle_publication",a.GetProperty("reference_basis").GetString());Assert.Equal("bundle_attachments",a.GetProperty("scope").GetString());Assert.Contains("verify-media",a.GetProperty("shared_with").GetString());Assert.Equal(OrdinaryAttachmentVerificationService.ObservationSemantics,a.GetProperty("observation_semantics").GetString());
        Assert.Equal(1073741824,caps.GetProperty("ordinary_media_verification").GetProperty("max_bytes").GetInt64());var before=f.Snapshot();var result=await Verify(c,body:"");Assert.True(result.GetProperty("content_verified").GetBoolean());Assert.Equal(5,result.GetProperty("items").GetArrayLength());Assert.Equal(before,f.Snapshot());
        Assert.DoesNotContain(f.Root,result.GetRawText());
        var evidence=Path.Combine(Path.GetTempPath(),"task343-verification-evidence");Directory.CreateDirectory(evidence);
        File.WriteAllText(Path.Combine(evidence,"sample-response.json"),result.GetRawText());
        File.WriteAllText(Path.Combine(evidence,"sample-capabilities.json"),a.GetRawText());
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
                ["output"]=new JsonObject {["directory"]=Path.GetDirectoryName(f.Op.MediaPath),["filename"]="formal.mp4",["conflict_policy"]="fail"}},"task343-synthetic",tray);
            tray.Confirm!(ConfirmationDecision.Approve());var rec=Assert.Single(engine._recs.Values);
            await Until(()=>f.Writer.Read(rec.Id)?.Bundle?.Status is "ready" or "failed");Assert.Equal(RecState.completed,rec.State);var row=f.Writer.Read(rec.Id)!;AssertHashes(row.Bundle!);
            Assert.Equal("ready",row.Bundle!.Status);Assert.NotNull(rec.LastMeta!.PublicationReceipt);Assert.NotNull(row.MediaHash);
            Assert.Equal(Hash(rec.OutputPath),row.MediaHash!.Value);Assert.False(new FfmpegOutputProber().Probe(rec.OutputPath).HasAudioStream);
            using(var metadata=JsonDocument.Parse(File.ReadAllText(Path.Combine(row.Bundle.Path!,"metadata.json"))))Assert.Equal(row.MediaHash.Value,metadata.RootElement.GetProperty("media").GetProperty("sha256").GetString());
            await AssertFormalHttp(engine,f.Store,f.Writer,row,headless);engine.Dispose();engine=null;backend.Dispose();backend=null;f.Reopen();
            engine=new(audit);engine.ConfigureOrdinaryRecordingHistory(f.Writer);engine.SetTray(tray);Assert.Equal(row.MediaHash,f.Writer.Read(rec.Id)!.MediaHash);await AssertFormalHttp(engine,f.Store,f.Writer,row,headless);
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
        var row=history.Read(rec.Id)!;AssertHashes(row.Bundle!);Assert.Equal("ready",row.Bundle!.Status);Assert.NotNull(row.MediaHash);Assert.NotNull(rec.LastMeta!.PublicationReceipt);
        Assert.True(new FfmpegOutputProber().Probe(rec.OutputPath).HasAudioStream);Assert.Equal(Hash(rec.OutputPath),row.MediaHash!.Value);
        await AssertFormalHttp(engine,store,history,row,false);Assert.True(history.DrainAndDispose(TimeSpan.FromSeconds(3)));
        using var reopened=new SqliteOrdinaryRecordingHistoryStore(store);await AssertFormalHttp(engine,store,reopened,reopened.Read(rec.Id)!,false);AvMuxOwnershipTests.AssertNoOwnedMux(rec.OutputPath);
    }
    private static T Field<T>(object fixture,string name)=>(T)fixture.GetType().GetField(name,BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(fixture)!;
    private static async Task Generate(string[] args)
    {
        var info=new ProcessStartInfo(Path.Combine(TestHelper.ProjectRoot,"tools/ffmpeg/bin/ffmpeg.exe")){UseShellExecute=false,CreateNoWindow=true,RedirectStandardError=true};foreach(var arg in args)info.ArgumentList.Add(arg);
        using var p=Process.Start(info)!;var err=p.StandardError.ReadToEndAsync();await p.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30));Assert.True(p.ExitCode==0,await err);
    }

    private static string NewRoot(){var p=Path.GetFullPath(Path.Combine(TestHelper.ProjectRoot,".local-data/temp/task343",Guid.NewGuid().ToString("N")));Directory.CreateDirectory(p);return p;}
    private static void AssertHashes(OrdinaryRecordingBundleSnapshot bundle)
    {
        Assert.Equal(5,bundle.ContentHashEvidence!.Values.Count);
        for(var i=0;i<5;i++)Assert.Equal(Hash(Path.Combine(bundle.Path!,bundle.ContentsEvidence!.Contents[i].Name)),bundle.ContentHash(i)!.Value);
    }
    private static async Task AssertFormalHttp(RecordingEngine engine,SqliteOperationalStore store,SqliteOrdinaryRecordingHistoryStore writer,OrdinaryRecordingHistorySnapshot row,bool headless)
    {
        var api=new ApiServer(engine,new RetentionSharedOutputPathTests.QuietAudit(),new Tray(headless),null,null,null,null,null,null,null,null,null,0,
            attachmentVerificationGateway:new SqliteOrdinaryAttachmentVerificationGateway(new OrdinaryAttachmentVerificationService(writer)),
            mediaVerificationGateway:new SqliteOrdinaryMediaVerificationGateway(new OrdinaryMediaVerificationService(writer)));
        try
        {
            api.Start();using var c=new HttpClient(new HttpClientHandler{UseProxy=false}){BaseAddress=new($"http://127.0.0.1:{api.BoundPort}")};c.DefaultRequestHeaders.Add("X-Agent-Recorder-Key",ApiKeyAuth.CurrentApiKey);
            var endpoint="/api/v1/recording-history/"+row.RecordingId+"/artifacts/verify-attachments";
            var data=await Post(c,endpoint,"{}");Assert.True(data.GetProperty("content_verified").GetBoolean());Assert.Equal("match",data.GetProperty("comparison").GetString());
            var items=data.GetProperty("items").EnumerateArray().ToArray();Assert.Equal(5,items.Length);
            for(var i=0;i<5;i++){Assert.Equal(row.Bundle!.ContentsEvidence!.Contents[i].Name,items[i].GetProperty("name").GetString());Assert.Equal(Hash(Path.Combine(row.Bundle.Path!,items[i].GetProperty("name").GetString()!)),items[i].GetProperty("observed_hash").GetProperty("value").GetString());Assert.Equal(row.Bundle.ContentHash(i)!.Value,items[i].GetProperty("recorded_hash").GetProperty("value").GetString());}
            Assert.DoesNotContain(row.Bundle!.Path!,data.GetRawText());
        }
        finally {api.Stop();}
    }
    [Fact]
    public async Task GenuineV36ReadyLegacyNeverOpensBlockedMetadataOrBackfills()
    {
        var root=NewRoot();var db=Path.Combine(root,"legacy.db");var media=Path.Combine(root,"absent.mp4");var bundle=OrdinaryRecordingBundleOperation.DerivePath(media);
        Directory.CreateDirectory(bundle);File.WriteAllText(Path.Combine(bundle,"metadata.json"),"{\"media\":{\"sha256\":\""+new string('a',64)+"\"}}");
        using(var c=new SqliteConnection($"Data Source={db};Pooling=False"))
        {
            c.Open();SqliteOutputPathCollation.Register(c);using var cmd=c.CreateCommand();foreach(var m in SqliteSchemaCatalog.Migrations.Where(m=>m.Version<=36)){cmd.CommandText=m.Definition;cmd.ExecuteNonQuery();cmd.CommandText="INSERT INTO schema_migrations VALUES($v,$name,$hash,0)";cmd.Parameters.AddWithValue("$v",m.Version);cmd.Parameters.AddWithValue("$name",m.Name);cmd.Parameters.AddWithValue("$hash",m.Checksum);cmd.ExecuteNonQuery();cmd.Parameters.Clear();}
            cmd.CommandText="INSERT INTO ordinary_recording_history(recording_id,history_sequence,created_at_utc,status_code,source_type,capture_semantics,source_title,audio_mode,output_path,started_at_utc,completed_at_utc,output_size_bytes,output_duration_ms,updated_at_utc) VALUES('legacy',1,$at,'completed','display','display_capture','Synthetic','none',$media,$at,$at,100,1000,$at);INSERT INTO ordinary_recording_bundles VALUES('legacy',$media,'ffffffffffffffffffffffffffffffff','ready',$bundle,NULL);";cmd.Parameters.AddWithValue("$at",At.UtcTicks);cmd.Parameters.AddWithValue("$media",media);cmd.Parameters.AddWithValue("$bundle",bundle);cmd.ExecuteNonQuery();
        }
        var before=UnifiedHistoryProfileFilterTests.RawSnapshot(db,schemaThrough:36);var store=new SqliteOperationalStore(db);using var blocked=new FileStream(Path.Combine(bundle,"metadata.json"),FileMode.Open,FileAccess.Read,FileShare.None);
        store.Initialize();store.Initialize();Assert.Equal(before,UnifiedHistoryProfileFilterTests.RawSnapshot(db,schemaThrough:36));using var writer=new SqliteOrdinaryRecordingHistoryStore(store);Assert.Null(writer.Read("legacy")!.Bundle!.ContentHashEvidence);
        var native=new Native();var ancestors=new Ancestors();var result=(await new OrdinaryAttachmentVerificationService(writer.ReadArtifactFacts,ancestors,native).VerifyAsync("legacy"))!;Unverified(result);Assert.Equal("recorded_hash_unavailable",result.ReasonCode);Assert.Equal(5,result.Items.Count);Assert.Empty(native.Handles);Assert.Empty(ancestors.Calls);
        using(var c=store.OpenReadOnlyConnection()){using var cmd=c.CreateCommand();cmd.CommandText="SELECT COUNT(*) FROM ordinary_bundle_attachment_hash;";Assert.Equal(0L,cmd.ExecuteScalar());}
    }

    [DllImport("kernel32.dll",CharSet=CharSet.Unicode,ExactSpelling=true,SetLastError=true)]
    [return:MarshalAs(UnmanagedType.Bool)] private static extern bool CreateHardLinkW(string link,string target,IntPtr security);
    [DllImport("kernel32.dll",CharSet=CharSet.Unicode,ExactSpelling=true,SetLastError=true)]
    [return:MarshalAs(UnmanagedType.U1)] private static extern bool CreateSymbolicLinkW(string link,string target,uint flags);
    private static async Task Until(Func<bool> done)
    {var stop=DateTime.UtcNow.AddSeconds(15);while(!done() && DateTime.UtcNow<stop)await Task.Delay(20);Assert.True(done(),"Synthetic producer did not finish in its owned deadline.");}
}
