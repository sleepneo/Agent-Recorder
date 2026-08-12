using System.Diagnostics;
using System.Globalization;

namespace AgentRecorder.Capture;

/// <summary>
/// Parses the blank-line-delimited event stream produced by
/// AgentRecorder.AudioHelper.exe on stdout. Supports \n and \r\n line endings
/// and blank lines that may contain spaces or tabs.
/// </summary>
public static class AudioHelperEventStreamParser
{
    /// <summary>
    /// Parses the helper stdout into a list of structured events.
    /// Never throws - returns an empty list on empty/malformed input.
    /// </summary>
    public static List<AudioHelperEvent> ParseEvents(string? stdout)
    {
        var events = new List<AudioHelperEvent>();
        if (string.IsNullOrWhiteSpace(stdout))
            return events;

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
    /// Parses a single event block (key-value pairs separated by newlines).
    /// </summary>
    internal static AudioHelperEvent? ParseEventBlock(List<string> lines)
    {
        var evt = new AudioHelperEvent();
        bool hasResult = false;
        var seenKeys = new HashSet<string>(StringComparer.Ordinal);

        foreach (var rawLine in lines)
        {
            var line = rawLine.Trim();
            if (line.Length == 0)
                continue;

            int colonIdx = line.IndexOf(": ", StringComparison.Ordinal);
            if (colonIdx <= 0)
                continue;

            var key = line.Substring(0, colonIdx).Trim();
            var value = line.Substring(colonIdx + 2).Trim();

            if (!seenKeys.Add(key))
            {
                evt.DuplicateField = true;
                if (key == "AudioSourceKind")
                    evt.AudioSourceKindDuplicate = true;
                continue;
            }

            if (key == "AudioSourceKind" &&
                (value.Length > 4096 || value.Any(char.IsControl)))
            {
                evt.AudioSourceKindInvalid = true;
                continue;
            }

            // Bound forward-compatible fields before storing them. This keeps
            // unknown or hostile helper output from growing the host's memory
            // or carrying control characters into diagnostics.
            if (key.Length > 128 || value.Length > 4096 || value.Any(char.IsControl))
                continue;

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
                case "AudioSourceKind":
                    evt.AudioSourceKind = value;
                    evt.AudioSourceKindInvalid =
                        value != "microphone" && value != "system-loopback";
                    break;
                case "SampleRate":
                    if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var sr))
                        evt.SampleRate = sr;
                    else
                        evt.SampleRateParseFailed = true;
                    break;
                case "Channels":
                    if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var ch))
                        evt.Channels = ch;
                    else
                        evt.ChannelsParseFailed = true;
                    break;
                case "BitsPerSample":
                    if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var bps))
                        evt.BitsPerSample = bps;
                    else
                        evt.BitsPerSampleParseFailed = true;
                    break;
                case "FirstSampleAnchorTicks":
                    if (long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var fst))
                        evt.FirstSampleAnchorTicks = fst;
                    else
                        evt.FirstSampleAnchorTicksParseFailed = true;
                    break;
                case "TimestampFrequency":
                    if (long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var tf))
                        evt.TimestampFrequency = tf;
                    else
                        evt.TimestampFrequencyParseFailed = true;
                    break;
                case "BytesWritten":
                    if (long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var bw))
                        evt.BytesWritten = bw;
                    else
                        evt.BytesWrittenParseFailed = true;
                    break;
                case "CaptureMethod":
                    evt.CaptureMethod = value;
                    break;
                case "CaptureEngine":
                    evt.CaptureEngine = value;
                    break;
                case "CaptureStrategy":
                    evt.CaptureStrategy = value;
                    break;
                case "PairEvidence":
                    evt.PairEvidence = value;
                    break;
                case "AutoHfpPairStatus":
                    evt.AutoHfpPairStatus = value;
                    break;
                case "AutoHfpPairResultCode":
                    evt.AutoHfpPairResultCode = value;
                    break;
                case "AutoHfpPairTransportClassification":
                    evt.AutoHfpPairTransportClassification = value;
                    break;
                case "RenderPrimeReadyMs":
                    if (long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var rpr))
                        evt.RenderPrimeReadyMs = rpr;
                    else
                        evt.RenderPrimeReadyMsParseFailed = true;
                    break;
                case "ElapsedMs":
                    if (long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var em))
                        evt.ElapsedMs = em;
                    else
                        evt.ElapsedMsParseFailed = true;
                    break;
                case "WallElapsedMs":
                    if (long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var wm))
                        evt.WallElapsedMs = wm;
                    else
                        evt.WallElapsedMsParseFailed = true;
                    break;
                case "EstimatedGapMs":
                    if (long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var eg))
                        evt.EstimatedGapMs = eg;
                    else
                        evt.EstimatedGapMsParseFailed = true;
                    break;
                case "DurationMs":
                    if (long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var dm))
                        evt.DurationMs = dm;
                    else
                        evt.DurationMsParseFailed = true;
                    break;
                case "StopReason":
                    evt.StopReason = value;
                    break;
                case "ErrorCode":
                    evt.ErrorCode = value;
                    break;
                case "Reason":
                    evt.Reason = value;
                    break;
                case "HRESULT":
                    evt.Hresult = value;
                    break;
                case "FailureStage":
                    evt.FailureStage = value;
                    break;
                case "EndpointId":
                    evt.EndpointId = value;
                    break;
                case "PartialOutputPath":
                    evt.PartialOutputPath = value;
                    break;
                case "SecondaryFailure":
                    evt.SecondaryFailure = value;
                    break;
                case "LastCallbackAgeMs":
                    if (long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var lca))
                        evt.LastCallbackAgeMs = lca;
                    else
                        evt.LastCallbackAgeMsParseFailed = true;
                    break;
                case "DiscontinuityCount":
                    if (long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var dc))
                        evt.DiscontinuityCount = dc;
                    else
                        evt.DiscontinuityCountParseFailed = true;
                    break;
                case "RecoveryCount":
                    if (long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var rc))
                        evt.RecoveryCount = rc;
                    else
                        evt.RecoveryCountParseFailed = true;
                    break;
                case "RecoveryAttempts":
                    if (long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var ra))
                        evt.RecoveryAttempts = ra;
                    else
                        evt.RecoveryAttemptsParseFailed = true;
                    break;
                case "GapFilledBytes":
                    if (long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var gfb))
                        evt.GapFilledBytes = gfb;
                    else
                        evt.GapFilledBytesParseFailed = true;
                    break;
                case "GapFilledMs":
                    if (long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var gfm))
                        evt.GapFilledMs = gfm;
                    else
                        evt.GapFilledMsParseFailed = true;
                    break;
                case "MaxEstimatedGapMs":
                    if (long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var meg))
                        evt.MaxEstimatedGapMs = meg;
                    else
                        evt.MaxEstimatedGapMsParseFailed = true;
                    break;
                case "ContinuityStatus":
                    evt.ContinuityStatus = value;
                    break;
                    // Unknown fields: ignore for forward compatibility.
            }
        }

        return hasResult ? evt : null;
    }

    private static AudioHelperEventResult ParseResult(string value)
    {
        return value.ToUpperInvariant() switch
        {
            "STARTED" => AudioHelperEventResult.Started,
            "PROGRESS" => AudioHelperEventResult.Progress,
            "OK" => AudioHelperEventResult.Ok,
            "STOPPED" => AudioHelperEventResult.Stopped,
            "FAIL" => AudioHelperEventResult.Fail,
            _ => AudioHelperEventResult.Unknown
        };
    }

    /// <summary>
    /// Copies optional runtime stream-health/recovery metrics from a terminal
    /// event into the session summary. Missing fields stay null so the host can
    /// distinguish "helper did not report" from a real zero.
    /// </summary>
    private static void CopyStreamHealthMetrics(AudioHelperEvent evt, AudioHelperSessionSummary summary)
    {
        summary.LastCallbackAgeMs = evt.LastCallbackAgeMs;
        summary.DiscontinuityCount = evt.DiscontinuityCount;
        summary.RecoveryCount = evt.RecoveryCount;
        summary.RecoveryAttempts = evt.RecoveryAttempts;
        summary.GapFilledBytes = evt.GapFilledBytes;
        summary.GapFilledMs = evt.GapFilledMs;
        summary.MaxEstimatedGapMs = evt.MaxEstimatedGapMs;
        summary.ContinuityStatus = evt.ContinuityStatus;
    }

    private static void CopyHfpMetadata(AudioHelperEvent evt, AudioHelperSessionSummary summary)
    {
        if (evt.CaptureStrategy != null)
            summary.CaptureStrategy = evt.CaptureStrategy;
        if (evt.PairEvidence != null)
            summary.PairEvidence = evt.PairEvidence;
        if (evt.RenderPrimeReadyMs.HasValue)
            summary.RenderPrimeReadyMs = evt.RenderPrimeReadyMs;
        if (evt.AutoHfpPairStatus != null)
            summary.AutoHfpPairStatus = evt.AutoHfpPairStatus;
        if (evt.AutoHfpPairResultCode != null)
            summary.AutoHfpPairResultCode = evt.AutoHfpPairResultCode;
        if (evt.AutoHfpPairTransportClassification != null)
            summary.AutoHfpPairTransportClassification = evt.AutoHfpPairTransportClassification;
    }

    /// <summary>
    /// Validates the event sequence according to the audio helper state machine.
    /// Returns a summary with the terminal state and any validation errors.
    /// </summary>
    public static AudioHelperSessionSummary ValidateAndSummarize(List<AudioHelperEvent> events)
    {
        var summary = new AudioHelperSessionSummary();

        if (events.Count == 0)
        {
            summary.State = AudioHelperSessionState.MalformedSequence;
            summary.ValidationErrors.Add("No events in stream");
            return summary;
        }

        bool seenStarted = false;
        bool seenTerminalEvent = false;
        bool hasMalformedSequence = false;
        bool hasMalformedSourceMetadata = false;
        bool? sourceAwareStream = null;
        string? firstRecordingId = null;
        string? firstAudioSourceKind = null;

        long lastElapsedMs = -1;
        long lastWallElapsedMs = -1;
        long lastBytesWritten = -1;
        long historicalProgressMaxEstimatedGapMs = -1;

        foreach (var evt in events)
        {
            if (evt.DuplicateField)
            {
                hasMalformedSequence = true;
                summary.ValidationErrors.Add("Duplicate field in event");
            }
            if (evt.AudioSourceKindDuplicate)
            {
                // This field is a trust boundary for the source-aware stream;
                // unlike historical duplicate fields in declarative FAIL, it
                // must fail closed even when ErrorCode is otherwise stable.
                hasMalformedSequence = true;
                hasMalformedSourceMetadata = true;
                summary.ValidationErrors.Add("Duplicate AudioSourceKind field in event");
            }
            if (evt.HasNumericParseError)
                summary.HasNumericParseError = true;
            if (evt.AudioSourceKindInvalid)
            {
                hasMalformedSequence = true;
                hasMalformedSourceMetadata = true;
                summary.ValidationErrors.Add("AudioSourceKind must be 'microphone' or 'system-loopback'");
            }

            if (evt.Result == AudioHelperEventResult.Started)
            {
                sourceAwareStream = evt.AudioSourceKind != null;
            }
            else if (sourceAwareStream.HasValue)
            {
                bool hasSource = evt.AudioSourceKind != null;
                if (hasSource != sourceAwareStream.Value)
                {
                    hasMalformedSequence = true;
                    hasMalformedSourceMetadata = true;
                    summary.ValidationErrors.Add(
                        sourceAwareStream.Value
                            ? $"{evt.Result} event missing AudioSourceKind in source-aware stream"
                            : $"{evt.Result} event introduced AudioSourceKind into a legacy stream");
                }
            }
            else if (evt.AudioSourceKind != null &&
                     !(evt.Result == AudioHelperEventResult.Fail && !seenStarted))
            {
                hasMalformedSequence = true;
                hasMalformedSourceMetadata = true;
                summary.ValidationErrors.Add("AudioSourceKind appeared before STARTED");
            }

            if (evt.AudioSourceKind != null && !evt.AudioSourceKindInvalid)
            {
                if (firstAudioSourceKind == null)
                {
                    firstAudioSourceKind = evt.AudioSourceKind;
                    summary.AudioSourceKind = evt.AudioSourceKind;
                }
                else if (!string.Equals(firstAudioSourceKind, evt.AudioSourceKind, StringComparison.Ordinal))
                {
                    hasMalformedSequence = true;
                    hasMalformedSourceMetadata = true;
                    summary.ValidationErrors.Add(
                        $"AudioSourceKind mismatch: expected '{firstAudioSourceKind}', got '{evt.AudioSourceKind}'");
                }

                if (evt.AudioSourceKind == "system-loopback")
                {
                    if (evt.CaptureMethod != null && evt.CaptureMethod != "WASAPI_SHARED_LOOPBACK")
                    {
                        hasMalformedSequence = true;
                        hasMalformedSourceMetadata = true;
                        summary.ValidationErrors.Add("system-loopback requires CaptureMethod WASAPI_SHARED_LOOPBACK");
                    }
                    if (evt.CaptureEngine != null && evt.CaptureEngine != "wasapi-direct")
                    {
                        hasMalformedSequence = true;
                        hasMalformedSourceMetadata = true;
                        summary.ValidationErrors.Add("system-loopback requires CaptureEngine wasapi-direct");
                    }
                    if (evt.PairEvidence != null ||
                        evt.AutoHfpPairStatus != null ||
                        evt.AutoHfpPairResultCode != null ||
                        evt.AutoHfpPairTransportClassification != null ||
                        evt.RenderPrimeReadyMs.HasValue)
                    {
                        hasMalformedSequence = true;
                        hasMalformedSourceMetadata = true;
                        summary.ValidationErrors.Add("system-loopback events must not contain HFP metadata");
                    }
                }
                else if (evt.AudioSourceKind == "microphone" && evt.CaptureMethod == "WASAPI_SHARED_LOOPBACK")
                {
                    hasMalformedSequence = true;
                    hasMalformedSourceMetadata = true;
                    summary.ValidationErrors.Add("microphone events cannot declare WASAPI_SHARED_LOOPBACK");
                }
            }
            if (evt.RenderPrimeReadyMs.HasValue && evt.RenderPrimeReadyMs.Value < 0)
                summary.ValidationErrors.Add("RenderPrimeReadyMs must be non-negative");

            switch (evt.Result)
            {
                case AudioHelperEventResult.Started:
                    if (seenStarted || seenTerminalEvent)
                    {
                        hasMalformedSequence = true;
                        summary.ValidationErrors.Add("Duplicate or out-of-order STARTED event");
                    }
                    seenStarted = true;
                    firstRecordingId = evt.RecordingId;

                    if (string.IsNullOrEmpty(evt.RecordingId))
                        summary.ValidationErrors.Add("STARTED event missing required field: RecordingId");
                    if (!evt.SampleRate.HasValue || evt.SampleRate <= 0)
                        summary.ValidationErrors.Add("STARTED event missing required field: SampleRate");
                    if (!evt.Channels.HasValue || evt.Channels <= 0)
                        summary.ValidationErrors.Add("STARTED event missing required field: Channels");
                    if (!evt.BitsPerSample.HasValue || evt.BitsPerSample <= 0)
                        summary.ValidationErrors.Add("STARTED event missing required field: BitsPerSample");
                    if (!evt.FirstSampleAnchorTicks.HasValue || evt.FirstSampleAnchorTicks <= 0)
                        summary.ValidationErrors.Add("STARTED event missing required field: FirstSampleAnchorTicks");
                    if (!evt.TimestampFrequency.HasValue || evt.TimestampFrequency <= 0)
                        summary.ValidationErrors.Add("STARTED event missing required field: TimestampFrequency");

                    summary.RecordingId = evt.RecordingId;
                    summary.AudioSourceKind ??= evt.AudioSourceKind;
                    summary.SampleRate = evt.SampleRate;
                    summary.Channels = evt.Channels;
                    summary.BitsPerSample = evt.BitsPerSample;
                    summary.FirstSampleAnchorTicks = evt.FirstSampleAnchorTicks;
                    summary.TimestampFrequency = evt.TimestampFrequency;
                    if (!evt.BytesWritten.HasValue || evt.BytesWritten < 0)
                        summary.ValidationErrors.Add("STARTED event missing required field: BytesWritten");

                    summary.CaptureMethod = evt.CaptureMethod;
                    summary.CaptureEngine = evt.CaptureEngine;
                    CopyHfpMetadata(evt, summary);
                    break;

                case AudioHelperEventResult.Progress:
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

                    if (!evt.ElapsedMs.HasValue)
                        summary.ValidationErrors.Add("PROGRESS event missing required field: ElapsedMs");
                    else if (evt.ElapsedMs.Value < 0)
                        summary.ValidationErrors.Add("PROGRESS event has negative ElapsedMs");
                    else if (evt.ElapsedMs.Value < lastElapsedMs)
                        summary.ValidationErrors.Add("PROGRESS event ElapsedMs regressed");

                    if (!evt.WallElapsedMs.HasValue)
                        summary.ValidationErrors.Add("PROGRESS event missing required field: WallElapsedMs");
                    else if (evt.WallElapsedMs.Value < 0)
                        summary.ValidationErrors.Add("PROGRESS event has negative WallElapsedMs");
                    else if (evt.WallElapsedMs.Value < lastWallElapsedMs)
                        summary.ValidationErrors.Add("PROGRESS event WallElapsedMs regressed");

                    if (!evt.BytesWritten.HasValue)
                        summary.ValidationErrors.Add("PROGRESS event missing required field: BytesWritten");
                    else if (evt.BytesWritten.Value < 0)
                        summary.ValidationErrors.Add("PROGRESS event has negative BytesWritten");
                    else if (evt.BytesWritten.Value < lastBytesWritten)
                        summary.ValidationErrors.Add("PROGRESS event BytesWritten regressed");

                    if (!evt.EstimatedGapMs.HasValue)
                        summary.ValidationErrors.Add("PROGRESS event missing required field: EstimatedGapMs");
                    else if (evt.EstimatedGapMs.Value < 0)
                        summary.ValidationErrors.Add("PROGRESS event has negative EstimatedGapMs");

                    if (!evt.MaxEstimatedGapMs.HasValue)
                        summary.ValidationErrors.Add("PROGRESS event missing required field: MaxEstimatedGapMs");
                    else if (evt.MaxEstimatedGapMs.Value < 0)
                        summary.ValidationErrors.Add("PROGRESS event has negative MaxEstimatedGapMs");
                    else if (historicalProgressMaxEstimatedGapMs >= 0 &&
                             evt.MaxEstimatedGapMs.Value < historicalProgressMaxEstimatedGapMs)
                        summary.ValidationErrors.Add("PROGRESS event MaxEstimatedGapMs regressed");

                    if (evt.EstimatedGapMs.HasValue && evt.EstimatedGapMs.Value >= 0 &&
                        evt.MaxEstimatedGapMs.HasValue && evt.MaxEstimatedGapMs.Value >= 0 &&
                        evt.MaxEstimatedGapMs.Value < evt.EstimatedGapMs.Value)
                        summary.ValidationErrors.Add("PROGRESS event MaxEstimatedGapMs below EstimatedGapMs");

                    if (evt.ElapsedMs.HasValue && evt.ElapsedMs.Value >= 0)
                        lastElapsedMs = evt.ElapsedMs.Value;
                    if (evt.WallElapsedMs.HasValue && evt.WallElapsedMs.Value >= 0)
                        lastWallElapsedMs = evt.WallElapsedMs.Value;
                    if (evt.BytesWritten.HasValue && evt.BytesWritten.Value >= 0)
                        lastBytesWritten = evt.BytesWritten.Value;
                    if (evt.MaxEstimatedGapMs.HasValue && evt.MaxEstimatedGapMs.Value >= 0)
                        historicalProgressMaxEstimatedGapMs = Math.Max(
                            historicalProgressMaxEstimatedGapMs,
                            evt.MaxEstimatedGapMs.Value);
                    break;

                case AudioHelperEventResult.Ok:
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
                    summary.State = AudioHelperSessionState.Success;
                    summary.DurationMs = evt.DurationMs;
                    summary.BytesWritten = evt.BytesWritten;
                    summary.EstimatedGapMs = evt.EstimatedGapMs;
                    summary.StopReason = "duration_reached";
                    summary.CaptureMethod ??= evt.CaptureMethod;
                    summary.CaptureEngine ??= evt.CaptureEngine;
                    CopyHfpMetadata(evt, summary);
                    CopyStreamHealthMetrics(evt, summary);
                    ValidateTerminalGapMetrics(evt, historicalProgressMaxEstimatedGapMs, summary);
                    break;

                case AudioHelperEventResult.Stopped:
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
                    summary.State = AudioHelperSessionState.Stopped;
                    summary.DurationMs = evt.DurationMs ?? evt.ElapsedMs;
                    summary.BytesWritten = evt.BytesWritten;
                    summary.EstimatedGapMs = evt.EstimatedGapMs;
                    summary.StopReason = evt.StopReason ?? "user_requested";
                    summary.CaptureMethod ??= evt.CaptureMethod;
                    summary.CaptureEngine ??= evt.CaptureEngine;
                    CopyHfpMetadata(evt, summary);
                    CopyStreamHealthMetrics(evt, summary);
                    ValidateTerminalGapMetrics(evt, historicalProgressMaxEstimatedGapMs, summary);
                    break;

                case AudioHelperEventResult.Fail:
                    if (!seenStarted)
                    {
                        hasMalformedSequence = true;
                        summary.ValidationErrors.Add("FAIL event without prior STARTED");
                    }
                    if (seenTerminalEvent)
                    {
                        hasMalformedSequence = true;
                        summary.ValidationErrors.Add("Duplicate terminal event (FAIL)");
                    }
                    seenTerminalEvent = true;
                    summary.State = AudioHelperSessionState.Failed;
                    summary.ErrorCode = evt.ErrorCode;
                    summary.Reason = evt.Reason;
                    summary.Hresult = evt.Hresult;
                    summary.FailureStage = evt.FailureStage;
                    summary.EndpointId = evt.EndpointId;
                    summary.PartialOutputPath = evt.PartialOutputPath;
                    summary.SecondaryFailure = evt.SecondaryFailure;
                    summary.BytesWritten = evt.BytesWritten;
                    summary.DurationMs = evt.DurationMs;
                    summary.StopReason = evt.StopReason ?? evt.ErrorCode;
                    summary.CaptureMethod ??= evt.CaptureMethod;
                    summary.CaptureEngine ??= evt.CaptureEngine;
                    CopyHfpMetadata(evt, summary);
                    CopyStreamHealthMetrics(evt, summary);

                    if (string.IsNullOrEmpty(evt.ErrorCode))
                        summary.ValidationErrors.Add("FAIL event missing required field: ErrorCode");
                    ValidateTerminalGapMetrics(evt, historicalProgressMaxEstimatedGapMs, summary);
                    break;

                case AudioHelperEventResult.Unknown:
                    hasMalformedSequence = true;
                    summary.ValidationErrors.Add("Unknown RESULT value in event");
                    break;
            }

            if (!string.IsNullOrEmpty(evt.RecordingId) && !string.IsNullOrEmpty(firstRecordingId))
            {
                if (evt.RecordingId != firstRecordingId)
                {
                    hasMalformedSequence = true;
                    summary.ValidationErrors.Add($"RecordingId mismatch: expected '{firstRecordingId}', got '{evt.RecordingId}'");
                }
            }
        }

        if (!seenStarted && !seenTerminalEvent)
        {
            hasMalformedSequence = true;
            summary.ValidationErrors.Add("No STARTED event found");
        }

        if (seenStarted && !seenTerminalEvent)
        {
            hasMalformedSequence = true;
            summary.ValidationErrors.Add("No terminal event (OK/STOPPED/FAIL) found");
        }

        if (historicalProgressMaxEstimatedGapMs >= 0 &&
            (!summary.MaxEstimatedGapMs.HasValue ||
             summary.MaxEstimatedGapMs.Value < historicalProgressMaxEstimatedGapMs))
        {
            // Keep the summary's historical max distinct from the terminal
            // current gap even when the terminal event is malformed.
            summary.MaxEstimatedGapMs = historicalProgressMaxEstimatedGapMs;
        }

        summary.HasMalformedSequence = hasMalformedSequence;

        bool hasDeclarativeFailure = summary.State == AudioHelperSessionState.Failed && !string.IsNullOrEmpty(summary.ErrorCode);

        if (hasMalformedSourceMetadata)
        {
            summary.State = AudioHelperSessionState.MalformedSequence;
        }
        else if ((hasMalformedSequence || summary.ValidationErrors.Count > 0 || summary.HasNumericParseError) && !hasDeclarativeFailure)
        {
            summary.State = AudioHelperSessionState.MalformedSequence;
        }

        if (summary.FirstSampleAnchorTicks.HasValue &&
            summary.TimestampFrequency.HasValue &&
            summary.TimestampFrequency.Value != Stopwatch.Frequency)
        {
            summary.ValidationErrors.Add($"TimestampFrequency mismatch: helper={summary.TimestampFrequency.Value}, host={Stopwatch.Frequency}");
            if (!hasDeclarativeFailure)
                summary.State = AudioHelperSessionState.MalformedSequence;
        }

        return summary;
    }

    private static void ValidateTerminalGapMetrics(
        AudioHelperEvent evt,
        long historicalProgressMaxEstimatedGapMs,
        AudioHelperSessionSummary summary)
    {
        if (evt.EstimatedGapMs.HasValue && evt.EstimatedGapMs.Value < 0)
            summary.ValidationErrors.Add("Terminal event has negative EstimatedGapMs");

        if (!evt.MaxEstimatedGapMs.HasValue)
        {
            if (historicalProgressMaxEstimatedGapMs >= 0)
                summary.ValidationErrors.Add("Terminal event missing required field: MaxEstimatedGapMs");
            return;
        }

        if (evt.MaxEstimatedGapMs.Value < 0)
        {
            summary.ValidationErrors.Add("Terminal event has negative MaxEstimatedGapMs");
            return;
        }

        if (evt.EstimatedGapMs.HasValue && evt.EstimatedGapMs.Value >= 0 &&
            evt.MaxEstimatedGapMs.Value < evt.EstimatedGapMs.Value)
            summary.ValidationErrors.Add("Terminal event MaxEstimatedGapMs below EstimatedGapMs");

        if (historicalProgressMaxEstimatedGapMs >= 0 &&
            evt.MaxEstimatedGapMs.Value < historicalProgressMaxEstimatedGapMs)
            summary.ValidationErrors.Add("Terminal MaxEstimatedGapMs below last PROGRESS historical max");
    }

    /// <summary>
    /// Convenience method to parse and validate in one call.
    /// </summary>
    public static AudioHelperSessionSummary ParseAndValidate(string? stdout)
    {
        var events = ParseEvents(stdout);
        return ValidateAndSummarize(events);
    }
}

