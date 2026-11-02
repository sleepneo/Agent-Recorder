using System.Drawing;
using System.Reflection;
using System.Windows.Forms;
using AgentRecorder.App;
using AgentRecorder.Infrastructure;
using AgentRecorder.Windows;
using Xunit;

namespace AgentRecorder.Tests;

[Collection("NonParallel-AgentRecorderDataDir")]
public sealed partial class IndependentWindow382UiTests : IDisposable
{
    private readonly TempDirectory temp = new();
    public IndependentWindow382UiTests() => DataDirResolver.SetOverride(temp.Path);
    public void Dispose() { DataDirResolver.ClearOverride(); temp.Dispose(); }

    private static void Sta(Action work)
    {
        Exception? error = null;
        var thread = new Thread(() => { try { work(); } catch (Exception e) { error = e; } });
        thread.SetApartmentState(ApartmentState.STA); thread.Start(); thread.Join();
        if (error is not null) throw new TargetInvocationException(error);
    }
    private sealed class SyntheticPreview : IScreenPreviewProvider
    {
        internal int Calls;
        public Bitmap Capture(ConfirmationCaptureBounds b, Size max) { Calls++; return new Bitmap(64, 64); }
    }
    private sealed class NoDwm : IDwmThumbnailProvider
    { public bool TryRegister(nint destination, nint source, out IDwmThumbnail thumbnail) { thumbnail = null!; return false; } }
    private sealed class Theme : IConfirmationThemeProvider
    {
        public event EventHandler? ThemeChanged { add { } remove { } }
        public ConfirmationThemeSnapshot Resolve() => new(ConfirmationThemeKind.Light, ConfirmationThemePalette.Light);
        public void Dispose() { }
    }
    private sealed class Chrome : IConfirmationNativeChromeAdapter
    { public bool Apply(nint hwnd, ConfirmationThemeKind theme) => true; public void Dispose() { } }
    private sealed class Scroll : IConfirmationScrollThemeAdapter
    { public bool Apply(Control host, ConfirmationThemeKind theme) => true; public void Dispose() { } }
    private sealed class Affinity : IWindowDisplayAffinity
    { public bool SetExcludeFromCapture(nint hwnd) => true; public bool GetAffinity(nint hwnd, out uint value) { value = 0x11; return true; } }
    private static IEnumerable<string> Texts(Control root)
    { yield return root.Text; foreach (Control child in root.Controls) foreach (var text in Texts(child)) yield return text; }

    [Theory][InlineData(UiLanguage.ZhCn)][InlineData(UiLanguage.EnUs)]
    public void IndependentWindow382HiddenConfirmationShowsWindowNumberAndFullTargetTitle(UiLanguage language)
    {
        Sta(() => {
            var text = new UiTextProvider(language); var window = new WindowRecordingIdentity(2, "合成画图 Paint & target — 标题与编号");
            var presentation = new RecordingConfirmationPresentation {
                RecordingId = "rec-window-two", ConfirmationId = "confirm-window-two", WindowIdentity = window,
                SourceType = "window", SourceTitle = window.Title, WindowId = "window_43", CaptureSemantics = "window_surface",
                PreviewSemantics = "window_surface", CaptureBounds = new(0, 0, 1200, 900), TimeoutSeconds = 60,
                CreatedAtUtc = DateTime.UtcNow, ExpiresAtUtc = DateTime.UtcNow.AddSeconds(60),
                Summary = new() { Source = "window: " + window.Title, Output = Path.Combine(temp.Path, "video.mp4"), Duration = "20s", NestedRole = "none" } };
            var preview = new SyntheticPreview();
            using var form = new ConfirmationForm(new(presentation, _ => { }), 1, 2, previewProvider: preview,
                textProvider: text, dwmThumbnailProvider: new NoDwm(), themeProvider: new Theme(),
                nativeChromeAdapter: new Chrome(), scrollThemeAdapter: new Scroll());
            Assert.Contains(WindowRecordingText.Format(text, window), Texts(form));
            Assert.False(form.Visible); Assert.True(form.WindowSurfacePreviewForTests); Assert.Equal(0, preview.Calls);
            Assert.DoesNotContain("OUTER", WindowRecordingText.Format(text, window));
            Assert.DoesNotContain("INNER", WindowRecordingText.Format(text, window));
        });
    }

