using System.Drawing;
using System.Windows.Forms;
using AgentRecorder.Infrastructure;
using AgentRecorder.UI.Geometry;

namespace AgentRecorder.App;

/// <summary>
/// WinForms-only measurement and padding logic. Placement is delegated to the
/// shared geometry project below.
/// </summary>
internal static class RecordingStopControlLayout
{
    public static Padding ButtonPadding { get; } = new Padding(12, 6, 12, 6);
    public const int HorizontalSafetyInsetLogical = 14;
    public const int VerticalSafetyInsetLogical = 4;

    public static Size MeasurePreferredSize(IUiTextProvider text, Font font, Rectangle targetMonitorBounds)
    {
        using var host = new DpiHost(targetMonitorBounds);
        host.EnsureHandle();
        return MeasureOnHost(text, font, host);
    }

    internal static Size MeasurePreferredSize(IUiTextProvider text, Font font, Rectangle targetMonitorBounds, int testDpi)
    {
        float scale = testDpi / 96f;
        var stopSize = TextRenderer.MeasureText(text.Get("StopControl_Button_Stop"), font, Size.Empty, TextFormatFlags.SingleLine);
        var stoppingSize = TextRenderer.MeasureText(text.Get("StopControl_Button_Stopping"), font, Size.Empty, TextFormatFlags.SingleLine);
        int width = (int)Math.Ceiling(Math.Max(stopSize.Width, stoppingSize.Width) * scale);
        int height = (int)Math.Ceiling(Math.Max(stopSize.Height, stoppingSize.Height) * scale);
        int horizontalInset = (int)Math.Ceiling(HorizontalSafetyInsetLogical * scale);
        int verticalInset = (int)Math.Ceiling(VerticalSafetyInsetLogical * scale);
        width += (int)Math.Ceiling(ButtonPadding.Horizontal * scale) + horizontalInset * 2;
        height += (int)Math.Ceiling(ButtonPadding.Vertical * scale) + verticalInset * 2;
        return new Size(
            Math.Max(StopControlGeometry.DefaultButtonWidth, width),
            Math.Max(StopControlGeometry.DefaultButtonHeight, height));
    }

    public static Size MeasurePreferredSize(IUiTextProvider text, Font font, Control? dpiSource = null)
        => MeasurePreferredSize(text, font, GetDpiScale(dpiSource));

    internal static Size MeasurePreferredSize(IUiTextProvider text, Font font, float dpiScale)
    {
        using var tempButton = CreateMeasureButton(font);
        tempButton.Text = text.Get("StopControl_Button_Stop");
        var stopSize = tempButton.GetPreferredSize(Size.Empty);
        tempButton.Text = text.Get("StopControl_Button_Stopping");
        var stoppingSize = tempButton.GetPreferredSize(Size.Empty);
        int width = Math.Max(stopSize.Width, stoppingSize.Width);
        int height = Math.Max(stopSize.Height, stoppingSize.Height);
        int horizontalInset = (int)Math.Ceiling(HorizontalSafetyInsetLogical * dpiScale);
        int verticalInset = (int)Math.Ceiling(VerticalSafetyInsetLogical * dpiScale);
        width += horizontalInset * 2;
        height += verticalInset * 2;
        return new Size(
            Math.Max(StopControlGeometry.DefaultButtonWidth, width),
            Math.Max(StopControlGeometry.DefaultButtonHeight, height));
    }

    public static Rectangle GetContentSafeRectangle(Button button)
        => GetContentSafeRectangle(button, GetDpiScale(button));

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

    private static Size MeasureOnHost(IUiTextProvider text, Font font, DpiHost host)
    {
        using var tempButton = CreateMeasureButton(font);
        host.Controls.Add(tempButton);
        tempButton.Text = text.Get("StopControl_Button_Stop");
        var stopSize = tempButton.GetPreferredSize(Size.Empty);
        tempButton.Text = text.Get("StopControl_Button_Stopping");
        var stoppingSize = tempButton.GetPreferredSize(Size.Empty);
        int width = Math.Max(stopSize.Width, stoppingSize.Width);
        int height = Math.Max(stopSize.Height, stoppingSize.Height);
        float dpiScale = host.DeviceDpi / 96f;
        width += (int)Math.Ceiling(HorizontalSafetyInsetLogical * dpiScale) * 2;
        height += (int)Math.Ceiling(VerticalSafetyInsetLogical * dpiScale) * 2;
        return new Size(
            Math.Max(StopControlGeometry.DefaultButtonWidth, width),
            Math.Max(StopControlGeometry.DefaultButtonHeight, height));
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
        try { return (control?.DeviceDpi ?? GetSystemDpi()) / 96f; }
        catch { return 1f; }
    }

