using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace AgentRecorder.AudioHelper.Fake;

/// <summary>
/// Deterministic fake of AgentRecorder.AudioHelper.exe for automated tests.
/// Writes a minimal WAV file and emits the audio-helper-v1 event stream.
/// Supports protocol anomalies and hang modes so the parent fail-closed
/// behaviour can be exercised without a real microphone.
/// </summary>
internal static class Program
{
    private const int SampleRate = 16000;
    private const int Channels = 1;
    private const int BitsPerSample = 16;
    private const int BytesPerSample = 2;

    internal static int Main(string[] args)
    {
        // Test hook: allow the probe timeout test to force a hang even when the
        // parent only passes --version. This is triggered by an environment
        // variable rather than a command-line arg because the production probe
        // always invokes the helper with --version.
        if (string.Equals(Environment.GetEnvironmentVariable("AGENT_RECORDER_FAKE_HANG"), "1", StringComparison.Ordinal))
        {
            Thread.Sleep(Timeout.Infinite);
            return 1;
        }

        string? endpointId = null;
        string? outputPath = null;
        string? stopSignalPath = null;
        string? recordingId = null;
        string? failCode = null;
        string? failReason = null;
        bool noTerminal = false;
        int? exitEarlyCode = null;
        bool hang = false;
        bool version = false;

        bool missingResultBlock = false;
        string? missingStartedField = null;
        bool badFrequency = false;
        bool nonPositiveAnchor = false;
        bool nonPositiveBytes = false;
        bool duplicateStarted = false;
        bool progressBeforeStarted = false;
        string? malformedProgressField = null;
        bool progressRegress = false;
        bool estimatedGapDecrease = false;
        bool unknownResult = false;
        bool duplicateTerminal = false;
        bool eventAfterTerminal = false;
        int? okThenExitCode = null;
        bool failThenExitZero = false;
        int? floodEvents = null;
        bool longLine = false;
        bool largeBlock = false;

        for (int i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            if (string.Equals(arg, "--endpoint-id", StringComparison.OrdinalIgnoreCase))
                endpointId = args[++i];
            else if (string.Equals(arg, "--output", StringComparison.OrdinalIgnoreCase))
                outputPath = args[++i];
            else if (string.Equals(arg, "--stop-signal", StringComparison.OrdinalIgnoreCase))
                stopSignalPath = args[++i];
            else if (string.Equals(arg, "--recording-id", StringComparison.OrdinalIgnoreCase))
                recordingId = args[++i];
            else if (string.Equals(arg, "--allowed-root", StringComparison.OrdinalIgnoreCase))
                _ = args[++i];
            else if (string.Equals(arg, "--emit-fail", StringComparison.OrdinalIgnoreCase))
            {
                failCode = args[++i];
                failReason = args[++i];
            }
            else if (string.Equals(arg, "--no-terminal", StringComparison.OrdinalIgnoreCase))
                noTerminal = true;
            else if (string.Equals(arg, "--exit-early", StringComparison.OrdinalIgnoreCase))
                exitEarlyCode = int.Parse(args[++i], CultureInfo.InvariantCulture);
            else if (string.Equals(arg, "--hang", StringComparison.OrdinalIgnoreCase))
                hang = true;
            else if (string.Equals(arg, "--version", StringComparison.OrdinalIgnoreCase))
                version = true;
            else if (string.Equals(arg, "--missing-result-block", StringComparison.OrdinalIgnoreCase))
                missingResultBlock = true;
            else if (string.Equals(arg, "--missing-started-field", StringComparison.OrdinalIgnoreCase))
                missingStartedField = args[++i];
            else if (string.Equals(arg, "--bad-frequency", StringComparison.OrdinalIgnoreCase))
                badFrequency = true;
            else if (string.Equals(arg, "--non-positive-anchor", StringComparison.OrdinalIgnoreCase))
                nonPositiveAnchor = true;
            else if (string.Equals(arg, "--non-positive-bytes", StringComparison.OrdinalIgnoreCase))
                nonPositiveBytes = true;
            else if (string.Equals(arg, "--duplicate-started", StringComparison.OrdinalIgnoreCase))
                duplicateStarted = true;
            else if (string.Equals(arg, "--progress-before-started", StringComparison.OrdinalIgnoreCase))
                progressBeforeStarted = true;
            else if (string.Equals(arg, "--malformed-progress", StringComparison.OrdinalIgnoreCase))
                malformedProgressField = args[++i];
            else if (string.Equals(arg, "--progress-regress", StringComparison.OrdinalIgnoreCase))
                progressRegress = true;
            else if (string.Equals(arg, "--estimated-gap-decrease", StringComparison.OrdinalIgnoreCase))
                estimatedGapDecrease = true;
            else if (string.Equals(arg, "--unknown-result", StringComparison.OrdinalIgnoreCase))
                unknownResult = true;
            else if (string.Equals(arg, "--duplicate-terminal", StringComparison.OrdinalIgnoreCase))
                duplicateTerminal = true;
            else if (string.Equals(arg, "--event-after-terminal", StringComparison.OrdinalIgnoreCase))
                eventAfterTerminal = true;
            else if (string.Equals(arg, "--ok-then-exit", StringComparison.OrdinalIgnoreCase))
                okThenExitCode = int.Parse(args[++i], CultureInfo.InvariantCulture);
            else if (string.Equals(arg, "--fail-then-exit-0", StringComparison.OrdinalIgnoreCase))
                failThenExitZero = true;
            else if (string.Equals(arg, "--flood-events", StringComparison.OrdinalIgnoreCase))
                floodEvents = int.Parse(args[++i], CultureInfo.InvariantCulture);
            else if (string.Equals(arg, "--long-line", StringComparison.OrdinalIgnoreCase))
                longLine = true;
            else if (string.Equals(arg, "--large-block", StringComparison.OrdinalIgnoreCase))
                largeBlock = true;
        }

        recordingId ??= "rec_fake";
        outputPath ??= Path.Combine(Path.GetTempPath(), $"{recordingId}_audio.wav");

        Console.Error.WriteLine($"FAKE stopSignalPath={stopSignalPath} outputPath={outputPath}");

        if (hang)
        {
            Thread.Sleep(Timeout.Infinite);
            return 1;
        }

        if (version)
        {
            Console.Out.WriteLine("Protocol: audio-helper-v1");
            Console.Out.WriteLine($"TimestampFrequency: {Stopwatch.Frequency}");
            Console.Out.WriteLine();
            Console.Out.Flush();
            return 0;
        }

        if (exitEarlyCode.HasValue)
        {
            Console.Error.WriteLine($"FAKE exitEarlyCode={exitEarlyCode.Value}");
            return exitEarlyCode.Value;
        }

        if (!string.IsNullOrEmpty(failCode))
        {
            EmitFail(failCode, failReason ?? "fake failure", recordingId);
            if (failThenExitZero)
                return 0;
            return 1;
        }

        if (missingResultBlock)
        {
            Console.Out.WriteLine("Stage: AudioCapturing");
            Console.Out.WriteLine();
        }

        if (unknownResult)
        {
            WriteLine("RESULT", "BOGUS");
            WriteLine("Stage", "AudioCapturing");
            EndBlock();
        }

        if (longLine)
        {
            Console.Out.WriteLine("Stage: " + new string('x', 5000));
            Console.Out.WriteLine();
        }

        if (largeBlock)
        {
            WriteLine("RESULT", "STARTED");
            for (int i = 0; i < 80; i++)
                WriteLine($"Extra{i}", i);
            EndBlock();
        }

        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
        var startAnchor = nonPositiveAnchor ? 0L : Stopwatch.GetTimestamp();
        long bytesWritten = WriteMinimalWav(outputPath);
        if (nonPositiveBytes)
            bytesWritten = -1;

        if (progressBeforeStarted)
        {
            EmitProgress(recordingId, bytesWritten, 0);
        }

        EmitStarted(recordingId, startAnchor, bytesWritten, missingStartedField, badFrequency);

        if (duplicateStarted)
        {
            EmitStarted(recordingId, startAnchor, bytesWritten, missingStartedField, badFrequency);
        }

        if (floodEvents.HasValue)
        {
            for (int i = 0; i < floodEvents.Value; i++)
                EmitProgress(recordingId, bytesWritten, i);
        }

        if (malformedProgressField != null)
        {
            EmitMalformedProgress(malformedProgressField);
        }

        if (progressRegress)
        {
            EmitProgress(recordingId, bytesWritten, 100);
            EmitProgress(recordingId, bytesWritten, 50);
        }

        if (estimatedGapDecrease)
        {
            EmitProgress(recordingId, bytesWritten, 100, estimatedGapMs: 100, maxEstimatedGapMs: 100);
            EmitProgress(recordingId, bytesWritten, 101, estimatedGapMs: 25, maxEstimatedGapMs: 100);
        }

        if (noTerminal)
        {
            Thread.Sleep(Timeout.Infinite);
            return 1;
        }

        EmitOk(recordingId, bytesWritten, estimatedGapDecrease ? 100 : 0);

        if (duplicateTerminal)
        {
            EmitOk(recordingId, bytesWritten);
        }

        if (eventAfterTerminal)
        {
            EmitProgress(recordingId, bytesWritten, 200);
        }

        if (okThenExitCode.HasValue)
        {
            return okThenExitCode.Value;
        }

        return 0;
    }

