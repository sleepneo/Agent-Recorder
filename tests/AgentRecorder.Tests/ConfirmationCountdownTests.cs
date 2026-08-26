using System.Drawing;
using System.Reflection;
using System.Windows.Forms;
using AgentRecorder.App;
using AgentRecorder.Infrastructure;
using Xunit;

namespace AgentRecorder.Tests;

public sealed class ConfirmationCountdownTests
{
    [Fact]
    public void Calculator_UsesAbsoluteDeadlineAndCeilingSeconds()
    {
        var created = new DateTime(2026, 8, 26, 0, 0, 0, DateTimeKind.Utc);
        var deadline = created.AddSeconds(10);

        var initial = ConfirmationCountdownCalculator.Compute(TimeSpan.FromSeconds(10), deadline, created);
        Assert.Equal(1d, initial.Ratio, 8);
        Assert.Equal(10, initial.RemainingSeconds);
        Assert.False(initial.IsUrgent);
        Assert.False(initial.IsExpired);

        var halfway = ConfirmationCountdownCalculator.Compute(
            TimeSpan.FromSeconds(10), deadline, created.AddSeconds(5));
        Assert.Equal(0.5d, halfway.Ratio, 8);
        Assert.Equal(5, halfway.RemainingSeconds);
        Assert.True(halfway.IsUrgent);

        var finalSecond = ConfirmationCountdownCalculator.Compute(
            TimeSpan.FromSeconds(10), deadline, created.AddSeconds(9.2));
        Assert.Equal(0.08d, finalSecond.Ratio, 6);
        Assert.Equal(1, finalSecond.RemainingSeconds);
        Assert.True(finalSecond.IsUrgent);
    }

    [Fact]
    public void Calculator_DeadlineAndInvalidDurationClampToSafeExpiredState()
    {
        var created = new DateTime(2026, 8, 26, 0, 0, 0, DateTimeKind.Utc);
        var deadline = created.AddSeconds(10);

        var atDeadline = ConfirmationCountdownCalculator.Compute(
            TimeSpan.FromSeconds(10), deadline, deadline);
        var afterDeadline = ConfirmationCountdownCalculator.Compute(
            TimeSpan.FromSeconds(10), deadline, deadline.AddHours(1));
        var invalid = ConfirmationCountdownCalculator.Compute(
            TimeSpan.Zero, deadline, created);

        foreach (var snapshot in new[] { atDeadline, afterDeadline, invalid })
        {
            Assert.Equal(0d, snapshot.Ratio);
            Assert.Equal(0, snapshot.RemainingSeconds);
            Assert.False(snapshot.IsUrgent);
            Assert.True(snapshot.IsExpired);
        }
    }

    [Fact]
    public void Calculator_ClampsEarlyAndVeryLargeClockObservations()
    {
        var created = new DateTime(2026, 8, 26, 0, 0, 0, DateTimeKind.Utc);
        var deadline = created.AddSeconds(30);

        var beforeStart = ConfirmationCountdownCalculator.Compute(
            TimeSpan.FromSeconds(30), deadline, created.AddMinutes(-1));
        Assert.Equal(1d, beforeStart.Ratio, 8);
        Assert.Equal(30, beforeStart.RemainingSeconds);

        var veryLarge = ConfirmationCountdownCalculator.Compute(
            TimeSpan.FromDays(3_000_000), DateTime.MaxValue, DateTime.MinValue);
        Assert.Equal(1d, veryLarge.Ratio, 8);
        Assert.Equal(int.MaxValue, veryLarge.RemainingSeconds);
        Assert.False(veryLarge.IsExpired);
    }

    [Fact]
    public void Calculator_RecomputesAfterDelayedTimerWithoutAccumulatedDrift()
    {
        var created = new DateTime(2026, 8, 26, 0, 0, 0, DateTimeKind.Utc);
        var deadline = created.AddSeconds(20);

        var beforeDelay = ConfirmationCountdownCalculator.Compute(
            TimeSpan.FromSeconds(20), deadline, created.AddSeconds(4));
        var afterDelay = ConfirmationCountdownCalculator.Compute(
            TimeSpan.FromSeconds(20), deadline, created.AddSeconds(13));

        Assert.Equal(0.8d, beforeDelay.Ratio, 8);
        Assert.Equal(7, afterDelay.RemainingSeconds);
        Assert.Equal(0.35d, afterDelay.Ratio, 8);
    }

    [Theory]
    [InlineData(96, 52)]
    [InlineData(120, 65)]
    [InlineData(144, 78)]
    [InlineData(192, 104)]
    public void Ring_ScalesFixedLogicalDiameterAcrossDpi(int dpi, int expected)
    {
        Assert.Equal(expected, ConfirmationCountdownRing.ScaleLogicalSize(
            ConfirmationCountdownRing.LogicalDiameter, dpi));

        var client = new Size(expected, expected);
        var bounds = ConfirmationCountdownRing.ComputePaintBounds(client, 4f * dpi / 96f);
        Assert.True(bounds.Left >= 0);
        Assert.True(bounds.Top >= 0);
        Assert.True(bounds.Right <= client.Width);
        Assert.True(bounds.Bottom <= client.Height);
        Assert.True(ConfirmationCountdownRing.ComputeTextBounds(client).Contains(
            new Point(expected / 2, expected / 2)));
    }

