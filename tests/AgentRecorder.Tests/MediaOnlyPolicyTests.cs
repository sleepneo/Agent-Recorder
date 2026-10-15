using System.Net;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Drawing;
using AgentRecorder.App;
using AgentRecorder.Core;
using AgentRecorder.Infrastructure;
using Xunit;

namespace AgentRecorder.Tests;

[Collection("NonParallel-AgentRecorderDataDir")]
public sealed class MediaOnlyPolicyTests
{
    [Theory][InlineData(0,false)][InlineData(1,false)][InlineData(-1,false)][InlineData(0,true)][InlineData(1,true)][InlineData(-1,true)]
    public async Task RawAndQuickFreezeDefaultTrueAndExplicitBooleanInConfirmationAndStatus(int policy,bool quick)
    {
        using var f=new MediaOnlyFixture();var rec=await f.Create(policy,quick);Assert.Equal(0,f.BackendCalls);Assert.Equal(0,f.GeneratorCalls);
        using var response=await f.Client.GetAsync("/api/v1/recordings/"+rec.Id);var data=JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.GetProperty("data");Assert.Equal(policy>=0,data.GetProperty("config").GetProperty("artifacts").GetProperty("bundle").GetBoolean());
        f.Tray.Confirm!(ConfirmationDecision.Reject());Assert.Equal(RecState.rejected,rec.State);Assert.Equal(0,f.BackendCalls);Assert.Equal(0,f.NativeApi.Calls);
    }
    public static IEnumerable<object[]> InvalidPolicies()
    {
        foreach(var quick in new[]{false,true})foreach(var member in new[]{"\"artifacts\":null","\"artifacts\":false","\"artifacts\":[]","\"artifacts\":\"false\"","\"artifacts\":{\"bundle\":null}","\"artifacts\":{\"bundle\":0}","\"artifacts\":{\"bundle\":\"false\"}","\"artifacts\":{\"bundle\":[]}","\"artifacts\":{\"unknown\":false}","\"artifacts\":{\"bundle\":false,\"bundle\":true}","\"artifacts\":{\"bundle\":false,\"\\u0062undle\":false}","\"artifacts\":{},\"artifacts\":{\"bundle\":false}"})yield return new object[]{quick,member};
    }
    [Theory][MemberData(nameof(InvalidPolicies))]
    public async Task MalformedArtifactsAreRejectedBeforeProvidersUiOrOutput(bool quick,string member)
    {
        using var f=new MediaOnlyFixture();var cfg=f.Request();cfg.Remove("artifacts");var untouched=Path.Combine(f.Root,"must-not-create");cfg["output"]!["directory"]=untouched;cfg["audio"]=new JsonObject{["microphone"]=new JsonObject{["enabled"]=true}};
        if(quick){cfg.Remove("source");cfg["target"]=new JsonObject{["type"]="selected_region"};}
        var body=cfg.ToJsonString()[..^1]+","+member+"}";var r=await f.Post(quick?"/api/v1/recordings/quick":"/api/v1/recordings",body);
        Assert.Equal(HttpStatusCode.BadRequest,r.Status);Assert.Equal("INVALID_ARGUMENT",r.Json.GetProperty("error").GetProperty("code").GetString());Assert.Empty(f.Engine._recs);Assert.Equal(0,f.Devices.Calls);Assert.Equal(0,f.Tray.Windows);Assert.Equal(0,f.Tray.Regions);Assert.Equal(0,f.BackendCalls);Assert.False(Directory.Exists(untouched));
    }
    [Theory][InlineData("/api/v1/profiles")][InlineData("/api/v1/plan-setups")][InlineData("/api/v1/recurring-plan-setups")][InlineData("/api/v1/required-once-plan-setups")][InlineData("/api/v1/future-window-authorizations")][InlineData("/api/v1/plans/unknown")]
    public async Task ExcludedHttpFamiliesRejectEvenNullPolicyBeforeGateway(string path)
    {using var f=new MediaOnlyFixture();var r=await f.Post(path,"{\"artifacts\":null}");Assert.Equal(HttpStatusCode.BadRequest,r.Status);Assert.Equal(0,f.Tray.Windows);Assert.Equal(0,f.Devices.Calls);Assert.Empty(f.Engine._recs);}
    [Theory][InlineData("nested",false)][InlineData("nested",true)][InlineData("profile_ref",false)][InlineData("profile_ref",true)][InlineData("screenshot_series",false)][InlineData("screenshot_series",true)]
    public async Task ExcludedRecordingModesCannotSilentlyDropPolicy(string kind,bool quick)
    {
        using var f=new MediaOnlyFixture();var cfg=f.Request(-1);if(kind=="screenshot_series")cfg["mode"]=kind;else cfg[kind]=new JsonObject();if(quick){cfg.Remove("source");cfg["target"]=new JsonObject{["type"]="selected_region"};}
        var r=await f.Post(quick?"/api/v1/recordings/quick":"/api/v1/recordings",cfg.ToJsonString());Assert.Equal(HttpStatusCode.BadRequest,r.Status);Assert.Equal(0,f.Devices.Calls);Assert.Equal(0,f.Tray.Windows);Assert.Equal(0,f.Tray.Regions);Assert.Empty(f.Engine._recs);
    }
    [Fact]
    public void EmptyPolicyDefaultsTrueAndProfileDomainCannotBypassPolicyGate()
    {
        using var f=new MediaOnlyFixture();var cfg=f.Request();cfg["artifacts"]=new JsonObject();var rec=ConfigParser.Build(cfg,"owned",out var summary);Assert.True(rec.GenerateBundle);Assert.True(summary.Artifacts!.Bundle);
        var called=0;var bad=f.Request(-1);Assert.Throws<ApiException>(()=>f.Engine.CreateRecordingFromFixedRegionProfile(bad,"owned",f.Tray,new("P",1,AgentRecorder.Core.Automation.RecurringFixedRegionProfileVersion.DigestPrefix+new string('a',64)),()=>{called++;return null;}));Assert.Equal(0,called);
    }
    [Fact]
    public void OriginalJsonMutationAndLocalOutputChangeCannotChangeFrozenPolicy()
    {
        using var f=new MediaOnlyFixture();var cfg=f.Request(-1);f.Engine.CreateRecording(cfg,"owned",f.Tray);var rec=Assert.Single(f.Engine._recs.Values);var summary=f.Tray.Presentation!.Summary;
        cfg["artifacts"]!["bundle"]=true;cfg["output"]!["directory"]=Path.Combine(f.Root,"mutated");Assert.False(rec.GenerateBundle);Assert.False(summary.Artifacts!.Bundle);Assert.Null(typeof(Recording).GetProperty(nameof(Recording.GenerateBundle))!.SetMethod);
        f.Tray.Confirm!(ConfirmationDecision.Reject());Assert.Equal(0,f.BackendCalls);
    }
    [Theory][InlineData(false,UiLanguage.EnUs)][InlineData(true,UiLanguage.EnUs)][InlineData(false,UiLanguage.ZhCn)][InlineData(true,UiLanguage.ZhCn)]
    public void HiddenLocalizedFormShowsFrozenPolicyWithoutChangingDefaultCancel(bool bundle,UiLanguage language)
    {
        Exception? error=null;var thread=new Thread(()=>
        {
            try
            {
                using var f=new MediaOnlyFixture();var cfg=f.Request(bundle?1:-1);f.Engine.CreateRecording(cfg,"owned",f.Tray);var p=f.Tray.Presentation!;var choices=new List<ConfirmationDecision>();
                using var form=new ConfirmationForm(new PendingConfirmationItem(p,_=>{}),1,1,choices.Add,previewProvider:new Preview(),textProvider:new UiTextProvider(language));
                form.Size=new Size(1000,760);_=form.Handle;form.PerformLayout();Assert.Equal(13,form.GetInfoRowTextsForTests().Count);
                Assert.Contains(form.GetInfoRowTextsForTests(),row=>row.Value==(language==UiLanguage.ZhCn?bundle?"MP4与录制说明包":"仅MP4":bundle?"MP4 and recording bundle":"MP4 only"));Assert.False(form.Visible);Assert.Same(form.AcceptButton,form.CancelButton);Assert.NotNull(form.CancelButton);Assert.Empty(choices);
                form.CloseWithoutResult();f.Tray.Confirm!(ConfirmationDecision.Reject());
            }
            catch(Exception e){error=e;}
        });thread.SetApartmentState(ApartmentState.STA);thread.Start();Assert.True(thread.Join(TimeSpan.FromSeconds(15)));if(error is not null)throw error;
    }
    private sealed class Preview:IScreenPreviewProvider{public Bitmap Capture(ConfirmationCaptureBounds b,Size s)=>new(s.Width,s.Height);}
    [Fact]
    public async Task ProductionAppConstructsRealGeneratorAndAdvertisesScopedDefault()
    {
        Assert.IsType<AgentRecorder.Capture.FfmpegRecordingBundleGenerator>(Program.CreateRecordingBundleGenerator());var type=typeof(Program);var factory=type.GetMethod("CreateRecordingBundleGenerator",BindingFlags.Static|BindingFlags.NonPublic)!;
        var calls=(IEnumerable<MethodBase>)typeof(RetentionRuntimeHostTests).GetMethod("Calls",BindingFlags.Static|BindingFlags.NonPublic)!.Invoke(null,new object[]{type.GetMethod("Run",BindingFlags.Static|BindingFlags.NonPublic)!})!;Assert.Contains(factory,calls);
        using var f=new MediaOnlyFixture();using var response=await f.Client.GetAsync("/api/v1/capabilities");var data=JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.GetProperty("data");var cap=data.GetProperty("recording_artifacts");Assert.True(cap.GetProperty("default_bundle").GetBoolean());Assert.Equal(2,cap.GetProperty("endpoints").GetArrayLength());Assert.Equal("ordinary_non_nested_video",cap.GetProperty("scope").GetString());Assert.False(data.GetProperty("retention_preview").GetProperty("deletion_supported").GetBoolean());
    }
}
