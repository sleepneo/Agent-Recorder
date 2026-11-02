using System.Drawing;

namespace AgentRecorder.App;

// Compose the existing collision resolver and presentation geometry. The capture
// rectangle stays frozen; only the excluded, transparent UI window may grow to
// contain its full measured badge when the selected window is narrow.
internal static class IndependentWindowControlLayout
{
    internal static RecordingIndicatorPresentation PlaceLabel(RecordingIndicatorPresentation plan, Size measured,
        Rectangle screen, IEnumerable<RecordingStopControlBounds> occupied)
    {
        var capture = plan.InnerCaptureBounds;
        var size = new Size(Math.Min(measured.Width, screen.Width), Math.Min(measured.Height, screen.Height));
        var preferred = new RecordingStopControlBounds(capture.X + 6, capture.Y + 6, size.Width, size.Height);
        var label = RecordingStopControlGeometry.ResolveCollision(preferred, size, screen, occupied);
        var labelRectangle = new Rectangle(label.X, label.Y, label.Width, label.Height);
        var union = Rectangle.Union(new(capture.X, capture.Y, capture.Width, capture.Height), labelRectangle);
        int border = Math.Min(RecordingIndicatorGeometry.BorderWidth, Math.Min(capture.Width, capture.Height));
        return plan with {
            WindowBounds = new(union.X, union.Y, union.Width, union.Height), LabelBounds = labelRectangle,
            BorderRectangles = new[] {
                new Rectangle(capture.X,capture.Y,capture.Width,border),
                new Rectangle(capture.X,capture.Y+capture.Height-border,capture.Width,border),
                new Rectangle(capture.X,capture.Y,border,capture.Height),
                new Rectangle(capture.X+capture.Width-border,capture.Y,border,capture.Height) }
        };
    }

    internal static RecordingStopControlBounds AsOccupied(Rectangle rectangle) =>
        new(rectangle.X, rectangle.Y, rectangle.Width, rectangle.Height);
}
