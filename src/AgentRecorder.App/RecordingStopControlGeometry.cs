using System.Drawing;
using System.Windows.Forms;
using AgentRecorder.Infrastructure;

namespace AgentRecorder.App;

/// <summary>
/// Immutable description of the on-screen location of a recording stop control.
/// </summary>
internal sealed record RecordingStopControlBounds(int X, int Y, int Width, int Height);

/// <summary>
/// Layout helpers for determining the DPI-aware preferred size of the floating stop-control
/// button from the actual stop/stopping text for the current UI language.
/// </summary>
internal static class RecordingStopControlLayout
{
    public static Padding ButtonPadding { get; } = new Padding(12, 6, 12, 6);

    /// <summary>
    /// Extra logical-pixel safety inset on each horizontal side at 96 DPI.
    /// Accounts for CJK fallback overhang, focus cue, glyph overhang and rounding.
    /// Set to 14 so the final logical width clearly exceeds the legacy 102 px baseline
    /// after PerMonitorV2 scaling (e.g. 125 % -> ~138 px, 150 % -> ~165 px).
    /// </summary>
    public const int HorizontalSafetyInsetLogical = 14;

    /// <summary>
    /// Extra logical-pixel safety inset on each vertical side at 96 DPI.
    /// </summary>
    public const int VerticalSafetyInsetLogical = 4;

    /// <summary>
    /// Measures the preferred size using a temporary button on the target monitor. This is the
    /// production path: the temporary control is created with its bounds on the target display so
    /// its <see cref="Control.DeviceDpi"/> matches the real PerMonitorV2 context of the recording.
    /// </summary>
    public static Size MeasurePreferredSize(IUiTextProvider text, Font font, Rectangle targetMonitorBounds)
    {
        using var host = new DpiHost(targetMonitorBounds);
        host.EnsureHandle();
        return MeasureOnHost(text, font, host);
    }

    /// <summary>
    /// Test-only overload that computes the preferred size for a specific target DPI using a
    /// device context at that DPI. The production path uses <see cref="Button.GetPreferredSize"/>
    /// on a control whose handle lives on the target monitor; that path cannot be exercised on a
    /// single-monitor test machine because WinForms derives the scaling from the monitor DPI.
    /// This seam keeps the production 8 pt font and scales only the text measurement and safety
    /// insets by the supplied DPI, giving a deterministic lower-bound size for the 96-192 DPI matrix.
    /// </summary>
    internal static Size MeasurePreferredSize(IUiTextProvider text, Font font, Rectangle targetMonitorBounds, int testDpi)
    {
        float scale = testDpi / 96f;

        var stopText = text.Get("StopControl_Button_Stop");
        var stoppingText = text.Get("StopControl_Button_Stopping");

        // Measure at the current process DPI so we can scale the entire result to the
        // requested test DPI. This avoids relying on a Graphics object's DPI being honoured
        // by TextRenderer and keeps the matrix deterministic across test machines.
        var stopSize = TextRenderer.MeasureText(stopText, font, Size.Empty, TextFormatFlags.SingleLine);
        var stoppingSize = TextRenderer.MeasureText(stoppingText, font, Size.Empty, TextFormatFlags.SingleLine);

        int width = Math.Max(stopSize.Width, stoppingSize.Width);
        int height = Math.Max(stopSize.Height, stoppingSize.Height);

        int horizontalInset = (int)Math.Ceiling(HorizontalSafetyInsetLogical * scale);
        int verticalInset = (int)Math.Ceiling(VerticalSafetyInsetLogical * scale);

        width = (int)Math.Ceiling(width * scale);
        height = (int)Math.Ceiling(height * scale);

        width += (int)Math.Ceiling(ButtonPadding.Horizontal * scale) + horizontalInset * 2;
        height += (int)Math.Ceiling(ButtonPadding.Vertical * scale) + verticalInset * 2;

        return new Size(
            Math.Max(RecordingStopControlGeometry.DefaultButtonWidth, width),
            Math.Max(RecordingStopControlGeometry.DefaultButtonHeight, height));
    }

