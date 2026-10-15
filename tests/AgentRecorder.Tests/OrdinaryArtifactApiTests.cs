using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using AgentRecorder.Api;
using AgentRecorder.Core;
using AgentRecorder.Infrastructure;
using AgentRecorder.Persistence;
using AgentRecorder.Windows;
using Xunit;
using Xunit.Abstractions;
using static AgentRecorder.Tests.OrdinaryArtifactHistoryTests;
using static AgentRecorder.Tests.UnifiedHistoryTestDatabase;

namespace AgentRecorder.Tests;

[Collection("NonParallel-AgentRecorderDataDir")]
public sealed class OrdinaryArtifactApiTests(ITestOutputHelper output)
{
    private const string Endpoint="/api/v1/recording-history/r/artifacts/check";
    private sealed class Host : IDisposable
    {
        private readonly string? _prior=Environment.GetEnvironmentVariable("AGENT_RECORDER_DATA_DIR");
        internal readonly RecordingEngine Engine;internal readonly ApiServer Api;
        internal Host(Fixture f,bool headless,IOrdinaryArtifactCheckGateway? gateway)
        {
            Environment.SetEnvironmentVariable("AGENT_RECORDER_DATA_DIR",f.Root);ApiKeyAuth.InitializeForTesting(f.Root);
            var audit=new RetentionSharedOutputPathTests.QuietAudit();Engine=new(audit);Engine.ConfigureOrdinaryRecordingHistory(f.Writer);
            var tray=new Tray(headless);Engine.SetTray(tray);Engine.BackendFactory=_=>throw new InvalidOperationException("No capture allowed");
            Api=new(Engine,audit,tray,null,null,null,null,null,null,null,null,null,0,
                unifiedHistoryGateway:new SqliteUnifiedHistoryGateway(new(f.Db.Store,f.Writer,headless),()=> (Sid,Session)),artifactCheckGateway:gateway);Api.Start();
        }
        internal HttpClient Client(bool auth=true)
        {var c=new HttpClient(new HttpClientHandler{UseProxy=false}){BaseAddress=new($"http://127.0.0.1:{Api.BoundPort}"),Timeout=TimeSpan.FromSeconds(8)};if(auth)c.DefaultRequestHeaders.Add("X-Agent-Recorder-Key",ApiKeyAuth.CurrentApiKey);return c;}
        public void Dispose(){Api.Stop();Engine.Dispose();ApiKeyAuth.ResetForTesting(null);Environment.SetEnvironmentVariable("AGENT_RECORDER_DATA_DIR",_prior);}
    }
    private sealed class Tray(bool headless) : ITrayContext
    {
        public string HostMode=>headless?"headless":"tray";public bool SupportsRegionSelectionUi=>false;
        public void RequestConfirmation(RecordingConfirmationPresentation p,Action<ConfirmationDecision> c)=>throw new InvalidOperationException("No UI");
        public void RequestRegionSelection(int t,Action<string,int,int,int,int,string,string> c)=>throw new InvalidOperationException("No UI");
        public void SetRecording(RecordingUiPresentation r)=>throw new InvalidOperationException("No capture");public void SetIdle(RecordingUiPresentation r){}public void SetAllIdle(){}public void ShowError(string s)=>throw new InvalidOperationException("No UI");
    }
    private static async Task<JsonElement> Get(HttpClient client,string path=Endpoint)
    {using var r=await client.GetAsync(path);Assert.Equal(HttpStatusCode.OK,r.StatusCode);using var d=JsonDocument.Parse(await r.Content.ReadAsStringAsync());return d.RootElement.GetProperty("data").Clone();}
    private static IEnumerable<JsonElement> Files(JsonElement data)=>new[]{data.GetProperty("media")}.Concat(data.GetProperty("bundle").GetProperty("contents").EnumerateArray());
    private sealed class CancellationGateway(OrdinaryArtifactCheckService service,TaskCompletionSource cancelled) : IOrdinaryArtifactCheckGateway
    {
        public async Task<OrdinaryArtifactCheckResult?> ReadAsync(string id,CancellationToken token=default)
        {var result=await service.ReadAsync(id,token);if(result?.ProbeState=="cancelled")cancelled.TrySetResult();return result;}
    }

    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task AuthArgumentsMissingAndDamagePrecedeAllProbes(bool headless)
    {
        using var f=new Fixture();f.Seed(anchor:true);var a=new Ancestors();var l=new Leaf();var reads=0;
        var s=new OrdinaryArtifactCheckService(id=>{reads++;return f.Writer.ReadArtifactFacts(id);},a,l,_=>true);
        using var host=new Host(f,headless,new SqliteOrdinaryArtifactCheckGateway(s));using var denied=host.Client(false);
        using(var r=await denied.GetAsync(Endpoint))Assert.Equal(HttpStatusCode.Unauthorized,r.StatusCode);
        denied.DefaultRequestHeaders.Add("X-Agent-Recorder-Key","wrong");using(var r=await denied.GetAsync(Endpoint))Assert.Equal(HttpStatusCode.Unauthorized,r.StatusCode);
        Assert.Equal(0,reads);using var client=host.Client();
        foreach(var path in new[]{Endpoint+"?path=C%3A%5Cx.mp4",Endpoint+"?name=metadata.json",Endpoint+"?force=true",Endpoint+"?limit=1","/api/v1/recording-history/%20/artifacts/check","/api/v1/recording-history/bad%2Fid/artifacts/check"})
        {using var r=await client.GetAsync(path);Assert.Equal(HttpStatusCode.BadRequest,r.StatusCode);}
        Assert.Equal(0,reads);using(var r=await client.GetAsync(Endpoint.Replace("/r/","/absent/")))Assert.Equal(HttpStatusCode.NotFound,r.StatusCode);
        Assert.Equal(1,reads);Assert.Empty(a.Calls);Assert.Empty(l.Calls);
        f.Db.RawExecute("PRAGMA foreign_keys=OFF;DELETE FROM ordinary_bundle_contents_items WHERE ordinal=2;");
        using(var r=await client.GetAsync(Endpoint))
        {Assert.Equal(HttpStatusCode.ServiceUnavailable,r.StatusCode);var text=await r.Content.ReadAsStringAsync();Assert.DoesNotContain(f.Root,text);Assert.DoesNotContain("operation",text);}
        Assert.Empty(a.Calls);Assert.Empty(l.Calls);
    }

    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task UnconfiguredGatewayAndDisabledAuthDoNotClaimSupport(bool headless)
    {
        using var f=new Fixture();using var host=new Host(f,headless,null);using var c=host.Client();
        Assert.False((await Get(c,"/api/v1/capabilities")).GetProperty("ordinary_artifact_check").GetProperty("supported").GetBoolean());
        using(var r=await c.GetAsync(Endpoint))Assert.Equal(HttpStatusCode.ServiceUnavailable,r.StatusCode);
        ApiKeyAuth.ResetForTesting(null);using var unauth=host.Client(false);using var response=await unauth.GetAsync(Endpoint);Assert.Equal(HttpStatusCode.Unauthorized,response.StatusCode);
    }

    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task NativeLavfiBundleSixFilesRemainReadOnlyAndThreeHistoricalProjectionsStayTheSame(bool headless)
    {
        using var f=new Fixture(videos:true);await f.RealBundle();var before=f.Db.Snapshot();var row=f.Writer.Read("r")!;
        var paths=new[]{row.OutputPath}.Concat(row.Bundle!.ContentsEvidence!.Contents.Select(i=>Path.Combine(row.Bundle.Path!,i.Name))).ToArray();
        string FileState()=>string.Join("|",paths.Select(p=>new FileInfo(p).Length+":"+File.GetLastWriteTimeUtc(p).Ticks));var files=FileState();
        var fake=new RetentionPreviewQueryTests.MetadataFake();var retention=new RetentionPreviewQueryService(f.Db.Query.GetOrdinaryEvidencePage,f.Root,fake,fake,()=>At.AddDays(5), TestRuntimeOutputUse.None);
        var retentionBefore=JsonSerializer.Serialize(retention.GetPage(Sid,Session,20,At.AddHours(1)));
        var trace=new WindowsReadOnlyFileMetadataNativeTests.Trace();var a=new Ancestors{Read=File.GetAttributes};var s=new OrdinaryArtifactCheckService(f.Writer.ReadArtifactFacts,a,new WindowsReadOnlyFileMetadataProbe(trace));
        using var host=new Host(f,headless,new SqliteOrdinaryArtifactCheckGateway(s));using var client=host.Client();
        var historical=new[]{"/api/v1/recording-history","/api/v1/recording-history/r","/api/v1/history?source=ordinary"};
        foreach(var path in historical){var d=await Get(client,path);var item=path.Contains("/recording-history/")?d.GetProperty("recording"):Assert.Single(d.GetProperty("items").EnumerateArray());Assert.Equal(5,item.GetProperty("bundle").GetProperty("contents").GetArrayLength());}
        Assert.Equal(0,trace.Opens);var data=await Get(client);Assert.Equal("complete",data.GetProperty("probe_state").GetString());
        Assert.Equal("completed",data.GetProperty("media_history_status").GetString());Assert.Equal("ready",data.GetProperty("bundle").GetProperty("history_status").GetString());
        Assert.All(Files(data),item=>{Assert.Equal("present_size_matches",item.GetProperty("observation").GetString());Assert.NotEqual(JsonValueKind.Null,item.GetProperty("sampled_at_utc").ValueKind);});
        Assert.Equal("match",data.GetProperty("media").GetProperty("identity_state").GetString());Assert.All(data.GetProperty("bundle").GetProperty("contents").EnumerateArray(),i=>Assert.Equal("unverified",i.GetProperty("identity_state").GetString()));
        Assert.Equal(6,trace.Opens);Assert.InRange(a.Calls.Count,1,64);Assert.Equal(a.Calls.Count,a.Calls.Distinct(StringComparer.OrdinalIgnoreCase).Count());Assert.All(trace.Handles,h=>Assert.True(h.IsClosed));
        foreach(var secret in new[]{"volume_id","file_id","operation_id","nonce",f.Root})Assert.DoesNotContain(secret,data.GetRawText());
        Assert.False(data.GetProperty("content_verified").GetBoolean());Assert.False(data.GetProperty("deletion_supported").GetBoolean());
        Assert.Equal(before,f.Db.Snapshot());Assert.Equal(files,FileState());Assert.Equal(retentionBefore,JsonSerializer.Serialize(retention.GetPage(Sid,Session,20,At.AddHours(1))));Assert.Empty(fake.Observations);
        var caps=(await Get(client,"/api/v1/capabilities")).GetProperty("ordinary_artifact_check");Assert.True(caps.GetProperty("supported").GetBoolean());Assert.Equal(6,caps.GetProperty("max_files").GetInt32());Assert.Equal(3000,caps.GetProperty("deadline_ms").GetInt32());
        output.WriteLine($"scope={headless}; owned lavfi/production five-file bundle; 6 native READ_ATTRIBUTES-only handles closed; DB, file lengths/write times, retention stats unchanged.");
    }

