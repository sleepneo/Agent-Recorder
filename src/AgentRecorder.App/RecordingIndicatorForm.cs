using System;
using System.Drawing;
using System.Windows.Forms;
using AgentRecorder.Core;
using AgentRecorder.Windows;

namespace AgentRecorder.App;

/// <summary>
/// Immutable description of the recording area shown by the indicator.
/// </summary>
internal sealed record RecordingIndicatorBounds(int X, int Y, int Width, int Height);

/// <summary>
/// Geometry helpers for placing the recording indicator and its label on screen.
/// </summary>
internal static class RecordingIndicatorGeometry
{
    public const int BorderWidth = 4;
    public const int MinIndicatorSize = 32;

    /// <summary>
    /// Attempts to clamp the recording bounds to the virtual screen and enforce
    /// the minimum indicator size. Returns <c>null</c> when the bounds are not
    /// displayable (zero/negative size, completely outside the virtual screen,
    /// or the virtual screen itself has no area).
    /// </summary>
    public static RecordingIndicatorBounds? TryClampToVirtualScreen(RecordingIndicatorBounds bounds)
    {
        if (bounds.Width <= 0 || bounds.Height <= 0)
            return null;

        var vs = SystemInformation.VirtualScreen;
        if (vs.Width <= 0 || vs.Height <= 0)
            return null;

        int left = Math.Max(bounds.X, vs.X);
        int top = Math.Max(bounds.Y, vs.Y);
        int right = Math.Min(bounds.X + bounds.Width, vs.X + vs.Width);
        int bottom = Math.Min(bounds.Y + bounds.Height, vs.Y + vs.Height);

        // No overlap with the virtual screen.
        if (right <= left || bottom <= top)
            return null;

        // Try to expand a small intersection to the minimum size while staying
        // inside the virtual screen. If the virtual screen is smaller than the
        // minimum size, fall back to the full available area.
        if (right - left < MinIndicatorSize)
        {
            int extra = MinIndicatorSize - (right - left);
            int expandLeft = extra / 2;
            int expandRight = extra - expandLeft;

            int newLeft = left - expandLeft;
            int newRight = right + expandRight;

            if (newLeft < vs.X) newLeft = vs.X;
            if (newRight > vs.X + vs.Width) newRight = vs.X + vs.Width;

            if (newRight - newLeft < MinIndicatorSize)
            {
                newLeft = vs.X;
                newRight = vs.X + vs.Width;
            }

            left = newLeft;
            right = newRight;
        }

        if (bottom - top < MinIndicatorSize)
        {
            int extra = MinIndicatorSize - (bottom - top);
            int expandTop = extra / 2;
            int expandBottom = extra - expandTop;

            int newTop = top - expandTop;
            int newBottom = bottom + expandBottom;

            if (newTop < vs.Y) newTop = vs.Y;
            if (newBottom > vs.Y + vs.Height) newBottom = vs.Y + vs.Height;

            if (newBottom - newTop < MinIndicatorSize)
            {
                newTop = vs.Y;
                newBottom = vs.Y + vs.Height;
            }

            top = newTop;
            bottom = newBottom;
        }

        int width = right - left;
        int height = bottom - top;

        if (width <= 0 || height <= 0)
            return null;

        return new RecordingIndicatorBounds(left, top, width, height);
    }

    /// <summary>
    /// Clamps the recording bounds to the virtual screen. Throws when the bounds
    /// cannot be displayed at all; callers that need to handle undisplayable
    /// bounds should use <see cref="TryClampToVirtualScreen"/>.
    /// </summary>
    public static RecordingIndicatorBounds ClampToVirtualScreen(RecordingIndicatorBounds bounds)
    {
        var clamped = TryClampToVirtualScreen(bounds)
            ?? throw new ArgumentException("Recording indicator bounds are not displayable.", nameof(bounds));
        return clamped;
    }

