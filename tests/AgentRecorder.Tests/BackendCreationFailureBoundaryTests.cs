using System.Net;
using System.Text;
using System.Text.Json;
using System.Windows.Forms;
using AgentRecorder.Api;
using AgentRecorder.App;
using AgentRecorder.Capture;
using AgentRecorder.Core;
using AgentRecorder.Core.Automation;
using AgentRecorder.Infrastructure;
using AgentRecorder.Persistence;
using Xunit;
using static AgentRecorder.Tests.RuntimeOutputTestEngine;

namespace AgentRecorder.Tests;

public sealed partial class BackendCreationFailureTests
{
    [Theory][InlineData(false)][InlineData(true)]
    public void BackendCreationFailureDefaultMaterializationAndNullRemainUnknown(bool returnsNull)
    {
        using var f=new Fixture();var calls=0;
        f.Engine.BackendMaterializerForTests=_=>{calls++;if(!returnsNull)throw new IOException(Secret);return null!;};
        var rec=f.Create();Assert.Null(Record.Exception(()=>f.Ui.Confirm!(ConfirmationDecision.Approve())));AssertFailed(f,rec);Assert.Equal(1,calls);
    }

    [Fact]
    public void BackendCreationFailureActualDefaultSelectorFailureUsesSameTerminalPath()
    {
        using var f=new Fixture();
        // Real default selector rejects an unknown decision; no physical worker is constructed.
        f.Engine.CapturePlanFactoryForTests=cfg=>new("test","unsupported-task371",new("test","unsupported-task371","fixture","not_run",0,false),"display_surface",cfg.SourceKind,null,0,null);
        var rec=f.Create();Assert.Null(Record.Exception(()=>f.Ui.Confirm!(ConfirmationDecision.Approve())));AssertFailed(f,rec);
    }

    [Theory][InlineData("stop")][InlineData("shutdown")][InlineData("stopping")]
    public async Task BackendCreationFailureLosingFactoryCannotReplaceStopOwner(string action)
    {
        using var f=new Fixture();var entered=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release=new ManualResetEventSlim();var calls=0;
        f.Engine.BackendFactory=_=>{calls++;entered.TrySetResult();Assert.True(release.Wait(TimeSpan.FromSeconds(10)));throw new IOException(Secret);};
        var rec=f.Create();var approval=Task.Run(()=>f.Ui.Confirm!(ConfirmationDecision.Approve()));
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        string winner;
        try
        {
            if(action=="shutdown")f.Engine.BeginHostShutdown("test_shutdown");
            else if(action=="stopping") {lock(rec){rec.State=RecState.stopping;rec.StopReason="owned_stop";}}
            else f.Engine.Stop(rec.Id,"user_requested");
            winner=Json(f.Engine.GetStatus(rec.Id)).GetRawText();Assert.Empty(f.Ui.Errors);
        }
        finally {release.Set();}
        await approval.WaitAsync(TimeSpan.FromSeconds(5));Assert.Equal(winner,Json(f.Engine.GetStatus(rec.Id)).GetRawText());
        Assert.Equal(1,calls);Assert.Empty(f.Ui.Errors);Assert.DoesNotContain(f.Audit.Events,e=>e.Name=="recording.failed");
        Assert.False(rec.OrdinaryBackendCreation!.ObserveRetired(()=>false));Assert.Null(rec.Backend);Assert.Equal(0,f.Ui.Recording);
        if(action!="stopping")Assert.Equal("cancelled",f.History.Read(rec.Id)!.Status);
        f.Ui.Confirm!(ConfirmationDecision.Approve());Assert.Equal(1,calls);
    }

    [Theory][InlineData(false)][InlineData(true)]
    public void BackendCreationFailureWinnerStaysFailedAfterStopOrShutdown(bool shutdown)
    {
        using var f=new Fixture();var calls=0;f.Engine.BackendFactory=_=>{calls++;throw new IOException(Secret);};
        var rec=f.Create();f.Ui.Confirm!(ConfirmationDecision.Approve());AssertFailed(f,rec);
        var before=Json(f.Engine.GetStatus(rec.Id)).GetRawText();var durable=Json(f.History.Read(rec.Id)!).GetRawText();
        if(shutdown)f.Engine.BeginHostShutdown("test_shutdown");else f.Engine.Stop(rec.Id,"user_requested");
        f.Engine.TriggerConfirmationExpiryForTests(rec.ConfirmationId!);f.Ui.Confirm(ConfirmationDecision.Approve());
        Assert.Equal(before,Json(f.Engine.GetStatus(rec.Id)).GetRawText());Assert.Equal(durable,Json(f.History.Read(rec.Id)!).GetRawText());
        Assert.Equal(1,calls);Assert.Single(f.Ui.Errors);Assert.Single(f.Audit.Events.Where(e=>e.Name=="recording.failed"));
    }

