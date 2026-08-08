using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using AgentRecorder.Capture;
using AgentRecorder.Core;
using AgentRecorder.Windows;
using Xunit;

namespace AgentRecorder.Tests;

/// <summary>
/// Direct tests for <see cref="RecordingPreflightChecker"/> using its provider
/// seams so failures can be injected without depending on real disk space,
/// missing FFmpeg binaries, or live windows.
/// </summary>
[Collection("NonParallel-SystemQueryProviders")]
public class RecordingPreflightCheckerTests : IDisposable
{
    private readonly TempDirectory _tmp = new();
    private readonly RecordingPreflightChecker.TryGetFreeSpace _originalFreeSpace;
    private readonly RecordingPreflightChecker.TryGetEncoderPaths _originalEncoder;
    private readonly RecordingPreflightChecker.TryResolveAudioHelper _originalAudioHelperPathResolver;
    private readonly RecordingPreflightChecker.RunAudioHelperProbe _originalAudioHelperProbeRunner;
    private readonly Func<bool> _originalShouldUseWasapiBackend;
    private readonly Func<bool, bool, System.Collections.Generic.List<SystemQuery.WindowInfo>>? _originalWindowProvider;
    private readonly Func<System.Collections.Generic.List<SystemQuery.DisplayInfo>>? _originalDisplayProvider;

    public RecordingPreflightCheckerTests()
    {
        _originalFreeSpace = RecordingPreflightChecker.FreeSpaceProvider;
        _originalEncoder = RecordingPreflightChecker.EncoderProvider;
        _originalAudioHelperPathResolver = RecordingPreflightChecker.AudioHelperPathResolver;
        _originalAudioHelperProbeRunner = RecordingPreflightChecker.AudioHelperProbeRunner;
        _originalShouldUseWasapiBackend = RecordingPreflightChecker.ShouldUseWasapiBackend;
        _originalWindowProvider = GetWindowProviderField();
        _originalDisplayProvider = GetDisplayProviderField();

        // Default safe providers for most tests.
        RecordingPreflightChecker.FreeSpaceProvider = (string _, out long free) =>
        {
            free = 10L * 1024 * 1024 * 1024; // 10 GB
            return true;
        };
        RecordingPreflightChecker.EncoderProvider = (out string? f, out string? p) =>
        {
            f = Path.Combine(_tmp.Path, "ffmpeg.exe");
            p = Path.Combine(_tmp.Path, "ffprobe.exe");
            File.WriteAllText(f, "fake ffmpeg");
            File.WriteAllText(p, "fake ffprobe");
            return true;
        };

        SystemQuery.SetDisplayProvider(() => new()
        {
            new("display_1", "Display 1", true, new SystemQuery.Bounds(0, 0, 1920, 1080), 1.0)
        });
    }

    public void Dispose()
    {
        RecordingPreflightChecker.FreeSpaceProvider = _originalFreeSpace;
        RecordingPreflightChecker.EncoderProvider = _originalEncoder;
        RecordingPreflightChecker.AudioHelperPathResolver = _originalAudioHelperPathResolver;
        RecordingPreflightChecker.AudioHelperProbeRunner = _originalAudioHelperProbeRunner;
        RecordingPreflightChecker.ShouldUseWasapiBackend = _originalShouldUseWasapiBackend;
        SystemQuery.SetWindowProvider(_originalWindowProvider);
        SystemQuery.SetDisplayProvider(_originalDisplayProvider);
        _tmp.Dispose();
    }

