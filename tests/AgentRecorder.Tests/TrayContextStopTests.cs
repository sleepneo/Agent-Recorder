using System;
using System.Drawing;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Windows.Forms;
using AgentRecorder.App;
using AgentRecorder.Capture;
using AgentRecorder.Core;
using AgentRecorder.Logging;
using Xunit;

namespace AgentRecorder.Tests;

/// <summary>
/// Tests for TrayContext stop-state machine, global hotkey integration, and menu/icon updates.
/// All tests use a fake global hotkey so they never occupy the user's Ctrl+Shift+F10.
/// </summary>
public class TrayContextStopTests
{
    private static void RunOnSta(Action action)
    {
        Exception? ex = null;
        var thread = new Thread(() =>
        {
            try { action(); }
            catch (Exception e) { ex = e; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (ex != null)
            throw new TargetInvocationException(ex);
    }

    private static T GetPrivateField<T>(object obj, string fieldName)
    {
        var field = obj.GetType().GetField(fieldName, BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(field);
        return (T)field!.GetValue(obj)!;
    }

    private static Recording MakeRecording(
        (int x, int y, int w, int h) bounds,
        string? nestedRole = null)
    {
        return new Recording
        {
            SourceType = "region",
            StartedAtUtc = DateTime.UtcNow,
            NestedRole = nestedRole,
            Config = new CaptureConfig
            {
                SourceKind = "region",
                Bounds = bounds,
                OutputPath = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"test-tray-{Guid.NewGuid():N}.mp4")
            }
        };
    }

    private static TrayContext CreateTrayContext(RecordingEngine engine, AuditLogger audit, bool hotkeyRegistered = true, IWindowActivator? confirmationActivator = null)
    {
        var ctx = new TrayContext(engine, audit, FakeGlobalStopHotkeyFactory.Create(hotkeyRegistered), confirmationActivator);
        engine.SetTray(ctx);
        return ctx;
    }

    private sealed class FakeConfirmationActivator : IWindowActivator
    {
        public bool SetTopMostResult { get; set; } = true;
        public bool SetForegroundResult { get; set; } = true;
        public bool BringToTopResult { get; set; } = true;
        public bool SetTopMostCalled { get; private set; }
        public bool SetForegroundCalled { get; private set; }
        public bool BringToTopCalled { get; private set; }

        public bool SetTopMost(IntPtr hWnd)
        {
            SetTopMostCalled = true;
            return SetTopMostResult;
        }

        public bool SetForeground(IntPtr hWnd)
        {
            SetForegroundCalled = true;
            return SetForegroundResult;
        }

        public bool BringToTop(IntPtr hWnd)
        {
            BringToTopCalled = true;
            return BringToTopResult;
        }

        public IntPtr GetForegroundWindow() => IntPtr.Zero;
    }

    [Fact]
    public void TrayContext_StopCapabilityProperties_AreTrue()
    {
        RunOnSta(() =>
        {
            var audit = new CaptureAuditLogger();
            var engine = new RecordingEngine(audit);
            using var ctx = CreateTrayContext(engine, audit);

            Assert.True(ctx.SupportsFloatingStopButton);
            Assert.True(ctx.SupportsTrayStop);
            Assert.True(ctx.SupportsGlobalStopHotkey);
            Assert.Equal("Ctrl+Shift+F10", ctx.GlobalStopHotkeyGesture);
            Assert.True(ctx.IsGlobalStopHotkeyRegistered);
        });
    }

    [Fact]
    public void TrayContext_FakeHotkey_RegistrationStateIsControlled()
    {
        RunOnSta(() =>
        {
            var audit = new CaptureAuditLogger();
            var engine = new RecordingEngine(audit);
            using var ctx = CreateTrayContext(engine, audit, hotkeyRegistered: false);

            Assert.False(ctx.IsGlobalStopHotkeyRegistered);
        });
    }

    [Fact]
    public void TrayContext_SetRecording_UpdatesIconTextAndMenu()
    {
        RunOnSta(() =>
        {
            var audit = new CaptureAuditLogger();
            var engine = new RecordingEngine(audit);
            using var ctx = CreateTrayContext(engine, audit);
            var rec = MakeRecording((100, 100, 800, 600));

            ctx.SetRecording(rec);

            var icon = GetPrivateField<NotifyIcon>(ctx, "_icon");
            var stopItem = GetPrivateField<ToolStripMenuItem>(ctx, "_stopItem");
            var iconFactory = GetPrivateField<TrayIconFactory>(ctx, "_iconFactory");

            Assert.Equal("Agent Recorder — 正在录制", icon.Text);
            Assert.Same(iconFactory.RecordingIcon, icon.Icon);
            Assert.True(stopItem.Enabled);
            Assert.Equal("停止录制", stopItem.Text);
        });
    }

    [Fact]
    public void TrayContext_SetTwoRecordings_MenuShowsStopAll()
    {
        RunOnSta(() =>
        {
            var audit = new CaptureAuditLogger();
            var engine = new RecordingEngine(audit);
            using var ctx = CreateTrayContext(engine, audit);
            var r1 = MakeRecording((100, 100, 800, 600));
            var r2 = MakeRecording((500, 500, 640, 480));

            ctx.SetRecording(r1);
            ctx.SetRecording(r2);

            var icon = GetPrivateField<NotifyIcon>(ctx, "_icon");
            var stopItem = GetPrivateField<ToolStripMenuItem>(ctx, "_stopItem");

            Assert.Contains("（2条并发）", icon.Text);
            Assert.True(stopItem.Enabled);
            Assert.Equal("停止全部录制（2）", stopItem.Text);
        });
    }

    [Fact]
    public void TrayContext_SetIdle_ReturnsToIdleState()
    {
        RunOnSta(() =>
        {
            var audit = new CaptureAuditLogger();
            var engine = new RecordingEngine(audit);
            using var ctx = CreateTrayContext(engine, audit);
            var rec = MakeRecording((100, 100, 800, 600));

            ctx.SetRecording(rec);
            ctx.SetIdle(rec);

            var icon = GetPrivateField<NotifyIcon>(ctx, "_icon");
            var stopItem = GetPrivateField<ToolStripMenuItem>(ctx, "_stopItem");
            var iconFactory = GetPrivateField<TrayIconFactory>(ctx, "_iconFactory");

            Assert.Equal("Agent Recorder — 空闲", icon.Text);
            Assert.Same(iconFactory.IdleIcon, icon.Icon);
            Assert.False(stopItem.Enabled);
        });
    }

    [Fact]
    public void TrayContext_StopAll_LogsRequestOnceAndShowsStopping()
    {
        RunOnSta(() =>
        {
            var audit = new CaptureAuditLogger();
            var engine = new RecordingEngine(audit);
            using var ctx = CreateTrayContext(engine, audit);
            var rec = MakeRecording((100, 100, 800, 600));

            ctx.SetRecording(rec);

            // Invoke private StopAll via reflection.
            var stopAllMethod = ctx.GetType().GetMethod("StopAll", BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.NotNull(stopAllMethod);
            stopAllMethod!.Invoke(ctx, new object[] { "test" });

            // The UI must synchronously switch to the stopping state before any background work runs.
            var icon = GetPrivateField<NotifyIcon>(ctx, "_icon");
            var stopItem = GetPrivateField<ToolStripMenuItem>(ctx, "_stopItem");

            var requestEvents = audit.Events.Where(e => e.evt == "recording.stop_requested_local").ToList();
            Assert.Single(requestEvents);
            Assert.Contains("\"trigger\":\"test\"", requestEvents[0].json);
            Assert.False(stopItem.Enabled);
            Assert.Contains("正在停止", stopItem.Text);
            Assert.Contains("正在停止", icon.Text);
            Assert.True(icon.Text.Length <= 127);
        });
    }

    [Fact]
    public void TrayContext_GlobalHotkeyIdle_LogsNoOpOnce()
    {
        RunOnSta(() =>
        {
            var audit = new CaptureAuditLogger();
            var engine = new RecordingEngine(audit);
            using var ctx = CreateTrayContext(engine, audit);

            var onGlobalHotkey = ctx.GetType().GetMethod("OnGlobalHotkeyPressed", BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.NotNull(onGlobalHotkey);
            onGlobalHotkey!.Invoke(ctx, Array.Empty<object>());

            var icon = GetPrivateField<NotifyIcon>(ctx, "_icon");
            Assert.Equal("Agent Recorder — 空闲", icon.Text);

            var requestEvents = audit.Events.Where(e => e.evt == "recording.stop_requested_local").ToList();
            Assert.Single(requestEvents);
            Assert.Contains("\"trigger\":\"global_hotkey\"", requestEvents[0].json);
            Assert.Contains("\"active_count\":0", requestEvents[0].json);
        });
    }

    [Fact]
    public void TrayContext_GlobalHotkeyActive_RequestsStopAllOnce()
    {
        RunOnSta(() =>
        {
            var audit = new CaptureAuditLogger();
            var engine = new RecordingEngine(audit);
            using var ctx = CreateTrayContext(engine, audit);
            var rec = MakeRecording((100, 100, 800, 600));

            ctx.SetRecording(rec);
            engine._recs[rec.Id] = rec;

            var onGlobalHotkey = ctx.GetType().GetMethod("OnGlobalHotkeyPressed", BindingFlags.NonPublic | BindingFlags.Instance);
            onGlobalHotkey!.Invoke(ctx, Array.Empty<object>());

            Application.DoEvents();
            Thread.Sleep(50);
            Application.DoEvents();

            var requestEvents = audit.Events.Where(e => e.evt == "recording.stop_requested_local").ToList();
            Assert.Single(requestEvents);
            Assert.Contains("\"trigger\":\"global_hotkey\"", requestEvents[0].json);
        });
    }

    [Fact]
    public void TrayContext_Dispose_ReleasesResources()
    {
        RunOnSta(() =>
        {
            var audit = new CaptureAuditLogger();
            var engine = new RecordingEngine(audit);
            var ctx = CreateTrayContext(engine, audit);
            ctx.Dispose();

            var icon = GetPrivateField<NotifyIcon>(ctx, "_icon");
            Assert.False(icon.Visible);
        });
    }

    [Fact]
    public void TrayContext_Dispose_CallsHotkeyDisposeOnceAndSetsRegisteredFalse()
    {
        RunOnSta(() =>
        {
            var audit = new CaptureAuditLogger();
            var engine = new RecordingEngine(audit);
            var ctx = CreateTrayContext(engine, audit);

            var hotkey = GetPrivateField<IGlobalStopHotkey>(ctx, "_globalStopHotkey");
            Assert.IsType<FakeGlobalStopHotkey>(hotkey);
            var fake = (FakeGlobalStopHotkey)hotkey;

            ctx.Dispose();

            Assert.Equal(1, fake.DisposeCallCount);
            Assert.False(fake.Registered);
        });
    }

    [Fact]
    public void TrayContext_Dispose_Twice_CallsHotkeyDisposeOnce()
    {
        RunOnSta(() =>
        {
            var audit = new CaptureAuditLogger();
            var engine = new RecordingEngine(audit);
            var ctx = CreateTrayContext(engine, audit);
            var fake = (FakeGlobalStopHotkey)GetPrivateField<IGlobalStopHotkey>(ctx, "_globalStopHotkey");

            ctx.Dispose();
            ctx.Dispose();

            Assert.Equal(1, fake.DisposeCallCount);
        });
    }

    [Fact]
    public void TrayContext_UsesFakeHotkey_NoNativeWindowCreated()
    {
        RunOnSta(() =>
        {
            var audit = new CaptureAuditLogger();
            var engine = new RecordingEngine(audit);
            var ctx = CreateTrayContext(engine, audit);

            var hotkey = GetPrivateField<IGlobalStopHotkey>(ctx, "_globalStopHotkey");
            Assert.IsType<FakeGlobalStopHotkey>(hotkey);
            Assert.IsNotType<GlobalStopHotkey>(hotkey);

            ctx.Dispose();
        });
    }

    [Fact]
    public void TrayContext_StopAll_PerRecordingStoppingEvent_CountEqualsActive()
    {
        RunOnSta(() =>
        {
            var audit = new CaptureAuditLogger();
            var engine = new RecordingEngine(audit);
            using var ctx = CreateTrayContext(engine, audit);
            var r1 = MakeRecording((100, 100, 800, 600));
            var r2 = MakeRecording((500, 500, 640, 480), "inner");

            ctx.SetRecording(r1);
            ctx.SetRecording(r2);

            var stopAllMethod = ctx.GetType().GetMethod("StopAll", BindingFlags.NonPublic | BindingFlags.Instance);
            stopAllMethod!.Invoke(ctx, new object[] { "test" });

            var stoppingEvents = audit.Events.Where(e => e.evt == "recording_stop_control.stopping").ToList();
            Assert.Equal(2, stoppingEvents.Count);
            Assert.All(stoppingEvents, e => Assert.Contains("\"trigger\":\"test\"", e.json));
        });
    }

    [Fact]
    public void TrayContext_TrayMenuStop_RequestsStopWithTrayMenuReason()
    {
        RunOnSta(() =>
        {
            var audit = new CaptureAuditLogger();
            var engine = new RecordingEngine(audit);
            using var ctx = CreateTrayContext(engine, audit);
            var rec = MakeRecording((100, 100, 800, 600));

            ctx.SetRecording(rec);
            engine._recs[rec.Id] = rec;

            var stopAllMethod = ctx.GetType().GetMethod("StopAll", BindingFlags.NonPublic | BindingFlags.Instance);
            stopAllMethod!.Invoke(ctx, new object[] { "tray_menu" });

            Application.DoEvents();
            Thread.Sleep(500);
            Application.DoEvents();

            var requestEvents = audit.Events.Where(e => e.evt == "recording.stop_requested_local").ToList();
            Assert.Single(requestEvents);
            Assert.Contains("\"trigger\":\"tray_menu\"", requestEvents[0].json);

            var stoppingEvents = audit.Events.Where(e => e.evt == "recording_stop_control.stopping").ToList();
            Assert.Single(stoppingEvents);
            Assert.Contains("\"trigger\":\"tray_menu\"", stoppingEvents[0].json);

            var engineStopping = audit.Events.Where(e => e.evt == "recording.stopping").ToList();
            Assert.Single(engineStopping);
            Assert.Contains("\"reason\":\"tray_menu\"", engineStopping[0].json);
        });
    }

    [Fact]
    public void TrayContext_GlobalHotkeyStop_RequestsStopWithGlobalHotkeyReason()
    {
        RunOnSta(() =>
        {
            var audit = new CaptureAuditLogger();
            var engine = new RecordingEngine(audit);
            using var ctx = CreateTrayContext(engine, audit);
            var rec = MakeRecording((100, 100, 800, 600));

            ctx.SetRecording(rec);
            engine._recs[rec.Id] = rec;

            var onGlobalHotkey = ctx.GetType().GetMethod("OnGlobalHotkeyPressed", BindingFlags.NonPublic | BindingFlags.Instance);
            onGlobalHotkey!.Invoke(ctx, Array.Empty<object>());

            Application.DoEvents();
            Thread.Sleep(500);
            Application.DoEvents();

            var requestEvents = audit.Events.Where(e => e.evt == "recording.stop_requested_local").ToList();
            Assert.Single(requestEvents);
            Assert.Contains("\"trigger\":\"global_hotkey\"", requestEvents[0].json);

            var stoppingEvents = audit.Events.Where(e => e.evt == "recording_stop_control.stopping").ToList();
            Assert.Single(stoppingEvents);
            Assert.Contains("\"trigger\":\"global_hotkey\"", stoppingEvents[0].json);

            var engineStopping = audit.Events.Where(e => e.evt == "recording.stopping").ToList();
            Assert.Single(engineStopping);
            Assert.Contains("\"reason\":\"global_hotkey\"", engineStopping[0].json);
        });
    }

    [Fact]
    public void TrayContext_FloatingButtonStop_RequestsStopWithFloatingButtonReason()
    {
        RunOnSta(() =>
        {
            var audit = new CaptureAuditLogger();
            var engine = new RecordingEngine(audit);
            using var ctx = CreateTrayContext(engine, audit);
            var rec = MakeRecording((100, 100, 800, 600));

            ctx.SetRecording(rec);
            engine._recs[rec.Id] = rec;

            var onFloating = ctx.GetType().GetMethod("OnFloatingStopRequested", BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.NotNull(onFloating);
            onFloating!.Invoke(ctx, new object[] { rec.Id });

            Application.DoEvents();
            Thread.Sleep(500);
            Application.DoEvents();

            var stoppingEvents = audit.Events.Where(e => e.evt == "recording_stop_control.stopping").ToList();
            Assert.Single(stoppingEvents);
            Assert.Contains("\"trigger\":\"floating_button\"", stoppingEvents[0].json);

            var engineStopping = audit.Events.Where(e => e.evt == "recording.stopping").ToList();
            Assert.Single(engineStopping);
            Assert.Contains("\"reason\":\"floating_button\"", engineStopping[0].json);
        });
    }

    [Fact]
    public void TrayContext_ConfirmationForegroundDenied_TrayMenuStillResolves()
    {
        RunOnSta(() =>
        {
            var audit = new CaptureAuditLogger();
            var engine = new RecordingEngine(audit);
            var activator = new FakeConfirmationActivator
            {
                SetForegroundResult = false,
                BringToTopResult = true
            };
            using var ctx = CreateTrayContext(engine, audit, confirmationActivator: activator);

            var approved = false;
            var summary = new
            {
                source = "region: test",
                capture_bounds = new { x = 100, y = 200, width = 1280, height = 720 },
                source_type = "region",
                source_title = "test",
                audio = "No audio",
                duration = "30s",
                output = "out.mp4",
                nested_role = "none",
                recording_id = "rec_tray",
                confirmation_id = "conf_tray",
                timeout_seconds = 60,
                expires_at = "2026-01-01T00:00:00Z"
            };

            ctx.RequestConfirmation(summary, decision => { approved = decision.Approved; });
            Application.DoEvents();
            Thread.Sleep(50);
            Application.DoEvents();

            Assert.True(activator.SetTopMostCalled);
            Assert.True(activator.SetForegroundCalled);
            Assert.True(activator.BringToTopCalled);

            var approveFromMenu = ctx.GetType().GetMethod("ApproveFromMenu", BindingFlags.NonPublic | BindingFlags.Instance);
            approveFromMenu!.Invoke(ctx, Array.Empty<object>());
            Application.DoEvents();
            Thread.Sleep(50);
            Application.DoEvents();

            Assert.True(approved);

            var approvedFromMenu = audit.Events.Where(e => e.evt == "confirmation.approved_from_menu").ToList();
            Assert.Single(approvedFromMenu);

            var formClosed = audit.Events.LastOrDefault(e => e.evt == "confirmation.form_closed");
            Assert.NotEqual(default, formClosed);
            Assert.Contains("\"close_reason\":\"queue_advanced\"", formClosed.json);
        });
    }

    [Fact]
    public void TrayContext_uiInvoker_IsInvisibleAndZeroSize()
    {
        RunOnSta(() =>
        {
            var audit = new CaptureAuditLogger();
            var engine = new RecordingEngine(audit);
            using var ctx = CreateTrayContext(engine, audit);

            var invoker = GetPrivateField<Control>(ctx, "_uiInvoker");
            Assert.False(invoker.Visible);
            Assert.Equal(0, invoker.Width);
            Assert.Equal(0, invoker.Height);
        });
    }
}
