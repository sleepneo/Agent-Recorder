using System;
using System.IO;
using System.Threading;
using AgentRecorder.Capture;
using AgentRecorder.Infrastructure;
using Xunit;

namespace AgentRecorder.Tests;

public class WasapiAudioCaptureWorkerTests : IDisposable
{
    private readonly string _tmpDir;

    public WasapiAudioCaptureWorkerTests()
    {
        _tmpDir = Path.Combine(Path.GetTempPath(), $"wasapi-worker-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tmpDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tmpDir, true); } catch { }
    }

    [Fact]
    public void BuildArgs_ProductionAutoPairIsEnabledOnceAndIsAStandaloneSwitch()
    {
        var worker = new WasapiAudioCaptureWorker();
        Assert.True(worker.EnableAutomaticHfpPairDiscovery);

        var args = WasapiAudioCaptureWorker.BuildArgs(
            "capture", "output.wav", "root", "stop.signal", "recording", "--auto-hfp-pair", true);

        Assert.Equal(1, args.Count(arg => string.Equals(arg, "--auto-hfp-pair", StringComparison.OrdinalIgnoreCase)));
        var index = args.FindIndex(arg => string.Equals(arg, "--auto-hfp-pair", StringComparison.OrdinalIgnoreCase));
        Assert.True(index >= 0);
        Assert.Equal("--recording-id", args[index - 2]);
        Assert.Equal("recording", args[index - 1]);
    }

    [Fact]
    public void Start_FakeHelper_EmitsAudioReadyAndSetsAnchor()
    {
        var outputPath = Path.Combine(_tmpDir, "audio.wav");
        var worker = new WasapiAudioCaptureWorker
        {
            HelperExePathOverride = FakeHelperExePath(),
            SkipMicrophoneStatusMonitor = true
        };

        bool audioReady = false;
        worker.AudioReady += () => audioReady = true;

        worker.Start(CaptureConfigWithMic(), outputPath);

        Assert.True(SpinWait.SpinUntil(() => audioReady, TimeSpan.FromSeconds(5)), "AudioReady was not raised");
        Assert.True(worker.IsAudioReady);
        Assert.NotNull(worker.ReadyAtUtc);
        Assert.True(worker.MediaStartAnchorTicks > 0, "Media start anchor was not set");

        worker.Stop();
        worker.Dispose();
    }

    [Fact]
    public void Start_FakeHelper_Stop_ConvergesToSuccess()
    {
        var outputPath = Path.Combine(_tmpDir, "audio.wav");
        var worker = new WasapiAudioCaptureWorker
        {
            HelperExePathOverride = FakeHelperExePath(),
            SkipMicrophoneStatusMonitor = true
        };

        worker.Start(CaptureConfigWithMic(), outputPath);
        Assert.True(SpinWait.SpinUntil(() => worker.IsAudioReady, TimeSpan.FromSeconds(5)));

        worker.Stop();
        Assert.True(SpinWait.SpinUntil(() => worker.HasExited, TimeSpan.FromSeconds(5)), "Worker did not exit");

        var summary = worker.GetTerminalSummary();
        Assert.NotNull(summary);
        Assert.True(summary.State == AudioHelperSessionState.Success || summary.State == AudioHelperSessionState.Stopped,
            $"Expected success/stopped terminal state, got {summary.State}. ValidationErrors: {string.Join("; ", summary.ValidationErrors)}");
        Assert.True(File.Exists(outputPath), "Output WAV was not published");

        worker.Dispose();
    }

    [Fact]
    public void Start_FakeHelper_CurrentEstimatedGapDecrease_IsAcceptedAndKeepsHistoricalMax()
    {
        var outputPath = Path.Combine(_tmpDir, "current-gap-decrease.wav");
        var worker = CreateWorker("--estimated-gap-decrease");

        worker.Start(CaptureConfigWithMic(), outputPath);
        Assert.True(SpinWait.SpinUntil(() => worker.HasExited, TimeSpan.FromSeconds(5)), "Worker did not exit");

        var summary = worker.GetTerminalSummary();
        Assert.NotNull(summary);
        Assert.NotEqual("audio_helper_protocol_error", summary.ErrorCode);
        Assert.True(summary.State == AudioHelperSessionState.Success || summary.State == AudioHelperSessionState.Stopped,
            $"Current gap decrease should be valid. State={summary.State}; ValidationErrors: {string.Join("; ", summary.ValidationErrors)}");
        Assert.Equal(0, summary.EstimatedGapMs);
        Assert.Equal(100, summary.MaxEstimatedGapMs);

        worker.Dispose();
    }

    [Fact]
    public void Start_FakeHelper_FailEvent_DoesNotFallbackAndReportsFailure()
    {
        var outputPath = Path.Combine(_tmpDir, "audio.wav");
        var worker = new WasapiAudioCaptureWorker
        {
            HelperExePathOverride = FakeHelperExePath(),
            HelperArgumentsOverride = "--emit-fail audio_endpoint_not_found \"simulated helper failure\"",
            SkipMicrophoneStatusMonitor = true
        };

        bool audioReady = false;
        worker.AudioReady += () => audioReady = true;

        bool exited = false;
        worker.NaturalExit += (_, _) => exited = true;

        worker.Start(CaptureConfigWithMic(), outputPath);

        Assert.True(SpinWait.SpinUntil(() => worker.HasExited, TimeSpan.FromSeconds(5)), "Worker did not exit");
        Assert.True(SpinWait.SpinUntil(() => exited, TimeSpan.FromSeconds(2)) || exited, "NaturalExit was not raised");

        // Fail event must arrive before AudioReady; there is no dshow fallback.
        Assert.False(audioReady, "AudioReady should not be raised for a failing helper");

        var summary = worker.GetTerminalSummary();
        Assert.NotNull(summary);
        Assert.Equal(AudioHelperSessionState.Failed, summary.State);
        Assert.Equal("audio_endpoint_not_found", summary.ErrorCode);
        Assert.False(File.Exists(outputPath), "Output WAV should not be published on failure");

        worker.Dispose();
    }

    [Fact]
    public void Start_UnmappableDeviceId_ThrowsApiException()
    {
        var worker = new WasapiAudioCaptureWorker { SkipMicrophoneStatusMonitor = true };
        var cfg = new CaptureConfig { Microphone = true, MicDevice = "not-a-dshow-id" };

        var ex = Assert.Throws<ApiException>(() => worker.Start(cfg, Path.Combine(_tmpDir, "audio.wav")));

        Assert.Equal("audio_endpoint_id_unmappable", ex.Code);
    }

    [Fact]
    public void Start_MissingHelper_ThrowsApiException()
    {
        var worker = new WasapiAudioCaptureWorker
        {
            HelperExePathOverride = Path.Combine(_tmpDir, "nonexistent.exe"),
            SkipMicrophoneStatusMonitor = true
        };

        var ex = Assert.Throws<ApiException>(() => worker.Start(CaptureConfigWithMic(), Path.Combine(_tmpDir, "audio.wav")));

        Assert.Equal("audio_helper_unavailable", ex.Code);
    }

    [Fact]
    public void Start_FakeHelper_EmitsTerminalSummaryWithFirstSampleAnchor()
    {
        var outputPath = Path.Combine(_tmpDir, "audio.wav");
        var worker = new WasapiAudioCaptureWorker
        {
            HelperExePathOverride = FakeHelperExePath(),
            SkipMicrophoneStatusMonitor = true
        };

        worker.Start(CaptureConfigWithMic(), outputPath);
        Assert.True(SpinWait.SpinUntil(() => worker.IsAudioReady, TimeSpan.FromSeconds(5)));

        worker.Stop();
        Assert.True(SpinWait.SpinUntil(() => worker.HasExited, TimeSpan.FromSeconds(5)));

        var summary = worker.GetTerminalSummary();
        Assert.NotNull(summary);
        Assert.True(summary.FirstSampleAnchorTicks > 0, "First sample anchor was not captured");
        Assert.Equal(16000, summary.SampleRate);
        Assert.Equal(1, summary.Channels);

        worker.Dispose();
    }

    [Theory]
    [InlineData("--missing-result-block")]
    [InlineData("--unknown-result")]
    [InlineData("--long-line")]
    [InlineData("--progress-before-started")]
    [InlineData("--missing-started-field RecordingId")]
    [InlineData("--bad-frequency")]
    [InlineData("--non-positive-anchor")]
    [InlineData("--non-positive-bytes")]
    public void Start_FakeHelper_ProtocolAnomalyBeforeReady_RaisesProtocolErrorAndNoAudioReady(string helperArgs)
    {
        var outputPath = Path.Combine(_tmpDir, $"anomaly-{helperArgs.Replace(" ", "_")}.wav");
        var worker = CreateWorker(helperArgs);
        int audioReadyCount = 0;
        worker.AudioReady += () => Interlocked.Increment(ref audioReadyCount);

        worker.Start(CaptureConfigWithMic(), outputPath);
        Assert.True(SpinWait.SpinUntil(() => worker.HasExited, TimeSpan.FromSeconds(5)), "Worker did not exit");

        Assert.Equal(0, audioReadyCount);
        var summary = worker.GetTerminalSummary();
        Assert.NotNull(summary);
        Assert.True(summary.State == AudioHelperSessionState.MalformedSequence || summary.State == AudioHelperSessionState.Failed,
            $"Expected terminal failure, got {summary.State}");
        Assert.False(string.IsNullOrEmpty(summary.ErrorCode));

        worker.Dispose();
    }

    [Theory]
    [InlineData("--duplicate-started")]
    [InlineData("--duplicate-source-fail")]
    [InlineData("--malformed-progress ElapsedMs")]
    [InlineData("--progress-regress")]
    [InlineData("--duplicate-terminal")]
    [InlineData("--event-after-terminal")]
    [InlineData("--flood-events 20000")]
    public void Start_FakeHelper_ProtocolAnomalyAfterReady_RaisesProtocolError(string helperArgs)
    {
        var outputPath = Path.Combine(_tmpDir, $"anomaly-{helperArgs.Replace(" ", "_")}.wav");
        var worker = CreateWorker(helperArgs);
        int audioReadyCount = 0;
        worker.AudioReady += () => Interlocked.Increment(ref audioReadyCount);

        worker.Start(CaptureConfigWithMic(), outputPath);
        Assert.True(SpinWait.SpinUntil(() => worker.HasExited, TimeSpan.FromSeconds(5)), "Worker did not exit");

        var summary = worker.GetTerminalSummary();
        Assert.NotNull(summary);
        Assert.Equal(AudioHelperSessionState.MalformedSequence, summary.State);
        Assert.Equal("audio_helper_protocol_error", summary.ErrorCode);

        worker.Dispose();
    }

    [Fact]
    public void Start_FakeHelper_LargeEventBlock_RaisesProtocolError()
    {
        var outputPath = Path.Combine(_tmpDir, "large-block.wav");
        var worker = CreateWorker("--large-block");
        int audioReadyCount = 0;
        worker.AudioReady += () => Interlocked.Increment(ref audioReadyCount);

        worker.Start(CaptureConfigWithMic(), outputPath);
        Assert.True(SpinWait.SpinUntil(() => worker.HasExited, TimeSpan.FromSeconds(5)), "Worker did not exit");

        Assert.Equal(0, audioReadyCount);
        var summary = worker.GetTerminalSummary();
        Assert.NotNull(summary);
        Assert.Equal(AudioHelperSessionState.MalformedSequence, summary.State);
        Assert.Equal("audio_helper_protocol_error", summary.ErrorCode);

        worker.Dispose();
    }

    [Fact]
    public void Start_FakeHelper_NoTerminalEvent_ReturnsNoTerminalErrorCode()
    {
        var outputPath = Path.Combine(_tmpDir, "no-terminal.wav");
        var worker = CreateWorker("--no-terminal");

        worker.Start(CaptureConfigWithMic(), outputPath);
        Assert.True(SpinWait.SpinUntil(() => worker.IsAudioReady, TimeSpan.FromSeconds(5)), "AudioReady was not raised");

        worker.Stop();
        Assert.True(SpinWait.SpinUntil(() => worker.HasExited, TimeSpan.FromSeconds(15)), "Worker did not exit after stop");

        var summary = worker.GetTerminalSummary();
        Assert.NotNull(summary);
        Assert.Equal(AudioHelperSessionState.MalformedSequence, summary.State);
        Assert.Equal("audio_helper_no_terminal_event", summary.ErrorCode);

        worker.Dispose();
    }

    [Fact]
    public void Start_FakeHelper_OkThenNonZeroExit_ReturnsExitProtocolMismatch()
    {
        var outputPath = Path.Combine(_tmpDir, "ok-exit-7.wav");
        var worker = CreateWorker("--ok-then-exit 7");

        worker.Start(CaptureConfigWithMic(), outputPath);
        Assert.True(SpinWait.SpinUntil(() => worker.HasExited, TimeSpan.FromSeconds(5)), "Worker did not exit");

        var summary = worker.GetTerminalSummary();
        Assert.NotNull(summary);
        Assert.Equal(AudioHelperSessionState.MalformedSequence, summary.State);
        Assert.Equal("audio_helper_exit_protocol_mismatch", summary.ErrorCode);

        worker.Dispose();
    }

    [Fact]
    public void Start_FakeHelper_FailThenZeroExit_ReturnsExitProtocolMismatch()
    {
        var outputPath = Path.Combine(_tmpDir, "fail-exit-0.wav");
        var worker = CreateWorker("--fail-then-exit-0 --emit-fail audio_endpoint_not_found \"simulated failure\"");

        worker.Start(CaptureConfigWithMic(), outputPath);
        Assert.True(SpinWait.SpinUntil(() => worker.HasExited, TimeSpan.FromSeconds(5)), "Worker did not exit");

        var summary = worker.GetTerminalSummary();
        Assert.NotNull(summary);
        Assert.Equal(AudioHelperSessionState.MalformedSequence, summary.State);
        Assert.Equal("audio_helper_exit_protocol_mismatch", summary.ErrorCode);

        worker.Dispose();
    }

    [Fact]
    public void Dispose_WithoutStop_CleansUpHelperAndSignal()
    {
        var outputPath = Path.Combine(_tmpDir, "dispose.wav");
        var worker = CreateWorker("");
        var stopSignal = Path.Combine(_tmpDir, "dispose_stop.signal");
        worker.StopSignalPathOverride = stopSignal;

        worker.Start(CaptureConfigWithMic(), outputPath);
        Assert.True(SpinWait.SpinUntil(() => worker.IsAudioReady, TimeSpan.FromSeconds(5)));

        worker.Dispose();
        Assert.True(SpinWait.SpinUntil(() => worker.HasExited, TimeSpan.FromSeconds(5)), "Worker did not exit after Dispose");
        Assert.False(File.Exists(stopSignal), "Stop signal was not cleaned up");
    }

    // --- P0-1: Real WasapiAudioCaptureWorker source-kind (requested vs observed) validation ---
    // These start the production worker against the fake helper and control the
    // AudioSourceKind reported in the STARTED event via the
    // AGENT_RECORDER_FAKE_SOURCE_KIND environment variable (a test-only hook that
    // never enters the production argument set).

    [Fact]
    public void SystemLoopbackRequested_HelperReportsMicrophone_RaisesProtocolErrorAndNoAudioReady()
    {
        var summary = RunRealWorkerSourceKindScenario(
            CaptureConfigWithLoopback(),
            reportedSourceKind: "microphone",
            out int audioReadyCount);

        Assert.Equal(0, audioReadyCount);
        Assert.Equal(AudioHelperSessionState.MalformedSequence, summary.State);
        Assert.Equal("audio_helper_protocol_error", summary.ErrorCode);
        // The detailed requested-vs-observed mismatch text is carried in the
        // terminal summary's validation errors (not in the Reason field, which
        // holds the stable protocol reason "protocol_invalid_started").
        Assert.Contains(summary.ValidationErrors, v => v.Contains("AudioSourceKind mismatch"));
        Assert.Contains(summary.ValidationErrors, v => v.Contains("expected 'system-loopback', got 'microphone'"));
    }

    [Fact]
    public void MicrophoneRequested_HelperReportsSystemLoopback_RaisesProtocolErrorAndNoAudioReady()
    {
        var summary = RunRealWorkerSourceKindScenario(
            CaptureConfigWithMic(),
            reportedSourceKind: "system-loopback",
            out int audioReadyCount);

        Assert.Equal(0, audioReadyCount);
        Assert.Equal(AudioHelperSessionState.MalformedSequence, summary.State);
        Assert.Equal("audio_helper_protocol_error", summary.ErrorCode);
        Assert.Contains(summary.ValidationErrors, v => v.Contains("AudioSourceKind mismatch"));
        Assert.Contains(summary.ValidationErrors, v => v.Contains("expected 'microphone', got 'system-loopback'"));
    }

    [Fact]
    public void SystemLoopbackRequested_HelperReportsSystemLoopback_RaisesAudioReadyExactlyOnce()
    {
        var summary = RunRealWorkerSourceKindScenario(
            CaptureConfigWithLoopback(),
            reportedSourceKind: "system-loopback",
            out int audioReadyCount);

        Assert.Equal(1, audioReadyCount);
        Assert.True(summary.State == AudioHelperSessionState.Success || summary.State == AudioHelperSessionState.Stopped,
            $"Expected success/stopped terminal state, got {summary.State}. ValidationErrors: {string.Join("; ", summary.ValidationErrors)}");
        Assert.Equal("system-loopback", summary.AudioSourceKind);
    }

    [Fact]
    public void MicrophoneRequested_HelperReportsMicrophone_RaisesAudioReadyExactlyOnce()
    {
        var summary = RunRealWorkerSourceKindScenario(
            CaptureConfigWithMic(),
            reportedSourceKind: "microphone",
            out int audioReadyCount);

        Assert.Equal(1, audioReadyCount);
        Assert.True(summary.State == AudioHelperSessionState.Success || summary.State == AudioHelperSessionState.Stopped,
            $"Expected success/stopped terminal state, got {summary.State}. ValidationErrors: {string.Join("; ", summary.ValidationErrors)}");
        Assert.Equal("microphone", summary.AudioSourceKind);
    }

    [Fact]
    public void SystemLoopbackRequested_HelperOmitsSourceKind_FailsClosedAndNoAudioReady()
    {
        var summary = RunRealWorkerSourceKindScenario(
            CaptureConfigWithLoopback(),
            reportedSourceKind: null,
            out int audioReadyCount);

        Assert.Equal(0, audioReadyCount);
        Assert.Equal(AudioHelperSessionState.MalformedSequence, summary.State);
        Assert.Equal("audio_helper_protocol_error", summary.ErrorCode);
        Assert.Contains(summary.ValidationErrors, v => v.Contains("AudioSourceKind"));
    }

    [Fact]
    public void MicrophoneRequested_HelperOmitsSourceKind_AllowsLegacyReadyPath()
    {
        // Microphone is the legacy flow: a missing AudioSourceKind is tolerated
        // (unlike system-loopback, which fails closed).
        var summary = RunRealWorkerSourceKindScenario(
            CaptureConfigWithMic(),
            reportedSourceKind: null,
            out int audioReadyCount);

        Assert.Equal(1, audioReadyCount);
        Assert.True(summary.State == AudioHelperSessionState.Success || summary.State == AudioHelperSessionState.Stopped,
            $"Expected success/stopped terminal state, got {summary.State}. ValidationErrors: {string.Join("; ", summary.ValidationErrors)}");
    }

    private static AudioHelperSessionSummary RunRealWorkerSourceKindScenario(
        CaptureConfig cfg,
        string? reportedSourceKind,
        out int audioReadyCount)
    {
        var outputPath = Path.Combine(Path.GetTempPath(), $"wasapi-src-kind-{Guid.NewGuid():N}.wav");
        var original = Environment.GetEnvironmentVariable("AGENT_RECORDER_FAKE_SOURCE_KIND");
        try
        {
            Environment.SetEnvironmentVariable("AGENT_RECORDER_FAKE_SOURCE_KIND", reportedSourceKind);

            var worker = new WasapiAudioCaptureWorker
            {
                HelperExePathOverride = FakeHelperExePath(),
                SkipMicrophoneStatusMonitor = true
            };

            int ready = 0;
            worker.AudioReady += () => Interlocked.Increment(ref ready);

            worker.Start(cfg, outputPath);
            Assert.True(SpinWait.SpinUntil(() => worker.HasExited, TimeSpan.FromSeconds(10)),
                "Worker did not exit for source-kind scenario");

            audioReadyCount = Volatile.Read(ref ready);

            try { worker.Dispose(); } catch { }

            var summary = worker.GetTerminalSummary();
            Assert.NotNull(summary);
            return summary!;
        }
        finally
        {
            Environment.SetEnvironmentVariable("AGENT_RECORDER_FAKE_SOURCE_KIND", original);
            try { if (File.Exists(outputPath)) File.Delete(outputPath); } catch { }
        }
    }

    private static CaptureConfig CaptureConfigWithLoopback()
    {
        return new CaptureConfig
        {
            AudioSourceKind = AudioCaptureSourceKind.SystemLoopback,
            SystemLoopbackEndpoint = @"\\?\@device_render_{0.0.0.00000000}.{12345678-1234-1234-1234-123456789012}",
            DurationSeconds = 300
        };
    }

    private static WasapiAudioCaptureWorker CreateWorker(string helperArgs)
    {
        return new WasapiAudioCaptureWorker
        {
            HelperExePathOverride = FakeHelperExePath(),
            HelperArgumentsOverride = helperArgs,
            SkipMicrophoneStatusMonitor = true
        };
    }

    private static CaptureConfig CaptureConfigWithMic()
    {
        // Use a syntactically valid dshow alternative name so the endpoint mapping succeeds.
        return new CaptureConfig
        {
            Microphone = true,
            MicDevice = @"\\?\@device_cm_{33D9A762-90C8-11D0-BD43-00A0C911CE86}\wave_{12345678-1234-1234-1234-123456789012}",
            DurationSeconds = 300
        };
    }

    private static string FakeHelperExePath()
    {
        var candidates = new[]
        {
            Path.Combine(TestHelper.ProjectRoot, "tests", "AgentRecorder.AudioHelper.Fake", "bin", "Release", "net8.0-windows10.0.19041.0", "AgentRecorder.AudioHelper.Fake.exe"),
            Path.Combine(TestHelper.ProjectRoot, "tests", "AgentRecorder.AudioHelper.Fake", "bin", "Debug", "net8.0-windows10.0.19041.0", "AgentRecorder.AudioHelper.Fake.exe"),
        };

        foreach (var candidate in candidates)
        {
            if (File.Exists(candidate))
                return candidate;
        }

        throw new FileNotFoundException("Fake audio helper executable not found. Build tests/AgentRecorder.AudioHelper.Fake first.");
    }
}
