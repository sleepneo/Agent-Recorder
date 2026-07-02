namespace AgentRecorder.Capture;

/// <summary>
/// Parses the blank-line-delimited event stream produced by wgc-native-helper.exe
/// on stdout for continuous recording sessions. Supports \n and \r\n line endings,
/// and blank lines that may contain spaces or tabs.
/// </summary>
public static class WgcContinuousEventStreamParser
{
    /// <summary>
    /// Parse the helper stdout into a list of structured events.
    /// Never throws - returns an empty list on empty/malformed input.
    /// </summary>
    public static List<WgcContinuousEvent> ParseEvents(string? stdout)
    {
        var events = new List<WgcContinuousEvent>();

        if (string.IsNullOrWhiteSpace(stdout))
            return events;

        // Split by blank lines (lines containing only whitespace) using line-by-line scan
        var blockLines = new List<string>();
        using (var reader = new StringReader(stdout))
        {
            string? line;
            while ((line = reader.ReadLine()) != null)
            {
                if (line.Trim().Length == 0)
                {
                    if (blockLines.Count > 0)
                    {
                        var evt = ParseEventBlock(blockLines);
                        if (evt != null)
                            events.Add(evt);
                        blockLines.Clear();
                    }
                    continue;
                }
                blockLines.Add(line);
            }
        }

        if (blockLines.Count > 0)
        {
            var evt = ParseEventBlock(blockLines);
            if (evt != null)
                events.Add(evt);
        }

        return events;
    }

    /// <summary>
    /// Parse a single event block (key-value pairs separated by newlines).
    /// </summary>
    private static WgcContinuousEvent? ParseEventBlock(List<string> lines)
    {
        var evt = new WgcContinuousEvent();
        bool hasResult = false;

        foreach (var rawLine in lines)
        {
            var line = rawLine.Trim();
            if (line.Length == 0)
                continue;

            // Find first ": "
            int colonIdx = line.IndexOf(": ", StringComparison.Ordinal);
            if (colonIdx <= 0)
                continue;

            var key = line.Substring(0, colonIdx).Trim();
            var value = line.Substring(colonIdx + 2).Trim();

            switch (key)
            {
                case "RESULT":
                    evt.Result = ParseResult(value);
                    hasResult = true;
                    break;
                case "Stage":
                    evt.Stage = value;
                    break;
                case "RecordingId":
                    evt.RecordingId = value;
                    break;
                case "Output":
                    evt.Output = value;
                    break;
                case "Container":
                    evt.Container = value;
                    break;
                case "Codec":
                    evt.Codec = value;
                    break;
                case "Fps":
                    if (int.TryParse(value, out var fps))
                        evt.Fps = fps;
                    else
                        evt.FpsParseFailed = true;
                    break;
                case "Width":
                    if (int.TryParse(value, out var width))
                        evt.Width = width;
                    else
                        evt.WidthParseFailed = true;
                    break;
                case "Height":
                    if (int.TryParse(value, out var height))
                        evt.Height = height;
                    else
                        evt.HeightParseFailed = true;
                    break;
                case "FramesCaptured":
                    if (long.TryParse(value, out var fc))
                        evt.FramesCaptured = fc;
                    else
                        evt.FramesCapturedParseFailed = true;
                    break;
                case "FramesDropped":
                    if (long.TryParse(value, out var fd))
                        evt.FramesDropped = fd;
                    else
                        evt.FramesDroppedParseFailed = true;
                    break;
                case "ElapsedMs":
                    if (long.TryParse(value, out var em))
                        evt.ElapsedMs = em;
                    else
                        evt.ElapsedMsParseFailed = true;
                    break;
                case "DurationMs":
                    if (long.TryParse(value, out var dm))
                        evt.DurationMs = dm;
                    else
                        evt.DurationMsParseFailed = true;
                    break;
                case "BytesWritten":
                    if (long.TryParse(value, out var bw))
                        evt.BytesWritten = bw;
                    else
                        evt.BytesWrittenParseFailed = true;
                    break;
                case "FileSize":
                    var fs = ParseFileSizeValue(value);
                    if (fs.HasValue)
                        evt.FileSize = fs.Value;
                    else
                        evt.FileSizeParseFailed = true;
                    break;
                case "StopReason":
                    evt.StopReason = value;
                    break;
                case "CaptureMethod":
                    evt.CaptureMethod = value;
                    break;
                case "HRESULT":
                    evt.Hresult = value;
                    break;
                case "Reason":
                    evt.Reason = value;
                    break;
                case "ErrorCode":
                    evt.ErrorCode = value;
                    break;
                case "PartialOutputPath":
                    evt.PartialOutputPath = value;
                    break;
                // Unknown fields: ignore
            }
        }

        return hasResult ? evt : null;
    }

