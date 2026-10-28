using System.Net;
using System.Reflection;
using System.Text.Json.Nodes;
using AgentRecorder.Api;
using AgentRecorder.Capture;
using AgentRecorder.Core;
using AgentRecorder.Infrastructure;
using AgentRecorder.Logging;
using AgentRecorder.Windows;
using Xunit;

namespace AgentRecorder.Tests;

[Collection("NonParallel-AgentRecorderDataDir")]
public sealed class NeverStartedWorkerRetirementTests
{
    [Fact]
    public void NativeMissingHelperRetiresOnlyAfterRealCleanupWithoutChangingHasExited()
    {
        var root = Root();
        using var worker = MissingWorker(root);
        Assert.False(worker.HostResourcesRetired);
        var error = Assert.Throws<ApiException>(() => worker.Start(Config(), Path.Combine(root, "never.wav")));
        Assert.Equal("audio_helper_unavailable", error.Code);
        Assert.Null(Field(worker, "_watcher")); Assert.Null(Field(worker, "_stdoutReader"));
        Assert.Null(worker.HelperProcessIdForTests);
        Assert.False(worker.HasExited);
        Assert.False(worker.HostResourcesRetired);
        worker.Dispose();
        Assert.True(worker.HostResourcesRetired);
        Assert.False(worker.HasExited); // No fabricated recording lifecycle exit.
        Assert.False(File.Exists(Path.Combine(root, "never.wav")));
        Assert.ThrowsAny<Exception>(() => worker.Start(Config(), Path.Combine(root, "second.wav")));
        Assert.True(worker.HostResourcesRetired);
    }