    [Theory]
    [InlineData("media_missing","missing")][InlineData("attachment_missing","missing")]
    [InlineData("short","size_mismatch")][InlineData("extend","size_mismatch")][InlineData("directory","unsafe_path")]
    [InlineData("replace_attachment","present_size_matches")][InlineData("replace_media","present_size_matches")][InlineData("hardlink","present_size_matches")]
    public async Task NativeMutationsSeparateCurrentObservationFromHistoricalFacts(string kind,string expected)
    {
        using var f=new Fixture();await f.RealBundle();var row=f.Writer.Read("r")!;var before=f.Db.Snapshot();
        var attachment=Path.Combine(row.Bundle!.Path!,"metadata.json");var path=kind is "media_missing" or "replace_media" or "hardlink"?row.OutputPath:attachment;
        if(kind.Contains("missing",StringComparison.Ordinal)||kind is "directory" or "replace_media" or "replace_attachment")File.Move(path,path+".saved-original");
        if(kind=="directory")Directory.CreateDirectory(path);
        if(kind is "replace_media" or "replace_attachment")await File.WriteAllBytesAsync(path,await File.ReadAllBytesAsync(path+".saved-original"));
        if(kind is "short" or "extend"){using var writer=new FileStream(path,FileMode.Open,FileAccess.Write);writer.SetLength(writer.Length+(kind=="short"?-1:1));}
        if(kind=="hardlink")WindowsReadOnlyFileMetadataNativeTests.HardLink(path+".alias.mp4",path);
        var trace=new WindowsReadOnlyFileMetadataNativeTests.Trace();var service=new OrdinaryArtifactCheckService(f.Writer.ReadArtifactFacts,ReadOnlyPathMetadata.Instance,new WindowsReadOnlyFileMetadataProbe(trace));
        var result=await service.ReadAsync("r");var observed=path==row.OutputPath?result!.Media:result!.Bundle.Contents![0];
        Assert.Equal(expected,observed.Observation);Assert.Equal(before,f.Db.Snapshot());Assert.Equal(row,f.Writer.Read("r"));Assert.All(trace.Handles,h=>Assert.True(h.IsClosed));
        if(kind=="replace_attachment")Assert.Equal("unverified",observed.IdentityState);
        if(kind=="replace_media")Assert.Equal("mismatch",observed.IdentityState);
        if(kind=="hardlink")Assert.Equal("match",observed.IdentityState);
        if(kind.Contains("missing",StringComparison.Ordinal))
        {
            await File.WriteAllBytesAsync(path,new byte[checked((int)observed.RecordedSizeBytes!.Value)]);
            var later=await service.ReadAsync("r");Assert.Equal("present_size_matches",(path==row.OutputPath?later!.Media:later!.Bundle.Contents![0]).Observation);
            Assert.Equal(before,f.Db.Snapshot());
        }
    }