    /// <summary>
    /// Parse the RESULT field value.
    /// </summary>
    private static ContinuousEventResult ParseResult(string value)
    {
        return value.ToUpperInvariant() switch
        {
            "STARTED" => ContinuousEventResult.Started,
            "PROGRESS" => ContinuousEventResult.Progress,
            "OK" => ContinuousEventResult.Ok,
            "STOPPED" => ContinuousEventResult.Stopped,
            "FAIL" => ContinuousEventResult.Fail,
            _ => ContinuousEventResult.Unknown
        };
    }

    /// <summary>
    /// Parses a FileSize value such as "123456", "123456 bytes", or "  123456   bytes".
    /// Returns null if no number can be extracted.
    /// </summary>
    private static long? ParseFileSizeValue(string value)
    {
        if (string.IsNullOrEmpty(value))
            return null;

        int start = -1;
        int end = -1;
        for (int i = 0; i < value.Length; i++)
        {
            char c = value[i];
            if (char.IsAsciiDigit(c))
            {
                if (start == -1) start = i;
                end = i;
            }
            else if (start != -1)
            {
                break;
            }
        }

        if (start == -1)
            return null;

        var digits = value.AsSpan(start, end - start + 1);
        if (long.TryParse(digits, out var fs))
            return fs;

        return null;
    }

