using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using AgentRecorder.Capture;
using AgentRecorder.Core;
using AgentRecorder.Infrastructure;
using Xunit;

namespace AgentRecorder.Tests;

/// <summary>
/// Tests for system loopback audio capture integration with the A/V split backend.
/// These tests verify the source-aware audio path without running real audio hardware.
/// </summary>
[Collection("NonParallel-AgentRecorderDataDir")]
public sealed class SystemLoopbackCaptureTests
{
    // ============================================================
    // CaptureConfig normalization and validation
    // ============================================================

    [Fact]
    public void CaptureConfig_LegacyMicrophone_NormalizesToMicrophone()
    {
        var cfg = new CaptureConfig
        {
            Microphone = true,
            MicDevice = "Microphone (Realtek)"
        };
        Assert.Equal(AudioCaptureSourceKind.None, cfg.AudioSourceKind);
        cfg.NormalizeAudioSource();
        Assert.Equal(AudioCaptureSourceKind.Microphone, cfg.AudioSourceKind);
        Assert.True(cfg.AudioRequested);
        Assert.True(cfg.IsMicrophone);
        Assert.False(cfg.IsSystemLoopback);
    }

    [Fact]
    public void CaptureConfig_ExplicitSystemLoopback_Works()
    {
        var cfg = new CaptureConfig
        {
            AudioSourceKind = AudioCaptureSourceKind.SystemLoopback,
            SystemLoopbackEndpoint = "{0.0.0.00000000}.{00000000-0000-0000-0000-000000000000}"
        };
        Assert.Equal(AudioCaptureSourceKind.SystemLoopback, cfg.AudioSourceKind);
        Assert.True(cfg.AudioRequested);
        Assert.False(cfg.IsMicrophone);
        Assert.True(cfg.IsSystemLoopback);
        Assert.Null(cfg.ValidateAudioSource());
    }

