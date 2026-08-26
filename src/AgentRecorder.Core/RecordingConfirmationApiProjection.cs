using System.Globalization;
using AgentRecorder.Infrastructure;

namespace AgentRecorder.Core;

/// <summary>
/// Explicit boundary between the strongly typed local confirmation
/// presentation and the legacy public API summary shape. The API contract is
/// intentionally projected field-by-field so UI-only metadata cannot leak into
/// HTTP responses accidentally.
/// </summary>
internal static class RecordingConfirmationApiProjection
{
    public static object ToObject(RecordingConfirmationPresentation presentation)
    {
        var summary = presentation.Summary;
        var series = summary.Series;

        return new
        {
            mode = summary.Mode,
            source = summary.Source,
            audio = summary.Audio,
            audio_source_kind = summary.AudioSourceKind,
            // These five fields intentionally preserve the legacy
            // reflection-based ToString() contract. They are historical
            // strings at the public API boundary, even though the local
            // presentation model is strongly typed.
            audio_system_enabled = summary.AudioSystemEnabled.ToString(),
            audio_system_default_output = summary.AudioSystemDefaultOutput ?? "",
            audio_system_output_name = summary.AudioSystemOutputName ?? "",
            audio_system_output_is_default = summary.AudioSystemOutputIsDefault?.ToString() ?? "",
            audio_system_output_selection = summary.AudioSystemOutputSelection,
            duration = summary.Duration,
            countdown_seconds = summary.CountdownSeconds,
            output = summary.Output,
            series = LegacySeriesToString(series),
            series_interval_ms = series?.IntervalMs,
            series_max_count = series?.MaxCount,
            series_max_duration_seconds = series?.MaxDurationSeconds,
            series_planned_frame_count = series?.PlannedFrameCount,
            output_kind = presentation.OutputKind,
            nested_role = summary.NestedRole,
            recording_id = presentation.RecordingId,
            confirmation_id = presentation.ConfirmationId,
            timeout_seconds = presentation.TimeoutSeconds,
            expires_at = presentation.ExpiresAtUtc.ToUniversalTime()
                .ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture),
            source_type = presentation.SourceType,
            source_title = presentation.SourceTitle,
            source_application = presentation.SourceApplication,
            window_id = presentation.WindowId,
            trace_id = presentation.TraceId,
            coordinate_space = presentation.CoordinateSpace,
            capture_semantics = presentation.CaptureSemantics,
            planned_backend = presentation.PlannedBackend,
            preview_semantics = presentation.PreviewSemantics,
            selection_reason_code = presentation.SelectionReasonCode,
            selection_availability_source = presentation.SelectionAvailabilitySource,
            selection_fallback = presentation.SelectionFallback,
            target_display_id = presentation.TargetDisplayId,
            target_display_bounds = ToBoundsObject(presentation.TargetDisplayBounds),
            capture_bounds = ToBoundsObject(presentation.CaptureBounds)
        };
    }

    private static object? ToBoundsObject(ConfirmationCaptureBounds? bounds) => bounds == null
        ? null
        : new
        {
            x = bounds.X,
            y = bounds.Y,
            width = bounds.Width,
            height = bounds.Height
        };

    private static string LegacySeriesToString(RecordingSeriesPresentation? series)
    {
        if (series == null)
            return "";

        // This is the exact shape of the anonymous series object converted by
        // the legacy summary string bridge.
        // Keep it as a string until a versioned API contract explicitly
        // changes this historical field.
        return new
        {
            interval_ms = series.IntervalMs,
            max_count = series.MaxCount,
            max_duration_seconds = series.MaxDurationSeconds,
            planned_frame_count = series.PlannedFrameCount,
            output_kind = series.OutputKind
        }.ToString() ?? "";
    }
}