    /// <summary>
    /// Computes a top-left label location that stays inside the indicator bounds.
    /// </summary>
    public static Point ComputeLabelLocation(RecordingIndicatorBounds bounds, Size labelSize)
    {
        int x = bounds.X + BorderWidth + 2;
        int y = bounds.Y + BorderWidth + 2;

        // If label would overflow right/bottom, move it inside.
        var vs = SystemInformation.VirtualScreen;
        int maxRight = Math.Min(bounds.X + bounds.Width, vs.X + vs.Width);
        int maxBottom = Math.Min(bounds.Y + bounds.Height, vs.Y + vs.Height);

        if (x + labelSize.Width > maxRight)
            x = Math.Max(bounds.X, maxRight - labelSize.Width);
        if (y + labelSize.Height > maxBottom)
            y = Math.Max(bounds.Y, maxBottom - labelSize.Height);

        return new Point(x, y);
    }

    /// <summary>
    /// Computes the presentation plan for a recording indicator.
    /// For ordinary recordings and outer nested recordings the plan keeps the window
    /// coincident with the capture bounds and requests display affinity.
    /// For inner recordings with a matching, active, containing parent, the plan may
    /// switch to parent-visible mode: the window is enlarged so that the red border
    /// and REC label render strictly outside the inner capture rectangle but inside
    /// the parent capture rectangle, making the controls visible to the parent capture
    /// while keeping the inner capture clean.
    /// </summary>
    public static RecordingIndicatorPresentation ComputePresentationPlan(
        Recording recording,
        RecordingIndicatorBounds captureBounds,
        Recording? parentRecording,
        Size labelSize,
        Rectangle virtualScreen,
        string? parentFallbackReason = null)
    {
        bool isInner = string.Equals(recording.NestedRole, "inner", StringComparison.OrdinalIgnoreCase);
        if (!isInner)
        {
            return new RecordingIndicatorPresentation(
                CaptureVisibilityMode.ExcludeFromCapture,
                captureBounds,
                captureBounds,
                ParentCaptureBounds(parentRecording),
                Array.Empty<Rectangle>(),
                ComputeLabelBounds(captureBounds, labelSize, virtualScreen),
                DisplayAffinityRequested: true,
                FallbackReason: null);
        }

        RecordingIndicatorBounds? parentBounds = ParentCaptureBounds(parentRecording);

        // Strict parent-visible precondition checks.
        if (parentRecording == null || string.IsNullOrEmpty(parentRecording.Id) || string.IsNullOrEmpty(recording.ParentRecordingId))
        {
            return Exclude(captureBounds, parentBounds, labelSize, virtualScreen, parentFallbackReason ?? "parent_missing");
        }

        if (!string.Equals(recording.ParentRecordingId, parentRecording.Id, StringComparison.Ordinal))
        {
            return Exclude(captureBounds, parentBounds, labelSize, virtualScreen, "parent_id_mismatch");
        }

        if (!string.Equals(parentRecording.NestedRole, "outer", StringComparison.OrdinalIgnoreCase))
        {
            return Exclude(captureBounds, parentBounds, labelSize, virtualScreen, "parent_not_outer");
        }

        if (!string.Equals(recording.NestedSessionId, parentRecording.NestedSessionId, StringComparison.Ordinal))
        {
            return Exclude(captureBounds, parentBounds, labelSize, virtualScreen, "session_mismatch");
        }

        if (parentBounds == null || !Contains(parentBounds, captureBounds))
        {
            return Exclude(captureBounds, parentBounds, labelSize, virtualScreen, "inner_not_contained");
        }

        var plan = TryComputeParentVisiblePlan(
            captureBounds,
            parentBounds,
            labelSize,
            virtualScreen);

        if (plan == null)
        {
            return Exclude(captureBounds, parentBounds, labelSize, virtualScreen, "insufficient_margin");
        }

        return plan;
    }

