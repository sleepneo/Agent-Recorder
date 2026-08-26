using System;
using System.Text.Json;
using System.Text.Json.Nodes;
using AgentRecorder.App;
using AgentRecorder.Infrastructure;

namespace AgentRecorder.Tests;

/// <summary>
/// Compatibility fixture for legacy confirmation tests while the production
/// contract is migrated to immutable typed presentation values. JSON parsing
/// is deliberately confined to this test-only adapter; no production UI path
/// accepts or reconstructs a summary from JSON.
/// </summary>
internal static class ConfirmationPresentationTestData
{
    public static PendingConfirmationItem CreateItem(
        string confirmationId,
        string recordingId,
        object summary,
        Action<ConfirmationDecision> callback,
        int timeoutSeconds)
    {
        return new PendingConfirmationItem(
            CreatePresentation(confirmationId, recordingId, summary, timeoutSeconds),
            callback);
    }

    private static RecordingConfirmationPresentation CreatePresentation(
        string confirmationId,
        string recordingId,
        object summary,
        int timeoutSeconds)
    {
        var node = JsonNode.Parse(JsonSerializer.Serialize(summary));
        var seriesNode = node?["series"];
        var series = seriesNode != null || node?["series_interval_ms"] != null
            ? new RecordingSeriesPresentation
            {
                IntervalMs = GetInt(seriesNode, "interval_ms") ?? GetInt(node, "series_interval_ms") ?? 0,
                MaxCount = GetInt(seriesNode, "max_count") ?? GetInt(node, "series_max_count"),
                MaxDurationSeconds = GetInt(seriesNode, "max_duration_seconds") ?? GetInt(node, "series_max_duration_seconds"),
                PlannedFrameCount = GetInt(seriesNode, "planned_frame_count") ?? GetInt(node, "series_planned_frame_count") ?? 0,
                OutputKind = GetString(seriesNode, "output_kind") ?? "png_sequence_directory"
            }
            : null;

        var createdAtUtc = DateTime.UtcNow;
        return new RecordingConfirmationPresentation
        {
            Summary = new RecordingRequestSummary
            {
                Mode = GetString(node, "mode") ?? "video",
                Source = GetString(node, "source") ?? "",
                Audio = GetString(node, "audio") ?? "No audio",
                AudioSourceKind = GetString(node, "audio_source_kind") ?? "none",
                AudioSystemEnabled = GetBool(node, "audio_system_enabled") ?? false,
                AudioSystemDefaultOutput = GetString(node, "audio_system_default_output"),
                AudioSystemOutputName = GetString(node, "audio_system_output_name"),
                AudioSystemOutputIsDefault = GetBool(node, "audio_system_output_is_default"),
                AudioSystemOutputSelection = GetString(node, "audio_system_output_selection") ?? "selected",
                AudioDevice = GetString(node, "audio_device"),
                AudioVolumePercent = GetInt(node, "audio_volume_percent"),
                Duration = GetString(node, "duration") ?? "Manual stop",
                CountdownSeconds = GetInt(node, "countdown_seconds") ?? 0,
                Output = GetString(node, "output") ?? "",
                Series = series,
                NestedRole = GetString(node, "nested_role") ?? "none"
            },
            RecordingId = recordingId,
            ConfirmationId = confirmationId,
            TimeoutSeconds = timeoutSeconds,
            CreatedAtUtc = createdAtUtc,
            ExpiresAtUtc = createdAtUtc.AddSeconds(timeoutSeconds),
            SourceType = GetString(node, "source_type") ?? "",
            SourceTitle = GetString(node, "source_title"),
            SourceApplication = GetString(node, "source_application"),
            WindowId = GetString(node, "window_id"),
            TraceId = GetString(node, "trace_id"),
            CoordinateSpace = GetString(node, "coordinate_space") ?? "virtual_screen",
            CaptureSemantics = GetString(node, "capture_semantics") ?? "",
            PlannedBackend = GetString(node, "planned_backend") ?? "",
            PreviewSemantics = GetString(node, "preview_semantics") ?? "",
            SelectionReasonCode = GetString(node, "selection_reason_code") ?? "",
            SelectionAvailabilitySource = GetString(node, "selection_availability_source") ?? "",
            SelectionFallback = GetBool(node, "selection_fallback") ?? false,
            TargetDisplayId = GetString(node, "target_display_id") ?? "",
            TargetDisplayBounds = GetBounds(node?["target_display_bounds"]),
            CaptureBounds = GetBounds(node?["capture_bounds"]),
            OutputKind = GetString(node, "output_kind") ?? "mp4_file"
        };
    }

    private static ConfirmationCaptureBounds? GetBounds(JsonNode? node)
    {
        var x = GetInt(node, "x");
        var y = GetInt(node, "y");
        var width = GetInt(node, "width");
        var height = GetInt(node, "height");
        return x.HasValue && y.HasValue && width.HasValue && height.HasValue
            ? new ConfirmationCaptureBounds(x.Value, y.Value, width.Value, height.Value)
            : null;
    }

    private static string? GetString(JsonNode? node, string name)
    {
        try { return node?[name]?.GetValue<string?>(); }
        catch { return node?[name]?.ToString(); }
    }

    private static int? GetInt(JsonNode? node, string name)
    {
        try { return node?[name]?.GetValue<int?>(); }
        catch { return null; }
    }

    private static bool? GetBool(JsonNode? node, string name)
    {
        try { return node?[name]?.GetValue<bool?>(); }
        catch { return null; }
    }
}