    private static Size MeasureOnHost(IUiTextProvider text, Font font, DpiHost host)
    {
        using var tempButton = CreateMeasureButton(font);
        host.Controls.Add(tempButton);

        var stopText = text.Get("StopControl_Button_Stop");
        var stoppingText = text.Get("StopControl_Button_Stopping");

        tempButton.Text = stopText;
        var stopSize = tempButton.GetPreferredSize(Size.Empty);
        tempButton.Text = stoppingText;
        var stoppingSize = tempButton.GetPreferredSize(Size.Empty);

        int width = Math.Max(stopSize.Width, stoppingSize.Width);
        int height = Math.Max(stopSize.Height, stoppingSize.Height);

        float dpiScale = host.DeviceDpi / 96f;
        int horizontalInset = (int)Math.Ceiling(HorizontalSafetyInsetLogical * dpiScale);
        int verticalInset = (int)Math.Ceiling(VerticalSafetyInsetLogical * dpiScale);

        width += horizontalInset * 2;
        height += verticalInset * 2;

        return new Size(
            Math.Max(RecordingStopControlGeometry.DefaultButtonWidth, width),
            Math.Max(RecordingStopControlGeometry.DefaultButtonHeight, height));
    }

    /// <summary>
    /// Legacy overload kept for callers that already have a live control on the correct monitor.
    /// New code should prefer the target-monitor overload.
    /// </summary>
    public static Size MeasurePreferredSize(IUiTextProvider text, Font font, Control? dpiSource = null)
    {
        var scale = GetDpiScale(dpiSource);
        return MeasurePreferredSize(text, font, scale);
    }

    internal static Size MeasurePreferredSize(IUiTextProvider text, Font font, float dpiScale)
    {
        var stopText = text.Get("StopControl_Button_Stop");
        var stoppingText = text.Get("StopControl_Button_Stopping");

        using var tempButton = CreateMeasureButton(font);
        tempButton.Text = stopText;
        var stopSize = tempButton.GetPreferredSize(Size.Empty);
        tempButton.Text = stoppingText;
        var stoppingSize = tempButton.GetPreferredSize(Size.Empty);

        int width = Math.Max(stopSize.Width, stoppingSize.Width);
        int height = Math.Max(stopSize.Height, stoppingSize.Height);

        int horizontalInset = (int)Math.Ceiling(HorizontalSafetyInsetLogical * dpiScale);
        int verticalInset = (int)Math.Ceiling(VerticalSafetyInsetLogical * dpiScale);

        width += horizontalInset * 2;
        height += verticalInset * 2;

        return new Size(
            Math.Max(RecordingStopControlGeometry.DefaultButtonWidth, width),
            Math.Max(RecordingStopControlGeometry.DefaultButtonHeight, height));
    }

    /// <summary>
    /// Returns the content rectangle inside a production button that is guaranteed to be
    /// available for text rendering after removing Padding and the DPI-aware safety inset.
    /// </summary>
    public static Rectangle GetContentSafeRectangle(Button button)
    {
        var scale = GetDpiScale(button);
        return GetContentSafeRectangle(button, scale);
    }

    internal static Rectangle GetContentSafeRectangle(Button button, float dpiScale)
    {
        var client = button.ClientRectangle;
        var pad = button.Padding;

        int horizontalInset = (int)Math.Ceiling(HorizontalSafetyInsetLogical * dpiScale);
        int verticalInset = (int)Math.Ceiling(VerticalSafetyInsetLogical * dpiScale);

        int left = client.X + pad.Left + horizontalInset;
        int top = client.Y + pad.Top + verticalInset;
        int right = Math.Max(left, client.Right - pad.Right - horizontalInset);
        int bottom = Math.Max(top, client.Bottom - pad.Bottom - verticalInset);

        return new Rectangle(left, top, right - left, bottom - top);
    }

    private static Button CreateMeasureButton(Font font)
    {
        var button = new Button
        {
            Font = font,
            FlatStyle = FlatStyle.Flat,
            Padding = ButtonPadding,
            UseMnemonic = false,
            TextAlign = ContentAlignment.MiddleCenter
        };
        button.FlatAppearance.BorderSize = 0;
        return button;
    }

    private static float GetDpiScale(Control? control)
    {
        int dpi;
        try
        {
            dpi = control?.DeviceDpi ?? GetSystemDpi();
        }
        catch
        {
            dpi = 96;
        }
        return dpi / 96f;
    }