    /// <summary>
    /// Returns an excluded presentation plan with an explicit fallback reason.
    /// Used when the stop control cannot be placed safely after the indicator plan
    /// was already computed as parent-visible.
    /// </summary>
    public static RecordingIndicatorPresentation ComputeExcludedPlan(
        RecordingIndicatorBounds captureBounds,
        Recording? parentRecording,
        Size labelSize,
        Rectangle virtualScreen,
        string reason)
    {
        return Exclude(captureBounds, ParentCaptureBounds(parentRecording), labelSize, virtualScreen, reason);
    }

    private static RecordingIndicatorBounds? ParentCaptureBounds(Recording? parentRecording)
    {
        if (parentRecording == null)
            return null;
        var pb = parentRecording.Config.Bounds;
        return new RecordingIndicatorBounds(pb.x, pb.y, pb.w, pb.h);
    }

    private static RecordingIndicatorPresentation Exclude(
        RecordingIndicatorBounds captureBounds,
        RecordingIndicatorBounds? parentBounds,
        Size labelSize,
        Rectangle virtualScreen,
        string reason)
    {
        return new RecordingIndicatorPresentation(
            CaptureVisibilityMode.ExcludeFromCapture,
            captureBounds,
            captureBounds,
            parentBounds,
            Array.Empty<Rectangle>(),
            ComputeLabelBounds(captureBounds, labelSize, virtualScreen),
            DisplayAffinityRequested: true,
            FallbackReason: reason);
    }

    private static RecordingIndicatorPresentation? TryComputeParentVisiblePlan(
        RecordingIndicatorBounds inner,
        RecordingIndicatorBounds parent,
        Size labelSize,
        Rectangle virtualScreen,
        int borderWidth = BorderWidth)
    {
        // Margin widths inside the parent but outside the inner frame.
        int topMargin = inner.Y - parent.Y - borderWidth;
        int bottomMargin = parent.Y + parent.Height - (inner.Y + inner.Height) - borderWidth;
        int leftMargin = inner.X - parent.X - borderWidth;
        int rightMargin = parent.X + parent.Width - (inner.X + inner.Width) - borderWidth;

        // Frame rectangles are drawn just outside the inner capture rectangle.
        var topBorder = new Rectangle(inner.X - borderWidth, inner.Y - borderWidth, inner.Width + 2 * borderWidth, borderWidth);
        var bottomBorder = new Rectangle(inner.X - borderWidth, inner.Y + inner.Height, inner.Width + 2 * borderWidth, borderWidth);
        var leftBorder = new Rectangle(inner.X - borderWidth, inner.Y, borderWidth, inner.Height);
        var rightBorder = new Rectangle(inner.X + inner.Width, inner.Y, borderWidth, inner.Height);

        var borders = new[] { topBorder, bottomBorder, leftBorder, rightBorder };

        // Find a side for the label outside the inner rectangle and outside the frame.
        Rectangle? labelRect = null;
        string[] sides = { "top", "right", "bottom", "left" };
        foreach (var side in sides)
        {
            labelRect = TryPlaceLabel(side, inner, parent, labelSize, borderWidth);
            if (labelRect != null)
                break;
        }

        if (labelRect == null)
            return null;

        var label = labelRect.Value;

        // Compute the minimal window bounds that contain all colored pixels.
        int windowLeft = Math.Min(topBorder.Left, Math.Min(leftBorder.Left, label.Left));
        int windowTop = Math.Min(topBorder.Top, Math.Min(leftBorder.Top, label.Top));
        int windowRight = Math.Max(bottomBorder.Right, Math.Max(rightBorder.Right, label.Right));
        int windowBottom = Math.Max(bottomBorder.Bottom, Math.Max(rightBorder.Bottom, label.Bottom));

        var windowBounds = new RecordingIndicatorBounds(
            windowLeft,
            windowTop,
            windowRight - windowLeft,
            windowBottom - windowTop);

        // Clip to parent and virtual screen. Parent containment was already verified,
        // but clipping protects against rounding or unusual virtual-screen layouts.
        windowBounds = Intersect(windowBounds, parent) ?? windowBounds;
        windowBounds = Intersect(windowBounds, virtualScreen) ?? windowBounds;

        // Verify all colored pixels are strictly outside the inner capture rectangle,
        // inside the parent capture rectangle, and inside the virtual screen.
        if (!AllOutside(borders, label, inner))
            return null;
        if (!AllInside(borders, label, parent))
            return null;
        if (!AllInside(borders, label, virtualScreen))
            return null;

        return new RecordingIndicatorPresentation(
            CaptureVisibilityMode.ParentVisible,
            windowBounds,
            inner,
            parent,
            borders,
            label,
            DisplayAffinityRequested: false,
            FallbackReason: null);
    }

