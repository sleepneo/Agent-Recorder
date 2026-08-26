using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using AgentRecorder.Capture;
using AgentRecorder.Core;
using AgentRecorder.Infrastructure;
using AgentRecorder.Logging;
using Xunit;

namespace AgentRecorder.Tests;

[Collection("NonParallel-AgentRecorderDataDir")]
public sealed class CountdownConfigurationTests
{
    [Theory]
    [InlineData("{}", 3)]
    [InlineData("{\"countdown_seconds\":0}", 0)]
    [InlineData("{\"countdown_seconds\":1}", 1)]
    [InlineData("{\"countdown_seconds\":3}", 3)]
    [InlineData("{\"countdown_seconds\":10}", 10)]
    public void NormalizeCountdownSeconds_AcceptsDefaultAndInclusiveRange(string json, int expected)
    {
        Assert.Equal(expected, ConfigParser.NormalizeCountdownSeconds(JsonNode.Parse(json)!));
    }

    [Theory]
    [InlineData("{\"countdown_seconds\":-1}")]
    [InlineData("{\"countdown_seconds\":11}")]
    [InlineData("{\"countdown_seconds\":1.5}")]
    [InlineData("{\"countdown_seconds\":\"1\"}")]
    [InlineData("{\"countdown_seconds\":true}")]
    [InlineData("{\"countdown_seconds\":null}")]
    [InlineData("{\"countdown_seconds\":{}}")]
    [InlineData("{\"countdown_seconds\":[]}")]
    public void NormalizeCountdownSeconds_RejectsNonIntegerOrOutOfRange(string json)
    {
        var ex = Assert.Throws<ApiException>(() =>
            ConfigParser.NormalizeCountdownSeconds(JsonNode.Parse(json)!));

        Assert.Equal(400, ex.Status);
        Assert.Equal("INVALID_ARGUMENT", ex.Code);
    }

    [Fact]
    public void ConfigParser_BuildCarriesNormalizedCountdownIntoRecordingAndSummary()
    {
        var previous = Environment.GetEnvironmentVariable("AGENT_RECORDER_TEST_MODE");
        Environment.SetEnvironmentVariable("AGENT_RECORDER_TEST_MODE", "1");
        try
        {
            var recording = ConfigParser.Build(
                JsonNode.Parse("{\"source\":{\"type\":\"display\",\"display_id\":\"display_1\"},\"countdown_seconds\":10}")!,
                "test-agent", out var summary);

            Assert.Equal(10, recording.CountdownSeconds);
            Assert.Equal(10, recording.Config.CountdownSeconds);
            Assert.Equal(10, summary.CountdownSeconds);
        }
        finally
        {
            Environment.SetEnvironmentVariable("AGENT_RECORDER_TEST_MODE", previous);
        }
    }

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    [InlineData(10)]
    public void OrdinaryFfmpeg_UsesPerRecordingCountdownBeforeBackendStart(int seconds)
    {
        var audit = new MemoryAuditLogger();
        var tray = new CountdownTray();
        var backend = new ObservableBackend(autoFirstFrame: true);
        var engine = new RecordingEngine(audit);
        engine.SetTray(tray);
        engine.CountdownInterval = TimeSpan.FromMilliseconds(15);
        engine.BackendFactory = _ => (backend, "ffmpeg");

        var recording = NewRecording(seconds);
        engine.StartCaptureForTests(recording, tray);

        Assert.True(tray.FirstCountdownShown.Wait(TimeSpan.FromSeconds(2)));
        Assert.Equal(0, backend.StartCallCount);
        Assert.Equal(RecState.countdown, recording.State);
        Assert.Equal(default, recording.StartedAtUtc);
        tray.ReleaseCountdown.Set();

        Assert.True(SpinWait.SpinUntil(() => backend.StartCallCount == 1, TimeSpan.FromSeconds(3)));
        Assert.True(SpinWait.SpinUntil(() => recording.State == RecState.recording, TimeSpan.FromSeconds(2)));
        Assert.Equal(Enumerable.Range(1, seconds).Reverse().ToArray(), tray.CountdownValues.ToArray());
        Assert.Contains("recording.countdown_started", audit.Events);
        Assert.Contains("recording.countdown_completed", audit.Events);
    }

