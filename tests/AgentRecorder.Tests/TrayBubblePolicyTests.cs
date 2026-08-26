using System;
using System.Collections.Generic;
using System.Drawing;
using System.Reflection;
using System.Threading;
using System.Windows.Forms;
using AgentRecorder.App;
using AgentRecorder.Capture;
using AgentRecorder.Core;
using AgentRecorder.Infrastructure;
using AgentRecorder.Logging;
using Xunit;

namespace AgentRecorder.Tests;

/// <summary>
/// Tests for the tray balloon silence policy and its integration into <see cref="TrayContext"/>.
/// These tests must not pop real shell balloons, region windows, confirmation windows,
/// recording indicators or floating stop buttons.
/// </summary>
public class TrayBubblePolicyTests
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

    private static TrayContext CreateContext(UiLanguage language, out CaptureAuditLogger audit, ITrayBalloonTip? balloonTip = null)
    {
        audit = new CaptureAuditLogger();
        var engine = new RecordingEngine(audit);
        var ctx = new TrayContext(engine, audit, FakeGlobalStopHotkeyFactory.Create(), uiTextProvider: new UiTextProvider(language), balloonTip: balloonTip);
        engine.SetTray(ctx);
        return ctx;
    }

    private static Recording MakeRecording()
    {
        return new Recording
        {
            SourceType = "region",
            StartedAtUtc = DateTime.UtcNow,
            Config = new CaptureConfig
            {
                SourceKind = "region",
                Bounds = (100, 100, 800, 600),
                OutputPath = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"test-bubble-{Guid.NewGuid():N}.mp4")
            }
        };
    }

    private static void SetActiveRecordings(TrayContext ctx, IEnumerable<Recording> recordings)
    {
        var field = typeof(TrayContext).GetField("_activeRecordings", BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(field);
        var dict = (Dictionary<string, RecordingUiPresentation>)field!.GetValue(ctx)!;
        dict.Clear();
        foreach (var rec in recordings)
            dict[rec.Id] = RecordingUiPresentationTestData.FromRecording(rec);
    }

    private static void ShowBalloonTipIfAllowed(TrayContext ctx, BubbleType type, int timeout, string title, string body, ToolTipIcon icon)
    {
        var method = typeof(TrayContext).GetMethod("ShowBalloonTipIfAllowed", BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(method);
        method!.Invoke(ctx, new object[] { type, timeout, title, body, icon });
    }

    private static void SetPrivateField(object obj, string fieldName, object? value)
    {
        var field = obj.GetType().GetField(fieldName, BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(field);
        field!.SetValue(obj, value);
    }

    private static RecordingIndicatorManager CreateNoOpIndicatorManager()
    {
        var audit = new CaptureAuditLogger();
        return new RecordingIndicatorManager(
            audit,
            _ => { },
            (id, bounds, started, duration, role) =>
            {
                var form = new RecordingIndicatorForm(id, bounds, started, duration, role);
                form.Dispose();
                return form;
            },
            (id, bounds, size, dpi) =>
            {
                var form = new RecordingStopControlForm(id, bounds, size, dpi);
                form.Dispose();
                return form;
            });
    }

    [Theory]
    [InlineData(BubbleType.RecordingStarted, 0, false)]
    [InlineData(BubbleType.RecordingStarted, 1, false)]
    [InlineData(BubbleType.ConfirmationWaiting, 0, false)]
    [InlineData(BubbleType.ConfirmationWaiting, 1, false)]
    [InlineData(BubbleType.ConfirmationWaiting, 2, false)]
    [InlineData(BubbleType.Error, 0, true)]
    [InlineData(BubbleType.Error, 1, false)]
    [InlineData(BubbleType.Error, 5, false)]
    public void TrayBubblePolicy_TruthTable(BubbleType type, int activeCount, bool expected)
    {
        var policy = new TrayBubblePolicy();
        Assert.Equal(expected, policy.AllowShowBubble(type, activeCount));
    }

    [Fact]
    public void TrayContext_ConfirmationWaiting_NoActiveRecording_SuppressesBubble()
    {
        RunOnSta(() =>
        {
            var balloon = new FakeTrayBalloonTip();
            using var ctx = CreateContext(UiLanguage.EnUs, out _, balloon);

            ShowBalloonTipIfAllowed(ctx, BubbleType.ConfirmationWaiting, 5000,
                "Confirmation", "A confirmation is waiting.", ToolTipIcon.Warning);
            Application.DoEvents();

            Assert.Empty(balloon.Calls);
        });
    }

    [Fact]
    public void TrayContext_ConfirmationWaiting_ActiveRecording_SuppressesBubble()
    {
        RunOnSta(() =>
        {
            var balloon = new FakeTrayBalloonTip();
            using var ctx = CreateContext(UiLanguage.EnUs, out _, balloon);

            // Simulate active recording state without creating any real UI.
            SetActiveRecordings(ctx, new[] { MakeRecording() });

            ShowBalloonTipIfAllowed(ctx, BubbleType.ConfirmationWaiting, 5000,
                "Confirmation", "A confirmation is waiting.", ToolTipIcon.Warning);
            Application.DoEvents();

            Assert.Empty(balloon.Calls);
        });
    }

    [Fact]
    public void TrayContext_Error_ActiveRecording_SuppressesBubble()
    {
        RunOnSta(() =>
        {
            var balloon = new FakeTrayBalloonTip();
            using var ctx = CreateContext(UiLanguage.EnUs, out _, balloon);

            SetActiveRecordings(ctx, new[] { MakeRecording() });

            ctx.ShowError("something went wrong");
            Application.DoEvents();
            Thread.Sleep(50);
            Application.DoEvents();

            Assert.Empty(balloon.Calls);
        });
    }

    [Fact]
    public void TrayContext_Error_NoActiveRecording_ShowsBubble()
    {
        RunOnSta(() =>
        {
            var balloon = new FakeTrayBalloonTip();
            using var ctx = CreateContext(UiLanguage.EnUs, out _, balloon);

            ctx.ShowError("something went wrong");
            Application.DoEvents();
            Thread.Sleep(50);
            Application.DoEvents();

            Assert.Single(balloon.Calls);
        });
    }

    [Fact]
    public void TrayContext_SetRecording_NeverShowsStartedBubble()
    {
        RunOnSta(() =>
        {
            var balloon = new FakeTrayBalloonTip();
            using var ctx = CreateContext(UiLanguage.EnUs, out _, balloon);

            // Replace the real indicator manager with a no-op stub so SetRecording
            // exercises production code without showing REC borders or stop buttons.
            SetPrivateField(ctx, "_indicatorManager", CreateNoOpIndicatorManager());

            ctx.SetRecording(RecordingUiPresentationTestData.FromRecording(MakeRecording()));
            Application.DoEvents();
            Thread.Sleep(50);
            Application.DoEvents();

            Assert.Empty(balloon.Calls);
        });
    }

    private sealed class FakeTrayBalloonTip : ITrayBalloonTip
    {
        public System.Collections.Generic.List<(int Timeout, string Title, string Body, ToolTipIcon Icon)> Calls { get; } = new();

        public void ShowBalloonTip(int timeout, string title, string body, ToolTipIcon icon)
        {
            Calls.Add((timeout, title, body, icon));
        }
    }
}