    private static Rectangle? TryPlaceLabel(
        string side,
        RecordingIndicatorBounds inner,
        RecordingIndicatorBounds parent,
        Size labelSize,
        int borderWidth)
    {
        int availableWidth;
        int availableHeight;
        int x;
        int y;

        switch (side)
        {
            case "top":
                availableWidth = inner.Width;
                availableHeight = inner.Y - parent.Y - borderWidth;
                if (availableHeight < labelSize.Height || availableWidth < labelSize.Width)
                    return null;
                x = inner.X;
                y = inner.Y - borderWidth - labelSize.Height;
                break;
            case "bottom":
                availableWidth = inner.Width;
                availableHeight = parent.Y + parent.Height - (inner.Y + inner.Height) - borderWidth;
                if (availableHeight < labelSize.Height || availableWidth < labelSize.Width)
                    return null;
                x = inner.X;
                y = inner.Y + inner.Height + borderWidth;
                break;
            case "left":
                availableWidth = inner.X - parent.X - borderWidth;
                availableHeight = inner.Height;
                if (availableWidth < labelSize.Width || availableHeight < labelSize.Height)
                    return null;
                x = parent.X;
                y = inner.Y;
                break;
            case "right":
                availableWidth = parent.X + parent.Width - (inner.X + inner.Width) - borderWidth;
                availableHeight = inner.Height;
                if (availableWidth < labelSize.Width || availableHeight < labelSize.Height)
                    return null;
                x = inner.X + inner.Width + borderWidth;
                y = inner.Y;
                break;
            default:
                return null;
        }

        // The label must be rendered at its full measured size; cropping is not allowed
        // in parent-visible mode because the full REC text must remain readable.
        return new Rectangle(x, y, labelSize.Width, labelSize.Height);
    }

    private static Rectangle ComputeLabelBounds(RecordingIndicatorBounds bounds, Size labelSize, Rectangle virtualScreen)
    {
        var loc = ComputeLabelLocation(bounds, labelSize);
        int width = Math.Min(labelSize.Width, virtualScreen.Right - loc.X);
        int height = Math.Min(labelSize.Height, virtualScreen.Bottom - loc.Y);
        return new Rectangle(loc.X, loc.Y, Math.Max(0, width), Math.Max(0, height));
    }

    private static bool Contains(RecordingIndicatorBounds outer, RecordingIndicatorBounds inner)
    {
        return outer.X <= inner.X &&
               outer.Y <= inner.Y &&
               outer.X + outer.Width >= inner.X + inner.Width &&
               outer.Y + outer.Height >= inner.Y + inner.Height;
    }

    private static RecordingIndicatorBounds? Intersect(RecordingIndicatorBounds bounds, RecordingIndicatorBounds other)
    {
        int left = Math.Max(bounds.X, other.X);
        int top = Math.Max(bounds.Y, other.Y);
        int right = Math.Min(bounds.X + bounds.Width, other.X + other.Width);
        int bottom = Math.Min(bounds.Y + bounds.Height, other.Y + other.Height);
        if (right <= left || bottom <= top)
            return null;
        return new RecordingIndicatorBounds(left, top, right - left, bottom - top);
    }

    private static RecordingIndicatorBounds? Intersect(RecordingIndicatorBounds bounds, Rectangle rect)
    {
        return Intersect(bounds, new RecordingIndicatorBounds(rect.X, rect.Y, rect.Width, rect.Height));
    }