    private static int GetSystemDpi()
    {
        using var temp = new Control();
        return temp.DeviceDpi;
    }

    /// <summary>
    /// A hidden control created on the target monitor so that its <see cref="Control.DeviceDpi"/>
    /// reflects the real PerMonitorV2 DPI of that display. Child controls added after the handle
    /// is created inherit this DPI for measurement.
    /// </summary>
    private sealed class DpiHost : Control
    {
        public DpiHost(Rectangle bounds)
        {
            Bounds = bounds;
            Visible = false;
        }

        public void EnsureHandle()
        {
            if (!IsHandleCreated)
                CreateHandle();
        }
    }
}

/// <summary>
/// Geometry helpers for placing the floating stop-control button next to a recording region.
/// All core calculations are pure and receive an explicit virtual screen rectangle so tests
/// can inject deterministic display configurations. Production convenience overloads read
/// <see cref="SystemInformation.VirtualScreen"/>.
/// </summary>
internal static class RecordingStopControlGeometry
{
    public const int DefaultButtonWidth = 76;
    public const int DefaultButtonHeight = 28;
    public const int OutsideMargin = 4;
    public const int InsideMargin = 4;
    public const int NestedOffset = 32;

    private static readonly (int dx, int dy)[] CollisionSearchDirections =
    {
        (0, 1),   // below
        (0, -1),  // above
        (1, 0),   // right
        (-1, 0),  // left
        (1, 1),   // below-right
        (1, -1),  // above-right
        (-1, 1),  // below-left
        (-1, -1)  // above-left
    };

    /// <summary>
    /// Computes the stop-control bounds for a recording using the current virtual screen.
    /// </summary>
    public static RecordingStopControlBounds ComputeBounds(
        RecordingIndicatorBounds recordingBounds,
        Size controlSize,
        string? nestedRole)
    {
        return ComputeBounds(recordingBounds, controlSize, nestedRole, SystemInformation.VirtualScreen);
    }

    /// <summary>
    /// Pure overload that uses the supplied virtual screen rectangle.
    /// Strategy:
    /// 1. Prefer the top-right corner outside the recording area.
    /// 2. Fall back to the top-right corner inside the recording area if outside space is unavailable.
    /// 3. Clamp to the virtual screen.
    /// 4. For nested inner recordings, place the button in a stable non-overlapping position
    ///    relative to the outer button, preferring below, then above, then left, then right.
    /// </summary>
    public static RecordingStopControlBounds ComputeBounds(
        RecordingIndicatorBounds recordingBounds,
        Size controlSize,
        string? nestedRole,
        Rectangle virtualScreen)
    {
        return ComputeBounds(recordingBounds, controlSize, nestedRole, virtualScreen, parentBounds: null, mode: CaptureVisibilityMode.ExcludeFromCapture);
    }

    /// <summary>
    /// Computes stop-control bounds with explicit parent bounds and capture-visibility mode.
    /// In parent-visible mode the button must be placed strictly outside the inner capture
    /// rectangle and strictly inside the parent capture rectangle.
    /// </summary>
    public static RecordingStopControlBounds ComputeBounds(
        RecordingIndicatorBounds recordingBounds,
        Size controlSize,
        string? nestedRole,
        Rectangle virtualScreen,
        RecordingIndicatorBounds? parentBounds,
        CaptureVisibilityMode mode)
    {
        if (mode == CaptureVisibilityMode.ParentVisible && parentBounds != null &&
            string.Equals(nestedRole, "inner", StringComparison.OrdinalIgnoreCase))
        {
            var preferred = ComputeParentVisiblePreferredBounds(recordingBounds, parentBounds, controlSize);
            if (preferred != null)
            {
                return preferred;
            }
        }

        var outer = ComputeBaseBounds(recordingBounds, controlSize, virtualScreen);

        if (string.Equals(nestedRole, "inner", StringComparison.OrdinalIgnoreCase))
        {
            return ComputeInnerBounds(outer, controlSize, virtualScreen);
        }

        return outer;
    }

