using System;
using System.Drawing;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Windows.Forms;
using AgentRecorder.App;
using AgentRecorder.Infrastructure;
using AgentRecorder.Logging;
using Xunit;

namespace AgentRecorder.Tests;

public sealed class RecordingStatusVisualTests
{
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
            throw new TargetInvocationException(error);
    }

    private static RecordingIndicatorPresentation ExcludePresentation()
    {
        var vs = SystemInformation.VirtualScreen;
        var bounds = new RecordingIndicatorBounds(vs.X + 100, vs.Y + 100, 640, 420);
        return new RecordingIndicatorPresentation(
            CaptureVisibilityMode.ExcludeFromCapture,
            bounds,
            bounds,
            null,
            Array.Empty<Rectangle>(),
            new Rectangle(bounds.X + 8, bounds.Y + 8, 150, 24),
            DisplayAffinityRequested: false,
            FallbackReason: null);
    }

    [Fact]
    public void VisualModel_MapsEveryIndicatorPhase_AndUsesOpaqueRestrainedColors()
    {
        var preparing = RecordingStatusVisualModel.IndicatorPalette(RecordingIndicatorPhase.Preparing, false);
        var countdown = RecordingStatusVisualModel.IndicatorPalette(RecordingIndicatorPhase.Countdown, false);
        var series = RecordingStatusVisualModel.IndicatorPalette(RecordingIndicatorPhase.Series, false);
        var recording = RecordingStatusVisualModel.IndicatorPalette(RecordingIndicatorPhase.Recording, false);
        var finalizing = RecordingStatusVisualModel.IndicatorPalette(RecordingIndicatorPhase.Finalizing, false);

        Assert.Equal(preparing.Border, countdown.Border);
        Assert.Equal(series.Border, series.RecordingLow);
        Assert.Equal(recording.RecordingLow, recording.Border);
        Assert.NotEqual(recording.RecordingLow, recording.RecordingHigh);
        Assert.Equal(finalizing.Border, finalizing.RecordingLow);
        Assert.All(new[] { recording.RecordingLow, recording.RecordingHigh, recording.LabelBackground }, color =>
            Assert.Equal(255, color.A));
        Assert.InRange(Math.Abs(recording.RecordingHigh.R - recording.RecordingLow.R), 1, 40);
        Assert.InRange(Math.Abs(recording.RecordingHigh.G - recording.RecordingLow.G), 1, 40);
        Assert.InRange(Math.Abs(recording.RecordingHigh.B - recording.RecordingLow.B), 1, 40);
        Assert.All(new[] { recording.LabelBackgroundLow, recording.LabelBackgroundHigh }, color =>
            Assert.Equal(255, color.A));
        Assert.NotEqual(recording.LabelBackgroundLow, recording.LabelBackgroundHigh);
        Assert.InRange(Math.Abs(recording.LabelBackgroundHigh.R - recording.LabelBackgroundLow.R), 1, 30);
        Assert.InRange(Math.Abs(recording.LabelBackgroundHigh.G - recording.LabelBackgroundLow.G), 1, 30);
        Assert.InRange(Math.Abs(recording.LabelBackgroundHigh.B - recording.LabelBackgroundLow.B), 1, 30);
    }

    [Fact]
    public void VisualModel_HighContrastUsesSystemColorsAndStopsMotionContrast()
    {
        var indicator = RecordingStatusVisualModel.IndicatorPalette(RecordingIndicatorPhase.Recording, true);
        var stop = RecordingStatusVisualModel.StopControlPalette(true);

        Assert.True(indicator.IsHighContrast);
        Assert.Equal(SystemColors.Highlight, indicator.Border);
        Assert.Equal(SystemColors.Highlight, indicator.RecordingLow);
        Assert.Equal(indicator.RecordingLow, indicator.RecordingHigh);
        Assert.Equal(SystemColors.Highlight, stop.Normal);
        Assert.Equal(SystemColors.Control, stop.Stopping);
        Assert.Equal(SystemColors.WindowText, stop.CapsuleBorder);
    }

    [Fact]
    public void StopVisualModel_MapsHoverPressedStoppingDisabledAndRoundedCapsule()
    {
        var palette = RecordingStatusVisualModel.StopControlPalette(false);
        Assert.NotEqual(palette.Normal, RecordingStatusVisualModel.StopControlBackground(palette, RecordingStopControlVisualState.Hover));
        Assert.NotEqual(palette.Hover, RecordingStatusVisualModel.StopControlBackground(palette, RecordingStopControlVisualState.Pressed));
        Assert.Equal(palette.Stopping, RecordingStatusVisualModel.StopControlBackground(palette, RecordingStopControlVisualState.Stopping));
        Assert.Equal(palette.Disabled, RecordingStatusVisualModel.StopControlBackground(palette, RecordingStopControlVisualState.Disabled));
        Assert.Equal(28, RecordingStatusVisualModel.CapsuleCornerRadius(new Size(96, 28)));
        Assert.True(RecordingStatusVisualModel.IsInsideCapsule(new Size(96, 28), new Point(48, 14)));
        Assert.False(RecordingStatusVisualModel.IsInsideCapsule(new Size(96, 28), new Point(0, 0)));
    }

    [Fact]
    public void StopCapsule_UsesMatchingRoundedRegions_AndRebuildsAfterResize()
    {
        RunOnSta(() =>
        {
            using var form = new RecordingStopControlForm(
                "region-stop",
                new RecordingStopControlBounds(100, 100, 120, 32),
                new Size(120, 32),
                new DisplayDpiInfo("test", new Rectangle(0, 0, 1920, 1080), 96, 96, 1, false, null),
                CaptureVisibilityMode.ExcludeFromCapture,
                "outer",
                new UiTextProvider(UiLanguage.EnUs));
            form.Show();
            Application.DoEvents();

            Assert.NotNull(form.CapsuleRegionForTests);
            Assert.NotNull(form.ButtonRegionForTests);
            Assert.Equal(form.ClientSize, form.ButtonBoundsForTests.Size);
            Assert.True(form.CapsuleRegionContainsForTests(new Point(60, 16)));
            Assert.True(form.CapsuleRegionContainsForTests(new Point(1, 16)));
            Assert.True(form.CapsuleRegionContainsForTests(new Point(118, 16)));
            Assert.True(form.ButtonRegionContainsForTests(new Point(60, 16)));
            Assert.False(form.CapsuleRegionContainsForTests(new Point(0, 0)));
            Assert.False(form.CapsuleRegionContainsForTests(new Point(119, 0)));
            Assert.False(form.CapsuleRegionContainsForTests(new Point(0, 31)));
            Assert.False(form.CapsuleRegionContainsForTests(new Point(119, 31)));
            Assert.DoesNotContain(new[] { form.PaletteForTests.Normal, form.PaletteForTests.CapsuleBorder },
                color => color == Color.Magenta || color.A != 255);

            var firstFormRegion = form.CapsuleRegionForTests;
            var firstButtonRegion = form.ButtonRegionForTests;
            int firstFormGeneration = form.CapsuleRegionGenerationForTests;
            int firstButtonGeneration = form.ButtonRegionGenerationForTests;
            form.ClientSize = new Size(144, 36);
            Application.DoEvents();

            Assert.NotSame(firstFormRegion, form.CapsuleRegionForTests);
            Assert.NotSame(firstButtonRegion, form.ButtonRegionForTests);
            Assert.True(form.CapsuleRegionGenerationForTests > firstFormGeneration);
            Assert.True(form.ButtonRegionGenerationForTests > firstButtonGeneration);
            Assert.True(form.CapsuleRegionContainsForTests(new Point(72, 18)));
            Assert.False(form.CapsuleRegionContainsForTests(new Point(0, 0)));
            Assert.Equal(new Size(144, 36), form.ClientSize);
            form.Close();
        });
    }

    [Fact]
    public void MotionCurve_IsSmoothTwoSecondPingPong()
    {
        Assert.Equal(0, RecordingIndicatorMotion.PulseAmount(TimeSpan.Zero), 6);
        Assert.Equal(0.5, RecordingIndicatorMotion.PulseAmount(TimeSpan.FromMilliseconds(500)), 6);
        Assert.Equal(1, RecordingIndicatorMotion.PulseAmount(TimeSpan.FromSeconds(1)), 6);
        Assert.Equal(0.5, RecordingIndicatorMotion.PulseAmount(TimeSpan.FromMilliseconds(1500)), 6);
        Assert.Equal(0, RecordingIndicatorMotion.PulseAmount(TimeSpan.FromSeconds(2)), 6);

        var beforeWrap = RecordingIndicatorMotion.PulseAmount(TimeSpan.FromMilliseconds(1999));
        var afterWrap = RecordingIndicatorMotion.PulseAmount(TimeSpan.FromMilliseconds(1));
        Assert.InRange(Math.Abs(beforeWrap - afterWrap), 0, 0.001);
        Assert.InRange(RecordingIndicatorMotion.TimerIntervalMilliseconds, 100, 500);
        Assert.InRange(RecordingIndicatorMotion.Cycle.TotalSeconds, 1.6, 2.4);
    }

    [Fact]
    public void MotionPreferenceFailureFailsClosedWithoutChangingSystemSettings()
    {
        RunOnSta(() =>
        {
            using var form = new RecordingIndicatorForm(
                "motion-failure",
                ExcludePresentation(),
                DateTime.UtcNow,
                motionPreference: new ThrowingMotionPreference());

            Assert.False(form.MotionEnabledForTests);
            Assert.False(form.MotionTimerEnabledForTests);
            form.SetPhase(RecordingIndicatorPhase.Recording);
            form.UpdateRecordingMotionForTests(TimeSpan.FromSeconds(1));
            Assert.Equal(0, form.MotionTickCountForTests);
        });
    }

    [Fact]
    public void IndicatorMotion_UsesOnePulseForBorderAndLabel_ThenRestoresStaticPhaseColors()
    {
        RunOnSta(() =>
        {
            using var form = new RecordingIndicatorForm(
                "motion-label",
                ExcludePresentation(),
                DateTime.UtcNow,
                motionPreference: new FixedRecordingMotionPreference(true),
                highContrastPreference: () => false);
            form.Show();
            Application.DoEvents();
            form.SetPhase(RecordingIndicatorPhase.Recording);

            var palette = form.VisualPaletteForTests;
            var lowBorder = palette.RecordingLow;
            var lowLabel = palette.LabelBackgroundLow;
            form.UpdateRecordingMotionForTests(TimeSpan.FromMilliseconds(500));
            Assert.Equal(RecordingStatusVisualModel.IndicatorRecordingColor(palette, 0.5), form.RecordingBorderColorForTests);
            Assert.Equal(RecordingStatusVisualModel.IndicatorLabelColor(palette, 0.5), form.RecordingLabelBackgroundForTests);
            Assert.Equal(form.RecordingLabelBackgroundForTests, form.LabelBackColorForTests);
            Assert.NotEqual(lowBorder, form.RecordingBorderColorForTests);
            Assert.NotEqual(lowLabel, form.RecordingLabelBackgroundForTests);
            Assert.Equal(255, form.RecordingBorderColorForTests.A);
            Assert.Equal(255, form.RecordingLabelBackgroundForTests.A);

            form.UpdateRecordingMotionForTests(TimeSpan.FromSeconds(1));
            Assert.Equal(RecordingStatusVisualModel.IndicatorRecordingColor(palette, 1), form.RecordingBorderColorForTests);
            Assert.Equal(RecordingStatusVisualModel.IndicatorLabelColor(palette, 1), form.RecordingLabelBackgroundForTests);

            form.SetPhase(RecordingIndicatorPhase.Finalizing);
            var finalizing = form.VisualPaletteForTests;
            Assert.Equal(finalizing.Border, form.RecordingBorderColorForTests);
            Assert.Equal(finalizing.LabelBackgroundLow, form.RecordingLabelBackgroundForTests);
            Assert.Equal(form.RecordingLabelBackgroundForTests, form.LabelBackColorForTests);

            form.SetPhase(RecordingIndicatorPhase.Preparing);
            var preparing = form.VisualPaletteForTests;
            Assert.Equal(preparing.Border, form.RecordingBorderColorForTests);
            Assert.Equal(preparing.LabelBackgroundLow, form.RecordingLabelBackgroundForTests);
            form.Close();
        });
    }

    [Fact]
    public void IndicatorMotion_SystemPreferenceAndHighContrastChangesAreReevaluated()
    {
        RunOnSta(() =>
        {
            bool highContrast = false;
            using var form = new RecordingIndicatorForm(
                "motion-high-contrast",
                ExcludePresentation(),
                DateTime.UtcNow,
                motionPreference: new FixedRecordingMotionPreference(true),
                highContrastPreference: () => highContrast);
            form.Show();
            Application.DoEvents();
            form.SetPhase(RecordingIndicatorPhase.Recording);
            Assert.True(form.MotionEnabledForTests);
            Assert.True(form.MotionTimerEnabledForTests);

            highContrast = true;
            form.RefreshSystemColorsForTests();
            Assert.False(form.MotionEnabledForTests);
            Assert.False(form.MotionTimerEnabledForTests);
            Assert.True(form.VisualPaletteForTests.IsHighContrast);
            var stableBorder = form.RecordingBorderColorForTests;
            var stableLabel = form.RecordingLabelBackgroundForTests;
            form.UpdateRecordingMotionForTests(TimeSpan.FromSeconds(1));
            Assert.Equal(stableBorder, form.RecordingBorderColorForTests);
            Assert.Equal(stableLabel, form.RecordingLabelBackgroundForTests);

            highContrast = false;
            form.RefreshSystemColorsForTests();
            Assert.True(form.MotionEnabledForTests);
            Assert.True(form.MotionTimerEnabledForTests);
            form.Close();
        });
    }

    [Fact]
    public void IndicatorMotion_OnlyRunsInRecording_AndReleasesOnPhaseEndAndDispose()
    {
        RunOnSta(() =>
        {
            using var form = new RecordingIndicatorForm(
                "motion-lifecycle",
                ExcludePresentation(),
                DateTime.UtcNow,
                motionPreference: new FixedRecordingMotionPreference(true));

            form.SetPhase(RecordingIndicatorPhase.Preparing);
            form.Show();
            Application.DoEvents();
            Assert.False(form.MotionTimerEnabledForTests);

            form.SetPhase(RecordingIndicatorPhase.Countdown, 2);
            Assert.False(form.MotionTimerEnabledForTests);
            form.SetPhase(RecordingIndicatorPhase.Series);
            Assert.False(form.MotionTimerEnabledForTests);
            form.SetPhase(RecordingIndicatorPhase.Recording);
            Assert.True(form.MotionTimerEnabledForTests);
            Assert.Equal(RecordingIndicatorMotion.TimerIntervalMilliseconds, form.MotionTimerIntervalForTests);

            form.SetPhase(RecordingIndicatorPhase.Finalizing);
            Assert.False(form.MotionTimerEnabledForTests);
            form.Close();
            form.Dispose();
            Assert.False(form.MotionTimerEnabledForTests);
        });
    }

    [Fact]
    public void IndicatorMotion_TickChangesOnlyBorderColorAndKeepsPresentationGeometry()
    {
        RunOnSta(() =>
        {
            using var form = new RecordingIndicatorForm(
                "motion-geometry",
                ExcludePresentation(),
                DateTime.UtcNow,
                motionPreference: new FixedRecordingMotionPreference(true));
            form.Show();
            Application.DoEvents();

            var initialBounds = form.Bounds;
            var initialClient = form.ClientRectangle;
            var initialLabel = form.LabelBoundsForTests;
            var initialPresentation = form.PresentationForTests;
            form.UpdateRecordingMotionForTests(TimeSpan.FromSeconds(1));

            Assert.Equal(initialPresentation.WindowBounds, form.PresentationForTests.WindowBounds);
            Assert.Equal(initialBounds, form.Bounds);
            Assert.Equal(initialClient, form.ClientRectangle);
            Assert.Equal(initialLabel, form.LabelBoundsForTests);
            Assert.Equal(255, form.RecordingBorderColorForTests.A);

            var dirty = form.LastMotionDirtyRegionsForTests;
            Assert.NotEmpty(dirty);
            Assert.All(dirty, region => Assert.True(initialClient.Contains(region)));
            var dirtyArea = dirty.Sum(region => region.Width * region.Height);
            Assert.True(dirtyArea < initialClient.Width * initialClient.Height,
                "motion must invalidate only border/label regions, not the entire indicator client");
            form.Close();
        });
    }

    [Fact]
    public void DebugPreviewState_UsesRealManagerWindowCountsAndHasNoMockCanvasType()
    {
        Assert.DoesNotContain(
            typeof(RecordingStatusPreviewState).Assembly.GetTypes(),
            type => type.Name.Contains("RecordingStatusPreviewCanvas", StringComparison.Ordinal));

        RunOnSta(() =>
        {
            var audit = new CaptureAuditLogger();
            var manager = new RecordingIndicatorManager(audit);
            var vs = SystemInformation.VirtualScreen;
            var outer = new RecordingUiPresentation
            {
                RecordingId = "preview-state-outer",
                State = RecordingUiState.Recording,
                SourceType = "debug_preview",
                CaptureBounds = new RecordingUiBounds(vs.X + 120, vs.Y + 120, 640, 420),
                StartedAtUtc = DateTime.UtcNow,
                NestedRole = "outer",
                NestedSessionId = "preview-state-session"
            };
            var inner = outer with
            {
                RecordingId = "preview-state-inner",
                CaptureBounds = new RecordingUiBounds(vs.X + 220, vs.Y + 220, 360, 220),
                NestedRole = "inner",
                ParentRecordingId = outer.RecordingId
            };

            manager.ShowFor(outer);
            manager.ShowFor(inner, outer);
            Assert.Equal(RecordingStatusPreviewState.Expected(true), RecordingStatusPreviewState.Capture(manager));

            manager.CloseAll("preview_state_refresh");
            manager.ShowFor(outer with
            {
                RecordingId = "preview-state-ordinary",
                NestedRole = null,
                NestedSessionId = null
            });
            Assert.Equal(RecordingStatusPreviewState.Expected(false), RecordingStatusPreviewState.Capture(manager));

            manager.CloseAll("preview_state_cleanup");
        });
    }

    [Theory]
    [InlineData(UiLanguage.ZhCn, null)]
    [InlineData(UiLanguage.ZhCn, "outer")]
    [InlineData(UiLanguage.ZhCn, "inner")]
    [InlineData(UiLanguage.EnUs, null)]
    [InlineData(UiLanguage.EnUs, "outer")]
    [InlineData(UiLanguage.EnUs, "inner")]
    public void StopCapsule_UsesExplicitRoleAndStableSizeAcrossStates(UiLanguage language, string? role)
    {
        RunOnSta(() =>
        {
            var text = new UiTextProvider(language);
            using var font = new Font("Segoe UI", 8, FontStyle.Bold);
            var size = RecordingStopControlLayout.MeasurePreferredSize(text, font, role);
            using var form = new RecordingStopControlForm(
                "stop-role",
                new RecordingStopControlBounds(100, 100, size.Width, size.Height),
                size,
                new DisplayDpiInfo("test", new Rectangle(0, 0, 1920, 1080), 96, 96, 1, false, null),
                CaptureVisibilityMode.ExcludeFromCapture,
                role,
                text);

            var before = form.Bounds;
            var initialText = form.ButtonTextForTests;
            var button = (Button)typeof(RecordingStopControlForm)
                .GetField("_button", BindingFlags.NonPublic | BindingFlags.Instance)!
                .GetValue(form)!;
            form.Show();
            Application.DoEvents();
            button.PerformClick();
            Application.DoEvents();

            Assert.Equal(role, form.NestedRoleForTests);
            Assert.Equal(before, form.Bounds);
            Assert.Equal(size, form.ClientSize);
            Assert.Equal(Math.Min(size.Width, size.Height), form.CapsuleCornerRadiusForTests);
            Assert.Contains(language == UiLanguage.ZhCn
                ? (role == null ? "停止" : role.ToUpperInvariant())
                : (role == null ? "Stop" : role.ToUpperInvariant()), initialText);
            Assert.Equal(text.Get("StopControl_Button_Stopping"), role == null
                ? form.ButtonTextForTests
                : form.ButtonTextForTests[(form.ButtonTextForTests.IndexOf('·') + 2)..]);
            Assert.Equal(RecordingStopControlVisualState.Stopping, form.VisualStateForTests);

            form.ResetForRetry();
            Assert.True(form.ButtonEnabledForTests);
            Assert.Equal(RecordingStopControlVisualState.Normal, form.VisualStateForTests);
            Assert.Equal(before, form.Bounds);
            Assert.Equal(size, form.ClientSize);
            form.Close();
        });
    }

    [Theory]
    [InlineData(96)]
    [InlineData(120)]
    [InlineData(144)]
    [InlineData(192)]
    public void StopCapsule_DpiMatrixFitsAllRolesAndBothLocalizedStates(int dpi)
    {
        RunOnSta(() =>
        {
            foreach (var language in new[] { UiLanguage.ZhCn, UiLanguage.EnUs })
            foreach (var role in new string?[] { null, "outer", "inner" })
            {
                var text = new UiTextProvider(language);
                using var font = new Font("Segoe UI", 8, FontStyle.Bold);
                var size = RecordingStopControlLayout.MeasurePreferredSize(
                    text,
                    font,
                    new Rectangle(0, 0, 1920, 1080),
                    dpi,
                    role);
                Assert.True(size.Width >= RecordingStopControlGeometry.DefaultButtonWidth);
                Assert.True(size.Height >= RecordingStopControlGeometry.DefaultButtonHeight);
                using var form = new RecordingStopControlForm(
                    "stop-dpi",
                    new RecordingStopControlBounds(100, 100, size.Width, size.Height),
                    size,
                    new DisplayDpiInfo("test", new Rectangle(0, 0, 1920, 1080), dpi, dpi, dpi / 96f, false, null),
                    CaptureVisibilityMode.ExcludeFromCapture,
                    role,
                    text);
                Assert.Equal(size, form.ClientSize);
            }
        });
    }

    [Fact]
    public void Manager_PassesNestedRoleToStopControlInsteadOfInferringIt()
    {
        RunOnSta(() =>
        {
            var audit = new CaptureAuditLogger();
            var manager = new RecordingIndicatorManager(audit);
            var recording = new RecordingUiPresentation
            {
                RecordingId = "explicit-outer",
                State = RecordingUiState.Recording,
                SourceType = "region",
                CaptureBounds = new RecordingUiBounds(100, 100, 700, 450),
                StartedAtUtc = DateTime.UtcNow,
                NestedRole = "outer"
            };

            manager.ShowFor(recording);
            var stop = manager.StopControlsForTests[recording.RecordingId];
            Assert.Equal("outer", stop.NestedRoleForTests);
            Assert.Contains("OUTER", stop.ButtonTextForTests);
            manager.CloseAll("test");
        });
    }

    private sealed class ThrowingMotionPreference : IRecordingMotionPreference
    {
        public bool IsAnimationEnabled => throw new InvalidOperationException("preference query failed");
    }
}
