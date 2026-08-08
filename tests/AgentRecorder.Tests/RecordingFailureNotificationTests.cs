using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Windows.Forms;
using AgentRecorder.App;
using AgentRecorder.Core;
using AgentRecorder.Infrastructure;
using AgentRecorder.Logging;
using AgentRecorder.Windows;
using Xunit;

namespace AgentRecorder.Tests;

[Collection("NonParallel-AgentRecorderDataDir")]
public sealed class RecordingFailureNotificationTests : IDisposable
{
    private readonly TempDirectory _dataDir = new();

    public RecordingFailureNotificationTests()
    {
        DataDirResolver.SetOverride(_dataDir.Path);
    }

    public void Dispose()
    {
        DataDirResolver.ClearOverride();
        _dataDir.Dispose();
    }

    [Theory]
    [InlineData("window_closed")]
    [InlineData("window_minimized")]
    [InlineData("size_changed")]
    public void Manager_RequestsOneLocalizedAppOwnedNotification(string reasonCode)
    {
        var audit = new CaptureAuditLogger();
        var presenter = new FakePresenter();
        using var manager = new RecordingFailureNotificationManager(
            audit, () => new UiTextProvider(UiLanguage.ZhCn), () => 0, presenter);

        manager.Request("rec-195", reasonCode);

        var attempt = Assert.Single(presenter.Attempts);
        Assert.Equal("rec-195", attempt.Request.RecordingId);
        Assert.Equal(reasonCode, attempt.Request.ReasonCode);
        Assert.False(attempt.RequireCaptureExclusion);
        Assert.Equal(UiLanguage.ZhCn, attempt.Text.Language);
        Assert.Contains("录制", attempt.Text.Get("Tray_RecordingFailure_Title"));
        Assert.DoesNotContain(reasonCode, attempt.Text.Get(BodyKey(reasonCode)), StringComparison.Ordinal);
        Assert.Contains(audit.Events, e => e == "recording_failure_notification.requested");
        Assert.Contains(audit.Events, e => e == "recording_failure_notification.shown");
        var shown = Assert.Single(audit.Payloads, p => p.Event == "recording_failure_notification.shown");
        Assert.DoesNotContain(attempt.Text.Get(BodyKey(reasonCode)), shown.Json, StringComparison.Ordinal);
        Assert.DoesNotContain("output", shown.Json, StringComparison.OrdinalIgnoreCase);

        presenter.TriggerClose(RecordingFailureNotificationCloseReason.UserDismissed);

        Assert.Contains(audit.Events, e => e == "recording_failure_notification.closed");
    }

    [Fact]
    public void TrayContext_LifecycleFailureDoesNotCallShellBalloon()
    {
        RunOnSta(() =>
        {
            var audit = new CaptureAuditLogger();
            var balloon = new FakeBalloonTip();
            var presenter = new FakePresenter();
            var engine = new RecordingEngine(audit);
            using var tray = new TrayContext(
                engine,
                audit,
                FakeGlobalStopHotkeyFactory.Create(),
                uiTextProvider: new UiTextProvider(UiLanguage.EnUs),
                balloonTip: balloon,
                failureNotificationPresenter: presenter);
            engine.SetTray(tray);

            tray.ShowRecordingFailure("rec-close", "window_closed");

            Assert.Equal(0, balloon.CallCount);
            var attempt = Assert.Single(presenter.Attempts);
            Assert.Equal("rec-close", attempt.Request.RecordingId);
            Assert.Equal("window_closed", attempt.Request.ReasonCode);
        });
    }

    [Fact]
    public void Manager_DeduplicatesCompletionRaceAndKeepsStableRecordingId()
    {
        var audit = new CaptureAuditLogger();
        var presenter = new FakePresenter();
        using var manager = new RecordingFailureNotificationManager(
            audit, () => new UiTextProvider(UiLanguage.EnUs), () => 0, presenter);

        manager.Request("rec-race", "window_minimized");
        manager.Request("rec-race", "window_minimized");
        presenter.TriggerClose(RecordingFailureNotificationCloseReason.Timeout);
        manager.Request("rec-race", "window_minimized");

        Assert.Single(presenter.Attempts);
        Assert.Equal(2, audit.Events.Count(e => e == "recording_failure_notification.suppressed"));
        Assert.Contains(audit.Payloads, p => p.Event == "recording_failure_notification.suppressed" &&
                                             p.Json.Contains("duplicate", StringComparison.Ordinal));
    }

