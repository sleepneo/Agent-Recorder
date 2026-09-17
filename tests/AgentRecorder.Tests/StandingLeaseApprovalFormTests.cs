using System;
using System.Drawing;
using System.Windows.Forms;
using AgentRecorder.App;
using AgentRecorder.Core.Automation;
using AgentRecorder.Infrastructure;
using Xunit;

namespace AgentRecorder.Tests;

[Collection("HeadlessHostIntegration")]
public sealed class StandingLeaseApprovalFormTests
{
    [Fact]
    public void PreviewUsesAbsolutePhysicalRegionAndIsReleasedWithTheForm()
    {
        var provider = new FakePreviewProvider();
        using var form = new StandingLeaseApprovalForm(
            Details(),
            provider,
            new UiTextProvider(UiLanguage.ZhCn));

        Assert.Equal(new Rectangle(110, 220, 320, 180), provider.LastBounds);
        Assert.Equal(new Rectangle(110, 220, 320, 180), form.AbsolutePreviewBoundsForTests);
        Assert.True(form.HasPreviewImageForTests);
        Assert.Empty(form.PreviewFallbackTextForTests);

        form.Dispose();
        Assert.True(form.PreviewBitmapDisposedForTests);
    }

    [Fact]
    public void ProviderFailureShowsLocalizedFallbackAndFrozenCoordinates()
    {
        using var form = new StandingLeaseApprovalForm(
            Details(outputDirectory: new string('C', 280)),
            new ThrowingPreviewProvider(),
            new UiTextProvider(UiLanguage.EnUs));

        Assert.False(form.HasPreviewImageForTests);
        Assert.Contains("live region preview is unavailable", form.PreviewFallbackTextForTests, StringComparison.Ordinal);
        Assert.Contains("Absolute physical region", form.PreviewBoundsTextForTests, StringComparison.Ordinal);
        Assert.Contains(new string('C', 280), form.DetailsTextForTests, StringComparison.Ordinal);
        Assert.Equal(ScrollBars.Vertical, form.DetailsScrollBarsForTests);
    }

    [Fact]
    public void OnlyExplicitGreenApprovalIsPositiveAndEnterEscapeCloseRemainSafe()
    {
        using var form = new StandingLeaseApprovalForm(Details(), new FakePreviewProvider(), new UiTextProvider(UiLanguage.EnUs));

        Assert.True(form.AcceptButtonIsRejectForTests);
        Assert.True(form.CancelButtonIsRejectForTests);
        Assert.Equal(DialogResult.OK, form.ApproveButtonResultForTests);
        Assert.Equal(DialogResult.Cancel, form.RejectButtonResultForTests);
        Assert.Contains("only an explicit click", form.KeyboardTextForTests, StringComparison.Ordinal);
        Assert.DoesNotContain("Enter = approve", form.KeyboardTextForTests, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ClosingTheApprovalSurfaceDoesNotSetAnApprovalResult()
    {
        var form = new StandingLeaseApprovalForm(Details(), new FakePreviewProvider(), new UiTextProvider(UiLanguage.EnUs));
        form.Close();
        Assert.NotEqual(DialogResult.OK, form.DialogResult);
        form.Dispose();
    }

    [Fact]
    public void ChineseAndEnglishApprovalCopyAreCompleteAndDistinct()
    {
        using var chinese = new StandingLeaseApprovalForm(Details(), new FakePreviewProvider(), new UiTextProvider(UiLanguage.ZhCn));
        using var english = new StandingLeaseApprovalForm(Details(), new FakePreviewProvider(), new UiTextProvider(UiLanguage.EnUs));

        Assert.Contains("一次性无人值守", chinese.Text, StringComparison.Ordinal);
        Assert.Contains("批准授权", chinese.KeyboardTextForTests, StringComparison.Ordinal);
        Assert.Contains("one-shot unattended", english.Text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("explicit click", english.KeyboardTextForTests, StringComparison.OrdinalIgnoreCase);
    }

    private static StandingLeaseApprovalDetails Details(string? outputDirectory = null) => new(
        "Display 1",
        new AuthorizedPhysicalRectangle(100, 200, 800, 600),
        new AuthorizedPhysicalRectangle(10, 20, 320, 180),
        DateTimeOffset.UtcNow.AddMinutes(1),
        DateTimeOffset.UtcNow.AddMinutes(2),
        DateTimeOffset.UtcNow.AddMinutes(3),
        TimeSpan.FromSeconds(60),
        DateTimeOffset.UtcNow.AddMinutes(10),
        outputDirectory ?? @"C:\Recordings\Agent",
        "one-shot.mp4");

    private sealed class FakePreviewProvider : IScreenPreviewProvider
    {
        public Rectangle LastBounds { get; private set; }

        public Bitmap Capture(ConfirmationCaptureBounds bounds, Size maxSize)
        {
            LastBounds = new Rectangle(bounds.X, bounds.Y, bounds.Width, bounds.Height);
            return new Bitmap(32, 16);
        }
    }

    private sealed class ThrowingPreviewProvider : IScreenPreviewProvider
    {
        public Bitmap Capture(ConfirmationCaptureBounds bounds, Size maxSize) =>
            throw new InvalidOperationException("preview unavailable");
    }
}
