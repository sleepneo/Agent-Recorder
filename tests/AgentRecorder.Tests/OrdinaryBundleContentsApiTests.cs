using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using AgentRecorder.Api;
using AgentRecorder.Capture;
using AgentRecorder.Core;
using AgentRecorder.Infrastructure;
using AgentRecorder.Persistence;
using AgentRecorder.Windows;
using Xunit;
using Xunit.Abstractions;
using static AgentRecorder.Tests.UnifiedHistoryTestDatabase;

namespace AgentRecorder.Tests;

[Collection("NonParallel-AgentRecorderDataDir")]
public sealed class OrdinaryBundleContentsApiTests(ITestOutputHelper output) : IDisposable
{
    private readonly OrdinaryBundleContentsHistoryTests.Fixture _f=new();
    private readonly string? _priorData=Environment.GetEnvironmentVariable("AGENT_RECORDER_DATA_DIR");
    private readonly string? _priorMode=Environment.GetEnvironmentVariable("AGENT_RECORDER_TEST_MODE");
    private readonly Tray _tray=new();private RecordingEngine? _engine;private ApiServer? _api;private FfmpegCaptureBackend? _backend;
    private UnifiedHistoryQueryService? _query;
    private HttpClient Start(bool headless=false,bool gateway=true)
    {
        Environment.SetEnvironmentVariable("AGENT_RECORDER_DATA_DIR",_f.Root);ApiKeyAuth.InitializeForTesting(_f.Root);_tray.Headless=headless;
        if(_engine is null){_engine=new(new RetentionSharedOutputPathTests.QuietAudit());_engine.ConfigureOrdinaryRecordingHistory(_f.Writer);}
        _engine.SetTray(_tray);_query=new(_f.Store,_f.Writer,headless);
        _api=new(_engine,new RetentionSharedOutputPathTests.QuietAudit(),_tray,null,null,null,null,null,null,null,null,null,0,
            unifiedHistoryGateway:gateway?new SqliteUnifiedHistoryGateway(_query,()=> (Sid,Session)):null);_api.Start();
        return new(new HttpClientHandler{UseProxy=false}){BaseAddress=new($"http://127.0.0.1:{_api.BoundPort}"),Timeout=TimeSpan.FromSeconds(10)};
    }
    private static void Auth(HttpClient client)=>client.DefaultRequestHeaders.Add("X-Agent-Recorder-Key",ApiKeyAuth.CurrentApiKey);
    private static async Task<JsonElement> Data(HttpResponseMessage response)
    { Assert.Equal(HttpStatusCode.OK,response.StatusCode);using var d=JsonDocument.Parse(await response.Content.ReadAsStringAsync());return d.RootElement.GetProperty("data").Clone(); }
    private static async Task<JsonElement> Get(HttpClient client,string path)
    { using var response=await client.GetAsync(path);return await Data(response); }
    private static void Contents(JsonElement bundle,RecordingBundleContentsEvidence? expected)
    {
        Assert.Equal(5,bundle.EnumerateObject().Count());Assert.DoesNotContain("operation",bundle.ToString());
        if(expected is null){Assert.Equal(JsonValueKind.Null,bundle.GetProperty("contents_evidence_version").ValueKind);Assert.Equal(JsonValueKind.Null,bundle.GetProperty("contents").ValueKind);return;}
        Assert.Equal(1,bundle.GetProperty("contents_evidence_version").GetInt32());var items=bundle.GetProperty("contents").EnumerateArray().ToArray();Assert.Equal(5,items.Length);
        for(var i=0;i<5;i++){Assert.Equal(3,items[i].EnumerateObject().Count());Assert.Equal(expected.Contents[i].Name,items[i].GetProperty("name").GetString());Assert.Equal(expected.Contents[i].MediaType,items[i].GetProperty("media_type").GetString());Assert.Equal(expected.Contents[i].SizeBytes,items[i].GetProperty("size_bytes").GetInt64());}
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task BothScopesThreeProjectionsNullStatesCapabilitiesAndOriginalCursor(bool headless)
    {
        var(_,interrupted)=_f.Seed("interrupted");_f.Reopen();_f.Writer.RecoverIncomplete(At.AddHours(1));
        var(_,ready)=_f.Seed();_f.Complete(ready);var expected=_f.Writer.Read("r")!.Bundle!.ContentsEvidence;
        var(_,unknown)=_f.Seed("unknown");_f.Complete(unknown,known:false);_f.Seed("generating");
        var(_,failed)=_f.Seed("failed");Assert.True(_f.Writer.TryPublishBundle(new(failed,new("failed",failed.BundlePath,RecordingBundleErrorCodes.GenerationFailed))));
        _f.Writer.Register(_f.Row("notapp") with {BundleRegistration=new(null)});_f.Writer.Register(_f.Row("unrecorded"));
        using var client=Start(headless);Auth(client);_engine!.BackendFactory=_=>throw new InvalidOperationException("No capture in history test");
        foreach(var path in new[]{"/api/v1/recording-history","/api/v1/recording-history/r","/api/v1/history?source=ordinary&target_key="+OrdinaryTargetHistoryTests.Key})
        {
            var d=await Get(client,path);var row=path.Contains("/recording-history/")?d.GetProperty("recording"):Assert.Single(d.GetProperty("items").EnumerateArray(),i=>i.GetProperty("recording_id").GetString()=="r");
            Contents(row.GetProperty("bundle"),expected);
        }
        foreach(var id in new[]{"interrupted","unknown","generating","failed","notapp"})Contents((await Get(client,"/api/v1/recording-history/"+id)).GetProperty("recording").GetProperty("bundle"),null);
        Assert.Equal(JsonValueKind.Null,(await Get(client,"/api/v1/recording-history/unrecorded")).GetProperty("recording").GetProperty("bundle").ValueKind);
        var capabilities=(await Get(client,"/api/v1/capabilities")).GetProperty("unified_history");
        Assert.Equal("ordinary",Assert.Single(capabilities.GetProperty("bundle_contents_supported_sources").EnumerateArray()).GetString());Assert.Equal(1,capabilities.GetProperty("bundle_contents_evidence_version").GetInt32());Assert.Equal(5,capabilities.GetProperty("bundle_contents_max").GetInt32());
        var first=await Get(client,"/api/v1/history?source=ordinary&limit=1");var cursor=Uri.EscapeDataString(first.GetProperty("next_cursor").GetString()!);
        Assert.Single((await Get(client,"/api/v1/history?source=ordinary&limit=1&cursor="+cursor)).GetProperty("items").EnumerateArray());
        using var unsupported=await client.GetAsync("/api/v1/history?source=ordinary&contents_name=metadata.json");Assert.Equal(HttpStatusCode.BadRequest,unsupported.StatusCode);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task UnauthorizedPrecedesDamagedContentsAnd503DoesNotLeak(bool headless)
    {
        var(_,op)=_f.Seed();_f.Complete(op);using var client=Start(headless);var probes=0;_query!.OrdinaryFileProbeForTests=_=>{probes++;return true;};
        _f.Execute("DELETE FROM ordinary_bundle_contents_items WHERE ordinal=2;",raw:true);
        foreach(var path in new[]{"/api/v1/history?source=ordinary","/api/v1/recording-history","/api/v1/recording-history/r"})
        { using var response=await client.GetAsync(path);Assert.Equal(HttpStatusCode.Unauthorized,response.StatusCode); }
        Auth(client);
        foreach(var path in new[]{"/api/v1/history?source=ordinary","/api/v1/recording-history","/api/v1/recording-history/r"})
        { using var response=await client.GetAsync(path);Assert.Equal(HttpStatusCode.ServiceUnavailable,response.StatusCode);Assert.DoesNotContain(_f.Root,await response.Content.ReadAsStringAsync()); }
        Assert.Equal(0,probes);
    }

    [Fact]
    public async Task MissingUnifiedGatewayAdvertisesNoContentsSources()
    {
        using var client=Start(gateway:false);Auth(client);Assert.Empty((await Get(client,"/api/v1/capabilities")).GetProperty("unified_history").GetProperty("bundle_contents_supported_sources").EnumerateArray());
    }

    private async Task<Recording> Capture(IRecordingBundleGenerator generator)
    {
        Environment.SetEnvironmentVariable("AGENT_RECORDER_TEST_MODE","1");DataDirResolver.SetOverride(_f.Root);
        SystemQuery.SetDisplayTopologyProvider(()=>new(){new("display_1","Synthetic",true,new SystemQuery.Bounds(0,0,1920,1080),1.0,"synthetic-test-display:display_1",DisplayIdentityResolutionStatus.Resolved)});
        _engine=new(new RetentionSharedOutputPathTests.QuietAudit(),bundleGenerator:generator);_engine.ConfigureOrdinaryRecordingHistory(_f.Writer);_engine.SetTray(_tray);
        var calls=0;_engine.BackendFactory=_=>{calls++;_backend=new(){VideoInputArgumentsForTests=_=>new[]{"-re","-f","lavfi","-t","1","-i","color=c=black:s=64x64:r=15"}};return(_backend,"ffmpeg");};
        _engine.CreateRecording(new JsonObject{["source"]=new JsonObject{["type"]="display",["display_id"]="display_1"},["video"]=new JsonObject{["fps"]=15},["duration_seconds"]=1,["countdown_seconds"]=0,["output"]=new JsonObject{["directory"]=_f.Root,["filename"]="core.mp4",["conflict_policy"]="fail"}},"task332-synthetic",_tray);
        Assert.Equal(0,calls);Assert.Null(_backend);_tray.Confirm!(ConfirmationDecision.Approve());var rec=Assert.Single(_engine._recs.Values);
        await Until(()=>rec.IsFinalized);await Until(()=>_f.Writer.Read(rec.Id)?.Bundle?.Status is "ready" or "failed");Assert.Equal(RecState.completed,rec.State);return rec;
    }
    private static async Task Until(Func<bool> predicate)
    { var until=DateTime.UtcNow.AddSeconds(8);while(!predicate()&&DateTime.UtcNow<until)await Task.Delay(10);Assert.True(predicate(),"Bounded bundle completion did not settle."); }
    private sealed class Generator(Func<RecordingBundleRequest,Task<RecordingBundleGenerationResult>> generate) : IRecordingBundleGenerator
    { public Task<RecordingBundleGenerationResult> GenerateAsync(RecordingBundleRequest r,CancellationToken token=default)=>generate(r); }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task ActualLavfiCoreGeneratorWriterAllApisAndReopenKeepRecordedLengths(bool headless)
    {
        RecordingBundleGenerationResult? result=null;var real=new FfmpegRecordingBundleGenerator();
        var rec=await Capture(new Generator(async r=>result=await real.GenerateAsync(r)));Assert.NotNull(result!.ContentsEvidence);
        var row=_f.Writer.Read(rec.Id)!;Assert.Equal("ready",rec.BundleSnapshot.Status);Assert.Equal(result.ContentsEvidence,row.Bundle!.ContentsEvidence);
        foreach(var item in result.ContentsEvidence.Contents)Assert.Equal(new FileInfo(Path.Combine(row.Bundle.Path!,item.Name)).Length,item.SizeBytes);
        // Later attachment changes do not rewrite recorded generation bytes. Keep the owned files.
        await File.AppendAllTextAsync(Path.Combine(row.Bundle.Path!,"metadata.json"),"\nchanged-after-generation");
        using(var client=Start(headless))
        {
            Auth(client);
            foreach(var path in new[]{"/api/v1/recording-history","/api/v1/recording-history/"+rec.Id,"/api/v1/history?source=ordinary&target_key="+row.TargetKey})
            { var d=await Get(client,path);var item=path.Contains("/recording-history/")?d.GetProperty("recording"):Assert.Single(d.GetProperty("items").EnumerateArray());Contents(item.GetProperty("bundle"),result.ContentsEvidence); }
        }
        _api!.Stop();_api=null;_engine!.Dispose();_engine=null;_f.Reopen();_f.Writer.RecoverIncomplete(At.AddHours(1));Assert.Equal(row,_f.Writer.Read(rec.Id));
        using(var client=Start(headless)){Auth(client);Contents((await Get(client,"/api/v1/recording-history/"+rec.Id)).GetProperty("recording").GetProperty("bundle"),result.ContentsEvidence);}
        output.WriteLine("Formal authorized Core + synthetic lavfi MP4 + real Ffmpeg generator five positive file lengths -> atomic writer -> three APIs -> reopen; altered metadata is not reread.");
    }

    [Theory]
    [InlineData("compat")] [InlineData("path")] [InlineData("missing")] [InlineData("duplicate")]
    [InlineData("type")] [InlineData("zero")] [InlineData("negative")] [InlineData("extra")] [InlineData("traversal")]
    public async Task CompatibilityUnknownAndMalformedProducerNeverPublishFalseReadyEvidence(string fault)
    {
        var rec=await Capture(new Generator(r=>Task.FromResult(fault switch {
            "compat"=>RecordingBundleGenerationResult.Ready(OrdinaryRecordingBundleOperation.DerivePath(r.MediaPath)),
            "path"=>RecordingBundleGenerationResult.Ready(Path.Combine(_f.Root,"unapproved.bundle"),BundleContentsGenerationTests.Items()),
            _=>RecordingBundleGenerationResult.Ready(OrdinaryRecordingBundleOperation.DerivePath(r.MediaPath),BundleContentsGenerationTests.BadItems(fault)) })));
        var row=_f.Writer.Read(rec.Id)!;Assert.Equal("completed",row.Status);Assert.Equal(fault=="compat"?"ready":"failed",row.Bundle!.Status);Assert.Equal(row.Bundle.Status,rec.BundleSnapshot.Status);Assert.Null(row.Bundle.ContentsEvidence);
        if(fault!="compat")Assert.Equal(RecordingBundleErrorCodes.GenerationFailed,row.Bundle.ErrorCode);
        _f.Reopen();Assert.Equal(row,_f.Writer.Read(rec.Id));
    }

    private sealed class Tray : ITrayContext
    {
        internal bool Headless;internal Action<ConfirmationDecision>? Confirm;public string HostMode=>Headless?"headless":"tray";public bool SupportsRegionSelectionUi=>false;
        public void RequestConfirmation(RecordingConfirmationPresentation p,Action<ConfirmationDecision> c)=>Confirm=c;
        public void RequestRegionSelection(int t,Action<string,int,int,int,int,string,string> c)=>throw new InvalidOperationException("No real UI");
        public void SetRecording(RecordingUiPresentation r){}public void SetIdle(RecordingUiPresentation r){}public void SetAllIdle(){}public void ShowError(string s){}
    }
    public void Dispose()
    { _api?.Stop();_engine?.Dispose();_backend?.Dispose();_f.Dispose();SystemQuery.SetDisplayTopologyProvider(null);DataDirResolver.ClearOverride();ApiKeyAuth.ResetForTesting(null);Environment.SetEnvironmentVariable("AGENT_RECORDER_DATA_DIR",_priorData);Environment.SetEnvironmentVariable("AGENT_RECORDER_TEST_MODE",_priorMode); }
}
