using AgentRecorder.App;
using AgentRecorder.Capture;
using AgentRecorder.Core;
using AgentRecorder.Infrastructure;
using Xunit;

namespace AgentRecorder.Tests;

public sealed partial class BackendCreationFailureTests
{
    private sealed class ExitRoute(Ui consent, TrayContext tray) : ITrayContext, IRecordingFailureNotifier
    {
        private readonly NotificationRoute inner = new(consent, tray);
        public string HostMode => inner.HostMode;
        public bool SupportsRegionSelectionUi => true;
        public void RequestConfirmation(RecordingConfirmationPresentation p, Action<ConfirmationDecision> callback) => inner.RequestConfirmation(p, callback);
        public void RequestRegionSelection(int t, Action<string,int,int,int,int,string,string> callback) => inner.RequestRegionSelection(t, callback);
        public void SetRecording(RecordingUiPresentation p) => inner.SetRecording(p);
        public void SetIdle(RecordingUiPresentation p) => inner.SetIdle(p);
        public void SetAllIdle() => inner.SetAllIdle();
        public void ShowError(string text) => inner.ShowError(text);
        public void ShowRecordingFailure(string id, string reason)
        {
            Assert.True(consent.Idle > 0); // Check ordering at the actual notifier call.
            inner.ShowRecordingFailure(id, reason);
        }
    }
    private static Recording ExitCreate(Fixture f, TrayContext tray)
    {
        var route = new ExitRoute(f.Ui, tray); f.Engine.SetTray(route);
        f.Engine.CreateRecording(f.Request(), "unexpected-exit", route);
        return Assert.Single(f.Engine._recs.Values);
    }
    private sealed class ExitBackend : ICaptureBackend, IFirstFrameObservableCaptureBackend
    {
        private Action<int, OutputMeta>? callback;
        public event Action<FirstFrameObservation>? FirstFrameObserved;
        public void Start(CaptureConfig config, CaptureAuthorizationProof proof) =>
            FirstFrameObserved?.Invoke(new FirstFrameObservation { FrameNumber = 1, OutTimeUs = 0 });
        public void OnNaturalExit(Action<int, OutputMeta> value) => callback = value;
        internal void Exit(int code, OutputMeta meta) => callback!(code, meta);
        public OutputMeta Stop() => Good();
        public int ExitCode => 0;
        public void Dispose() { }
        internal static OutputMeta Good() => new() { SizeBytes = 2048, DurationSeconds = 5, OutputFileExists = true };
    }
    private static void Started(Recording rec) => Assert.True(SpinWait.SpinUntil(
        () => rec.State == RecState.recording, TimeSpan.FromSeconds(5)));

    [Theory]
    [InlineData(false, UiLanguage.ZhCn, "nonzero")]
    [InlineData(false, UiLanguage.EnUs, "empty")]
    [InlineData(true, UiLanguage.ZhCn, "duration")]
    [InlineData(true, UiLanguage.EnUs, "nonzero")]
    public void UnexpectedExitRoutesAfterIdleThroughActualTray(bool active, UiLanguage language, string fault) => NotificationSta(() =>
    {
        using var f = new Fixture(); var balloon = new Balloon(); var presenter = new NotificationPresenter();
        using var tray = NotificationTray(f, language, balloon, presenter);
        var outer = NotificationOuter(); if (active) tray.SetRecording(outer);
        var backend = new ExitBackend(); f.Engine.BackendFactory = _ => (backend, "ffmpeg");
        var rec = ExitCreate(f, tray); rec.SyntheticCapturePlanForTests = true; f.Ui.Confirm!(ConfirmationDecision.Approve()); Started(rec);
        var meta = ExitBackend.Good(); meta.StderrLog = Secret;
        if (fault == "empty") meta.SizeBytes = 0;
        if (fault == "duration") meta.DurationSeconds = 0;
        backend.Exit(fault == "nonzero" ? 1 : 0, meta);
        Assert.True(rec.IsFinalized); Assert.Equal(RecState.failed, rec.State); Assert.Equal("unexpected_exit", rec.StopReason);
        Assert.Equal(fault == "nonzero" ? "non_zero_exit" : fault == "empty" ? "empty_output" : "zero_duration", rec.Error);
        Assert.True(f.Ui.Idle > 0); var attempt = Assert.Single(presenter.Attempts);
        Assert.Equal("unexpected_exit", attempt.Request.ReasonCode); Assert.Equal(active, attempt.Exclusion);
        Assert.Equal("Tray_RecordingFailure_GenericBody", RecordingFailureNotificationLayout.BodyKey("unexpected_exit"));
        Assert.DoesNotContain(Secret, attempt.Text.Get("Tray_RecordingFailure_GenericBody"));
        Assert.False(File.Exists(rec.OutputPath)); Assert.Equal("not_applicable", rec.BundleSnapshot.Status); Assert.Equal(0, f.Bundle.Calls);
        Assert.True(SpinWait.SpinUntil(() => f.History.Read(rec.Id)?.Status == "failed", TimeSpan.FromSeconds(5)));
        Assert.Equal("unexpected_exit", f.History.Read(rec.Id)!.ReasonCode);
        backend.Exit(1, meta); f.Engine.Stop(rec.Id, "user_requested"); Assert.Single(presenter.Attempts); Assert.Empty(balloon.Bodies);
        if (active) Assert.Same(outer, NotificationActive(tray)[outer.RecordingId]);
        Assert.Equal("worker_retirement_unknown", f.Engine.TryReserveIdleHostShutdown(() => null));
    });

