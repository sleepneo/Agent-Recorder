using System;
using System.Globalization;

namespace AgentRecorder.Capture;

/// <summary>
/// Computes the output dimensions for display captures. The bundled FFmpeg is
/// old enough that the scale filter must receive concrete even dimensions;
/// relying on force_original_aspect_ratio=decrease can otherwise produce an
/// odd dimension such as 1703x1080.
/// </summary>
internal static class DisplayScaleGeometry
{
    internal const int MaxWidth = 1920;
    internal const int MaxHeight = 1080;

    /// <summary>
    /// Returns the concrete display output dimensions when a filter is needed,
    /// or null when the source can be encoded at its physical dimensions.
    /// The result is always positive, even, and never larger than the source
    /// or the 1920x1080 limit.
    /// </summary>
    internal static (int Width, int Height)? GetOutputSize(int sourceWidth, int sourceHeight)
    {
        ValidatePositive(sourceWidth, sourceHeight);

        if (sourceWidth <= MaxWidth && sourceHeight <= MaxHeight &&
            IsEven(sourceWidth) && IsEven(sourceHeight))
        {
            return null;
        }

        var ratio = Math.Min(1.0,
            Math.Min((double)MaxWidth / sourceWidth, (double)MaxHeight / sourceHeight));
        var width = MakePositiveEven((int)Math.Floor(sourceWidth * ratio));
        var height = MakePositiveEven((int)Math.Floor(sourceHeight * ratio));

        if (width > sourceWidth || height > sourceHeight ||
            width > MaxWidth || height > MaxHeight)
        {
            throw new ArgumentException(
                $"Display scale result {width}x{height} exceeds source or maximum dimensions.");
        }

        return (width, height);
    }

    internal static string? BuildFilter(int sourceWidth, int sourceHeight)
    {
        var output = GetOutputSize(sourceWidth, sourceHeight);
        return output.HasValue
            ? $"scale={output.Value.Width.ToString(CultureInfo.InvariantCulture)}:" +
              $"{output.Value.Height.ToString(CultureInfo.InvariantCulture)}"
            : null;
    }

    internal static string? ValidateCaptureBounds(CaptureConfig cfg)
    {
        var (_, _, width, height) = cfg.Bounds;
        if (width <= 0 || height <= 0)
            return "capture bounds width and height must be positive";

        if (string.Equals(cfg.SourceKind, "region", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(cfg.SourceKind, "window", StringComparison.OrdinalIgnoreCase))
        {
            if (!IsEven(width) || !IsEven(height))
            {
                return $"{cfg.SourceKind} bounds must already be even for H.264: {width}x{height}";
            }

            return null;
        }

        try
        {
            _ = GetOutputSize(width, height);
            return null;
        }
        catch (ArgumentException ex)
        {
            return ex.Message;
        }
    }

    internal static void ThrowIfInvalidCaptureBounds(CaptureConfig cfg)
    {
        var error = ValidateCaptureBounds(cfg);
        if (error != null)
            throw new ArgumentException(error, nameof(cfg));
    }

    private static void ValidatePositive(int width, int height)
    {
        if (width <= 0 || height <= 0)
            throw new ArgumentException("Display dimensions must be positive.");
    }

    private static bool IsEven(int value) => (value & 1) == 0;

    private static int MakePositiveEven(int value)
    {
        var even = value & ~1;
        if (even < 2)
            throw new ArgumentException("Display scale cannot produce positive even dimensions.");
        return even;
    }
}
