using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
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
/// Tests for the recording indicator UI, its geometry helpers, lifecycle manager,
/// TrayContext integration, and the no-window process start settings used to
/// prevent blank helper windows.
/// </summary>
public class RecordingIndicatorTests
{
    private static readonly string FfmpegExe = Path.Combine(
        TestHelper.ProjectRoot, "tools", "ffmpeg", "bin", "ffmpeg.exe");

    private static void RunOnSta(Action action)
    {
        Exception? ex = null;
        var thread = new Thread(() =>
        {
            try
            {
                action();
            }
            catch (Exception e)
            {
                ex = e;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (ex != null)
            throw new TargetInvocationException(ex);
    }

    private static T RunOnSta<T>(Func<T> func)
    {
        T result = default!;
        Exception? ex = null;
        var thread = new Thread(() =>
        {
            try
            {
                result = func();
            }
            catch (Exception e)
            {
                ex = e;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (ex != null)
            throw new TargetInvocationException(ex);
        return result;
    }

    private static T GetPrivateField<T>(object obj, string fieldName)
    {
        var field = obj.GetType().GetField(fieldName, BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(field);
        return (T)field!.GetValue(obj)!;
    }

    private static Recording MakeRecording(
        (int x, int y, int w, int h) bounds,
        int? duration = null,
        string? nestedRole = null)
    {
        return new Recording
        {
            SourceType = "region",
            StartedAtUtc = DateTime.UtcNow,
            DurationSeconds = duration,
            NestedRole = nestedRole,
            Config = new CaptureConfig
            {
                SourceKind = "region",
                Bounds = bounds,
                OutputPath = Path.Combine(Path.GetTempPath(), $"test-indicator-{Guid.NewGuid():N}.mp4")
            }
        };
    }

    // =====================================================================
    // Geometry tests
    // =====================================================================

    [Fact]
    public void ClampToVirtualScreen_InsideBounds_ReturnsSame()
    {
        var vs = SystemInformation.VirtualScreen;
        var input = new RecordingIndicatorBounds(vs.X + 100, vs.Y + 100, 800, 600);

        var result = RecordingIndicatorGeometry.ClampToVirtualScreen(input);

        Assert.Equal(input.X, result.X);
        Assert.Equal(input.Y, result.Y);
        Assert.Equal(input.Width, result.Width);
        Assert.Equal(input.Height, result.Height);
    }

    [Fact]
    public void ClampToVirtualScreen_TinyBounds_EnforcesMinSize()
    {
        var input = new RecordingIndicatorBounds(0, 0, 1, 1);

        var result = RecordingIndicatorGeometry.ClampToVirtualScreen(input);

        Assert.True(result.Width >= RecordingIndicatorGeometry.MinIndicatorSize);
        Assert.True(result.Height >= RecordingIndicatorGeometry.MinIndicatorSize);
    }

    [Fact]
    public void ClampToVirtualScreen_PartiallyOutside_IsContained()
    {
        var vs = SystemInformation.VirtualScreen;
        var input = new RecordingIndicatorBounds(
            vs.X + vs.Width - 100,
            vs.Y + vs.Height - 100,
            200,
            200);

        var result = RecordingIndicatorGeometry.ClampToVirtualScreen(input);

        Assert.True(result.X >= vs.X);
        Assert.True(result.Y >= vs.Y);
        Assert.True(result.X + result.Width <= vs.X + vs.Width);
        Assert.True(result.Y + result.Height <= vs.Y + vs.Height);
    }

    [Fact]
    public void ClampToVirtualScreen_NegativeOrigin_ClampedToVirtualScreen()
    {
        var vs = SystemInformation.VirtualScreen;
        if (vs.X >= 0)
        {
            // Only meaningful when the virtual screen starts at or after 0.
            // Top-left is outside, but the rectangle still overlaps the virtual screen.
            var input = new RecordingIndicatorBounds(vs.X - 100, vs.Y - 100, 800, 600);
            var result = RecordingIndicatorGeometry.ClampToVirtualScreen(input);
            Assert.True(result.X >= vs.X);
            Assert.True(result.Y >= vs.Y);
            Assert.True(result.Width > 0);
            Assert.True(result.Height > 0);
        }
    }

    [Fact]
    public void ComputeLabelLocation_InsideBounds_ReturnsOffset()
    {
        var bounds = new RecordingIndicatorBounds(100, 100, 800, 600);
        var labelSize = new Size(80, 20);

        var result = RecordingIndicatorGeometry.ComputeLabelLocation(bounds, labelSize);

        Assert.Equal(bounds.X + RecordingIndicatorGeometry.BorderWidth + 2, result.X);
        Assert.Equal(bounds.Y + RecordingIndicatorGeometry.BorderWidth + 2, result.Y);
    }

    [Fact]
    public void ComputeLabelLocation_NearRightEdge_StaysInside()
    {
        var vs = SystemInformation.VirtualScreen;
        var bounds = new RecordingIndicatorBounds(
            vs.X + vs.Width - 120,
            vs.Y + 100,
            120,
            120);
        var labelSize = new Size(80, 20);

        var result = RecordingIndicatorGeometry.ComputeLabelLocation(bounds, labelSize);

        Assert.True(result.X >= bounds.X);
        Assert.True(result.X + labelSize.Width <= vs.X + vs.Width);
    }

    [Fact]
    public void TryClampToVirtualScreen_FullyRight_ReturnsNull()
    {
        var vs = SystemInformation.VirtualScreen;
        var input = new RecordingIndicatorBounds(vs.X + vs.Width + 10, vs.Y + 100, 100, 100);

        var result = RecordingIndicatorGeometry.TryClampToVirtualScreen(input);

        Assert.Null(result);
    }

    [Fact]
    public void TryClampToVirtualScreen_FullyLeft_ReturnsNull()
    {
        var vs = SystemInformation.VirtualScreen;
        var input = new RecordingIndicatorBounds(vs.X - 200, vs.Y + 100, 100, 100);

        var result = RecordingIndicatorGeometry.TryClampToVirtualScreen(input);

        Assert.Null(result);
    }

    [Fact]
    public void TryClampToVirtualScreen_FullyBelow_ReturnsNull()
    {
        var vs = SystemInformation.VirtualScreen;
        var input = new RecordingIndicatorBounds(vs.X + 100, vs.Y + vs.Height + 10, 100, 100);

        var result = RecordingIndicatorGeometry.TryClampToVirtualScreen(input);

        Assert.Null(result);
    }

    [Fact]
    public void TryClampToVirtualScreen_FullyAbove_ReturnsNull()
    {
        var vs = SystemInformation.VirtualScreen;
        var input = new RecordingIndicatorBounds(vs.X + 100, vs.Y - 200, 100, 100);

        var result = RecordingIndicatorGeometry.TryClampToVirtualScreen(input);

        Assert.Null(result);
    }

    [Theory]
    [InlineData(0, 100)]
    [InlineData(100, 0)]
    [InlineData(-1, 100)]
    [InlineData(100, -1)]
    public void TryClampToVirtualScreen_ZeroOrNegativeSize_ReturnsNull(int width, int height)
    {
        var input = new RecordingIndicatorBounds(100, 100, width, height);

        var result = RecordingIndicatorGeometry.TryClampToVirtualScreen(input);

        Assert.Null(result);
    }

    [Fact]
    public void TryClampToVirtualScreen_TinyIntersection_ExpandsToMinSize()
    {
        var vs = SystemInformation.VirtualScreen;
        // A 1x1 pixel that touches the virtual screen should expand to the min size.
        var input = new RecordingIndicatorBounds(vs.X + vs.Width - 1, vs.Y + vs.Height - 1, 1, 1);

        var result = RecordingIndicatorGeometry.TryClampToVirtualScreen(input);

        Assert.NotNull(result);
        Assert.True(result!.Width >= RecordingIndicatorGeometry.MinIndicatorSize || result.Width == vs.Width);
        Assert.True(result.Height >= RecordingIndicatorGeometry.MinIndicatorSize || result.Height == vs.Height);
        Assert.True(result.X >= vs.X);
        Assert.True(result.Y >= vs.Y);
        Assert.True(result.X + result.Width <= vs.X + vs.Width);
        Assert.True(result.Y + result.Height <= vs.Y + vs.Height);
    }

    [Fact]
    public void TryClampToVirtualScreen_InsideBounds_ReturnsPositiveSizeWithinVirtualScreen()
    {
        var vs = SystemInformation.VirtualScreen;
        var input = new RecordingIndicatorBounds(vs.X + 10, vs.Y + 10, 100, 100);

        var result = RecordingIndicatorGeometry.TryClampToVirtualScreen(input);

        Assert.NotNull(result);
        Assert.True(result!.Width > 0);
        Assert.True(result.Height > 0);
        Assert.True(result.X >= vs.X);
        Assert.True(result.Y >= vs.Y);
        Assert.True(result.X + result.Width <= vs.X + vs.Width);
        Assert.True(result.Y + result.Height <= vs.Y + vs.Height);
    }

    [Fact]
    public void ClampToVirtualScreen_InvalidBounds_Throws()
    {
        var input = new RecordingIndicatorBounds(999999, 999999, 100, 100);
        Assert.Throws<ArgumentException>(() => RecordingIndicatorGeometry.ClampToVirtualScreen(input));
    }

    // =====================================================================
    // RecordingIndicatorForm property / behavior tests
    // =====================================================================

    [Fact]
    public void Form_HasExpectedWindowProperties()
    {
        RunOnSta(() =>
        {
            using var form = new RecordingIndicatorForm(
                "r1",
                new RecordingIndicatorBounds(100, 100, 800, 600),
                DateTime.UtcNow);

            Assert.False(form.ShowInTaskbar);
            Assert.True(form.TopMost);
            Assert.Equal(FormBorderStyle.None, form.FormBorderStyle);
            Assert.False(form.ControlBox);
            Assert.False(form.MaximizeBox);
            Assert.False(form.MinimizeBox);
            Assert.Equal("", form.Text);
            Assert.Equal(FormStartPosition.Manual, form.StartPosition);
            Assert.Equal(1.0, form.Opacity);

            var showWithoutActivation = typeof(Form)
                .GetProperty("ShowWithoutActivation", BindingFlags.Instance | BindingFlags.NonPublic)
                ?.GetValue(form) as bool?;
            Assert.True(showWithoutActivation.GetValueOrDefault());
        });
    }

    [Fact]
    public void Form_Bounds_AreClampedAndStored()
    {
        RunOnSta(() =>
        {
            var input = new RecordingIndicatorBounds(0, 0, 1, 1);
            using var form = new RecordingIndicatorForm("r1", input, DateTime.UtcNow);

            Assert.True(form.BoundsForTests.Width >= RecordingIndicatorGeometry.MinIndicatorSize);
            Assert.True(form.BoundsForTests.Height >= RecordingIndicatorGeometry.MinIndicatorSize);
        });
    }

    [Fact]
    public void Form_LabelText_NoDuration_ShowsRecAndElapsed()
    {
        RunOnSta(() =>
        {
            using var form = new RecordingIndicatorForm(
                "r1",
                new RecordingIndicatorBounds(100, 100, 800, 600),
                DateTime.UtcNow);
            Assert.Equal("REC 00:00", form.LabelTextForTests);
        });
    }

    [Fact]
    public void Form_LabelText_WithDuration_ShowsTotal()
    {
        RunOnSta(() =>
        {
            using var form = new RecordingIndicatorForm(
                "r1",
                new RecordingIndicatorBounds(100, 100, 800, 600),
                DateTime.UtcNow,
                30);
            Assert.Equal("REC 00:00 / 00:30", form.LabelTextForTests);
        });
    }

    [Fact]
    public void Form_LabelText_WithNestedRole_IncludesRole()
    {
        RunOnSta(() =>
        {
            using var form = new RecordingIndicatorForm(
                "r1",
                new RecordingIndicatorBounds(100, 100, 800, 600),
                DateTime.UtcNow,
                30,
                "outer");
            Assert.Equal("REC OUTER 00:00 / 00:30", form.LabelTextForTests);
        });
    }

    [Fact]
    public void Form_TimerStarts_OnShown()
    {
        RunOnSta(() =>
        {
            using var form = new RecordingIndicatorForm(
                "r1",
                new RecordingIndicatorBounds(100, 100, 800, 600),
                DateTime.UtcNow);
            Assert.False(form.TimerEnabledForTests);
            form.Show();
            Application.DoEvents();
            Thread.Sleep(50);
            Application.DoEvents();
            Assert.True(form.TimerEnabledForTests);
            form.Close();
        });
    }

    [Fact]
    public void Form_Dispose_StopsTimer()
    {
        RunOnSta(() =>
        {
            var form = new RecordingIndicatorForm(
                "r1",
                new RecordingIndicatorBounds(100, 100, 800, 600),
                DateTime.UtcNow);
            form.Show();
            form.Dispose();
            Assert.False(form.TimerEnabledForTests);
        });
    }

    [Fact]
    public void Form_ClientRectangle_HasNonEmptySize()
    {
        RunOnSta(() =>
        {
            using var form = new RecordingIndicatorForm(
                "r1",
                new RecordingIndicatorBounds(100, 100, 800, 600),
                DateTime.UtcNow);
            form.Show();
            Assert.True(form.ClientRectangle.Width >= RecordingIndicatorGeometry.MinIndicatorSize);
            Assert.True(form.ClientRectangle.Height >= RecordingIndicatorGeometry.MinIndicatorSize);
            form.Close();
        });
    }

    // =====================================================================
    // RecordingIndicatorManager lifecycle tests
    // =====================================================================

    [Fact]
    public void Manager_ShowFor_CreatesIndicatorAndLogs()
    {
        RunOnSta(() =>
        {
            var audit = new CaptureAuditLogger();
            var mgr = new RecordingIndicatorManager(audit);
            var rec = MakeRecording((100, 100, 800, 600), 30);

            mgr.ShowFor(rec);

            Assert.Single(mgr.IndicatorsForTests);
            Assert.Contains(audit.Events, e => e.evt == "recording_indicator.shown");

            mgr.CloseAll("test");
        });
    }

    [Fact]
    public void Manager_ShowFor_EmptyBounds_SkipsAndLogs()
    {
        RunOnSta(() =>
        {
            var audit = new CaptureAuditLogger();
            var mgr = new RecordingIndicatorManager(audit);
            var rec = MakeRecording((0, 0, 0, 0), 30);

            mgr.ShowFor(rec);

            Assert.Empty(mgr.IndicatorsForTests);
            var skipped = Assert.Single(audit.Events, e => e.evt == "recording_indicator.skipped");
            Assert.Contains("\"reason\":\"invalid_bounds\"", skipped.json);
        });
    }

    [Fact]
    public void Manager_ShowFor_Duplicate_ReplacesIndicator()
    {
        RunOnSta(() =>
        {
            var audit = new CaptureAuditLogger();
            var mgr = new RecordingIndicatorManager(audit);
            var rec = MakeRecording((100, 100, 800, 600), 30);

            mgr.ShowFor(rec);
            var first = mgr.IndicatorsForTests[rec.Id];

            mgr.ShowFor(rec);
            var second = mgr.IndicatorsForTests[rec.Id];

            Assert.NotSame(first, second);
            Assert.Contains(audit.Events, e => e.evt == "recording_indicator.closed");

            mgr.CloseAll("test");
        });
    }

    [Fact]
    public void Manager_CloseFor_ClosesIndicatorAndLogs()
    {
        RunOnSta(() =>
        {
            var audit = new CaptureAuditLogger();
            var mgr = new RecordingIndicatorManager(audit);
            var rec = MakeRecording((100, 100, 800, 600), 30);

            mgr.ShowFor(rec);
            mgr.CloseFor(rec.Id, "test.close");

            Assert.Empty(mgr.IndicatorsForTests);
            Assert.Contains(audit.Events, e => e.evt == "recording_indicator.closed");
        });
    }

    [Fact]
    public void Manager_CloseAll_ClosesAllIndicators()
    {
        RunOnSta(() =>
        {
            var audit = new CaptureAuditLogger();
            var mgr = new RecordingIndicatorManager(audit);
            var r1 = MakeRecording((100, 100, 800, 600), 30);
            var r2 = MakeRecording((500, 500, 640, 480), 60);

            mgr.ShowFor(r1);
            mgr.ShowFor(r2);
            Assert.Equal(2, mgr.IndicatorsForTests.Count);

            mgr.CloseAll("test.all");
            Assert.Empty(mgr.IndicatorsForTests);
        });
    }

    [Fact]
    public void Manager_NestedTwoRecordings_NoLeak()
    {
        RunOnSta(() =>
        {
            var audit = new CaptureAuditLogger();
            var mgr = new RecordingIndicatorManager(audit);
            var outer = MakeRecording((0, 0, 1920, 1080), 60, "outer");
            var inner = MakeRecording((100, 100, 640, 480), 30, "inner");

            mgr.ShowFor(outer);
            mgr.ShowFor(inner);
            Assert.Equal(2, mgr.IndicatorsForTests.Count);

            mgr.CloseFor(inner.Id, "inner.done");
            Assert.Single(mgr.IndicatorsForTests);

            mgr.CloseFor(outer.Id, "outer.done");
            Assert.Empty(mgr.IndicatorsForTests);
        });
    }

    [Fact]
    public void Manager_TextProviderFactory_UsesCurrentProviderForEachNewStopControl()
    {
        RunOnSta(() =>
        {
            var audit = new CaptureAuditLogger();
            IUiTextProvider currentProvider = new UiTextProvider(UiLanguage.ZhCn);
            var mgr = new RecordingIndicatorManager(audit, _ => { }, () => currentProvider);

            var rec1 = MakeRecording((100, 100, 800, 600), 30);
            mgr.ShowFor(rec1);

            var first = mgr.StopControlsForTests[rec1.Id];
            Assert.Contains("停止", first.ButtonTextForTests);
            Assert.Contains("停止本次录制", first.TooltipTextForTests);

            // Simulate a language switch: the factory now returns the English provider.
            currentProvider = new UiTextProvider(UiLanguage.EnUs);

            var rec2 = MakeRecording((500, 500, 640, 480), 60);
            mgr.ShowFor(rec2);

            var second = mgr.StopControlsForTests[rec2.Id];
            Assert.Contains("Stop", second.ButtonTextForTests);
            Assert.Contains("Stop this recording", second.TooltipTextForTests);

            // The first stop control must keep its original language.
            Assert.Contains("停止", first.ButtonTextForTests);

            mgr.CloseAll("test");
        });
    }

    [Fact]
    public void Manager_ShowFor_FullyOutsideVirtualScreen_SkipsAndLogs()
    {
        RunOnSta(() =>
        {
            var audit = new CaptureAuditLogger();
            var mgr = new RecordingIndicatorManager(audit);
            var vs = SystemInformation.VirtualScreen;
            var rec = MakeRecording((vs.X + vs.Width + 10, vs.Y + 100, 100, 100), 30);

            mgr.ShowFor(rec);

            Assert.Empty(mgr.IndicatorsForTests);
            var skipped = Assert.Single(audit.Events, e => e.evt == "recording_indicator.skipped");
            Assert.Contains("\"reason\":\"outside_virtual_screen\"", skipped.json);
        });
    }

    [Fact]
    public void Manager_ShowFor_NegativeBounds_SkipsAndLogsInvalidBounds()
    {
        RunOnSta(() =>
        {
            var audit = new CaptureAuditLogger();
            var mgr = new RecordingIndicatorManager(audit);
            var rec = MakeRecording((100, 100, -50, 100), 30);

            mgr.ShowFor(rec);

            Assert.Empty(mgr.IndicatorsForTests);
            var skipped = Assert.Single(audit.Events, e => e.evt == "recording_indicator.skipped");
            Assert.Contains("\"reason\":\"invalid_bounds\"", skipped.json);
        });
    }

    [Fact]
    public void Manager_ShowFor_ShowException_DisposesFormAndLogs()
    {
        RunOnSta(() =>
        {
            var audit = new CaptureAuditLogger();
            var disposed = false;

            RecordingIndicatorForm Factory(string id, RecordingIndicatorBounds bounds, DateTime started, int? duration, string? role)
            {
                var form = new RecordingIndicatorForm(id, bounds, started, duration, role);
                form.Disposed += (_, _) => disposed = true;
                return form;
            }

            // Use a factory that throws on Show by creating and returning a form, then causing Show to fail.
            // We simulate the failure by disposing the form before Show is called; Show on a disposed form throws.
            RecordingIndicatorForm ThrowingFactory(string id, RecordingIndicatorBounds bounds, DateTime started, int? duration, string? role)
            {
                var form = Factory(id, bounds, started, duration, role);
                form.Dispose();
                return form;
            }

            var mgr = new RecordingIndicatorManager(audit, ThrowingFactory);
            var rec = MakeRecording((100, 100, 800, 600), 30);

            mgr.ShowFor(rec);

            Assert.Empty(mgr.IndicatorsForTests);
            Assert.Contains(audit.Events, e => e.evt == "recording_indicator.show_error");
            Assert.True(disposed);
        });
    }

    // =====================================================================
    // TrayContext integration tests
    // =====================================================================

    [Fact]
    public void TrayContext_UiInvoker_IsHiddenZeroSize()
    {
        RunOnSta(() =>
        {
            var audit = new CaptureAuditLogger();
            var engine = new RecordingEngine(audit);
            using var ctx = new TrayContext(engine, audit, FakeGlobalStopHotkeyFactory.Create());

            var invoker = GetPrivateField<Control>(ctx, "_uiInvoker");
            Assert.False(invoker.Visible);
            Assert.Equal(0, invoker.Width);
            Assert.Equal(0, invoker.Height);
        });
    }

    [Fact]
    public void TrayContext_SetRecording_CreatesIndicator_SetIdle_ClosesIt()
    {
        RunOnSta(() =>
        {
            var audit = new CaptureAuditLogger();
            var engine = new RecordingEngine(audit);
            using var ctx = new TrayContext(engine, audit, FakeGlobalStopHotkeyFactory.Create());
            var rec = MakeRecording((100, 100, 800, 600), 30);

            ctx.SetRecording(rec);
            var mgr = GetPrivateField<RecordingIndicatorManager>(ctx, "_indicatorManager");
            Assert.Single(mgr.IndicatorsForTests);

            ctx.SetIdle(rec);
            Assert.Empty(mgr.IndicatorsForTests);
        });
    }

    [Fact]
    public void TrayContext_SetAllIdle_ClosesAllIndicators()
    {
        RunOnSta(() =>
        {
            var audit = new CaptureAuditLogger();
            var engine = new RecordingEngine(audit);
            using var ctx = new TrayContext(engine, audit, FakeGlobalStopHotkeyFactory.Create());
            var r1 = MakeRecording((100, 100, 800, 600), 30);
            var r2 = MakeRecording((500, 500, 640, 480), 60);

            ctx.SetRecording(r1);
            ctx.SetRecording(r2);
            var mgr = GetPrivateField<RecordingIndicatorManager>(ctx, "_indicatorManager");
            Assert.Equal(2, mgr.IndicatorsForTests.Count);

            ctx.SetAllIdle();
            Assert.Empty(mgr.IndicatorsForTests);
        });
    }

    // =====================================================================
    // No-window helper process tests
    // =====================================================================

    [Fact]
    public void FfmpegCaptureBackend_Start_HasNoWindowSemantics()
    {
        if (!File.Exists(FfmpegExe))
            return; // skip if FFmpeg not present

        var backend = new FfmpegCaptureBackend();
        var output = Path.Combine(Path.GetTempPath(), $"test-indicator-ffmpeg-{Guid.NewGuid():N}.mp4");
        var cfg = new CaptureConfig
        {
            SourceKind = "region",
            Bounds = (0, 0, 320, 240),
            Fps = 15,
            DurationSeconds = 1,
            OutputPath = output
        };

        try
        {
            backend.Start(cfg);

            var procField = typeof(FfmpegCaptureBackend).GetField(
                "_proc", BindingFlags.NonPublic | BindingFlags.Instance);
            var proc = procField?.GetValue(backend) as Process;
            Assert.NotNull(proc);

            Assert.False(proc!.StartInfo.UseShellExecute);
            Assert.True(proc.StartInfo.CreateNoWindow);
            Assert.Equal(ProcessWindowStyle.Hidden, proc.StartInfo.WindowStyle);
            Assert.False(proc.StartInfo.ErrorDialog);

            backend.Stop();
        }
        finally
        {
            try { backend.Dispose(); } catch { }
            try { File.Delete(output); } catch { }
        }
    }

    // =====================================================================
    // DPI-safe REC label sizing and placement
    // =====================================================================

    [Theory]
    [InlineData(1.00, null, null, "REC 2:00:00")]
    [InlineData(1.00, null, 30, "REC 00:30 / 00:30")]
    [InlineData(1.00, "outer", 60, "REC OUTER 01:00 / 01:00")]
    [InlineData(1.00, "inner", 30, "REC INNER 00:30 / 00:30")]
    [InlineData(1.00, null, 3660, "REC 1:01:00 / 1:01:00")]
    [InlineData(1.25, null, 30, "REC 00:30 / 00:30")]
    [InlineData(1.50, "outer", 60, "REC OUTER 01:00 / 01:00")]
    [InlineData(1.75, "inner", 30, "REC INNER 00:30 / 00:30")]
    [InlineData(2.00, null, 3660, "REC 1:01:00 / 1:01:00")]
    public void Label_MeasureLabelSize_DpiMatrix_FitsLongestText(double scale, string? role, int? duration, string expectedMaxText)
    {
        RunOnSta(() =>
        {
            var font = new Font("Segoe UI", (float)(9 * scale), FontStyle.Bold);
            var padding = new Padding(4, 2, 4, 2);
            var size = RecordingIndicatorForm.MeasureLabelSize(role, duration, font, padding);

            var textSize = TextRenderer.MeasureText(expectedMaxText, font, Size.Empty, TextFormatFlags.SingleLine);
            Assert.True(size.Width >= textSize.Width + padding.Horizontal,
                $"Measured width {size.Width} does not fit text width {textSize.Width} at scale {scale}");
            Assert.True(size.Height >= textSize.Height + padding.Vertical,
                $"Measured height {size.Height} does not fit text height {textSize.Height} at scale {scale}");
        });
    }

    [Fact]
    public void Label_BoundsStable_AfterTimerUpdate()
    {
        RunOnSta(() =>
        {
            using var form = new RecordingIndicatorForm(
                "r1",
                new RecordingIndicatorBounds(100, 100, 800, 600),
                DateTime.UtcNow.AddSeconds(-5),
                30);
            form.Show();
            Application.DoEvents();

            var initialBounds = form.LabelBoundsForTests;
            var measuredSize = form.LabelMeasuredSizeForTests;

            Assert.True(initialBounds.Width >= measuredSize.Width,
                $"Label width {initialBounds.Width} smaller than measured {measuredSize.Width}");
            Assert.True(initialBounds.Height >= measuredSize.Height,
                $"Label height {initialBounds.Height} smaller than measured {measuredSize.Height}");

            // Wait for at least one timer tick.
            Thread.Sleep(700);
            Application.DoEvents();

            var updatedBounds = form.LabelBoundsForTests;
            Assert.Equal(initialBounds.Size, updatedBounds.Size);
            Assert.Equal(initialBounds.Location, updatedBounds.Location);
            Assert.NotEqual("REC 00:00 / 00:30", form.LabelTextForTests);

            form.Close();
        });
    }

    [Fact]
    public void Label_NearVirtualScreenEdges_StaysInside()
    {
        RunOnSta(() =>
        {
            var vs = SystemInformation.VirtualScreen;

            using var formRight = new RecordingIndicatorForm(
                "r1",
                new RecordingIndicatorBounds(vs.Right - 200, vs.Y + 100, 200, 200),
                DateTime.UtcNow,
                30);
            formRight.Show();
            var labelRight = formRight.LabelBoundsForTests;
            Assert.True(formRight.Bounds.X + labelRight.Right <= vs.Right,
                $"Label overflows right edge: right={formRight.Bounds.X + labelRight.Right}, vs.Right={vs.Right}");
            Assert.True(formRight.Bounds.Y + labelRight.Bottom <= vs.Bottom,
                $"Label overflows bottom edge: bottom={formRight.Bounds.Y + labelRight.Bottom}, vs.Bottom={vs.Bottom}");
            formRight.Close();

            using var formBottom = new RecordingIndicatorForm(
                "r2",
                new RecordingIndicatorBounds(vs.X + 100, vs.Bottom - 200, 200, 200),
                DateTime.UtcNow,
                30);
            formBottom.Show();
            var labelBottom = formBottom.LabelBoundsForTests;
            Assert.True(formBottom.Bounds.X + labelBottom.Right <= vs.Right);
            Assert.True(formBottom.Bounds.Y + labelBottom.Bottom <= vs.Bottom);
            formBottom.Close();
        });
    }

    // =====================================================================
    // Long timer formatting and measurement consistency
    // =====================================================================

    [Theory]
    [InlineData(0, "00:00")]
    [InlineData(3599, "59:59")]
    [InlineData(3600, "1:00:00")]
    [InlineData(3660, "1:01:00")]
    [InlineData(7200, "2:00:00")]
    public void FormatTime_MapsSecondsToExpectedString(int seconds, string expected)
    {
        Assert.Equal(expected, RecordingIndicatorForm.FormatTime(TimeSpan.FromSeconds(seconds)));
    }

    [Fact]
    public void Form_LabelText_WithLongDuration_ShowsHours()
    {
        RunOnSta(() =>
        {
            using var form = new RecordingIndicatorForm(
                "r1",
                new RecordingIndicatorBounds(100, 100, 800, 600),
                DateTime.UtcNow,
                3660);
            Assert.Equal("REC 00:00 / 1:01:00", form.LabelTextForTests);
        });
    }

    [Fact]
    public void Label_BoundsStable_CrossingHourBoundary()
    {
        RunOnSta(() =>
        {
            // Start one second before the one-hour mark so the next timer tick crosses it.
            using var form = new RecordingIndicatorForm(
                "r1",
                new RecordingIndicatorBounds(100, 100, 800, 600),
                DateTime.UtcNow.AddSeconds(-3599),
                7200);
            form.Show();
            Application.DoEvents();
            Thread.Sleep(50);

            var before = form.LabelBoundsForTests;
            var beforeText = form.LabelTextForTests;
            Assert.Contains("59:59", beforeText);

            // Wait long enough for the label to update past the 1:00:00 boundary.
            Thread.Sleep(1200);
            Application.DoEvents();
            Thread.Sleep(50);
            Application.DoEvents();

            var after = form.LabelBoundsForTests;
            var afterText = form.LabelTextForTests;
            Assert.Contains("1:00:00", afterText);

            Assert.Equal(before.Size, after.Size);
            form.Close();
        });
    }
}
