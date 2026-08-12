using System.IO;
using Xunit;

namespace AgentRecorder.AudioHelper.Tests;

public class AudioHelperEventWriterTests
{
    [Fact]
    public void Started_EmitsRequiredFields()
    {
        var sw = new StringWriter();
        var writer = new EventWriter(sw, null);

        writer.Started(new AudioHelperEventInfo
        {
            RecordingId = "rec_1",
            SampleRate = 44100,
            Channels = 2,
            BitsPerSample = 16,
            FirstSampleAnchorTicks = 12345,
            TimestampFrequency = 10000000,
            BytesWritten = 100,
            CaptureMethod = "WASAPI_SHARED_CAPTURE",
            CaptureEngine = "wasapi-direct"
        });

        var output = sw.ToString();
        Assert.Contains("RESULT: STARTED", output);
        Assert.Contains("RecordingId: rec_1", output);
        Assert.Contains("SampleRate: 44100", output);
        Assert.Contains("Channels: 2", output);
        Assert.Contains("BitsPerSample: 16", output);
        Assert.Contains("FirstSampleAnchorTicks: 12345", output);
        Assert.Contains("TimestampFrequency: 10000000", output);
        Assert.Contains("BytesWritten: 100", output);
        Assert.Contains("CaptureMethod: WASAPI_SHARED_CAPTURE", output);
        Assert.Contains("CaptureEngine: wasapi-direct", output);
    }

    [Fact]
    public void Fail_EmitsNativeCaptureEngineAndHresult()
    {
        var sw = new StringWriter();
        var writer = new EventWriter(sw, null);

        writer.Fail(new AudioHelperEventInfo
        {
            ErrorCode = "audio_native_initialize_failed",
            Reason = "stage=initialize",
            Hresult = "0x80004005",
            FailureStage = "initialize",
            EndpointId = "{0.0.1.00000000}.{endpoint}",
            PartialOutputPath = "C:\\root\\rec.partial.wav",
            SecondaryFailure = "dispose:InvalidOperationException:0x80131509:Injected",
            CaptureMethod = "WINDOWS_MEDIACAPTURE",
            CaptureEngine = "windows-mediacapture"
        });

        var output = sw.ToString();
        Assert.Contains("RESULT: FAIL", output);
        Assert.Contains("HRESULT: 0x80004005", output);
        Assert.Contains("FailureStage: initialize", output);
        Assert.Contains("EndpointId: {0.0.1.00000000}.{endpoint}", output);
        Assert.Contains("PartialOutputPath: C:\\root\\rec.partial.wav", output);
        Assert.Contains("SecondaryFailure: dispose:InvalidOperationException:0x80131509:Injected", output);
        Assert.Contains("CaptureMethod: WINDOWS_MEDIACAPTURE", output);
        Assert.Contains("CaptureEngine: windows-mediacapture", output);
    }

    [Fact]
    public void Progress_EmitsElapsedAndGap()
    {
        var sw = new StringWriter();
        var writer = new EventWriter(sw, null);

        writer.Progress(new AudioHelperEventInfo
        {
            ElapsedMs = 500,
            WallElapsedMs = 510,
            EstimatedGapMs = 10,
            BytesWritten = 200
        });

        var output = sw.ToString();
        Assert.Contains("RESULT: PROGRESS", output);
        Assert.Contains("ElapsedMs: 500", output);
        Assert.Contains("WallElapsedMs: 510", output);
        Assert.Contains("EstimatedGapMs: 10", output);
    }

    [Fact]
    public void Ok_EmitsTerminalBlock()
    {
        var sw = new StringWriter();
        var writer = new EventWriter(sw, null);

        writer.Ok(new AudioHelperEventInfo
        {
            DurationMs = 1000,
            BytesWritten = 300,
            EstimatedGapMs = 0
        });

        var output = sw.ToString();
        Assert.Contains("RESULT: OK", output);
        Assert.Contains("DurationMs: 1000", output);
    }

    [Fact]
    public void Stopped_EmitsUserRequested()
    {
        var sw = new StringWriter();
        var writer = new EventWriter(sw, null);

        writer.Stopped(new AudioHelperEventInfo
        {
            DurationMs = 1000,
            BytesWritten = 300,
            EstimatedGapMs = 0
        });

        var output = sw.ToString();
        Assert.Contains("RESULT: STOPPED", output);
        Assert.Contains("StopReason: user_requested", output);
    }

    [Fact]
    public void Fail_EmitsErrorCodeAndReason()
    {
        var sw = new StringWriter();
        var writer = new EventWriter(sw, null);

        writer.Fail(new AudioHelperEventInfo
        {
            ErrorCode = "audio_endpoint_not_found",
            Reason = "Endpoint unavailable",
            BytesWritten = 0
        });

        var output = sw.ToString();
        Assert.Contains("RESULT: FAIL", output);
        Assert.Contains("ErrorCode: audio_endpoint_not_found", output);
        Assert.Contains("Reason: Endpoint unavailable", output);
    }

    [Fact]
    public void SequentialEvents_AreSeparatedByBlankLines()
    {
        var sw = new StringWriter();
        var writer = new EventWriter(sw, null);

        writer.Started(new AudioHelperEventInfo { RecordingId = "rec_1" });
        writer.Progress(new AudioHelperEventInfo { });

        var output = sw.ToString();
        var normalized = output.Replace("\r\n", "\n");
        var blocks = normalized.Split(new[] { "\n\n" }, StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(2, blocks.Length);
    }

    [Fact]
    public void InjectedTextWriter_WritesToProvidedOutput()
    {
        var sw = new StringWriter();
        var writer = new EventWriter(sw, null);

        writer.Ok(new AudioHelperEventInfo { DurationMs = 123, BytesWritten = 10, EstimatedGapMs = 0 });

        var output = sw.ToString();
        Assert.Contains("RESULT: OK", output);
        Assert.Contains("DurationMs: 123", output);
    }

    [Fact]
    public void LoopbackEvents_EmitSourceAndDoNotEmitHfpMetadata()
    {
        var sw = new StringWriter();
        var writer = new EventWriter(sw, null);

        var info = new AudioHelperEventInfo
        {
            RecordingId = "rec_loopback",
            AudioSourceKind = AudioSourceKindNames.SystemLoopback,
            SampleRate = 48000,
            Channels = 2,
            BitsPerSample = 32,
            FirstSampleAnchorTicks = 100,
            TimestampFrequency = 10000000,
            BytesWritten = 0,
            CaptureMethod = "WASAPI_SHARED_LOOPBACK",
            CaptureEngine = "wasapi-direct",
            CaptureStrategy = "wasapi-loopback",
            PairEvidence = "must-not-appear",
            AutoHfpPairStatus = "must-not-appear"
        };

        writer.Started(info);
        writer.Progress(info);
        writer.Stopped(info);

        var output = sw.ToString();
        Assert.Contains("AudioSourceKind: system-loopback", output);
        Assert.Contains("CaptureMethod: WASAPI_SHARED_LOOPBACK", output);
        Assert.DoesNotContain("PairEvidence:", output);
        Assert.DoesNotContain("AutoHfpPairStatus:", output);
    }
}
