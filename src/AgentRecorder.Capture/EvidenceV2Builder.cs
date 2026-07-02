using System.Text;
using System.Text.Json;

namespace AgentRecorder.Capture;

/// <summary>
/// Builds a schema v2.0 continuous recording evidence JSON from a session summary
/// and metadata. This is a pure mapping builder - it does NOT validate media
/// files on disk. File existence and size are provided by the caller via the
/// IMediaFileProbe interface.
/// </summary>
public sealed class EvidenceV2Builder
{
    private readonly ContinuousSessionMetadata _metadata;
    private readonly IMediaFileProbe? _mediaProbe;

    public EvidenceV2Builder(ContinuousSessionMetadata metadata, IMediaFileProbe? mediaProbe = null)
    {
        _metadata = metadata ?? throw new ArgumentNullException(nameof(metadata));
        _mediaProbe = mediaProbe;
    }

    /// <summary>
    /// Builds a v2.0 continuous evidence JSON string from the session summary.
    /// Returns null if the session summary state is Unknown or MalformedSequence
    /// without any output.
    /// </summary>
    public string? Build(WgcContinuousSessionSummary summary)
    {
        if (summary == null)
            throw new ArgumentNullException(nameof(summary));

        // Determine final status and error
        string finalStatus;
        string? errorMessage = null;
        bool isPlayable = false;
        bool partialOutputExists = false;
        long actualFileSize = 0;
        bool actualFileExists = false;

        string outputPath = summary.OutputPath ?? "";

        switch (summary.State)
        {
            case ContinuousSessionState.Success:
                finalStatus = "completed";
                errorMessage = null;
                partialOutputExists = false;
                break;

            case ContinuousSessionState.Stopped:
                finalStatus = "completed"; // Graceful stop is still a completed session
                errorMessage = null;
                partialOutputExists = false;
                break;

            case ContinuousSessionState.Failed:
                finalStatus = "failed";
                // Surface the most specific error message available
                if (!string.IsNullOrEmpty(summary.Reason))
                    errorMessage = summary.Reason;
                else if (!string.IsNullOrEmpty(summary.ErrorCode))
                    errorMessage = summary.ErrorCode;
                else if (!string.IsNullOrEmpty(summary.Hresult))
                    errorMessage = $"hr={summary.Hresult}";
                else
                    errorMessage = "unknown_failure";

                partialOutputExists = summary.PartialOutputExists;
                break;

            case ContinuousSessionState.MalformedSequence:
                // Malformed sequence with no meaningful output - treat as failed
                finalStatus = "failed";
                if (summary.ValidationErrors.Count > 0)
                    errorMessage = $"malformed_sequence: {string.Join("; ", summary.ValidationErrors)}";
                else
                    errorMessage = "malformed_sequence: unknown error";
                partialOutputExists = false;
                break;

            case ContinuousSessionState.Unknown:
            default:
                // Cannot build evidence for unknown state
                return null;
        }

        // Probe media file if available and path is non-empty
        if (_mediaProbe != null && !string.IsNullOrEmpty(outputPath))
        {
            var probeResult = _mediaProbe.Probe(outputPath);
            if (probeResult != null)
            {
                actualFileExists = probeResult.FileExists;
                actualFileSize = probeResult.FileSizeBytes;
                isPlayable = probeResult.IsPlayable;
            }
        }
        else if (!string.IsNullOrEmpty(outputPath))
        {
            // Fallback: use File.Exists and FileInfo.Length directly
            try
            {
                if (File.Exists(outputPath))
                {
                    actualFileExists = true;
                    actualFileSize = new FileInfo(outputPath).Length;
                    // Without a media probe, we cannot determine playability
                    isPlayable = false;
                }
            }
            catch
            {
                actualFileExists = false;
                actualFileSize = 0;
            }
        }

        // Build output object
        var outputObj = new Dictionary<string, object?>
        {
            ["path"] = outputPath,
            ["container"] = summary.Container ?? "",
            ["codec"] = summary.Codec ?? "",
            ["bytes_written"] = summary.BytesWritten ?? 0,
            ["width"] = summary.Width ?? 0,
            ["height"] = summary.Height ?? 0,
            ["duration_ms"] = summary.DurationMs ?? 0,
            ["fps"] = summary.Fps ?? 0,
            ["frames_captured"] = summary.FramesCaptured ?? 0,
            ["frames_dropped"] = summary.FramesDropped ?? 0,
            ["capture_method"] = summary.CaptureMethod ?? ""
        };

        // Build audit events
        var auditEvents = BuildAuditEvents(summary);

        // Build warnings
        var warnings = new List<string>();
        if (summary.State == ContinuousSessionState.Failed && partialOutputExists)
        {
            warnings.Add("Partial output exists due to failure");
        }

        // Build the top-level evidence object
        var evidence = new Dictionary<string, object?>
        {
            ["schema_version"] = "2.0",
            ["timestamp"] = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ"),
            ["mode"] = "real",
            ["capture_kind"] = "continuous",
            ["window_hwnd"] = _metadata.WindowHwnd,
            ["window_id"] = _metadata.WindowId,
            ["window_title"] = _metadata.WindowTitle ?? "",
            ["recording_id"] = _metadata.RecordingId ?? "",
            ["confirmation_id"] = _metadata.ConfirmationId ?? "",
            ["final_status"] = finalStatus,
            ["output"] = outputObj,
            ["actual_file_exists"] = actualFileExists,
            ["actual_file_size"] = actualFileSize,
            ["is_playable_media"] = isPlayable,
            ["partial_output_exists"] = partialOutputExists,
            ["duration_ms"] = summary.DurationMs ?? 0,
            ["requested_duration_ms"] = _metadata.RequestedDurationMs,
            ["fps"] = summary.Fps ?? 0,
            ["frames_captured"] = summary.FramesCaptured ?? 0,
            ["frames_dropped"] = summary.FramesDropped ?? 0,
            ["stop_reason"] = summary.GetStopReasonForEvidence(),
            ["warnings"] = warnings,
            ["error"] = errorMessage ?? "",
            ["audit_events"] = auditEvents
        };

        var options = new JsonSerializerOptions
        {
            WriteIndented = true,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        };

        return JsonSerializer.Serialize(evidence, options);
    }

