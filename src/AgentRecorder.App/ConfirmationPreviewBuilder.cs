using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows.Forms;

namespace AgentRecorder.App;

/// <summary>
/// Immutable description of a capture rectangle in virtual screen coordinates.
/// </summary>
internal sealed record CaptureBounds(int X, int Y, int Width, int Height);

/// <summary>
/// Abstraction over screen capture so tests can inject a fake provider.
/// </summary>
internal interface IScreenPreviewProvider
{
    /// <summary>
    /// Captures the given virtual screen bounds and returns a bitmap no larger
    /// than <paramref name="maxSize"/>, preserving aspect ratio and never upscaling.
    /// </summary>
    Bitmap Capture(CaptureBounds bounds, Size maxSize);
}

/// <summary>
/// Default GDI+ screen preview provider. Uses Graphics.CopyFromScreen.
/// Bounds are clamped to the current virtual screen before capture.
/// </summary>
internal sealed class GdiScreenPreviewProvider : IScreenPreviewProvider
{
    public Bitmap Capture(CaptureBounds bounds, Size maxSize)
    {
        var virtualScreen = SystemInformation.VirtualScreen;

        int x = Math.Max(bounds.X, virtualScreen.X);
        int y = Math.Max(bounds.Y, virtualScreen.Y);
        int right = Math.Min(bounds.X + bounds.Width, virtualScreen.X + virtualScreen.Width);
        int bottom = Math.Min(bounds.Y + bounds.Height, virtualScreen.Y + virtualScreen.Height);

        int width = right - x;
        int height = bottom - y;
        if (width <= 0 || height <= 0)
            throw new InvalidOperationException("Capture bounds are completely outside the virtual screen.");

        double scale = Math.Min((double)maxSize.Width / width, (double)maxSize.Height / height);
        if (scale > 1.0) scale = 1.0; // never upscale

        int thumbWidth = Math.Max(1, (int)(width * scale));
        int thumbHeight = Math.Max(1, (int)(height * scale));

        using var source = new Bitmap(width, height);
        using (var g = Graphics.FromImage(source))
        {
            g.CopyFromScreen(new Point(x, y), Point.Empty, new Size(width, height));
        }

        if (thumbWidth == width && thumbHeight == height)
            return new Bitmap(source);

        var thumb = new Bitmap(thumbWidth, thumbHeight);
        using (var g = Graphics.FromImage(thumb))
        {
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.DrawImage(source, 0, 0, thumbWidth, thumbHeight);
        }
        return thumb;
    }
}

/// <summary>
/// Builds a preview bitmap for the confirmation UI from the recording summary.
/// All operations are synchronous, do not write to disk, and never throw.
/// </summary>
internal static class ConfirmationPreviewBuilder
{
    private const string FallbackMessage = "无法生成预览，但仍可根据文本信息确认。";
    private static readonly Color HighlightBorderColor = Color.Gold;
    private const int HighlightBorderWidth = 3;

    /// <summary>
    /// Parses capture_bounds from the summary node. Returns null if missing or invalid.
    /// Never throws for malformed nodes.
    /// </summary>
    public static CaptureBounds? ParseBounds(JsonNode summary)
    {
        try
        {
            var captureBounds = summary["capture_bounds"];
            if (captureBounds == null)
                return null;
            if (captureBounds.GetValueKind() != JsonValueKind.Object)
                return null;

            if (!TryGetInt(captureBounds["x"], out var x)) return null;
            if (!TryGetInt(captureBounds["y"], out var y)) return null;
            if (!TryGetInt(captureBounds["width"], out var w)) return null;
            if (!TryGetInt(captureBounds["height"], out var h)) return null;

            if (w <= 0 || h <= 0)
                return null;

            return new CaptureBounds(x, y, w, h);
        }
        catch
        {
            return null;
        }
    }

    private static bool TryGetInt(JsonNode? node, out int value)
    {
        value = 0;
        if (node == null)
            return false;
        try
        {
            value = node.GetValue<int>();
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Attempts to build a preview bitmap for the given summary.
    /// Returns null and a fallback message if bounds are missing or capture fails.
    /// The returned bitmap has a highlight border drawn on its edges.
    /// </summary>
    public static Bitmap? TryBuildPreview(JsonNode summary, IScreenPreviewProvider provider, Size maxSize, out string fallbackMessage)
    {
        fallbackMessage = string.Empty;

        try
        {
            var bounds = ParseBounds(summary);
            if (bounds == null)
            {
                fallbackMessage = "无法生成预览：未包含录制范围信息。";
                return null;
            }

            var clampedBounds = ClampToVirtualScreen(bounds);
            if (clampedBounds.Width <= 0 || clampedBounds.Height <= 0)
            {
                fallbackMessage = FallbackMessage;
                return null;
            }

            var bitmap = provider.Capture(clampedBounds, maxSize);
            DrawHighlightBorder(bitmap);
            return bitmap;
        }
        catch
        {
            fallbackMessage = FallbackMessage;
            return null;
        }
    }

    /// <summary>
    /// Clamps capture bounds to the current virtual screen so providers do not
    /// receive out-of-screen coordinates.
    /// </summary>
    public static CaptureBounds ClampToVirtualScreen(CaptureBounds bounds)
    {
        var virtualScreen = SystemInformation.VirtualScreen;

        int x = Math.Max(bounds.X, virtualScreen.X);
        int y = Math.Max(bounds.Y, virtualScreen.Y);
        int right = Math.Min(bounds.X + bounds.Width, virtualScreen.X + virtualScreen.Width);
        int bottom = Math.Min(bounds.Y + bounds.Height, virtualScreen.Y + virtualScreen.Height);
        int width = Math.Max(0, right - x);
        int height = Math.Max(0, bottom - y);

        return new CaptureBounds(x, y, width, height);
    }

    private static void DrawHighlightBorder(Bitmap bitmap)
    {
        using var g = Graphics.FromImage(bitmap);
        using var pen = new Pen(HighlightBorderColor, HighlightBorderWidth);
        // Draw inside the image so the border is fully visible.
        float offset = HighlightBorderWidth / 2.0f;
        g.DrawRectangle(pen, offset, offset, bitmap.Width - HighlightBorderWidth, bitmap.Height - HighlightBorderWidth);
    }
}
