using System;
using System.Drawing;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows.Forms;
using AgentRecorder.App;
using Xunit;

namespace AgentRecorder.Tests;

public class ConfirmationPreviewBuilderTests
{
    private class FakeProvider : IScreenPreviewProvider
    {
        public CaptureBounds? LastBounds { get; private set; }
        public Size? LastMaxSize { get; private set; }
        public bool Throw { get; set; }

        public Bitmap Capture(CaptureBounds bounds, Size maxSize)
        {
            if (Throw)
                throw new InvalidOperationException("capture failed");

            LastBounds = bounds;
            LastMaxSize = maxSize;
            return new Bitmap(maxSize.Width, maxSize.Height);
        }
    }

    private static JsonNode Summary(object captureBounds) =>
        JsonNode.Parse(JsonSerializer.Serialize(new { capture_bounds = captureBounds, source_type = "region" }))!;

    [Fact]
    public void ParseBounds_ValidCaptureBounds_ReturnsBounds()
    {
        var summary = Summary(new { x = 100, y = 200, width = 1280, height = 720 });

        var bounds = ConfirmationPreviewBuilder.ParseBounds(summary);

        Assert.NotNull(bounds);
        Assert.Equal(100, bounds!.X);
        Assert.Equal(200, bounds.Y);
        Assert.Equal(1280, bounds.Width);
        Assert.Equal(720, bounds.Height);
    }

    [Fact]
    public void ParseBounds_MissingBounds_ReturnsNull()
    {
        var summary = JsonNode.Parse(JsonSerializer.Serialize(new { source_type = "region" }))!;

        Assert.Null(ConfirmationPreviewBuilder.ParseBounds(summary));
    }

    [Theory]
    [InlineData(0, 720)]
    [InlineData(1280, 0)]
    [InlineData(-100, 720)]
    [InlineData(1280, -50)]
    public void ParseBounds_ZeroOrNegativeSize_ReturnsNull(int w, int h)
    {
        var summary = Summary(new { x = 0, y = 0, width = w, height = h });

        Assert.Null(ConfirmationPreviewBuilder.ParseBounds(summary));
    }

    [Fact]
    public void TryBuildPreview_WithFakeProvider_ReturnsBitmapWithinMaxSize()
    {
        var provider = new FakeProvider();
        var summary = Summary(new { x = 100, y = 200, width = 1280, height = 720 });

        using var bitmap = ConfirmationPreviewBuilder.TryBuildPreview(
            summary, provider, new Size(320, 180), out var fallback);

        Assert.NotNull(bitmap);
        Assert.True(bitmap!.Width <= 320);
        Assert.True(bitmap.Height <= 180);
        Assert.Empty(fallback);
        Assert.NotNull(provider.LastBounds);
        Assert.Equal(100, provider.LastBounds!.X);
        Assert.Equal(200, provider.LastBounds.Y);
    }

    [Fact]
    public void TryBuildPreview_WhenProviderThrows_ReturnsNullAndFallback()
    {
        var provider = new FakeProvider { Throw = true };
        var summary = Summary(new { x = 0, y = 0, width = 100, height = 100 });

        var bitmap = ConfirmationPreviewBuilder.TryBuildPreview(
            summary, provider, new Size(320, 180), out var fallback);

        Assert.Null(bitmap);
        Assert.Contains("无法生成预览，但仍可根据文本信息确认", fallback);
    }

    [Fact]
    public void ParseBounds_MalformedFieldType_ReturnsNull()
    {
        var summary = Summary(new { x = "oops", y = 200, width = 1280, height = 720 });
        Assert.Null(ConfirmationPreviewBuilder.ParseBounds(summary));

        summary = Summary(new { x = 100, y = 200, width = new { }, height = 720 });
        Assert.Null(ConfirmationPreviewBuilder.ParseBounds(summary));
    }

    [Fact]
    public void ParseBounds_CaptureBoundsArray_ReturnsNull()
    {
        var summary = JsonNode.Parse(JsonSerializer.Serialize(new { capture_bounds = new[] { 1, 2, 3, 4 } }))!;
        Assert.Null(ConfirmationPreviewBuilder.ParseBounds(summary));
    }

    [Fact]
    public void TryBuildPreview_MalformedBounds_DoesNotThrowAndReturnsFallback()
    {
        var provider = new FakeProvider();
        var summary = Summary(new { x = "not-an-int", y = 200, width = 1280, height = 720 });

        var bitmap = ConfirmationPreviewBuilder.TryBuildPreview(
            summary, provider, new Size(320, 180), out var fallback);

        Assert.Null(bitmap);
        Assert.NotEmpty(fallback);
        Assert.Null(provider.LastBounds); // provider should not be called
    }

    [Fact]
    public void TryBuildPreview_ClampsBoundsToVirtualScreen()
    {
        var provider = new FakeProvider();
        var virtualScreen = SystemInformation.VirtualScreen;

        // Request bounds that extend past the right/bottom edge of the virtual screen.
        var summary = Summary(new
        {
            x = virtualScreen.X + virtualScreen.Width - 50,
            y = virtualScreen.Y + virtualScreen.Height - 50,
            width = 200,
            height = 200
        });

        using var bitmap = ConfirmationPreviewBuilder.TryBuildPreview(
            summary, provider, new Size(320, 180), out var fallback);

        Assert.NotNull(bitmap);
        Assert.NotNull(provider.LastBounds);
        Assert.True(provider.LastBounds!.Width < 200);
        Assert.True(provider.LastBounds.Height < 200);
        Assert.Empty(fallback);
    }
}