    /// <summary>
    /// Validate the event sequence according to the state machine rules.
    /// Returns a WgcContinuousSessionSummary with the parsed state and any validation errors.
    /// </summary>
    public static WgcContinuousSessionSummary ValidateAndSummarize(List<WgcContinuousEvent> events)
    {
        var summary = new WgcContinuousSessionSummary();

        if (events.Count == 0)
        {
            summary.State = ContinuousSessionState.MalformedSequence;
            summary.ValidationErrors.Add("No events in stream");
            return summary;
        }

        // Track state machine state
        bool seenStarted = false;
        bool seenTerminalEvent = false;
        bool hasMalformedSequence = false;
        string? firstRecordingId = null;
        long lastFramesCaptured = 0;
        long lastElapsedMs = 0;
        bool hadAnyNumericParseError = false;

        foreach (var evt in events)
        {
            // Track numeric parse errors on every event, regardless of type
            if (evt.HasNumericParseError)
                hadAnyNumericParseError = true;

            switch (evt.Result)
            {
                case ContinuousEventResult.Started:
                    if (seenStarted || seenTerminalEvent)
                    {
                        hasMalformedSequence = true;
                        summary.ValidationErrors.Add("Duplicate or out-of-order STARTED event");
                    }
                    seenStarted = true;
                    firstRecordingId = evt.RecordingId;

                    // Required STARTED fields: RecordingId, Output, Container, Codec, Fps, Width, Height, CaptureMethod
                    if (string.IsNullOrEmpty(evt.RecordingId))
                        summary.ValidationErrors.Add("STARTED event missing required field: RecordingId");
                    if (string.IsNullOrEmpty(evt.Output))
                        summary.ValidationErrors.Add("STARTED event missing required field: Output");
                    if (string.IsNullOrEmpty(evt.Container))
                        summary.ValidationErrors.Add("STARTED event missing required field: Container");
                    if (string.IsNullOrEmpty(evt.Codec))
                        summary.ValidationErrors.Add("STARTED event missing required field: Codec");
                    if (string.IsNullOrEmpty(evt.CaptureMethod))
                        summary.ValidationErrors.Add("STARTED event missing required field: CaptureMethod");

                    // Required numeric STARTED fields
                    if (evt.FpsParseFailed)
                        summary.ValidationErrors.Add("STARTED event failed to parse Fps as integer");
                    else if (!evt.Fps.HasValue || evt.Fps <= 0)
                        summary.ValidationErrors.Add("STARTED event missing required field: Fps (positive integer)");

                    if (evt.WidthParseFailed)
                        summary.ValidationErrors.Add("STARTED event failed to parse Width as integer");
                    else if (!evt.Width.HasValue || evt.Width <= 0)
                        summary.ValidationErrors.Add("STARTED event missing required field: Width (positive integer)");

                    if (evt.HeightParseFailed)
                        summary.ValidationErrors.Add("STARTED event failed to parse Height as integer");
                    else if (!evt.Height.HasValue || evt.Height <= 0)
                        summary.ValidationErrors.Add("STARTED event missing required field: Height (positive integer)");

                    // Copy session metadata to summary
                    summary.RecordingId = evt.RecordingId;
                    summary.OutputPath = evt.Output;
                    summary.Container = evt.Container;
                    summary.Codec = evt.Codec;
                    summary.Fps = evt.Fps;
                    summary.Width = evt.Width;
                    summary.Height = evt.Height;
                    summary.CaptureMethod = evt.CaptureMethod;
                    break;

                case ContinuousEventResult.Progress:
                    if (!seenStarted)
                    {
                        hasMalformedSequence = true;
                        summary.ValidationErrors.Add("PROGRESS event before STARTED");
                    }
                    if (seenTerminalEvent)
                    {
                        hasMalformedSequence = true;
                        summary.ValidationErrors.Add("PROGRESS event after terminal event");
                    }

                    // Required PROGRESS fields: FramesCaptured, ElapsedMs
                    if (evt.FramesCapturedParseFailed)
                        summary.ValidationErrors.Add("PROGRESS event failed to parse FramesCaptured as integer");
                    else if (!evt.FramesCaptured.HasValue)
                        summary.ValidationErrors.Add("PROGRESS event missing required field: FramesCaptured");
                    else if (evt.FramesCaptured < 0)
                        summary.ValidationErrors.Add("PROGRESS event FramesCaptured must be non-negative");

                    if (evt.ElapsedMsParseFailed)
                        summary.ValidationErrors.Add("PROGRESS event failed to parse ElapsedMs as integer");
                    else if (!evt.ElapsedMs.HasValue)
                        summary.ValidationErrors.Add("PROGRESS event missing required field: ElapsedMs");
                    else if (evt.ElapsedMs < 0)
                        summary.ValidationErrors.Add("PROGRESS event ElapsedMs must be non-negative");

                    // Check for regression (only if values are valid and have been tracked)
                    if (evt.FramesCaptured.HasValue && evt.FramesCaptured < lastFramesCaptured && lastFramesCaptured > 0)
                    {
                        hasMalformedSequence = true;
                        summary.ValidationErrors.Add($"FramesCaptured regressed from {lastFramesCaptured} to {evt.FramesCaptured}");
                    }
                    if (evt.ElapsedMs.HasValue && evt.ElapsedMs < lastElapsedMs && lastElapsedMs > 0)
                    {
                        hasMalformedSequence = true;
                        summary.ValidationErrors.Add($"ElapsedMs regressed from {lastElapsedMs} to {evt.ElapsedMs}");
                    }

                    if (evt.FramesCaptured.HasValue)
                        lastFramesCaptured = evt.FramesCaptured.Value;
                    if (evt.ElapsedMs.HasValue)
                        lastElapsedMs = evt.ElapsedMs.Value;
                    break;

                case ContinuousEventResult.Ok:
                    if (!seenStarted)
                    {
                        hasMalformedSequence = true;
                        summary.ValidationErrors.Add("OK event without prior STARTED");
                    }
                    if (seenTerminalEvent)
                    {
                        hasMalformedSequence = true;
                        summary.ValidationErrors.Add("Duplicate terminal event (OK)");
                    }
                    seenTerminalEvent = true;
                    summary.State = ContinuousSessionState.Success;

                    // Required OK fields: FramesCaptured, DurationMs, Width, Height, FileSize or BytesWritten
                    // Task 57: OK terminal event must carry its own Width/Height (no STARTED fallback).
                    if (evt.FramesCapturedParseFailed)
                        summary.ValidationErrors.Add("OK event failed to parse FramesCaptured as integer");
                    else if (!evt.FramesCaptured.HasValue)
                        summary.ValidationErrors.Add("OK event missing required field: FramesCaptured");

                    if (evt.DurationMsParseFailed)
                        summary.ValidationErrors.Add("OK event failed to parse DurationMs as integer");
                    else if (!evt.DurationMs.HasValue)
                        summary.ValidationErrors.Add("OK event missing required field: DurationMs");

                    // Task 57: OK Width must be present on the OK event itself and must be a positive integer
                    if (evt.WidthParseFailed)
                        summary.ValidationErrors.Add("OK event failed to parse Width as integer");
                    else if (!evt.Width.HasValue)
                        summary.ValidationErrors.Add("OK event missing required field: Width (terminal event must carry its own dimensions)");
                    else if (evt.Width <= 0)
                        summary.ValidationErrors.Add("OK event Width must be a positive integer");

                    // Task 57: OK Height must be present on the OK event itself and must be a positive integer
                    if (evt.HeightParseFailed)
                        summary.ValidationErrors.Add("OK event failed to parse Height as integer");
                    else if (!evt.Height.HasValue)
                        summary.ValidationErrors.Add("OK event missing required field: Height (terminal event must carry its own dimensions)");
                    else if (evt.Height <= 0)
                        summary.ValidationErrors.Add("OK event Height must be a positive integer");

                    if (evt.FileSizeParseFailed && evt.BytesWrittenParseFailed)
                        summary.ValidationErrors.Add("OK event failed to parse FileSize and BytesWritten; at least one is required");
                    else if (!evt.FileSize.HasValue && !evt.BytesWritten.HasValue)
                        summary.ValidationErrors.Add("OK event missing required field: FileSize or BytesWritten");

                    summary.FramesCaptured = evt.FramesCaptured ?? lastFramesCaptured;
                    summary.FramesDropped = evt.FramesDropped;
                    summary.DurationMs = evt.DurationMs;
                    summary.BytesWritten = evt.BytesWritten;
                    summary.FileSize = evt.FileSize ?? evt.BytesWritten;
                    summary.Width = evt.Width;
                    summary.Height = evt.Height;
                    summary.StopReason = "duration_reached";
                    break;

                case ContinuousEventResult.Stopped:
                    if (!seenStarted)
                    {
                        hasMalformedSequence = true;
                        summary.ValidationErrors.Add("STOPPED event without prior STARTED");
                    }
                    if (seenTerminalEvent)
                    {
                        hasMalformedSequence = true;
                        summary.ValidationErrors.Add("Duplicate terminal event (STOPPED)");
                    }
                    seenTerminalEvent = true;
                    summary.State = ContinuousSessionState.Stopped;

                    // STOPPED is graceful; PartialOutputPath presence means malformed sequence
                    if (!string.IsNullOrEmpty(evt.PartialOutputPath))
                    {
                        hasMalformedSequence = true;
                        summary.ValidationErrors.Add("STOPPED (graceful) event must not carry PartialOutputPath");
                    }

                    // Task 57: StopReason whitelist. STOPPED is a graceful stop; only user_requested
                    // is permitted currently. Failure categories (timeout, encoding_error, disk_full,
                    // window_not_found, zero_frames, cancelled, error) must use FAIL instead of STOPPED.
                    string[] allowedGracefulStopReasons = new[] { "user_requested" };
                    string[] failureStopReasons = new[]
                    {
                        "timeout", "cancelled", "encoding_error", "disk_full",
                        "window_not_found", "zero_frames", "error"
                    };

                    if (string.IsNullOrEmpty(evt.StopReason))
                    {
                        summary.ValidationErrors.Add("STOPPED event missing required field: StopReason (graceful stop requires 'user_requested')");
                    }
                    else if (!allowedGracefulStopReasons.Contains(evt.StopReason.ToLowerInvariant()))
                    {
                        hasMalformedSequence = true;
                        if (failureStopReasons.Contains(evt.StopReason.ToLowerInvariant()))
                            summary.ValidationErrors.Add($"STOPPED (graceful) event must not carry failure StopReason '{evt.StopReason}'; failure StopReasons belong to FAIL events");
                        else
                            summary.ValidationErrors.Add($"STOPPED event StopReason '{evt.StopReason}' is not a recognized graceful stop reason (expected: user_requested)");
                    }

                    // Required STOPPED fields: FramesCaptured, DurationMs or ElapsedMs, FileSize or BytesWritten
                    if (evt.FramesCapturedParseFailed)
                        summary.ValidationErrors.Add("STOPPED event failed to parse FramesCaptured as integer");
                    else if (!evt.FramesCaptured.HasValue)
                        summary.ValidationErrors.Add("STOPPED event missing required field: FramesCaptured");

                    if (evt.DurationMsParseFailed)
                        summary.ValidationErrors.Add("STOPPED event failed to parse DurationMs as integer");
                    if (evt.ElapsedMsParseFailed)
                        summary.ValidationErrors.Add("STOPPED event failed to parse ElapsedMs as integer");
                    if (!evt.DurationMs.HasValue && !evt.ElapsedMs.HasValue)
                        summary.ValidationErrors.Add("STOPPED event missing required field: DurationMs or ElapsedMs");

                    if (evt.FileSizeParseFailed && evt.BytesWrittenParseFailed)
                        summary.ValidationErrors.Add("STOPPED event failed to parse FileSize and BytesWritten; at least one is required");
                    else if (!evt.FileSize.HasValue && !evt.BytesWritten.HasValue)
                        summary.ValidationErrors.Add("STOPPED event missing required field: FileSize or BytesWritten");

                    summary.FramesCaptured = evt.FramesCaptured ?? lastFramesCaptured;
                    summary.FramesDropped = evt.FramesDropped;
                    summary.DurationMs = evt.DurationMs;
                    summary.BytesWritten = evt.BytesWritten;
                    summary.FileSize = evt.FileSize ?? evt.BytesWritten;
                    summary.Width = evt.Width ?? summary.Width;
                    summary.Height = evt.Height ?? summary.Height;
                    summary.StopReason = evt.StopReason ?? "user_requested";
                    break;

                case ContinuousEventResult.Fail:
                    if (seenTerminalEvent)
                    {
                        hasMalformedSequence = true;
                        summary.ValidationErrors.Add("Duplicate terminal event (FAIL)");
                    }
                    seenTerminalEvent = true;
                    summary.State = ContinuousSessionState.Failed;

                    // FAIL requires Reason or ErrorCode (unless direct FAIL before STARTED, which is still allowed to have them)
                    if (string.IsNullOrEmpty(evt.Reason) && string.IsNullOrEmpty(evt.ErrorCode))
                        summary.ValidationErrors.Add("FAIL event missing required field: Reason or ErrorCode");

                    summary.Hresult = evt.Hresult;
                    summary.Reason = evt.Reason;
                    summary.ErrorCode = evt.ErrorCode;
                    summary.PartialOutputPath = evt.PartialOutputPath;
                    summary.BytesWritten = evt.BytesWritten;
                    summary.FileSize = evt.FileSize ?? evt.BytesWritten;
                    summary.FramesCaptured = evt.FramesCaptured ?? lastFramesCaptured;
                    summary.FramesDropped = evt.FramesDropped;
                    summary.DurationMs = evt.DurationMs;
                    summary.StopReason = evt.StopReason ?? evt.ErrorCode;

                    // When FAIL has a partial output path but no OutputPath, surface partial as the failure output path
                    if (string.IsNullOrEmpty(summary.OutputPath) && !string.IsNullOrEmpty(summary.PartialOutputPath))
                        summary.OutputPath = summary.PartialOutputPath;
                    break;

                case ContinuousEventResult.Unknown:
                    hasMalformedSequence = true;
                    summary.ValidationErrors.Add($"Unknown RESULT value in event");
                    break;
            }

            // Check RecordingId consistency
            if (!string.IsNullOrEmpty(evt.RecordingId) && !string.IsNullOrEmpty(firstRecordingId))
            {
                if (evt.RecordingId != firstRecordingId)
                {
                    hasMalformedSequence = true;
                    summary.ValidationErrors.Add($"RecordingId mismatch: expected '{firstRecordingId}', got '{evt.RecordingId}'");
                }
            }
        }

        // No STARTED and no terminal event at all is malformed
        if (!seenStarted && !seenTerminalEvent)
        {
            hasMalformedSequence = true;
            summary.ValidationErrors.Add("No STARTED event found");
        }

        // Direct FAIL without STARTED is allowed only when it has Reason or ErrorCode.
        // If it reached here without STARTED and without Reason/ErrorCode, the required
        // field check in the FAIL case above already added an error.

        // Missing terminal event
        if (seenStarted && !seenTerminalEvent)
        {
            hasMalformedSequence = true;
            summary.ValidationErrors.Add("No terminal event (OK/STOPPED/FAIL) found");
        }

        summary.HasNumericParseError = hadAnyNumericParseError;
        summary.HasMalformedSequence = hasMalformedSequence;

        // Any validation error (including required-field failures and numeric parse errors)
        // must prevent the summary from remaining in a success/stopped terminal state
        if (hasMalformedSequence || summary.ValidationErrors.Count > 0 || hadAnyNumericParseError)
        {
            summary.State = ContinuousSessionState.MalformedSequence;
        }

        // Explicitly set Success property logic is derived; nothing more to do

        return summary;
    }

    /// <summary>
    /// Convenience method to parse and validate in one call.
    /// </summary>
    public static WgcContinuousSessionSummary ParseAndValidate(string? stdout)
    {
        var events = ParseEvents(stdout);
        return ValidateAndSummarize(events);
    }
}