    [Fact]
    public void ZeroCountdown_StartsImmediatelyWithoutVisibleCountdownEvents()
    {
        var audit = new MemoryAuditLogger();
        var tray = new CountdownTray();
        var backend = new ObservableBackend(autoFirstFrame: true);
        var engine = new RecordingEngine(audit) { CountdownInterval = TimeSpan.FromMilliseconds(10) };
        engine.SetTray(tray);
        engine.BackendFactory = _ => (backend, "ffmpeg");

        var recording = NewRecording(0);
        engine.StartCaptureForTests(recording, tray);

        Assert.Equal(1, backend.StartCallCount);
        Assert.True(SpinWait.SpinUntil(() => recording.State == RecState.recording, TimeSpan.FromSeconds(2)));
        Assert.Empty(tray.CountdownValues);
        Assert.DoesNotContain("recording.countdown_started", audit.Events);
        Assert.DoesNotContain("recording.countdown_completed", audit.Events);
        Assert.DoesNotContain("recording.countdown_cancelled", audit.Events);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(10)]
    public void AudioReadyBackend_UsesConfiguredCountdown(int seconds)
    {
        var audit = new MemoryAuditLogger();
        var tray = new CountdownTray { PauseAfterFirst = true };
        var backend = new AudioReadyBackend();
        var engine = new RecordingEngine(audit);
        engine.SetTray(tray);
        engine.CountdownInterval = TimeSpan.FromMilliseconds(10);
        engine.BackendFactory = _ => (backend, "ffmpeg-av-split");

        var recording = NewRecording(seconds);
        recording.Microphone = true;
        recording.MicrophoneDeviceId = "mic-test";
        recording.Config.Microphone = true;
        recording.Config.MicDevice = "mic-test";
        engine.StartCaptureForTests(recording, tray);

        Assert.True(tray.FirstCountdownShown.Wait(TimeSpan.FromSeconds(2)));
        Assert.True(backend.AudioWorkerStarted);
        Assert.Equal(0, backend.StartVideoCallCount);
        tray.ReleaseCountdown.Set();
        Assert.True(SpinWait.SpinUntil(() => backend.StartVideoCallCount == 1, TimeSpan.FromSeconds(3)));
        Assert.True(SpinWait.SpinUntil(() => recording.State == RecState.recording, TimeSpan.FromSeconds(2)));
        Assert.Equal(Enumerable.Range(1, seconds).Reverse().ToArray(), tray.CountdownValues.ToArray());
    }

    [Fact]
    public void DeferredBackend_UsesConfiguredCountdownAndAuthorizesOnlyAtZero()
    {
        var audit = new MemoryAuditLogger();
        var tray = new CountdownTray();
        var backend = new DeferredBackend();
        var engine = new RecordingEngine(audit);
        engine.SetTray(tray);
        engine.CountdownInterval = TimeSpan.FromMilliseconds(10);
        engine.BackendFactory = _ => (backend, "wgc-continuous");

        var recording = NewRecording(1);
        engine.StartCaptureForTests(recording, tray);

        Assert.True(SpinWait.SpinUntil(() => tray.CountdownValues.Count == 1, TimeSpan.FromSeconds(2)));
        Assert.Equal(0, backend.StartCaptureCallCount);
        Assert.True(SpinWait.SpinUntil(() => backend.StartCaptureCallCount == 1, TimeSpan.FromSeconds(2)));
        Assert.True(SpinWait.SpinUntil(() => recording.State == RecState.recording, TimeSpan.FromSeconds(2)));
    }

    private static Recording NewRecording(int seconds) => new()
    {
        SourceType = "display",
        OutputPath = Path.Combine(Path.GetTempPath(), $"countdown-{Guid.NewGuid():N}.mp4"),
        CountdownSeconds = seconds,
        Config = new CaptureConfig
        {
            SourceKind = "display",
            Bounds = (0, 0, 320, 240),
            OutputPath = Path.Combine(Path.GetTempPath(), $"countdown-{Guid.NewGuid():N}.mp4"),
            CountdownSeconds = seconds
        }
    };

    private sealed class MemoryAuditLogger : AuditLogger
    {
        public MemoryAuditLogger() : base(Path.Combine(Path.GetTempPath(), $"countdown-audit-{Guid.NewGuid():N}.jsonl")) { }
        public List<string> Events { get; } = new();
        public override void Log(string evt, object payload) => Events.Add(evt);
    }

