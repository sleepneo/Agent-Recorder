using System.Drawing;
using System.Runtime.InteropServices;

namespace AgentRecorder.App;

/// <summary>
/// Injectable preference seam for the small REC border animation. A false value
/// means the indicator must remain static.
/// </summary>
internal interface IRecordingMotionPreference
{
    bool IsAnimationEnabled { get; }
}

/// <summary>
/// Production Windows preference reader. Query failure and high contrast both
/// fail closed to a static indicator. No system setting is changed.
/// </summary>
internal sealed class WindowsRecordingMotionPreference : IRecordingMotionPreference
{
    private const uint SPI_GETCLIENTAREAANIMATION = 0x1042;

    public bool IsAnimationEnabled
    {
        get
        {
            if (System.Windows.Forms.SystemInformation.HighContrast)
                return false;

            try
            {
                int enabled = 0;
                return SystemParametersInfo(SPI_GETCLIENTAREAANIMATION, 0, ref enabled, 0)
                    && enabled != 0;
            }
            catch
            {
                return false;
            }
        }
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SystemParametersInfo(
        uint action,
        uint parameter,
        ref int value,
        uint flags);
}

internal sealed class FixedRecordingMotionPreference : IRecordingMotionPreference
{
    public FixedRecordingMotionPreference(bool enabled) => IsAnimationEnabled = enabled;

    public bool IsAnimationEnabled { get; }
}

/// <summary>
/// Pure, deterministic motion and dirty-region helpers. The curve is a smooth
/// ping-pong with a two-second period; callers supply time and own the timer.
/// </summary>
internal static class RecordingIndicatorMotion
{
    public static readonly TimeSpan Cycle = TimeSpan.FromSeconds(2);
    public const int TimerIntervalMilliseconds = 120;

    public static double PulseAmount(TimeSpan elapsed)
    {
        if (elapsed < TimeSpan.Zero)
            elapsed = TimeSpan.Zero;

        double cycleMilliseconds = Cycle.TotalMilliseconds;
        if (cycleMilliseconds <= 0)
            return 0;

        double position = elapsed.TotalMilliseconds % cycleMilliseconds / cycleMilliseconds;
        // A cosine gives zero velocity at both endpoints and a continuous
        // low -> high -> low ping-pong without a discontinuity at wraparound.
        return 0.5 - 0.5 * Math.Cos(position * Math.PI * 2.0);
    }

    public static Color BlendOpaque(Color low, Color high, double amount)
    {
        amount = Math.Clamp(amount, 0, 1);
        int r = (int)Math.Round(low.R + (high.R - low.R) * amount);
        int g = (int)Math.Round(low.G + (high.G - low.G) * amount);
        int b = (int)Math.Round(low.B + (high.B - low.B) * amount);
        return Color.FromArgb(255, r, g, b);
    }

    public static Rectangle[] ComputeDirtyRegions(
        RecordingIndicatorPresentation presentation,
        Rectangle clientBounds,
        Rectangle labelBounds)
        => ComputeDirtyRegions(presentation, clientBounds, labelBounds, includeBorder: true, includeLabel: true);

    public static Rectangle[] ComputeDirtyRegions(
        RecordingIndicatorPresentation presentation,
        Rectangle clientBounds,
        Rectangle labelBounds,
        bool includeBorder,
        bool includeLabel)
    {
        if (clientBounds.Width <= 0 || clientBounds.Height <= 0)
            return Array.Empty<Rectangle>();

        var regions = new List<Rectangle>();
        if (includeBorder && presentation.Mode == CaptureVisibilityMode.ParentVisible)
        {
            foreach (var border in presentation.BorderRectangles)
            {
                var local = new Rectangle(
                    border.X - presentation.WindowBounds.X,
                    border.Y - presentation.WindowBounds.Y,
                    border.Width,
                    border.Height);
                AddIntersected(regions, local, clientBounds);
            }
        }
        else if (includeBorder)
        {
            int thickness = Math.Min(RecordingIndicatorGeometry.BorderWidth, Math.Min(clientBounds.Width, clientBounds.Height));
            AddIntersected(regions, new Rectangle(clientBounds.Left, clientBounds.Top, clientBounds.Width, thickness), clientBounds);
            AddIntersected(regions, new Rectangle(clientBounds.Left, clientBounds.Bottom - thickness, clientBounds.Width, thickness), clientBounds);
            AddIntersected(regions, new Rectangle(clientBounds.Left, clientBounds.Top, thickness, clientBounds.Height), clientBounds);
            AddIntersected(regions, new Rectangle(clientBounds.Right - thickness, clientBounds.Top, thickness, clientBounds.Height), clientBounds);
        }

        // Keep the label in the invalidation contract so a future visual model
        // can animate the label without broadening this to the full client area.
        if (includeLabel)
            AddIntersected(regions, labelBounds, clientBounds);
        return regions.Distinct().ToArray();
    }

    private static void AddIntersected(List<Rectangle> regions, Rectangle candidate, Rectangle bounds)
    {
        var clipped = Rectangle.Intersect(candidate, bounds);
        if (!clipped.IsEmpty)
            regions.Add(clipped);
    }
}