    private List<Dictionary<string, object?>> BuildAuditEvents(WgcContinuousSessionSummary summary)
    {
        var events = new List<Dictionary<string, object?>>();
        string recId = _metadata.RecordingId ?? "";
        string confId = _metadata.ConfirmationId ?? "";

        // confirmation.created
        if (!string.IsNullOrEmpty(confId))
        {
            events.Add(new Dictionary<string, object?> { ["event"] = "confirmation.created", ["confirmation_id"] = confId });
        }

        // confirmation.approved
        if (!string.IsNullOrEmpty(confId) && !string.IsNullOrEmpty(recId))
        {
            events.Add(new Dictionary<string, object?> { ["event"] = "confirmation.approved", ["confirmation_id"] = confId, ["recording_id"] = recId });
        }

        // recording.backend_selected
        if (!string.IsNullOrEmpty(recId))
        {
            events.Add(new Dictionary<string, object?> { ["event"] = "recording.backend_selected", ["backend"] = "wgc", ["recording_id"] = recId });
        }

        // recording.session_started
        if (!string.IsNullOrEmpty(recId))
        {
            events.Add(new Dictionary<string, object?> { ["event"] = "recording.session_started", ["recording_id"] = recId });
        }

        // Terminal event
        if (summary.State == ContinuousSessionState.Success)
        {
            // OK -> completed: recording.completed only
            events.Add(new Dictionary<string, object?> { ["event"] = "recording.completed", ["recording_id"] = recId });
        }
        else if (summary.State == ContinuousSessionState.Stopped)
        {
            // STOPPED -> completed: must have both recording.stopped AND recording.completed
            events.Add(new Dictionary<string, object?> { ["event"] = "recording.stopped", ["recording_id"] = recId, ["reason"] = "user_requested" });
            events.Add(new Dictionary<string, object?> { ["event"] = "recording.completed", ["recording_id"] = recId });
        }
        else if (summary.State == ContinuousSessionState.Failed || summary.State == ContinuousSessionState.MalformedSequence)
        {
            // Failed: recording.failed only (confirmation.created is already added above if confId exists)
            var errDict = new Dictionary<string, object?> { ["event"] = "recording.failed", ["recording_id"] = recId };
            if (!string.IsNullOrEmpty(summary.ErrorCode))
                errDict["error"] = summary.ErrorCode;
            else if (!string.IsNullOrEmpty(summary.Reason))
                errDict["error"] = summary.Reason;
            events.Add(errDict);
        }

        return events;
    }
}

/// <summary>
/// Metadata required to build evidence JSON that cannot be derived from the event stream alone.
/// </summary>
public sealed class ContinuousSessionMetadata
{
    public string WindowHwnd { get; set; } = "";
    public string WindowId { get; set; } = "";
    public string? WindowTitle { get; set; }
    public string? RecordingId { get; set; }
    public string? ConfirmationId { get; set; }
    public long RequestedDurationMs { get; set; }
}

/// <summary>
/// Interface for probing media file properties (existence, size, playability).
/// Allows tests to inject mock probes without touching the real file system.
/// </summary>
public interface IMediaFileProbe
{
    MediaProbeResult? Probe(string path);
}

/// <summary>
/// Result of a media file probe.
/// </summary>
public sealed class MediaProbeResult
{
    public bool FileExists { get; set; }
    public long FileSizeBytes { get; set; }
    public bool IsPlayable { get; set; }
}