    private static long WriteMinimalWav(string path)
    {
        int sampleCount = SampleRate / 10;
        int dataBytes = sampleCount * Channels * BytesPerSample;
        int totalBytes = 44 + dataBytes;

        using var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
        using var writer = new BinaryWriter(stream, Encoding.UTF8);
        writer.Write(Encoding.ASCII.GetBytes("RIFF"));
        writer.Write(totalBytes - 8);
        writer.Write(Encoding.ASCII.GetBytes("WAVE"));
        writer.Write(Encoding.ASCII.GetBytes("fmt "));
        writer.Write(16);
        writer.Write((short)1);
        writer.Write((short)Channels);
        writer.Write(SampleRate);
        writer.Write(SampleRate * Channels * BytesPerSample);
        writer.Write((short)(Channels * BytesPerSample));
        writer.Write((short)BitsPerSample);
        writer.Write(Encoding.ASCII.GetBytes("data"));
        writer.Write(dataBytes);
        for (int i = 0; i < sampleCount * Channels; i++)
            writer.Write((short)0);
        writer.Flush();
        return totalBytes;
    }

    private static void EmitStarted(string recordingId, long anchorTicks, long bytesWritten, string? missingField, bool badFrequency)
    {
        WriteLine("RESULT", "STARTED");
        WriteLine("Stage", "AudioCapturing");
        if (!string.Equals(missingField, "RecordingId", StringComparison.OrdinalIgnoreCase))
            WriteLine("RecordingId", recordingId);
        if (!string.Equals(missingField, "SampleRate", StringComparison.OrdinalIgnoreCase))
            WriteLine("SampleRate", SampleRate);
        if (!string.Equals(missingField, "Channels", StringComparison.OrdinalIgnoreCase))
            WriteLine("Channels", Channels);
        if (!string.Equals(missingField, "BitsPerSample", StringComparison.OrdinalIgnoreCase))
            WriteLine("BitsPerSample", BitsPerSample);
        if (!string.Equals(missingField, "FirstSampleAnchorTicks", StringComparison.OrdinalIgnoreCase))
            WriteLine("FirstSampleAnchorTicks", anchorTicks);
        WriteLine("TimestampFrequency", badFrequency ? Stopwatch.Frequency + 1 : Stopwatch.Frequency);
        if (!string.Equals(missingField, "BytesWritten", StringComparison.OrdinalIgnoreCase))
            WriteLine("BytesWritten", bytesWritten);
        if (!string.Equals(missingField, "CaptureMethod", StringComparison.OrdinalIgnoreCase))
            WriteLine("CaptureMethod", "FAKE_WASAPI_CAPTURE");
        EndBlock();
    }