    private static bool AllOutside(Rectangle[] borders, Rectangle label, RecordingIndicatorBounds inner)
    {
        var innerRect = new Rectangle(inner.X, inner.Y, inner.Width, inner.Height);
        foreach (var r in borders)
        {
            if (r.IntersectsWith(innerRect))
                return false;
        }
        return !label.IntersectsWith(innerRect);
    }

    private static bool AllInside(Rectangle[] borders, Rectangle label, RecordingIndicatorBounds parent)
    {
        var parentRect = new Rectangle(parent.X, parent.Y, parent.Width, parent.Height);
        foreach (var r in borders)
        {
            if (!parentRect.Contains(r))
                return false;
        }
        return parentRect.Contains(label);
    }

    private static bool AllInside(Rectangle[] borders, Rectangle label, Rectangle rect)
    {
        foreach (var r in borders)
        {
            if (!rect.Contains(r))
                return false;
        }
        return rect.Contains(label);
    }
}

/// <summary>
/// Top-most, click-through, non-activating border window that indicates an active recording region.
/// Displays a red border and a small REC timer label. Does not capture focus or block user input.
/// </summary>
internal sealed class RecordingIndicatorForm : Form
{
    private readonly string _recordingId;
    private readonly RecordingIndicatorBounds _bounds;
    private readonly RecordingIndicatorPresentation _presentation;
    private readonly DateTime _startedAtUtc;
    private readonly int? _durationSeconds;
    private readonly string? _nestedRole;
    private readonly IWindowDisplayAffinity _displayAffinity;
    private System.Windows.Forms.Timer _timer = null!;
    private Label _label = null!;
    private bool _displayAffinityApplied;
    private Exception? _displayAffinityError;
    private int _actualWindowDpi;

    internal RecordingIndicatorBounds BoundsForTests => _bounds;
    internal int ActualWindowDpiForTests => _actualWindowDpi;
    internal bool TimerEnabledForTests => _timer?.Enabled ?? false;
    internal string LabelTextForTests => _label?.Text ?? "";
    internal Rectangle LabelBoundsForTests => _label?.Bounds ?? Rectangle.Empty;
    internal Size LabelMeasuredSizeForTests => MeasureLabelSize(_nestedRole, _durationSeconds, _label?.Font ?? new Font("Segoe UI", 9, FontStyle.Bold), _label?.Padding ?? new Padding(4, 2, 4, 2));
    internal bool DisplayAffinityAppliedForTests => _displayAffinityApplied;
    internal Exception? DisplayAffinityErrorForTests => _displayAffinityError;
    internal CaptureVisibilityMode CaptureVisibilityModeForTests => _presentation.Mode;
    internal RecordingIndicatorPresentation PresentationForTests => _presentation;
    internal Rectangle[] BorderRectanglesForTests => _presentation.BorderRectangles;

    public RecordingIndicatorForm(
        string recordingId,
        RecordingIndicatorBounds bounds,
        DateTime startedAtUtc,
        int? durationSeconds = null,
        string? nestedRole = null,
        IWindowDisplayAffinity? displayAffinity = null)
        : this(recordingId, bounds, startedAtUtc, durationSeconds, nestedRole, null, displayAffinity)
    {
    }

    internal RecordingIndicatorForm(
        string recordingId,
        RecordingIndicatorPresentation presentation,
        DateTime startedAtUtc,
        int? durationSeconds = null,
        string? nestedRole = null,
        IWindowDisplayAffinity? displayAffinity = null)
    {
        _recordingId = recordingId;
        _presentation = presentation;
        _bounds = RecordingIndicatorGeometry.ClampToVirtualScreen(presentation.WindowBounds);
        _startedAtUtc = startedAtUtc;
        _durationSeconds = durationSeconds;
        _nestedRole = nestedRole;
        _displayAffinity = displayAffinity ?? WindowDisplayAffinity.Instance;

        InitializeComponent();
        StartPosition = FormStartPosition.Manual;
        Bounds = new Rectangle(_bounds.X, _bounds.Y, _bounds.Width, _bounds.Height);
    }

