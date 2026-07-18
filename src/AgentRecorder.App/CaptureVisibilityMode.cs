using System.Drawing;
using AgentRecorder.Infrastructure;

namespace AgentRecorder.App;

/// <summary>
/// Defines how a recording control window participates in screen capture.
/// </summary>
internal enum CaptureVisibilityMode
{
    /// <summary>
    /// The control is excluded from all screen captures via WDA_EXCLUDEFROMCAPTURE.
    /// Used for ordinary recordings and for nested recordings that cannot safely be
    /// shown to the parent capture.
    /// </summary>
    ExcludeFromCapture,

    /// <summary>
    /// The control is intentionally not excluded from capture so that a parent outer
    /// recording can include it. All colored pixels and the interactive stop button
    /// are placed strictly outside the inner capture rectangle but inside the parent
    /// capture rectangle, so the inner video itself remains clean.
    /// </summary>
    ParentVisible
}

/// <summary>
/// Immutable description of where the recording indicator should render and how it
/// should participate in screen capture.
/// </summary>
internal sealed record RecordingIndicatorPresentation(
    CaptureVisibilityMode Mode,
    RecordingIndicatorBounds WindowBounds,
    RecordingIndicatorBounds InnerCaptureBounds,
    RecordingIndicatorBounds? ParentCaptureBounds,
    Rectangle[] BorderRectangles,
    Rectangle LabelBounds,
    bool DisplayAffinityRequested,
    string? FallbackReason);

/// <summary>
/// Combined planning result for both the indicator and the stop control.
/// The plan is computed before any UI is created so that indicator, label and stop
/// control can be decided jointly: if any part cannot be placed safely, the whole
/// recording falls back to capture-excluded mode.
/// </summary>
internal sealed record RecordingControlPlan(
    RecordingIndicatorPresentation IndicatorPresentation,
    RecordingStopControlBounds StopBounds,
    Size StopControlSize,
    DisplayDpiInfo DpiInfo,
    string? FallbackReason);