    private static void EmitProgress(
        string recordingId,
        long bytesWritten,
        long elapsedMs,
        long estimatedGapMs = 0,
        long maxEstimatedGapMs = 0)
    {
        WriteLine("RESULT", "PROGRESS");
        WriteLine("Stage", "AudioCapturing");
        WriteLine("ElapsedMs", elapsedMs);
        WriteLine("WallElapsedMs", elapsedMs);
        WriteLine("BytesWritten", bytesWritten);
        WriteLine("EstimatedGapMs", estimatedGapMs);
        WriteLine("MaxEstimatedGapMs", maxEstimatedGapMs);
        EndBlock();
    }

    private static void EmitMalformedProgress(string field)
    {
        WriteLine("RESULT", "PROGRESS");
        WriteLine("Stage", "AudioCapturing");
        if (string.Equals(field, "ElapsedMs", StringComparison.OrdinalIgnoreCase))
            WriteLine("ElapsedMs", "bad");
        else
            WriteLine("ElapsedMs", 100);
        if (string.Equals(field, "WallElapsedMs", StringComparison.OrdinalIgnoreCase))
            WriteLine("WallElapsedMs", "bad");
        else
            WriteLine("WallElapsedMs", 100);
        if (string.Equals(field, "BytesWritten", StringComparison.OrdinalIgnoreCase))
            WriteLine("BytesWritten", "bad");
        else
            WriteLine("BytesWritten", 100);
        if (string.Equals(field, "EstimatedGapMs", StringComparison.OrdinalIgnoreCase))
            WriteLine("EstimatedGapMs", "bad");
        else
            WriteLine("EstimatedGapMs", 0);
        WriteLine("MaxEstimatedGapMs", 0);
        EndBlock();
    }

    private static void EmitOk(string recordingId, long bytesWritten, long maxEstimatedGapMs = 0)
    {
        long durationMs = (long)(bytesWritten / (double)(SampleRate * Channels * BytesPerSample) * 1000.0);
        WriteLine("RESULT", "OK");
        WriteLine("Stage", "Complete");
        WriteLine("DurationMs", durationMs);
        WriteLine("BytesWritten", bytesWritten);
        WriteLine("EstimatedGapMs", 0);
        WriteLine("MaxEstimatedGapMs", maxEstimatedGapMs);
        EndBlock();
    }

    private static void EmitFail(string code, string reason, string recordingId)
    {
        WriteLine("RESULT", "FAIL");
        WriteLine("ErrorCode", code);
        WriteLine("Reason", reason);
        WriteLine("RecordingId", recordingId);
        WriteLine("BytesWritten", 0);
        EndBlock();
    }

    private static void WriteLine(string key, string value)
    {
        Console.Out.WriteLine($"{key}: {value}");
    }

    private static void WriteLine(string key, long value)
    {
        Console.Out.WriteLine($"{key}: {value.ToString(CultureInfo.InvariantCulture)}");
    }

    private static void EndBlock()
    {
        Console.Out.WriteLine();
        Console.Out.Flush();
    }
}