    [Theory][InlineData("cancelled")][InlineData("rejected")][InlineData("expired")]
    public void BackendCreationFailureLateApprovalNeverCreatesAfterEarlierTerminal(string outcome)
    {
        using var f=new Fixture();var calls=0;f.Engine.BackendFactory=_=>{calls++;throw new IOException(Secret);};var rec=f.Create();
        if(outcome=="cancelled")f.Engine.Stop(rec.Id,"user_requested");
        else if(outcome=="rejected")f.Ui.Confirm!(ConfirmationDecision.Reject());
        else f.Engine.TriggerConfirmationExpiryForTests(rec.ConfirmationId!);
        var prior=Json(f.Engine.GetStatus(rec.Id)).GetRawText();f.Ui.Confirm!(ConfirmationDecision.Approve());
        Assert.Equal(prior,Json(f.Engine.GetStatus(rec.Id)).GetRawText());Assert.Equal(0,calls);Assert.True(rec.IsFinalized);Assert.Empty(f.Ui.Errors);
        Assert.True(rec.OrdinaryBackendCreation!.ObserveRetired(()=>false));
        var other=Path.Combine(f.Root,"other.mp4");Claim(f.Engine,new Recording(false){IsOrdinaryLocalRequest=true,OutputPath=other},other);
        Assert.False(Read(f.Engine,rec.OutputPath)!.Paths.Single().InUse);
    }

    [Fact]
    public void BackendCreationFailureTypedAuthorizationKeepsAuthorizationMapping()
    {
        using var f=new Fixture();
        var type=typeof(RecordingEngine).GetNestedType("CaptureAuthorizationStartException",System.Reflection.BindingFlags.NonPublic)!;
        f.Engine.BackendFactory=_=>throw (Exception)Activator.CreateInstance(type,System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.Public|System.Reflection.BindingFlags.NonPublic,null,["authorization_revoked"],null)!;
        var rec=f.Create();Assert.Null(Record.Exception(()=>f.Ui.Confirm!(ConfirmationDecision.Approve())));
        Assert.Equal(RecState.failed,rec.State);Assert.Equal("capture_authorization_invalid",rec.StopReason);
        Assert.DoesNotContain(f.Audit.Events,e=>e.Json.Contains("backend_creation_failed"));Assert.Equal(0,f.Ui.Recording);
        Assert.False(rec.OrdinaryBackendCreation!.ObserveRetired(()=>false));
    }

    [Fact]
    public async Task BackendCreationFailureHttpCreationWaitAndStatusLongPollWakeOnFailure()
    {
        using var f=new Fixture();var calls=0;f.Engine.BackendFactory=_=>{calls++;throw new IOException(Secret);};
        ApiKeyAuth.InitializeForTesting(f.Root);
        var server=new ApiServer(f.Engine,f.Audit,f.Ui,null,null,null,f.Trace,null,null,null,null,null,0);
        using var stopServer=new ServerCleanup(server);
        server.Start();using var client=new HttpClient(new HttpClientHandler{UseProxy=false}){BaseAddress=new Uri($"http://127.0.0.1:{server.BoundPort}"),Timeout=TimeSpan.FromSeconds(10)};
        client.DefaultRequestHeaders.Add("X-Agent-Recorder-Key",ApiKeyAuth.CurrentApiKey);
        var waiting=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        f.Engine.BeforeCreationWaitBlocksForTests=_=>waiting.TrySetResult();
        var post=client.PostAsync("/api/v1/recordings?wait_for=recording&wait_ms=5000",new StringContent(f.Request().ToJsonString(),Encoding.UTF8,"application/json"));
        await f.Ui.Requested.Task.WaitAsync(TimeSpan.FromSeconds(5));await waiting.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var rec=Assert.Single(f.Engine._recs.Values);
        var polling=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        f.Engine.BeforeStateWaitBlocksForTests=()=>polling.TrySetResult();
        var poll=client.GetAsync($"/api/v1/recordings/{rec.Id}?wait_ms=5000&since_status=pending_confirmation");
        await polling.Task.WaitAsync(TimeSpan.FromSeconds(5));
        f.Ui.Confirm!(ConfirmationDecision.Approve());
        using var response=await post;var body=await response.Content.ReadAsStringAsync();Assert.Equal(HttpStatusCode.OK,response.StatusCode);Assert.DoesNotContain(Secret,body);
        var data=JsonDocument.Parse(body).RootElement.GetProperty("data");Assert.Equal("failed",data.GetProperty("status").GetString());
        Assert.False(data.GetProperty("wait").GetProperty("timed_out").GetBoolean());Assert.False(data.GetProperty("wait").GetProperty("goal_reached").GetBoolean());Assert.True(data.GetProperty("wait").GetProperty("terminal").GetBoolean());
        using var polled=await poll;var status=await polled.Content.ReadAsStringAsync();Assert.DoesNotContain(Secret,status);Assert.Contains("failed",status);Assert.DoesNotContain("\"timed_out\":true",status);
        AssertFailed(f,rec);Assert.Equal(1,calls);
    }