    private static Func<bool, bool, System.Collections.Generic.List<SystemQuery.WindowInfo>>? GetWindowProviderField()
    {
        // Access the current async-scoped value via reflection to allow restoration.
        var field = typeof(SystemQuery).GetField("_windowProvider",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        var scoped = field?.GetValue(null);
        return scoped?.GetType().GetProperty("Value")?.GetValue(scoped)
            as Func<bool, bool, System.Collections.Generic.List<SystemQuery.WindowInfo>>;
    }

    private static Func<System.Collections.Generic.List<SystemQuery.DisplayInfo>>? GetDisplayProviderField()
    {
        var field = typeof(SystemQuery).GetField("_displayProvider",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        var scoped = field?.GetValue(null);
        return scoped?.GetType().GetProperty("Value")?.GetValue(scoped)
            as Func<System.Collections.Generic.List<SystemQuery.DisplayInfo>>;
    }

    private static Recording DisplayRecording(string outputPath, int? durationSeconds = null)
    {
        return new Recording
        {
            SourceType = "display",
            SourceTitle = "Display 1",
            OutputPath = outputPath,
            DurationSeconds = durationSeconds,
            Config = new AgentRecorder.Capture.CaptureConfig
            {
                SourceKind = "display",
                Bounds = (0, 0, 1920, 1080),
                OutputPath = outputPath,
                DurationSeconds = durationSeconds
            }
        };
    }

    private static Recording WindowRecording(string outputPath, nint hwnd, int? durationSeconds = null)
    {
        return new Recording
        {
            SourceType = "window",
            SourceTitle = $"Test Window (window_{hwnd.ToInt64()})",
            OutputPath = outputPath,
            DurationSeconds = durationSeconds,
            Config = new AgentRecorder.Capture.CaptureConfig
            {
                SourceKind = "window",
                Bounds = (0, 0, 1280, 720),
                OutputPath = outputPath,
                DurationSeconds = durationSeconds,
                WindowHandle = hwnd
            }
        };
    }

    private static Recording DisplayRecordingWithMicrophone(string outputPath, string? micDevice = null)
    {
        return new Recording
        {
            SourceType = "display",
            SourceTitle = "Display 1",
            OutputPath = outputPath,
            Microphone = true,
            MicrophoneDeviceId = micDevice,
            Config = new AgentRecorder.Capture.CaptureConfig
            {
                SourceKind = "display",
                Bounds = (0, 0, 1920, 1080),
                OutputPath = outputPath,
                Microphone = true,
                MicDevice = micDevice
            }
        };
    }

    [Fact]
    public void CheckBeforeConfirmation_OutputDirectoryWritable_PassesAndCleansTempFile()
    {
        var outputPath = Path.Combine(_tmp.Path, "videos", "out.mp4");
        var rec = DisplayRecording(outputPath);

        var result = RecordingPreflightChecker.CheckBeforeConfirmation(rec);

        Assert.True(result.Passed);
        var dir = Path.Combine(_tmp.Path, "videos");
        Assert.True(Directory.Exists(dir));
        Assert.DoesNotContain(Directory.EnumerateFiles(dir), f => Path.GetFileName(f).StartsWith(".agent-recorder-preflight-"));
    }

    [Fact]
    public void CheckBeforeConfirmation_InsufficientDiskSpace_Fails()
    {
        var outputPath = Path.Combine(_tmp.Path, "out.mp4");
        var rec = DisplayRecording(outputPath, durationSeconds: 60);
        RecordingPreflightChecker.FreeSpaceProvider = (string _, out long free) =>
        {
            free = 10L * 1024 * 1024; // 10 MB
            return true;
        };

        var result = RecordingPreflightChecker.CheckBeforeConfirmation(rec);

        Assert.False(result.Passed);
        Assert.Equal("INSUFFICIENT_DISK_SPACE", result.ErrorCode);
        Assert.Equal("free_disk_space_or_choose_another_directory", result.SuggestedAction);
    }

    [Fact]
    public void CheckBeforeConfirmation_EncoderUnavailable_Fails()
    {
        var outputPath = Path.Combine(_tmp.Path, "out.mp4");
        var rec = DisplayRecording(outputPath);
        RecordingPreflightChecker.EncoderProvider = (out string? f, out string? p) =>
        {
            f = null;
            p = null;
            return false;
        };

        var result = RecordingPreflightChecker.CheckBeforeConfirmation(rec);

        Assert.False(result.Passed);
        Assert.Equal("ENCODER_UNAVAILABLE", result.ErrorCode);
    }

    [Fact]
    public void CheckBeforeStart_WindowDisappeared_FailsSourceNotFound()
    {
        var outputPath = Path.Combine(_tmp.Path, "out.mp4");
        var rec = WindowRecording(outputPath, new nint(12345));
        SystemQuery.SetWindowProvider((_, _) => new());

        var result = RecordingPreflightChecker.CheckBeforeStart(rec);

        Assert.False(result.Passed);
        Assert.Equal("SOURCE_NOT_FOUND", result.ErrorCode);
        Assert.Equal("choose_source_again", result.SuggestedAction);
    }

    [Fact]
    public void CheckBeforeStart_WindowMinimized_FailsSourceUnavailable()
    {
        var outputPath = Path.Combine(_tmp.Path, "out.mp4");
        var hwnd = new nint(12345);
        var rec = WindowRecording(outputPath, hwnd);
        SystemQuery.SetWindowProvider((_, _) => new()
        {
            new SystemQuery.WindowInfo(
                $"window_{hwnd.ToInt64()}",
                "Notepad",
                "notepad.exe",
                42,
                false,
                true,
                new SystemQuery.Bounds(0, 0, 1280, 720))
        });

        var result = RecordingPreflightChecker.CheckBeforeStart(rec);

        Assert.False(result.Passed);
        Assert.Equal("SOURCE_UNAVAILABLE", result.ErrorCode);
        Assert.Equal("restore_or_move_window_then_retry", result.SuggestedAction);
    }

    [Fact]
    public void CheckBeforeStart_WindowAvailable_Passes()
    {
        var outputPath = Path.Combine(_tmp.Path, "out.mp4");
        var hwnd = new nint(12345);
        var rec = WindowRecording(outputPath, hwnd);
        SystemQuery.SetWindowProvider((_, _) => new()
        {
            new SystemQuery.WindowInfo(
                $"window_{hwnd.ToInt64()}",
                "Notepad",
                "notepad.exe",
                42,
                false,
                false,
                new SystemQuery.Bounds(0, 0, 1280, 720))
        });

        var result = RecordingPreflightChecker.CheckBeforeStart(rec);

        Assert.True(result.Passed);
    }

    [Fact]
    public void CheckBeforeStart_WindowTooSmall_FailsSourceUnavailable()
    {
        var outputPath = Path.Combine(_tmp.Path, "out.mp4");
        var hwnd = new nint(12345);
        var rec = WindowRecording(outputPath, hwnd);
        SystemQuery.SetWindowProvider((_, _) => new()
        {
            new SystemQuery.WindowInfo(
                $"window_{hwnd.ToInt64()}",
                "Tiny",
                "tiny.exe",
                42,
                false,
                false,
                new SystemQuery.Bounds(0, 0, 10, 10))
        });

        var result = RecordingPreflightChecker.CheckBeforeStart(rec);

        Assert.False(result.Passed);
        Assert.Equal("SOURCE_UNAVAILABLE", result.ErrorCode);
    }

    [Fact]
    public void CheckBeforeConfirmation_MicrophoneDisabled_DoesNotProbeAudioHelper()
    {
        var outputPath = Path.Combine(_tmp.Path, "out.mp4");
        var rec = DisplayRecording(outputPath);
        RecordingPreflightChecker.ShouldUseWasapiBackend = () => true;
        bool probed = false;
        RecordingPreflightChecker.AudioHelperPathResolver = () => "should-not-be-called";
        RecordingPreflightChecker.AudioHelperProbeRunner = (_, _) =>
        {
            probed = true;
            return new AudioHelperProbeResult { Success = true, ProtocolVersion = "audio-helper-v1", TimestampFrequency = Stopwatch.Frequency };
        };

        var result = RecordingPreflightChecker.CheckBeforeConfirmation(rec);

        Assert.True(result.Passed);
        Assert.False(probed);
    }

    [Fact]
    public void CheckBeforeConfirmation_ExplicitDshow_DoesNotProbeAudioHelper()
    {
        var outputPath = Path.Combine(_tmp.Path, "out.mp4");
        var rec = DisplayRecordingWithMicrophone(outputPath);
        RecordingPreflightChecker.ShouldUseWasapiBackend = () => false;
        bool probed = false;
        RecordingPreflightChecker.AudioHelperPathResolver = () => "should-not-be-called";
        RecordingPreflightChecker.AudioHelperProbeRunner = (_, _) =>
        {
            probed = true;
            return new AudioHelperProbeResult { Success = true, ProtocolVersion = "audio-helper-v1", TimestampFrequency = Stopwatch.Frequency };
        };

        var result = RecordingPreflightChecker.CheckBeforeConfirmation(rec);

        Assert.True(result.Passed);
        Assert.False(probed);
    }

    [Fact]
    public void CheckBeforeConfirmation_WasapiHelper_ProbesBeforeConfirmation()
    {
        var outputPath = Path.Combine(_tmp.Path, "out.mp4");
        var rec = DisplayRecordingWithMicrophone(outputPath, micDevice: "fake-mic");
        RecordingPreflightChecker.ShouldUseWasapiBackend = () => true;
        bool probed = false;
        RecordingPreflightChecker.AudioHelperPathResolver = () => Path.Combine(_tmp.Path, "helper.exe");
        File.WriteAllText(Path.Combine(_tmp.Path, "helper.exe"), "fake");
        RecordingPreflightChecker.AudioHelperProbeRunner = (_, _) =>
        {
            probed = true;
            return new AudioHelperProbeResult { Success = true, ProtocolVersion = "audio-helper-v1", TimestampFrequency = Stopwatch.Frequency };
        };

        var result = RecordingPreflightChecker.CheckBeforeConfirmation(rec);

        Assert.True(result.Passed);
        Assert.True(probed);
    }

    [Fact]
    public void CheckBeforeConfirmation_HelperMissing_FailsAudioHelperUnavailable()
    {
        var outputPath = Path.Combine(_tmp.Path, "out.mp4");
        var rec = DisplayRecordingWithMicrophone(outputPath);
        RecordingPreflightChecker.ShouldUseWasapiBackend = () => true;
        RecordingPreflightChecker.AudioHelperPathResolver = () => null;

        var result = RecordingPreflightChecker.CheckBeforeConfirmation(rec);

        Assert.False(result.Passed);
        Assert.Equal("audio_helper_unavailable", result.ErrorCode);
    }

    [Fact]
    public void CheckBeforeConfirmation_HelperProbeTimeout_FailsAudioHelperProbeTimeout()
    {
        var outputPath = Path.Combine(_tmp.Path, "out.mp4");
        var rec = DisplayRecordingWithMicrophone(outputPath);
        RecordingPreflightChecker.ShouldUseWasapiBackend = () => true;
        RecordingPreflightChecker.AudioHelperPathResolver = () => Path.Combine(_tmp.Path, "helper.exe");
        RecordingPreflightChecker.AudioHelperProbeRunner = (_, token) =>
        {
            token.WaitHandle.WaitOne(TimeSpan.FromSeconds(30));
            token.ThrowIfCancellationRequested();
            return new AudioHelperProbeResult { Success = true };
        };

        var result = RecordingPreflightChecker.CheckBeforeConfirmation(rec);

        Assert.False(result.Passed);
        Assert.Equal("audio_helper_probe_timeout", result.ErrorCode);
    }

    [Fact]
    public void CheckBeforeConfirmation_HelperProbeBadVersion_FailsAudioHelperProtocolError()
    {
        var outputPath = Path.Combine(_tmp.Path, "out.mp4");
        var rec = DisplayRecordingWithMicrophone(outputPath);
        RecordingPreflightChecker.ShouldUseWasapiBackend = () => true;
        RecordingPreflightChecker.AudioHelperPathResolver = () => Path.Combine(_tmp.Path, "helper.exe");
        RecordingPreflightChecker.AudioHelperProbeRunner = (_, _) => new AudioHelperProbeResult
        {
            Success = true,
            ProtocolVersion = "audio-helper-v2",
            TimestampFrequency = Stopwatch.Frequency
        };

        var result = RecordingPreflightChecker.CheckBeforeConfirmation(rec);

        Assert.False(result.Passed);
        Assert.Equal("audio_helper_protocol_error", result.ErrorCode);
    }

    [Fact]
    public void CheckBeforeConfirmation_HelperProbeBadFrequency_FailsAudioHelperProtocolError()
    {
        var outputPath = Path.Combine(_tmp.Path, "out.mp4");
        var rec = DisplayRecordingWithMicrophone(outputPath);
        RecordingPreflightChecker.ShouldUseWasapiBackend = () => true;
        RecordingPreflightChecker.AudioHelperPathResolver = () => Path.Combine(_tmp.Path, "helper.exe");
        RecordingPreflightChecker.AudioHelperProbeRunner = (_, _) => new AudioHelperProbeResult
        {
            Success = true,
            ProtocolVersion = "audio-helper-v1",
            TimestampFrequency = Stopwatch.Frequency + 1
        };

        var result = RecordingPreflightChecker.CheckBeforeConfirmation(rec);

        Assert.False(result.Passed);
        Assert.Equal("audio_helper_protocol_error", result.ErrorCode);
    }

    [Theory]
    [InlineData("audio-helper-v10")]
    [InlineData("audio-helper-v1-malformed")]
    [InlineData("audio-helper-v1evil")]
    [InlineData("audio-helper-v2")]
    [InlineData("v1")]
    public void CheckBeforeConfirmation_HelperProbeProtocolPrefixSpoof_FailsAudioHelperProtocolError(string protocolVersion)
    {
        var outputPath = Path.Combine(_tmp.Path, "out.mp4");
        var rec = DisplayRecordingWithMicrophone(outputPath);
        RecordingPreflightChecker.ShouldUseWasapiBackend = () => true;
        RecordingPreflightChecker.AudioHelperPathResolver = () => Path.Combine(_tmp.Path, "helper.exe");
        RecordingPreflightChecker.AudioHelperProbeRunner = (_, _) => new AudioHelperProbeResult
        {
            Success = true,
            ProtocolVersion = protocolVersion,
            TimestampFrequency = Stopwatch.Frequency
        };

        var result = RecordingPreflightChecker.CheckBeforeConfirmation(rec);

        Assert.False(result.Passed);
        Assert.Equal("audio_helper_protocol_error", result.ErrorCode);
    }

    [Fact]
    public void CheckBeforeConfirmation_UnmappableEndpoint_FailsAudioEndpointIdUnmappable()
    {
        var outputPath = Path.Combine(_tmp.Path, "out.mp4");
        // Contains the dshow \wave_ marker but no valid GUID, forcing the
        // CoreAudio endpoint mapping to return empty and fail closed.
        var rec = DisplayRecordingWithMicrophone(outputPath, micDevice: @"\\?\@device_cm_{33D9A762-90C8-11D0-BD43-00A0C911CE86}\wave_{not-a-valid-guid}");
        RecordingPreflightChecker.ShouldUseWasapiBackend = () => true;
        RecordingPreflightChecker.AudioHelperPathResolver = () => Path.Combine(_tmp.Path, "helper.exe");
        RecordingPreflightChecker.AudioHelperProbeRunner = (_, _) => new AudioHelperProbeResult
        {
            Success = true,
            ProtocolVersion = "audio-helper-v1",
            TimestampFrequency = Stopwatch.Frequency
        };

        var result = RecordingPreflightChecker.CheckBeforeConfirmation(rec);

        Assert.False(result.Passed);
        Assert.Equal("audio_endpoint_id_unmappable", result.ErrorCode);
    }

    [Fact]
    public void CheckBeforeStart_WasapiHelper_RunsProbeAndSourceChecks()
    {
        var outputPath = Path.Combine(_tmp.Path, "out.mp4");
        var rec = DisplayRecordingWithMicrophone(outputPath, micDevice: "fake-mic");
        RecordingPreflightChecker.ShouldUseWasapiBackend = () => true;
        bool probed = false;
        RecordingPreflightChecker.AudioHelperPathResolver = () => Path.Combine(_tmp.Path, "helper.exe");
        RecordingPreflightChecker.AudioHelperProbeRunner = (_, _) =>
        {
            probed = true;
            return new AudioHelperProbeResult { Success = true, ProtocolVersion = "audio-helper-v1", TimestampFrequency = Stopwatch.Frequency };
        };

        var result = RecordingPreflightChecker.CheckBeforeStart(rec);

        Assert.True(result.Passed);
        Assert.True(probed);
    }

    [Fact]
    public void AudioHelperProbeLauncher_HangingFakeHelper_ReturnsTimeoutAndKillsProcess()
    {
        var baseDir = Path.Combine(
            AppContext.BaseDirectory,
            "..", "..", "..", "..", "..",
            "tests", "AgentRecorder.AudioHelper.Fake", "bin");
        string? fakeHelperPath = null;
        foreach (var config in new[] { "Release", "Debug" })
        {
            var candidate = Path.GetFullPath(Path.Combine(baseDir, config, "net8.0-windows10.0.19041.0", "AgentRecorder.AudioHelper.Fake.exe"));
            if (File.Exists(candidate))
            {
                fakeHelperPath = candidate;
                break;
            }
        }
        Assert.NotNull(fakeHelperPath);
        Assert.True(File.Exists(fakeHelperPath), $"Fake helper not found under {baseDir}");

        var previous = Environment.GetEnvironmentVariable("AGENT_RECORDER_FAKE_HANG");
        try
        {
            Environment.SetEnvironmentVariable("AGENT_RECORDER_FAKE_HANG", "1");

            var sw = Stopwatch.StartNew();
            var result = AudioHelperProbeLauncher.Run(fakeHelperPath, TimeSpan.FromMilliseconds(500));
            sw.Stop();

            Assert.False(result.Success);
            Assert.Equal("audio_helper_probe_timeout", result.ErrorCode);
            Assert.True(sw.Elapsed >= TimeSpan.FromMilliseconds(400), $"Timeout returned too early: {sw.Elapsed}");
            Assert.True(sw.Elapsed < TimeSpan.FromSeconds(3), $"Timeout took too long: {sw.Elapsed}");

            int pid = result.ProcessId;
            Assert.NotEqual(0, pid);

            // Give the OS a moment to reap the process, then assert it is gone.
            for (int i = 0; i < 50; i++)
            {
                try
                {
                    if (Process.GetProcessById(pid).HasExited)
                        break;
                }
                catch (ArgumentException)
                {
                    break;
                }
                Thread.Sleep(100);
            }

            try
            {
                Assert.True(Process.GetProcessById(pid).HasExited, "Hanging fake helper process was not terminated");
            }
            catch (ArgumentException)
            {
                // Process no longer exists: this is the expected outcome.
            }
        }
        finally
        {
            Environment.SetEnvironmentVariable("AGENT_RECORDER_FAKE_HANG", previous);
        }
    }
}
