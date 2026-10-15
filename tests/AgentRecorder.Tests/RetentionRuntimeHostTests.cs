using System.Net;
using System.Reflection;
using System.Reflection.Emit;
using System.Text.Json;
using AgentRecorder.Api;
using AgentRecorder.Core;
using AgentRecorder.Infrastructure;
using AgentRecorder.Persistence;
using Xunit;
using static AgentRecorder.Tests.UnifiedHistoryTestDatabase;
using static AgentRecorder.Tests.RuntimeOutputTestEngine;

namespace AgentRecorder.Tests;

[Collection("NonParallel-AgentRecorderDataDir")]
public sealed class RetentionRuntimeHostTests
{
    [Theory][InlineData(false)][InlineData(true)]
    public async Task BothProductionHostFactoriesUseTheirActualEngineAuthenticateAndKeepPrivateOwnersHidden(bool headless)
    {
        using var f=new RetentionPreviewQueryTests.Fixture(ordinaryOnly:headless);var row=f.Seed("old");var before=f.Db.Snapshot();
        var prior=Environment.GetEnvironmentVariable("AGENT_RECORDER_DATA_DIR");ApiServer? server=null;
        using var engine=new RecordingEngine(new RetentionSharedOutputPathTests.QuietAudit());
        var tray=new QuietTray(headless);engine.SetTray(tray);engine.BackendFactory=_=>throw new IOException("No native capture permitted");
        var owner=new Recording("SECRET_OTHER_OWNER"){IsOrdinaryLocalRequest=true,OutputPath=row.OutputPath!.ToUpperInvariant()};
        Claim(engine,owner,owner.OutputPath);Claim(engine,new Recording("SECRET_FOREIGN_OWNER"){IsOrdinaryLocalRequest=true},@"C:\SECRET_FOREIGN_PATH\private.mp4");
        try
        {
            Environment.SetEnvironmentVariable("AGENT_RECORDER_DATA_DIR",f.Db.Root);ApiKeyAuth.InitializeForTesting(f.Db.Root);
            var query=new UnifiedHistoryQueryService(f.Db.Store,f.Db.Ordinary,headless);
            var gateway=headless?AgentRecorder.Headless.Program.CreateRetentionPreviewGateway(query,f.Db.Root,engine,()=>(Sid,Session)):
                AgentRecorder.App.Program.CreateRetentionPreviewGateway(query,f.Db.Root,engine,()=>(Sid,Session));
            var program=headless?typeof(AgentRecorder.Headless.Program):typeof(AgentRecorder.App.Program);
            Assert.Contains(program.GetMethod("CreateRetentionPreviewGateway",BindingFlags.Static|BindingFlags.NonPublic)!,Calls(program.GetMethod("Run",BindingFlags.Static|BindingFlags.NonPublic)!));
            server=new(engine,new RetentionSharedOutputPathTests.QuietAudit(),tray,null,null,null,null,null,null,null,null,null,0,retentionPreviewGateway:gateway);server.Start();
            using var client=new HttpClient(new HttpClientHandler{UseProxy=false}){BaseAddress=new($"http://127.0.0.1:{server.BoundPort}")};
            var url="/api/v1/retention/preview?completed_before="+Uri.EscapeDataString(f.Before.ToString("O"));
            using(var denied=await client.GetAsync(url))Assert.Equal(HttpStatusCode.Unauthorized,denied.StatusCode);
            client.DefaultRequestHeaders.Add("X-Agent-Recorder-Key","bad-key");
            using(var denied=await client.GetAsync(url))Assert.Equal(HttpStatusCode.Forbidden,denied.StatusCode);
            client.DefaultRequestHeaders.Remove("X-Agent-Recorder-Key");client.DefaultRequestHeaders.Add("X-Agent-Recorder-Key",ApiKeyAuth.CurrentApiKey);
            foreach(var parameter in new[]{"path","output_path","owner","runtime_paths"})
                using(var invalid=await client.GetAsync(url+"&"+parameter+"=C%3A%5Cprivate.mp4"))Assert.Equal(HttpStatusCode.BadRequest,invalid.StatusCode);
            using var response=await client.GetAsync(url);var text=await response.Content.ReadAsStringAsync();Assert.Equal(HttpStatusCode.OK,response.StatusCode);
            using var json=JsonDocument.Parse(text);var data=json.RootElement.GetProperty("data");var item=Assert.Single(data.GetProperty("items").EnumerateArray());
            Assert.Equal("runtime_output_in_use",item.GetProperty("reason_code").GetString());Assert.Equal("protected",item.GetProperty("evaluation").GetString());
            Assert.True(item.GetProperty("age_candidate").GetBoolean());Assert.Equal(row.OutputPath,item.GetProperty("recorded_path").GetString());
            Assert.Equal(12,item.GetProperty("recorded_bytes").GetInt64());Assert.Equal(JsonValueKind.Null,item.GetProperty("observed_file_bytes").ValueKind);
            Assert.Equal(1,data.GetProperty("unknown_candidate_file_count").GetInt32());Assert.Equal(0,data.GetProperty("observed_candidate_bytes").GetInt64());
            Assert.True(data.GetProperty("read_only").GetBoolean());Assert.False(data.GetProperty("deletion_supported").GetBoolean());
            using var cap=await client.GetAsync("/api/v1/capabilities");using var capabilities=JsonDocument.Parse(await cap.Content.ReadAsStringAsync());
            Assert.False(capabilities.RootElement.GetProperty("data").GetProperty("retention_preview").GetProperty("deletion_supported").GetBoolean());
            foreach(var secret in new[]{"SECRET_","owner_count","claim_count","nonce",Sid,Session})Assert.DoesNotContain(secret,text);
            Assert.Equal(2,OwnerCount(engine));Assert.Empty(engine._recs);Assert.Equal(0,tray.Calls);Assert.Equal(before,f.Db.Snapshot());
            engine.BeginHostShutdown("test_shutdown");using var unavailable=await client.GetAsync(url);var failed=await unavailable.Content.ReadAsStringAsync();
            Assert.Equal(HttpStatusCode.ServiceUnavailable,unavailable.StatusCode);Assert.Contains("RETENTION_PREVIEW_UNAVAILABLE",failed);
            Assert.DoesNotContain("items",failed);Assert.DoesNotContain("SECRET_",failed);
        }
        finally{server?.Stop();ApiKeyAuth.ResetForTesting(null);Environment.SetEnvironmentVariable("AGENT_RECORDER_DATA_DIR",prior);}
    }

