using System.Drawing;
using System.Windows.Forms;
using AgentRecorder.App;
using AgentRecorder.Core.Automation;
using AgentRecorder.Infrastructure;
using Xunit;

namespace AgentRecorder.Tests;

[Collection("HeadlessHostIntegration")]
public sealed class RecurringLeaseApprovalFormTests
{
    [Theory]
    [InlineData(UiLanguage.EnUs)]
    [InlineData(UiLanguage.ZhCn)]
    public void BilingualCopyIncludesRecurringScopeLimitsAndFixedPolicies(UiLanguage language)
    {
        using var form = new RecurringLeaseApprovalForm(Details(), new FakePreview(), new UiTextProvider(language));
        Assert.DoesNotContain("RecurringLeaseApproval_", form.Text + form.DetailsTextForTests + form.KeyboardTextForTests);
        foreach (var policy in new[] { "No audio", "Natural wake only", "Interactive desktop required", "Fail if output exists" })
            Assert.Contains(policy, form.DetailsTextForTests);
        Assert.Contains("2026-01-01", form.DetailsTextForTests); Assert.Contains("2026-01-14", form.DetailsTextForTests);
        Assert.Contains("09:30:00", form.DetailsTextForTests); Assert.Contains("China Standard Time", form.DetailsTextForTests);
        Assert.Contains("300", form.DetailsTextForTests); Assert.Contains("60", form.DetailsTextForTests);
        Assert.Contains(language == UiLanguage.EnUs ? "Monday" : "星期一", form.DetailsTextForTests);
        Assert.Contains(language == UiLanguage.EnUs ? "Weekly" : "每周", form.DetailsTextForTests);
        Assert.True(form.SafeDefaultsForTests);
    }

    [Fact]
    public void PreviewUsesFrozenAbsoluteCoordinatesAndDisposesInMemoryImage()
    {
        var preview = new FakePreview();
        using var form = new RecurringLeaseApprovalForm(Details(), preview, new UiTextProvider(UiLanguage.EnUs));
        Assert.Equal(new Rectangle(110, 220, 640, 480), preview.Bounds);
        Assert.Equal(preview.Bounds, form.PreviewBoundsForTests);
        Assert.True(form.HasPreviewForTests); Assert.Empty(form.FallbackTextForTests);
        form.Dispose(); Assert.False(form.HasPreviewForTests); Assert.True(form.PreviewDisposedForTests);
    }

    [Theory]
    [InlineData(UiLanguage.EnUs)]
    [InlineData(UiLanguage.ZhCn)]
    public void PreviewFallbackAndLongTextsRemainScrollableAtMinimumAndHighDpiScale(UiLanguage language)
    {
        var path = "D:\\" + new string('a', 800);
        var longZone = RecurringPlanSchedule.CreateWeekly("Central Brazilian Standard Time", new(2026, 1, 1), new(2026, 1, 14), new(9, 30),
            new[] { DayOfWeek.Monday }, 2, TimeSpan.FromSeconds(30), TimeSpan.FromMinutes(5));
        using var form = new RecurringLeaseApprovalForm(Details() with { OutputDirectory = path, StableDisplayFingerprint = new string('b', 500), Schedule = longZone },
            new FakePreview { Fail = true }, new UiTextProvider(language));
        Assert.False(form.HasPreviewForTests); Assert.NotEmpty(form.FallbackTextForTests);
        Assert.DoesNotContain("RecurringLeaseApproval_", form.FallbackTextForTests);
        Assert.Contains(path, form.DetailsTextForTests); Assert.Equal(ScrollBars.Both, form.DetailsScrollBarsForTests);
        Assert.Contains("Central Brazilian Standard Time", form.DetailsTextForTests);
        Assert.Equal(AutoScaleMode.Dpi, form.AutoScaleMode); Assert.Equal(FormBorderStyle.Sizable, form.FormBorderStyle);
        form.Size = form.MinimumSize; form.PerformLayout();
        Assert.True(form.ContentScrollsForTests); Assert.True(form.DetailsWithinScrollForTests);
        Assert.True(form.LayoutDoesNotOverlapForTests);
        form.Scale(new SizeF(2, 2)); form.PerformLayout();
        Assert.True(form.ContentScrollsForTests); Assert.True(form.DetailsWithinScrollForTests);
        Assert.True(form.LayoutDoesNotOverlapForTests);
        Assert.Contains(path, form.DetailsTextForTests);
    }

    [Theory]
    [InlineData(Keys.Enter)]
    [InlineData(Keys.Escape)]
    public void EnterAndEscapeRejectEvenWhenApprovalButtonIsFocused(Keys key)
    {
        using var form = new RecurringLeaseApprovalForm(Details(), new FakePreview(), new UiTextProvider(UiLanguage.EnUs));
        form.KeyboardForTests(key);
        Assert.Equal(RecurringLeaseApprovalResult.Rejected, form.Result);
    }

    [Theory]
    [InlineData("Approved")]
    [InlineData("Rejected")]
    [InlineData("TimedOut")]
    [InlineData("HostShutdown")]
    public void ApprovalResultsRemainDistinctAndFirstDecisionWins(string result)
    {
        using var form = new RecurringLeaseApprovalForm(Details(), new FakePreview(), new UiTextProvider(UiLanguage.EnUs));
        var expected = Enum.Parse<RecurringLeaseApprovalResult>(result);
        form.DecideForTests(expected); form.DecideForTests(RecurringLeaseApprovalResult.Approved);
        Assert.Equal(expected, form.Result);
    }

    [Fact]
    public void WindowCloseDefaultsToRejection()
    {
        using var form = new RecurringLeaseApprovalForm(Details(), new FakePreview(), new UiTextProvider(UiLanguage.EnUs));
        form.Close(); Assert.Equal(RecurringLeaseApprovalResult.Rejected, form.Result);
    }

    private static RecurringLeaseApprovalDetails Details() => new("stable-display", new(100, 200, 1920, 1080), new(10, 20, 640, 480),
        96, 96, 1920, 1080, AuthorizedDisplayOrientation.Landscape,
        RecurringPlanSchedule.CreateWeekly("China Standard Time", new(2026, 1, 1), new(2026, 1, 14), new(9, 30),
            new[] { DayOfWeek.Monday, DayOfWeek.Wednesday }, 4, TimeSpan.FromSeconds(30), TimeSpan.FromMinutes(5)),
        2, TimeSpan.FromSeconds(60), new(2026, 1, 15, 0, 0, 0, TimeSpan.Zero), "D:\\Recordings", "recurring-prefix");

    private sealed class FakePreview : IScreenPreviewProvider
    {
        internal bool Fail;
        internal Rectangle Bounds;
        public Bitmap Capture(ConfirmationCaptureBounds bounds, Size maxSize)
        {
            if (Fail) throw new InvalidOperationException("unavailable");
            Bounds = new(bounds.X, bounds.Y, bounds.Width, bounds.Height); return new Bitmap(32, 16);
        }
    }
}