    private static int GetSystemDpi()
    {
        using var temp = new Control();
        return temp.DeviceDpi;
    }

    private sealed class DpiHost : Control
    {
        public DpiHost(Rectangle bounds) { Bounds = bounds; Visible = false; }
        public void EnsureHandle() { if (!IsHandleCreated) CreateHandle(); }
    }
}

/// <summary>
/// App compatibility adapter. It maps App recording bounds and visibility mode
/// to the shared stop-control geometry; it contains no placement formulas.
/// </summary>
internal static class RecordingStopControlGeometry
{
    public const int DefaultButtonWidth = StopControlGeometry.DefaultButtonWidth;
    public const int DefaultButtonHeight = StopControlGeometry.DefaultButtonHeight;
    public const int OutsideMargin = StopControlGeometry.OutsideMargin;
    public const int InsideMargin = StopControlGeometry.InsideMargin;
    public const int NestedOffset = StopControlGeometry.NestedOffset;

    public static RecordingStopControlBounds ComputeBounds(
        RecordingIndicatorBounds recordingBounds,
        Size controlSize,
        string? nestedRole)
        => ComputeBounds(recordingBounds, controlSize, nestedRole, SystemInformation.VirtualScreen);

    public static RecordingStopControlBounds ComputeBounds(
        RecordingIndicatorBounds recordingBounds,
        Size controlSize,
        string? nestedRole,
        Rectangle virtualScreen)
        => ComputeBounds(recordingBounds, controlSize, nestedRole, virtualScreen, null,
            CaptureVisibilityMode.ExcludeFromCapture);

    public static RecordingStopControlBounds ComputeBounds(
        RecordingIndicatorBounds recordingBounds,
        Size controlSize,
        string? nestedRole,
        Rectangle virtualScreen,
        RecordingIndicatorBounds? parentBounds,
        CaptureVisibilityMode mode)
        => StopControlGeometry.ComputeBounds(
            ToRectangle(recordingBounds), controlSize, nestedRole, virtualScreen,
            parentBounds is not null ? ToRectangle(parentBounds) : null,
            mode == CaptureVisibilityMode.ParentVisible
                ? StopControlVisibilityMode.ParentVisible
                : StopControlVisibilityMode.ExcludeFromCapture);

    public static RecordingStopControlBounds ComputeBounds(RecordingIndicatorBounds recordingBounds, string? nestedRole)
        => ComputeBounds(recordingBounds, new Size(DefaultButtonWidth, DefaultButtonHeight), nestedRole);

    public static RecordingStopControlBounds ComputeBounds(
        RecordingIndicatorBounds recordingBounds, string? nestedRole, Rectangle virtualScreen)
        => ComputeBounds(recordingBounds, new Size(DefaultButtonWidth, DefaultButtonHeight), nestedRole, virtualScreen);

    public static RecordingStopControlBounds ResolveCollision(
        RecordingStopControlBounds preferred,
        Size controlSize,
        Rectangle virtualScreen,
        IEnumerable<RecordingStopControlBounds> occupiedBounds)
        => StopControlGeometry.ResolveCollision(preferred, controlSize, virtualScreen, occupiedBounds);

    public static RecordingStopControlBounds? ResolveCollision(
        RecordingStopControlBounds preferred,
        Size controlSize,
        Rectangle virtualScreen,
        IEnumerable<RecordingStopControlBounds> occupiedBounds,
        Rectangle? forbiddenZone,
        Rectangle? allowedZone)
        => StopControlGeometry.ResolveCollision(preferred, controlSize, virtualScreen,
            occupiedBounds, forbiddenZone, allowedZone);

    public static bool TryResolveCollision(
        RecordingStopControlBounds preferred,
        Size controlSize,
        Rectangle virtualScreen,
        IEnumerable<RecordingStopControlBounds> occupiedBounds,
        Rectangle? forbiddenZone,
        Rectangle? allowedZone,
        out RecordingStopControlBounds? bounds)
        => StopControlGeometry.TryResolveCollision(preferred, controlSize, virtualScreen,
            occupiedBounds, forbiddenZone, allowedZone, out bounds);

    internal static bool Intersects(RecordingStopControlBounds a, RecordingStopControlBounds b)
        => StopControlGeometry.Intersects(a, b);

    internal static bool IsInside(RecordingStopControlBounds bounds, Rectangle virtualScreen)
        => StopControlGeometry.IsInside(bounds, virtualScreen);

    private static Rectangle ToRectangle(RecordingIndicatorBounds bounds)
        => new(bounds.X, bounds.Y, bounds.Width, bounds.Height);
}