    private static RecordingStopControlBounds? ComputeParentVisiblePreferredBounds(
        RecordingIndicatorBounds inner,
        RecordingIndicatorBounds parent,
        Size controlSize)
    {
        // Candidate margins: top, right, bottom, left of the inner rectangle, inside the parent.
        // Prefer right, then bottom, then left, then top for consistency with the non-parent-visible path.
        var candidates = new (int x, int y, int availableWidth, int availableHeight, string side)[]
        {
            (inner.X + inner.Width + OutsideMargin, inner.Y, parent.X + parent.Width - (inner.X + inner.Width) - OutsideMargin, inner.Height, "right"),
            (inner.X, inner.Y + inner.Height + OutsideMargin, inner.Width, parent.Y + parent.Height - (inner.Y + inner.Height) - OutsideMargin, "bottom"),
            (parent.X, inner.Y, inner.X - parent.X - OutsideMargin, inner.Height, "left"),
            (inner.X, parent.Y, inner.Width, inner.Y - parent.Y - OutsideMargin, "top")
        };

        foreach (var (x, y, availableWidth, availableHeight, side) in candidates)
        {
            if (availableWidth >= controlSize.Width && availableHeight >= controlSize.Height)
            {
                return new RecordingStopControlBounds(x, y, controlSize.Width, controlSize.Height);
            }
        }

        return null;
    }

    /// <summary>
    /// Convenience overload using the default button size and the current virtual screen.
    /// </summary>
    public static RecordingStopControlBounds ComputeBounds(
        RecordingIndicatorBounds recordingBounds,
        string? nestedRole)
    {
        return ComputeBounds(recordingBounds, new Size(DefaultButtonWidth, DefaultButtonHeight), nestedRole, SystemInformation.VirtualScreen);
    }

    /// <summary>
    /// Convenience overload using the default button size and an explicit virtual screen.
    /// </summary>
    public static RecordingStopControlBounds ComputeBounds(
        RecordingIndicatorBounds recordingBounds,
        string? nestedRole,
        Rectangle virtualScreen)
    {
        return ComputeBounds(recordingBounds, new Size(DefaultButtonWidth, DefaultButtonHeight), nestedRole, virtualScreen);
    }

    /// <summary>
    /// Resolves overlap between the preferred stop-control bounds and a set of already-occupied bounds.
    /// If <paramref name="preferred"/> does not intersect any occupied bounds and lies inside the virtual
    /// screen, it is returned unchanged. Otherwise a stable ring search is performed around preferred
    /// using <c>width + OutsideMargin</c> and <c>height + OutsideMargin</c> as grid steps.
    /// This overload permits a last-resort clamped position when no valid slot exists, keeping the
    /// ordinary exclude-mode path robust on tiny or crowded screens.
    /// </summary>
    public static RecordingStopControlBounds ResolveCollision(
        RecordingStopControlBounds preferred,
        Size controlSize,
        Rectangle virtualScreen,
        IEnumerable<RecordingStopControlBounds> occupiedBounds)
    {
        var occupied = occupiedBounds.ToList();

        if (IsValid(preferred, virtualScreen, occupied, null, null))
        {
            return preferred;
        }

        if (TryFindValidCandidate(preferred, controlSize, virtualScreen, occupied, null, null, out var candidate) && candidate != null)
        {
            return candidate;
        }

        // Last-resort deterministic return: clamp preferred to the virtual screen.
        int clampedX = Math.Max(virtualScreen.X, Math.Min(preferred.X, virtualScreen.Right - controlSize.Width));
        int clampedY = Math.Max(virtualScreen.Y, Math.Min(preferred.Y, virtualScreen.Bottom - controlSize.Height));
        return new RecordingStopControlBounds(clampedX, clampedY, controlSize.Width, controlSize.Height);
    }

