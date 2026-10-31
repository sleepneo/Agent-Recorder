using System.Reflection;
using System.Text.Json;
using AgentRecorder.App;
using AgentRecorder.Core;
using AgentRecorder.Infrastructure;
using AgentRecorder.Windows;
using Xunit;
using static AgentRecorder.Tests.RuntimeOutputTestEngine;

namespace AgentRecorder.Tests;

public sealed partial class BackendCreationFailureTests
{
    // Only consent is fake. UI lifecycle/error/notifier calls use actual TrayContext.
    private sealed class NotificationRoute(Ui consent,TrayContext tray) : ITrayContext,IRecordingFailureNotifier
    {
        public string HostMode=>"tray";public bool SupportsRegionSelectionUi=>true;
        public void RequestConfirmation(RecordingConfirmationPresentation p,Action<ConfirmationDecision> callback)=>consent.RequestConfirmation(p,callback);
        public void RequestRegionSelection(int t,Action<string,int,int,int,int,string,string> callback)=>throw new InvalidOperationException();
        public void SetRecording(RecordingUiPresentation p){consent.SetRecording(p);tray.SetRecording(p);}
        public void SetIdle(RecordingUiPresentation p){consent.SetIdle(p);tray.SetIdle(p);}
        public void SetAllIdle()=>tray.SetAllIdle();
        public void ShowError(string text)=>tray.ShowError(text);
        public void ShowRecordingFailure(string id,string reason)=>tray.ShowRecordingFailure(id,reason);
    }
    private sealed class NotificationIndicator : IIndicatorPresenter
    {public void ShowFor(RecordingUiPresentation p,RecordingUiPresentation? parent,string? parentFallbackReason=null) { }}
    private sealed class NotificationPresenter : IRecordingFailureNotificationPresenter
    {
        internal sealed record Attempt(RecordingFailureNotificationRequest Request,IUiTextProvider Text,bool Exclusion,bool Shown);
        internal readonly List<Attempt> Attempts=new();internal bool Affinity=true,Prove=true;internal int Visible,Closes;
        private Action<RecordingFailureNotificationCloseReason>? closed;
        public NotificationPresentationResult TryShow(RecordingFailureNotificationRequest r,IUiTextProvider text,bool exclusion,Action<RecordingFailureNotificationCloseReason> onClosed)
        {
            var shown=!exclusion||Affinity;Attempts.Add(new(r,text,exclusion,shown));if(shown){Visible++;closed=onClosed;}
            return new(shown,exclusion,Affinity&&Prove);
        }
        public void Close(RecordingFailureNotificationCloseReason reason){Closes++;if(Visible>0)Visible--;var callback=closed;closed=null;callback?.Invoke(reason);}
        public void Dispose() { }
    }
    private static void NotificationSta(Action body)
    {
        Exception? error=null;var thread=new Thread(()=>{try{body();}catch(Exception e){error=e;}});
        thread.SetApartmentState(ApartmentState.STA);thread.Start();Assert.True(thread.Join(TimeSpan.FromSeconds(15)));Assert.Null(error);
    }
    private static RecordingFailureNotificationManager NotificationManager(TrayContext tray)=>
        (RecordingFailureNotificationManager)typeof(TrayContext).GetField("_failureNotificationManager",BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(tray)!;
    private static IDictionary<string,RecordingUiPresentation> NotificationActive(TrayContext tray)=>
        (IDictionary<string,RecordingUiPresentation>)typeof(TrayContext).GetField("_activeRecordings",BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(tray)!;
    private static RecordingUiPresentation NotificationOuter()=>new(){RecordingId="synthetic-outer",State=RecordingUiState.Recording,SourceType="region",CaptureBounds=new(0,0,100,100),StartedAtUtc=DateTime.UtcNow,NestedRole="outer",NestedSessionId="notification-test"};
    private static TrayContext NotificationTray(Fixture f,UiLanguage language,Balloon balloon,NotificationPresenter presenter)=>
        new(f.Engine,f.Audit,FakeGlobalStopHotkeyFactory.Create(),uiTextProvider:new UiTextProvider(language),balloonTip:balloon,indicatorPresenter:new NotificationIndicator(),failureNotificationPresenter:presenter,chapterMarksHotkeyFactory:FakeGlobalStopHotkeyFactory.Create());
    private static Recording NotificationCreate(Fixture f,TrayContext tray)
    {
        var route=new NotificationRoute(f.Ui,tray);f.Engine.SetTray(route);f.Engine.CreateRecording(f.Request(),"notification-test",route);return Assert.Single(f.Engine._recs.Values);
    }
    private static void NotificationFailed(Fixture f,Recording rec)
    {
        Assert.Equal(RecState.failed,rec.State);Assert.True(rec.IsFinalized);Assert.Equal("backend_creation_failed",rec.Error);Assert.Equal("backend_creation_failed",rec.StopReason);
        Assert.Equal(AgentRecorder.Capture.CaptureAuthorizationProofState.Revoked,rec.AuthorizationProof!.State);Assert.False(rec.BackendStartAttempted);Assert.Null(rec.Backend);
        Assert.Equal(0,f.Ui.Recording);Assert.Equal("not_applicable",rec.BundleSnapshot.Status);Assert.False(File.Exists(rec.OutputPath));Assert.Equal(0,f.Bundle.Calls);
        Assert.Equal("failed",f.History.Read(rec.Id)!.Status);Assert.False(rec.OrdinaryBackendCreation!.ObserveRetired(()=>false));
        Assert.DoesNotContain(Secret,Json(f.Engine.GetStatus(rec.Id)).GetRawText());Assert.DoesNotContain(Secret,string.Join('|',f.Audit.Events.Select(x=>x.Json)));
        var path=Path.Combine(f.Root,"other.mp4");Claim(f.Engine,new Recording(false){IsOrdinaryLocalRequest=true,OutputPath=path},path);
        Assert.True(Read(f.Engine,rec.OutputPath)!.Paths.Single().InUse);Assert.Equal("worker_retirement_unknown",f.Engine.TryReserveIdleHostShutdown(()=>null));
    }

    [Theory][InlineData(false,UiLanguage.ZhCn)][InlineData(false,UiLanguage.EnUs)][InlineData(true,UiLanguage.ZhCn)][InlineData(true,UiLanguage.EnUs)]
    public void BackendFailureNotificationFormalFactoryFailureRoutesThroughActualTray(bool active,UiLanguage language)=>NotificationSta(()=>
    {
        using var f=new Fixture();var balloon=new Balloon();var presenter=new NotificationPresenter();using var tray=NotificationTray(f,language,balloon,presenter);
        var outer=NotificationOuter();if(active)tray.SetRecording(outer);var calls=0;f.Engine.BackendFactory=_=>{calls++;throw new IOException(Secret);};
        var rec=NotificationCreate(f,tray);f.Ui.Confirm!(ConfirmationDecision.Approve());
        var attempt=Assert.Single(presenter.Attempts);Assert.Equal(rec.Id,attempt.Request.RecordingId);Assert.Equal("backend_creation_failed",attempt.Request.ReasonCode);
        Assert.Equal(active,attempt.Exclusion);Assert.True(attempt.Shown);Assert.Equal(1,presenter.Visible);Assert.Empty(balloon.Bodies);
        var body=attempt.Text.Get("Tray_BackendCreationFailed");Assert.Contains(language==UiLanguage.ZhCn?"录制未开始":"Recording did not start",body);Assert.DoesNotContain(Secret,body);Assert.DoesNotContain("backend_creation_failed",body);
        NotificationFailed(f,rec);f.Ui.Confirm(ConfirmationDecision.Approve());f.Engine.Stop(rec.Id,"user_requested");
        Assert.Equal(1,calls);Assert.Single(presenter.Attempts);Assert.Single(f.Audit.Events.Where(x=>x.Name=="recording_failure_notification.requested"));
        if(active)Assert.Same(outer,NotificationActive(tray)[outer.RecordingId]);
    });

    [Theory][InlineData(UiLanguage.ZhCn)][InlineData(UiLanguage.EnUs)]
    public void BackendFailureNotificationDefersUntilActualTrayIdleTransitionAndDeduplicates(UiLanguage language)=>NotificationSta(()=>
    {
        using var f=new Fixture();var balloon=new Balloon();var presenter=new NotificationPresenter{Affinity=false};using var tray=NotificationTray(f,language,balloon,presenter);
        var outer=NotificationOuter();tray.SetRecording(outer);f.Engine.BackendFactory=_=>throw new IOException(Secret);
        var rec=NotificationCreate(f,tray);f.Ui.Confirm!(ConfirmationDecision.Approve());NotificationFailed(f,rec);
        var before=Json(f.History.Read(rec.Id)!).GetRawText();var manager=NotificationManager(tray);
        Assert.True(Assert.Single(presenter.Attempts).Exclusion);Assert.False(presenter.Attempts[0].Shown);Assert.Equal(0,presenter.Visible);Assert.Equal(1,manager.PendingCountForTests);Assert.Empty(balloon.Bodies);
        f.Ui.Confirm(ConfirmationDecision.Approve());f.Engine.Stop(rec.Id,"user_requested");tray.ShowRecordingFailure(rec.Id,"backend_creation_failed");
        Assert.Single(presenter.Attempts);Assert.Same(outer,NotificationActive(tray)[outer.RecordingId]);
        tray.SetIdle(outer with {State=RecordingUiState.Idle});
        Assert.Equal(2,presenter.Attempts.Count);Assert.False(presenter.Attempts[1].Exclusion);Assert.True(presenter.Attempts[1].Shown);Assert.Equal(1,presenter.Visible);Assert.Equal(0,manager.PendingCountForTests);
        Assert.Equal(language,presenter.Attempts[1].Text.Language);Assert.Equal(before,Json(f.History.Read(rec.Id)!).GetRawText());Assert.Empty(balloon.Bodies);
        tray.SetIdle(outer);presenter.Close(RecordingFailureNotificationCloseReason.Timeout);tray.ShowRecordingFailure(rec.Id,"backend_creation_failed");
        Assert.Equal(2,presenter.Attempts.Count);Assert.Equal(0,presenter.Visible);Assert.Single(f.Audit.Events.Where(x=>x.Name=="recording_failure_notification.requested"));
        Assert.Single(f.Audit.Events.Where(x=>x.Name=="recording_failure_notification.deferred"));Assert.Single(f.Audit.Events.Where(x=>x.Name=="recording_failure_notification.shown"));
    });

    [Fact]
    public void BackendFailureNotificationCannotAcceptUnprovenActivePresenterVisibility()=>NotificationSta(()=>
    {
        using var f=new Fixture();var balloon=new Balloon();var presenter=new NotificationPresenter{Prove=false};using var tray=NotificationTray(f,UiLanguage.EnUs,balloon,presenter);
        var outer=NotificationOuter();tray.SetRecording(outer);f.Engine.BackendFactory=_=>throw new IOException(Secret);var rec=NotificationCreate(f,tray);f.Ui.Confirm!(ConfirmationDecision.Approve());
        Assert.Equal(1,presenter.Closes);Assert.Equal(0,presenter.Visible);Assert.Equal(1,NotificationManager(tray).PendingCountForTests);Assert.Empty(balloon.Bodies);
        Assert.DoesNotContain(f.Audit.Events,x=>x.Name=="recording_failure_notification.shown");Assert.Same(outer,NotificationActive(tray)[outer.RecordingId]);
        tray.SetIdle(outer);Assert.Equal(1,presenter.Visible);Assert.Equal(2,presenter.Attempts.Count);NotificationFailed(f,rec);
    });

    [Theory][InlineData(true)][InlineData(false)]
    public void BackendFailureNotificationStopAndFailureOwnersKeepSingleRequest(bool stopFirst)=>NotificationSta(()=>
    {
        using var f=new Fixture();var balloon=new Balloon();var presenter=new NotificationPresenter();using var tray=NotificationTray(f,UiLanguage.EnUs,balloon,presenter);
        var outer=NotificationOuter();tray.SetRecording(outer);var calls=0;using var entered=new ManualResetEventSlim();using var release=new ManualResetEventSlim();
        f.Engine.BackendFactory=_=>{Interlocked.Increment(ref calls);entered.Set();Assert.True(release.Wait(TimeSpan.FromSeconds(5)));throw new IOException(Secret);};
        var rec=NotificationCreate(f,tray);
        if(stopFirst)
        {
            var approval=Task.Run(()=>f.Ui.Confirm!(ConfirmationDecision.Approve()));
            try{Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));f.Engine.Stop(rec.Id,"user_requested");}finally{release.Set();}
            approval.WaitAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
            Assert.Equal(RecState.cancelled,rec.State);Assert.Equal("cancelled",f.History.Read(rec.Id)!.Status);Assert.Empty(presenter.Attempts);
            Assert.DoesNotContain(f.Audit.Events,x=>x.Name=="recording_failure_notification.requested");
        }
        else
        {
            release.Set();f.Ui.Confirm!(ConfirmationDecision.Approve());NotificationFailed(f,rec);f.Engine.Stop(rec.Id,"user_requested");Assert.Single(presenter.Attempts);
        }
        f.Ui.Confirm!(ConfirmationDecision.Approve());Assert.Equal(1,calls);Assert.Empty(balloon.Bodies);Assert.Same(outer,NotificationActive(tray)[outer.RecordingId]);
        Assert.False(rec.OrdinaryBackendCreation!.ObserveRetired(()=>false));Assert.Equal(0,f.Ui.Recording);
    });

