using System.Diagnostics;
using System.Net;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using AgentRecorder.Api;
using AgentRecorder.Capture;
using AgentRecorder.Core;
using AgentRecorder.Infrastructure;
using AgentRecorder.Persistence;
using AgentRecorder.Core.Automation;
using AgentRecorder.Windows;
using Microsoft.Data.Sqlite;
using Xunit;
using Xunit.Abstractions;

namespace AgentRecorder.Tests;

// The process/event provider is synthetic. Session, consent gate, final publishers,
// mux, probes, frame extraction, digest reads and history persistence are real.
[Collection("NonParallel-AgentRecorderDataDir")]
public sealed class OrdinaryWgcBundleTests(ITestOutputHelper output)
{
    private sealed class Events : Stream
    {
        private readonly Channel<byte[]> channel = Channel.CreateUnbounded<byte[]>();
        private byte[]? current; private int offset;
        internal void Send(string text) => Assert.True(channel.Writer.TryWrite(Encoding.UTF8.GetBytes(text)));
        internal void Complete() => channel.Writer.TryComplete();
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token = default)
        {
            if (current is null || offset == current.Length)
            {
                if (!await channel.Reader.WaitToReadAsync(token)) return 0;
                Assert.True(channel.Reader.TryRead(out current)); offset = 0;
            }
            var n = Math.Min(buffer.Length, current!.Length - offset);
            current.AsMemory(offset, n).CopyTo(buffer); offset += n; return n;
        }
        public override int Read(byte[] b, int o, int n) => ReadAsync(b.AsMemory(o, n)).AsTask().GetAwaiter().GetResult();
        public override bool CanRead => true; public override bool CanSeek => false; public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long o, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long n) => throw new NotSupportedException();
        public override void Write(byte[] b, int o, int n) => throw new NotSupportedException();
    }

    private sealed class SyntheticProcess(WgcContinuousSessionOptions options, string clip, Func<long> anchor) : IWgcContinuousProcess
    {
        private readonly Events events = new();
        private readonly CancellationTokenSource cancellation = new();
        private readonly TaskCompletionSource exited = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource Finish = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal Task? Worker; internal int BeginObserved; internal bool FailMedia;
        public int Id => 346; public bool HasExited => exited.Task.IsCompleted; public int ExitCode { get; private set; }
        public Stream StandardOutputStream => events; public Stream StandardErrorStream => Stream.Null;
        public void Start(string exe, IReadOnlyList<string> args)
        {
            Assert.Equal(options.HelperExePath, exe);
            Worker = Task.Run(async () =>
            {
                try
                {
                    await Until(() => File.Exists(options.BeginSignalPath), cancellation.Token);
                    Assert.Equal(options.BeginToken, File.ReadAllText(options.BeginSignalPath)); BeginObserved++;
                    var method = options.TargetKind switch { WgcContinuousTargetKind.Region => "WGC_D3D11_REGION_FRAME_STREAM", WgcContinuousTargetKind.Window => "WGC_D3D11_WINDOW_FRAME_STREAM", _ => "WGC_D3D11_FRAME_STREAM" };
                    events.Send($"RESULT: STARTED\nRecordingId: {options.RecordingId}\nOutput: {options.OutputPath}\nContainer: mp4\nCodec: h264\nFps: 15\nWidth: 64\nHeight: 64\nCaptureMethod: {method}\nEncoderMode: software\nEncoderSelectionReason: software_default\n\n");
                    events.Send($"RESULT: FIRST_FRAME\nStage: Capturing\nRecordingId: {options.RecordingId}\nFrameNumber: 1\nElapsedMs: 10\nSourceTimeHns: {anchor()}\n\n");
                    await Until(() => Finish.Task.IsCompleted || File.Exists(options.StopSignalPath), cancellation.Token);
                    var stopped = File.Exists(options.StopSignalPath);
                    if (FailMedia) events.Send("RESULT: FAIL\nErrorCode: ENCODER_UNAVAILABLE\nReason: owned_media_fault\nFramesCaptured: 0\nBytesWritten: 0\n\n");
                    else
                    {
                        File.Copy(clip, options.OutputPath, false);
                        events.Send($"RESULT: {(stopped ? "STOPPED" : "OK")}\nEncoderMode: software\nEncoderSelectionReason: software_default\nStopReason: {(stopped ? "user_requested" : "duration_reached")}\nFramesCaptured: 15\nFramesDropped: 0\nDurationMs: 1000\nFileSize: {new FileInfo(clip).Length} bytes\nWidth: 64\nHeight: 64\n\n");
                    }
                    ExitCode = FailMedia ? 1 : 0;
                }
                catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { ExitCode = 1; }
                finally { events.Complete(); exited.TrySetResult(); }
            });
        }
        public void KillEntireTree() => cancellation.Cancel();
        public Task WaitForExitAsync(CancellationToken token) => exited.Task.WaitAsync(token);
        public void Dispose()
        {
            cancellation.Cancel(); Worker?.WaitAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
            events.Dispose(); cancellation.Dispose();
        }
    }

    private sealed class HeldGenerator : IRecordingBundleGenerator
    {
        internal readonly FfmpegRecordingBundleGenerator Real = new();
        internal readonly TaskCompletionSource Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource<RecordingBundleRequest> Started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal Func<RecordingBundleRequest, Task<RecordingBundleGenerationResult>>? Fault;
        internal Task<RecordingBundleGenerationResult>? Work; internal int Calls;
        internal readonly List<Task<RecordingBundleGenerationResult>> Works = new(); internal Task? ObservedFault;
        public Task<RecordingBundleGenerationResult> GenerateAsync(RecordingBundleRequest r, CancellationToken token = default)
        {
            Interlocked.Increment(ref Calls);
            Work = Run(r, token); lock (Works) Works.Add(Work); Started.TrySetResult(r); return Work;
        }
        private async Task<RecordingBundleGenerationResult> Run(RecordingBundleRequest r, CancellationToken token)
        { await Release.Task.WaitAsync(token); return Fault is null ? await Real.GenerateAsync(r, token) : await Fault(r); }
    }
    private sealed class Tray : ITrayContext
    {
        internal Action<ConfirmationDecision>? Confirm;
        public string HostMode => "tray"; public bool SupportsRegionSelectionUi => true;
        public void RequestConfirmation(RecordingConfirmationPresentation p, Action<ConfirmationDecision> c) => Confirm = c;
        public void RequestRegionSelection(int t, Action<string, int, int, int, int, string, string> c) => c("selected", 0, 0, 64, 64, "display_1", "virtual_screen");
        public void SetRecording(RecordingUiPresentation r) { } public void SetIdle(RecordingUiPresentation r) { }
        public void SetAllIdle() { } public void ShowError(string s) { }
    }
    private sealed class Probe : IWgcContinuousAvailabilityProbe
    { public WgcContinuousAvailabilityResult Check(CaptureConfig c) => new(true, "synthetic_provider", "test"); }
    private sealed class Capacity : IStorageCapacityProvider
    {
        public Task<IReadOnlyList<StorageVolumeSample>> QueryAsync(IReadOnlyList<string> paths, CancellationToken token) =>
            Task.FromResult<IReadOnlyList<StorageVolumeSample>>(paths.Select(p => new StorageVolumeSample(p, "synthetic", 100L * 1024 * 1024 * 1024)).ToArray());
    }
    private sealed class Devices : ISystemAudioEndpointProvider
    {
        private static readonly SystemAudioEndpointInfo Endpoint = new("task346-output", "Synthetic output", "render", "active", true);
        public Task<IReadOnlyList<SystemAudioEndpointInfo>> GetRenderEndpointsAsync(CancellationToken t = default) => Task.FromResult<IReadOnlyList<SystemAudioEndpointInfo>>(new[] { Endpoint });
        public Task<SystemAudioEndpointInfo?> GetDefaultMultimediaRenderEndpointAsync(CancellationToken t = default) => Task.FromResult<SystemAudioEndpointInfo?>(Endpoint);
        public Task<SystemAudioEndpointInfo?> GetEndpointAsync(string id, CancellationToken t = default) => Task.FromResult<SystemAudioEndpointInfo?>(id == Endpoint.Id ? Endpoint : null);
    }
    private sealed class AvWorkers(Fixture owner) : IAvWorkerFactory
    {
        public IAudioCaptureWorker CreateAudioWorker() => throw new InvalidOperationException("Source-bearing overload required");
        public IAudioCaptureWorker CreateAudioWorker(AudioCaptureSourceKind kind)
        {
            Assert.Equal(AudioCaptureSourceKind.SystemLoopback, kind);
            return owner.Audio = new(true, holdFileOpen: true, holdFileOpenCopyFrom: owner.Wav, reportedSourceKind: "system-loopback");
        }
        public IVideoCaptureWorker CreateVideoWorker() => throw new InvalidOperationException("Proof-bearing WGC overload required");
        public IVideoCaptureWorker CreateVideoWorker(CaptureConfig cfg, CaptureAuthorizationProof proof)
        {
            Assert.True(cfg.RequireWindowSurface); Assert.NotNull(proof);
            return new WgcContinuousVideoCaptureWorker(proof, owner.NewWgc, Stopwatch.GetTimestamp);
        }
    }

    private sealed class Fixture : IDisposable
    {
        private static readonly string Parent = Path.GetFullPath(Path.Combine(TestHelper.ProjectRoot, ".local-data/temp/task346"));
        internal readonly string Root = Path.Combine(Parent, Guid.NewGuid().ToString("N"));
        internal readonly HeldGenerator Generator = new(); internal readonly Tray Tray = new();
        internal SqliteOperationalStore Store = null!; internal SqliteOrdinaryRecordingHistoryStore Writer = null!;
        internal RecordingEngine Engine = null!; internal ApiServer? Api;
        internal WgcContinuousCaptureBackend? Wgc; internal AvSplitCaptureBackend? Av; internal SyntheticProcess? Process;
        internal FakeAudioCaptureWorker? Audio; internal int FactoryCalls; internal bool UnknownNative;
        private readonly List<WgcContinuousCaptureBackend> backends = new(); private readonly List<SyntheticProcess> processes = new();
        internal string Clip => Path.Combine(Root, "prepared.mp4"); internal string Wav => Path.Combine(Root, "prepared.wav");
        private readonly Dictionary<string, string?> environment = new();
        private readonly string? priorDir = DataDirResolver.HasOverride ? DataDirResolver.Resolve() : null;
        private readonly IStorageCapacityProvider capacity = RecordingPreflightChecker.StorageCapacityProvider;
        private readonly RecordingPreflightChecker.TryResolveAudioHelper helper = RecordingPreflightChecker.AudioHelperPathResolver;
        private readonly RecordingPreflightChecker.RunAudioHelperProbe probe = RecordingPreflightChecker.AudioHelperProbeRunner;
        private readonly Func<List<SystemQuery.DisplayInfo>>? displays = Provider<Func<List<SystemQuery.DisplayInfo>>>("_displayProvider");
        private readonly Func<List<SystemQuery.DisplayTopologyInfo>>? topology = Provider<Func<List<SystemQuery.DisplayTopologyInfo>>>("_displayTopologyProvider");
        private readonly Func<bool, bool, List<SystemQuery.WindowInfo>>? windows = Provider<Func<bool, bool, List<SystemQuery.WindowInfo>>>("_windowProvider");
        private bool disposed;
        internal Fixture(bool generator = true, bool failConstruction = false)
        {
            try
            {
                Directory.CreateDirectory(Root);
                Set("AGENT_RECORDER_DATA_DIR", Root); Set("AGENT_RECORDER_TEST_MODE", null);
                Set(CaptureBackendSelector.DisplayBackendEnvVar, "wgc-continuous"); Set(CaptureBackendSelector.RegionBackendEnvVar, "wgc-continuous");
                Set(CaptureBackendSelector.WgcEnvVar, "wgc-continuous"); DataDirResolver.SetOverride(Root);
                RecordingPreflightChecker.StorageCapacityProvider = new Capacity();
                RecordingPreflightChecker.AudioHelperPathResolver = () => Path.Combine(Root, "audio-helper.exe");
                RecordingPreflightChecker.AudioHelperProbeRunner = (_, _) => new() { Success = true, ProtocolVersion = "audio-helper-v1", TimestampFrequency = Stopwatch.Frequency };
                File.WriteAllText(Path.Combine(Root, "audio-helper.exe"), "synthetic-not-executed");
                File.WriteAllText(Path.Combine(Root, "wgc-helper.exe"), "synthetic-not-executed");
                SystemQuery.SetDisplayProvider(() => new() { new("display_1", "Synthetic", true, new(0, 0, 64, 64), 1) });
                var fingerprint = DisplayIdentityDeriver.Resolve("task346", new[] { new DisplayTargetMapping("task346", "owned-display") }).Fingerprint!;
                SystemQuery.SetDisplayTopologyProvider(() => new() { new("display_1", "Synthetic", true, new(0, 0, 64, 64), 1, fingerprint, DisplayIdentityResolutionStatus.Resolved) });
                SystemQuery.SetWindowProvider((_, _) => new() { new("window_42", "Synthetic window", "task346.exe", 7, false, false, new(0, 0, 64, 64)) });
                if (failConstruction) throw new IOException("owned_constructor_fault");
                Store = new(Path.Combine(Root, "history.db")); Store.Initialize(); Writer = new(Store);
                Engine = new(new RetentionSharedOutputPathTests.QuietAudit(), systemAudioEndpointProvider: new Devices(), bundleGenerator: generator ? Generator : null)
                    { CountdownSteps = 0, DisableDeadlineWatchdogForTests = true };
                Engine.CapturePlanFactoryForTests = cfg => CaptureBackendSelector.BuildPlan(cfg, new Probe());
                Engine.OrdinaryWindowMetadataForTests = (_, _) => OrdinaryWindowTargetKeyTests.Facts();
                Engine.BackendFactory = cfg =>
                {
                    FactoryCalls++;
                    if (cfg.IsSystemLoopback) { Av = new(new AvWorkers(this), new ExternalProcessRunner(), new TempRetentionPolicy(Root)); return (Av, "wgc-window-av-split"); }
                    return (NewWgc(), "wgc-continuous");
                };
                Engine.ConfigureOrdinaryRecordingHistory(Writer); Engine.SetTray(Tray);
            }
            catch { Dispose(); throw; }
        }
        private void Set(string name, string? value) { environment.Add(name, Environment.GetEnvironmentVariable(name)); Environment.SetEnvironmentVariable(name, value); }
        private static T? Provider<T>(string name) where T : class => ((AsyncLocal<T?>)typeof(SystemQuery).GetField(name, BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!).Value;
        internal WgcContinuousCaptureBackend NewWgc()
        {
            IStagingToFinalPublisher publisher = UnknownNative
                ? new StagingToFinalPublisher(new PhysicalFileSystemOperations(), identityApi: new BundlePublicationIdentityTests.NoIdentityApi()) : StagingToFinalPublisher.Instance;
            Wgc = new(options =>
            {
                Process = new(options, Clip, () => (long)((decimal)(Audio is null ? Stopwatch.GetTimestamp() : Audio.MediaStartAnchorTicks + Stopwatch.Frequency / 10) * 10_000_000 / Stopwatch.Frequency));
                lock (processes) processes.Add(Process);
                return new WgcContinuousManagedSession(options, Process);
            }, publisher, FfmpegCaptureBackend.Probe, () => Path.Combine(Root, "wgc-helper.exe"), Root);
            backends.Add(Wgc); return Wgc;
        }
        internal async Task Prepare(bool audio = false)
        {
            await AvMuxOwnershipTests.Generate(Clip, "color=c=blue:s=64x64:r=15", false);
            if (audio) await AvMuxOwnershipTests.Generate(Wav, "sine=frequency=440:sample_rate=48000", true);
        }
        internal JsonObject Request(string source, bool audio = false)
        {
            var s = new JsonObject { ["type"] = source };
            if (source == "window") s["window_id"] = "window_42"; else s["display_id"] = "display_1";
            if (source == "region") { s["coordinate_space"] = "virtual_screen"; s["bounds"] = new JsonObject { ["x"] = 0, ["y"] = 0, ["width"] = 64, ["height"] = 64 }; }
            var cfg = new JsonObject { ["mode"] = "video", ["source"] = s, ["video"] = new JsonObject { ["fps"] = 15 }, ["stop_condition"] = new JsonObject { ["type"] = "duration", ["seconds"] = 1 }, ["countdown_seconds"] = 0,
                ["output"] = new JsonObject { ["directory"] = Path.Combine(Root, "Videos"), ["filename"] = "capture.mp4", ["conflict_policy"] = "fail" } };
            if (source == "window") { cfg["required_capture_semantics"] = "window_surface"; cfg["stop_condition"] = new JsonObject { ["type"] = "duration", ["seconds"] = 1 }; }
            if (audio) cfg["audio"] = new JsonObject { ["system_audio"] = new JsonObject { ["enabled"] = true } };
            return cfg;
        }
        internal Recording Create(string source, bool audio = false, string origin = "raw")
        {
            var cfg = Request(source, audio);
            if (origin == "profile") Engine.CreateRecordingFromFixedRegionProfile(cfg, "task346", Tray, new("P", 2, RecurringFixedRegionProfileVersion.DigestPrefix + new string('a', 64)), () => null);
            else
            {
                if (origin == "nested") cfg["nested"] = new JsonObject { ["role"] = "outer", ["session_id"] = "task346-session" };
                Engine.CreateRecording(cfg, "task346", Tray);
            }
            return Pending();
        }
        internal Recording Pending()
        {
            var rec = Assert.Single(Engine._recs.Values); Assert.True(rec.IsOrdinaryLocalRequest); Assert.Equal(RecState.pending_confirmation, rec.State);
            Assert.Equal("pending_confirmation", Writer.Read(rec.Id)!.Status); Assert.Null(rec.AuthorizationProof);
            Assert.Equal(0, FactoryCalls); Assert.Null(Process); Assert.Equal(0, Generator.Calls); Assert.NotNull(Tray.Confirm); return rec;
        }
        internal async Task Approve(Recording rec)
        {
            Tray.Confirm!(ConfirmationDecision.Approve()); await Until(() => rec.State == RecState.recording || rec.IsFinalized);
            Assert.True(rec.State == RecState.recording, $"Approval failed: {rec.State}; {rec.Error}; {rec.LastMeta?.StderrLog}; {rec.LastMeta?.StopReason}");
            Assert.Equal(1, FactoryCalls); Assert.Equal(1, Process!.BeginObserved); Assert.NotNull(rec.AuthorizationProof);
            Engine.AddMark(rec.Id, "标记346", "agent");
        }
        internal async Task Terminal(Recording rec, bool manual)
        {
            if (manual) Engine.Stop(rec.Id, "user_requested"); else Process!.Finish.TrySetResult();
            await Until(() => rec.IsFinalized); Assert.Equal(RecState.completed, rec.State);
            Assert.True(File.Exists(rec.OutputPath)); Assert.Equal(64, rec.LastMeta!.Width); Assert.Equal(64, rec.LastMeta.Height); Assert.InRange(rec.LastMeta.DurationSeconds, .9, 1.2);
        }
        internal void StartApi()
        {
            ApiKeyAuth.InitializeForTesting(Root);
            Api = new(Engine, new RetentionSharedOutputPathTests.QuietAudit(), Tray, null, null, null, null, null, null, null, null, null, 0,
                unifiedHistoryGateway: new SqliteUnifiedHistoryGateway(new(Store, Writer), () => (UnifiedHistoryTestDatabase.Sid, UnifiedHistoryTestDatabase.Session))); Api.Start();
        }
        internal HttpClient Client()
        {
            var c = new HttpClient(new HttpClientHandler { UseProxy = false }) { BaseAddress = new($"http://127.0.0.1:{Api!.BoundPort}"), Timeout = TimeSpan.FromSeconds(10) };
            c.DefaultRequestHeaders.Add("X-Agent-Recorder-Key", ApiKeyAuth.CurrentApiKey); return c;
        }
        internal void Reopen() { Assert.True(Writer.DrainAndDispose(TimeSpan.FromSeconds(5))); Store.Initialize(); Writer = new(Store); }
        public void Dispose()
        {
            if (disposed) return; disposed = true;
            var errors = new List<Exception>();
            void Clean(Action a) { try { a(); } catch (Exception e) { errors.Add(e); } }
            Generator.Release.TrySetResult();
            Clean(() => Api?.Stop()); Clean(() => Engine?.Dispose());
            Task[] jobs; lock (Generator.Works) jobs = Generator.Works.Where(t => !ReferenceEquals(t, Generator.ObservedFault)).ToArray();
            foreach (var job in jobs) Clean(() => job.WaitAsync(TimeSpan.FromSeconds(30)).GetAwaiter().GetResult());
            // Backend Dispose can already own process disposal; retained Worker is joined
            // separately so an exceptional backend teardown cannot leave fixture work alive.
            Clean(() => Av?.Dispose()); foreach (var backend in backends) Clean(backend.Dispose);
            SyntheticProcess[] owned; lock (processes) owned = processes.ToArray();
            foreach (var process in owned) Clean(() => { if (process.Worker is { } w) { if (!w.IsCompleted) process.KillEntireTree(); w.WaitAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult(); } });
            Clean(() =>
            {
                if (Writer is not null && !Writer.DrainAndDispose(TimeSpan.FromSeconds(5)))
                {
                    var failures = typeof(SqliteOrdinaryRecordingHistoryStore).GetFields(BindingFlags.Instance | BindingFlags.NonPublic)
                        .Where(f => f.Name.Contains("Failure", StringComparison.Ordinal))
                        .Select(f => f.Name + "=" + (f.GetValue(Writer) is System.Collections.IEnumerable entries ? string.Join(";", entries.Cast<object>()) : f.GetValue(Writer)));
                    throw new InvalidOperationException("Owned history writer did not drain: " + string.Join(" / ", failures));
                }
            });
            Clean(() => SystemQuery.SetDisplayProvider(displays)); Clean(() => SystemQuery.SetDisplayTopologyProvider(topology)); Clean(() => SystemQuery.SetWindowProvider(windows));
            Clean(() => RecordingPreflightChecker.StorageCapacityProvider = capacity);
            Clean(() => RecordingPreflightChecker.AudioHelperPathResolver = helper); Clean(() => RecordingPreflightChecker.AudioHelperProbeRunner = probe);
            Clean(() => { if (priorDir is null) DataDirResolver.ClearOverride(); else DataDirResolver.SetOverride(priorDir); });
            foreach (var pair in environment) Clean(() => Environment.SetEnvironmentVariable(pair.Key, pair.Value));
            Clean(() => ApiKeyAuth.ResetForTesting(null)); Clean(SqliteConnection.ClearAllPools);
            // Never erase evidence when an owned worker failed to settle.
            if (errors.Count == 0) Clean(() => { Assert.Equal(Parent, Path.GetDirectoryName(Path.GetFullPath(Root))); if (Directory.Exists(Root)) Directory.Delete(Root, true); });
            if (errors.Count != 0) throw new AggregateException(errors);
        }
    }

    private static async Task Until(Func<bool> p, CancellationToken token = default)
    {
        var end = DateTime.UtcNow.AddSeconds(15);
        while (!p() && DateTime.UtcNow < end) await Task.Delay(10, token);
        Assert.True(p(), "Owned asynchronous operation did not settle within 15 seconds.");
    }
    private static string Hash(string path) { using var f = File.OpenRead(path); return Convert.ToHexString(SHA256.HashData(f)).ToLowerInvariant(); }
    private static async Task<JsonElement> Get(HttpClient c, string path)
    {
        using var response = await c.GetAsync(path); Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var d = JsonDocument.Parse(await response.Content.ReadAsStringAsync()); return d.RootElement.GetProperty("data").Clone();
    }
    private async Task Verify(Fixture f, Recording rec, bool audio = false)
    {
        var request = await f.Generator.Started.Task.WaitAsync(TimeSpan.FromSeconds(15));
        await Until(() => f.Writer.Read(rec.Id)?.Bundle?.Status == "generating");
        Assert.Equal("completed", f.Writer.Read(rec.Id)!.Status); Assert.Equal(RecState.completed, rec.State);
        Assert.NotNull(rec.LastMeta!.PublicationReceipt); var op = rec.OrdinaryBundleOperation!;
        Assert.Equal(rec.Id, op.RecordingId); Assert.Equal(rec.OutputPath, op.MediaPath);
        Assert.Equal(op.OperationId, request.PublicationOperationId); Assert.Equal(rec.BackendType, request.Backend);
        Assert.Equal(rec.OutputPath, rec.LastMeta.PublicationReceipt!.Anchor.Path);
        f.Generator.Release.TrySetResult(); var result = await f.Generator.Work!;
        Assert.True(result.Success, $"Formal Bundle publisher failed: {result.ErrorCode}; {result.ErrorDetail}. TxF failure must remain failure, never skip.");
        await Until(() => f.Writer.Read(rec.Id)?.Bundle?.Status == "ready"); var row = f.Writer.Read(rec.Id)!;
        var identity = row.Bundle!.IdentityEvidence!; Assert.NotNull(identity); Assert.Equal(op.OperationId, identity.OperationId);
        Assert.Equal(rec.Id, identity.RecordingId); Assert.Equal(rec.OutputPath, identity.MediaPath);
        Assert.Equal(rec.LastMeta.PublicationReceipt.Anchor, result.MediaHashEvidence!.Anchor);
        Assert.Equal(Hash(rec.OutputPath), row.MediaHash!.Value);
        var names = new[] { "metadata.json", "thumbnail.jpg", "first_frame.png", "last_frame.png", "marks.json" };
        Assert.Equal(names, row.Bundle.ContentsEvidence!.Contents.Select(c => c.Name));
        Assert.Equal(names.OrderBy(n => n), Directory.GetFiles(row.Bundle.Path!).Select(Path.GetFileName).OrderBy(n => n));
        for (var i = 0; i < 5; i++)
        {
            var path = Path.Combine(row.Bundle.Path!, names[i]); using var s = File.OpenRead(path);
            var native = PublicationHandleObservation.Read(WindowsFileMetadataApi.Instance, s.SafeFileHandle, path)!;
            Assert.Equal(native.Snapshot.Identity, identity.Anchors[i].Identity); Assert.Equal(s.Length, identity.Anchors[i].Length);
            Assert.Equal(Hash(path), row.Bundle.ContentHash(i)!.Value); Assert.Equal(s.Length, row.Bundle.ContentsEvidence.Contents[i].SizeBytes);
        }
        using var metadata = JsonDocument.Parse(File.ReadAllText(Path.Combine(row.Bundle.Path!, "metadata.json")));
        Assert.Equal(rec.BackendType, metadata.RootElement.GetProperty("recording").GetProperty("backend").GetString());
        Assert.Equal(rec.SourceType, metadata.RootElement.GetProperty("source").GetProperty("type").GetString());
        Assert.Equal(row.MediaHash.Value, metadata.RootElement.GetProperty("media").GetProperty("sha256").GetString());
        using var marks = JsonDocument.Parse(File.ReadAllText(Path.Combine(row.Bundle.Path!, "marks.json")));
        Assert.Equal("标记346", Assert.Single(marks.RootElement.GetProperty("marks").EnumerateArray()).GetProperty("label").GetString());
        if (audio)
        {
            Assert.True(rec.LastMeta.HasAudioStream); Assert.Equal("system_loopback_recorded", rec.LastMeta.AudioStatus);
            Assert.True(FfmpegCaptureBackend.Probe(rec.OutputPath).HasAudioStream); Assert.Null(f.Wgc!.Stop().PublicationReceipt);
            Assert.True(f.Audio!.StopCalled); Assert.True(f.Audio.WaitForExitCalled); Assert.True(f.Audio.IsFileHandleReleased);
            Assert.Equal("system_loopback_recorded", metadata.RootElement.GetProperty("recording").GetProperty("audio_status").GetString());
        }
        f.StartApi(); using var client = f.Client();
        // Read-only history still works while content handles cannot be opened.
        using (var block = new FileStream(rec.OutputPath, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            foreach (var path in new[] { "/api/v1/recording-history", "/api/v1/recording-history/" + rec.Id, "/api/v1/history?source=ordinary" })
            {
                var data = await Get(client, path); var item = path.Contains("/recording-history/") ? data.GetProperty("recording") : Assert.Single(data.GetProperty("items").EnumerateArray());
                Assert.Equal("ready", item.GetProperty("bundle").GetProperty("status").GetString());
                Assert.Equal(row.MediaHash.Value, item.GetProperty("media_hash").GetProperty("value").GetString());
                Assert.Equal(Enumerable.Range(0, 5).Select(i => row.Bundle.ContentHash(i)!.Value), item.GetProperty("bundle").GetProperty("contents").EnumerateArray().Select(i => i.GetProperty("content_hash").GetProperty("value").GetString()));
            }
        }
        f.Api!.Stop(); f.Api = null; f.Engine.Dispose(); f.Reopen();
        Assert.Equal(row.Bundle, f.Writer.Read(rec.Id)!.Bundle); Assert.Equal(row.MediaHash, f.Writer.Read(rec.Id)!.MediaHash);
        Assert.Equal(row.Bundle, Assert.Single(f.Writer.ReadPage(20, null).Items).Bundle);
        Assert.Equal(row.MediaHash, Assert.Single(new UnifiedHistoryQueryService(f.Store, f.Writer).GetPage(UnifiedHistoryTestDatabase.Sid, UnifiedHistoryTestDatabase.Session, 20, new("ordinary")).Items).MediaHash);
        Assert.Equal(1, f.Generator.Calls);
        output.WriteLine($"Synthetic events -> actual WGC managed session -> {(audio ? "intermediate video + real AV mux" : "real silent final publisher")} -> actual FFmpeg five attachments -> private exact operation/digests -> SQLite/three HTTP projections/reopen; {rec.SourceType}, {rec.BackendType}.");
    }

    [Theory]
    [InlineData("display", false)] [InlineData("display", true)]
    [InlineData("region", false)] [InlineData("region", true)]
    [InlineData("window", false)] [InlineData("window", true)]
    public async Task ActualSilentSessionFinalPublicationAndBundle(string source, bool manual)
    { using var f = new Fixture(); await f.Prepare(); var rec = f.Create(source); await f.Approve(rec); await f.Terminal(rec, manual); await Verify(f, rec); }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task ActualWgcVideoAdapterAndAvFinalMuxReceipt(bool manual)
    { using var f = new Fixture(); await f.Prepare(true); var rec = f.Create("window", true); await f.Approve(rec); await f.Terminal(rec, manual); Assert.Equal("wgc-window-av-split", rec.BackendType); await Verify(f, rec, true); }

    [Theory]
    [InlineData("profile")] [InlineData("nested")] [InlineData("quick")]
    public async Task ExistingOrdinaryOriginsUseSamePublicationChain(string origin)
    {
        using var f = new Fixture(); await f.Prepare(); Recording rec;
        if (origin == "quick")
        {
            f.StartApi(); using var client = f.Client();
            var cfg = new JsonObject { ["target"] = new JsonObject { ["type"] = "primary_display" }, ["duration_seconds"] = 1, ["countdown_seconds"] = 0, ["output"] = f.Request("display")["output"]!.DeepClone() };
            using var response = await client.PostAsync("/api/v1/recordings/quick", new StringContent(cfg.ToJsonString(), Encoding.UTF8, "application/json"));
            Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync()); rec = f.Pending(); f.Api!.Stop(); f.Api = null;
        }
        else rec = f.Create(origin == "profile" ? "region" : "display", origin: origin);
        await f.Approve(rec); await f.Terminal(rec, false); await Verify(f, rec);
        if (origin == "profile") Assert.Equal("P", f.Writer.Read(rec.Id)!.ProfileId);
        if (origin == "nested") Assert.Equal("outer", f.Writer.Read(rec.Id)!.NestedRole);
    }

    [Theory]
    [InlineData("exception")] [InlineData("conflict")] [InlineData("foreign_path")] [InlineData("transaction_unavailable")]
    public async Task BundleFailurePreservesSuccessfulWgcMedia(string fault)
    {
        using var f = new Fixture(); await f.Prepare(); var rec = f.Create("display"); await f.Approve(rec); await f.Terminal(rec, false);
        await f.Generator.Started.Task; var hash = Hash(rec.OutputPath);
        if (fault == "exception") f.Generator.Fault = _ => throw new IOException("owned_generator_fault");
        if (fault == "foreign_path") f.Generator.Fault = _ => Task.FromResult(RecordingBundleGenerationResult.Ready(Path.Combine(f.Root, "foreign.bundle")));
        if (fault == "transaction_unavailable") f.Generator.Real.PublisherFactory = () => new() { StageForTests = (stage, _) => { if (stage == "before_commit") throw new PlatformNotSupportedException("owned_TxF_unavailable_fault"); } };
        if (fault == "conflict") { Directory.CreateDirectory(rec.OrdinaryBundleOperation!.BundlePath); File.WriteAllText(Path.Combine(rec.OrdinaryBundleOperation.BundlePath, "sentinel"), "owned-conflict"); }
        f.Generator.Release.TrySetResult(); await Until(() => f.Writer.Read(rec.Id)?.Bundle?.Status == "failed");
        Assert.Equal("completed", f.Writer.Read(rec.Id)!.Status); Assert.Equal(RecState.completed, rec.State); Assert.Equal(hash, Hash(rec.OutputPath));
        Assert.NotNull(rec.LastMeta!.PublicationReceipt); Assert.Null(f.Writer.Read(rec.Id)!.MediaHash); Assert.Equal(1, f.Generator.Calls);
        if (fault == "conflict") Assert.Equal("owned-conflict", File.ReadAllText(Path.Combine(rec.OrdinaryBundleOperation!.BundlePath, "sentinel")));
        // The injected exception is an expected generation fault already handled by Core.
        if (fault == "exception") { await Assert.ThrowsAsync<IOException>(() => f.Generator.Work!); f.Generator.ObservedFault = f.Generator.Work; }
    }

    [Fact]
    public async Task NaturalStopRaceAndForeignOrLateWriterCannotChangeWinner()
    {
        using var f = new Fixture(); await f.Prepare(); var rec = f.Create("window"); await f.Approve(rec);
        using var release = new ManualResetEventSlim(); var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        f.Wgc!.OnBeforeCallbackArbiterForTests = () => { entered.TrySetResult(); Assert.True(release.Wait(TimeSpan.FromSeconds(5))); };
        f.Process!.Finish.TrySetResult(); await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var stopping = Task.Run(() => f.Engine.Stop(rec.Id, "user_requested")); release.Set(); await stopping;
        f.Wgc.OnBeforeCallbackArbiterForTests = null;
        await Until(() => rec.IsFinalized); var request = await f.Generator.Started.Task; var op = rec.OrdinaryBundleOperation!;
        await Until(() => f.Writer.Read(rec.Id)?.Bundle?.Status == "generating");
        Assert.True(f.Writer.TryPublishBundle(new(op with { OperationId = Guid.NewGuid().ToString("N") }, new("failed", op.BundlePath, RecordingBundleErrorCodes.PublishFailed))));
        Assert.Equal("generating", f.Writer.Read(rec.Id)!.Bundle!.Status);
        f.Generator.Release.TrySetResult(); await Until(() => f.Writer.Read(rec.Id)?.Bundle?.Status == "ready"); var winner = f.Writer.Read(rec.Id)!;
        Assert.True(f.Writer.TryPublishBundle(new(op, new("failed", op.BundlePath, RecordingBundleErrorCodes.PublishFailed))));
        // Replacing an accepted contents set is an existing write-failure latch.
        // It must not silently alter the winner; restart retains the durable facts.
        await Until(() => ((System.Collections.IDictionary)typeof(SqliteOrdinaryRecordingHistoryStore).GetField("_unsettledBundleFailures", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(f.Writer)!).Count == 1);
        Assert.Throws<OrdinaryRecordingHistoryUnavailableException>(() => f.Writer.Read(rec.Id));
        Assert.False(f.Writer.DrainAndDispose(TimeSpan.FromSeconds(5)));
        Assert.True(((Task)typeof(SqliteOrdinaryRecordingHistoryStore).GetField("_worker", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(f.Writer)!).IsCompletedSuccessfully);
        f.Store.Initialize(); f.Writer = new(f.Store); Assert.Equal(winner, f.Writer.Read(rec.Id));
        Assert.Equal(1, f.Generator.Calls); Assert.Equal(op.OperationId, request.PublicationOperationId); Assert.Equal(RecState.completed, rec.State);
    }

    [Fact]
    public async Task UnknownNativeIdentityKeepsCompatibilityWithoutTrustedDigest()
    {
        using var f = new Fixture { UnknownNative = true }; f.Generator.Real.MediaHashMetadataApi = new BundlePublicationIdentityTests.NoIdentityApi();
        f.Generator.Real.PublisherFactory = () => new() { IdentityApi = new BundlePublicationIdentityTests.NoIdentityApi() };
        await f.Prepare(); var rec = f.Create("display"); await f.Approve(rec); await f.Terminal(rec, false);
        await f.Generator.Started.Task; Assert.Null(rec.LastMeta!.PublicationReceipt); f.Generator.Release.TrySetResult();
        await Until(() => f.Writer.Read(rec.Id)?.Bundle?.Status == "ready"); var row = f.Writer.Read(rec.Id)!;
        Assert.Null(row.MediaHash); Assert.Null(row.Bundle!.IdentityEvidence); Assert.Null(row.Bundle.ContentHashEvidence); Assert.Equal(5, row.Bundle.ContentsEvidence!.Contents.Count);
        f.Engine.Dispose(); f.Reopen(); Assert.Equal(row.Bundle, f.Writer.Read(rec.Id)!.Bundle); Assert.Null(f.Writer.Read(rec.Id)!.MediaHash);
    }

    [Fact]
    public async Task NestedInnerAndOuterKeepSeparateWgcPublicationOperations()
    {
        using var f = new Fixture(); await f.Prepare(); var outer = f.Create("display", origin: "nested"); await f.Approve(outer); var outerProcess = f.Process!;
        var cfg = f.Request("region"); cfg["output"]!["filename"] = "inner.mp4";
        cfg["nested"] = new JsonObject { ["role"] = "inner", ["session_id"] = "task346-session", ["parent_recording_id"] = outer.Id };
        f.Engine.CreateRecording(cfg, "task346", f.Tray); var inner = Assert.Single(f.Engine._recs.Values, r => r.Id != outer.Id);
        Assert.True(inner.IsOrdinaryLocalRequest); Assert.Equal(RecState.pending_confirmation, inner.State); Assert.Equal(1, f.FactoryCalls);
        f.Tray.Confirm!(ConfirmationDecision.Approve()); await Until(() => inner.State == RecState.recording); Assert.Equal(2, f.FactoryCalls);
        f.Engine.AddMark(inner.Id, "inner346", "agent"); f.Process!.Finish.TrySetResult(); await Until(() => inner.IsFinalized);
        await f.Generator.Started.Task; f.Generator.Release.TrySetResult(); await Until(() => f.Writer.Read(inner.Id)?.Bundle?.Status == "ready");
        outerProcess.Finish.TrySetResult(); await Until(() => outer.IsFinalized); await Until(() => f.Writer.Read(outer.Id)?.Bundle?.Status == "ready");
        Assert.Equal(2, f.Generator.Calls); Assert.NotEqual(inner.OrdinaryBundleOperation!.OperationId, outer.OrdinaryBundleOperation!.OperationId);
        foreach (var rec in new[] { outer, inner })
        {
            var row = f.Writer.Read(rec.Id)!; Assert.Equal("completed", row.Status); Assert.Equal(rec.Id, row.Bundle!.IdentityEvidence!.RecordingId);
            Assert.Equal(rec.OrdinaryBundleOperation!.OperationId, row.Bundle.IdentityEvidence.OperationId); Assert.Equal(Hash(rec.OutputPath), row.MediaHash!.Value);
            Assert.NotNull(rec.LastMeta!.PublicationReceipt); Assert.Equal(rec.OutputPath, rec.LastMeta.PublicationReceipt!.Anchor.Path);
        }
        Assert.Equal(outer.Id, f.Writer.Read(inner.Id)!.ParentRecordingId); Assert.Equal("inner", f.Writer.Read(inner.Id)!.NestedRole);
        using var metadata = JsonDocument.Parse(File.ReadAllText(Path.Combine(inner.OrdinaryBundleOperation.BundlePath, "metadata.json")));
        Assert.Equal("inner", metadata.RootElement.GetProperty("recording").GetProperty("nested_role").GetString());
        Assert.Equal(outer.Id, metadata.RootElement.GetProperty("recording").GetProperty("parent_recording_id").GetString());
    }

    [Theory]
    [InlineData("pending")] [InlineData("rejected")] [InlineData("expired")] [InlineData("failed_media")] [InlineData("no_generator")]
    public async Task OrdinaryNegativePathsDoNotGenerate(string path)
    {
        using var f = new Fixture(path != "no_generator"); await f.Prepare();
        var rec = f.Create("display");
        if (path == "rejected") { f.Tray.Confirm!(ConfirmationDecision.Reject()); Assert.Equal(RecState.rejected, rec.State); }
        if (path == "expired") { f.Engine.TriggerConfirmationExpiryForTests(rec.ConfirmationId!); await Until(() => rec.State == RecState.expired); f.Tray.Confirm!(ConfirmationDecision.Approve()); Assert.Equal(RecState.expired, rec.State); }
        if (path is "pending" or "rejected" or "expired") { Assert.Equal(0, f.FactoryCalls); Assert.Null(f.Process); }
        else
        {
            await f.Approve(rec); if (path == "failed_media") f.Process!.FailMedia = true;
            f.Process!.Finish.TrySetResult(); await Until(() => rec.IsFinalized);
            Assert.Equal(path == "failed_media" ? RecState.failed : RecState.completed, rec.State);
        }
        Assert.Equal(0, f.Generator.Calls); Assert.Equal(path == "pending" ? "pending" : "not_applicable", rec.BundleSnapshot.Status);
    }

    private sealed class Counting : IRecordingBundleGenerator
    {
        internal int Calls;
        public Task<RecordingBundleGenerationResult> GenerateAsync(RecordingBundleRequest r, CancellationToken t = default)
        { Interlocked.Increment(ref Calls); return Task.FromResult(RecordingBundleGenerationResult.Ready(OrdinaryRecordingBundleOperation.DerivePath(r.MediaPath))); }
    }
    private sealed class EligibilityBackend : ICaptureBackend
    {
        private Action<int, OutputMeta>? natural;
        public void Start(CaptureConfig cfg, CaptureAuthorizationProof proof) { }
        public OutputMeta Stop() => new();
        public void OnNaturalExit(Action<int, OutputMeta> callback) => natural = callback;
        internal void FireNaturalExit(int code, OutputMeta meta) => natural!(code, meta);
        public int ExitCode => 0; public void Dispose() { }
    }
    [Theory]
    [InlineData("ffmpeg", false, "mp4", true)] [InlineData("ffmpeg-region", false, "mp4", true)]
    [InlineData("ffmpeg-window-region", false, "mp4", true)] [InlineData("ffmpeg-av-split", false, "mp4", true)]
    [InlineData("ffmpeg-region-av-split", false, "mp4", true)] [InlineData("ffmpeg-window-region-av-split", false, "mp4", true)]
    [InlineData("wgc-continuous", true, "mp4", true)] [InlineData("wgc-window-av-split", true, "mp4", true)]
    [InlineData("wgc-continuous", false, "mp4", false)] [InlineData("wgc-window-av-split", false, "mp4", false)]
    [InlineData("wgc-unknown", true, "mp4", false)] [InlineData("WGC-CONTINUOUS", true, "mp4", false)]
    [InlineData("wgc-continuous", true, "avi", false)] [InlineData("wgc-window-av-split", true, "avi", false)]
    public async Task LocalEligibilityOnlyAndOriginalSixFfmpegBackends(string backend, bool ordinary, string container, bool expected)
    {
        // This is only a classification test, deliberately not publication evidence.
        using var f = new Fixture(); var gen = new Counting(); using var engine = new RecordingEngine(new RetentionSharedOutputPathTests.QuietAudit(), bundleGenerator: gen);
        var capture = new EligibilityBackend(); engine.SetTray(f.Tray); engine.BackendFactory = _ => (capture, backend);
        var rec = new Recording { IsOrdinaryLocalRequest = ordinary, SourceType = "region", BackendType = backend, OutputPath = Path.Combine(f.Root, "eligibility.mp4"), Config = new() { SourceKind = "region", Bounds = (0, 0, 64, 64), OutputPath = Path.Combine(f.Root, "eligibility.mp4") } };
        engine.StartCaptureForTests(rec, f.Tray); capture.FireNaturalExit(0, new() { Container = container, Codec = "h264", DurationSeconds = 1, SizeBytes = 1024, Width = 64, Height = 64 });
        if (expected) await Until(() => gen.Calls == 1); else Assert.Equal(0, gen.Calls);
        Assert.Equal(expected, rec.BundleSnapshot.Status is "generating" or "ready");
        Assert.Equal(backend.StartsWith("ffmpeg", StringComparison.Ordinal), CaptureBackendSelector.IsFfmpegMp4Backend(backend));
    }

    [Fact]
    public void ConstructorFailureRestoresScopedProvidersAndEnvironment()
    {
        var old = Environment.GetEnvironmentVariable(CaptureBackendSelector.DisplayBackendEnvVar); var capacity = RecordingPreflightChecker.StorageCapacityProvider;
        Assert.Throws<IOException>(() => new Fixture(failConstruction: true));
        Assert.Equal(old, Environment.GetEnvironmentVariable(CaptureBackendSelector.DisplayBackendEnvVar)); Assert.Same(capacity, RecordingPreflightChecker.StorageCapacityProvider);
    }

    [Theory]
    [InlineData("wgc-continuous")] [InlineData("wgc-window-av-split")]
    public void ScreenshotModeCannotEnterNewWgcBundleEligibility(string backend)
    {
        using var f = new Fixture(); var rec = new Recording { IsOrdinaryLocalRequest = true, BackendType = backend, Config = new() { Mode = ScreenshotSeriesConfig.ModeName } };
        var args = new object?[] { rec, new OutputMeta { Container = "mp4" }, null };
        Assert.False((bool)typeof(RecordingEngine).GetMethod("TryPrepareBundleGeneration", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(f.Engine, args)!);
        Assert.Equal(0, f.Generator.Calls); Assert.Equal(0, f.FactoryCalls);
    }
}