    [Fact]
    public async Task NativeBetweenFrameChangesRenameAndOptionalSymlinkAreConservative()
    {
        using var f=new Fixture();await f.RealBundle();var row=f.Writer.Read("r")!;var trace=new WindowsReadOnlyFileMetadataNativeTests.Trace();var moved=false;
        trace.BeforeBasic=n=>{if(n==2){File.Move(row.OutputPath,row.OutputPath+".renamed");moved=true;}};
        var result=await new OrdinaryArtifactCheckService(f.Writer.ReadArtifactFacts,ReadOnlyPathMetadata.Instance,new WindowsReadOnlyFileMetadataProbe(trace)).ReadAsync("r");
        Assert.True(moved);Assert.Equal("unsafe_path",result!.Media.Observation);Assert.Equal("resolved_path_mismatch",result.Media.ReasonCode);Assert.All(trace.Handles,h=>Assert.True(h.IsClosed));
        File.Move(row.OutputPath+".renamed",row.OutputPath);var changedTrace=new WindowsReadOnlyFileMetadataNativeTests.Trace();changedTrace.BeforeBasic=n=>{if(n==2)File.SetLastWriteTimeUtc(row.OutputPath,File.GetLastWriteTimeUtc(row.OutputPath).AddMinutes(1));};
        var changed=await new OrdinaryArtifactCheckService(f.Writer.ReadArtifactFacts,ReadOnlyPathMetadata.Instance,new WindowsReadOnlyFileMetadataProbe(changedTrace)).ReadAsync("r");Assert.Equal("changed_during_probe",changed!.Media.Observation);Assert.All(changedTrace.Handles,h=>Assert.True(h.IsClosed));
        var link=Path.Combine(f.Root,"symbolic.mp4");
        try{File.CreateSymbolicLink(link,row.OutputPath);}
        catch(Exception ex) when(ex is UnauthorizedAccessException or IOException)
        {output.WriteLine("Owned symlink preparation unavailable; native symlink not claimed, HRESULT="+(ex.HResult&0xffff)+"; controlled reparse adapters covered.");return;}
        Assert.Equal(FileObservationOutcome.ReparsePoint,WindowsReadOnlyFileMetadataProbe.Instance.Observe(link).Outcome);
        output.WriteLine("Real rename/final-path mismatch and between-frame write-time change; metadata handles released; owned symlink branch also exercised.");
    }

    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task UnanchoredMediaAndLegacyReadyDoNotAcquireEvidenceFromExistingFiles(bool known)
    {
        using var f=new Fixture();await f.RealBundle(anchor:false,known:known);var before=f.Db.Snapshot();var trace=new WindowsReadOnlyFileMetadataNativeTests.Trace();
        var result=await new OrdinaryArtifactCheckService(f.Writer.ReadArtifactFacts,ReadOnlyPathMetadata.Instance,new WindowsReadOnlyFileMetadataProbe(trace)).ReadAsync("r");
        Assert.Equal("present_size_matches",result!.Media.Observation);Assert.Equal("unverified",result.Media.IdentityState);Assert.Equal("ready",result.Bundle.HistoryStatus);
        Assert.Equal(known?6:1,trace.Opens);Assert.Equal(known?1:(int?)null,result.Bundle.ContentsEvidenceVersion);
        if(!known)Assert.Null(result.Bundle.Contents);else Assert.All(result.Bundle.Contents!,i=>Assert.Equal("unverified",i.IdentityState));
        Assert.Equal(before,f.Db.Snapshot());Assert.All(trace.Handles,h=>Assert.True(h.IsClosed));
        output.WriteLine($"Owned real lavfi + five production files exist; recorded contents known={known}; no anchor guessed and legacy contents not inferred from files.");
    }

