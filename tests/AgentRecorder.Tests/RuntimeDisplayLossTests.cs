using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using AgentRecorder.Capture;
using AgentRecorder.Core;
using AgentRecorder.Infrastructure;
using AgentRecorder.Logging;
using AgentRecorder.Windows;
using Xunit;

namespace AgentRecorder.Tests;

/// <summary>
/// Production-chain tests for runtime display supervision. The engine, the
/// real AV split backend, and the real plan/identity types are used; only the
/// display topology provider, capture workers, and mux runner are injected.
/// </summary>
[Collection("NonParallel-AgentRecorderDataDir")]
public sealed class RuntimeDisplayLossTests : IDisposable
{
    private readonly string _dataDir;
    private readonly string? _originalDataDir;

    public RuntimeDisplayLossTests()
    {
        _dataDir = Path.Combine(Path.GetTempPath(), $"runtime-display-loss-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_dataDir);
        _originalDataDir = Environment.GetEnvironmentVariable("AGENT_RECORDER_DATA_DIR");
        Environment.SetEnvironmentVariable("AGENT_RECORDER_DATA_DIR", _dataDir);
        DataDirResolver.SetOverride(_dataDir);
    }

    public void Dispose()
    {
        DataDirResolver.ClearOverride();
        if (_originalDataDir == null)
            Environment.SetEnvironmentVariable("AGENT_RECORDER_DATA_DIR", null);
        else
            Environment.SetEnvironmentVariable("AGENT_RECORDER_DATA_DIR", _originalDataDir);
        try { if (Directory.Exists(_dataDir)) Directory.Delete(_dataDir, recursive: true); }
        catch { }
    }

    [Theory]
    [InlineData("display", "ffmpeg")]
    [InlineData("region", "ffmpeg-region")]
    public void FfmpegDisplayOrRegionLoss_AbortsAsTrustedFailure_AndDeletesPartialOutput(
        string sourceKind, string backendType)
    {
        var provider = new SequenceDisplayTopologyProvider();
        provider.SetSequence(
            new[] { TargetDisplay() },
            Array.Empty<DisplayTopologySnapshot>());

        var backend = new RuntimeFfmpegBackend();
        var tray = new RecordingTestTray();
        var audit = new CapturingAuditLogger();
        using var engine = CreateEngine(provider, audit, tray, backend, backendType, sourceKind);
        var rec = CreateRecording(sourceKind, backendType, backend.OutputPath);

        engine.StartCaptureForTests(rec, tray, "trace_runtime_display_loss");
        WaitUntil(() => rec.State == RecState.recording);
        WaitUntil(() => rec.State == RecState.failed);
        WaitUntil(() => engine.ActiveDisplayRuntimeMonitorCountForTests == 0);

        Assert.Equal("display_unavailable", rec.StopReason);
        Assert.Equal("display_unavailable", rec.Error);
        Assert.Equal(1, backend.AbortCalls);
        Assert.Equal(0, backend.StopCalls);
        Assert.False(File.Exists(rec.OutputPath));
        Assert.Equal("not_applicable", rec.BundleSnapshot.Status);
        Assert.Single(tray.FailureReasons);
        Assert.Equal("display_unavailable", tray.FailureReasons[0]);
        Assert.Empty(tray.Errors);
        Assert.Single(audit.Events, evt => evt == "recording.capture_ended");
        Assert.Single(audit.Events, evt => evt == "recording.failed");
    }

    [Theory]
    [InlineData("display", "ffmpeg-av-split")]
    [InlineData("region", "ffmpeg-region-av-split")]
    public void AvSplitDisplayOrRegionLoss_StopsWorkersOnce_SkipsMux_AndPublishesNoFinalMedia(
        string sourceKind, string backendType)
    {
        var provider = new SequenceDisplayTopologyProvider();
        provider.SetSequence(
            new[] { TargetDisplay() },
            Array.Empty<DisplayTopologySnapshot>());

        var audioFixture = Path.Combine(_dataDir, "audio-fixture.wav");
        File.WriteAllBytes(audioFixture, Enumerable.Repeat((byte)0x5A, 1024).ToArray());
        var audio = new FakeAudioCaptureWorker(
            raiseAudioReadyOnStart: true,
            holdFileOpen: true,
            holdFileOpenCopyFrom: audioFixture);
        var video = new FakeVideoCaptureWorker(firstFrameDelayMs: 15);
        var factory = new FakeAvWorkerFactory { AudioWorker = audio, VideoWorker = video };
        var runner = new FakeExternalProcessRunner();
        var backend = new AvSplitCaptureBackend(factory, runner, new TempRetentionPolicy(_dataDir))
        {
            ApplyContinuityCheck = false
        };
        var tray = new RecordingTestTray();
        var audit = new CapturingAuditLogger();
        using var engine = CreateEngine(provider, audit, tray, backend, backendType, sourceKind, microphone: true);
        var rec = CreateRecording(sourceKind, backendType,
            Path.Combine(_dataDir, $"final-{Guid.NewGuid():N}.mp4"), microphone: true);

        engine.StartCaptureForTests(rec, tray, "trace_runtime_av_display_loss");
        WaitUntil(() => rec.State == RecState.recording);
        WaitUntil(() => backend.TempVideoPath != null);
        File.WriteAllBytes(backend.TempVideoPath!, Enumerable.Repeat((byte)0x33, 2048).ToArray());

        WaitUntil(() => rec.State == RecState.failed);
        WaitUntil(() => engine.ActiveDisplayRuntimeMonitorCountForTests == 0);

        Assert.Equal("display_unavailable", rec.StopReason);
        Assert.Equal("display_unavailable", rec.Error);
        Assert.Equal(1, video.StopCalled ? 1 : 0);
        Assert.True(audio.StopCalled);
        Assert.Equal(0, runner.RunCallCount);
        Assert.False(File.Exists(rec.OutputPath));
        Assert.Equal("not_applicable", rec.BundleSnapshot.Status);
        Assert.Single(tray.FailureReasons);
        Assert.Equal("display_unavailable", tray.FailureReasons[0]);
        Assert.Single(audit.Events, evt => evt == "recording.capture_ended");
        Assert.Single(audit.Events, evt => evt == "recording.failed");
    }

    [Fact]
    public void NonTargetDisplayLoss_DoesNotAbortTargetCapture()
    {
        var target = TargetDisplay();
        var other = new DisplayTopologySnapshot(
            "display_2", "stable-other", DisplayIdentityResolutionStatus.Resolved,
            new CapturePlanBounds(1920, 0, 1920, 1080));
        var provider = new SequenceDisplayTopologyProvider();
        provider.SetSequence(new[] { target, other }, new[] { target });

        var backend = new RuntimeFfmpegBackend();
        var tray = new RecordingTestTray();
        using var engine = CreateEngine(provider, new CapturingAuditLogger(), tray, backend, "ffmpeg", "display");
        var rec = CreateRecording("display", "ffmpeg", backend.OutputPath);

        engine.StartCaptureForTests(rec, tray);
        WaitUntil(() => rec.State == RecState.recording);
        WaitUntil(() => provider.CallCount >= 2);

        Assert.Equal(RecState.recording, rec.State);
        Assert.Equal(0, backend.AbortCalls);
        Assert.Equal(1, engine.ActiveDisplayRuntimeMonitorCountForTests);
        engine.Stop(rec.Id, "test_stop");
        WaitUntil(() => engine.ActiveDisplayRuntimeMonitorCountForTests == 0);
        Assert.Equal(RecState.completed, rec.State);
        Assert.Empty(tray.FailureReasons);
    }

    [Fact]
    public void StableIdentitySurvivesPublicIdAndBoundsChange()
    {
        var provider = new SequenceDisplayTopologyProvider();
        provider.SetSequence(
            TargetDisplay(),
            new DisplayTopologySnapshot(
                "display_9", TargetIdentity, DisplayIdentityResolutionStatus.Resolved,
                new CapturePlanBounds(-2560, 100, 2560, 1440)));

        var backend = new RuntimeFfmpegBackend();
        var tray = new RecordingTestTray();
        using var engine = CreateEngine(provider, new CapturingAuditLogger(), tray, backend, "ffmpeg", "display");
        var rec = CreateRecording("display", "ffmpeg", backend.OutputPath);

        engine.StartCaptureForTests(rec, tray);
        WaitUntil(() => rec.State == RecState.recording);
        WaitUntil(() => provider.CallCount >= 2);

        Assert.Equal(RecState.recording, rec.State);
        Assert.Equal(0, backend.AbortCalls);
        engine.Stop(rec.Id, "test_stop");
        WaitUntil(() => engine.ActiveDisplayRuntimeMonitorCountForTests == 0);
        Assert.Equal(RecState.completed, rec.State);
    }

    [Fact]
    public void StableIdentityChange_FailsClosedEvenWhenPublicIdAndBoundsLookUnchanged()
    {
        var provider = new SequenceDisplayTopologyProvider();
        provider.SetSequence(
            TargetDisplay(),
            new DisplayTopologySnapshot(
                "display_1", "stable-replaced-panel", DisplayIdentityResolutionStatus.Resolved,
                new CapturePlanBounds(0, 0, 1920, 1080)));

        var backend = new RuntimeFfmpegBackend();
        var tray = new RecordingTestTray();
        using var engine = CreateEngine(provider, new CapturingAuditLogger(), tray, backend, "ffmpeg", "display");
        var rec = CreateRecording("display", "ffmpeg", backend.OutputPath);

        engine.StartCaptureForTests(rec, tray);
        WaitUntil(() => rec.State == RecState.failed);
        WaitUntil(() => engine.ActiveDisplayRuntimeMonitorCountForTests == 0);

        Assert.Equal("display_unavailable", rec.StopReason);
        Assert.Equal("display_unavailable", rec.Error);
        Assert.Equal(1, backend.AbortCalls);
        Assert.Single(tray.FailureReasons);
    }

    [Fact]
    public void TopologyProviderFailure_FailsClosedAsDisplayUnavailable()
    {
        var provider = new SequenceDisplayTopologyProvider
        {
            ThrowAfterFirstCall = true
        };
        provider.SetSequence(TargetDisplay());

        var backend = new RuntimeFfmpegBackend();
        var tray = new RecordingTestTray();
        using var engine = CreateEngine(provider, new CapturingAuditLogger(), tray, backend, "ffmpeg", "display");
        var rec = CreateRecording("display", "ffmpeg", backend.OutputPath);

        engine.StartCaptureForTests(rec, tray);
        WaitUntil(() => rec.State == RecState.failed);
        WaitUntil(() => engine.ActiveDisplayRuntimeMonitorCountForTests == 0);

        Assert.Equal("display_unavailable", rec.Error);
        Assert.Equal(1, backend.AbortCalls);
        Assert.Single(tray.FailureReasons);
    }

    [Theory]
    [InlineData(DisplayIdentityResolutionStatus.Unavailable)]
    [InlineData(DisplayIdentityResolutionStatus.Ambiguous)]
    [InlineData(DisplayIdentityResolutionStatus.Conflict)]
    public void InvalidTargetIdentityStatus_FailsClosedWithoutUsingPublicMetadata(
        DisplayIdentityResolutionStatus invalidStatus)
    {
        var ambiguous = new DisplayTopologySnapshot(
            "display_1", TargetIdentity, invalidStatus,
            new CapturePlanBounds(0, 0, 1920, 1080));
        var provider = new SequenceDisplayTopologyProvider();
        provider.SetSequence(TargetDisplay(), ambiguous);

        var backend = new RuntimeFfmpegBackend();
        var tray = new RecordingTestTray();
        using var engine = CreateEngine(provider, new CapturingAuditLogger(), tray, backend, "ffmpeg", "display");
        var rec = CreateRecording("display", "ffmpeg", backend.OutputPath);

        engine.StartCaptureForTests(rec, tray);
        WaitUntil(() => rec.State == RecState.failed);
        WaitUntil(() => engine.ActiveDisplayRuntimeMonitorCountForTests == 0);

        Assert.Equal("display_unavailable", rec.StopReason);
        Assert.Equal(1, backend.AbortCalls);
        Assert.Single(tray.FailureReasons);
    }

    [Fact]
    public void NormalNaturalCompletion_RetiresMonitorWithoutFailureNotification()
    {
        var provider = new SequenceDisplayTopologyProvider();
        provider.SetSequence(TargetDisplay(), TargetDisplay(), TargetDisplay());
        var backend = new RuntimeFfmpegBackend(naturalExitDelayMs: 100);
        var tray = new RecordingTestTray();
        var audit = new CapturingAuditLogger();
        using var engine = CreateEngine(provider, audit, tray, backend, "ffmpeg", "display");
        var rec = CreateRecording("display", "ffmpeg", backend.OutputPath);

        engine.StartCaptureForTests(rec, tray);
        WaitUntil(() => rec.State == RecState.completed);
        WaitUntil(() => engine.ActiveDisplayRuntimeMonitorCountForTests == 0);

        Assert.Equal("duration_reached", rec.StopReason);
        Assert.Equal(0, backend.AbortCalls);
        Assert.Empty(tray.FailureReasons);
        Assert.Single(audit.Events, evt => evt == "recording.completed");
    }

    [Fact]
    public void DisplayLossAfterTerminalCompletion_DoesNotRewriteReasonOrNotify()
    {
        var provider = new SequenceDisplayTopologyProvider();
        provider.SetSequence(TargetDisplay());
        var backend = new RuntimeFfmpegBackend(naturalExitDelayMs: 60);
        var tray = new RecordingTestTray();
        using var engine = CreateEngine(provider, new CapturingAuditLogger(), tray, backend, "ffmpeg", "display");
        var rec = CreateRecording("display", "ffmpeg", backend.OutputPath);

        engine.StartCaptureForTests(rec, tray);
        WaitUntil(() => rec.State == RecState.completed);
        var completedReason = rec.StopReason;
        provider.SetSequence(Array.Empty<DisplayTopologySnapshot>());

        Assert.Equal(RecState.completed, rec.State);
        Assert.Equal(completedReason, rec.StopReason);
        Assert.Equal(0, backend.AbortCalls);
        Assert.Empty(tray.FailureReasons);
    }

    [Fact]
    public async Task UserStopAndDisplayLossRace_RepeatsTenTimesWithOneTerminalOwner()
    {
        for (int iteration = 0; iteration < 10; iteration++)
        {
            var provider = new SequenceDisplayTopologyProvider();
            provider.SetSequence(new[] { TargetDisplay() }, Array.Empty<DisplayTopologySnapshot>());
            var backend = new RuntimeFfmpegBackend();
            var tray = new RecordingTestTray();
            var audit = new CapturingAuditLogger();
            using var engine = CreateEngine(provider, audit, tray, backend, "ffmpeg", "display");
            var rec = CreateRecording("display", "ffmpeg", backend.OutputPath);

            engine.StartCaptureForTests(rec, tray);
            provider.SetSequence(TargetDisplay());
            WaitUntil(() => rec.State == RecState.recording);
            provider.SetSequence(Array.Empty<DisplayTopologySnapshot>());
            var stopTask = Task.Run(() => engine.Stop(rec.Id, "user_race"));
            WaitUntil(() => rec.IsFinalized || rec.State == RecState.stopping);
            await stopTask.WaitAsync(TimeSpan.FromSeconds(5));
            WaitUntil(() => engine.ActiveDisplayRuntimeMonitorCountForTests == 0);

            Assert.True(rec.IsFinalized);
            Assert.Contains(rec.State, new[] { RecState.completed, RecState.failed });
            Assert.Equal(1, backend.AbortCalls + backend.StopCalls);
            Assert.InRange(tray.FailureReasons.Count, 0, 1);
            Assert.Single(audit.Events, evt => evt == "recording.failed" || evt == "recording.completed");
        }
    }

    private RecordingEngine CreateEngine(
        IDisplayTopologyProvider provider,
        CapturingAuditLogger audit,
        RecordingTestTray tray,
        ICaptureBackend backend,
        string backendType,
        string sourceKind,
        bool microphone = false)
    {
        var engine = new RecordingEngine(audit, displayTopologyProvider: provider)
        {
            CountdownSteps = 0,
            CountdownInterval = TimeSpan.FromMilliseconds(1),
            DisplayRuntimeMonitorInterval = TimeSpan.FromMilliseconds(5),
            BackendFactory = _ => (backend, backendType)
        };
        engine.SetTray(tray);
        return engine;
    }

    private Recording CreateRecording(string sourceKind, string backendType, string outputPath, bool microphone = false)
    {
        var cfg = new CaptureConfig
        {
            SourceKind = sourceKind,
            Bounds = (0, 0, 320, 240),
            DisplayId = "display_1",
            DisplayStableIdentity = TargetIdentity,
            DisplayIdentityStatus = DisplayIdentityResolutionStatus.Resolved,
            DisplayBounds = (0, 0, 1920, 1080),
            Microphone = microphone,
            MicDevice = microphone ? "fake-mic" : null,
            AudioSourceKind = microphone ? AudioCaptureSourceKind.Microphone : AudioCaptureSourceKind.None,
            Fps = 30,
            OutputPath = outputPath,
            CountdownSeconds = 0
        };

        return new Recording
        {
            SourceType = sourceKind,
            OutputPath = outputPath,
            Config = cfg,
            DurationSeconds = 0,
            Microphone = microphone,
            AudioSourceKind = cfg.AudioSourceKind,
            ApprovedCapturePlan = new CapturePlan(
                backendType,
                backendType,
                new CaptureBackendSelectionEvidence(
                    backendType, backendType, "test_approved_plan", "test", null, false),
                sourceKind == "display" ? "display_surface" : "region_rectangle",
                sourceKind,
                null,
                nint.Zero,
                new CapturePlanBounds(0, 0, 320, 240),
                TargetIdentity,
                new CapturePlanBounds(0, 0, 1920, 1080),
                "display_1",
                DisplayIdentityResolutionStatus.Resolved,
                cfg.AudioSourceKind,
                audioEndpointId: null,
                audioEndpointName: null,
                audioEndpointIsDefault: null)
        };
    }

    private static DisplayTopologySnapshot TargetDisplay() => new(
        "display_1", TargetIdentity, DisplayIdentityResolutionStatus.Resolved,
        new CapturePlanBounds(0, 0, 1920, 1080));

    private const string TargetIdentity = "display-stable-test-runtime";

    private static void WaitUntil(Func<bool> condition)
    {
        Assert.True(SpinWait.SpinUntil(condition, TimeSpan.FromSeconds(8)),
            "Timed out waiting for the runtime display lifecycle condition.");
    }

    private sealed class CapturingAuditLogger : AuditLogger
    {
        private readonly ConcurrentQueue<string> _events = new();
        public IReadOnlyList<string> Events => _events.ToArray();

        public override void Log(string evt, object payload) => _events.Enqueue(evt);
    }

    private sealed class RecordingTestTray : ITrayContext, IRecordingFailureNotifier
    {
        private readonly object _lock = new();
        public List<string> FailureReasons { get; } = new();
        public List<string> Errors { get; } = new();

        public string HostMode => "tray";
        public bool SupportsRegionSelectionUi => true;

        public void RequestConfirmation(RecordingConfirmationPresentation presentation, Action<ConfirmationDecision> callback) { }
        public void RequestRegionSelection(int timeoutSeconds, Action<string, int, int, int, int, string, string> callback) { }
        public void SetRecording(RecordingUiPresentation presentation) { }
        public void SetIdle(RecordingUiPresentation presentation) { }
        public void SetAllIdle() { }
        public void ShowError(string text)
        {
            lock (_lock) Errors.Add(text);
        }
        public void ShowRecordingFailure(string recordingId, string reasonCode)
        {
            lock (_lock) FailureReasons.Add(reasonCode);
        }
    }

    private sealed class SequenceDisplayTopologyProvider : IDisplayTopologyProvider
    {
        private readonly object _lock = new();
        private readonly List<IReadOnlyList<DisplayTopologySnapshot>> _sequence = new();
        private int _calls;

        public bool ThrowAfterFirstCall { get; set; }
        public int CallCount => Volatile.Read(ref _calls);

        public void SetSequence(params IReadOnlyList<DisplayTopologySnapshot>[] snapshots)
        {
            lock (_lock)
            {
                _sequence.Clear();
                _sequence.AddRange(snapshots);
                _calls = 0;
            }
        }

        public void SetSequence(params DisplayTopologySnapshot[] snapshots)
            => SetSequence(snapshots.Select(snapshot => (IReadOnlyList<DisplayTopologySnapshot>)new[] { snapshot }).ToArray());

        public IReadOnlyList<DisplayTopologySnapshot> GetCurrentDisplays()
        {
            int index = Interlocked.Increment(ref _calls) - 1;
            if (ThrowAfterFirstCall && index > 0)
                throw new InvalidOperationException("topology provider failed");

            lock (_lock)
            {
                if (_sequence.Count == 0)
                    return Array.Empty<DisplayTopologySnapshot>();
                return _sequence[Math.Min(index, _sequence.Count - 1)];
            }
        }
    }

    private sealed class RuntimeFfmpegBackend : ICaptureBackend, IFirstFrameObservableCaptureBackend
    {
        private readonly int _naturalExitDelayMs;
        private Action<int, OutputMeta>? _naturalExit;
        private int _abortCalls;
        private int _stopCalls;

        public RuntimeFfmpegBackend(int naturalExitDelayMs = -1)
        {
            _naturalExitDelayMs = naturalExitDelayMs;
            OutputPath = Path.Combine(Path.GetTempPath(), $"runtime-display-output-{Guid.NewGuid():N}.mp4");
        }

        public event Action<FirstFrameObservation>? FirstFrameObserved;
        public string OutputPath { get; }
        public int AbortCalls => Volatile.Read(ref _abortCalls);
        public int StopCalls => Volatile.Read(ref _stopCalls);
        public int ExitCode => 0;

        public void Start(CaptureConfig cfg)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(cfg.OutputPath)!);
            File.WriteAllBytes(cfg.OutputPath, Enumerable.Repeat((byte)0x7F, 4096).ToArray());
            _ = Task.Run(async () =>
            {
                await Task.Delay(15).ConfigureAwait(false);
                FirstFrameObserved?.Invoke(new FirstFrameObservation
                {
                    EvidenceKind = "frame",
                    FrameNumber = 1,
                    TotalSizeBytes = 4096,
                    OutTimeUs = 1000
                });

                if (_naturalExitDelayMs >= 0)
                {
                    await Task.Delay(_naturalExitDelayMs).ConfigureAwait(false);
                    _naturalExit?.Invoke(0, HealthyMeta());
                }
            });
        }

        public OutputMeta Stop()
        {
            Interlocked.Increment(ref _stopCalls);
            return HealthyMeta();
        }

        public OutputMeta Abort(CaptureAbortReason reason)
        {
            Interlocked.Increment(ref _abortCalls);
            try { if (File.Exists(OutputPath)) File.Delete(OutputPath); }
            catch { }
            var meta = HealthyMeta();
            meta.StopReason = CaptureAbortReasonCodes.ToCode(reason);
            return meta;
        }

        public void OnNaturalExit(Action<int, OutputMeta> callback) => _naturalExit = callback;
        public void Dispose()
        {
            try { if (File.Exists(OutputPath)) File.Delete(OutputPath); }
            catch { }
        }

        private OutputMeta HealthyMeta() => new()
        {
            OutputPath = OutputPath,
            SizeBytes = 4096,
            DurationSeconds = 1,
            Width = 320,
            Height = 240,
            Fps = 30,
            Container = "mp4",
            Codec = "h264",
            AudioStatus = "not_requested"
        };
    }
}