    private sealed class NotificationAffinity : IWindowDisplayAffinity
    {
        public bool SetExcludeFromCapture(IntPtr handle)=>false;
        public bool GetAffinity(IntPtr handle,out uint affinity){affinity=0;return false;}
    }
    [Theory][InlineData(UiLanguage.ZhCn)][InlineData(UiLanguage.EnUs)]
    public void BackendFailureNotificationUnshownFormAndLayoutUseNotStartedTitleAndBody(UiLanguage language)=>NotificationSta(()=>
    {
        var text=new UiTextProvider(language);using var form=new RecordingFailureNotificationForm(new("failed-request","backend_creation_failed"),text,new NotificationAffinity(),_=>{});
        Assert.False(form.Visible);Assert.Equal(text.Get("Tray_BackendCreationFailed"),form.BodyLabelForTests.Text);
        Assert.Equal(text.Get("Tray_RecordingFailure_BackendCreationTitle"),form.TitleLabelForTests.Text);
        Assert.Equal(language==UiLanguage.ZhCn?"录制未开始":"Recording did not start",form.TitleLabelForTests.Text);
        foreach(var dpi in new[]{96,144,192})Assert.True(RecordingFailureNotificationLayout.FitsAtDpi(text,"backend_creation_failed",dpi));
    });

    [Fact]
    public void BackendFailureNotificationNonNotifierHostKeepsObservableFallback()
    {
        using var f=new Fixture();f.Engine.BackendFactory=_=>throw new IOException(Secret);var rec=f.Create();f.Ui.Confirm!(ConfirmationDecision.Approve());
        Assert.Equal("backend_creation_failed",Assert.Single(f.Ui.Errors));Assert.DoesNotContain(f.Audit.Events,x=>x.Name.StartsWith("recording_failure_notification."));NotificationFailed(f,rec);
    }
}
