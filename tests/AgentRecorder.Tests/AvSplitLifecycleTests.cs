using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using AgentRecorder.Capture;
using AgentRecorder.Infrastructure;
using Xunit;

namespace AgentRecorder.Tests;

/// <summary>
/// Lightweight fake audio worker for split A/V lifecycle tests.
/// </summary>
internal sealed class FakeAudioCaptureWorker : IAudioCaptureWorker, IAudioHelperSummaryProvider
{
    private readonly bool _raiseAudioReadyOnStart;
    private readonly int _audioReadyDelayMs;
    private readonly bool _holdFileOpen;
    private readonly string? _holdFileOpenCopyFrom;
    private readonly int _naturalExitDelayMs;
    private string _stderrLog;
    private readonly long _runtimeAudioLostAtMs;
    private readonly string? _reportedSourceKind;
    private FileStream? _fileStream;
    private int _audioReadyRaised;
    private AudioHelperSessionSummary? _terminalSummary;
    private bool _protocolErrorRaised;

    public event Action? AudioReady;
    public event Action<int, string>? NaturalExit;

    public DateTime? ReadyAtUtc { get; private set; }
    public long MediaStartAnchorTicks { get; private set; }
    public string? OutputPath { get; private set; }
    public int ExitCode { get; private set; }
    public bool HasExited { get; private set; }
    public bool IsAudioReady { get; private set; }
    public bool StopCalled { get; private set; }
    public bool WaitForExitCalled { get; private set; }
    public long RuntimeAudioLostAtMs => _runtimeAudioLostAtMs;
    public bool IsFileHandleReleased => _fileStream == null;
    public bool SuppressExit { get; set; }
    public bool SuppressHandleRelease { get; set; }
    public bool ProtocolErrorRaised => _protocolErrorRaised;

    public FakeAudioCaptureWorker(
        bool raiseAudioReadyOnStart = false,
        int audioReadyDelayMs = 0,
        bool holdFileOpen = false,
        string? holdFileOpenCopyFrom = null,
        int naturalExitDelayMs = -1,
        string stderrLog = "",
        long runtimeAudioLostAtMs = 0,
        string? reportedSourceKind = null)
    {
        _raiseAudioReadyOnStart = raiseAudioReadyOnStart;
        _audioReadyDelayMs = audioReadyDelayMs;
        _holdFileOpen = holdFileOpen;
        _holdFileOpenCopyFrom = holdFileOpenCopyFrom;
        _naturalExitDelayMs = naturalExitDelayMs;
        _stderrLog = stderrLog;
        _runtimeAudioLostAtMs = runtimeAudioLostAtMs;
        _reportedSourceKind = reportedSourceKind;
    }

    public void Start(CaptureConfig cfg, string outputPath)
    {
        OutputPath = outputPath;

        // Simulate requested-vs-observed source kind mismatch.
        // When the fake helper reports a different source kind than the config
        // requested, set a protocol error and do not raise AudioReady.
        if (_reportedSourceKind != null)
        {
            var expectedKind = cfg.IsSystemLoopback ? "system-loopback" : "microphone";
            if (!string.Equals(_reportedSourceKind, expectedKind, StringComparison.Ordinal))
            {
                _protocolErrorRaised = true;
                _stderrLog = $"protocol_invalid_started: AudioSourceKind mismatch: expected '{expectedKind}', got '{_reportedSourceKind}'";
                HasExited = true;
                ExitCode = 1;
                try { NaturalExit?.Invoke(1, _stderrLog); }
                catch { }
                return;
            }
        }

        if (_holdFileOpen)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
            if (!string.IsNullOrEmpty(_holdFileOpenCopyFrom) && File.Exists(_holdFileOpenCopyFrom))
            {
                File.Copy(_holdFileOpenCopyFrom, outputPath, overwrite: true);
            }
            _fileStream = new FileStream(outputPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        }

        if (_raiseAudioReadyOnStart)
        {
            RaiseAudioReady();
        }
        else if (_audioReadyDelayMs > 0)
        {
            Task.Run(async () =>
            {
                await Task.Delay(_audioReadyDelayMs);
                RaiseAudioReady();
            });
        }

        if (_naturalExitDelayMs >= 0)
        {
            Task.Run(async () =>
            {
                await Task.Delay(_naturalExitDelayMs);
                EmitNaturalExit(0, _stderrLog);
            });
        }
    }

    private void RaiseAudioReady()
    {
        if (Interlocked.Exchange(ref _audioReadyRaised, 1) != 0)
            return;
        IsAudioReady = true;
        ReadyAtUtc = DateTime.UtcNow;
        // Simulate a media start anchor slightly before the ready notification,
        // as the real worker estimates WAV time zero from the first progress.
        MediaStartAnchorTicks = Stopwatch.GetTimestamp() - Stopwatch.Frequency / 10;
        try { AudioReady?.Invoke(); }
        catch { }
    }

    public void EmitNaturalExit(int exitCode, string stderr)
    {
        if (HasExited || SuppressExit) return;
        ExitCode = exitCode;
        HasExited = true;
        try { NaturalExit?.Invoke(exitCode, stderr); }
        catch { }
    }

    public void Stop()
    {
        StopCalled = true;
        if (!SuppressHandleRelease)
        {
            _fileStream?.Dispose();
            _fileStream = null;
        }
        if (!HasExited)
            EmitNaturalExit(0, _stderrLog);
    }

    public string GetStderrLog() => _stderrLog;

    public bool WaitForExit(TimeSpan timeout)
    {
        WaitForExitCalled = true;
        var deadline = DateTime.UtcNow + timeout;
        while (!HasExited && DateTime.UtcNow < deadline)
            Thread.Sleep(10);
        return HasExited;
    }

    public void SetMicrophoneStatusProvider(IMicrophoneStatusProvider? provider) { }

    public void SetTerminalSummary(AudioHelperSessionSummary? summary)
    {
        _terminalSummary = summary;
    }

    public AudioHelperSessionSummary? GetTerminalSummary()
    {
        return _terminalSummary;
    }

    public void Dispose()
    {
        _fileStream?.Dispose();
    }
}

/// <summary>
/// Lightweight fake video worker for split A/V lifecycle tests.
/// </summary>
internal sealed class FakeVideoCaptureWorker : IVideoCaptureWorker
{
    private readonly int _firstFrameDelayMs;
    private readonly int _naturalExitDelayMs;
    private readonly string _stderrLog;

    public event Action<FirstFrameObservation>? FirstFrameObserved;
    public event Action<int, string>? NaturalExit;

    public string? OutputPath { get; private set; }
    public int ExitCode { get; private set; }
    public bool HasExited { get; private set; }
    public bool StopCalled { get; private set; }
    public bool WaitForExitCalled { get; private set; }
    public long LaunchAnchorTicks { get; private set; }
    public long FirstFrameAnchorTicks { get; private set; }
    public long? FirstProgressFrame { get; private set; }
    public long? FirstProgressOutTimeUs { get; private set; }
    public double? ProgressAnchorDeltaMs { get; private set; }
    public int FirstFrameCount { get; private set; }

    /// <summary>
    /// When true, the worker never reports that it has exited. Used to test
    /// backend behavior when video.WaitForExit times out.
    /// </summary>
    public bool SuppressExit { get; set; }

    public FakeVideoCaptureWorker(
        int firstFrameDelayMs = 0,
        int naturalExitDelayMs = -1,
        string stderrLog = "")
    {
        _firstFrameDelayMs = firstFrameDelayMs;
        _naturalExitDelayMs = naturalExitDelayMs;
        _stderrLog = stderrLog;
    }