    [Fact]
    public async Task RealDataSharingConflictDoesNotClaimMissingOrContentLock()
    {
        using var f=new Fixture();await f.RealBundle();var row=f.Writer.Read("r")!;var before=f.Db.Snapshot();
        using var holder=new FileStream(row.OutputPath,FileMode.Open,FileAccess.ReadWrite,FileShare.None);
        var conflict=Assert.Throws<IOException>(()=>{using var other=new FileStream(row.OutputPath,FileMode.Open,FileAccess.ReadWrite,FileShare.None);});Assert.True((conflict.HResult&0xffff) is 32 or 33);
        var trace=new WindowsReadOnlyFileMetadataNativeTests.Trace();
        var result=await new OrdinaryArtifactCheckService(f.Writer.ReadArtifactFacts,ReadOnlyPathMetadata.Instance,new WindowsReadOnlyFileMetadataProbe(trace)).ReadAsync("r");
        Assert.Equal("present_size_matches",result!.Media.Observation);Assert.Equal("match",result.Media.IdentityState);Assert.Equal(before,f.Db.Snapshot());Assert.All(trace.Handles,h=>Assert.True(h.IsClosed));
        output.WriteLine("Real data sharing conflict (32/33) does not block READ_ATTRIBUTES; metadata succeeds, not content lock proof. Native open 5/32/33 -> inaccessible is controlled-adapter tested.");
    }

