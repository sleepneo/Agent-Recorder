using System.Globalization;

namespace AgentRecorder.AudioHelper;

/// <summary>
/// Emits blank-line-delimited UTF-8 event blocks on stdout for the audio helper
/// IPC protocol. All writes are flushed immediately so the parent process can
/// stream-parse them.
/// </summary>
internal sealed class EventWriter
{
    private readonly TextWriter? _output;
    private readonly TextWriter? _error;
    private readonly object _lock = new();

    public EventWriter()
        : this(null, null)
    {
    }

    public EventWriter(TextWriter? output, TextWriter? error)
    {
        _output = output;
        _error = error;
    }

    private TextWriter OutWriter => _output ?? Console.Out;

    public void Started(AudioHelperEventInfo info)
    {
        lock (_lock)
        {
            WriteLine("RESULT", "STARTED");
            WriteLine("Stage", "AudioCapturing");
            WriteLine("RecordingId", info.RecordingId);
            WriteLine("AudioSourceKind", info.AudioSourceKind);
            WriteLine("SampleRate", info.SampleRate);
            WriteLine("Channels", info.Channels);
            WriteLine("BitsPerSample", info.BitsPerSample);
            WriteLine("FirstSampleAnchorTicks", info.FirstSampleAnchorTicks);
            WriteLine("TimestampFrequency", info.TimestampFrequency);
            WriteLine("BytesWritten", info.BytesWritten);
            WriteLine("CaptureMethod", info.CaptureMethod);
            WriteLine("CaptureEngine", info.CaptureEngine);
            WriteHfpMetadata(info);
            EndBlock();
        }
    }

    public void Progress(AudioHelperEventInfo info)
    {
        lock (_lock)
        {
            WriteLine("RESULT", "PROGRESS");
            WriteLine("Stage", "AudioCapturing");
            WriteLine("AudioSourceKind", info.AudioSourceKind);
            WriteLine("ElapsedMs", info.ElapsedMs);
            WriteLine("WallElapsedMs", info.WallElapsedMs);
            WriteLine("BytesWritten", info.BytesWritten);
            WriteLine("EstimatedGapMs", info.EstimatedGapMs);
            WriteLine("LastCallbackAgeMs", info.LastCallbackAgeMs);
            WriteLine("DiscontinuityCount", info.DiscontinuityCount);
            WriteLine("RecoveryCount", info.RecoveryCount);
            WriteLine("GapFilledBytes", info.GapFilledBytes);
            WriteLine("GapFilledMs", info.GapFilledMs);
            WriteLine("MaxEstimatedGapMs", info.MaxEstimatedGapMs);
            WriteLine("ContinuityStatus", string.IsNullOrEmpty(info.ContinuityStatus) ? "continuous" : info.ContinuityStatus);
            WriteLine("CaptureEngine", info.CaptureEngine);
            WriteHfpMetadata(info);
            EndBlock();
        }
    }

    public void Ok(AudioHelperEventInfo info)
    {
        lock (_lock)
        {
            WriteLine("RESULT", "OK");
            WriteLine("Stage", "Complete");
            WriteLine("DurationMs", info.DurationMs);
            WriteLine("BytesWritten", info.BytesWritten);
            WriteLine("EstimatedGapMs", info.EstimatedGapMs);
            WriteLine("AudioSourceKind", info.AudioSourceKind);
            WriteLine("CaptureMethod", info.CaptureMethod);
            WriteLine("CaptureEngine", info.CaptureEngine);
            WriteHfpMetadata(info);
            WriteTerminalMetrics(info);
            EndBlock();
        }
    }

    public void Stopped(AudioHelperEventInfo info)
    {
        lock (_lock)
        {
            WriteLine("RESULT", "STOPPED");
            WriteLine("StopReason", "user_requested");
            WriteLine("DurationMs", info.DurationMs);
            WriteLine("BytesWritten", info.BytesWritten);
            WriteLine("EstimatedGapMs", info.EstimatedGapMs);
            WriteLine("AudioSourceKind", info.AudioSourceKind);
            WriteLine("CaptureMethod", info.CaptureMethod);
            WriteLine("CaptureEngine", info.CaptureEngine);
            WriteHfpMetadata(info);
            WriteTerminalMetrics(info);
            EndBlock();
        }
    }

    public void Fail(AudioHelperEventInfo info)
    {
        lock (_lock)
        {
            WriteLine("RESULT", "FAIL");
            if (!string.IsNullOrEmpty(info.ErrorCode))
                WriteLine("ErrorCode", info.ErrorCode);
            if (!string.IsNullOrEmpty(info.Reason))
                WriteLine("Reason", info.Reason);
            if (!string.IsNullOrEmpty(info.Hresult))
                WriteLine("HRESULT", info.Hresult);
            if (!string.IsNullOrEmpty(info.FailureStage))
                WriteLine("FailureStage", info.FailureStage);
            if (!string.IsNullOrEmpty(info.EndpointId))
                WriteLine("EndpointId", info.EndpointId);
            if (!string.IsNullOrEmpty(info.PartialOutputPath))
                WriteLine("PartialOutputPath", info.PartialOutputPath);
            if (!string.IsNullOrEmpty(info.SecondaryFailure))
                WriteLine("SecondaryFailure", info.SecondaryFailure);
            if (info.BytesWritten >= 0)
                WriteLine("BytesWritten", info.BytesWritten);
            if (!string.IsNullOrEmpty(info.CaptureMethod))
                WriteLine("CaptureMethod", info.CaptureMethod);
            if (!string.IsNullOrEmpty(info.CaptureEngine))
                WriteLine("CaptureEngine", info.CaptureEngine);
            if (!string.IsNullOrEmpty(info.AudioSourceKind))
                WriteLine("AudioSourceKind", info.AudioSourceKind);
            WriteHfpMetadata(info);
            WriteTerminalMetrics(info);
            EndBlock();
        }
    }

