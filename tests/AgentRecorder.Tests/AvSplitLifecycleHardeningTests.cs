using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using AgentRecorder.Capture;
using AgentRecorder.Core;
using AgentRecorder.Infrastructure;
using Xunit;

namespace AgentRecorder.Tests;

/// <summary>
/// Task 180D lifecycle hardening tests. Covers convergence timeout/owner
/// completion, natural-owner exception notification, worker exit-state
/// synchronization, deadline capture-ended exactly-once, and media anchor
/// rejection of invalid progress timestamps.
/// </summary>
[Collection("NonParallel-AgentRecorderDataDir")]
public sealed class AvSplitLifecycleHardeningTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string? _originalDataDir;

    public AvSplitLifecycleHardeningTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"avsplit-hardening-{Guid.NewGuid():N}");
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

    private static string CreateValidVideo(string tempDir)
    {
        var path = Path.Combine(tempDir, $"fixture-video-{Guid.NewGuid():N}.mp4");
        RunFfmpeg($"-y -nostats -loglevel error -f lavfi -i testsrc=duration=2:size=320x240:rate=10 -pix_fmt yuv420p -c:v libx264 -t 2 \"{path}\"");
        return path;
    }

    private static string CreateValidAudio(string tempDir)
    {
        var path = Path.Combine(tempDir, $"fixture-audio-{Guid.NewGuid():N}.wav");
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

    private static void SkipIfNoFfmpeg()
    {
        Assert.True(File.Exists(FfmpegLocator.FfmpegPath), "Bundled FFmpeg not available.");
    }

    #region Convergence timeout and owner completion

    [Fact]
    public async Task Converge_SlowOwnerBlockedBeyondShortWaiterThreshold_WaiterReceivesOwnerResult()
    {
        var validAudio = CreateValidAudio(_tempDir);
        var validVideo = CreateValidVideo(_tempDir);
        var audio = new FakeAudioCaptureWorker(raiseAudioReadyOnStart: true);
        var video = new FakeVideoCaptureWorker(stderrLog: "video-stderr");
        var gate = new ManualResetEventSlim(false);
        var runner = new FakeExternalProcessRunner(outputFileToCopy: validVideo, blockBeforeRun: gate);
        var factory = new FakeAvWorkerFactory { AudioWorker = audio, VideoWorker = video };
        var backend = new AvSplitCaptureBackend(factory, runner, new TempRetentionPolicy(_tempDir))
        {
            ApplyContinuityCheck = false,
            // Waiter timeout is short, but owner legitimate work is bounded by this.
            ConvergenceTimeoutOverride = TimeSpan.FromSeconds(2)
        };

        backend.Start(CreateConfig());
        backend.StartVideo();
        File.Copy(validVideo, video.OutputPath!, overwrite: true);
        File.Copy(validAudio, audio.OutputPath!, overwrite: true);

        OutputMeta? naturalMeta = null;
        backend.OnNaturalExit((_, meta) => naturalMeta = meta);

        // Natural exit becomes owner and blocks inside the finalizer runner.
        var naturalTask = Task.Run(() => video.EmitNaturalExit(0, "video-stderr"));
        Assert.True(SpinWait.SpinUntil(() => runner.RunCallCount > 0, TimeSpan.FromSeconds(5)),
            "owner must enter the finalizer");

        // While owner is legitimately blocked, a waiter calls Stop. It must not
        // time out with a placeholder result; it must wait for the owner.
        OutputMeta? manualMeta = null;
        var manualTask = Task.Run(() => { manualMeta = backend.Stop(); });

        await Task.Delay(200);
        Assert.Null(manualMeta); // waiter should still be waiting

        gate.Set();
        await Task.WhenAll(naturalTask, manualTask).WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(1, runner.RunCallCount);
        Assert.NotNull(naturalMeta);
        Assert.NotNull(manualMeta);
        Assert.Same(naturalMeta, manualMeta);
        Assert.DoesNotContain("convergence_owner_timeout", manualMeta!.StderrLog ?? "");
    }

    [Fact]
    public async Task Converge_OwnerTimeout_AllCallersReceiveCanonicalTimeoutResult()
    {
        var validAudio = CreateValidAudio(_tempDir);
        var validVideo = CreateValidVideo(_tempDir);
        var audio = new FakeAudioCaptureWorker(raiseAudioReadyOnStart: true);
        var video = new FakeVideoCaptureWorker(stderrLog: "video-stderr");
        var gate = new ManualResetEventSlim(false);
        var runner = new FakeExternalProcessRunner(outputFileToCopy: validVideo, blockBeforeRun: gate);
        var factory = new FakeAvWorkerFactory { AudioWorker = audio, VideoWorker = video };
        var backend = new AvSplitCaptureBackend(factory, runner, new TempRetentionPolicy(_tempDir))
        {
            ApplyContinuityCheck = false,
            ConvergenceTimeoutOverride = TimeSpan.FromMilliseconds(50)
        };

        backend.Start(CreateConfig());
        backend.StartVideo();
        File.Copy(validVideo, video.OutputPath!, overwrite: true);
        File.Copy(validAudio, audio.OutputPath!, overwrite: true);

        int naturalCallbackCount = 0;
        OutputMeta? naturalMeta = null;
        backend.OnNaturalExit((_, meta) =>
        {
            Interlocked.Increment(ref naturalCallbackCount);
            naturalMeta = meta;
        });

        // Natural exit becomes owner on a background thread; its handler blocks
        // inside ConcludeCapture until the gate is released.
        var naturalTask = Task.Run(() => video.EmitNaturalExit(0, "video-stderr"));
        Assert.True(SpinWait.SpinUntil(() => runner.RunCallCount > 0, TimeSpan.FromSeconds(5)),
            "owner must enter the finalizer");

        // Waiter times out and atomically arbitrates the canonical result via the TCS.
        var manualMeta = backend.Stop();

        Assert.NotNull(manualMeta);
        Assert.Contains("convergence_owner_timeout", manualMeta!.StderrLog ?? "");

        // Release the gate so the owner can complete. It must read the same
        // canonical timeout result and cannot override it with its success candidate.
        gate.Set();
        Assert.True(SpinWait.SpinUntil(() => naturalCallbackCount == 1, TimeSpan.FromSeconds(5)));
        await naturalTask.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(1, naturalCallbackCount);
        Assert.NotNull(naturalMeta);
        Assert.Same(manualMeta, naturalMeta);
        Assert.Contains("convergence_owner_timeout", naturalMeta!.StderrLog ?? "");
        Assert.Equal(1, runner.RunCallCount);
    }

    [Fact]
    public async Task Converge_OwnerCompletesBeforeTimeout_AllCallersReceiveCanonicalSuccess()
    {
        var validAudio = CreateValidAudio(_tempDir);
        var validVideo = CreateValidVideo(_tempDir);
        var audio = new FakeAudioCaptureWorker(raiseAudioReadyOnStart: true);
        var video = new FakeVideoCaptureWorker(stderrLog: "video-stderr");
        var gate = new ManualResetEventSlim(false);
        var runner = new FakeExternalProcessRunner(outputFileToCopy: validVideo, blockBeforeRun: gate);
        var factory = new FakeAvWorkerFactory { AudioWorker = audio, VideoWorker = video };
        var backend = new AvSplitCaptureBackend(factory, runner, new TempRetentionPolicy(_tempDir))
        {
            ApplyContinuityCheck = false,
            ConvergenceTimeoutOverride = TimeSpan.FromSeconds(2)
        };

        backend.Start(CreateConfig());
        backend.StartVideo();
        File.Copy(validVideo, video.OutputPath!, overwrite: true);
        File.Copy(validAudio, audio.OutputPath!, overwrite: true);

        OutputMeta? naturalMeta = null;
        backend.OnNaturalExit((_, meta) => naturalMeta = meta);

        // Natural exit becomes owner and blocks inside the finalizer runner.
        var naturalTask = Task.Run(() => video.EmitNaturalExit(0, "video-stderr"));
        Assert.True(SpinWait.SpinUntil(() => runner.RunCallCount > 0, TimeSpan.FromSeconds(5)),
            "owner must enter the finalizer");

        // Multiple waiters call Stop while owner is legitimately blocked.
        OutputMeta? manualMeta1 = null;
        OutputMeta? manualMeta2 = null;
        var manualTask1 = Task.Run(() => { manualMeta1 = backend.Stop(); });
        var manualTask2 = Task.Run(() => { manualMeta2 = backend.Stop(); });

        // Release the gate so owner completes well before the 2s timeout.
        gate.Set();
        await Task.WhenAll(naturalTask, manualTask1, manualTask2).WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(1, runner.RunCallCount);
        Assert.NotNull(naturalMeta);
        Assert.NotNull(manualMeta1);
        Assert.NotNull(manualMeta2);
        Assert.Same(naturalMeta, manualMeta1);
        Assert.Same(naturalMeta, manualMeta2);
        Assert.DoesNotContain("convergence_owner_timeout", naturalMeta!.StderrLog ?? "");
    }

    #endregion

    #region Natural owner exception notification

    [Fact]
    public async Task NaturalOwner_FinalizeException_BackendCallbackFiresExactlyOnce()
    {
        // This test verifies the backend-level contract: a natural owner that
        // throws during finalization still fires its callback exactly once and
        // shares the same failure metadata with all waiters. It does not
        // instantiate RecordingEngine; the RecordingEngine integration contract
        // is covered by RecordingEngine_NaturalOwnerFinalizeException_EntersFailed.
        var audio = new FakeAudioCaptureWorker(raiseAudioReadyOnStart: true);
        var video = new FakeVideoCaptureWorker(stderrLog: "video-stderr");
        var runner = new ThrowingExternalProcessRunner("mux_explosion");
        var factory = new FakeAvWorkerFactory { AudioWorker = audio, VideoWorker = video };
        var backend = new AvSplitCaptureBackend(factory, runner, new TempRetentionPolicy(_tempDir))
        {
            ApplyContinuityCheck = false
        };

        backend.Start(CreateConfig());
        backend.StartVideo();
        File.Copy(CreateValidVideo(_tempDir), video.OutputPath!, overwrite: true);
        File.Copy(CreateValidAudio(_tempDir), audio.OutputPath!, overwrite: true);

        int naturalCallbackCount = 0;
        OutputMeta? naturalMeta = null;
        backend.OnNaturalExit((_, meta) =>
        {
            Interlocked.Increment(ref naturalCallbackCount);
            naturalMeta = meta;
        });

        OutputMeta? manualMeta = null;
        var manualTask = Task.Run(() => { manualMeta = backend.Stop(); });

        // Natural exit becomes owner; ConcludeCapture throws inside the finalizer.
        video.EmitNaturalExit(0, "video-stderr");

        await manualTask.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(1, naturalCallbackCount);
        Assert.NotNull(naturalMeta);
        Assert.NotNull(manualMeta);
        Assert.Same(naturalMeta, manualMeta);
        Assert.Contains("finalize_exception", naturalMeta!.StderrLog ?? "");
        Assert.Contains("mux_explosion", naturalMeta!.StderrLog ?? "");
    }

    [Fact]
    public void RecordingEngine_NaturalOwnerFinalizeException_EntersFailedTerminalState()
    {
        var validVideo = CreateValidVideo(_tempDir);
        var validAudio = CreateValidAudio(_tempDir);
        var audit = new CaptureAuditLogger();
        var tray = new CountingTray();
        var tracer = new CountingTracer();
        var engine = new RecordingEngine(audit, tracer);
        engine.SetTray(tray);
        engine.CountdownSteps = 0;
        engine.CountdownInterval = TimeSpan.FromMilliseconds(1);

        AvSplitCaptureBackend? backend = null;
        engine.BackendFactory = cfg =>
        {
            var audio = new FakeAudioCaptureWorker(raiseAudioReadyOnStart: true);
            var video = new FakeVideoCaptureWorker(stderrLog: "video-stderr");
            var runner = new ThrowingExternalProcessRunner("mux_explosion");
            var factory = new FakeAvWorkerFactory { AudioWorker = audio, VideoWorker = video };
            backend = new AvSplitCaptureBackend(factory, runner, new TempRetentionPolicy(_tempDir))
            {
                ApplyContinuityCheck = false
            };
            return (backend, "av-split");
        };

        var outputPath = Path.Combine(_tempDir, $"engine-exception-{Guid.NewGuid():N}.mp4");
        var rec = new Recording
        {
            SourceType = "display",
            Microphone = true,
            OutputPath = outputPath,
            Config = new CaptureConfig
            {
                SourceKind = "display",
                Microphone = true,
                MicDevice = "fake-mic",
                Bounds = (0, 0, 320, 240),
                Fps = 30,
                OutputPath = outputPath
            }
        };

        engine.StartCaptureForTests(rec, tray);

        Assert.True(SpinWait.SpinUntil(() => rec.State == RecState.recording, TimeSpan.FromSeconds(5)));
        Assert.NotNull(backend);

        // Place valid temp files so the natural-exit path passes stability checks
        // and reaches the throwing finalizer runner.
        var tempVideoPath = (string?)backend!.GetType()
            .GetField("_tempVideoPath", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)
            ?.GetValue(backend);
        var tempAudioPath = (string?)backend.GetType()
            .GetField("_tempAudioPath", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)
            ?.GetValue(backend);
        File.Copy(validVideo, tempVideoPath!, overwrite: true);
        File.Copy(validAudio, tempAudioPath!, overwrite: true);

        // Trigger natural exit; finalizer throws and the backend callback notifies
        // RecordingEngine with the canonical failure metadata.
        var video = (FakeVideoCaptureWorker?)backend.GetType()
            .GetField("_videoWorker", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)
            ?.GetValue(backend);
        Assert.NotNull(video);
        video!.EmitNaturalExit(0, "video-stderr");

        Assert.True(SpinWait.SpinUntil(() => rec.IsFinalized, TimeSpan.FromSeconds(5)));

        Assert.True(rec.State == RecState.failed || rec.State == RecState.completed,
            $"unexpected terminal state {rec.State}");
        Assert.NotNull(rec.LastMeta);
        Assert.Contains("finalize_exception", rec.LastMeta!.StderrLog ?? "");
        Assert.Contains("mux_explosion", rec.LastMeta!.StderrLog ?? "");
        Assert.Equal(1, tracer.RecordingTerminalCount);
        Assert.Equal(1, audit.Events.Count(e =>
            e.evt == "recording.completed" || e.evt == "recording.failed"));
    }

    [Fact]
    public async Task RecordingEngine_DeadlineRacesSlowNaturalOwner_TimeoutProducesSingleTerminalState()
    {
        var validVideo = CreateValidVideo(_tempDir);
        var validAudio = CreateValidAudio(_tempDir);
        var audit = new CaptureAuditLogger();
        var tray = new CountingTray();
        var tracer = new CountingTracer();
        var engine = new RecordingEngine(audit, tracer);
        engine.SetTray(tray);
        engine.CountdownSteps = 0;
        engine.CountdownInterval = TimeSpan.FromMilliseconds(1);

        var runnerGate = new ManualResetEventSlim(false);
        AvSplitCaptureBackend? backend = null;
        FakeVideoCaptureWorker? video = null;

        engine.BackendFactory = cfg =>
        {
            var audio = new FakeAudioCaptureWorker(raiseAudioReadyOnStart: true);
            video = new FakeVideoCaptureWorker(stderrLog: "video-stderr");
            var runner = new FakeExternalProcessRunner(outputFileToCopy: validVideo, blockBeforeRun: runnerGate);
            var factory = new FakeAvWorkerFactory { AudioWorker = audio, VideoWorker = video };
            backend = new AvSplitCaptureBackend(factory, runner, new TempRetentionPolicy(_tempDir))
            {
                ApplyContinuityCheck = false,
                ConvergenceTimeoutOverride = TimeSpan.FromMilliseconds(50)
            };
            return (backend, "av-split");
        };

        var outputPath = Path.Combine(_tempDir, $"engine-timeout-{Guid.NewGuid():N}.mp4");
        var rec = new Recording
        {
            SourceType = "display",
            DurationSeconds = 1,
            Microphone = true,
            OutputPath = outputPath,
            Config = new CaptureConfig
            {
                SourceKind = "display",
                Microphone = true,
                MicDevice = "fake-mic",
                Bounds = (0, 0, 320, 240),
                Fps = 30,
                OutputPath = outputPath
            }
        };

        engine.StartCaptureForTests(rec, tray);

        // Wait for the recording to reach the recording state so the deadline
        // watchdog is armed.
        Assert.True(SpinWait.SpinUntil(() => rec.State == RecState.recording, TimeSpan.FromSeconds(5)));
        Assert.NotNull(video);

        // Natural exit becomes owner on a background thread and blocks inside
        // the finalizer runner.
        var naturalTask = Task.Run(() => video!.EmitNaturalExit(0, "video-stderr"));
        Assert.True(SpinWait.SpinUntil(() => backend?.LastMeta == null, TimeSpan.FromSeconds(1)));

        // The deadline watchdog fires, becomes a waiter, times out, and
        // arbitrates the canonical timeout result. RecordingEngine finalizes
        // the recording to a single terminal state.
        Assert.True(SpinWait.SpinUntil(() => rec.IsFinalized, TimeSpan.FromSeconds(5)));

        var canonicalMeta = rec.LastMeta;
        Assert.NotNull(canonicalMeta);
        Assert.Contains("convergence_owner_timeout", canonicalMeta!.StderrLog ?? "");

        // Release the gate so the owner completes. It must not override the
        // already-finalized recording.
        runnerGate.Set();
        await naturalTask.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(rec.State == RecState.failed || rec.State == RecState.completed,
            $"unexpected terminal state {rec.State}");
        Assert.Same(canonicalMeta, rec.LastMeta);
        Assert.Equal(1, tracer.RecordingTerminalCount);
        Assert.Equal(1, audit.Events.Count(e =>
            e.evt == "recording.completed" || e.evt == "recording.failed"));
    }

    [Fact]
    public void ManualStop_DoesNotTriggerNaturalCallback()
    {
        var audio = new FakeAudioCaptureWorker(raiseAudioReadyOnStart: true);
        var video = new FakeVideoCaptureWorker();
        var factory = new FakeAvWorkerFactory { AudioWorker = audio, VideoWorker = video };
        var runner = new FakeExternalProcessRunner(outputFileToCopy: CreateValidVideo(_tempDir));
        var backend = new AvSplitCaptureBackend(factory, runner, new TempRetentionPolicy(_tempDir))
        {
            ApplyContinuityCheck = false
        };

        backend.Start(CreateConfig());
        backend.StartVideo();
        File.Copy(CreateValidVideo(_tempDir), video.OutputPath!, overwrite: true);
        File.Copy(CreateValidAudio(_tempDir), audio.OutputPath!, overwrite: true);

        int naturalCallbackCount = 0;
        backend.OnNaturalExit((_, _) => Interlocked.Increment(ref naturalCallbackCount));

        backend.Stop();

        Assert.Equal(0, naturalCallbackCount);
    }

    #endregion

    #region Worker exit-state synchronization

    [Fact]
    public void VideoCaptureWorker_WaitForExitAfterQuickProcess_ExitCodeAndHasExitedSynchronized()
    {
        SkipIfNoFfmpeg();

        using var worker = new VideoCaptureWorker();
        worker.TestArgumentsOverride = new List<string> { "-version" };
        var outputPath = Path.Combine(_tempDir, $"video-{Guid.NewGuid():N}.mp4");

        worker.Start(new CaptureConfig { SourceKind = "display", Fps = 30, Bounds = (0, 0, 320, 240) }, outputPath);

        Assert.True(worker.WaitForExit(TimeSpan.FromSeconds(5)));
        Assert.True(worker.HasExited);
        Assert.Equal(0, worker.ExitCode);
    }

    [Fact]
    public void AudioCaptureWorker_WaitForExitAfterQuickProcess_ExitCodeAndHasExitedSynchronized()
    {
        SkipIfNoFfmpeg();

        using var worker = new AudioCaptureWorker();
        worker.TestArgumentsOverride = new List<string> { "-version" };
        var outputPath = Path.Combine(_tempDir, $"audio-{Guid.NewGuid():N}.wav");

        worker.Start(new CaptureConfig { SourceKind = "display", Fps = 30, Bounds = (0, 0, 320, 240), MicDevice = "fake" }, outputPath);

        Assert.True(worker.WaitForExit(TimeSpan.FromSeconds(5)));
        Assert.True(worker.HasExited);
        Assert.Equal(0, worker.ExitCode);
    }

    [Fact]
    public void VideoCaptureWorker_StopAfterProcessAlreadyExited_DrainCompletesBeforeReturn()
    {
        SkipIfNoFfmpeg();

        using var worker = new VideoCaptureWorker();
        worker.TestArgumentsOverride = new List<string> { "-version" };
        var outputPath = Path.Combine(_tempDir, $"video-{Guid.NewGuid():N}.mp4");

        worker.Start(new CaptureConfig { SourceKind = "display", Fps = 30, Bounds = (0, 0, 320, 240) }, outputPath);

        // Give the short-lived FFmpeg -version process time to exit before Stop(),
        // so Stop() exercises the already-exited path and must still wait for the
        // watcher to publish ExitCode/HasExited/drain.
        Thread.Sleep(100);

        var meta = worker.Stop();

        Assert.True(worker.HasExited);
        Assert.Equal(0, worker.ExitCode);
        Assert.NotNull(meta);
    }

    [Fact]
    public void AudioCaptureWorker_StopAfterProcessAlreadyExited_DrainCompletesBeforeReturn()
    {
        SkipIfNoFfmpeg();

        using var worker = new AudioCaptureWorker();
        worker.TestArgumentsOverride = new List<string> { "-version" };
        var outputPath = Path.Combine(_tempDir, $"audio-{Guid.NewGuid():N}.wav");

        worker.Start(new CaptureConfig { SourceKind = "display", Fps = 30, Bounds = (0, 0, 320, 240), MicDevice = "fake" }, outputPath);
        worker.Stop();

        Assert.True(worker.HasExited);
        Assert.Equal(0, worker.ExitCode);
    }

    #endregion

    #region Media anchor backfill

    [Fact]
    public void VideoCaptureWorker_FirstFrameZeroOutTime_BackfillsAnchorFromFirstPositiveProgress()
    {
        using var worker = new VideoCaptureWorker();
        var firstFrameCount = 0;
        worker.FirstFrameObserved += _ => Interlocked.Increment(ref firstFrameCount);

        worker.HandleProgressGroup(CreateProgressGroup(frame: 17, totalSize: 48, outTimeUs: 0));

        Assert.Equal(1, firstFrameCount);
        Assert.Equal(0, worker.FirstFrameAnchorTicks);

        worker.HandleProgressGroup(CreateProgressGroup(frame: 18, totalSize: 2048, outTimeUs: 200_000));

        var backfilledAnchor = worker.FirstFrameAnchorTicks;
        Assert.Equal(1, firstFrameCount);
        Assert.True(backfilledAnchor > 0);

        worker.HandleProgressGroup(CreateProgressGroup(frame: 19, totalSize: 4096, outTimeUs: 900_000));

        Assert.Equal(1, firstFrameCount);
        Assert.Equal(backfilledAnchor, worker.FirstFrameAnchorTicks);
    }

    [Fact]
    public void AvSplitFinalization_UsesLaunchVideoAnchorAfterFirstFrameCallback()
    {
        var validAudio = CreateValidAudio(_tempDir);
        var validVideo = CreateValidVideo(_tempDir);
        var audio = new FakeAudioCaptureWorker(raiseAudioReadyOnStart: true);
        var video = new FakeVideoCaptureWorker();
        var runner = new FakeExternalProcessRunner(outputFileToCopy: validVideo);
        var factory = new FakeAvWorkerFactory { AudioWorker = audio, VideoWorker = video };
        var backend = new AvSplitCaptureBackend(factory, runner, new TempRetentionPolicy(_tempDir))
        {
            ApplyContinuityCheck = false
        };

        backend.Start(CreateConfig());
        backend.StartVideo();
        File.Copy(validVideo, video.OutputPath!, overwrite: true);
        File.Copy(validAudio, audio.OutputPath!, overwrite: true);

        video.SetLaunchAnchorTicks(audio.MediaStartAnchorTicks + Stopwatch.Frequency / 10);
        video.SetFirstFrameAnchorTicks(audio.MediaStartAnchorTicks + Stopwatch.Frequency / 20);
        var meta = backend.Stop();

        Assert.Equal(1, runner.RunCallCount);
        Assert.Equal("available", meta.VideoAnchorStatus);
        Assert.Equal(video.LaunchAnchorTicks, meta.VideoLaunchAnchorTicks);
        Assert.Equal(video.FirstFrameAnchorTicks, meta.VideoProgressAnchorTicks);
        Assert.Equal("available", meta.AudioAnchorStatus);
        Assert.NotNull(meta.AudioPreRollMs);
        Assert.InRange(meta.AudioPreRollMs!.Value, 99.0, 101.0);
        Assert.NotInRange(meta.AudioPreRollMs.Value, 49.0, 51.0);
    }

    private static FFmpegProgressGroup CreateProgressGroup(long frame, long totalSize, long? outTimeUs)
    {
        var values = new Dictionary<string, string>
        {
            ["frame"] = frame.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["total_size"] = totalSize.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["progress"] = "continue"
        };
        if (outTimeUs.HasValue)
            values["out_time_us"] = outTimeUs.Value.ToString(System.Globalization.CultureInfo.InvariantCulture);
        return new FFmpegProgressGroup(values);
    }

    #endregion

    #region Deadline capture-ended exactly once

    [Fact]
    public void DeadlineWatchdog_BackendEventRacesDeadline_CaptureEndedRecordedExactlyOnce()
    {
        var audit = new CaptureAuditLogger();
        var tray = new CountingTray();
        var tracer = new CountingTracer();
        var engine = new RecordingEngine(audit, tracer);
        engine.SetTray(tray);

        var backend = new ObservableFakeBackend
        {
            // Backend fires CaptureEnded before the deadline watchdog calls Stop().
            FireCaptureEndedBeforeStop = true,
            StopDelayMs = 200,
            StopResult = new OutputMeta { DurationSeconds = 1.0, SizeBytes = 1024 }
        };
        engine.BackendFactory = _ => (backend, "fake-observable");

        var outputPath = Path.Combine(_tempDir, $"deadline-{Guid.NewGuid():N}.mp4");
        var rec = new Recording
        {
            SourceType = "display",
            DurationSeconds = 1,
            OutputPath = outputPath,
            Config = new CaptureConfig
            {
                SourceKind = "display",
                Bounds = (0, 0, 320, 240),
                Fps = 30,
                OutputPath = outputPath
            }
        };

        engine.StartCaptureForTests(rec, tray);

        // Wait for the terminal completed state (not just IsFinalized) so the
        // assertion does not observe an intermediate value while FinalizeRecording
        // still holds the recording lock.
        Assert.True(SpinWait.SpinUntil(() => rec.State == RecState.completed, TimeSpan.FromSeconds(5)));

        Assert.Equal(1, tracer.CaptureEndedCount);
        Assert.Equal(1, audit.Events.Count(e => e.evt == "recording.capture_ended"));
        Assert.Equal(1, tray.FinalizingCount);
        Assert.Equal(1, audit.Events.Count(e => e.evt == "recording.completed"));
        Assert.Equal(0, audit.Events.Count(e => e.evt == "recording.failed"));
    }

    [Fact]
    public void DeadlineWatchdog_DeadlineWinsBeforeBackendEvent_CaptureEndedRecordedExactlyOnce()
    {
        var audit = new CaptureAuditLogger();
        var tray = new CountingTray();
        var tracer = new CountingTracer();
        var engine = new RecordingEngine(audit, tracer);
        engine.SetTray(tray);

        var backend = new ObservableFakeBackend
        {
            // Deadline calls Stop() first; Stop() emits CaptureEnded while blocking.
            FireCaptureEndedDuringStop = true,
            StopDelayMs = 200,
            StopResult = new OutputMeta { DurationSeconds = 1.0, SizeBytes = 1024 }
        };
        engine.BackendFactory = _ => (backend, "fake-observable");

        var outputPath = Path.Combine(_tempDir, $"deadline-{Guid.NewGuid():N}.mp4");
        var rec = new Recording
        {
            SourceType = "display",
            DurationSeconds = 1,
            OutputPath = outputPath,
            Config = new CaptureConfig
            {
                SourceKind = "display",
                Bounds = (0, 0, 320, 240),
                Fps = 30,
                OutputPath = outputPath
            }
        };

        engine.StartCaptureForTests(rec, tray);

        // Wait for the terminal completed state to avoid reading IsFinalized
        // while FinalizeRecording still holds the recording lock.
        Assert.True(SpinWait.SpinUntil(() => rec.State == RecState.completed, TimeSpan.FromSeconds(5)));

        Assert.Equal(1, tracer.CaptureEndedCount);
        Assert.Equal(1, audit.Events.Count(e => e.evt == "recording.capture_ended"));
        Assert.Equal(1, tray.FinalizingCount);
        Assert.Equal(1, audit.Events.Count(e => e.evt == "recording.completed"));
    }

    #endregion

    #region Supporting fakes

    /// <summary>
    /// External process runner that always throws from RunAsync, used to force
    /// ConcludeCapture into its exception handling path.
    /// </summary>
    private sealed class ThrowingExternalProcessRunner : IExternalProcessRunner
    {
        private readonly string _message;

        public ThrowingExternalProcessRunner(string message) => _message = message;

        public Task<ExternalProcessResult> RunAsync(
            string fileName,
            IReadOnlyList<string> argumentList,
            TimeSpan timeout,
            bool captureStderr = true,
            Encoding? stderrEncoding = null,
            CancellationToken cancellationToken = default)
        {
            throw new InvalidOperationException(_message);
        }
    }

    /// <summary>
    /// Backend that can synchronously raise a first-frame observation and a
    /// capture-ended observation on demand, used to race the deadline watchdog
    /// against the backend event path.
    /// </summary>
    private sealed class ObservableFakeBackend : ICaptureBackend, ICaptureEndedObservableBackend, IFirstFrameObservableCaptureBackend
    {
        public event Action<CaptureEndedObservation>? CaptureEnded;
        public event Action<FirstFrameObservation>? FirstFrameObserved;

        public OutputMeta StopResult { get; set; } = new();
        public bool FireCaptureEndedBeforeStop { get; set; }
        public bool FireCaptureEndedDuringStop { get; set; }
        public int StopDelayMs { get; set; }
        public bool StopCalled { get; private set; }

        private Action<int, OutputMeta>? _onNaturalExit;

        public void Start(CaptureConfig cfg)
        {
            // Synchronously transition engine to recording so the deadline watchdog starts.
            FirstFrameObserved?.Invoke(new FirstFrameObservation
            {
                EvidenceKind = "fake",
                FrameNumber = 1,
                TotalSizeBytes = 1024,
                OutTimeUs = 1_000_000
            });

            if (FireCaptureEndedBeforeStop)
            {
                CaptureEnded?.Invoke(new CaptureEndedObservation
                {
                    EndedAtUtc = DateTime.UtcNow,
                    ExitCode = 0,
                    Reason = "natural"
                });
            }
        }

        public OutputMeta Stop()
        {
            StopCalled = true;
            if (FireCaptureEndedDuringStop)
            {
                Thread.Sleep(20);
                CaptureEnded?.Invoke(new CaptureEndedObservation
                {
                    EndedAtUtc = DateTime.UtcNow,
                    ExitCode = 0,
                    Reason = "natural"
                });
            }
            if (StopDelayMs > 0)
                Thread.Sleep(StopDelayMs);
            return StopResult;
        }

        public void OnNaturalExit(Action<int, OutputMeta> callback) => _onNaturalExit = callback;

        public int ExitCode => 0;

        public void Dispose() { }
    }

    private sealed class CountingTray : ITrayContext
    {
        public string HostMode => "headless";
        public bool SupportsRegionSelectionUi => false;

        public int FinalizingCount => _finalizingCount;
        public int RecordingCount => _recordingCount;
        public int IdleCount => _idleCount;

        public void RequestConfirmation(RecordingConfirmationPresentation presentation, Action<ConfirmationDecision> callback) { }
        public void RequestRegionSelection(int timeoutSeconds,
            Action<string, int, int, int, int, string, string> callback) { }
        public void SetRecording(RecordingUiPresentation rec) => Interlocked.Increment(ref _recordingCount);
        public void SetIdle(RecordingUiPresentation rec) => Interlocked.Increment(ref _idleCount);
        public void SetAllIdle() { }
        public void ShowError(string text) { }
        public void SetPreparing(RecordingUiPresentation rec) { }
        public void SetFinalizing(RecordingUiPresentation rec) => Interlocked.Increment(ref _finalizingCount);

        private int _finalizingCount;
        private int _recordingCount;
        private int _idleCount;
    }

    private sealed class CountingTracer : IPerformanceTracer
    {
        public int CaptureEndedCount => _captureEndedCount;
        public int RecordingTerminalCount => _recordingTerminalCount;
        private int _captureEndedCount;
        private int _recordingTerminalCount;

        public void CaptureEnded(string traceId, string recordingId) => Interlocked.Increment(ref _captureEndedCount);

        public void RecordingTerminal(string traceId, string recordingId, string status, string? stopReason = null, string? errorCode = null)
            => Interlocked.Increment(ref _recordingTerminalCount);

        public void IntentAccepted(string traceId, string endpoint, string? clientSentAtUtc = null) { }
        public void SetEnsureContextAssociation(string traceId, EnsureContextAssociation association) { }
        public void IntentValidated(string traceId, string endpoint, bool success, string? errorCode = null) { }
        public void CorrelationSet(string traceId, string recordingId, string? confirmationId = null, string? sourceType = null) { }
        public bool HasValidationResult(string traceId) => false;
        public void ConfirmationCreated(string traceId, string recordingId, string confirmationId) { }
        public void ConfirmationShown(string traceId, string recordingId, string confirmationId) { }
        public void ConfirmationApproved(string traceId, string recordingId, string confirmationId) { }
        public void ConfirmationRejected(string traceId, string recordingId, string confirmationId) { }
        public void ConfirmationExpired(string traceId, string recordingId, string confirmationId) { }
        public void CaptureStartRequested(string traceId, string recordingId, string backendType) { }
        public void CaptureBackendStartReturned(string traceId, string recordingId, string backendType) { }
        public void CaptureBackendStartFailed(string traceId, string recordingId, string backendType, string errorCode, string errorType) { }
        public void MicrophonePrepareStarted(string traceId, string recordingId) { }
        public void MicrophoneReady(string traceId, string recordingId) { }
        public void CountdownStarted(string traceId, string recordingId) { }
        public void CaptureFirstFrameObserved(string traceId, string recordingId, FirstFrameEvidence evidence) { }
        public void FinalizationCompleted(string traceId, string recordingId, bool success) { }
        public void LongPollCompleted(string traceId, string kind, int requestedWaitMs, int actualWaitMs, bool changed, string? recordingId = null, string? confirmationId = null) { }
        public void Flush() { }
        public string? ResolveTraceId(string? recordingId = null, string? confirmationId = null) => null;
    }

    #endregion
}