    [Fact]
    public void Manager_DefersWhenActiveRecordingCannotBeExcluded_ThenShowsWhenIdle()
    {
        var audit = new CaptureAuditLogger();
        var presenter = new FakePresenter { AffinityAvailable = false };
        int activeCount = 1;
        using var manager = new RecordingFailureNotificationManager(
            audit, () => new UiTextProvider(UiLanguage.EnUs), () => activeCount, presenter);

        manager.Request("rec-inner", "size_changed");

        Assert.Single(presenter.Attempts);
        Assert.False(presenter.Attempts[0].Shown);
        Assert.Equal(1, manager.PendingCountForTests);
        Assert.Contains(audit.Events, e => e == "recording_failure_notification.deferred");

        activeCount = 0;
        presenter.AffinityAvailable = false;
        manager.ActiveRecordingCountChanged();

        Assert.Equal(2, presenter.Attempts.Count);
        Assert.True(presenter.Attempts[1].Shown);
        Assert.False(presenter.Attempts[1].RequireCaptureExclusion);
        Assert.Equal(0, manager.PendingCountForTests);
    }

    [Fact]
    public void Manager_AllowsActiveRecordingWhenDisplayAffinitySucceeds()
    {
        var audit = new CaptureAuditLogger();
        var presenter = new FakePresenter { AffinityAvailable = true };
        using var manager = new RecordingFailureNotificationManager(
            audit, () => new UiTextProvider(UiLanguage.EnUs), () => 1, presenter);

        manager.Request("rec-outer", "window_closed");

        var attempt = Assert.Single(presenter.Attempts);
        Assert.True(attempt.RequireCaptureExclusion);
        Assert.True(attempt.Shown);
        Assert.DoesNotContain("recording_failure_notification.deferred", audit.Events);
    }

    [Theory]
    [InlineData("output_validation_failed")]
    [InlineData("unexpected_exit")]
    [InlineData("cancelled")]
    [InlineData("timeout")]
    public void Manager_SuppressesNonLifecycleReasons(string reasonCode)
    {
        var audit = new CaptureAuditLogger();
        var presenter = new FakePresenter();
        using var manager = new RecordingFailureNotificationManager(
            audit, () => new UiTextProvider(UiLanguage.EnUs), () => 0, presenter);

        manager.Request("rec-no-toast", reasonCode);

        Assert.Empty(presenter.Attempts);
        Assert.Contains(audit.Payloads, p => p.Event == "recording_failure_notification.suppressed" &&
                                             p.Json.Contains("unsupported", StringComparison.Ordinal));
    }