    public void Start(CaptureConfig cfg, string outputPath)
    {
        OutputPath = outputPath;
        LaunchAnchorTicks = Stopwatch.GetTimestamp();
        if (_firstFrameDelayMs == 0)
        {
            EmitFirstFrame();
        }
        else if (_firstFrameDelayMs > 0)
        {
            Task.Run(async () =>
            {
                await Task.Delay(_firstFrameDelayMs);
                EmitFirstFrame();
            });
        }

        if (_naturalExitDelayMs == 0)
        {
            EmitNaturalExit(0, _stderrLog);
        }
        else if (_naturalExitDelayMs > 0)
        {
            Task.Run(async () =>
            {
                await Task.Delay(_naturalExitDelayMs);
                EmitNaturalExit(0, _stderrLog);
            });
        }
    }

    public void EmitFirstFrame()
    {
        FirstFrameCount++;
        FirstFrameAnchorTicks = Stopwatch.GetTimestamp();
        try
        {
            FirstFrameObserved?.Invoke(new FirstFrameObservation
            {
                EvidenceKind = "frame",
                FrameNumber = 1,
                TotalSizeBytes = 1024,
                OutTimeUs = 0
            });
        }
        catch { }
    }

    public void SetFirstFrameAnchorTicks(long ticks)
    {
        FirstFrameAnchorTicks = ticks;
    }

    public void SetLaunchAnchorTicks(long ticks)
    {
        LaunchAnchorTicks = ticks;
    }

    public void EmitNaturalExit(int exitCode, string stderr)
    {
        if (HasExited || SuppressExit) return;
        ExitCode = exitCode;
        HasExited = true;
        try { NaturalExit?.Invoke(exitCode, stderr); }
        catch { }
    }

    public OutputMeta Stop()
    {
        StopCalled = true;
        // Manual stop must mark the worker as exited without invoking the
        // NaturalExit event, matching the production worker behavior where
        // _manualStopped suppresses the natural-exit callback.
        if (!HasExited && !SuppressExit)
        {
            ExitCode = 0;
            HasExited = true;
        }
        return new OutputMeta { StderrLog = _stderrLog };
    }

    public string GetStderrLog() => _stderrLog;

    public bool WaitForExit(TimeSpan timeout)
    {
        WaitForExitCalled = true;
        var deadline = DateTime.UtcNow + timeout;
        while (!HasExited && !SuppressExit && DateTime.UtcNow < deadline)
            Thread.Sleep(10);
        return HasExited;
    }

    public void Dispose() { }
}

/// <summary>
/// Factory that returns preconfigured fake workers.
/// </summary>
internal sealed class FakeAvWorkerFactory : IAvWorkerFactory
{
    public FakeAudioCaptureWorker? AudioWorker { get; set; }
    public FakeVideoCaptureWorker? VideoWorker { get; set; }

    public int CreateAudioWorkerCount { get; private set; }
    public AudioCaptureSourceKind? LastAudioSourceKind { get; private set; }
    public int CreateVideoWorkerCount { get; private set; }

    public IAudioCaptureWorker CreateAudioWorker()
    {
        CreateAudioWorkerCount++;
        return AudioWorker ?? new FakeAudioCaptureWorker();
    }

    public IAudioCaptureWorker CreateAudioWorker(AudioCaptureSourceKind sourceKind)
    {
        LastAudioSourceKind = sourceKind;
        return CreateAudioWorker();
    }

    public IVideoCaptureWorker CreateVideoWorker()
    {
        CreateVideoWorkerCount++;
        return VideoWorker ?? new FakeVideoCaptureWorker();
    }
}

/// <summary>
/// Fake external process runner for finalizer production-path tests.
/// </summary>
internal sealed class FakeExternalProcessRunner : IExternalProcessRunner
{
    private readonly bool _simulateTimeout;
    private readonly string? _outputFileToCopy;
    private readonly int _exitCode;
    private readonly string _stderr;
    private readonly ManualResetEventSlim? _blockBeforeRun;
    private readonly bool _throwCancellationAfterWrite;
    private readonly bool _throwExceptionAfterWrite;

    public int RunCallCount { get; private set; }
    public string? LastFileName { get; private set; }
    public IReadOnlyList<string>? LastArgs { get; private set; }
    public TimeSpan? LastTimeout { get; private set; }
    public bool KillInvoked { get; private set; }

    public FakeExternalProcessRunner(
        bool simulateTimeout = false,
        string? outputFileToCopy = null,
        int exitCode = 0,
        string stderr = "",
        ManualResetEventSlim? blockBeforeRun = null,
        bool throwCancellationAfterWrite = false,
        bool throwExceptionAfterWrite = false)
    {
        _simulateTimeout = simulateTimeout;
        _outputFileToCopy = outputFileToCopy;
        _exitCode = exitCode;
        _stderr = stderr;
        _blockBeforeRun = blockBeforeRun;
        _throwCancellationAfterWrite = throwCancellationAfterWrite;
        _throwExceptionAfterWrite = throwExceptionAfterWrite;
    }

    public async Task<ExternalProcessResult> RunAsync(
        string fileName,
        IReadOnlyList<string> argumentList,
        TimeSpan timeout,
        bool captureStderr = true,
        Encoding? stderrEncoding = null,
        CancellationToken cancellationToken = default)
    {
        RunCallCount++;
        LastFileName = fileName;
        LastArgs = argumentList.ToList();
        LastTimeout = timeout;

        _blockBeforeRun?.Wait();

        if (_throwCancellationAfterWrite || _throwExceptionAfterWrite)
        {
            // Simulate the production runner surfacing the caller's cancellation
            // or an unexpected failure only after it has already begun writing to
            // the mux temp path (the last argument). The finalizer must still
            // clean up the partial mux file and never touch a pre-existing final.
            if (!string.IsNullOrEmpty(_outputFileToCopy) && argumentList.Count > 0)
            {
                var outputPath = argumentList.Last();
                try
                {
                    if (File.Exists(outputPath)) File.Delete(outputPath);
                    File.Copy(_outputFileToCopy, outputPath);
                }
                catch { }
            }

            if (_throwCancellationAfterWrite)
                throw new OperationCanceledException("simulated caller cancellation after partial write");
            throw new InvalidOperationException("simulated runner failure after partial write");
        }

        if (_simulateTimeout)
        {
            var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            using var timeoutCts = new CancellationTokenSource(timeout);
            using var reg1 = cancellationToken.Register(() => tcs.TrySetCanceled());
            using var reg2 = timeoutCts.Token.Register(() =>
            {
                KillInvoked = true;
                tcs.TrySetResult(true);
            });

            try { await tcs.Task.ConfigureAwait(false); }
            catch (OperationCanceledException) { throw; }

            return new ExternalProcessResult(-1, true, _stderr);
        }

        if (!string.IsNullOrEmpty(_outputFileToCopy) && argumentList.Count > 0)
        {
            var outputPath = argumentList.Last();
            try
            {
                if (File.Exists(outputPath)) File.Delete(outputPath);
                File.Copy(_outputFileToCopy, outputPath);
            }
            catch { }
        }

        return new ExternalProcessResult(_exitCode, false, _stderr);
    }
}

