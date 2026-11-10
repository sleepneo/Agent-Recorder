using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using AgentRecorder.Api;
using AgentRecorder.Capture;
using AgentRecorder.Core;
using AgentRecorder.Infrastructure;
using AgentRecorder.Logging;
using AgentRecorder.Windows;
using Xunit;

namespace AgentRecorder.Tests;

[Collection("HeadlessHostIntegration")]
public sealed partial class ChapterMarksTests : IDisposable
{
    private sealed class NoOpTray : ITrayContext
    {
        public string HostMode => "headless";
        public bool SupportsRegionSelectionUi => false;
        public void RequestConfirmation(RecordingConfirmationPresentation presentation, Action<ConfirmationDecision> callback) { }
        public void RequestRegionSelection(int timeoutSeconds,
            Action<string, int, int, int, int, string, string> callback) { }
        public void SetRecording(RecordingUiPresentation rec) { }
        public void SetIdle(RecordingUiPresentation rec) { }
        public void SetAllIdle() { }
        public void ShowError(string text) { }
    }

    private sealed class FirstFrameBackend : ICaptureBackend, IFirstFrameObservableCaptureBackend
    {
        public event Action<FirstFrameObservation>? FirstFrameObserved;
        public bool EmitOnStart { get; set; }

        public void Start(CaptureConfig cfg, CaptureAuthorizationProof authorizationProof)
        {
            cfg.CommandArgs = "chapter-marks-test";
            if (EmitOnStart)
                Emit();
        }

        public OutputMeta Stop() => new();
        public void OnNaturalExit(Action<int, OutputMeta> callback) { }
        public int ExitCode => 0;
        public void Dispose() { }

        public void Emit() => FirstFrameObserved?.Invoke(new FirstFrameObservation
        {
            EvidenceKind = "test",
            FrameNumber = 1,
            TotalSizeBytes = 1,
            OutTimeUs = 0
        });
    }

    private readonly string _dataDir;
    private readonly CaptureAuditLogger _audit;
    private readonly RecordingEngine _engine;
    private readonly ApiServer _server;

    public ChapterMarksTests()
    {
        _dataDir = Path.Combine(Path.GetTempPath(), $"chapter-marks-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_dataDir);
        ApiKeyAuth.InitializeForTesting(_dataDir);

        _audit = new CaptureAuditLogger();
        _engine = new RecordingEngine(_audit, bundleGenerator: new UnusedMarkBundleGenerator());
        _engine.MonotonicFrequencyForTests = 1000;
        _engine.MonotonicTimestampProviderForTests = () => 0;
        _server = new ApiServer(_engine, _audit, new NoOpTray(), null, null, null, null, null, null, null, null, null, 0);
        _server.Start();
    }

    public void Dispose()
    {
        _server.Stop();
        try { if (Directory.Exists(_dataDir)) Directory.Delete(_dataDir, recursive: true); } catch { }
        ApiKeyAuth.ResetForTesting(null);
    }

    private Recording Register(RecState state = RecState.recording, DateTime? startedAtUtc = null, bool bundle = true, string backend = "ffmpeg", bool ordinary = false)
    {
        var rec = new Recording(bundle)
        {
            BackendType = backend,
            IsOrdinaryLocalRequest = ordinary,
            State = state,
            StartedAtUtc = startedAtUtc ?? DateTime.UtcNow.AddSeconds(-1),
            BackendStartAtUtc = DateTime.UtcNow.AddMinutes(-1),
            OutputPath = Path.Combine(_dataDir, "recording.mp4"),
            MarkTimelineAnchorTicks = 0
        };
        _engine._recs[rec.Id] = rec;
        return rec;
    }

    private static StringContent JsonContent(string json) =>
        new(json, Encoding.UTF8, "application/json");

    private async Task<(HttpStatusCode Status, string Body)> PostMarkAsync(
        string recordingId, string body, bool authenticated = true)
    {
        using var client = new HttpClient(new HttpClientHandler { UseProxy = false })
        {
            Timeout = TimeSpan.FromSeconds(5)
        };
        if (authenticated)
            client.DefaultRequestHeaders.Add("X-Agent-Recorder-Key", ApiKeyAuth.CurrentApiKey);

        using var response = await client.PostAsync(
            $"http://127.0.0.1:{_server.BoundPort}/api/v1/recordings/{recordingId}/marks",
            JsonContent(body));
        return (response.StatusCode, await response.Content.ReadAsStringAsync());
    }



    [Fact]
    public async Task MutatingMarkEndpoint_RequiresAuthentication()
    {
        var rec = Register();
        var result = await PostMarkAsync(rec.Id, "{\"label\":\"secret\"}", authenticated: false);

        Assert.Equal(HttpStatusCode.Unauthorized, result.Status);
        Assert.Empty(rec.SnapshotMarks());
        Assert.DoesNotContain(_audit.Events, e => e.evt == "recording.mark_added");
    }



    [Fact]
    public async Task UnknownRecording_ReturnsExistingNotFoundError()
    {
        var result = await PostMarkAsync("rec_does_not_exist", "{\"label\":\"x\"}");

        Assert.Equal(HttpStatusCode.NotFound, result.Status);
        Assert.Contains("RECORDING_NOT_FOUND", result.Body);
        Assert.DoesNotContain(_audit.Events, e => e.evt == "recording.mark_added");
    }







    [Fact]
    public void RecordingMark_ValidatesSubmittedControlsBeforeTrimming_AndPreservesScalarLimit()
    {
        foreach (var invalid in new[] { "\nchapter", "chapter\r", "\tchapter", "chapter\t", "chap\0ter" })
            Assert.Throws<ArgumentException>(() => new RecordingMark(0, invalid, "agent"));

        Assert.Equal("chapter", new RecordingMark(0, "  chapter  ", "agent").Label);
        Assert.Equal(200, new RecordingMark(0, new string('x', 200), "agent").Label.Length);
        Assert.Throws<ArgumentException>(() => new RecordingMark(0, new string('x', 201), "agent"));
        Assert.Throws<ArgumentException>(() => new RecordingMark(0, "\uD800", "agent"));
        Assert.Throws<ArgumentException>(() => new RecordingMark(0, "\uDC00", "agent"));
    }

    [Fact]
    public void FirstFrameTransition_EstablishesMonotonicAnchorExactlyOnce()
    {
        var rec = new Recording
        {
            SourceType = "display",
            OutputPath = Path.Combine(_dataDir, "transition.mp4"),
            Config = new CaptureConfig { SourceKind = "display", OutputPath = Path.Combine(_dataDir, "transition.mp4") }
        };
        var backend = new FirstFrameBackend { EmitOnStart = true };
        _engine.BackendFactory = _ => (backend, "fake-first-frame");
        var providerCalls = 0;
        _engine.MonotonicTimestampProviderForTests = () =>
        {
            providerCalls++;
            return 10_000;
        };
        _engine.UtcNowForTests = () => new DateTime(2026, 8, 20, 4, 0, 0, DateTimeKind.Utc);

        _engine.StartCaptureForTests(rec, new NoOpTray());

        Assert.Equal(RecState.recording, rec.State);
        Assert.Equal(10_000, rec.MarkTimelineAnchorTicks);
        Assert.Equal(1, providerCalls);

        backend.Emit();
        Assert.Equal(1, providerCalls);
    }




}