    private RecordingIndicatorForm(
        string recordingId,
        RecordingIndicatorBounds bounds,
        DateTime startedAtUtc,
        int? durationSeconds,
        string? nestedRole,
        RecordingIndicatorPresentation? presentation,
        IWindowDisplayAffinity? displayAffinity)
    {
        _recordingId = recordingId;
        _bounds = RecordingIndicatorGeometry.ClampToVirtualScreen(bounds);
        _startedAtUtc = startedAtUtc;
        _durationSeconds = durationSeconds;
        _nestedRole = nestedRole;
        _displayAffinity = displayAffinity ?? WindowDisplayAffinity.Instance;

        _presentation = presentation ?? new RecordingIndicatorPresentation(
            CaptureVisibilityMode.ExcludeFromCapture,
            _bounds,
            _bounds,
            null,
            Array.Empty<Rectangle>(),
            RecordingIndicatorGeometry.ComputeLabelLocation(_bounds, MeasureLabelSize(nestedRole, durationSeconds, new Font("Segoe UI", 9, FontStyle.Bold), new Padding(4, 2, 4, 2))) is { } loc
                ? new Rectangle(loc.X, loc.Y, 0, 0)
                : Rectangle.Empty,
            DisplayAffinityRequested: true,
            FallbackReason: null);

        InitializeComponent();
        StartPosition = FormStartPosition.Manual;
        Bounds = new Rectangle(_bounds.X, _bounds.Y, _bounds.Width, _bounds.Height);
    }

    private void InitializeComponent()
    {
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        TopMost = true;
        StartPosition = FormStartPosition.Manual;
        BackColor = Color.Magenta;
        TransparencyKey = Color.Magenta;
        Opacity = 1.0;
        DoubleBuffered = true;
        ControlBox = false;
        MaximizeBox = false;
        MinimizeBox = false;
        Text = "";
        AutoScaleMode = _presentation.Mode == CaptureVisibilityMode.ParentVisible
            ? AutoScaleMode.None
            : AutoScaleMode.Dpi;

        var font = new Font("Segoe UI", 9, FontStyle.Bold);
        var padding = new Padding(4, 2, 4, 2);
        var size = _presentation.Mode == CaptureVisibilityMode.ParentVisible
            ? _presentation.LabelBounds.Size
            : MeasureLabelSize(_nestedRole, _durationSeconds, font, padding);

        _label = new Label
        {
            AutoSize = false,
            BackColor = Color.FromArgb(180, 255, 0, 0),
            ForeColor = Color.White,
            Font = font,
            Padding = padding,
            Text = FormatLabel(TimeSpan.Zero),
            Visible = true,
            Size = size,
            TextAlign = ContentAlignment.MiddleCenter
        };
        Controls.Add(_label);

        _timer = new System.Windows.Forms.Timer
        {
            Interval = 500
        };
        _timer.Tick += (_, _) => UpdateLabel();
    }

    /// <summary>
    /// Maximum manual recording duration supported by the product API. Used as the
    /// conservative upper bound for label sizing when no explicit duration is set.
    /// </summary>
    internal const int MaxManualRecordingSeconds = 7200;

    /// <summary>
    /// Formats a non-negative time span for the REC label.
    /// Uses mm:ss below one hour and h:mm:ss at or above one hour to avoid
    /// minute-component wrap-around after 59:59.
    /// </summary>
    internal static string FormatTime(TimeSpan time)
    {
        if (time.TotalHours >= 1)
            return $"{time.Hours}:{time.Minutes:D2}:{time.Seconds:D2}";
        return $"{time.Minutes:D2}:{time.Seconds:D2}";
    }