    [Theory][InlineData("shutdown")][InlineData("overflow")][InlineData("locked")][InlineData("missing")][InlineData("throws")]
    [InlineData("argument")]
    public async Task UnavailableEngineOrProviderIsSanitizedEntire503WithZeroFileProbes(string fault)
    {
        using var f=new RetentionPreviewQueryTests.Fixture();var row=f.Seed("a");f.Seed("b");
        using var engine=new RecordingEngine(new RetentionSharedOutputPathTests.QuietAudit());
        for(var i=0;i<(fault=="overflow"?501:1);i++)Claim(engine,new Recording{IsOrdinaryLocalRequest=true},row.OutputPath!);
        if(fault=="shutdown")engine.BeginHostShutdown("test_shutdown");
        IOrdinaryRuntimeOutputUseProvider? provider=engine;
        if(fault=="missing")provider=null;
        if(fault is "throws" or "argument")provider=new RetentionRuntimeOutputTests.Provider{Read=_=>
        {
            if(fault=="argument")throw new UnifiedHistoryArgumentException();
            throw new IOException("SECRET_PRIVATE");
        }};
        var query=new RetentionPreviewQueryService(f.Db.Query.GetOrdinaryEvidencePage,f.Db.Root,f.Files,f.Files,()=>At.AddDays(2),provider);
        using var entered=new ManualResetEventSlim();using var release=new ManualResetEventSlim();Task? holder=null;
        if(fault=="locked")holder=Task.Run(()=>{lock(OutputLock(engine)){entered.Set();Assert.True(release.Wait(TimeSpan.FromSeconds(10)));}});
        var prior=Environment.GetEnvironmentVariable("AGENT_RECORDER_DATA_DIR");ApiServer? server=null;
        try
        {
            if(holder is not null)Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));
            Environment.SetEnvironmentVariable("AGENT_RECORDER_DATA_DIR",f.Db.Root);ApiKeyAuth.InitializeForTesting(f.Db.Root);
            var tray=new QuietTray(false);server=new(engine,new RetentionSharedOutputPathTests.QuietAudit(),tray,null,null,null,null,null,null,null,null,null,0,retentionPreviewGateway:new SqliteRetentionPreviewGateway(query,()=>(Sid,Session)));server.Start();
            using var client=new HttpClient(new HttpClientHandler{UseProxy=false}){BaseAddress=new($"http://127.0.0.1:{server.BoundPort}")};client.DefaultRequestHeaders.Add("X-Agent-Recorder-Key",ApiKeyAuth.CurrentApiKey);
            using var response=await client.GetAsync("/api/v1/retention/preview?completed_before="+Uri.EscapeDataString(f.Before.ToString("O")));
            Assert.Equal(HttpStatusCode.ServiceUnavailable,response.StatusCode);var text=await response.Content.ReadAsStringAsync();Assert.Contains("RETENTION_PREVIEW_UNAVAILABLE",text);
            foreach(var secret in new[]{"items","SECRET_","Sqlite","_ordinaryOutputs",Sid,Session})Assert.DoesNotContain(secret,text);
            RetentionRuntimeOutputTests.NoProbes(f);
        }
        finally{release.Set();if(holder is not null)await holder.WaitAsync(TimeSpan.FromSeconds(5));server?.Stop();ApiKeyAuth.ResetForTesting(null);Environment.SetEnvironmentVariable("AGENT_RECORDER_DATA_DIR",prior);}
    }

    private static IEnumerable<MethodBase> Calls(MethodInfo method)
    {
        var opcodes=typeof(OpCodes).GetFields(BindingFlags.Public|BindingFlags.Static).Where(f=>f.FieldType==typeof(OpCode)).Select(f=>(OpCode)f.GetValue(null)!).ToDictionary(o=>unchecked((ushort)o.Value));
        var il=method.GetMethodBody()!.GetILAsByteArray()!;
        for(var offset=0;offset<il.Length;)
        {
            ushort value=il[offset++];if(value==0xfe)value=(ushort)(0xfe00|il[offset++]);var op=opcodes[value];
            if(op.OperandType==OperandType.InlineMethod)yield return method.Module.ResolveMethod(BitConverter.ToInt32(il,offset))!;
            offset+=op.OperandType switch
            {
                OperandType.InlineNone=>0,OperandType.ShortInlineBrTarget or OperandType.ShortInlineI or OperandType.ShortInlineVar=>1,
                OperandType.InlineVar=>2,OperandType.InlineI8 or OperandType.InlineR=>8,
                OperandType.InlineSwitch=>4+4*BitConverter.ToInt32(il,offset),_=>4
            };
        }
    }

    private sealed class QuietTray(bool headless) : ITrayContext
    {
        internal int Calls;public string HostMode=>headless?"headless":"tray";public bool SupportsRegionSelectionUi=>false;
        public void RequestConfirmation(RecordingConfirmationPresentation p,Action<ConfirmationDecision> callback)=>Calls++;
        public void RequestRegionSelection(int t,Action<string,int,int,int,int,string,string> callback)=>Calls++;
        public void SetRecording(RecordingUiPresentation p)=>Calls++;public void SetIdle(RecordingUiPresentation p)=>Calls++;
        public void SetAllIdle()=>Calls++;public void ShowError(string text)=>Calls++;
    }
}