/// <summary>
/// Backend-level lifecycle tests for the split A/V capture backend using
/// deterministic fake workers. Members run sequentially because they mutate
/// the process-scoped data directory.
/// </summary>
[Collection("NonParallel-AgentRecorderDataDir")]
public sealed class AvSplitLifecycleTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string? _originalDataDir;

    public AvSplitLifecycleTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"avsplit-lifecycle-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
        _originalDataDir = Environment.GetEnvironmentVariable("AGENT_RECORDER_DATA_DIR");
        Environment.SetEnvironmentVariable("AGENT_RECORDER_DATA_DIR", _tempDir);
        DataDirResolver.SetOverride(_tempDir);
    }

    public void Dispose()
    {
        DataDirResolver.ClearOverride();
        if (_originalDataDir == null)
            Environment.SetEnvironmentVariable("AGENT_RECORDER_DATA_DIR", null);
        else
            Environment.SetEnvironmentVariable("AGENT_RECORDER_DATA_DIR", _originalDataDir);
        try { if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, recursive: true); }
        catch { }
    }

    private CaptureConfig CreateConfig(bool microphone = true)
    {
        return new CaptureConfig
        {
            SourceKind = "display",
            Microphone = microphone,
            MicDevice = "fake-mic",
            Fps = 30,
            Bounds = (0, 0, 320, 240),
            OutputPath = Path.Combine(_tempDir, $"final-{Guid.NewGuid():N}.mp4")
        };
    }

    private CaptureConfig CreateSystemLoopbackConfig(string? endpoint = null)
    {
        return new CaptureConfig
        {
            SourceKind = "display",
            AudioSourceKind = AudioCaptureSourceKind.SystemLoopback,
            SystemLoopbackEndpoint = endpoint ?? "{0.0.0.00000000}.{00000000-0000-0000-0000-000000000000}",
            Fps = 30,
            Bounds = (0, 0, 320, 240),
            OutputPath = Path.Combine(_tempDir, $"final-{Guid.NewGuid():N}.mp4")
        };
    }

    // ============================================================
    // System loopback lifecycle tests
    // ============================================================

    [Fact]
    public void SystemLoopback_CallsMux_WithCorrectFfmpegArgs()
    {
        var validAudio = CreateValidAudio();
        var validVideo = CreateValidVideo();
        var audio = new FakeAudioCaptureWorker(
            raiseAudioReadyOnStart: true,
            holdFileOpen: true,
            holdFileOpenCopyFrom: validAudio,
            stderrLog: "audio-stderr");
        var video = new FakeVideoCaptureWorker(stderrLog: "video-stderr");
        var factory = new FakeAvWorkerFactory { AudioWorker = audio, VideoWorker = video };
        var runner = new FakeExternalProcessRunner(outputFileToCopy: validVideo);
        var backend = new AvSplitCaptureBackend(factory, runner, new TempRetentionPolicy(_tempDir))
        {
            ApplyContinuityCheck = false
        };

        var cfg = CreateSystemLoopbackConfig();
        backend.Start(cfg);
        Assert.Equal(AudioCaptureSourceKind.SystemLoopback, factory.LastAudioSourceKind);
        backend.StartVideo();
        File.Copy(validVideo, video.OutputPath!, overwrite: true);

        video.EmitNaturalExit(0, "video-stderr");

        Assert.True(SpinWait.SpinUntil(() => backend.HasExited && runner.RunCallCount > 0, TimeSpan.FromSeconds(5)));
        Assert.True(runner.RunCallCount > 0, "Mux must be called (system loopback must not take the no-audio quick path)");

        var lastArgs = runner.LastArgs;
        Assert.NotNull(lastArgs);
        var argsStr = string.Join(" ", lastArgs!);
        Assert.Contains("-map 0:v:0", argsStr);
        Assert.Contains("-map [a]", argsStr);
        Assert.Contains("-c:v copy", argsStr);
        Assert.Contains("-c:a aac", argsStr);
        Assert.Contains("128k", argsStr);
        Assert.Contains("atrim", argsStr);
        Assert.Contains("asetpts", argsStr);
    }

    [Fact]
    public void SystemLoopback_MuxFailure_DoesNotWriteRecorded()
    {
        var validAudio = CreateValidAudio();
        var validVideo = CreateValidVideo();
        var audio = new FakeAudioCaptureWorker(
            raiseAudioReadyOnStart: true,
            holdFileOpen: true,
            holdFileOpenCopyFrom: validAudio);
        var video = new FakeVideoCaptureWorker();
        var factory = new FakeAvWorkerFactory { AudioWorker = audio, VideoWorker = video };
        // Simulate mux failure (exit code 1)
        var runner = new FakeExternalProcessRunner(exitCode: 1, stderr: "mux-failed");
        var backend = new AvSplitCaptureBackend(factory, runner, new TempRetentionPolicy(_tempDir))
        {
            ApplyContinuityCheck = false
        };

        var cfg = CreateSystemLoopbackConfig();
        backend.Start(cfg);
        backend.StartVideo();
        File.Copy(validVideo, video.OutputPath!, overwrite: true);

        video.EmitNaturalExit(0, "");

        Assert.True(SpinWait.SpinUntil(() => backend.HasExited, TimeSpan.FromSeconds(5)));
        var meta = backend.LastMeta;
        Assert.NotNull(meta);
        Assert.DoesNotContain("system_loopback_recorded", meta!.AudioStatus ?? "", StringComparison.OrdinalIgnoreCase);
        Assert.Contains("mux-failed", meta.StderrLog ?? "");
        Assert.Equal("system-loopback", meta.AudioSourceKind);
    }

    [Fact]
    public void SystemLoopback_MuxTimeout_DoesNotWriteRecorded()
    {
        var validAudio = CreateValidAudio();
        var validVideo = CreateValidVideo();
        var audio = new FakeAudioCaptureWorker(
            raiseAudioReadyOnStart: true,
            holdFileOpen: true,
            holdFileOpenCopyFrom: validAudio);
        var video = new FakeVideoCaptureWorker();
        var factory = new FakeAvWorkerFactory { AudioWorker = audio, VideoWorker = video };
        // Simulate mux timeout
        var runner = new FakeExternalProcessRunner(simulateTimeout: true, stderr: "mux-timeout");
        var backend = new AvSplitCaptureBackend(factory, runner, new TempRetentionPolicy(_tempDir))
        {
            ApplyContinuityCheck = false
        };

        var cfg = CreateSystemLoopbackConfig();
        backend.Start(cfg);
        backend.StartVideo();
        File.Copy(validVideo, video.OutputPath!, overwrite: true);

        video.EmitNaturalExit(0, "");

        Assert.True(SpinWait.SpinUntil(() => backend.HasExited, TimeSpan.FromSeconds(5)));
        var meta = backend.LastMeta;
        Assert.NotNull(meta);
        Assert.DoesNotContain("system_loopback_recorded", meta!.AudioStatus ?? "", StringComparison.OrdinalIgnoreCase);
        Assert.Equal("system-loopback", meta.AudioSourceKind);
    }

    [Fact]
    public void SystemLoopback_Metadata_HasCorrectSourceKind()
    {
        var validAudio = CreateValidAudio();
        var validVideo = CreateValidVideo();
        var audio = new FakeAudioCaptureWorker(
            raiseAudioReadyOnStart: true,
            holdFileOpen: true,
            holdFileOpenCopyFrom: validAudio);
        var video = new FakeVideoCaptureWorker();
        var factory = new FakeAvWorkerFactory { AudioWorker = audio, VideoWorker = video };
        var runner = new FakeExternalProcessRunner(outputFileToCopy: validVideo);
        var backend = new AvSplitCaptureBackend(factory, runner, new TempRetentionPolicy(_tempDir))
        {
            ApplyContinuityCheck = false
        };

        var cfg = CreateSystemLoopbackConfig();
        backend.Start(cfg);
        backend.StartVideo();
        File.Copy(validVideo, video.OutputPath!, overwrite: true);

        video.EmitNaturalExit(0, "");

        Assert.True(SpinWait.SpinUntil(() => backend.HasExited, TimeSpan.FromSeconds(5)));
        var meta = backend.LastMeta;
        Assert.NotNull(meta);
        Assert.Equal("system-loopback", meta!.AudioSourceKind);
        // Must not use microphone keys
        if (meta.AudioStatus != null)
            Assert.DoesNotContain("microphone", meta.AudioStatus, StringComparison.OrdinalIgnoreCase);
        if (meta.AudioCaptureBackend != null)
            Assert.DoesNotContain("microphone", meta.AudioCaptureBackend, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void SystemLoopback_Start_RejectsIllegalCombinations()
    {
        // Illegal audio configurations must fail during Start() and must not
        // create the backend temp directory or any worker (the audio
        // validate/normalize runs before any output side effects).
        string BackendTempDir() => Path.Combine(_tempDir, "temp");

        // Microphone + SystemLoopback conflict
        {
            var cfg = new CaptureConfig
            {
                SourceKind = "display",
                Microphone = true,
                MicDevice = "fake-mic",
                AudioSourceKind = AudioCaptureSourceKind.SystemLoopback,
                SystemLoopbackEndpoint = "{endpoint}",
                OutputPath = Path.Combine(_tempDir, "conflict.mp4")
            };
            var backend = new AvSplitCaptureBackend(
                new FakeAvWorkerFactory(), new FakeExternalProcessRunner(), new TempRetentionPolicy(_tempDir));
            var ex = Assert.Throws<ArgumentException>(() => backend.Start(cfg));
            Assert.Contains("cannot both", ex.Message, StringComparison.OrdinalIgnoreCase);
            Assert.False(Directory.Exists(BackendTempDir()), "temp dir must not be created for illegal config");
        }

        // SystemLoopback without endpoint
        {
            var cfg = CreateSystemLoopbackConfig(endpoint: null);
            // Clear the endpoint to create an invalid config
            cfg.SystemLoopbackEndpoint = null;
            var backend = new AvSplitCaptureBackend(
                new FakeAvWorkerFactory(), new FakeExternalProcessRunner(), new TempRetentionPolicy(_tempDir));
            var ex = Assert.Throws<ArgumentException>(() => backend.Start(cfg));
            Assert.Contains("requires", ex.Message, StringComparison.OrdinalIgnoreCase);
            Assert.False(Directory.Exists(BackendTempDir()), "temp dir must not be created for illegal config");
        }

        // SystemLoopback with MicDevice set
        {
            var cfg = CreateSystemLoopbackConfig();
            cfg.MicDevice = "unexpected-mic";
            var backend = new AvSplitCaptureBackend(
                new FakeAvWorkerFactory(), new FakeExternalProcessRunner(), new TempRetentionPolicy(_tempDir));
            var ex = Assert.Throws<ArgumentException>(() => backend.Start(cfg));
            Assert.Contains("MicDevice", ex.Message, StringComparison.OrdinalIgnoreCase);
            Assert.False(Directory.Exists(BackendTempDir()), "temp dir must not be created for illegal config");
        }
    }

    [Fact]
    public void SystemLoopback_NoAudioQuickPath_NotTaken()
    {
        // Verify that system loopback does NOT take the no-audio copy quick path.
        // When audio file is missing, the backend will fail at the WAV stability
        // check (wav_file_not_stable) rather than silently producing a video-only
        // output. The key is that AudioStatus is NOT "not_requested".
        var validVideo = CreateValidVideo();
        var audio = new FakeAudioCaptureWorker(
            raiseAudioReadyOnStart: true,
            stderrLog: "audio-stderr");
        var video = new FakeVideoCaptureWorker(stderrLog: "video-stderr");
        var factory = new FakeAvWorkerFactory { AudioWorker = audio, VideoWorker = video };
        var runner = new FakeExternalProcessRunner(outputFileToCopy: validVideo);
        var backend = new AvSplitCaptureBackend(factory, runner, new TempRetentionPolicy(_tempDir))
        {
            ApplyContinuityCheck = false
        };

        var cfg = CreateSystemLoopbackConfig();
        backend.Start(cfg);
        backend.StartVideo();
        File.Copy(validVideo, video.OutputPath!, overwrite: true);

        video.EmitNaturalExit(0, "video-stderr");

        Assert.True(SpinWait.SpinUntil(() => backend.HasExited, TimeSpan.FromSeconds(5)));
        var meta = backend.LastMeta;
        Assert.NotNull(meta);
        // Must NOT be "not_requested" (the no-audio quick path label)
        Assert.NotEqual("not_requested", meta!.AudioStatus);
        // Must report the WAV stability failure
        Assert.Contains("wav_file_not_stable", meta.StderrLog ?? "");
    }

    // ============================================================
    // Audio source kind mismatch tests (P0-3)
    // ============================================================

    [Fact]
    public void SystemLoopback_SourceKindMismatch_ProtocolError()
    {
        // Request system-loopback, but the worker reports microphone.
        // The fake worker detects the mismatch during Start() and fires
        // NaturalExit synchronously, causing the backend to conclude
        // before the video worker is started.
        var audio = new FakeAudioCaptureWorker(
            raiseAudioReadyOnStart: true,
            reportedSourceKind: "microphone");
        var video = new FakeVideoCaptureWorker();
        var factory = new FakeAvWorkerFactory { AudioWorker = audio, VideoWorker = video };
        var backend = new AvSplitCaptureBackend(factory, new FakeExternalProcessRunner(), new TempRetentionPolicy(_tempDir));

        var cfg = CreateSystemLoopbackConfig();
        backend.Start(cfg);

        // The NaturalExit fires synchronously during Start() because the
        // source mismatch is detected immediately. The backend concludes
        // before StartVideo() is called.
        Assert.True(backend.HasExited, "Backend should conclude immediately on source mismatch");
        Assert.True(audio.ProtocolErrorRaised);
        Assert.False(backend.IsAudioReady);

        // StartVideo must be a no-op when the backend is already concluded.
        backend.StartVideo();
        Assert.Null(video.OutputPath);
        Assert.False(video.HasExited);

        var meta = backend.LastMeta;
        Assert.NotNull(meta);
        Assert.Contains("protocol_invalid_started", meta!.StderrLog ?? "");
        Assert.Contains("audio_worker_exited_before_video_started", meta.Warnings ?? Array.Empty<string>());
    }

    [Fact]
    public void Microphone_SourceKindMismatch_ProtocolError()
    {
        // Request microphone, but the worker reports system-loopback.
        var audio = new FakeAudioCaptureWorker(
            raiseAudioReadyOnStart: true,
            reportedSourceKind: "system-loopback");
        var video = new FakeVideoCaptureWorker();
        var factory = new FakeAvWorkerFactory { AudioWorker = audio, VideoWorker = video };
        var backend = new AvSplitCaptureBackend(factory, new FakeExternalProcessRunner(), new TempRetentionPolicy(_tempDir));

        var cfg = CreateConfig();
        backend.Start(cfg);

        Assert.True(backend.HasExited, "Backend should conclude immediately on source mismatch");
        Assert.True(audio.ProtocolErrorRaised);
        Assert.False(backend.IsAudioReady);

        backend.StartVideo();
        Assert.Null(video.OutputPath);
        Assert.False(video.HasExited);

        var meta = backend.LastMeta;
        Assert.NotNull(meta);
        Assert.Contains("protocol_invalid_started", meta!.StderrLog ?? "");
        Assert.Contains("audio_worker_exited_before_video_started", meta.Warnings ?? Array.Empty<string>());
    }

    [Fact]
    public void SystemLoopback_SourceKindMatch_Ready()
    {
        // Request system-loopback, and the worker also reports system-loopback.
        // No protocol error; AudioReady is raised normally.
        var audio = new FakeAudioCaptureWorker(
            raiseAudioReadyOnStart: true,
            reportedSourceKind: "system-loopback");
        var video = new FakeVideoCaptureWorker();
        var factory = new FakeAvWorkerFactory { AudioWorker = audio, VideoWorker = video };
        var backend = new AvSplitCaptureBackend(factory, new FakeExternalProcessRunner(), new TempRetentionPolicy(_tempDir));

        var cfg = CreateSystemLoopbackConfig();
        backend.Start(cfg);

        Assert.True(SpinWait.SpinUntil(() => backend.IsAudioReady, TimeSpan.FromSeconds(2)));
        Assert.False(audio.ProtocolErrorRaised);

        // Video worker can still be started normally.
        backend.StartVideo();
        Assert.NotNull(video.OutputPath);
        Assert.False(video.HasExited);
    }

    [Fact]
    public void Microphone_SourceKindMatch_Ready()
    {
        // Request microphone, and the worker also reports microphone.
        var audio = new FakeAudioCaptureWorker(
            raiseAudioReadyOnStart: true,
            reportedSourceKind: "microphone");
        var video = new FakeVideoCaptureWorker();
        var factory = new FakeAvWorkerFactory { AudioWorker = audio, VideoWorker = video };
        var backend = new AvSplitCaptureBackend(factory, new FakeExternalProcessRunner(), new TempRetentionPolicy(_tempDir));

        var cfg = CreateConfig();
        backend.Start(cfg);

        Assert.True(SpinWait.SpinUntil(() => backend.IsAudioReady, TimeSpan.FromSeconds(2)));
        Assert.False(audio.ProtocolErrorRaised);

        backend.StartVideo();
        Assert.NotNull(video.OutputPath);
        Assert.False(video.HasExited);
    }

    [Fact]
    public void NaturalVideoExit_FollowsStopDrainMuxOrder()
    {
        var validAudio = CreateValidAudio();
        var validVideo = CreateValidVideo();
        var audio = new FakeAudioCaptureWorker(
            raiseAudioReadyOnStart: true,
            holdFileOpen: true,
            holdFileOpenCopyFrom: validAudio,
            stderrLog: "audio-stderr");
        var video = new FakeVideoCaptureWorker(stderrLog: "video-stderr");
        var factory = new FakeAvWorkerFactory { AudioWorker = audio, VideoWorker = video };
        var runner = new FakeExternalProcessRunner(outputFileToCopy: validVideo);
        var backend = new AvSplitCaptureBackend(factory, runner, new TempRetentionPolicy(_tempDir))
        {
            ApplyContinuityCheck = false
        };

        backend.Start(CreateConfig());
        backend.StartVideo();
        File.Copy(validVideo, video.OutputPath!, overwrite: true);

        // Natural video exit must not re-stop the video worker, but must still
        // stop audio, wait for it to exit, wait for WAV stability, then mux.
        video.EmitNaturalExit(0, "video-stderr");

        Assert.True(SpinWait.SpinUntil(() => backend.HasExited && runner.RunCallCount > 0, TimeSpan.FromSeconds(5)));
        Assert.False(video.StopCalled); // natural exit: do not stop an already-exited worker
        Assert.True(audio.StopCalled);
        Assert.True(audio.WaitForExitCalled);
        Assert.True(runner.RunCallCount > 0);
    }

    [Fact]
    public void ManualStop_FollowsStopDrainMuxOrder()
    {
        var validAudio = CreateValidAudio();
        var validVideo = CreateValidVideo();
        var audio = new FakeAudioCaptureWorker(
            raiseAudioReadyOnStart: true,
            holdFileOpen: true,
            holdFileOpenCopyFrom: validAudio,
            stderrLog: "audio-stderr");
        var video = new FakeVideoCaptureWorker(stderrLog: "video-stderr");
        var factory = new FakeAvWorkerFactory { AudioWorker = audio, VideoWorker = video };
        var runner = new FakeExternalProcessRunner(outputFileToCopy: validVideo);
        var backend = new AvSplitCaptureBackend(factory, runner, new TempRetentionPolicy(_tempDir))
        {
            ApplyContinuityCheck = false
        };

        backend.Start(CreateConfig());
        backend.StartVideo();
        File.Copy(validVideo, video.OutputPath!, overwrite: true);

        // Active stop must first stop and drain video, then audio, then mux.
        var meta = backend.Stop();

        Assert.True(video.StopCalled);
        Assert.True(video.WaitForExitCalled);
        Assert.True(audio.StopCalled);
        Assert.True(audio.WaitForExitCalled);
        Assert.True(runner.RunCallCount > 0);
        Assert.Equal(0, video.ExitCode);
        Assert.Contains("video-stderr", meta.StderrLog);
    }

    [Fact]
    public void AudioWaitTimeout_BlocksFinalizer()
    {
        var validVideo = CreateValidVideo();
        var audio = new FakeAudioCaptureWorker(
            raiseAudioReadyOnStart: true,
            stderrLog: "audio-stderr");
        // Prevent the fake audio worker from ever exiting.
        audio.SuppressExit = true;
        var video = new FakeVideoCaptureWorker();
        var factory = new FakeAvWorkerFactory { AudioWorker = audio, VideoWorker = video };
        var runner = new FakeExternalProcessRunner(outputFileToCopy: validVideo);
        var backend = new AvSplitCaptureBackend(factory, runner, new TempRetentionPolicy(_tempDir))
        {
            ApplyContinuityCheck = false
        };

        backend.Start(CreateConfig());
        backend.StartVideo();
        File.Copy(validVideo, video.OutputPath!, overwrite: true);

        video.EmitNaturalExit(0, "");

        Assert.True(SpinWait.SpinUntil(() => backend.HasExited, TimeSpan.FromSeconds(15)));
        Assert.Equal(0, runner.RunCallCount);
        Assert.Contains("audio_worker_exit_timeout", backend.LastMeta?.StderrLog ?? "");
    }

    [Fact]
    public void WavLocked_BlocksFinalizer()
    {
        var validAudio = CreateValidAudio();
        var validVideo = CreateValidVideo();
        var audio = new FakeAudioCaptureWorker(
            raiseAudioReadyOnStart: true,
            holdFileOpen: true,
            holdFileOpenCopyFrom: validAudio);
        // Do not release the file handle on Stop, so WAV remains locked.
        audio.SuppressHandleRelease = true;
        var video = new FakeVideoCaptureWorker();
        var factory = new FakeAvWorkerFactory { AudioWorker = audio, VideoWorker = video };
        var runner = new FakeExternalProcessRunner(outputFileToCopy: validVideo);
        var backend = new AvSplitCaptureBackend(factory, runner, new TempRetentionPolicy(_tempDir))
        {
            ApplyContinuityCheck = false
        };

        backend.Start(CreateConfig());
        backend.StartVideo();
        File.Copy(validVideo, video.OutputPath!, overwrite: true);

        video.EmitNaturalExit(0, "");

        Assert.True(SpinWait.SpinUntil(() => backend.HasExited, TimeSpan.FromSeconds(5)));
        Assert.Equal(0, runner.RunCallCount);
        Assert.Contains("wav_file_not_stable", backend.LastMeta?.StderrLog ?? "");
    }

    [Fact]
    public void VideoWaitTimeout_BlocksFinalizer()
    {
        var validAudio = CreateValidAudio();
        var audio = new FakeAudioCaptureWorker(
            raiseAudioReadyOnStart: true,
            holdFileOpen: true,
            holdFileOpenCopyFrom: validAudio);
        var video = new FakeVideoCaptureWorker(stderrLog: "video-stderr");
        // Simulate a video worker that ignores the quit signal and never exits.
        video.SuppressExit = true;
        var factory = new FakeAvWorkerFactory { AudioWorker = audio, VideoWorker = video };
        var runner = new FakeExternalProcessRunner(outputFileToCopy: CreateValidVideo());
        var backend = new AvSplitCaptureBackend(factory, runner, new TempRetentionPolicy(_tempDir))
        {
            ApplyContinuityCheck = false
        };

        backend.Start(CreateConfig());
        backend.StartVideo();
        // The audio worker already copied the valid audio into its output path
        // because holdFileOpenCopyFrom was provided; do not copy again while the
        // handle is still open.

        var meta = backend.Stop();

        Assert.True(video.StopCalled);
        Assert.True(video.WaitForExitCalled);
        Assert.Equal(0, runner.RunCallCount);
        Assert.Contains("video_worker_exit_timeout", meta.StderrLog ?? "");
        // Audio worker must still be stopped and drained.
        Assert.True(audio.StopCalled);
    }

    [Fact]
    public async Task ConvergeExactlyOnce_NaturalThenManual()
    {
        var validAudio = CreateValidAudio();
        var validVideo = CreateValidVideo();
        var audio = new FakeAudioCaptureWorker(raiseAudioReadyOnStart: true);
        var video = new FakeVideoCaptureWorker();
        var factory = new FakeAvWorkerFactory { AudioWorker = audio, VideoWorker = video };
        var runner = new FakeExternalProcessRunner(outputFileToCopy: validVideo);
        var backend = new AvSplitCaptureBackend(factory, runner, new TempRetentionPolicy(_tempDir))
        {
            ApplyContinuityCheck = false
        };

        backend.Start(CreateConfig());
        backend.StartVideo();
        File.Copy(validVideo, video.OutputPath!, overwrite: true);
        File.Copy(validAudio, audio.OutputPath!, overwrite: true);

        var tcs = new TaskCompletionSource<OutputMeta>();
        backend.OnNaturalExit((_, meta) => tcs.TrySetResult(meta));

        // Race: natural exit and manual stop arrive concurrently.
        video.EmitNaturalExit(0, "");
        var manualMeta = backend.Stop();

        var naturalMeta = await tcs.Task.WaitAsync(TimeSpan.FromSeconds(5));

        // Exactly one finalizer invocation despite both paths entering.
        Assert.Equal(1, runner.RunCallCount);
        // Both callers receive the same single result.
        Assert.Same(naturalMeta, manualMeta);
    }

    [Fact]
    public async Task ConvergeExactlyOnce_ConcurrentCallers_ObtainSameResult()
    {
        var validAudio = CreateValidAudio();
        var validVideo = CreateValidVideo();
        var audio = new FakeAudioCaptureWorker(raiseAudioReadyOnStart: true);
        var video = new FakeVideoCaptureWorker(stderrLog: "video-stderr");
        var gate = new ManualResetEventSlim(false);
        var runner = new FakeExternalProcessRunner(outputFileToCopy: validVideo, blockBeforeRun: gate);
        var factory = new FakeAvWorkerFactory { AudioWorker = audio, VideoWorker = video };
        var backend = new AvSplitCaptureBackend(factory, runner, new TempRetentionPolicy(_tempDir))
        {
            ApplyContinuityCheck = false
        };

        backend.Start(CreateConfig());
        backend.StartVideo();
        File.Copy(validVideo, video.OutputPath!, overwrite: true);
        File.Copy(validAudio, audio.OutputPath!, overwrite: true);

        OutputMeta? naturalMeta = null;
        backend.OnNaturalExit((_, meta) => naturalMeta = meta);

        // Natural exit becomes the convergence owner and blocks inside the
        // finalizer until the gate is released.
        var naturalTask = Task.Run(() => video.EmitNaturalExit(0, "video-stderr"));

        // Wait until the owner has entered the finalizer (runner is blocked).
        Assert.True(SpinWait.SpinUntil(() => runner.RunCallCount > 0, TimeSpan.FromSeconds(10)),
            "owner must reach the finalizer runner within the timeout");

        // While the owner is blocked, concurrent stop requests must wait on the
        // convergence primitive instead of starting a second finalization.
        OutputMeta? manualMeta1 = null;
        OutputMeta? manualMeta2 = null;
        var manualTask1 = Task.Run(() => { manualMeta1 = backend.Stop(); });
        var manualTask2 = Task.Run(() => { manualMeta2 = backend.Stop(); });

        // Give waiters time to register on the convergence task.
        await Task.Delay(200);

        // Release the gate so finalization completes and waiters are unblocked.
        gate.Set();

        await Task.WhenAll(naturalTask, manualTask1, manualTask2).WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(1, runner.RunCallCount);
        Assert.NotNull(naturalMeta);
        Assert.NotNull(manualMeta1);
        Assert.NotNull(manualMeta2);
        Assert.Same(naturalMeta, manualMeta1);
        Assert.Same(naturalMeta, manualMeta2);
    }

    [Fact]
    public async Task RuntimeAudioLostAtMs_PropagatedToMeta()
    {
        var lostAt = 1234567890123L;
        var validAudio = CreateValidAudio();
        var validVideo = CreateValidVideo();
        var audio = new FakeAudioCaptureWorker(
            raiseAudioReadyOnStart: true,
            runtimeAudioLostAtMs: lostAt);
        var video = new FakeVideoCaptureWorker();
        var factory = new FakeAvWorkerFactory { AudioWorker = audio, VideoWorker = video };
        var runner = new FakeExternalProcessRunner(outputFileToCopy: validVideo);
        var backend = new AvSplitCaptureBackend(factory, runner, new TempRetentionPolicy(_tempDir))
        {
            ApplyContinuityCheck = false
        };

        backend.Start(CreateConfig());
        backend.StartVideo();
        File.Copy(validVideo, video.OutputPath!, overwrite: true);
        File.Copy(validAudio, audio.OutputPath!, overwrite: true);

        var tcs = new TaskCompletionSource<OutputMeta>();
        backend.OnNaturalExit((_, meta) => tcs.TrySetResult(meta));
        video.EmitNaturalExit(0, "");

        var meta = await tcs.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(lostAt, meta.AudioLostAtMs);
        Assert.Equal("lost", meta.AudioStatus);
    }

    [Fact]
    public void AudioHelperFailsBeforeVideoStarts_VideoWorkerDoesNotStart()
    {
        var audio = new FakeAudioCaptureWorker(naturalExitDelayMs: 0, stderrLog: "audio-premature-stderr");
        audio.SetTerminalSummary(new AudioHelperSessionSummary
        {
            State = AudioHelperSessionState.Failed,
            ErrorCode = "audio_endpoint_inactive"
        });
        var video = new FakeVideoCaptureWorker();
        var factory = new FakeAvWorkerFactory { AudioWorker = audio, VideoWorker = video };
        var backend = new AvSplitCaptureBackend(factory, new FakeExternalProcessRunner(), new TempRetentionPolicy(_tempDir))
        {
            ApplyContinuityCheck = false
        };

        backend.Start(CreateConfig());

        // The fake audio worker raises natural exit asynchronously (even with
        // delay 0), so wait for the backend to conclude before calling StartVideo.
        Assert.True(SpinWait.SpinUntil(() => backend.HasExited, TimeSpan.FromSeconds(2)),
            "Backend should conclude after the audio helper fails before video starts.");
        Assert.Equal("audio_endpoint_inactive", backend.LastMeta?.AudioHelperErrorCode);

        backend.StartVideo();

        Assert.Null(video.OutputPath);
        Assert.False(video.HasExited);
    }

    [Fact]
    public void AudioReady_SyncInStart_NotLost()
    {
        var audio = new FakeAudioCaptureWorker(raiseAudioReadyOnStart: true);
        var factory = new FakeAvWorkerFactory { AudioWorker = audio };
        var backend = new AvSplitCaptureBackend(factory, new FakeExternalProcessRunner(), new TempRetentionPolicy(_tempDir));

        int callbackCount = 0;
        backend.AudioReady += () => Interlocked.Increment(ref callbackCount);

        backend.Start(CreateConfig());

        Assert.True(backend.IsAudioReady);
        Assert.Equal(1, callbackCount);
    }

    [Fact]
    public void AudioReady_AsyncAfterStart_NotLost()
    {
        var audio = new FakeAudioCaptureWorker(audioReadyDelayMs: 50);
        var factory = new FakeAvWorkerFactory { AudioWorker = audio };
        var backend = new AvSplitCaptureBackend(factory, new FakeExternalProcessRunner(), new TempRetentionPolicy(_tempDir));

        int callbackCount = 0;
        backend.AudioReady += () => Interlocked.Increment(ref callbackCount);

        backend.Start(CreateConfig());
        Assert.False(backend.IsAudioReady);

        Assert.True(SpinWait.SpinUntil(() => backend.IsAudioReady, TimeSpan.FromSeconds(2)));
        Assert.Equal(1, callbackCount);
    }

    [Fact]
    public void FirstFrame_ForwardedExactlyOnce()
    {
        // Disable auto first-frame so we can raise it manually multiple times.
        var video = new FakeVideoCaptureWorker(firstFrameDelayMs: -1);
        var factory = new FakeAvWorkerFactory { VideoWorker = video };
        var backend = new AvSplitCaptureBackend(factory, new FakeExternalProcessRunner(), new TempRetentionPolicy(_tempDir));

        int count = 0;
        ((IFirstFrameObservableCaptureBackend)backend).FirstFrameObserved += _ => Interlocked.Increment(ref count);

        backend.Start(CreateConfig(microphone: false));
        backend.StartVideo();
        video.EmitFirstFrame();
        video.EmitFirstFrame();
        video.EmitFirstFrame();

        Assert.Equal(1, count);
    }

    [Fact]
    public async Task AudioWorker_PrematureExit_Reported()
    {
        var audio = new FakeAudioCaptureWorker(raiseAudioReadyOnStart: true, stderrLog: "audio-premature-stderr");
        var video = new FakeVideoCaptureWorker();
        var factory = new FakeAvWorkerFactory { AudioWorker = audio, VideoWorker = video };
        var runner = new FakeExternalProcessRunner(outputFileToCopy: CreateValidVideo());
        var backend = new AvSplitCaptureBackend(factory, runner, new TempRetentionPolicy(_tempDir));

        var tcs = new TaskCompletionSource<OutputMeta>();
        backend.OnNaturalExit((_, meta) => tcs.TrySetResult(meta));

        backend.Start(CreateConfig());
        backend.StartVideo();

        // Audio exits while video is still running.
        audio.EmitNaturalExit(1, "audio-premature-stderr");

        // Now end video naturally.
        video.EmitNaturalExit(0, "video-stderr");

        var meta = await tcs.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Contains("audio-premature-stderr", meta.StderrLog);
    }

    [Fact]
    public async Task Stderr_VideoAudioMux_Drained()
    {
        var validAudio = CreateValidAudio();
        var validVideo = CreateValidVideo();
        var audio = new FakeAudioCaptureWorker(
            raiseAudioReadyOnStart: true,
            stderrLog: "audio-stderr");
        var video = new FakeVideoCaptureWorker(stderrLog: "video-stderr");
        var factory = new FakeAvWorkerFactory { AudioWorker = audio, VideoWorker = video };
        var runner = new FakeExternalProcessRunner(outputFileToCopy: validVideo, stderr: "mux-stderr");
        var backend = new AvSplitCaptureBackend(factory, runner, new TempRetentionPolicy(_tempDir))
        {
            ApplyContinuityCheck = false
        };

        var tcs = new TaskCompletionSource<OutputMeta>();
        backend.OnNaturalExit((_, meta) => tcs.TrySetResult(meta));

        backend.Start(CreateConfig());
        backend.StartVideo();
        File.Copy(validVideo, video.OutputPath!, overwrite: true);
        File.Copy(validAudio, audio.OutputPath!, overwrite: true);
        video.EmitNaturalExit(0, "video-stderr");

        var meta = await tcs.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Contains("audio-stderr", meta.StderrLog);
        Assert.Contains("video-stderr", meta.StderrLog);
        Assert.Contains("mux-stderr", meta.StderrLog);
    }

    [Fact]
    public void TempFiles_CleanupOnSuccessAndFailure()
    {
        // Success path: valid temp files => finalizer succeeds => temp files deleted.
        {
            var validVideo = CreateValidVideo();
            var validAudio = CreateValidAudio();
            var audio = new FakeAudioCaptureWorker(raiseAudioReadyOnStart: true);
            var video = new FakeVideoCaptureWorker();
            var factory = new FakeAvWorkerFactory { AudioWorker = audio, VideoWorker = video };
            var runner = new FakeExternalProcessRunner(outputFileToCopy: CreateValidVideo());
            var backend = new AvSplitCaptureBackend(factory, runner, new TempRetentionPolicy(_tempDir));

            var cfg = CreateConfig();
            backend.Start(cfg);
            backend.StartVideo();
            // Point workers at real temp files so the backend finalizes against them.
            File.Copy(validVideo, video.OutputPath!, overwrite: true);
            File.Copy(validAudio, audio.OutputPath!, overwrite: true);

            video.EmitNaturalExit(0, "");
            Assert.True(SpinWait.SpinUntil(() => backend.HasExited, TimeSpan.FromSeconds(5)));

            Assert.False(File.Exists(video.OutputPath));
            Assert.False(File.Exists(audio.OutputPath));
        }

        // Failure path: empty temp files => finalizer fails => temp files moved to failed/.
        {
            var audio = new FakeAudioCaptureWorker(raiseAudioReadyOnStart: true);
            var video = new FakeVideoCaptureWorker();
            var factory = new FakeAvWorkerFactory { AudioWorker = audio, VideoWorker = video };
            var runner = new FakeExternalProcessRunner(outputFileToCopy: CreateValidVideo());
            var backend = new AvSplitCaptureBackend(factory, runner, new TempRetentionPolicy(_tempDir));

            var cfg = CreateConfig();
            backend.Start(cfg);
            backend.StartVideo();
            File.WriteAllText(video.OutputPath!, "not a video");
            File.WriteAllText(audio.OutputPath!, "not audio");

            video.EmitNaturalExit(0, "");
            Assert.True(SpinWait.SpinUntil(() => backend.HasExited, TimeSpan.FromSeconds(5)));

            var recordingId = Path.GetFileNameWithoutExtension(cfg.OutputPath);
            var failedDir = Path.Combine(_tempDir, "failed", recordingId);
            Assert.True(Directory.Exists(failedDir));
            Assert.True(File.Exists(Path.Combine(failedDir, "video.mp4")) || File.Exists(video.OutputPath));
            Assert.True(File.Exists(Path.Combine(failedDir, "audio.wav")) || File.Exists(audio.OutputPath));
        }
    }

    [Fact]
    public void AudioHelperErrorCode_FailedSummary_AudioEndpointInactive_NormalizedToLowercase()
    {
        var audio = new FakeAudioCaptureWorker(raiseAudioReadyOnStart: true);
        audio.SetTerminalSummary(new AudioHelperSessionSummary
        {
            State = AudioHelperSessionState.Failed,
            ErrorCode = "AUDIO_ENDPOINT_INACTIVE"
        });
        var video = new FakeVideoCaptureWorker();
        var factory = new FakeAvWorkerFactory { AudioWorker = audio, VideoWorker = video };
        var backend = new AvSplitCaptureBackend(factory, new FakeExternalProcessRunner(), new TempRetentionPolicy(_tempDir))
        {
            ApplyContinuityCheck = false
        };

        backend.Start(CreateConfig());
        backend.StartVideo();
        video.EmitNaturalExit(0, "");

        Assert.True(SpinWait.SpinUntil(() => backend.HasExited, TimeSpan.FromSeconds(5)));
        Assert.Equal("audio_endpoint_inactive", backend.LastMeta?.AudioHelperErrorCode);
    }

    [Fact]
    public void AudioHelperErrorCode_UnknownNonEmptyCode_MappedToAudioHelperFailure()
    {
        var audio = new FakeAudioCaptureWorker(raiseAudioReadyOnStart: true);
        audio.SetTerminalSummary(new AudioHelperSessionSummary
        {
            State = AudioHelperSessionState.Failed,
            ErrorCode = "some_unknown_bizarre_code"
        });
        var video = new FakeVideoCaptureWorker();
        var factory = new FakeAvWorkerFactory { AudioWorker = audio, VideoWorker = video };
        var backend = new AvSplitCaptureBackend(factory, new FakeExternalProcessRunner(), new TempRetentionPolicy(_tempDir))
        {
            ApplyContinuityCheck = false
        };

        backend.Start(CreateConfig());
        backend.StartVideo();
        video.EmitNaturalExit(0, "");

        Assert.True(SpinWait.SpinUntil(() => backend.HasExited, TimeSpan.FromSeconds(5)));
        Assert.Equal("audio_helper_failure", backend.LastMeta?.AudioHelperErrorCode);
    }

    [Fact]
    public void AudioHelperErrorCode_SuccessSummary_IsNull()
    {
        var audio = new FakeAudioCaptureWorker(raiseAudioReadyOnStart: true);
        audio.SetTerminalSummary(new AudioHelperSessionSummary { State = AudioHelperSessionState.Success });
        var video = new FakeVideoCaptureWorker();
        var factory = new FakeAvWorkerFactory { AudioWorker = audio, VideoWorker = video };
        var backend = new AvSplitCaptureBackend(factory, new FakeExternalProcessRunner(), new TempRetentionPolicy(_tempDir))
        {
            ApplyContinuityCheck = false
        };

        backend.Start(CreateConfig());
        backend.StartVideo();
        video.EmitNaturalExit(0, "");

        Assert.True(SpinWait.SpinUntil(() => backend.HasExited, TimeSpan.FromSeconds(5)));
        Assert.Null(backend.LastMeta?.AudioHelperErrorCode);
    }

    [Fact]
    public void AudioHelperErrorCode_SuccessSummary_VideoFailure_IsNull()
    {
        var validAudio = CreateValidAudio();
        var audio = new FakeAudioCaptureWorker(raiseAudioReadyOnStart: true, holdFileOpen: true, holdFileOpenCopyFrom: validAudio);
        audio.SetTerminalSummary(new AudioHelperSessionSummary { State = AudioHelperSessionState.Success });
        var video = new FakeVideoCaptureWorker();
        var factory = new FakeAvWorkerFactory { AudioWorker = audio, VideoWorker = video };
        var backend = new AvSplitCaptureBackend(factory, new FakeExternalProcessRunner(), new TempRetentionPolicy(_tempDir))
        {
            ApplyContinuityCheck = false
        };

        backend.Start(CreateConfig());
        backend.StartVideo();
        // Leave the temp video file empty/invalid so the video stability check fails.
        video.EmitNaturalExit(0, "");

        Assert.True(SpinWait.SpinUntil(() => backend.HasExited, TimeSpan.FromSeconds(5)));
        Assert.Null(backend.LastMeta?.AudioHelperErrorCode);
        Assert.Contains("video_file_not_stable", backend.LastMeta?.StderrLog ?? "");
    }

    [Fact]
    public void AudioHelperErrorCode_SuccessSummary_MuxFailure_IsNull()
    {
        var validAudio = CreateValidAudio();
        var validVideo = CreateValidVideo();
        var audio = new FakeAudioCaptureWorker(raiseAudioReadyOnStart: true, holdFileOpen: true, holdFileOpenCopyFrom: validAudio);
        audio.SetTerminalSummary(new AudioHelperSessionSummary { State = AudioHelperSessionState.Success });
        var video = new FakeVideoCaptureWorker();
        var factory = new FakeAvWorkerFactory { AudioWorker = audio, VideoWorker = video };
        var runner = new FakeExternalProcessRunner(exitCode: 1, stderr: "mux-failed");
        var backend = new AvSplitCaptureBackend(factory, runner, new TempRetentionPolicy(_tempDir))
        {
            ApplyContinuityCheck = false
        };

        backend.Start(CreateConfig());
        backend.StartVideo();
        File.Copy(validVideo, video.OutputPath!, overwrite: true);
        // The audio worker already holds a copy of validAudio open; do not
        // overwrite it while the handle is still locked.
        video.EmitNaturalExit(0, "");

        Assert.True(SpinWait.SpinUntil(() => backend.HasExited, TimeSpan.FromSeconds(5)));
        Assert.Null(backend.LastMeta?.AudioHelperErrorCode);
        Assert.Contains("mux-failed", backend.LastMeta?.StderrLog ?? "");
    }

    private string CreateValidVideo()
    {
        var path = Path.Combine(_tempDir, $"fixture-video-{Guid.NewGuid():N}.mp4");
        RunFfmpeg($"-y -nostats -loglevel error -f lavfi -i testsrc=duration=2:size=320x240:rate=10 -pix_fmt yuv420p -c:v libx264 -t 2 \"{path}\"");
        return path;
    }

    private string CreateValidAudio()
    {
        var path = Path.Combine(_tempDir, $"fixture-audio-{Guid.NewGuid():N}.wav");
        RunFfmpeg($"-y -nostats -loglevel error -f lavfi -i sine=frequency=1000:duration=2 -acodec pcm_s16le -ar 44100 -ac 2 \"{path}\"");
        return path;
    }

    private static void RunFfmpeg(string arguments)
    {
        var psi = new ProcessStartInfo
        {
            FileName = FfmpegLocator.FfmpegPath,
            Arguments = arguments,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        using var proc = Process.Start(psi) ?? throw new InvalidOperationException("ffmpeg failed");
        proc.BeginOutputReadLine();
        if (!proc.WaitForExit(30000))
        {
            try { proc.Kill(true); } catch { }
            throw new InvalidOperationException("ffmpeg generation timed out");
        }
        if (proc.ExitCode != 0)
            throw new InvalidOperationException("ffmpeg generation failed: " + proc.StandardError.ReadToEnd());
    }
}