    /// <summary>
    /// Resolves overlap while honoring optional geometric constraints. <paramref name="forbiddenZone"/>
    /// defines a region the stop control must not intersect (e.g. the inner capture rectangle in
    /// parent-visible mode). <paramref name="allowedZone"/> defines a region the stop control must be
    /// fully contained within (e.g. the parent outer capture rectangle).
    /// </summary>
    /// <returns>
    /// A valid position when one exists; <c>null</c> when no position satisfies all constraints.
    /// Callers that need a guaranteed non-null result should use the unconstrained overload or handle
    /// the failure by falling back to a different visibility mode.
    /// </returns>
    public static RecordingStopControlBounds? ResolveCollision(
        RecordingStopControlBounds preferred,
        Size controlSize,
        Rectangle virtualScreen,
        IEnumerable<RecordingStopControlBounds> occupiedBounds,
        Rectangle? forbiddenZone,
        Rectangle? allowedZone)
    {
        TryResolveCollision(preferred, controlSize, virtualScreen, occupiedBounds, forbiddenZone, allowedZone, out var bounds);
        return bounds;
    }

    /// <summary>
    /// Attempts to resolve overlap while honoring optional geometric constraints.
    /// </summary>
    /// <returns><c>true</c> if a position satisfying all constraints was found.</returns>
    public static bool TryResolveCollision(
        RecordingStopControlBounds preferred,
        Size controlSize,
        Rectangle virtualScreen,
        IEnumerable<RecordingStopControlBounds> occupiedBounds,
        Rectangle? forbiddenZone,
        Rectangle? allowedZone,
        out RecordingStopControlBounds? bounds)
    {
        var occupied = occupiedBounds.ToList();

        if (IsValid(preferred, virtualScreen, occupied, forbiddenZone, allowedZone))
        {
            bounds = preferred;
            return true;
        }

        if (TryFindValidCandidate(preferred, controlSize, virtualScreen, occupied, forbiddenZone, allowedZone, out bounds) && bounds != null)
        {
            return true;
        }

        bounds = null;
        return false;
    }

    private static bool TryFindValidCandidate(
        RecordingStopControlBounds preferred,
        Size controlSize,
        Rectangle virtualScreen,
        List<RecordingStopControlBounds> occupied,
        Rectangle? forbiddenZone,
        Rectangle? allowedZone,
        out RecordingStopControlBounds? candidate)
    {
        int stepX = controlSize.Width + OutsideMargin;
        int stepY = controlSize.Height + OutsideMargin;

        int maxRingsX = Math.Max(1, virtualScreen.Width / stepX + 2);
        int maxRingsY = Math.Max(1, virtualScreen.Height / stepY + 2);
        int maxRings = Math.Max(maxRingsX, maxRingsY);

        for (int ring = 1; ring <= maxRings; ring++)
        {
            foreach (var (dx, dy) in CollisionSearchDirections)
            {
                var ringCandidate = new RecordingStopControlBounds(
                    preferred.X + dx * ring * stepX,
                    preferred.Y + dy * ring * stepY,
                    controlSize.Width,
                    controlSize.Height);

                if (IsValid(ringCandidate, virtualScreen, occupied, forbiddenZone, allowedZone))
                {
                    candidate = ringCandidate;
                    return true;
                }
            }
        }

        // Scan the allowed zone or virtual screen for any valid slot.
        var scanArea = allowedZone ?? virtualScreen;
        for (int y = scanArea.Y; y + controlSize.Height <= scanArea.Bottom; y += stepY)
        {
            for (int x = scanArea.X; x + controlSize.Width <= scanArea.Right; x += stepX)
            {
                var fallback = new RecordingStopControlBounds(x, y, controlSize.Width, controlSize.Height);
                if (IsValid(fallback, virtualScreen, occupied, forbiddenZone, allowedZone))
                {
                    candidate = fallback;
                    return true;
                }
            }
        }

        candidate = null;
        return false;
    }

    private static bool IsValid(
        RecordingStopControlBounds candidate,
        Rectangle virtualScreen,
        List<RecordingStopControlBounds> occupied,
        Rectangle? forbiddenZone,
        Rectangle? allowedZone)
    {
        if (!IsInside(candidate, virtualScreen))
            return false;
        if (forbiddenZone.HasValue && candidate.ToRectangle().IntersectsWith(forbiddenZone.Value))
            return false;
        if (allowedZone.HasValue && !allowedZone.Value.Contains(candidate.ToRectangle()))
            return false;
        return !occupied.Any(o => Intersects(candidate, o));
    }

    private static Rectangle ToRectangle(this RecordingStopControlBounds b)
    {
        return new Rectangle(b.X, b.Y, b.Width, b.Height);
    }

