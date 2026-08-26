using System;
using System.Drawing;
using System.Windows.Forms;
using AgentRecorder.App;
using AgentRecorder.Infrastructure;
using Xunit;

namespace AgentRecorder.Tests;

public class ConfirmationPreviewBuilderTests
{
    private sealed class FakeProvider : IScreenPreviewProvider
    {
        public ConfirmationCaptureBounds? LastBounds { get; private set; }
        public Size? LastMaxSize { get; private set; }
        public bool Throw { get; set; }

        public Bitmap Capture(ConfirmationCaptureBounds bounds, Size maxSize)
        {
            if (Throw)
                throw new InvalidOperationException("capture failed");

            LastBounds = bounds;
            LastMaxSize = maxSize;
            return new Bitmap(maxSize.Width, maxSize.Height);
        }
    }

    [Fact]
    public void TypedCaptureBounds_ArePassedToPreviewProvider()
    {
        var provider = new FakeProvider();
        var bounds = new ConfirmationCaptureBounds(100, 200, 1280, 720);

        using var bitmap = ConfirmationPreviewBuilder.TryBuildPreview(
            bounds, provider, new Size(320, 180), out var fallback);

        Assert.NotNull(bitmap);
        Assert.True(bitmap!.Width <= 320);
        Assert.True(bitmap.Height <= 180);
        Assert.Empty(fallback);
        Assert.Equal(bounds, provider.LastBounds);
    }

    [Fact]
    public void MissingBounds_ReturnsFallbackWithoutCallingProvider()
    {
        var provider = new FakeProvider();

        var bitmap = ConfirmationPreviewBuilder.TryBuildPreview(
            null, provider, new Size(320, 180), out var fallback);

        Assert.Null(bitmap);
        Assert.Contains("未包含录制范围信息", fallback);
        Assert.Null(provider.LastBounds);
    }

    [Theory]
    [InlineData(0, 720)]
    [InlineData(1280, 0)]
    [InlineData(-100, 720)]
    [InlineData(1280, -50)]
    public void InvalidTypedBounds_ReturnFallback(int width, int height)
    {
        var provider = new FakeProvider();
        var bitmap = ConfirmationPreviewBuilder.TryBuildPreview(
            new ConfirmationCaptureBounds(0, 0, width, height),
            provider,
            new Size(320, 180),
            out var fallback);

        Assert.Null(bitmap);
        Assert.NotEmpty(fallback);
        Assert.Null(provider.LastBounds);
    }

    [Fact]
    public void TryBuildPreview_WhenProviderThrows_ReturnsFallback()
    {
        var provider = new FakeProvider { Throw = true };

        var bitmap = ConfirmationPreviewBuilder.TryBuildPreview(
            new ConfirmationCaptureBounds(0, 0, 100, 100),
            provider,
            new Size(320, 180),
            out var fallback);

        Assert.Null(bitmap);
        Assert.Contains("无法生成预览，但仍可根据文本信息确认", fallback);
    }

    [Fact]
    public void TryBuildPreview_ClampsBoundsToVirtualScreen()
    {
        var provider = new FakeProvider();
        var virtualScreen = SystemInformation.VirtualScreen;
        var bounds = new ConfirmationCaptureBounds(
            virtualScreen.X + virtualScreen.Width - 50,
            virtualScreen.Y + virtualScreen.Height - 50,
            200,
            200);

        using var bitmap = ConfirmationPreviewBuilder.TryBuildPreview(
            bounds, provider, new Size(320, 180), out var fallback);

        Assert.NotNull(bitmap);
        Assert.NotNull(provider.LastBounds);
        Assert.True(provider.LastBounds!.Width < 200);
        Assert.True(provider.LastBounds.Height < 200);
        Assert.Empty(fallback);
    }
}