    /// <summary>
    /// Terminal continuity/recovery metrics shared by OK/STOPPED/FAIL so the
    /// host can propagate the real stream health regardless of the outcome.
    /// </summary>
    private void WriteTerminalMetrics(AudioHelperEventInfo info)
    {
        WriteLine("ContinuityStatus", string.IsNullOrEmpty(info.ContinuityStatus) ? "continuous" : info.ContinuityStatus);
        WriteLine("RecoveryCount", info.RecoveryCount);
        WriteLine("RecoveryAttempts", info.RecoveryAttempts);
        WriteLine("GapFilledBytes", info.GapFilledBytes);
        WriteLine("GapFilledMs", info.GapFilledMs);
        WriteLine("DiscontinuityCount", info.DiscontinuityCount);
        WriteLine("MaxEstimatedGapMs", info.MaxEstimatedGapMs);
    }

    private void WriteHfpMetadata(AudioHelperEventInfo info)
    {
        if (info.AudioSourceKind == AudioSourceKindNames.SystemLoopback)
            return;

        if (!string.IsNullOrEmpty(info.CaptureStrategy))
            WriteLine("CaptureStrategy", info.CaptureStrategy);
        if (!string.IsNullOrEmpty(info.PairEvidence))
            WriteLine("PairEvidence", info.PairEvidence);
        if (!string.IsNullOrEmpty(info.AutoHfpPairStatus))
            WriteLine("AutoHfpPairStatus", info.AutoHfpPairStatus);
        if (!string.IsNullOrEmpty(info.AutoHfpPairResultCode))
            WriteLine("AutoHfpPairResultCode", info.AutoHfpPairResultCode);
        if (!string.IsNullOrEmpty(info.AutoHfpPairTransportClassification))
            WriteLine("AutoHfpPairTransportClassification", info.AutoHfpPairTransportClassification);
        if (info.RenderPrimeReadyMs >= 0)
            WriteLine("RenderPrimeReadyMs", info.RenderPrimeReadyMs);
    }

    public void WriteRaw(string text)
    {
        lock (_lock)
        {
            OutWriter.Write(text);
            OutWriter.Flush();
        }
    }

    private void WriteLine(string key, string value)
    {
        OutWriter.WriteLine($"{key}: {value}");
    }

    private void WriteLine(string key, long value)
    {
        OutWriter.WriteLine($"{key}: {value.ToString(CultureInfo.InvariantCulture)}");
    }

    private void WriteLine(string key, int value)
    {
        OutWriter.WriteLine($"{key}: {value.ToString(CultureInfo.InvariantCulture)}");
    }

    private void EndBlock()
    {
        OutWriter.WriteLine();
        OutWriter.Flush();
    }
}

internal sealed class AudioHelperEventInfo
{
    public string RecordingId { get; set; } = "";
    public string AudioSourceKind { get; set; } = AudioSourceKindNames.Microphone;
    public int SampleRate { get; set; }
    public int Channels { get; set; }
    public int BitsPerSample { get; set; }
    public long FirstSampleAnchorTicks { get; set; }
    public long TimestampFrequency { get; set; }
    public long BytesWritten { get; set; }
    public long ElapsedMs { get; set; }
    public long WallElapsedMs { get; set; }
    public long EstimatedGapMs { get; set; }
    public long DurationMs { get; set; }
    public string CaptureMethod { get; set; } = "WASAPI_SHARED_CAPTURE";
    public string CaptureEngine { get; set; } = "wasapi-direct";
    public string CaptureStrategy { get; set; } = "";
    public string PairEvidence { get; set; } = "";
    public string AutoHfpPairStatus { get; set; } = "";
    public string AutoHfpPairResultCode { get; set; } = "";
    public string AutoHfpPairTransportClassification { get; set; } = "";
    public long RenderPrimeReadyMs { get; set; } = -1;
    public string ErrorCode { get; set; } = "";
    public string Reason { get; set; } = "";
    public string Hresult { get; set; } = "";
    public string FailureStage { get; set; } = "";
    public string EndpointId { get; set; } = "";
    public string PartialOutputPath { get; set; } = "";
    public string SecondaryFailure { get; set; } = "";

    // Runtime stream-health and recovery metrics.
    public long LastCallbackAgeMs { get; set; }
    public long DiscontinuityCount { get; set; }
    public long RecoveryCount { get; set; }
    public long RecoveryAttempts { get; set; }
    public long GapFilledBytes { get; set; }
    public long GapFilledMs { get; set; }
    public long MaxEstimatedGapMs { get; set; }
    public string ContinuityStatus { get; set; } = "continuous";
}