    private static RecordingStopControlBounds ComputeBaseBounds(
        RecordingIndicatorBounds recordingBounds,
        Size controlSize,
        Rectangle virtualScreen)
    {
        int outsideX = recordingBounds.X + recordingBounds.Width + OutsideMargin;
        int outsideY = recordingBounds.Y;

        int insideX = recordingBounds.X + recordingBounds.Width - controlSize.Width - InsideMargin;
        int insideY = recordingBounds.Y + InsideMargin;

        int x;
        int y;

        // Prefer outside placement when it fits entirely on the virtual screen.
        if (outsideX + controlSize.Width <= virtualScreen.Right &&
            outsideY + controlSize.Height <= virtualScreen.Bottom &&
            outsideX >= virtualScreen.X &&
            outsideY >= virtualScreen.Y)
        {
            x = outsideX;
            y = outsideY;
        }
        else
        {
            x = insideX;
            y = insideY;
        }

        // Clamp to virtual screen.
        x = Math.Max(virtualScreen.X, Math.Min(x, virtualScreen.Right - controlSize.Width));
        y = Math.Max(virtualScreen.Y, Math.Min(y, virtualScreen.Bottom - controlSize.Height));

        return new RecordingStopControlBounds(x, y, controlSize.Width, controlSize.Height);
    }

    private static RecordingStopControlBounds ComputeInnerBounds(
        RecordingStopControlBounds outer,
        Size controlSize,
        Rectangle virtualScreen)
    {
        // Try placements that preserve the visual habit of keeping inner below outer when possible.
        var offsets = new (int dx, int dy)[]
        {
            (0, NestedOffset),                                 // below
            (0, -NestedOffset),                                // above
            (-(controlSize.Width + OutsideMargin), 0),         // left
            (controlSize.Width + OutsideMargin, 0)             // right
        };

        foreach (var (dx, dy) in offsets)
        {
            var candidate = TryPlaceRelative(outer, dx, dy, controlSize, virtualScreen);
            if (candidate != null && !Intersects(outer, candidate))
            {
                return candidate;
            }
        }

        // Degenerate fallback for tiny virtual screens.
        int fallbackX = virtualScreen.X;
        int fallbackY = virtualScreen.Y;

        if (Intersects(outer, new RecordingStopControlBounds(fallbackX, fallbackY, controlSize.Width, controlSize.Height)))
        {
            fallbackX = outer.X + outer.Width;
            fallbackY = outer.Y;
            if (fallbackX + controlSize.Width > virtualScreen.Right)
            {
                fallbackX = outer.X;
                fallbackY = outer.Y + outer.Height;
            }
        }

        fallbackX = Math.Max(virtualScreen.X, Math.Min(fallbackX, virtualScreen.Right - controlSize.Width));
        fallbackY = Math.Max(virtualScreen.Y, Math.Min(fallbackY, virtualScreen.Bottom - controlSize.Height));

        return new RecordingStopControlBounds(fallbackX, fallbackY, controlSize.Width, controlSize.Height);
    }

    private static RecordingStopControlBounds? TryPlaceRelative(
        RecordingStopControlBounds outer,
        int dx,
        int dy,
        Size controlSize,
        Rectangle virtualScreen)
    {
        int x = outer.X + dx;
        int y = outer.Y + dy;

        if (x < virtualScreen.X ||
            y < virtualScreen.Y ||
            x + controlSize.Width > virtualScreen.Right ||
            y + controlSize.Height > virtualScreen.Bottom)
        {
            return null;
        }

        return new RecordingStopControlBounds(x, y, controlSize.Width, controlSize.Height);
    }

    internal static bool Intersects(RecordingStopControlBounds a, RecordingStopControlBounds b)
    {
        return a.X < b.X + b.Width && a.X + a.Width > b.X &&
               a.Y < b.Y + b.Height && a.Y + a.Height > b.Y;
    }

    internal static bool IsInside(RecordingStopControlBounds b, Rectangle virtualScreen)
    {
        return b.X >= virtualScreen.X &&
               b.Y >= virtualScreen.Y &&
               b.X + b.Width <= virtualScreen.Right &&
               b.Y + b.Height <= virtualScreen.Bottom;
    }
}