    [Theory][InlineData(UiLanguage.ZhCn)][InlineData(UiLanguage.EnUs)]
    public void IndependentWindow382HiddenStopControlsAndRecKeepExactIdsAndNumbering(UiLanguage language)
    {
        Sta(() => {
            var text = new UiTextProvider(language); var windowA = new WindowRecordingIdentity(1, "合成记事本 Notepad");
            var windowB = new WindowRecordingIdentity(2, "合成画图 Paint"); var clicked = new List<string>();
            using var a = new RecordingStopControlForm("rec-one", new(100, 100, 200, 40), CaptureVisibilityMode.ExcludeFromCapture,
                null, text, new Affinity());
            using var b = new RecordingStopControlForm("rec-two", new(100, 150, 200, 40), CaptureVisibilityMode.ExcludeFromCapture,
                null, text, new Affinity());
            a.SetWindowIdentity(windowA); b.SetWindowIdentity(windowB);
            a.StopClicked += clicked.Add; b.StopClicked += clicked.Add;
            Assert.Contains(WindowRecordingText.Format(text, windowA, compact: true), a.ButtonTextForTests);
            Assert.Contains(WindowRecordingText.Format(text, windowB, compact: true), b.ButtonTextForTests);
            Assert.Contains(windowB.Title, b.TooltipTextForTests);
            var click = typeof(RecordingStopControlForm).GetMethod("OnButtonClick", BindingFlags.NonPublic | BindingFlags.Instance)!;
            click.Invoke(a, new object?[] { null, EventArgs.Empty }); click.Invoke(a, new object?[] { null, EventArgs.Empty });
            Assert.Equal(new[] { "rec-one" }, clicked); Assert.True(b.ButtonEnabledForTests);
            click.Invoke(b, new object?[] { null, EventArgs.Empty }); Assert.Equal(new[] { "rec-one", "rec-two" }, clicked);
            Assert.Throws<InvalidOperationException>(() => a.SetWindowIdentity(windowB));
            var bounds = new RecordingIndicatorBounds(100,100,1200,900);
            var presentation = new RecordingIndicatorPresentation(CaptureVisibilityMode.ExcludeFromCapture, bounds, bounds,
                null, Array.Empty<Rectangle>(), new(100,100,0,0), true, null);
            using var indicator = new RecordingIndicatorForm("rec-one", presentation, DateTime.UtcNow, textProviderFactory: () => text,
                displayAffinity: new Affinity());
            indicator.SetWindowIdentity(windowA); indicator.UpdateLabelForTests(TimeSpan.FromSeconds(4));
            Assert.Contains("1", indicator.LabelTextForTests); Assert.Contains(windowA.Title, indicator.LabelTextForTests);
            indicator.SetPhase(RecordingIndicatorPhase.Finalizing);
            Assert.Contains(WindowRecordingText.Format(text, windowA, compact: true), indicator.LabelTextForTests);
            Assert.False(a.Visible); Assert.False(b.Visible); Assert.False(indicator.Visible);
        });
    }