    [Theory]
    [InlineData(UiLanguage.ZhCn, false)] [InlineData(UiLanguage.EnUs, false)]
    [InlineData(UiLanguage.ZhCn, true)] [InlineData(UiLanguage.EnUs, true)]
    public void UnexpectedExitExclusionDefersAndDeduplicates(UiLanguage language, bool dishonest) => NotificationSta(() =>
    {
        using var f = new Fixture(); var balloon = new Balloon();
        var presenter = new NotificationPresenter { Affinity = dishonest, Prove = !dishonest };
        using var tray = NotificationTray(f, language, balloon, presenter); var outer = NotificationOuter(); tray.SetRecording(outer);
        var backend = new ExitBackend(); f.Engine.BackendFactory = _ => (backend, "ffmpeg");
        var rec = NotificationCreate(f, tray); rec.SyntheticCapturePlanForTests = true; f.Ui.Confirm!(ConfirmationDecision.Approve()); Started(rec);
        backend.Exit(1, new OutputMeta()); backend.Exit(1, new OutputMeta());
        Assert.Single(presenter.Attempts); Assert.Equal(0, presenter.Visible); Assert.Equal(1, NotificationManager(tray).PendingCountForTests);
        Assert.DoesNotContain(f.Audit.Events, x => x.Name == "recording_failure_notification.shown");
        tray.SetIdle(outer); Assert.Equal(1, presenter.Visible); Assert.Equal(2, presenter.Attempts.Count);
        tray.ShowRecordingFailure(rec.Id, "unexpected_exit"); Assert.Equal(2, presenter.Attempts.Count); Assert.Empty(balloon.Bodies);
    });

    [Theory]
    [InlineData("success")] [InlineData("stop")] [InlineData("not-started")]
    [InlineData("future")] [InlineData("standing")] [InlineData("recurring")] [InlineData("required")]
    public void UnexpectedExitDoesNotExpandOtherWinnersOrScopes(string kind)
    {
        using var f = new Fixture(); var backend = new ExitBackend(); f.Engine.BackendFactory = _ => (backend, "ffmpeg");
        var rec = f.Create(); rec.SyntheticCapturePlanForTests = true; f.Ui.Confirm!(ConfirmationDecision.Approve()); Started(rec);
        if (kind == "not-started") rec.StartedAtUtc = default;
        if (kind == "future") rec.IsFutureWindowOneShotExecution = true;
        if (kind == "standing") rec.IsStandingLeaseExecution = true;
        if (kind == "recurring") rec.IsRecurringLeaseExecution = true;
        if (kind == "required") rec.IsRequiredOnceExecution = true;
        if (kind == "stop") f.Engine.Stop(rec.Id, "user_requested");
        backend.Exit(kind == "success" ? 0 : 1, ExitBackend.Good());
        Assert.DoesNotContain(f.Audit.Events, x => x.Name == "recording_failure_notification.unavailable");
        if (kind is "success" or "stop") Assert.Equal(RecState.completed, rec.State);
        Assert.Empty(f.Ui.Errors);
    }

    [Fact]
    public void UnexpectedExitNonNotifierUsesSafeAuditOnly()
    {
        using var f = new Fixture(); var backend = new ExitBackend(); f.Engine.BackendFactory = _ => (backend, "ffmpeg");
        var rec = f.Create(); rec.SyntheticCapturePlanForTests = true; f.Ui.Confirm!(ConfirmationDecision.Approve()); Started(rec); backend.Exit(1, new OutputMeta { StderrLog = Secret });
        var audit = Assert.Single(f.Audit.Events, x => x.Name == "recording_failure_notification.unavailable");
        Assert.DoesNotContain(Secret, audit.Json); Assert.Contains("unexpected_exit", audit.Json); Assert.Empty(f.Ui.Errors);
    }

    [Theory]
    [InlineData("storage_capacity_unavailable")]
    [InlineData("storage_space_low")]
    [InlineData("display_unavailable")]
    [InlineData("window_closed")]
    [InlineData("audio_capture_discontinuous")]
    public void UnexpectedExitSpecificReasonWinsExactlyOneNotification(string reason) => NotificationSta(() =>
    {
        using var f = new Fixture(); var balloon = new Balloon(); var presenter = new NotificationPresenter();
        using var tray = NotificationTray(f, UiLanguage.EnUs, balloon, presenter);
        var backend = new ExitBackend(); f.Engine.BackendFactory = _ => (backend, "ffmpeg");
        var rec = ExitCreate(f, tray); rec.SyntheticCapturePlanForTests = true; f.Ui.Confirm!(ConfirmationDecision.Approve()); Started(rec);
        rec.AudioSourceKind = AudioCaptureSourceKind.SystemLoopback;
        rec.Config.AudioSourceKind = AudioCaptureSourceKind.SystemLoopback;
        var meta = new OutputMeta { StopReason = reason, AudioHelperErrorCode = "audio_capture_error" };
        rec.TrustedLifecycleAbortReason = reason switch
        {
            "storage_capacity_unavailable" => CaptureAbortReason.StorageCapacityUnavailable,
            "storage_space_low" => CaptureAbortReason.StorageSpaceLow,
            "display_unavailable" => CaptureAbortReason.DisplayUnavailable,
            _ => null
        };
        if (reason == "window_closed") rec.BackendType = "wgc-continuous";
        backend.Exit(1, meta); backend.Exit(1, meta);
        Assert.Equal(reason, Assert.Single(presenter.Attempts).Request.ReasonCode);
        Assert.Single(f.Audit.Events, x => x.Name == "recording_failure_notification.requested");
        Assert.Empty(balloon.Bodies); Assert.Equal(RecState.failed, rec.State);
    });
}