    /// <summary>
    /// Measures the label size required to display the longest possible text for this
    /// recording without resizing during the recording. The elapsed and total portions
    /// share the same formatting helper so measurement and runtime rendering stay in sync.
    /// </summary>
    internal static Size MeasureLabelSize(string? nestedRole, int? durationSeconds, Font font, Padding padding)
    {
        var prefix = string.IsNullOrEmpty(nestedRole)
            ? "REC"
            : $"REC {nestedRole.ToUpperInvariant()}";

        string maxText;
        if (durationSeconds.HasValue && durationSeconds.Value > 0)
        {
            var total = TimeSpan.FromSeconds(durationSeconds.Value);
            var longestElapsed = total; // elapsed never exceeds total
            maxText = $"{prefix} {FormatTime(longestElapsed)} / {FormatTime(total)}";
        }
        else
        {
            var longestElapsed = TimeSpan.FromSeconds(MaxManualRecordingSeconds);
            maxText = $"{prefix} {FormatTime(longestElapsed)}";
        }

        var textSize = TextRenderer.MeasureText(maxText, font, Size.Empty, TextFormatFlags.SingleLine);
        return new Size(textSize.Width + padding.Horizontal, textSize.Height + padding.Vertical);
    }

    /// <summary>
    /// DPI-aware variant that measures the label at the target monitor DPI.
    /// The measurement is derived from the current process DPI baseline and scaled to the
    /// requested DPI, keeping it consistent with runtime Label rendering while remaining
    /// testable with forced <see cref="DisplayDpiInfo"/> values.
    /// </summary>
    internal static Size MeasureLabelSize(string? nestedRole, int? durationSeconds, Font font, Padding padding, DisplayDpiInfo dpiInfo)
    {
        int effectiveDpi = Math.Max(dpiInfo.DpiX, dpiInfo.DpiY);
        if (effectiveDpi <= 0)
            effectiveDpi = 96;

        // Measure at the current process/screen DPI, then scale to the target monitor DPI.
        // This keeps measurement consistent with the runtime Label rendering (which uses
        // the same font at the monitor DPI) while remaining testable with forced DPI values.
        var screenSize = MeasureLabelSize(nestedRole, durationSeconds, font, padding);
        int screenDpi = GetSystemDpi();
        if (screenDpi <= 0)
            screenDpi = 96;

        float scale = effectiveDpi / (float)screenDpi;
        return new Size(
            (int)Math.Ceiling(screenSize.Width * scale),
            (int)Math.Ceiling(screenSize.Height * scale));
    }

    private static int GetSystemDpi()
    {
        using var temp = new Control();
        return temp.DeviceDpi;
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        PositionLabel();
        UpdateLabel();
        _timer.Start();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);

