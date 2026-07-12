using System;
using System.Drawing;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Threading;
using System.Windows.Forms;
using Xunit.Abstractions;
using AgentRecorder.App;
using AgentRecorder.Capture;
using AgentRecorder.Core;
using AgentRecorder.Infrastructure;
using AgentRecorder.Logging;
using AgentRecorder.Windows;
using Xunit;

namespace AgentRecorder.Tests;

/// <summary>
/// Tests for the floating stop-control button, its geometry helpers, and manager wiring.
/// Uses the system-query display provider; must not run in parallel with other tests
/// that mutate the same provider.
/// </summary>
[Collection("NonParallel-SystemQueryProviders")]
public class RecordingStopControlTests : IDisposable
{
    private readonly ITestOutputHelper _output;

    public RecordingStopControlTests(ITestOutputHelper output)
    {
        _output = output;
    }

    public void Dispose()
    {
        SystemQuery.SetDisplayProvider(null);
    }

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
                OutputPath = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"test-stop-{Guid.NewGuid():N}.mp4")
            }
        };
    }

    private static DisplayDpiInfo TestDpi(int dpi, Rectangle? monitorBounds = null) =>
        new("test_display", monitorBounds ?? new Rectangle(0, 0, 1920, 1080), dpi, dpi, dpi / 96f, false, null);

    private static int GetSystemDpi()
    {
        using var temp = new Control();
        return temp.DeviceDpi;
    }

    private static Size MeasureOnMonitor(IUiTextProvider text, Rectangle monitorBounds) =>
        RecordingStopControlLayout.MeasurePreferredSize(text, new Font("Segoe UI", 8, FontStyle.Bold), monitorBounds);

    private static RecordingStopControlForm CreateProductionForm(
        string id,
        RecordingStopControlBounds bounds,
        IUiTextProvider? text = null,
        int? dpi = null)
    {
        var provider = text ?? new UiTextProvider(UiLanguageStore.LoadOrDefault());
        var monitorBounds = new Rectangle(0, 0, 1920, 1080);
        var effectiveDpi = dpi ?? GetSystemDpi();
        var size = MeasureOnMonitor(provider, monitorBounds);
        return new RecordingStopControlForm(id, bounds, size, TestDpi(effectiveDpi, monitorBounds), provider);
    }

    private static void WithDisplayProvider(Func<List<SystemQuery.DisplayInfo>> provider, Action action)
    {
        SystemQuery.SetDisplayProvider(provider);
        try { action(); }
        finally { SystemQuery.SetDisplayProvider(null); }
    }

    private static void WithDisplayDetailProvider(Func<List<SystemQuery.DisplayDetail>> provider, Action action)
    {
        SystemQuery.SetDisplayDetailProvider(provider);
        try { action(); }
        finally { SystemQuery.SetDisplayDetailProvider(null); }
    }

    // =====================================================================
    // Geometry tests
    // =====================================================================

    [Fact]
    public void ComputeBounds_RegularRegion_PrefersOutsideTopRight()
    {
        var vs = SystemInformation.VirtualScreen;
        var recording = new RecordingIndicatorBounds(vs.X + 100, vs.Y + 100, 800, 600);

        var result = RecordingStopControlGeometry.ComputeBounds(recording, null);

        Assert.Equal(recording.X + recording.Width + RecordingStopControlGeometry.OutsideMargin, result.X);
        Assert.Equal(recording.Y, result.Y);
        Assert.Equal(RecordingStopControlGeometry.DefaultButtonWidth, result.Width);
        Assert.Equal(RecordingStopControlGeometry.DefaultButtonHeight, result.Height);
    }

    [Fact]
    public void ComputeBounds_NearRightEdge_FallsBackInside()
    {
        var vs = SystemInformation.VirtualScreen;
        var recording = new RecordingIndicatorBounds(
            vs.X + vs.Width - 100,
            vs.Y + 100,
            100,
            100);

        var result = RecordingStopControlGeometry.ComputeBounds(recording, null);

        // Should be inside the recording's top-right corner.
        Assert.True(result.X < recording.X + recording.Width);
        Assert.True(result.X + result.Width <= vs.X + vs.Width);
        Assert.True(result.Y >= vs.Y);
    }

    [Fact]
    public void ComputeBounds_NegativeCoordinates_ClampedToVirtualScreen()
    {
        var vs = SystemInformation.VirtualScreen;
        var recording = new RecordingIndicatorBounds(vs.X - 100, vs.Y - 100, 800, 600);

        var result = RecordingStopControlGeometry.ComputeBounds(recording, null);

        Assert.True(result.X >= vs.X);
        Assert.True(result.Y >= vs.Y);
        Assert.True(result.X + result.Width <= vs.X + vs.Width);
        Assert.True(result.Y + result.Height <= vs.Y + vs.Height);
    }

    [Fact]
    public void ComputeBounds_FullScreenRecording_ClampedAndVisible()
    {
        var vs = SystemInformation.VirtualScreen;
        var recording = new RecordingIndicatorBounds(vs.X, vs.Y, vs.Width, vs.Height);

        var result = RecordingStopControlGeometry.ComputeBounds(recording, null);

        // Outside placement would overflow, so it falls back inside and clamps.
        Assert.True(result.X >= vs.X);
        Assert.True(result.Y >= vs.Y);
        Assert.True(result.X + result.Width <= vs.X + vs.Width);
        Assert.True(result.Y + result.Height <= vs.Y + vs.Height);
    }

    [Fact]
    public void ComputeBounds_NestedInner_OffsetsDownFromOuter()
    {
        var vs = SystemInformation.VirtualScreen;
        var recording = new RecordingIndicatorBounds(vs.X + 100, vs.Y + 100, 800, 600);

        var outer = RecordingStopControlGeometry.ComputeBounds(recording, "outer");
        var inner = RecordingStopControlGeometry.ComputeBounds(recording, "inner");

        Assert.True(inner.Y > outer.Y, "inner button should be offset below outer button");

        // Rectangles must not overlap.
        bool overlapX = outer.X < inner.X + inner.Width && outer.X + outer.Width > inner.X;
        bool overlapY = outer.Y < inner.Y + inner.Height && outer.Y + outer.Height > inner.Y;
        Assert.False(overlapX && overlapY, "nested stop controls must not intersect");
    }

    [Fact]
    public void ComputeBounds_NestedInner_BottomEdge_DoesNotIntersect()
    {
        var controlSize = new Size(RecordingStopControlGeometry.DefaultButtonWidth, RecordingStopControlGeometry.DefaultButtonHeight);
        var virtualScreen = new Rectangle(0, 0, 3200, 1610);

        // Place recording so the outer button is flush with the bottom of the virtual screen.
        // This forces the inner "below" candidate to overflow and proves the algorithm picks
        // a non-overlapping alternative (above/left/right) instead of clamping back onto outer.
        var recording = new RecordingIndicatorBounds(
            100,
            virtualScreen.Height - controlSize.Height,
            200,
            100);

        var outer = RecordingStopControlGeometry.ComputeBounds(recording, "outer", virtualScreen);
        var inner = RecordingStopControlGeometry.ComputeBounds(recording, "inner", virtualScreen);

        Assert.True(RecordingStopControlGeometry.IsInside(outer, virtualScreen));
        Assert.True(RecordingStopControlGeometry.IsInside(inner, virtualScreen));
        Assert.False(RecordingStopControlGeometry.Intersects(outer, inner), "nested stop controls must not intersect at bottom edge");
    }

    [Fact]
    public void ComputeBounds_NestedInner_BottomRightCorner_DoesNotIntersect()
    {
        var controlSize = new Size(RecordingStopControlGeometry.DefaultButtonWidth, RecordingStopControlGeometry.DefaultButtonHeight);
        var virtualScreen = new Rectangle(0, 0, 3200, 1610);

        // Recording fills the bottom-right corner; outer is clamped to bottom-right.
        var recording = new RecordingIndicatorBounds(
            virtualScreen.Width - 200,
            virtualScreen.Height - controlSize.Height,
            200,
            100);

        var outer = RecordingStopControlGeometry.ComputeBounds(recording, "outer", virtualScreen);
        var inner = RecordingStopControlGeometry.ComputeBounds(recording, "inner", virtualScreen);

        Assert.True(RecordingStopControlGeometry.IsInside(outer, virtualScreen));
        Assert.True(RecordingStopControlGeometry.IsInside(inner, virtualScreen));
        Assert.False(RecordingStopControlGeometry.Intersects(outer, inner), "nested stop controls must not intersect at bottom-right corner");
    }

    [Fact]
    public void ComputeBounds_InjectedNegativeVirtualScreen_ActuallyUsesNegativeCoordinates()
    {
        var virtualScreen = new Rectangle(-1920, -200, 3200, 1280);
        var recording = new RecordingIndicatorBounds(-1500, -100, 800, 600);

        var outer = RecordingStopControlGeometry.ComputeBounds(recording, "outer", virtualScreen);
        var inner = RecordingStopControlGeometry.ComputeBounds(recording, "inner", virtualScreen);

        Assert.True(outer.X < 0 || outer.Y < 0, "outer should have at least one negative coordinate");
        Assert.True(RecordingStopControlGeometry.IsInside(outer, virtualScreen));
        Assert.True(RecordingStopControlGeometry.IsInside(inner, virtualScreen));
        Assert.False(RecordingStopControlGeometry.Intersects(outer, inner), "nested stop controls must not intersect with negative virtual screen origin");
    }

    [Fact]
    public void ResolveCollision_NoOccupied_KeepsPreferredBounds()
    {
        var virtualScreen = new Rectangle(0, 0, 3200, 1610);
        var preferred = new RecordingStopControlBounds(500, 500, 76, 28);

        var result = RecordingStopControlGeometry.ResolveCollision(
            preferred,
            new Size(76, 28),
            virtualScreen,
            Array.Empty<RecordingStopControlBounds>());

        Assert.Equal(preferred, result);
    }

    [Fact]
    public void ResolveCollision_NearBottomRight_StaysInsideAndAvoidsAllOccupied()
    {
        var virtualScreen = new Rectangle(0, 0, 3200, 1610);
        var occupied = new[]
        {
            new RecordingStopControlBounds(virtualScreen.Right - 80, virtualScreen.Bottom - 32, 76, 28)
        };
        var preferred = occupied[0];

        var result = RecordingStopControlGeometry.ResolveCollision(
            preferred,
            new Size(76, 28),
            virtualScreen,
            occupied);

        Assert.True(RecordingStopControlGeometry.IsInside(result, virtualScreen));
        Assert.False(RecordingStopControlGeometry.Intersects(result, occupied[0]));
    }

    [Fact]
    public void ResolveCollision_ThreeOccupiedSamePreferred_FindsNonOverlappingSpot()
    {
        var virtualScreen = new Rectangle(0, 0, 3200, 1610);
        var preferred = new RecordingStopControlBounds(500, 500, 76, 28);
        var occupied = new[]
        {
            new RecordingStopControlBounds(500, 500, 76, 28),
            new RecordingStopControlBounds(500, 532, 76, 28),
            new RecordingStopControlBounds(500, 564, 76, 28)
        };

        var result = RecordingStopControlGeometry.ResolveCollision(
            preferred,
            new Size(76, 28),
            virtualScreen,
            occupied);

        Assert.True(RecordingStopControlGeometry.IsInside(result, virtualScreen));
        Assert.All(occupied, o => Assert.False(RecordingStopControlGeometry.Intersects(result, o)));
    }

    [Fact]
    public void ComputeBounds_TinyRegion_ButtonFitsInsideVirtualScreen()
    {
        var vs = SystemInformation.VirtualScreen;
        var recording = new RecordingIndicatorBounds(vs.X + 10, vs.Y + 10, 32, 32);

        var result = RecordingStopControlGeometry.ComputeBounds(recording, null);

        Assert.True(result.Width > 0);
        Assert.True(result.Height > 0);
        Assert.True(result.X + result.Width <= vs.X + vs.Width);
        Assert.True(result.Y + result.Height <= vs.Y + vs.Height);
    }

    // =====================================================================
    // Form property / behavior tests
    // =====================================================================

    [Fact]
    public void Form_HasExpectedWindowProperties()
    {
        RunOnSta(() =>
        {
            using var form = new RecordingStopControlForm("r1",
                new RecordingStopControlBounds(100, 100, 76, 28));

            Assert.False(form.ShowInTaskbar);
            Assert.True(form.TopMost);
            Assert.Equal(FormBorderStyle.None, form.FormBorderStyle);
            Assert.False(form.ControlBox);
            Assert.False(form.MaximizeBox);
            Assert.False(form.MinimizeBox);
            Assert.Equal("", form.Text);
            Assert.Equal(FormStartPosition.Manual, form.StartPosition);

            var showWithoutActivation = typeof(Form)
                .GetProperty("ShowWithoutActivation", BindingFlags.Instance | BindingFlags.NonPublic)
                ?.GetValue(form) as bool?;
            Assert.True(showWithoutActivation.GetValueOrDefault());
        });
    }

    [Fact]
    public void Form_ButtonClick_FiresStopClickedOnceAndDisables()
    {
        RunOnSta(() =>
        {
            using var form = new RecordingStopControlForm("r1",
                new RecordingStopControlBounds(100, 100, 76, 28));
            string? clickedId = null;
            int clickCount = 0;
            form.StopClicked += id => { clickedId = id; Interlocked.Increment(ref clickCount); };

            form.Show();
            Application.DoEvents();

            // Simulate multiple clicks.
            var button = GetPrivateField<Button>(form, "_button");
            button.PerformClick();
            button.PerformClick();
            button.PerformClick();
            Application.DoEvents();

            Assert.Equal("r1", clickedId);
            Assert.Equal(1, clickCount);
            Assert.False(form.ButtonEnabledForTests);
            Assert.Equal("停止中...", form.ButtonTextForTests);

            form.Close();
        });
    }

    [Fact]
    public void Form_Dispose_ReleasesResources()
    {
        RunOnSta(() =>
        {
            var form = new RecordingStopControlForm("r1",
                new RecordingStopControlBounds(100, 100, 76, 28));
            form.Show();
            form.Dispose();

            Assert.Throws<ObjectDisposedException>(() => form.Show());
        });
    }

    // =====================================================================
    // Manager wiring tests
    // =====================================================================

    [Fact]
    public void Manager_ShowFor_CreatesStopControlAndIndicator()
    {
        RunOnSta(() =>
        {
            var audit = new CaptureAuditLogger();
            string? requestedId = null;
            var mgr = new RecordingIndicatorManager(audit, id => requestedId = id);
            var rec = MakeRecording((100, 100, 800, 600));

            mgr.ShowFor(rec);

            Assert.Single(mgr.IndicatorsForTests);
            Assert.Single(mgr.StopControlsForTests);
            Assert.Contains(audit.Events, e => e.evt == "recording_stop_control.shown");

            mgr.CloseAll("test");
        });
    }

    [Fact]
    public void Manager_StopControlClick_CallsBackWithRecordingId()
    {
        RunOnSta(() =>
        {
            var audit = new CaptureAuditLogger();
            string? requestedId = null;
            var mgr = new RecordingIndicatorManager(audit, id => requestedId = id);
            var rec = MakeRecording((100, 100, 800, 600));

            mgr.ShowFor(rec);
            var stopControl = mgr.StopControlsForTests[rec.Id];
            var button = GetPrivateField<Button>(stopControl, "_button");
            stopControl.Show();
            Application.DoEvents();
            button.PerformClick();
            Application.DoEvents();

            Assert.Equal(rec.Id, requestedId);
            Assert.Contains(audit.Events, e => e.evt == "recording_stop_control.clicked");

            mgr.CloseAll("test");
        });
    }

    [Fact]
    public void Manager_CloseFor_ClosesBothForms()
    {
        RunOnSta(() =>
        {
            var audit = new CaptureAuditLogger();
            var mgr = new RecordingIndicatorManager(audit);
            var rec = MakeRecording((100, 100, 800, 600));

            mgr.ShowFor(rec);
            Assert.Single(mgr.IndicatorsForTests);
            Assert.Single(mgr.StopControlsForTests);

            mgr.CloseFor(rec.Id, "test.close");

            Assert.Empty(mgr.IndicatorsForTests);
            Assert.Empty(mgr.StopControlsForTests);
            Assert.Contains(audit.Events, e => e.evt == "recording_stop_control.closed");
        });
    }

    [Fact]
    public void Manager_NestedTwoRecordings_StopControlsDoNotOverlap()
    {
        RunOnSta(() =>
        {
            var audit = new CaptureAuditLogger();
            var mgr = new RecordingIndicatorManager(audit);
            var outer = MakeRecording((0, 0, 1920, 1080), "outer");
            var inner = MakeRecording((100, 100, 640, 480), "inner");

            mgr.ShowFor(outer);
            mgr.ShowFor(inner);

            var outerBounds = mgr.StopControlsForTests[outer.Id].PlacementBounds;
            var innerBounds = mgr.StopControlsForTests[inner.Id].PlacementBounds;

            Assert.False(RecordingStopControlGeometry.Intersects(outerBounds, innerBounds),
                "nested stop controls must not intersect");

            mgr.CloseAll("test");
        });
    }

    [Fact]
    public void Manager_TwoDifferentRecordingBounds_ExactPreferredCollision_AreSeparated()
    {
        RunOnSta(() =>
        {
            var vs = SystemInformation.VirtualScreen;
            var audit = new CaptureAuditLogger();
            var mgr = new RecordingIndicatorManager(audit);

            var outerRecording = new RecordingIndicatorBounds(vs.X + 296, vs.Y + 200, 200, 100);
            var innerRecording = new RecordingIndicatorBounds(vs.X + 296, vs.Y + 168, 200, 100);

            var preferredOuter = RecordingStopControlGeometry.ComputeBounds(outerRecording, "outer", vs);
            var preferredInner = RecordingStopControlGeometry.ComputeBounds(innerRecording, "inner", vs);

            Assert.Equal(preferredOuter, preferredInner);

            var outer = MakeRecording((outerRecording.X, outerRecording.Y, outerRecording.Width, outerRecording.Height), "outer");
            var inner = MakeRecording((innerRecording.X, innerRecording.Y, innerRecording.Width, innerRecording.Height), "inner");

            mgr.ShowFor(outer);
            mgr.ShowFor(inner);

            var outerBounds = mgr.StopControlsForTests[outer.Id].PlacementBounds;
            var innerBounds = mgr.StopControlsForTests[inner.Id].PlacementBounds;

            Assert.True(RecordingStopControlGeometry.IsInside(outerBounds, vs));
            Assert.True(RecordingStopControlGeometry.IsInside(innerBounds, vs));
            Assert.False(RecordingStopControlGeometry.Intersects(outerBounds, innerBounds),
                "stop controls with originally identical preferred bounds must be separated");

            mgr.CloseAll("test");
        });
    }

    [Fact]
    public void Manager_ThreeActiveRecordings_AllStopControlsArePairwiseDisjoint()
    {
        RunOnSta(() =>
        {
            var vs = SystemInformation.VirtualScreen;
            var audit = new CaptureAuditLogger();
            var mgr = new RecordingIndicatorManager(audit);

            var recordingBounds = new RecordingIndicatorBounds(vs.X + 100, vs.Y + 100, 800, 600);
            var preferred = RecordingStopControlGeometry.ComputeBounds(recordingBounds, null, vs);

            var rec1 = MakeRecording((recordingBounds.X, recordingBounds.Y, recordingBounds.Width, recordingBounds.Height));
            var rec2 = MakeRecording((recordingBounds.X, recordingBounds.Y, recordingBounds.Width, recordingBounds.Height));
            var rec3 = MakeRecording((recordingBounds.X, recordingBounds.Y, recordingBounds.Width, recordingBounds.Height));

            mgr.ShowFor(rec1);
            mgr.ShowFor(rec2);
            mgr.ShowFor(rec3);

            Assert.Equal(3, mgr.StopControlsForTests.Count);

            var bounds = mgr.StopControlsForTests.Values.Select(s => s.PlacementBounds).ToList();
            for (int i = 0; i < bounds.Count; i++)
            {
                Assert.True(RecordingStopControlGeometry.IsInside(bounds[i], vs));
                for (int j = i + 1; j < bounds.Count; j++)
                {
                    Assert.False(RecordingStopControlGeometry.Intersects(bounds[i], bounds[j]),
                        $"stop controls {i} and {j} must not intersect");
                }
            }

            mgr.CloseAll("test");
        });
    }

    [Fact]
    public void Manager_StopControlShowFailure_DoesNotRemoveIndicator()
    {
        RunOnSta(() =>
        {
            var audit = new CaptureAuditLogger();
            RecordingStopControlForm ThrowingStopFactory(string id, RecordingStopControlBounds bounds, Size size, DisplayDpiInfo dpi)
            {
                var form = new RecordingStopControlForm(id, bounds, size, dpi);
                form.Dispose();
                return form;
            }

            var mgr = new RecordingIndicatorManager(
                audit,
                _ => { },
                (id, b, s, d, r) => new RecordingIndicatorForm(id, b, s, d, r),
                ThrowingStopFactory);
            var rec = MakeRecording((100, 100, 800, 600));

            mgr.ShowFor(rec);

            Assert.Single(mgr.IndicatorsForTests);
            Assert.Empty(mgr.StopControlsForTests);
            Assert.Contains(audit.Events, e => e.evt == "recording_stop_control.show_error");

            var indicator = mgr.IndicatorsForTests[rec.Id];
            mgr.CloseAll("test");

            Assert.Empty(mgr.IndicatorsForTests);
            Assert.Empty(mgr.StopControlsForTests);
            Assert.True(indicator.IsDisposed);
        });
    }

    [Fact]
    public void Manager_IndicatorFactoryThrow_LogsErrorAndStillCreatesStopControl()
    {
        RunOnSta(() =>
        {
            var audit = new CaptureAuditLogger();
            RecordingIndicatorForm ThrowingFactory(string id, RecordingIndicatorBounds bounds, DateTime started, int? duration, string? role)
                => throw new InvalidOperationException("indicator factory boom");

            var mgr = new RecordingIndicatorManager(
                audit,
                _ => { },
                ThrowingFactory,
                (id, b, size, dpi) => new RecordingStopControlForm(id, b, size, dpi));
            var rec = MakeRecording((100, 100, 800, 600));

            mgr.ShowFor(rec);

            Assert.Empty(mgr.IndicatorsForTests);
            Assert.Single(mgr.StopControlsForTests);
            var error = Assert.Single(audit.Events, e => e.evt == "recording_indicator.show_error");
            Assert.Contains("indicator factory boom", error.json);
            Assert.Contains("\"stage\":\"factory\"", error.json);

            var stopControl = mgr.StopControlsForTests[rec.Id];
            mgr.CloseAll("test");

            Assert.Empty(mgr.StopControlsForTests);
            Assert.True(stopControl.IsDisposed);
        });
    }

    [Fact]
    public void Manager_StopControlFactoryThrow_LogsErrorAndStillCreatesIndicator()
    {
        RunOnSta(() =>
        {
            var audit = new CaptureAuditLogger();
            RecordingStopControlForm ThrowingFactory(string id, RecordingStopControlBounds bounds, Size size, DisplayDpiInfo dpi)
                => throw new InvalidOperationException("stop control factory boom");

            var mgr = new RecordingIndicatorManager(
                audit,
                _ => { },
                (id, b, s, d, r) => new RecordingIndicatorForm(id, b, s, d, r),
                ThrowingFactory);
            var rec = MakeRecording((100, 100, 800, 600));

            mgr.ShowFor(rec);

            Assert.Single(mgr.IndicatorsForTests);
            Assert.Empty(mgr.StopControlsForTests);
            var error = Assert.Single(audit.Events, e => e.evt == "recording_stop_control.show_error");
            Assert.Contains("stop control factory boom", error.json);
            Assert.Contains("\"stage\":\"factory\"", error.json);

            var indicator = mgr.IndicatorsForTests[rec.Id];
            mgr.CloseAll("test");

            Assert.Empty(mgr.IndicatorsForTests);
            Assert.True(indicator.IsDisposed);
        });
    }

    [Fact]
    public void Manager_CloseAll_AfterPartialSuccess_CleansBothDictionaries()
    {
        RunOnSta(() =>
        {
            var audit = new CaptureAuditLogger();
            RecordingStopControlForm FailingStopFactory(string id, RecordingStopControlBounds bounds, Size size, DisplayDpiInfo dpi)
            {
                var form = new RecordingStopControlForm(id, bounds, size, dpi);
                form.Dispose();
                return form;
            }

            var mgr = new RecordingIndicatorManager(
                audit,
                _ => { },
                (id, b, s, d, r) => new RecordingIndicatorForm(id, b, s, d, r),
                FailingStopFactory);
            var rec = MakeRecording((100, 100, 800, 600));

            mgr.ShowFor(rec);

            Assert.Single(mgr.IndicatorsForTests);
            Assert.Empty(mgr.StopControlsForTests);

            mgr.CloseAll("test");

            Assert.Empty(mgr.IndicatorsForTests);
            Assert.Empty(mgr.StopControlsForTests);
        });
    }

    [Fact]
    public void Form_ResetForRetry_RestoresButtonState()
    {
        RunOnSta(() =>
        {
            using var form = new RecordingStopControlForm("r1",
                new RecordingStopControlBounds(100, 100, 76, 28));
            form.Show();
            Application.DoEvents();

            var button = GetPrivateField<Button>(form, "_button");
            button.PerformClick();
            Application.DoEvents();

            Assert.False(form.ButtonEnabledForTests);
            Assert.Equal("停止中...", form.ButtonTextForTests);

            form.ResetForRetry();

            Assert.True(form.ButtonEnabledForTests);
            Assert.Equal("\u25A0 停止", form.ButtonTextForTests);

            form.Close();
        });
    }

    [Fact]
    public void Manager_ResetStopControlAfterFailure_RestoresButtonState()
    {
        RunOnSta(() =>
        {
            var audit = new CaptureAuditLogger();
            var mgr = new RecordingIndicatorManager(audit);
            var rec = MakeRecording((100, 100, 800, 600));

            mgr.ShowFor(rec);
            var stopControl = mgr.StopControlsForTests[rec.Id];
            var button = GetPrivateField<Button>(stopControl, "_button");
            stopControl.Show();
            Application.DoEvents();
            button.PerformClick();
            Application.DoEvents();

            Assert.False(stopControl.ButtonEnabledForTests);
            Assert.Equal("停止中...", stopControl.ButtonTextForTests);

            mgr.ResetStopControlAfterFailure(rec.Id);

            Assert.True(stopControl.ButtonEnabledForTests);
            Assert.Equal("\u25A0 停止", stopControl.ButtonTextForTests);

            mgr.CloseAll("test");
        });
    }

    [Fact]
    public void Manager_ResetStopControlAfterFailure_MissingId_DoesNotThrow()
    {
        RunOnSta(() =>
        {
            var audit = new CaptureAuditLogger();
            var mgr = new RecordingIndicatorManager(audit);
            mgr.ResetStopControlAfterFailure("missing-id");
        });
    }

    // =====================================================================
    // DPI-aware stop button sizing and stable bounds
    // =====================================================================

    [Theory]
    [InlineData(UiLanguage.ZhCn)]
    [InlineData(UiLanguage.EnUs)]
    public void Form_ButtonSize_MeasuredSizeFitsBothStates(UiLanguage language)
    {
        RunOnSta(() =>
        {
            var text = new UiTextProvider(language);
            var font = new Font("Segoe UI", 8, FontStyle.Bold);
            var measuredSize = RecordingStopControlLayout.MeasurePreferredSize(text, font);

            using var form = new RecordingStopControlForm("r1",
                new RecordingStopControlBounds(100, 100, measuredSize.Width, measuredSize.Height),
                text);
            form.Show();

            var button = form.ButtonBoundsForTests;
            var stopSize = TextRenderer.MeasureText(text.Get("StopControl_Button_Stop"), font, Size.Empty, TextFormatFlags.SingleLine);
            var stoppingSize = TextRenderer.MeasureText(text.Get("StopControl_Button_Stopping"), font, Size.Empty, TextFormatFlags.SingleLine);
            int maxTextWidth = Math.Max(stopSize.Width, stoppingSize.Width);
            int maxTextHeight = Math.Max(stopSize.Height, stoppingSize.Height);

            Assert.True(button.Width >= maxTextWidth + RecordingStopControlLayout.ButtonPadding.Horizontal,
                $"Button width {button.Width} cannot fit longest state text");
            Assert.True(button.Height >= maxTextHeight + RecordingStopControlLayout.ButtonPadding.Vertical,
                $"Button height {button.Height} cannot fit longest state text");
            Assert.Equal(measuredSize, form.ClientSize);

            form.Close();
        });
    }

    [Fact]
    public void Form_ButtonStateChange_BoundsUnchanged()
    {
        RunOnSta(() =>
        {
            var text = new UiTextProvider(UiLanguage.ZhCn);
            var font = new Font("Segoe UI", 8, FontStyle.Bold);
            var measuredSize = RecordingStopControlLayout.MeasurePreferredSize(text, font);

            using var form = new RecordingStopControlForm("r1",
                new RecordingStopControlBounds(100, 100, measuredSize.Width, measuredSize.Height),
                text);
            form.Show();
            Application.DoEvents();

            var initialBounds = form.Bounds;
            var button = GetPrivateField<Button>(form, "_button");
            button.PerformClick();
            Application.DoEvents();

            Assert.Equal(initialBounds, form.Bounds);
            Assert.Equal(text.Get("StopControl_Button_Stopping"), form.ButtonTextForTests);

            form.Close();
        });
    }

    [Fact]
    public void Manager_ShowFor_UsesMeasuredSize_PlacementBoundsMatchFormBounds()
    {
        RunOnSta(() =>
        {
            var audit = new CaptureAuditLogger();
            var mgr = new RecordingIndicatorManager(audit);
            var rec = MakeRecording((100, 100, 800, 600));

            mgr.ShowFor(rec);

            var stopControl = mgr.StopControlsForTests[rec.Id];
            var placement = stopControl.PlacementBounds;
            var formBounds = stopControl.Bounds;

            Assert.Equal(placement.X, formBounds.X);
            Assert.Equal(placement.Y, formBounds.Y);
            Assert.Equal(placement.Width, formBounds.Width);
            Assert.Equal(placement.Height, formBounds.Height);

            mgr.CloseAll("test");
        });
    }

    [Fact]
    public void Manager_NestedTwoRecordings_DynamicSize_NoOverlap()
    {
        RunOnSta(() =>
        {
            var audit = new CaptureAuditLogger();
            var mgr = new RecordingIndicatorManager(audit);
            var outer = MakeRecording((0, 0, 1920, 1080), "outer");
            var inner = MakeRecording((100, 100, 640, 480), "inner");

            mgr.ShowFor(outer);
            mgr.ShowFor(inner);

            var outerBounds = mgr.StopControlsForTests[outer.Id].PlacementBounds;
            var innerBounds = mgr.StopControlsForTests[inner.Id].PlacementBounds;

            var text = new UiTextProvider(UiLanguageStore.LoadOrDefault());
            var font = new Font("Segoe UI", 8, FontStyle.Bold);
            var measuredSize = RecordingStopControlLayout.MeasurePreferredSize(text, font);

            Assert.True(outerBounds.Width >= measuredSize.Width,
                $"Outer stop control width {outerBounds.Width} smaller than measured {measuredSize.Width}");
            Assert.True(outerBounds.Height >= measuredSize.Height);
            Assert.True(innerBounds.Width >= measuredSize.Width);
            Assert.True(innerBounds.Height >= measuredSize.Height);

            Assert.False(RecordingStopControlGeometry.Intersects(outerBounds, innerBounds),
                "nested stop controls must not intersect");

            mgr.CloseAll("test");
        });
    }

    // =====================================================================
    // DPI-aware safety insets and synchronous stopping paint
    // =====================================================================

    /// <summary>
    /// Production DPI matrix test: exercises the target-monitor measurement path at 96-192 DPI.
    /// Uses the internal test seam that forces a specific device DPI on the temporary measurement
    /// host while keeping the production 8 pt font. The font is not artificially scaled; only the
    /// DPI context and safety insets scale, matching the real PerMonitorV2 production behavior.
    /// </summary>
    [Theory]
    [InlineData(UiLanguage.ZhCn, 96)]
    [InlineData(UiLanguage.ZhCn, 120)]
    [InlineData(UiLanguage.ZhCn, 144)]
    [InlineData(UiLanguage.ZhCn, 168)]
    [InlineData(UiLanguage.ZhCn, 192)]
    [InlineData(UiLanguage.EnUs, 96)]
    [InlineData(UiLanguage.EnUs, 120)]
    [InlineData(UiLanguage.EnUs, 144)]
    [InlineData(UiLanguage.EnUs, 168)]
    [InlineData(UiLanguage.EnUs, 192)]
    public void MeasurePreferredSize_TargetDpiMatrix_FitsTextWithScaledInsets(UiLanguage language, int dpi)
    {
        RunOnSta(() =>
        {
            var text = new UiTextProvider(language);
            var font = new Font("Segoe UI", 8, FontStyle.Bold);
            var monitorBounds = new Rectangle(0, 0, 1920, 1080);
            var measuredSize = RecordingStopControlLayout.MeasurePreferredSize(text, font, monitorBounds, dpi);

            var scale = dpi / 96f;
            var stopText = text.Get("StopControl_Button_Stop");
            var stoppingText = text.Get("StopControl_Button_Stopping");
            var stopSize = MeasureTextAtDpi(stopText, font, dpi);
            var stoppingSize = MeasureTextAtDpi(stoppingText, font, dpi);
            int maxTextWidth = Math.Max(stopSize.Width, stoppingSize.Width);
            int maxTextHeight = Math.Max(stopSize.Height, stoppingSize.Height);

            int horizontalInset = (int)Math.Ceiling(RecordingStopControlLayout.HorizontalSafetyInsetLogical * scale);
            int verticalInset = (int)Math.Ceiling(RecordingStopControlLayout.VerticalSafetyInsetLogical * scale);

            Assert.True(measuredSize.Width >= maxTextWidth + RecordingStopControlLayout.ButtonPadding.Horizontal + horizontalInset * 2,
                $"measured width too small at {dpi} DPI for {language}");
            Assert.True(measuredSize.Height >= maxTextHeight + RecordingStopControlLayout.ButtonPadding.Vertical + verticalInset * 2,
                $"measured height too small at {dpi} DPI for {language}");

            _output.WriteLine($"[{language}] dpi={dpi}: measured={measuredSize}, stop={stopSize}, stopping={stoppingSize}");
        });
    }

    private static Size MeasureTextAtDpi(string text, Font font, int dpi)
    {
        using var bitmap = new Bitmap(1, 1);
        bitmap.SetResolution(dpi, dpi);
        using var g = Graphics.FromImage(bitmap);
        return TextRenderer.MeasureText(g, text, font, Size.Empty, TextFormatFlags.SingleLine);
    }

    [Fact]
    public void Form_StoppingText_SyncPaintBeforeCallback()
    {
        RunOnSta(() =>
        {
            var text = new UiTextProvider(UiLanguage.ZhCn);
            var font = new Font("Segoe UI", 8, FontStyle.Bold);
            var measuredSize = RecordingStopControlLayout.MeasurePreferredSize(text, font);

            using var form = new RecordingStopControlForm("r1",
                new RecordingStopControlBounds(100, 100, measuredSize.Width, measuredSize.Height),
                text);
            form.Show();
            Application.DoEvents();

            int paintBefore = form.ButtonPaintCountForTests;
            string? observedText = null;
            form.StopClicked += id => observedText = form.ButtonTextForTests;

            var button = GetPrivateField<Button>(form, "_button");
            button.PerformClick();

            Assert.Equal(text.Get("StopControl_Button_Stopping"), observedText);
            Assert.True(form.ButtonPaintCountForTests > paintBefore,
                "button should have painted synchronously before the callback");
            Assert.True(form.StoppingPaintCountForTests > 0,
                "at least one paint of the stopping state should have occurred");

            form.Close();
        });
    }

    [Fact]
    public void Form_ButtonClick_CallbackException_DoesNotPreventRetry()
    {
        RunOnSta(() =>
        {
            var text = new UiTextProvider(UiLanguage.ZhCn);
            var font = new Font("Segoe UI", 8, FontStyle.Bold);
            var measuredSize = RecordingStopControlLayout.MeasurePreferredSize(text, font);

            using var form = new RecordingStopControlForm("r1",
                new RecordingStopControlBounds(100, 100, measuredSize.Width, measuredSize.Height),
                text);
            form.Show();
            Application.DoEvents();

            form.StopClicked += id => throw new InvalidOperationException("callback boom");

            var button = GetPrivateField<Button>(form, "_button");
            Exception? caught = null;
            try { button.PerformClick(); }
            catch (Exception ex) { caught = ex; }

            Assert.NotNull(caught);
            Assert.False(form.ButtonEnabledForTests);
            Assert.Equal(text.Get("StopControl_Button_Stopping"), form.ButtonTextForTests);

            form.ResetForRetry();

            Assert.True(form.ButtonEnabledForTests);
            Assert.Equal(text.Get("StopControl_Button_Stop"), form.ButtonTextForTests);

            form.Close();
        });
    }

    [Theory]
    [InlineData(UiLanguage.ZhCn)]
    [InlineData(UiLanguage.EnUs)]
    public void Form_ButtonStateChange_BoundsAndSizeUnchanged(UiLanguage language)
    {
        RunOnSta(() =>
        {
            var text = new UiTextProvider(language);
            var font = new Font("Segoe UI", 8, FontStyle.Bold);
            var measuredSize = RecordingStopControlLayout.MeasurePreferredSize(text, font);

            using var form = new RecordingStopControlForm("r1",
                new RecordingStopControlBounds(100, 100, measuredSize.Width, measuredSize.Height),
                text);
            form.Show();
            Application.DoEvents();

            var initialBounds = form.Bounds;
            var initialClientSize = form.ClientSize;
            var initialPlacement = form.PlacementBounds;

            var button = GetPrivateField<Button>(form, "_button");
            button.PerformClick();
            Application.DoEvents();

            Assert.Equal(initialBounds, form.Bounds);
            Assert.Equal(initialClientSize, form.ClientSize);
            Assert.Equal(initialPlacement, form.PlacementBounds);
            Assert.Equal(text.Get("StopControl_Button_Stopping"), form.ButtonTextForTests);

            form.Close();
        });
    }

    [Fact]
    public void Manager_ShowFor_AuditBoundsMatchFormBounds()
    {
        RunOnSta(() =>
        {
            var audit = new CaptureAuditLogger();
            var mgr = new RecordingIndicatorManager(audit);
            var rec = MakeRecording((100, 100, 800, 600));

            mgr.ShowFor(rec);

            var stopControl = mgr.StopControlsForTests[rec.Id];
            var formBounds = stopControl.Bounds;

            var shown = Assert.Single(audit.Events, e => e.evt == "recording_stop_control.shown");
            using var doc = JsonDocument.Parse(shown.json);
            var bounds = doc.RootElement.GetProperty("bounds");
            Assert.Equal(formBounds.X, bounds.GetProperty("x").GetInt32());
            Assert.Equal(formBounds.Y, bounds.GetProperty("y").GetInt32());
            Assert.Equal(formBounds.Width, bounds.GetProperty("w").GetInt32());
            Assert.Equal(formBounds.Height, bounds.GetProperty("h").GetInt32());

            mgr.CloseAll("test");
        });
    }

    // =====================================================================
    // Target-monitor DPI production path
    // =====================================================================

    [Fact]
    public void DisplayDpiResolver_FullyContainedRegion_ReturnsContainingDisplayDpi()
    {
        WithDisplayDetailProvider(() => new List<SystemQuery.DisplayDetail>
        {
            new("display_1", "Display 1", true, new SystemQuery.Bounds(0, 0, 1920, 1080), 1.5, 144, 144, IntPtr.Zero),
            new("display_2", "Display 2", false, new SystemQuery.Bounds(1920, 0, 1920, 1080), 2.0, 192, 192, IntPtr.Zero)
        }, () =>
        {
            var resolver = new DisplayDpiResolver();
            var info = resolver.Resolve(new Rectangle(100, 100, 800, 600));

            Assert.Equal("display_1", info.MonitorId);
            Assert.Equal(144, info.DpiX);
            Assert.Equal(144, info.DpiY);
            Assert.Equal(1.5f, info.Scale);
            Assert.False(info.IsFallback);
        });
    }

    [Fact]
    public void DisplayDpiResolver_CrossScreenRegion_ReturnsLargestIntersectionDisplayDpi()
    {
        WithDisplayDetailProvider(() => new List<SystemQuery.DisplayDetail>
        {
            new("display_1", "Display 1", true, new SystemQuery.Bounds(0, 0, 1920, 1080), 1.0, 96, 96, IntPtr.Zero),
            new("display_2", "Display 2", false, new SystemQuery.Bounds(1920, 0, 1920, 1080), 2.0, 192, 192, IntPtr.Zero)
        }, () =>
        {
            var resolver = new DisplayDpiResolver();
            var info = resolver.Resolve(new Rectangle(1800, 100, 400, 300));

            Assert.Equal("display_2", info.MonitorId);
            Assert.Equal(192, info.DpiX);
            Assert.Equal(192, info.DpiY);
            Assert.Equal(2.0f, info.Scale);
        });
    }

    [Fact]
    public void DisplayDpiResolver_NoIntersection_ReturnsNearestDisplayDpi()
    {
        WithDisplayDetailProvider(() => new List<SystemQuery.DisplayDetail>
        {
            new("display_1", "Display 1", true, new SystemQuery.Bounds(0, 0, 1920, 1080), 1.0, 96, 96, IntPtr.Zero),
            new("display_2", "Display 2", false, new SystemQuery.Bounds(1920, 0, 1920, 1080), 1.25, 120, 120, IntPtr.Zero)
        }, () =>
        {
            var resolver = new DisplayDpiResolver();
            var info = resolver.Resolve(new Rectangle(4000, 100, 100, 100));

            Assert.Equal("display_2", info.MonitorId);
            Assert.Equal(120, info.DpiX);
        });
    }

    [Fact]
    public void DisplayDpiResolver_NegativeCoordinates_ReturnsCorrectDisplayDpi()
    {
        WithDisplayDetailProvider(() => new List<SystemQuery.DisplayDetail>
        {
            new("display_left", "Display Left", false, new SystemQuery.Bounds(-1920, -200, 1920, 1080), 1.25, 120, 120, IntPtr.Zero),
            new("display_right", "Display Right", true, new SystemQuery.Bounds(0, 0, 1920, 1080), 1.0, 96, 96, IntPtr.Zero)
        }, () =>
        {
            var resolver = new DisplayDpiResolver();
            var info = resolver.Resolve(new Rectangle(-1500, -100, 800, 600));

            Assert.Equal("display_left", info.MonitorId);
            Assert.Equal(120, info.DpiX);
        });
    }

    [Fact]
    public void DisplayDpiResolver_NoDisplays_ReturnsFallback()
    {
        WithDisplayDetailProvider(() => new List<SystemQuery.DisplayDetail>(), () =>
        {
            var resolver = new DisplayDpiResolver();
            var info = resolver.Resolve(new Rectangle(100, 100, 800, 600));

            Assert.Equal("fallback", info.MonitorId);
            Assert.Equal(96, info.DpiX);
            Assert.Equal(96, info.DpiY);
            Assert.Equal(1.0f, info.Scale);
            Assert.True(info.IsFallback);
            Assert.Equal("no_displays_found", info.FallbackReason);
        });
    }

    [Fact]
    public void Manager_ShowFor_UsesResolvedDpiAndPassesToFactory()
    {
        RunOnSta(() =>
        {
            WithDisplayDetailProvider(() => new List<SystemQuery.DisplayDetail>
            {
                new("display_1", "Display 1", true, new SystemQuery.Bounds(0, 0, 1920, 1080), 1.5, 144, 144, IntPtr.Zero)
            }, () =>
            {
                var audit = new CaptureAuditLogger();
                DisplayDpiInfo? capturedDpi = null;
                RecordingStopControlForm CapturingFactory(string id, RecordingStopControlBounds bounds, Size size, DisplayDpiInfo dpi)
                {
                    capturedDpi ??= dpi; // capture the first (resolved) DPI, before any HWND retry
                    return new RecordingStopControlForm(id, bounds, size, dpi);
                }

                var mgr = new RecordingIndicatorManager(
                    audit,
                    _ => { },
                    (id, b, s, d, r) => new RecordingIndicatorForm(id, b, s, d, r),
                    CapturingFactory,
                    new DisplayDpiResolver());
                var rec = MakeRecording((100, 100, 800, 600));

                mgr.ShowFor(rec);

                Assert.NotNull(capturedDpi);
                Assert.Equal("display_1", capturedDpi!.MonitorId);
                Assert.Equal(144, capturedDpi.DpiX);
                Assert.Equal(144, capturedDpi.DpiY);
                Assert.Equal(1.5f, capturedDpi.Scale);

                mgr.CloseAll("test");
            });
        });
    }

    [Theory]
    [InlineData(96)]
    [InlineData(120)]
    [InlineData(144)]
    [InlineData(168)]
    [InlineData(192)]
    public void Manager_ShowFor_InjectedDpi_PassesDpiToFactory(int dpi)
    {
        RunOnSta(() =>
        {
            WithDisplayDetailProvider(() => new List<SystemQuery.DisplayDetail>
            {
                new("display_1", "Display 1", true, new SystemQuery.Bounds(0, 0, 1920, 1080), dpi / 96.0, dpi, dpi, IntPtr.Zero)
            }, () =>
            {
                var audit = new CaptureAuditLogger();
                DisplayDpiInfo? capturedDpi = null;
                RecordingStopControlForm CapturingFactory(string id, RecordingStopControlBounds bounds, Size size, DisplayDpiInfo d)
                {
                    capturedDpi ??= d; // capture the first (resolved) DPI, before any HWND retry
                    return new RecordingStopControlForm(id, bounds, size, d);
                }

                var mgr = new RecordingIndicatorManager(
                    audit,
                    _ => { },
                    (id, b, s, d, r) => new RecordingIndicatorForm(id, b, s, d, r),
                    CapturingFactory,
                    new DisplayDpiResolver());
                var rec = MakeRecording((100, 100, 800, 600));

                mgr.ShowFor(rec);

                Assert.NotNull(capturedDpi);
                Assert.Equal("display_1", capturedDpi!.MonitorId);
                Assert.Equal(dpi, capturedDpi.DpiX);
                Assert.Equal(dpi, capturedDpi.DpiY);
                Assert.Equal(dpi / 96f, capturedDpi.Scale);

                mgr.CloseAll("test");
            });
        });
    }

    /// <summary>
    /// Data-flow test: the single size computed by the injected size provider from the
    /// resolved DisplayDpiInfo must reach ComputeBounds, ResolveCollision, the form factory,
    /// the final form bounds, and the audit planned_bounds without being re-measured.
    /// </summary>
    [Theory]
    [InlineData(96, 110, 46)]
    [InlineData(192, 220, 92)]
    public void Manager_ShowFor_SizeProviderDpiDataFlow_PassesSizeToFactoryGeometryAndForm(int dpi, int expectedWidth, int expectedHeight)
    {
        RunOnSta(() =>
        {
            WithDisplayDetailProvider(() => new List<SystemQuery.DisplayDetail>
            {
                new("display_1", "Display 1", true, new SystemQuery.Bounds(0, 0, 1920, 1080), dpi / 96.0, dpi, dpi, IntPtr.Zero)
            }, () =>
            {
                var audit = new CaptureAuditLogger();
                var providerCalls = new List<(string MonitorId, int DpiX, int DpiY, Size Size)>();
                var factorySizes = new List<Size>();
                Size? firstFactorySize = null;

                Size TestSizeProvider(IUiTextProvider text, Font font, DisplayDpiInfo dpiInfo)
                {
                    // The provider decides size from the resolved DPI; it must not use a fake scaled font.
                    var size = dpiInfo.DpiX >= 192
                        ? new Size(220, 92)
                        : new Size(110, 46);
                    providerCalls.Add((dpiInfo.MonitorId, dpiInfo.DpiX, dpiInfo.DpiY, size));
                    return size;
                }

                RecordingStopControlForm CapturingFactory(string id, RecordingStopControlBounds bounds, Size size, DisplayDpiInfo d)
                {
                    factorySizes.Add(size);
                    firstFactorySize ??= size;
                    return new RecordingStopControlForm(id, bounds, size, d);
                }

                var mgr = new RecordingIndicatorManager(
                    audit,
                    _ => { },
                    (id, b, s, d, r) => new RecordingIndicatorForm(id, b, s, d, r),
                    CapturingFactory,
                    new DisplayDpiResolver(),
                    TestSizeProvider);
                var rec = MakeRecording((100, 100, 800, 600));

                mgr.ShowFor(rec);

                // Provider received the resolved DPI/monitor.
                var providerCall = Assert.Single(providerCalls, c => c.DpiX == dpi && c.DpiY == dpi);
                Assert.Equal("display_1", providerCall.MonitorId);
                Assert.Equal(expectedWidth, providerCall.Size.Width);
                Assert.Equal(expectedHeight, providerCall.Size.Height);

                // Factory received the size returned by the provider for the injected DPI.
                Assert.Contains(factorySizes, s => s == providerCall.Size);
                Assert.Equal(expectedWidth, firstFactorySize!.Value.Width);
                Assert.Equal(expectedHeight, firstFactorySize.Value.Height);

                // Final form bounds and placement use the size passed to its factory call.
                var stopControl = mgr.StopControlsForTests[rec.Id];
                var finalSize = factorySizes[^1];
                Assert.Equal(finalSize.Width, stopControl.PlacementBounds.Width);
                Assert.Equal(finalSize.Height, stopControl.PlacementBounds.Height);
                Assert.Equal(finalSize.Width, stopControl.Bounds.Width);
                Assert.Equal(finalSize.Height, stopControl.Bounds.Height);

                // Audit records the same size in planned_bounds and actual bounds.
                var shown = audit.Events.Last(e => e.evt == "recording_stop_control.shown");
                using var doc = JsonDocument.Parse(shown.json);
                var planned = doc.RootElement.GetProperty("planned_bounds");
                Assert.Equal(finalSize.Width, planned.GetProperty("w").GetInt32());
                Assert.Equal(finalSize.Height, planned.GetProperty("h").GetInt32());
                var bounds = doc.RootElement.GetProperty("bounds");
                Assert.Equal(finalSize.Width, bounds.GetProperty("w").GetInt32());
                Assert.Equal(finalSize.Height, bounds.GetProperty("h").GetInt32());

                mgr.CloseAll("test");
            });
        });
    }

    [Fact]
    public void Manager_ShowFor_TwoDisplaysDifferentDpi_UsesRespectiveDpi()
    {
        RunOnSta(() =>
        {
            WithDisplayDetailProvider(() => new List<SystemQuery.DisplayDetail>
            {
                new("display_1", "Display 1", true, new SystemQuery.Bounds(0, 0, 960, 540), 1.0, 96, 96, IntPtr.Zero),
                new("display_2", "Display 2", false, new SystemQuery.Bounds(0, 540, 960, 540), 2.0, 192, 192, IntPtr.Zero)
            }, () =>
            {
                var audit = new CaptureAuditLogger();
                var captured = new Dictionary<string, DisplayDpiInfo>();
                RecordingStopControlForm CapturingFactory(string id, RecordingStopControlBounds bounds, Size size, DisplayDpiInfo dpi)
                {
                    captured.TryAdd(id, dpi); // capture the first (resolved) DPI, before any HWND retry
                    return new RecordingStopControlForm(id, bounds, size, dpi);
                }

                var mgr = new RecordingIndicatorManager(
                    audit,
                    _ => { },
                    (id, b, s, d, r) => new RecordingIndicatorForm(id, b, s, d, r),
                    CapturingFactory,
                    new DisplayDpiResolver());

                var rec1 = MakeRecording((100, 100, 800, 400));
                var rec2 = MakeRecording((100, 600, 800, 400));

                mgr.ShowFor(rec1);
                mgr.ShowFor(rec2);

                Assert.Equal("display_1", captured[rec1.Id].MonitorId);
                Assert.Equal(96, captured[rec1.Id].DpiX);
                Assert.Equal("display_2", captured[rec2.Id].MonitorId);
                Assert.Equal(192, captured[rec2.Id].DpiX);
                Assert.False(RecordingStopControlGeometry.Intersects(
                    mgr.StopControlsForTests[rec1.Id].PlacementBounds,
                    mgr.StopControlsForTests[rec2.Id].PlacementBounds),
                    "stop controls on different DPI displays must not intersect");

                mgr.CloseAll("test");
            });
        });
    }

    [Fact]
    public void Manager_ShowFor_DpiFallback_LogsFallbackReason()
    {
        RunOnSta(() =>
        {
            WithDisplayDetailProvider(() => new List<SystemQuery.DisplayDetail>(), () =>
            {
                var audit = new CaptureAuditLogger();
                var mgr = new RecordingIndicatorManager(
                    audit,
                    _ => { },
                    (id, b, s, d, r) => new RecordingIndicatorForm(id, b, s, d, r),
                    (id, b, size, dpi) => new RecordingStopControlForm(id, b, size, dpi));
                var rec = MakeRecording((100, 100, 800, 600));

                mgr.ShowFor(rec);

                var shown = Assert.Single(audit.Events, e => e.evt == "recording_stop_control.shown");
                using var doc = JsonDocument.Parse(shown.json);
                Assert.True(doc.RootElement.GetProperty("dpi_fallback").GetBoolean());
                Assert.Equal("no_displays_found", doc.RootElement.GetProperty("dpi_fallback_reason").GetString());

                mgr.CloseAll("test");
            });
        });
    }

    [Fact]
    public void Form_ProductionConstructor_UsesProvidedSizeWithoutAutoScale()
    {
        RunOnSta(() =>
        {
            var text = new UiTextProvider(UiLanguage.ZhCn);
            var monitorBounds = new Rectangle(0, 0, 1920, 1080);
            var size = MeasureOnMonitor(text, monitorBounds);
            using var form = CreateProductionForm("r1",
                new RecordingStopControlBounds(100, 100, size.Width, size.Height), text);

            form.Show();
            Application.DoEvents();

            Assert.Equal(AutoScaleMode.None, form.AutoScaleMode);
            Assert.Equal(size, form.ClientSize);
            Assert.Equal(size.Width, form.Bounds.Width);
            Assert.Equal(size.Height, form.Bounds.Height);

            form.Close();
        });
    }

    [Theory]
    [InlineData(UiLanguage.ZhCn)]
    [InlineData(UiLanguage.EnUs)]
    public void MeasurePreferredSize_TargetMonitorBounds_FitsBothStatesOnCurrentMonitor(UiLanguage language)
    {
        RunOnSta(() =>
        {
            var text = new UiTextProvider(language);
            var monitorBounds = new Rectangle(
                SystemInformation.VirtualScreen.X,
                SystemInformation.VirtualScreen.Y,
                Math.Min(1920, SystemInformation.VirtualScreen.Width),
                Math.Min(1080, SystemInformation.VirtualScreen.Height));
            var size = MeasureOnMonitor(text, monitorBounds);

            using var form = CreateProductionForm("r1",
                new RecordingStopControlBounds(100, 100, size.Width, size.Height), text);
            form.Show();
            Application.DoEvents();

            var button = GetPrivateField<Button>(form, "_button");
            var safe = RecordingStopControlLayout.GetContentSafeRectangle(button);
            var font = button.Font;

            var stopSize = TextRenderer.MeasureText(text.Get("StopControl_Button_Stop"), font, Size.Empty, TextFormatFlags.SingleLine);
            var stoppingSize = TextRenderer.MeasureText(text.Get("StopControl_Button_Stopping"), font, Size.Empty, TextFormatFlags.SingleLine);

            Assert.True(safe.Width >= stopSize.Width && safe.Height >= stopSize.Height,
                $"stop text does not fit in safe rectangle for {language}");
            Assert.True(safe.Width >= stoppingSize.Width && safe.Height >= stoppingSize.Height,
                $"stopping text does not fit in safe rectangle for {language}");

            _output.WriteLine($"[{language}] current-monitor size={size}, safe={safe}, stop={stopSize}, stopping={stoppingSize}");

            form.Close();
        });
    }

    [Fact]
    public void Manager_ShowFor_AuditContainsDpiFields()
    {
        RunOnSta(() =>
        {
            var audit = new CaptureAuditLogger();
            var mgr = new RecordingIndicatorManager(audit);
            var rec = MakeRecording((100, 100, 800, 600));

            mgr.ShowFor(rec);

            var shown = Assert.Single(audit.Events, e => e.evt == "recording_stop_control.shown");
            using var doc = JsonDocument.Parse(shown.json);

            Assert.True(doc.RootElement.TryGetProperty("target_monitor", out _));
            Assert.True(doc.RootElement.TryGetProperty("target_dpi_x", out _));
            Assert.True(doc.RootElement.TryGetProperty("target_dpi_y", out _));
            Assert.True(doc.RootElement.TryGetProperty("dpi_scale", out _));
            Assert.True(doc.RootElement.TryGetProperty("planned_bounds", out _));
            Assert.True(doc.RootElement.TryGetProperty("actual_window_dpi", out _));

            mgr.CloseAll("test");
        });
    }
}
