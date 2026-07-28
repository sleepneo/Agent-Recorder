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
            WriteLine("SampleRate", info.SampleRate);
            WriteLine("Channels", info.Channels);
            WriteLine("BitsPerSample", info.BitsPerSample);
            WriteLine("FirstSampleAnchorTicks", info.FirstSampleAnchorTicks);
            WriteLine("TimestampFrequency", info.TimestampFrequency);
            WriteLine("BytesWritten", info.BytesWritten);
            WriteLine("CaptureMethod", info.CaptureMethod);
            EndBlock();
        }
    }

    public void Progress(AudioHelperEventInfo info)
    {
        lock (_lock)
        {
            WriteLine("RESULT", "PROGRESS");
            WriteLine("Stage", "AudioCapturing");
            WriteLine("ElapsedMs", info.ElapsedMs);
            WriteLine("WallElapsedMs", info.WallElapsedMs);
            WriteLine("BytesWritten", info.BytesWritten);
            WriteLine("EstimatedGapMs", info.EstimatedGapMs);
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
            if (!string.IsNullOrEmpty(info.PartialOutputPath))
                WriteLine("PartialOutputPath", info.PartialOutputPath);
            if (info.BytesWritten >= 0)
                WriteLine("BytesWritten", info.BytesWritten);
            EndBlock();
        }
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
    public string ErrorCode { get; set; } = "";
    public string Reason { get; set; } = "";
    public string Hresult { get; set; } = "";
    public string PartialOutputPath { get; set; } = "";
}