    private sealed class Balloon : ITrayBalloonTip
    {
        internal readonly List<string> Bodies=new();
        public void ShowBalloonTip(int timeout,string title,string body,ToolTipIcon icon)=>Bodies.Add(body);
    }
    private sealed class ServerCleanup(ApiServer server) : IDisposable
    {
        public void Dispose(){server.Stop();ApiKeyAuth.ResetForTesting(null);}
    }
    [Theory][InlineData(UiLanguage.ZhCn)][InlineData(UiLanguage.EnUs)]
    public void BackendCreationFailureTrayLocalizesStableCodeThroughExistingErrorEntry(UiLanguage language)
    {
        Exception? error=null;var thread=new Thread(()=>
        {
            try
            {
                using var f=new Fixture();var balloon=new Balloon();var text=new UiTextProvider(language);
                var presenter=new NotificationPresenter();using var tray=NotificationTray(f,language,balloon,presenter);
                f.Engine.BackendFactory=_=>throw new IOException(Secret);var rec=NotificationCreate(f,tray);f.Ui.Confirm!(ConfirmationDecision.Approve());
                var attempt=Assert.Single(presenter.Attempts);var body=attempt.Text.Get("Tray_BackendCreationFailed");Assert.Equal(rec.Id,attempt.Request.RecordingId);
                Assert.Equal(text.Get("Tray_BackendCreationFailed"),body);Assert.Empty(balloon.Bodies);
                Assert.DoesNotContain("backend_creation_failed",body);Assert.DoesNotContain(Secret,body);
                Assert.Contains(language==UiLanguage.ZhCn?"录制未开始":"Recording did not start",body);
            }
            catch(Exception ex){error=ex;}
        });thread.SetApartmentState(ApartmentState.STA);thread.Start();Assert.True(thread.Join(TimeSpan.FromSeconds(10)));Assert.Null(error);
    }
}

public sealed partial class CodexProfileRecordingRegressionTests
{
    [Fact]
    public async Task BackendCreationFailureProfileRegionKeepsImmutableSourceAndApprovedReference()
    {
        var body=RegionBody(countdown:0);var calls=0;
        _engine.BackendFactory=_=>{calls++;throw new IOException(@"private-C:\secret\profile-token.txt");};
        var store=new SqliteOperationalStore(Path.Combine(_temp.Path,"state.db"));using var history=new SqliteOrdinaryRecordingHistoryStore(store);_engine.ConfigureOrdinaryRecordingHistory(history);
        using var created=await RegionPost(body);Assert.True(created.IsSuccessStatusCode,await created.Content.ReadAsStringAsync());var rec=Assert.Single(_engine._recs.Values);
        var source=rec.FixedRegionProfileReference!.Value;var original=JsonSerializer.Serialize(_versions.Get(source.ProfileId,source.ProfileVersion));var bounds=rec.Config.Bounds;var path=rec.OutputPath;
        Assert.Null(Record.Exception(()=>_tray.Decision!(ConfirmationDecision.Approve())));
        Assert.Equal(RecState.failed,rec.State);Assert.Equal("backend_creation_failed",rec.Error);Assert.Equal(1,_tray.ErrorCount);Assert.Equal(0,_backend.StartCalls);Assert.Equal(1,calls);
        Assert.Equal(source,rec.FixedRegionProfileReference);Assert.Equal(bounds,rec.Config.Bounds);Assert.Equal(path,rec.OutputPath);Assert.Equal("region_only",rec.ProfileReuseMode);
        Assert.Equal(original,JsonSerializer.Serialize(_versions.Get(source.ProfileId,source.ProfileVersion)));Assert.Equal("failed",history.Read(rec.Id)!.Status);
        _tray.Decision!(ConfirmationDecision.Approve());Assert.Equal(1,calls);Assert.Equal(1,_tray.ErrorCount);Assert.Equal(CaptureAuthorizationProofState.Revoked,rec.AuthorizationProof!.State);
    }
}