    [Theory]
    [InlineData(64, 96)][InlineData(120, 144)]
    public void IndependentWindow382NarrowOverlappingWindowsKeepBothMeasuredLabelsReadable(int width, int dpi)
    {
        Sta(() => {
            var text = new UiTextProvider(UiLanguage.ZhCn);
            var manager = new RecordingIndicatorManager(new RetentionSharedOutputPathTests.QuietAudit(), _ => { }, text);
            var display = new DisplayDpiInfo("synthetic", new(0,0,3840,2160), dpi,dpi,dpi/96f,false,null);
            var rec = new RecordingUiPresentation { RecordingId = "rec-one", State = RecordingUiState.Recording,
                SourceType = "window", CaptureBounds = new(100,100,width,64), DurationSeconds = 120,
                WindowIdentity = new(1,"合成窗口标题 Paint & Notepad") };
            var a = manager.ComputeControlPlan(rec, null, display.MonitorBounds, display)!;
            var frame = a.IndicatorPresentation.WindowBounds;
            Assert.True(new Rectangle(frame.X,frame.Y,frame.Width,frame.Height).Contains(a.IndicatorPresentation.LabelBounds));
            Assert.Equal(new RecordingIndicatorBounds(100,100,width,64), a.IndicatorPresentation.InnerCaptureBounds);
            using var indicator = new RecordingIndicatorForm(rec.RecordingId, a.IndicatorPresentation, DateTime.UtcNow,
                durationSeconds:120, displayAffinity:new Affinity(),textProviderFactory:()=>text);
            indicator.SetWindowIdentity(rec.WindowIdentity);
            using var stop = new RecordingStopControlForm(rec.RecordingId,a.StopBounds,a.StopControlSize,display,
                CaptureVisibilityMode.ExcludeFromCapture,null,text,new Affinity());
            var indicators = (Dictionary<string,RecordingIndicatorForm>)typeof(RecordingIndicatorManager).GetField("_indicators",BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(manager)!;
            var stops = (Dictionary<string,RecordingStopControlForm>)typeof(RecordingIndicatorManager).GetField("_stopControls",BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(manager)!;
            indicators.Add(rec.RecordingId,indicator);stops.Add(rec.RecordingId,stop);
            try
            {
                var b = manager.ComputeControlPlan(rec with {RecordingId="rec-two",WindowIdentity=rec.WindowIdentity! with {Number=2}},null,display.MonitorBounds,display)!;
                Assert.False(a.IndicatorPresentation.LabelBounds.IntersectsWith(b.IndicatorPresentation.LabelBounds));
                foreach(var p in new[]{a,b})
                    Assert.False(new Rectangle(p.StopBounds.X,p.StopBounds.Y,p.StopBounds.Width,p.StopBounds.Height).IntersectsWith(p.IndicatorPresentation.LabelBounds));
                Assert.Equal(CaptureVisibilityMode.ExcludeFromCapture,b.IndicatorPresentation.Mode);
                Assert.False(indicator.Visible);Assert.False(stop.Visible);
            }
            finally {indicators.Clear();stops.Clear();}
        });
    }

    [Fact]
    public void IndependentWindow382PulseInvalidatesFrozenFrameNotExpandedBadgeWindow()
    {
        Sta(() => {
            var text = new UiTextProvider(UiLanguage.EnUs);
            var manager = new RecordingIndicatorManager(new RetentionSharedOutputPathTests.QuietAudit(), _ => { }, text);
            var monitor = new DisplayDpiInfo("synthetic",new(0,0,3840,2160),96,96,1,false,null);
            var record = new RecordingUiPresentation {RecordingId="rec-pulse",State=RecordingUiState.Recording,SourceType="window",
                CaptureBounds=new(100,100,64,64),DurationSeconds=120,WindowIdentity=new(1,"Synthetic narrow window")};
            var p = manager.ComputeControlPlan(record,null,monitor.MonitorBounds,monitor)!.IndicatorPresentation;
            Assert.True(p.WindowBounds.Width>64);
            var dirty = RecordingIndicatorMotion.ComputeDirtyRegions(p,new(0,0,p.WindowBounds.Width,p.WindowBounds.Height),Rectangle.Empty,true,false);
            Assert.Contains(dirty,r=>r.X==100+64-RecordingIndicatorGeometry.BorderWidth-p.WindowBounds.X && r.Width==RecordingIndicatorGeometry.BorderWidth);
            Assert.DoesNotContain(dirty,r=>r.Right>100+64-p.WindowBounds.X);
        });
    }

    [Theory]
    [InlineData(UiLanguage.ZhCn, 96)][InlineData(UiLanguage.EnUs, 96)]
    [InlineData(UiLanguage.ZhCn, 120)][InlineData(UiLanguage.EnUs, 120)]
    [InlineData(UiLanguage.ZhCn, 144)][InlineData(UiLanguage.EnUs, 144)]
    [InlineData(UiLanguage.ZhCn, 192)][InlineData(UiLanguage.EnUs, 192)]
    public void IndependentWindow382LocalizedTextIsMeasuredAndOverlapStopsStaySeparate(UiLanguage language, int dpi)
    {
        Sta(() => {
            var text = new UiTextProvider(language); var window = new WindowRecordingIdentity(12, "合成长标题 Paint 🪟 é — 需要在控件内省略但确认和提示保留全文");
            using var font = new Font("Segoe UI", 8, FontStyle.Bold);
            var size = RecordingStopControlLayout.MeasurePreferredSize(text, font, new Rectangle(0,0,3840,2160), dpi, null, window);
            var phrase = RecordingStatusVisualModel.StopButtonText(text, null, true, window);
            var measured = TextRenderer.MeasureText(phrase, font, Size.Empty, TextFormatFlags.SingleLine);
            Assert.True(size.Width >= measured.Width * dpi / 96f);
            Assert.Contains(language == UiLanguage.ZhCn ? "窗口12" : "WIN12", phrase);
            Assert.Contains("…", phrase); Assert.Contains(window.Title, RecordingStatusVisualModel.StopTooltip(text, null, window));
            var manager = new RecordingIndicatorManager(new RetentionSharedOutputPathTests.QuietAudit(), _ => { }, text);
            var one = new RecordingUiPresentation { RecordingId = "rec-one", State = RecordingUiState.Recording, SourceType = "window",
                CaptureBounds = new(100,100,1200,900), DurationSeconds = 120, WindowIdentity = window };
            var monitor = new DisplayDpiInfo("synthetic", new(0,0,3840,2160), dpi, dpi, dpi / 96f, false, null);
            var first = manager.ComputeControlPlan(one, null, monitor.MonitorBounds, monitor)!;
            Assert.Equal(CaptureVisibilityMode.ExcludeFromCapture, first.IndicatorPresentation.Mode);
            Assert.True(first.IndicatorPresentation.DisplayAffinityRequested);
            using var control = new RecordingStopControlForm(one.RecordingId, first.StopBounds, first.StopControlSize, monitor,
                CaptureVisibilityMode.ExcludeFromCapture, null, text, new Affinity());
            var map = (Dictionary<string, RecordingStopControlForm>)typeof(RecordingIndicatorManager)
                .GetField("_stopControls", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(manager)!;
            map.Add(one.RecordingId, control);
            try
            {
                var second = manager.ComputeControlPlan(one with { RecordingId = "rec-two", WindowIdentity = window with { Number = 13 } }, null,
                    monitor.MonitorBounds, monitor)!;
                var a = first.StopBounds; var b = second.StopBounds;
                Assert.False(new Rectangle(a.X,a.Y,a.Width,a.Height).IntersectsWith(new(b.X,b.Y,b.Width,b.Height)));
                Assert.Equal(CaptureVisibilityMode.ExcludeFromCapture, second.IndicatorPresentation.Mode);
            }
            finally { map.Clear(); }
        });
    }
}