/// <summary>
/// Terminal state of an audio helper session as derived from its event stream.
/// </summary>
public enum AudioHelperSessionState
{
    Unknown,
    Success,
    Stopped,
    Failed,
    MalformedSequence
}

/// <summary>
/// Summary of a parsed audio helper event stream.
/// </summary>
public sealed class AudioHelperSessionSummary
{
    public AudioHelperSessionState State { get; set; }
    public List<string> ValidationErrors { get; } = new();
    public bool HasMalformedSequence { get; set; }
    public bool HasNumericParseError { get; set; }

    public string? RecordingId { get; set; }
    public string? AudioSourceKind { get; set; }
    public int? SampleRate { get; set; }
    public int? Channels { get; set; }
    public int? BitsPerSample { get; set; }
    public long? FirstSampleAnchorTicks { get; set; }
    public long? TimestampFrequency { get; set; }
    public string? CaptureMethod { get; set; }
    public string? CaptureEngine { get; set; }
    public string? CaptureStrategy { get; set; }
    public string? PairEvidence { get; set; }
    public string? AutoHfpPairStatus { get; set; }
    public string? AutoHfpPairResultCode { get; set; }
    public string? AutoHfpPairTransportClassification { get; set; }
    public long? RenderPrimeReadyMs { get; set; }
    public long? DurationMs { get; set; }
    public long? BytesWritten { get; set; }
    public long? EstimatedGapMs { get; set; }
    public string? StopReason { get; set; }
    public string? ErrorCode { get; set; }
    public string? Reason { get; set; }
    public string? Hresult { get; set; }
    public string? FailureStage { get; set; }
    public string? EndpointId { get; set; }
    public string? PartialOutputPath { get; set; }
    public string? SecondaryFailure { get; set; }

    // Runtime stream-health and recovery metrics from the terminal event.
    public long? LastCallbackAgeMs { get; set; }
    public long? DiscontinuityCount { get; set; }
    public long? RecoveryCount { get; set; }
    public long? RecoveryAttempts { get; set; }
    public long? GapFilledBytes { get; set; }
    public long? GapFilledMs { get; set; }
    public long? MaxEstimatedGapMs { get; set; }

    /// <summary>"continuous" or "degraded" as declared by the helper; null when not reported.</summary>
    public string? ContinuityStatus { get; set; }
}