    [Fact]
    public async Task RealLongPathAndOptionalReparseAncestorRespectBoundsWithoutEnumeration()
    {
        using var f=new Fixture();var path=Path.Combine(f.Root,string.Join("\\",Enumerable.Repeat(new string('x',45),5)),"out.mp4");Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using(var file=new FileStream(path,FileMode.CreateNew,FileAccess.Write))file.SetLength(12);
        var trace=new WindowsReadOnlyFileMetadataNativeTests.Trace();var a=new Ancestors{Read=File.GetAttributes};
        var result=await new OrdinaryArtifactCheckService(_=>OrdinaryArtifactMetadataTests.Facts(path),a,new WindowsReadOnlyFileMetadataProbe(trace)).ReadAsync("r");
        Assert.InRange(path.Length,261,1024);Assert.Equal("present_size_matches",result!.Media.Observation);Assert.Single(trace.Handles);Assert.True(trace.Handles[0].IsClosed);Assert.InRange(a.Calls.Count,1,64);
        var alias=Path.Combine(f.Root,"ancestor-link");
        try{Directory.CreateSymbolicLink(alias,Path.GetDirectoryName(path)!);}
        catch(Exception ex) when(ex is UnauthorizedAccessException or IOException)
        {output.WriteLine("Owned ancestor symlink preparation unavailable; native branch not claimed, HRESULT="+(ex.HResult&0xffff)+"; ancestor adapter refusal covered.");return;}
        trace=new();var linked=await new OrdinaryArtifactCheckService(_=>OrdinaryArtifactMetadataTests.Facts(Path.Combine(alias,"out.mp4")),ReadOnlyPathMetadata.Instance,new WindowsReadOnlyFileMetadataProbe(trace)).ReadAsync("r");
        Assert.Equal("ancestor_reparse_point",linked!.Media.ReasonCode);Assert.Equal(0,trace.Opens);output.WriteLine("Real long local path metadata-only sample and reparse ancestor refusal before leaf open exercised.");
    }

    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task ApiStopAndRecreationKeepNativeQuotaUntilActualExit(bool headless)
    {
        using var f=new Fixture();f.Seed();var a=new Ancestors();var entered=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var exited=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);using var release=new ManualResetEventSlim();
        var l=new Leaf{Read=path=>{entered.TrySetResult();Assert.True(release.Wait(TimeSpan.FromSeconds(5)));return Sample(path);}};
        var service=new OrdinaryArtifactCheckService(f.Writer.ReadArtifactFacts,a,l,_=>true){WorkerExitedForTests=()=>exited.TrySetResult()};
        using(var host=new Host(f,headless,new SqliteOrdinaryArtifactCheckGateway(service)))
        {using var c=host.Client();var pending=c.GetAsync(Endpoint);await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));Assert.True(host.Api.Stop(TimeSpan.FromSeconds(2)));try{using var ignored=await pending;}catch(HttpRequestException){}}
        try
        {using var host=new Host(f,headless,new SqliteOrdinaryArtifactCheckGateway(new(f.Writer.ReadArtifactFacts,a,l,_=>true)));using var c=host.Client();Assert.Equal("busy",(await Get(c)).GetProperty("probe_state").GetString());Assert.Single(l.Calls);}
        finally{release.Set();await exited.Task.WaitAsync(TimeSpan.FromSeconds(2));}
        Assert.Single(l.Calls);
    }

    [Fact]
    public async Task ClientDisconnectCancelsBeforeNextFileAndRetainsLease()
    {
        using var f=new Fixture();f.Seed();var entered=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);var exited=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release=new ManualResetEventSlim();var l=new Leaf{Read=path=>{entered.TrySetResult();Assert.True(release.Wait(TimeSpan.FromSeconds(5)));return Sample(path);}};
        var s=new OrdinaryArtifactCheckService(f.Writer.ReadArtifactFacts,new Ancestors(),l,_=>true){WorkerExitedForTests=()=>exited.TrySetResult()};
        var cancelled=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var host=new Host(f,false,new CancellationGateway(s,cancelled));
        using(var raw=new TcpClient())
        {await raw.ConnectAsync(IPAddress.Loopback,host.Api.BoundPort);await raw.GetStream().WriteAsync(Encoding.UTF8.GetBytes($"GET {Endpoint} HTTP/1.1\r\nHost: localhost\r\nX-Agent-Recorder-Key: {ApiKeyAuth.CurrentApiKey}\r\n\r\n"));await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));raw.Client.LingerState=new LingerOption(true,0);raw.Client.Close(0);}
        using var c=host.Client();try{await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(2));Assert.Equal("busy",(await Get(c)).GetProperty("probe_state").GetString());}
        finally{release.Set();await exited.Task.WaitAsync(TimeSpan.FromSeconds(2));}
        Assert.Single(l.Calls);
    }
}