    private sealed class CountdownTray : ITrayContext
    {
        public string HostMode => "headless";
        public bool SupportsRegionSelectionUi => false;
        public List<int> CountdownValues { get; } = new();
        public bool PauseAfterFirst { get; init; }
        public ManualResetEventSlim FirstCountdownShown { get; } = new();
        public ManualResetEventSlim ReleaseCountdown { get; } = new();
        public void RequestConfirmation(RecordingConfirmationPresentation presentation, Action<ConfirmationDecision> callback) { }
        public void RequestRegionSelection(int timeoutSeconds, Action<string, int, int, int, int, string, string> callback) { }
        public void SetRecording(RecordingUiPresentation rec) { }
        public void SetIdle(RecordingUiPresentation rec) { }
        public void SetAllIdle() { }
        public void ShowError(string text) { }
        public void SetPreparing(RecordingUiPresentation rec) { }
        public void SetCountdown(RecordingUiPresentation rec)
        {
            if (rec.CountdownRemainingSeconds is int remainingSeconds)
            {
                bool first;
                lock (CountdownValues)
                {
                    first = CountdownValues.Count == 0;
                    CountdownValues.Add(remainingSeconds);
                }
                if (first)
                {
                    FirstCountdownShown.Set();
                    if (PauseAfterFirst)
                        ReleaseCountdown.Wait(TimeSpan.FromSeconds(5));
                }
            }
        }
        public void SetFinalizing(RecordingUiPresentation rec) { }
    }

    private sealed class ObservableBackend : ICaptureBackend, IFirstFrameObservableCaptureBackend
    {
        private readonly bool _autoFirstFrame;
        public ObservableBackend(bool autoFirstFrame) => _autoFirstFrame = autoFirstFrame;
        public event Action<FirstFrameObservation>? FirstFrameObserved;
        public int StartCallCount;
        public void Start(CaptureConfig cfg)
        {
            Interlocked.Increment(ref StartCallCount);
            if (_autoFirstFrame)
                FirstFrameObserved?.Invoke(new FirstFrameObservation
                {
                    EvidenceKind = "test_first_frame",
                    FrameNumber = 1,
                    TotalSizeBytes = 1
                });
        }
        public OutputMeta Stop() => new();
        public void OnNaturalExit(Action<int, OutputMeta> callback) { }
        public void Dispose() { }
    }

    private sealed class AudioReadyBackend : ICaptureBackend, IAudioReadyBackend, IFirstFrameObservableCaptureBackend
    {
        public event Action? AudioReady;
        public event Action<FirstFrameObservation>? FirstFrameObserved;
        public bool IsAudioReady => _started;
        public bool AudioWorkerStarted => _started;
        private bool _started;
        public int StartVideoCallCount;

        public void Start(CaptureConfig cfg)
        {
            _started = true;
            AudioReady?.Invoke();
        }
        public void StartVideo()
        {
            Interlocked.Increment(ref StartVideoCallCount);
            FirstFrameObserved?.Invoke(new FirstFrameObservation
            {
                EvidenceKind = "test_audio_first_frame",
                FrameNumber = 1,
                TotalSizeBytes = 1
            });
        }
        public OutputMeta Stop() => new();
        public void OnNaturalExit(Action<int, OutputMeta> callback) { }
        public void Dispose() { }
    }

    private sealed class DeferredBackend : ICaptureBackend, IDeferredCaptureStartBackend, IFirstFrameObservableCaptureBackend
    {
        public event Action<bool>? CaptureAuthorizationCompleted;
        public event Action<FirstFrameObservation>? FirstFrameObserved;
        public bool IsAwaitingCaptureStart => _started && StartCaptureCallCount == 0;
        private bool _started;
        public int StartCaptureCallCount;

        public void Start(CaptureConfig cfg) => _started = true;
        public void StartCapture()
        {
            Interlocked.Increment(ref StartCaptureCallCount);
            FirstFrameObserved?.Invoke(new FirstFrameObservation
            {
                EvidenceKind = "test_deferred_first_frame",
                FrameNumber = 1,
                TotalSizeBytes = 1
            });
            Task.Run(() => CaptureAuthorizationCompleted?.Invoke(true));
        }
        public OutputMeta Stop() => new();
        public void OnNaturalExit(Action<int, OutputMeta> callback) { }
        public void Dispose() { }
    }
}