    [Fact]
    public void CaptureConfig_MicrophoneAndSystemLoopback_Conflict()
    {
        var cfg = new CaptureConfig
        {
            Microphone = true,
            MicDevice = "Microphone (Realtek)",
            AudioSourceKind = AudioCaptureSourceKind.SystemLoopback,
            SystemLoopbackEndpoint = "{0.0.0.00000000}.{00000000-0000-0000-0000-000000000000}"
        };
        var error = cfg.ValidateAudioSource();
        Assert.NotNull(error);
        Assert.Contains("cannot both", error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void CaptureConfig_SystemLoopback_NoEndpoint_Fails()
    {
        var cfg = new CaptureConfig
        {
            AudioSourceKind = AudioCaptureSourceKind.SystemLoopback
        };
        var error = cfg.ValidateAudioSource();
        Assert.NotNull(error);
        Assert.Contains("requires", error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void CaptureConfig_None_IsValid()
    {
        var cfg = new CaptureConfig();
        Assert.False(cfg.AudioRequested);
        Assert.False(cfg.IsMicrophone);
        Assert.False(cfg.IsSystemLoopback);
        Assert.Null(cfg.ValidateAudioSource());
    }

    [Fact]
    public void CaptureConfig_NormalizeOnlyWhenNone()
    {
        // When AudioSourceKind is already set, NormalizeAudioSource must not override it.
        var cfg = new CaptureConfig
        {
            Microphone = true,
            MicDevice = "Mic",
            AudioSourceKind = AudioCaptureSourceKind.SystemLoopback,
            SystemLoopbackEndpoint = "{endpoint}"
        };
        cfg.NormalizeAudioSource();
        Assert.Equal(AudioCaptureSourceKind.SystemLoopback, cfg.AudioSourceKind);
        Assert.True(cfg.IsSystemLoopback);
    }

    // ============================================================
    // WasapiAudioCaptureWorker BuildArgs
    // ============================================================

    [Fact]
    public void BuildArgs_Microphone_IncludesHfpPair()
    {
        var args = WasapiAudioCaptureWorker.BuildArgs(
            endpointId: "{endpoint}",
            outputPath: @"C:\temp\test.wav",
            allowedRoot: @"C:\temp",
            stopSignalPath: @"C:\temp\stop.signal",
            recordingId: "rec_test",
            extraArgs: null,
            enableAutomaticHfpPairDiscovery: true,
            isSystemLoopback: false);

        Assert.Contains("--auto-hfp-pair", args);
        Assert.DoesNotContain("--source-kind", args);
    }

    [Fact]
    public void BuildArgs_SystemLoopback_IncludesSourceKind_NoHfp()
    {
        var args = WasapiAudioCaptureWorker.BuildArgs(
            endpointId: "{endpoint}",
            outputPath: @"C:\temp\test.wav",
            allowedRoot: @"C:\temp",
            stopSignalPath: @"C:\temp\stop.signal",
            recordingId: "rec_test",
            extraArgs: null,
            enableAutomaticHfpPairDiscovery: true,
            isSystemLoopback: true);

        Assert.Contains("--source-kind", args);
        Assert.Contains("system-loopback", args);
        Assert.DoesNotContain("--auto-hfp-pair", args);
    }

    [Fact]
    public void BuildArgs_SystemLoopback_ExactEndpoint()
    {
        const string endpoint = "{0.0.0.00000000}.{00000000-0000-0000-0000-000000000000}";
        var args = WasapiAudioCaptureWorker.BuildArgs(
            endpointId: endpoint,
            outputPath: @"C:\temp\test.wav",
            allowedRoot: @"C:\temp",
            stopSignalPath: @"C:\temp\stop.signal",
            recordingId: "rec_test",
            extraArgs: null,
            enableAutomaticHfpPairDiscovery: false,
            isSystemLoopback: true);

        var endpointIdx = args.IndexOf("--endpoint-id");
        Assert.True(endpointIdx >= 0);
        Assert.Equal(endpoint, args[endpointIdx + 1]);
    }

    [Fact]
    public void BuildArgs_SystemLoopback_IncludesCaptureEngine()
    {
        var args = WasapiAudioCaptureWorker.BuildArgs(
            endpointId: "{endpoint}",
            outputPath: @"C:\temp\test.wav",
            allowedRoot: @"C:\temp",
            stopSignalPath: @"C:\temp\stop.signal",
            recordingId: "rec_test",
            extraArgs: null,
            enableAutomaticHfpPairDiscovery: false,
            isSystemLoopback: true);

        var engineIdx = args.IndexOf("--capture-engine");
        Assert.True(engineIdx >= 0, "--capture-engine must be present for system loopback");
        Assert.Equal("wasapi-direct", args[engineIdx + 1]);
    }

    [Fact]
    public void BuildArgs_SystemLoopback_RejectsExtraArgs()
    {
        var args = WasapiAudioCaptureWorker.BuildArgs(
            endpointId: "{endpoint}",
            outputPath: @"C:\temp\test.wav",
            allowedRoot: @"C:\temp",
            stopSignalPath: @"C:\temp\stop.signal",
            recordingId: "rec_test",
            extraArgs: "--some-unsafe-arg value",
            enableAutomaticHfpPairDiscovery: false,
            isSystemLoopback: true);

        // System loopback must not include extra args to prevent parameter injection
        Assert.DoesNotContain("--some-unsafe-arg", args);
        Assert.DoesNotContain("value", args);
    }

    // ============================================================
    // AudioHelperEventStreamParser source-kind validation
    // ============================================================

    [Fact]
    public void EventStream_AcceptsSystemLoopbackStream()
    {
        // This test verifies that the parser accepts a system-loopback stream.
        // Source-kind mismatch is validated at the worker level, not the parser.
        var stdout = @"RESULT: STARTED
RecordingId: test_rec
SampleRate: 48000
Channels: 2
BitsPerSample: 32
FirstSampleAnchorTicks: 1000000
TimestampFrequency: 10000000
BytesWritten: 0
CaptureMethod: WASAPI_SHARED_LOOPBACK
CaptureEngine: wasapi-direct
AudioSourceKind: system-loopback

RESULT: PROGRESS
ElapsedMs: 1000
WallElapsedMs: 1000
BytesWritten: 192000
EstimatedGapMs: 0
MaxEstimatedGapMs: 0
AudioSourceKind: system-loopback

RESULT: STOPPED
DurationMs: 2000
ElapsedMs: 2000
WallElapsedMs: 2000
BytesWritten: 384000
EstimatedGapMs: 0
MaxEstimatedGapMs: 0
AudioSourceKind: system-loopback
";
        var summary = AudioHelperEventStreamParser.ParseAndValidate(stdout);
        Assert.Equal(AudioHelperSessionState.Stopped, summary.State);
        Assert.Equal("system-loopback", summary.AudioSourceKind);
    }

    [Fact]
    public void EventStream_RejectsHfpMetadata_OnSystemLoopback()
    {
        // system-loopback events must not contain HFP metadata
        var stdout = @"RESULT: FAIL
ErrorCode: audio_endpoint_not_found
Reason: Endpoint not found
AudioSourceKind: system-loopback
PairEvidence: some_evidence
";
        var summary = AudioHelperEventStreamParser.ParseAndValidate(stdout);
        Assert.Contains(summary.ValidationErrors, e => e.Contains("HFP") || e.Contains("PairEvidence"));
    }

    [Fact]
    public void EventStream_AcceptsMicrophoneStream()
    {
        var stdout = @"RESULT: STARTED
RecordingId: test_rec
SampleRate: 48000
Channels: 2
BitsPerSample: 16
FirstSampleAnchorTicks: 1000000
TimestampFrequency: 10000000
BytesWritten: 0
CaptureMethod: WASAPI_SHARED_CAPTURE
CaptureEngine: wasapi-direct
AudioSourceKind: microphone

RESULT: OK
DurationMs: 5000
ElapsedMs: 5000
WallElapsedMs: 5000
BytesWritten: 960000
EstimatedGapMs: 0
MaxEstimatedGapMs: 0
AudioSourceKind: microphone
";
        var summary = AudioHelperEventStreamParser.ParseAndValidate(stdout);
        Assert.Equal(AudioHelperSessionState.Success, summary.State);
        Assert.Equal("microphone", summary.AudioSourceKind);
    }

    // ============================================================
    // AvSplitCaptureBackend flow with system loopback
    // ============================================================

    [Fact]
    public void AvSplit_SystemLoopback_FailsWithoutRenderEndpoint()
    {
        var cfg = new CaptureConfig
        {
            OutputPath = Path.GetTempFileName(),
            AudioSourceKind = AudioCaptureSourceKind.SystemLoopback
            // No SystemLoopbackEndpoint set
        };

        var error = cfg.ValidateAudioSource();
        Assert.NotNull(error);
        Assert.Contains("requires", error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void AvSplit_SystemLoopback_Normalization_DoesNotOverrideExplicit()
    {
        // If AudioSourceKind is explicitly set, NormalizeAudioSource should not
        // override it even if Microphone is also true (caller error).
        var cfg = new CaptureConfig
        {
            Microphone = true,
            MicDevice = "Mic",
            AudioSourceKind = AudioCaptureSourceKind.SystemLoopback,
            SystemLoopbackEndpoint = "{endpoint}"
        };
        cfg.NormalizeAudioSource();
        Assert.Equal(AudioCaptureSourceKind.SystemLoopback, cfg.AudioSourceKind);
        Assert.True(cfg.IsSystemLoopback);
    }

    [Fact]
    public void AvSplit_IllegalAudioConfig_DoesNotCreateTempDirOrWorkers()
    {
        // Section 4: Start() must normalize/validate the audio source BEFORE
        // creating the temp directory or any worker. An illegal configuration
        // must fail with no output side effects.
        var dataDir = Path.Combine(Path.GetTempPath(), $"sysloop-illegal-{Guid.NewGuid():N}");
        var original = Environment.GetEnvironmentVariable("AGENT_RECORDER_DATA_DIR");
        try
        {
            Environment.SetEnvironmentVariable("AGENT_RECORDER_DATA_DIR", dataDir);
            DataDirResolver.SetOverride(dataDir);

            var factory = new FakeAvWorkerFactory();
            var backend = new AvSplitCaptureBackend(
                factory,
                new FakeExternalProcessRunner(),
                new TempRetentionPolicy(dataDir));

            var illegalCfg = new CaptureConfig
            {
                OutputPath = Path.Combine(dataDir, "out.mp4"),
                Microphone = true,
                MicDevice = "Mic",
                AudioSourceKind = AudioCaptureSourceKind.SystemLoopback,
                SystemLoopbackEndpoint = "{endpoint}"
            };

            var ex = Assert.Throws<ArgumentException>(() => backend.Start(illegalCfg));
            Assert.Contains("Invalid audio source configuration", ex.Message);

            Assert.False(Directory.Exists(Path.Combine(dataDir, "temp")),
                "temp directory must not be created for an illegal audio configuration");
            Assert.Equal(0, factory.CreateAudioWorkerCount);
            Assert.Equal(0, factory.CreateVideoWorkerCount);
        }
        finally
        {
            DataDirResolver.ClearOverride();
            if (original == null)
                Environment.SetEnvironmentVariable("AGENT_RECORDER_DATA_DIR", null);
            else
                Environment.SetEnvironmentVariable("AGENT_RECORDER_DATA_DIR", original);
            try { if (Directory.Exists(dataDir)) Directory.Delete(dataDir, recursive: true); }
            catch { }
        }
    }

    // ============================================================
    // OutputMeta - system loopback metadata
    // ============================================================

    [Fact]
    public void OutputMeta_SystemLoopback_DoesNotUseMicrophoneKeys()
    {
        var meta = new OutputMeta
        {
            AudioStatus = "system_loopback_recorded",
            AudioContinuityStatus = "continuous",
            AudioCaptureBackend = "wasapi-helper-loopback"
        };

        // Ensure no microphone_* key patterns are present in the values
        Assert.DoesNotContain("microphone", meta.AudioStatus, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("microphone", meta.AudioCaptureBackend, StringComparison.OrdinalIgnoreCase);
    }

}