    [Fact]
    public void Palette_ProvidesReadableNormalUrgentAndHighContrastRingColors()
    {
        Assert.Equal(ConfirmationThemePalette.Light.ApproveBackground, ConfirmationThemePalette.Light.CountdownArc);
        Assert.Equal(ConfirmationThemePalette.Light.ErrorText, ConfirmationThemePalette.Light.CountdownUrgentArc);
        Assert.Equal(ConfirmationThemePalette.Dark.ApproveBackground, ConfirmationThemePalette.Dark.CountdownArc);
        Assert.Equal(ConfirmationThemePalette.Dark.ErrorText, ConfirmationThemePalette.Dark.CountdownUrgentArc);
        Assert.Equal(SystemColors.Highlight, ConfirmationThemePalette.HighContrast.CountdownArc);
        Assert.Equal(SystemColors.WindowText, ConfirmationThemePalette.HighContrast.CountdownText);
        Assert.Equal(SystemColors.WindowText, ConfirmationThemePalette.HighContrast.CountdownTrack);
    }

    [Fact]
    public void Form_RingIsFixedNonInteractiveAndNearIndependentConfirmButton()
    {
        RunOnSta(() =>
        {
            var created = DateTime.UtcNow;
            var now = created;
            using var form = new ConfirmationForm(
                CreateItem(created, 30),
                1,
                1,
                utcNowProvider: () => now)
            {
                EnableDelayedForegroundVerification = false
            };

            form.Show();
            Application.DoEvents();

            var ring = form.CountdownRingBoundsForTests;
            var approve = form.ApproveButtonBoundsForTests;
            var reject = form.RejectButtonBoundsForTests;
            var client = new Rectangle(Point.Empty, form.ClientSize);

            Assert.False(form.CountdownRingEnabledForTests);
            Assert.False(form.CountdownRingTabStopForTests);
            Assert.Equal(AccessibleRole.ProgressBar, form.CountdownRingAccessibleRoleForTests);
            Assert.NotEmpty(form.CountdownRingAccessibleNameForTests);
            Assert.True(client.Contains(ring));
            Assert.True(client.Contains(approve));
            Assert.True(client.Contains(reject));
            Assert.False(ring.IntersectsWith(approve));
            Assert.False(ring.IntersectsWith(reject));
            Assert.Equal(form.DefaultActionForTests, form.RejectButtonForTests);

            form.CloseWithoutResult();
        });
    }

    [Fact]
    public void Form_AbsoluteRefreshUpdatesRingUrgencyAndTimeoutWithoutResettingDeadline()
    {
        RunOnSta(() =>
        {
            var created = DateTime.UtcNow;
            var now = created;
            using var form = new ConfirmationForm(
                CreateItem(created, 12),
                1,
                1,
                utcNowProvider: () => now)
            {
                EnableDelayedForegroundVerification = false
            };

            form.Show();
            Application.DoEvents();
            Assert.Equal(1d, form.CountdownRingRatioForTests, 2);
            Assert.Equal(12, form.CountdownRingSecondsForTests);
            Assert.False(form.CountdownRingUrgentForTests);

            now = created.AddSeconds(7.1);
            form.RefreshCountdownForTests();
            Assert.Equal(0.4083333333d, form.CountdownRingRatioForTests, 6);
            Assert.Equal(5, form.CountdownRingSecondsForTests);
            Assert.True(form.CountdownRingUrgentForTests);
            Assert.Contains("5", form.TimeoutTextForTests);

            now = created.AddSeconds(12.001);
            form.RefreshCountdownForTests();
            Assert.Equal(0d, form.CountdownRingRatioForTests);
            Assert.Equal(0, form.CountdownRingSecondsForTests);
            Assert.True(form.CountdownRingExpiredForTests);
            Assert.False(form.ApproveButtonEnabledForTests);
            Assert.False(form.CountdownTimerEnabledForTests);

            form.CloseWithoutResult();
        });
    }

    private static PendingConfirmationItem CreateItem(DateTime created, int timeoutSeconds)
    {
        var presentation = new RecordingConfirmationPresentation
        {
            Summary = new RecordingRequestSummary
            {
                Mode = "video",
                Source = "display: primary",
                Audio = "No audio",
                AudioSourceKind = "none",
                Duration = "Manual stop",
                CountdownSeconds = 0,
                Output = "C:\\AgentRecorder\\countdown-preview.mp4"
            },
            RecordingId = "countdown-test-recording",
            ConfirmationId = "countdown-test-confirmation",
            TimeoutSeconds = timeoutSeconds,
            CreatedAtUtc = created,
            ExpiresAtUtc = created.AddSeconds(timeoutSeconds),
            SourceType = "display",
            SourceTitle = "Countdown test",
            SourceApplication = "Agent Recorder",
            CaptureSemantics = "window_surface",
            PreviewSemantics = "DWM window preview",
            PlannedBackend = "test",
            OutputKind = "mp4_file"
        };

        return new PendingConfirmationItem(presentation, _ => { });
    }

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
}