        if (_presentation.Mode == CaptureVisibilityMode.ParentVisible)
        {
            // In parent-visible mode the red frame is drawn as four explicit rectangles
            // just outside the inner capture rectangle, keeping the inner capture clean.
            using var brush = new SolidBrush(Color.Red);
            foreach (var border in _presentation.BorderRectangles)
            {
                var clientRect = new Rectangle(
                    border.X - _bounds.X,
                    border.Y - _bounds.Y,
                    border.Width,
                    border.Height);
                e.Graphics.FillRectangle(brush, clientRect);
            }
        }
        else
        {
            using var pen = new Pen(Color.Red, RecordingIndicatorGeometry.BorderWidth);
            var rect = ClientRectangle;
            // Draw slightly inside so the full border is visible.
            float offset = RecordingIndicatorGeometry.BorderWidth / 2.0f;
            e.Graphics.DrawRectangle(pen, offset, offset, rect.Width - RecordingIndicatorGeometry.BorderWidth, rect.Height - RecordingIndicatorGeometry.BorderWidth);
        }
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        PositionLabel();
    }

    protected override void OnLoad(EventArgs e)
    {
        base.OnLoad(e);
        // Ensure the form does not steal activation when shown.
        // WM_SHOWWINDOW with SW_SHOWNOACTIVATE is handled by ShowWithoutActivation.
    }

    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            const int WS_EX_LAYERED = 0x80000;
            const int WS_EX_TRANSPARENT = 0x20;
            const int WS_EX_NOACTIVATE = 0x8000000;
            const int WS_EX_TOOLWINDOW = 0x80;

            var cp = base.CreateParams;
            cp.ExStyle |= WS_EX_LAYERED | WS_EX_TRANSPARENT | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW;
            // Keep click-through behavior: no caption, no thick frame.
            cp.Style &= ~(0x00C00000 | 0x00040000 | 0x00010000); // WS_CAPTION, WS_THICKFRAME, WS_SYSMENU
            return cp;
        }
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);

        try
        {
            _actualWindowDpi = Native.GetDpiForWindow(Handle);
        }
        catch
        {
            _actualWindowDpi = 0;
        }

        ApplyDisplayAffinity(Handle);
    }

    /// <summary>
    /// Applies the display-affinity setting and records the diagnostic outcome.
    /// Internal for unit-testing only; production code calls this from <see cref="OnHandleCreated"/>.
    /// </summary>
    internal void ApplyDisplayAffinity(IntPtr hWnd)
    {
        // Reset per-handle state so a recreated handle does not keep a stale error
        // from a previous attempt.
        _displayAffinityApplied = false;
        _displayAffinityError = null;

        if (!_presentation.DisplayAffinityRequested)
        {
            // Parent-visible mode intentionally remains capturable by the parent outer.
            return;
        }

        try
        {
            _displayAffinityApplied = _displayAffinity.SetExcludeFromCapture(hWnd);
        }
        catch (Exception ex)
        {
            // Display-affinity is a best-effort optimization. Failure must not break
            // the indicator, stop recording, or disturb the UI message loop.
            _displayAffinityError = ex;
            _displayAffinityApplied = false;
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _timer?.Stop();
            _timer?.Dispose();
            _label?.Font?.Dispose();
        }
        base.Dispose(disposing);
    }

    /// <summary>
    /// Closes the indicator without side effects. Safe to call multiple times.
    /// </summary>
    internal void CloseWithoutResult()
    {
        try { Close(); } catch { }
    }

    private void PositionLabel()
    {
        if (_label == null) return;

        if (_presentation.Mode == CaptureVisibilityMode.ParentVisible)
        {
            // The plan has already placed the label outside the inner capture rectangle.
            // The full measured label size must be used; cropping is not allowed in
            // parent-visible mode because the REC text must remain fully readable.
            _label.Location = new Point(
                _presentation.LabelBounds.X - _bounds.X,
                _presentation.LabelBounds.Y - _bounds.Y);
            _label.Size = new Size(
                _presentation.LabelBounds.Width,
                _presentation.LabelBounds.Height);
        }
        else
        {
            var size = _label.Size;
            var loc = RecordingIndicatorGeometry.ComputeLabelLocation(_bounds, size);
            _label.Location = new Point(loc.X - _bounds.X, loc.Y - _bounds.Y);
        }
    }

    private void UpdateLabel()
    {
        var elapsed = DateTime.UtcNow - _startedAtUtc;
        if (elapsed < TimeSpan.Zero) elapsed = TimeSpan.Zero;
        _label.Text = FormatLabel(elapsed);
    }

    /// <summary>
    /// Test-only entry point that updates the label with an explicit elapsed time
    /// without starting the timer or showing the window. Used to verify that the
    /// label text and bounds remain stable across the 59:59 -> 1:00:00 boundary.
    /// </summary>
    internal void UpdateLabelForTests(TimeSpan elapsed)
    {
        if (_label == null) return;
        _label.Text = FormatLabel(elapsed < TimeSpan.Zero ? TimeSpan.Zero : elapsed);
    }

    private string FormatLabel(TimeSpan elapsed)
    {
        var prefix = string.IsNullOrEmpty(_nestedRole)
            ? "REC"
            : $"REC {_nestedRole.ToUpperInvariant()}";

        if (_durationSeconds.HasValue && _durationSeconds.Value > 0)
        {
            var total = TimeSpan.FromSeconds(_durationSeconds.Value);
            return $"{prefix} {FormatTime(elapsed)} / {FormatTime(total)}";
        }
        return $"{prefix} {FormatTime(elapsed)}";
    }
}