    [Fact]
    public void Manager_QueuesInFifoOrderAndDisposesCurrentAndQueuedNotifications()
    {
        var audit = new CaptureAuditLogger();
        var presenter = new FakePresenter();
        using var manager = new RecordingFailureNotificationManager(
            audit, () => new UiTextProvider(UiLanguage.EnUs), () => 0, presenter);

        manager.Request("rec-1", "window_closed");
        manager.Request("rec-2", "window_minimized");
        Assert.Equal(1, manager.PendingCountForTests);

        presenter.TriggerClose(RecordingFailureNotificationCloseReason.UserDismissed);
        Assert.Equal("rec-2", presenter.Attempts[1].Request.RecordingId);
        presenter.TriggerClose(RecordingFailureNotificationCloseReason.Timeout);

        manager.Request("rec-3", "size_changed");
        manager.Request("rec-4", "window_closed");
        manager.Dispose();

        Assert.True(presenter.DisposeCalled);
        Assert.Contains(audit.Events, e => e == "recording_failure_notification.closed");
        Assert.Contains(audit.Payloads, p => p.Event == "recording_failure_notification.suppressed" &&
                                             p.Json.Contains("application_exit", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(UiLanguage.ZhCn)]
    [InlineData(UiLanguage.EnUs)]
    public void Layout_FitsLocalizedLongestTextAt100_150_And200Dpi(UiLanguage language)
    {
        var text = new UiTextProvider(language);
        foreach (var reason in new[] { "window_closed", "window_minimized", "size_changed" })
        {
            Assert.True(RecordingFailureNotificationLayout.FitsAtDpi(text, reason, 96));
            Assert.True(RecordingFailureNotificationLayout.FitsAtDpi(text, reason, 144));
            Assert.True(RecordingFailureNotificationLayout.FitsAtDpi(text, reason, 192));
        }
    }

    [Fact]
    public void Form_IsNonActivating_UsesRealHandleForAffinity_CloseAndTimerAreDeterministic()
    {
        RunOnSta(() =>
        {
            var affinity = new FakeDisplayAffinity { Result = true };
            var closed = new List<RecordingFailureNotificationCloseReason>();
            using var form = new RecordingFailureNotificationForm(
                new RecordingFailureNotificationRequest("rec-form", "window_closed"),
                new UiTextProvider(UiLanguage.EnUs),
                affinity,
                reason => closed.Add(reason));

            form.ApplyDisplayAffinity(IntPtr.Zero);
            Assert.Empty(affinity.Handles);
            form.CreateControl();
            _ = form.Handle;
            Assert.NotEqual(IntPtr.Zero, form.Handle);
            Assert.True(form.DisplayAffinityRequestedForTests);
            Assert.Contains(form.Handle, affinity.Handles);
            Assert.True(form.ShowWithoutActivationForTests);
            Assert.NotEqual(0, form.ExtendedStyleForTests & 0x8000000);

            form.Show();
            Application.DoEvents();
            Assert.True(form.TimerEnabledForTests);
            form.CloseButtonForTests.PerformClick();
            Application.DoEvents();
            form.CloseFor(RecordingFailureNotificationCloseReason.Timeout);
            Assert.Single(closed);
            Assert.Equal(RecordingFailureNotificationCloseReason.UserDismissed, closed[0]);
            Assert.True(form.TimerDisposedForTests);
        });
    }

    [Fact]
    public void Form_DisplayAffinityFailureIsAuditableButDoesNotPreventIdleDisplay()
    {
        RunOnSta(() =>
        {
            var affinity = new FakeDisplayAffinity { Result = false };
            using var form = new RecordingFailureNotificationForm(
                new RecordingFailureNotificationRequest("rec-no-active", "size_changed"),
                new UiTextProvider(UiLanguage.ZhCn),
                affinity,
                _ => { });

            _ = form.Handle;
            Assert.True(form.DisplayAffinityRequestedForTests);
            Assert.False(form.DisplayAffinityAppliedForTests);
            form.Show();
            Application.DoEvents();
            Assert.True(form.Visible);
            form.CloseFor(RecordingFailureNotificationCloseReason.UserDismissed);
        });
    }

    [Fact]
    public void HeadlessFailureNotificationIsAuditOnly()
    {
        var audit = new CaptureAuditLogger();
        var tray = new AgentRecorder.Headless.HeadlessTrayContext(audit);
        ((IRecordingFailureNotifier)tray).ShowRecordingFailure("rec-headless", "size_changed");

        var payload = Assert.Single(audit.Payloads, p => p.Event == "recording_failure_notification.requested");
        using var json = JsonDocument.Parse(payload.Json);
        Assert.Equal("audit_only", json.RootElement.GetProperty("outcome").GetString());
        Assert.DoesNotContain("shown", string.Join(" ", audit.Events));
    }

    private static string BodyKey(string reasonCode) => reasonCode switch
    {
        "window_closed" => "Tray_RecordingFailure_WindowClosedBody",
        "window_minimized" => "Tray_RecordingFailure_WindowMinimizedBody",
        _ => "Tray_RecordingFailure_SizeChangedBody"
    };

    private static void RunOnSta(Action action)
    {
        Exception? error = null;
        var thread = new Thread(() =>
        {
            try { action(); }
            catch (Exception ex) { error = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (error != null)
            throw new Xunit.Sdk.XunitException(error.ToString());
    }

    private sealed class CaptureAuditLogger : AuditLogger
    {
        public List<string> Events { get; } = new();
        public List<(string Event, string Json)> Payloads { get; } = new();

        public override void Log(string evt, object payload)
        {
            Events.Add(evt);
            Payloads.Add((evt, JsonSerializer.Serialize(payload)));
            base.Log(evt, payload);
        }
    }

    private sealed class FakePresenter : IRecordingFailureNotificationPresenter
    {
        internal sealed record Attempt(
            RecordingFailureNotificationRequest Request,
            IUiTextProvider Text,
            bool RequireCaptureExclusion,
            bool Shown);

        public List<Attempt> Attempts { get; } = new();
        public bool AffinityAvailable { get; set; } = true;
        public bool DisposeCalled { get; private set; }
        private Action<RecordingFailureNotificationCloseReason>? _onClosed;

        public NotificationPresentationResult TryShow(
            RecordingFailureNotificationRequest request,
            IUiTextProvider textProvider,
            bool requireCaptureExclusion,
            Action<RecordingFailureNotificationCloseReason> onClosed)
        {
            bool shown = !requireCaptureExclusion || AffinityAvailable;
            Attempts.Add(new Attempt(request, textProvider, requireCaptureExclusion, shown));
            if (shown)
                _onClosed = onClosed;
            return new NotificationPresentationResult(
                shown,
                DisplayAffinityRequested: true,
                DisplayAffinityApplied: AffinityAvailable);
        }

        public void TriggerClose(RecordingFailureNotificationCloseReason reason)
        {
            var callback = _onClosed;
            _onClosed = null;
            callback?.Invoke(reason);
        }

        public void Close(RecordingFailureNotificationCloseReason reason) => TriggerClose(reason);
        public void Dispose() => DisposeCalled = true;
    }

    private sealed class FakeBalloonTip : ITrayBalloonTip
    {
        public int CallCount { get; private set; }
        public void ShowBalloonTip(int timeout, string title, string body, ToolTipIcon icon) => CallCount++;
    }

    private sealed class FakeDisplayAffinity : IWindowDisplayAffinity
    {
        public bool Result { get; set; }
        public List<IntPtr> Handles { get; } = new();

        public bool SetExcludeFromCapture(IntPtr hWnd)
        {
            Handles.Add(hWnd);
            return Result;
        }

        public bool GetAffinity(IntPtr hWnd, out uint affinity)
        {
            affinity = Native.WDA_EXCLUDEFROMCAPTURE;
            return Result;
        }
    }
}