    [Fact]
    public async Task ActualAvSplitEngineFailureCanExitAfterOwnerCleanupWithoutVideoOrPublication()
    {
        var root = Root();
        var prior = Environment.GetEnvironmentVariable("AGENT_RECORDER_DATA_DIR");
        var priorTestMode = Environment.GetEnvironmentVariable("AGENT_RECORDER_TEST_MODE");
        Environment.SetEnvironmentVariable("AGENT_RECORDER_TEST_MODE", "1");
        Environment.SetEnvironmentVariable("AGENT_RECORDER_DATA_DIR", root);
        ApiKeyAuth.InitializeForTesting(root);
        var oldSpace = RecordingPreflightChecker.FreeSpaceProvider;
        var oldEncoder = RecordingPreflightChecker.EncoderProvider;
        var oldResolver = RecordingPreflightChecker.AudioHelperPathResolver;
        var oldProbe = RecordingPreflightChecker.AudioHelperProbeRunner;
        RecordingPreflightChecker.AudioHelperPathResolver = () => "private-preflight-helper";
        RecordingPreflightChecker.AudioHelperProbeRunner = (_, _) => new() { Success = true, ProtocolVersion = "audio-helper-v1", TimestampFrequency = System.Diagnostics.Stopwatch.Frequency };
        RecordingPreflightChecker.FreeSpaceProvider = (string _, out long free) => { free = 100L << 30; return true; };
        RecordingPreflightChecker.EncoderProvider = (out string? ffmpeg, out string? ffprobe) =>
        { ffmpeg = ffprobe = typeof(NeverStartedWorkerRetirementTests).Assembly.Location; return true; };
        var audit = new QuietAudit();
        var tray = new Tray();
        var factory = new MissingFactory(MissingWorker(root));
        using var backend = new AvSplitCaptureBackend(factory, new ForbiddenRunner(), new TempRetentionPolicy());
        using var engine = new RecordingEngine(audit,
            displayTopologyProvider: new FixedDisplayTopologyProvider(new DisplayTopologySnapshot("display_1", "synthetic-test-display:display_1",
                DisplayIdentityResolutionStatus.Resolved, new CapturePlanBounds(0, 0, 1920, 1080))),
            systemAudioEndpointProvider: new Endpoint());
        engine.BackendFactory = _ => (backend, "ffmpeg-region-av-split");
        var server = new ApiServer(engine, audit, tray, null, null, null, null, null, null, null, null, null, 0);
        using var history = new History();
        var exited = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        server.ConfigureHostShutdown(() => null, () => exited.TrySetResult(HostRecordingShutdown.Stop(server, engine, history, "private_failed_start")));
        server.Start();
        using var http = new HttpClient(new HttpClientHandler { UseProxy = false }) { BaseAddress = new Uri($"http://127.0.0.1:{server.BoundPort}/") };
        http.DefaultRequestHeaders.Add("X-Agent-Recorder-Key", ApiKeyAuth.CurrentApiKey);
        try
        {
            var request = JsonNode.Parse("""
                {"source":{"type":"region","display_id":"display_1","coordinate_space":"virtual_screen","bounds":{"x":0,"y":0,"width":640,"height":480}},
                 "audio":{"microphone":{"enabled":false},"system_audio":{"enabled":true,"device_id":"fake-loopback-endpoint"}},
                 "stop_condition":{"type":"duration","seconds":200},"artifacts":{"bundle":false}}
                """)!;
            request["output"] = new JsonObject { ["directory"] = root, ["filename"] = "not-published.mp4" };
            engine.CreateRecording(request, "private", tray);
            var rec = Assert.Single(engine._recs.Values);
            using (var busy = await http.PostAsync("api/v1/host/shutdown", new StringContent("{}")))
                Assert.Equal(HttpStatusCode.Conflict, busy.StatusCode);
            Assert.False(engine.HostAdmission.IsClosed);
            tray.Approve!(ConfirmationDecision.Approve()); // Fake test-only UI, no production approval surface.
            Assert.True(await Task.Run(() => SpinWait.SpinUntil(() => rec.IsFinalized, TimeSpan.FromSeconds(5))));
            Assert.Equal(RecState.failed, rec.State);
            Assert.Same(backend, rec.Backend);
            Assert.Equal(1, factory.Audios);
            Assert.Contains("Failed to launch WASAPI audio helper", rec.Error ?? "");
            Assert.Equal(default, rec.StartedAtUtc);
            Assert.Equal(200, rec.Config.DurationSeconds); Assert.Equal(3, rec.CountdownSeconds); Assert.False(rec.GenerateBundle);
            Assert.Equal(0, factory.Videos);
            Assert.False(File.Exists(rec.OutputPath));
            _ = await Task.Run(() => SpinWait.SpinUntil(() => backend.HostResourcesRetired, TimeSpan.FromSeconds(2)));
            using var response = await http.PostAsync("api/v1/host/shutdown", new StringContent("{}"));
            Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
            Assert.True(await exited.Task.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.True(history.Closed);
            Assert.True(backend.HostResourcesRetired);
            Assert.False(factory.Worker.HasExited);
            Assert.Equal(RecState.failed, rec.State);
        }
        finally
        {
            server.Stop();
            RecordingPreflightChecker.FreeSpaceProvider = oldSpace;
            RecordingPreflightChecker.EncoderProvider = oldEncoder;
            RecordingPreflightChecker.AudioHelperPathResolver = oldResolver;
            RecordingPreflightChecker.AudioHelperProbeRunner = oldProbe;
            ApiKeyAuth.ResetForTesting(null);
            Environment.SetEnvironmentVariable("AGENT_RECORDER_DATA_DIR", prior);
            Environment.SetEnvironmentVariable("AGENT_RECORDER_TEST_MODE", priorTestMode);
        }
    }

    internal static string Root()
    { var path = Path.Combine(Path.GetTempPath(), "never-started-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(path); return path; }
    internal static WasapiAudioCaptureWorker MissingWorker(string root) => new()
    { HelperExePathOverride = Path.Combine(root, "guaranteed-missing-helper.exe"), EndpointIdOverride = "fake-loopback-endpoint", AllowedRootOverride = root, SkipMicrophoneStatusMonitor = true };
    internal static CaptureConfig Config() => new() { AudioSourceKind = AudioCaptureSourceKind.SystemLoopback, SystemLoopbackEndpoint = "fake-loopback-endpoint", DurationSeconds = 1 };
    private static object? Field(object value, string name) => value.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(value);
    private sealed class QuietAudit : AuditLogger { public override void Log(string name, object payload) { } }
    private sealed class History : IDisposable { internal bool Closed; public void Dispose() => Closed = true; }
    private sealed class Tray : ITrayContext
    {
        internal Action<ConfirmationDecision>? Approve;
        public string HostMode => "headless";
        public bool SupportsRegionSelectionUi => false;
        public void RequestConfirmation(RecordingConfirmationPresentation p, Action<ConfirmationDecision> callback) => Approve = callback;
        public void RequestRegionSelection(int seconds, Action<string, int, int, int, int, string, string> callback) => throw new InvalidOperationException();
        public void SetRecording(RecordingUiPresentation p) { }
        public void SetIdle(RecordingUiPresentation p) { }
        public void SetAllIdle() { }
        public void ShowError(string text) { }
    }
    private sealed class MissingFactory(WasapiAudioCaptureWorker worker) : IAvWorkerFactory
    {
        internal readonly WasapiAudioCaptureWorker Worker = worker;
        internal int Videos;
        internal int Audios;
        public IAudioCaptureWorker CreateAudioWorker() { Audios++; return Worker; }
        public IVideoCaptureWorker CreateVideoWorker() { Videos++; throw new InvalidOperationException("Video must never start."); }
    }
    private sealed class ForbiddenRunner : IExternalProcessRunner
    {
        public Task<ExternalProcessResult> RunAsync(string executable, IReadOnlyList<string> arguments, TimeSpan timeout,
            bool captureStderr = true, System.Text.Encoding? stderrEncoding = null, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("No mux/probe process is permitted.");
    }
    private sealed class Endpoint : ISystemAudioEndpointProvider
    {
        private readonly SystemAudioEndpointInfo _endpoint = new("fake-loopback-endpoint", "Synthetic", "render", "active", true);
        public Task<IReadOnlyList<SystemAudioEndpointInfo>> GetRenderEndpointsAsync(CancellationToken c = default) => Task.FromResult<IReadOnlyList<SystemAudioEndpointInfo>>(new[] { _endpoint });
        public Task<SystemAudioEndpointInfo?> GetDefaultMultimediaRenderEndpointAsync(CancellationToken c = default) => Task.FromResult<SystemAudioEndpointInfo?>(_endpoint);
        public Task<SystemAudioEndpointInfo?> GetEndpointAsync(string id, CancellationToken c = default) => Task.FromResult<SystemAudioEndpointInfo?>(_endpoint);
    }
    private sealed class FixedDisplayTopologyProvider(DisplayTopologySnapshot display) : IDisplayTopologyProvider
    {
        public IReadOnlyList<DisplayTopologySnapshot> GetCurrentDisplays() => new[] { display };
    }
}
