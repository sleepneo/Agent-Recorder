using System.Diagnostics;
using System.Net;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using AgentRecorder.Api;
using AgentRecorder.App;
using AgentRecorder.Logging;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;
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

// Synthetic target and helper events; real authorization, session, publication,
// mux, SQLite transactions and coordinator terminal handoff.
[Collection("NonParallel-AgentRecorderDataDir")]
public sealed partial class FutureWindowPublicationAnchorTests(ITestOutputHelper output)
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
        private static readonly SystemAudioEndpointInfo Endpoint = new("task347-output", "Synthetic output", "render", "active", true);
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
        private static readonly string Parent = Path.GetFullPath(Path.Combine(TestHelper.ProjectRoot, ".local-data/temp/task347"));
        private string OwnedParent=Parent;
        internal string Root = Path.Combine(Parent, Guid.NewGuid().ToString("N"));
        internal readonly Tray Tray = new();
        internal SqliteFutureWindowAuthorizationRepository Future = null!; internal UnifiedHistoryQueryService Query = null!;
        internal FutureWindowOneShotAuthorizationCoordinator Coordinator = null!; private AuditLogger? audit;
        internal readonly string Sid = WindowsIdentity.GetCurrent().User!.Value; internal readonly string Session = CaptureAuthorizationSessionBinding.Current;
        internal readonly ApprovalUi Approval = new();
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
        internal Fixture(bool failConstruction = false,string? ownedParent=null)
        {
            try
            {
                if(ownedParent is not null){OwnedParent=Path.GetFullPath(ownedParent);Root=Path.Combine(OwnedParent,Guid.NewGuid().ToString("N"));}
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
                var fingerprint = DisplayIdentityDeriver.Resolve("task347", new[] { new DisplayTargetMapping("task347", "owned-display") }).Fingerprint!;
                SystemQuery.SetDisplayTopologyProvider(() => new() { new("display_1", "Synthetic", true, new(0, 0, 64, 64), 1, fingerprint, DisplayIdentityResolutionStatus.Resolved) });
                SystemQuery.SetWindowProvider((_, _) => new() { new("window_42", "Synthetic window", "task347.exe", 7, false, false, new(0, 0, 64, 64)) });
                if (failConstruction) throw new IOException("owned_constructor_fault");
                Store = new(Path.Combine(Root, "history.db")); Store.Initialize(); Writer = new(Store);
                Future = new(Store); Query = new(Store, Writer);
                Execute("UPDATE unattended_safety_state SET unattended_mode_code='enabled';");
                audit = new AuditLogger(Path.Combine(Root, "audit.jsonl"));
                Engine = new(new RetentionSharedOutputPathTests.QuietAudit(), null, null, null, null, null, new Devices(),
                    futureWindowStartSafetyInterlock: new StandingLeaseStartSafetyInterlock(),
                    futureWindowStartSafetyValidator: (ticket, now) => Future.ValidateAtBackendStart(ticket, now))
                    { CountdownSteps = 0, DisableDeadlineWatchdogForTests = true };
                Engine.CapturePlanFactoryForTests = cfg => CaptureBackendSelector.BuildPlan(cfg, new Probe());
                Engine.FutureWindowIdentityForTests = () => (Sid, Session, 1);
                Engine.FutureWindowPinnedTargetValidatorForTests = (hold, scope) => hold.Snapshot.ProcessUserSid == scope.CurrentUserSid ? null : "synthetic_target_mismatch";
                Engine.BackendFactory = cfg =>
                {
                    FactoryCalls++;
                    if (cfg.IsSystemLoopback) { Av = new(new AvWorkers(this), new ExternalProcessRunner(), new TempRetentionPolicy(Root)); return (Av, "wgc-window-av-split"); }
                    return (NewWgc(), "wgc-continuous");
                };
                Engine.ConfigureOrdinaryRecordingHistory(Writer); Engine.SetTray(Tray);
                Coordinator = new(Store, Engine, Tray, audit, new StandingLeaseStartSafetyInterlock(),
                    approvalUi: Approval, recoverPending: false, approvalDesktopAvailableForTests: () => true);
            }
            catch (Exception primary)
            {
                try { Dispose(); }
                catch (Exception cleanup) { throw new AggregateException(primary, cleanup); }
                throw;
            }
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
        internal FutureWindowAuthorizationScope Pending(string? id = null, bool audio = false, bool realExecutable = false)
        {
            id ??= "fwa_" + Guid.NewGuid().ToString("N");
            var exe = new FutureWindowExecutableIdentity(1, @"C:\Synthetic\player.exe", "synthetic-executable", new string('a', 64), null, null);
            if (realExecutable)
            {
                Assert.True(FutureWindowProcessIdentity.TryResolveExecutable(Environment.ProcessPath!, out var resolved, out var reason), reason);
                exe = resolved!;
            }
            var scope = new FutureWindowAuthorizationScope(id, "key-" + id, new string('b', 64), Sid, Session, exe,
                audio ? "task347-output" : null, audio ? "Synthetic output" : null, 1, 300, Root, id + ".mp4", "pending",
                DateTimeOffset.UtcNow.AddSeconds(-3), null, null, null, null, 0);
            Assert.Equal(FutureWindowCreateDisposition.Created, Future.CreateOrGet(scope).Disposition); return scope;
        }
        internal FutureWindowStartCommitReceipt Claim(FutureWindowAuthorizationScope scope, bool alreadyApproved = false)
        {
            if (!alreadyApproved) Future.Approve(scope.AuthorizationId, Sid, Session, "approval-" + scope.AuthorizationId, DateTimeOffset.UtcNow.AddSeconds(-2));
            var now = DateTimeOffset.UtcNow;
            var process = new FutureWindowProcessSnapshot("window_42", (nint)42, 7, now.UtcDateTime.ToFileTimeUtc(), Sid, 1,
                scope.ExecutableIdentity.CanonicalPath, scope.ExecutableIdentity);
            return Future.TryCommitStart(scope.AuthorizationId, Sid, Session, process, now, out var reason) ??
                throw new InvalidOperationException("Synthetic exact start rejected: " + reason);
        }
        internal async Task<Recording> Start(FutureWindowStartCommitReceipt start, bool audio = false)
        {
            var cfg = new CaptureConfig {
                SourceKind = "window", WindowHandle = (nint)42, WindowTitle = "Synthetic window", WindowProcessId = 7,
                Bounds = (0, 0, 64, 64), WindowSurfaceBounds = (0, 0, 64, 64), Fps = 15, Quality = "medium", RequireWindowSurface = true, DurationSeconds = 1,
                CountdownSeconds = 0, OutputPath = start.Authorization.OutputFilePath, OutputConflictPolicy = "fail_if_exists",
                AudioSourceKind = audio ? AudioCaptureSourceKind.SystemLoopback : AudioCaptureSourceKind.None,
                SystemLoopbackEndpoint = audio ? "task347-output" : null, SystemLoopbackEndpointName = audio ? "Synthetic output" : null
            };
            var plan = Engine.CapturePlanFactoryForTests!(cfg);
            using var hold = new FutureWindowIdentityHold(start.Process, new SafeProcessHandle(IntPtr.Zero, false), new SafeFileHandle(IntPtr.Zero, false));
            var result = Engine.StartFutureWindowCapture(start, cfg, plan, hold, Tray);
            Assert.True(result.Accepted, result.ReasonCode);
            var records = (System.Collections.IDictionary)typeof(RecordingEngine).GetField("_recs", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(Engine)!;
            var rec = (Recording)records[start.RunId]!;
            await Until(() => rec.State == RecState.recording || rec.IsFinalized);
            Assert.Equal(RecState.recording, rec.State); Assert.Equal(1, FactoryCalls); Assert.Equal(1, Process!.BeginObserved);
            Assert.False(rec.IsOrdinaryLocalRequest); return rec;
        }
        internal FutureWindowAuthorizationRow Row(string id) => Future.Get(id, Sid, Session)!;
        internal PublicationIdentityAnchor? Anchor(string id)
        {
            using var c = Store.OpenReadOnlyConnection(); using var t = c.BeginTransaction(System.Data.IsolationLevel.Serializable, deferred: true);
            var row = SqliteFutureWindowAuthorizationRepository.ReadById(c, t, id, Sid, Session)!;
            var anchor = FutureWindowPublicationStorage.Read(c, t, new[] { row })[id]; t.Commit(); return anchor;
        }
        internal UnifiedHistoryPage Page(int limit = 100, string? cursor = null, string? sid = null, string? session = null) =>
            Query.GetPage(sid ?? Sid, session ?? Session, limit, new("future_window"), cursor);
        internal void Execute(string sql)
        { using var c = Store.OpenConnection(); using var cmd = c.CreateCommand(); cmd.CommandText = sql; cmd.ExecuteNonQuery(); }
        internal void Corrupt(string sql)
        {
            using var c = Store.OpenConnection(); using var cmd = c.CreateCommand();
            string? transition = null;
            if (sql.StartsWith("UPDATE future_window_authorizations SET ", StringComparison.Ordinal))
            {
                cmd.CommandText = "SELECT sql FROM sqlite_master WHERE name='trg_future_window_authorization_transition';";
                transition = (string)cmd.ExecuteScalar()!;
            }
            cmd.CommandText = "PRAGMA foreign_keys=OFF; PRAGMA ignore_check_constraints=ON; " +
                (transition is null ? "" : "DROP TRIGGER trg_future_window_authorization_transition; ") + sql +
                (transition is null ? "" : transition + ";");
            cmd.ExecuteNonQuery();
        }
        internal long Scalar(string sql)
        { using var c = Store.OpenConnection(); using var cmd = c.CreateCommand(); cmd.CommandText = sql; return (long)cmd.ExecuteScalar()!; }
        internal string Snapshot() => UnifiedHistoryProfileFilterTests.RawSnapshot(Store.DatabasePath);
        internal void StartApi()
        {
            ApiKeyAuth.InitializeForTesting(Root);
            Api = new(Engine, new RetentionSharedOutputPathTests.QuietAudit(), Tray, null, null, null, null, null, null, null, null, null, 0,
                unifiedHistoryGateway: new SqliteUnifiedHistoryGateway(Query, () => (Sid, Session))); Api.Start();
        }
        internal HttpClient Client(bool authorized = true)
        {
            var c = new HttpClient(new HttpClientHandler { UseProxy = false }) { BaseAddress = new($"http://127.0.0.1:{Api!.BoundPort}"), Timeout = TimeSpan.FromSeconds(10) };
            if (authorized) c.DefaultRequestHeaders.Add("X-Agent-Recorder-Key", ApiKeyAuth.CurrentApiKey); return c;
        }
        public void Dispose()
        {
            if (disposed) return; disposed = true;
            var errors = new List<Exception>(); void Clean(Action a) { try { a(); } catch (Exception e) { errors.Add(e); } }
            Clean(() => Api?.Stop()); Clean(() => Coordinator?.Dispose()); Clean(() => Engine?.Dispose());
            Clean(() => Av?.Dispose()); foreach (var backend in backends) Clean(backend.Dispose);
            SyntheticProcess[] owned; lock (processes) owned = processes.ToArray();
            foreach (var process in owned) Clean(() => { if (process.Worker is { } w) { if (!w.IsCompleted) process.KillEntireTree(); w.WaitAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult(); } });
            Clean(() => { if (Writer is not null && !Writer.DrainAndDispose(TimeSpan.FromSeconds(5))) throw new IOException("Owned writer did not drain"); });
            Clean(() => SystemQuery.SetDisplayProvider(displays)); Clean(() => SystemQuery.SetDisplayTopologyProvider(topology)); Clean(() => SystemQuery.SetWindowProvider(windows));
            Clean(() => RecordingPreflightChecker.StorageCapacityProvider = capacity); Clean(() => RecordingPreflightChecker.AudioHelperPathResolver = helper);
            Clean(() => RecordingPreflightChecker.AudioHelperProbeRunner = probe);
            Clean(() => { if (priorDir is null) DataDirResolver.ClearOverride(); else DataDirResolver.SetOverride(priorDir); });
            foreach (var pair in environment) Clean(() => Environment.SetEnvironmentVariable(pair.Key, pair.Value));
            Clean(() => ApiKeyAuth.ResetForTesting(null)); Clean(SqliteConnection.ClearAllPools);
            if (errors.Count == 0) Clean(() => { Assert.Equal(OwnedParent, Path.GetDirectoryName(Path.GetFullPath(Root))); if (Directory.Exists(Root)) Directory.Delete(Root, true); });
            if (errors.Count != 0) throw new AggregateException(errors);
        }
    }
    private sealed class ApprovalUi : IFutureWindowAuthorizationApprovalUi
    {
        internal int Calls; internal readonly TaskCompletionSource Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task<FutureWindowApprovalOutcome> ShowAsync(string id, FutureWindowAuthorizationApprovalDetails details,
            IUiTextProvider text, Action<string, object> audit, CancellationToken token)
        { Calls++; await Release.Task.WaitAsync(token); return FutureWindowApprovalOutcome.Approved; }
    }
    private static async Task Until(Func<bool> p, CancellationToken token = default)
    {
        var end = DateTime.UtcNow.AddSeconds(15); while (!p() && DateTime.UtcNow < end) await Task.Delay(10, token);
        Assert.True(p(), "Owned operation did not settle within 15 seconds.");
    }
    private static PublicationIdentityAnchor Observe(string path)
    {
        using var s = File.OpenRead(path); var observed = PublicationHandleObservation.Read(WindowsFileMetadataApi.Instance, s.SafeFileHandle, path)!;
        return new(path, s.Length, observed.Snapshot.Identity!);
    }
    private static async Task<PublicationReceipt> Publish(Fixture f, FutureWindowStartCommitReceipt start)
    {
        if (!File.Exists(f.Clip)) await f.Prepare();
        var staging = Path.Combine(f.Root, Guid.NewGuid().ToString("N") + ".staging"); File.Copy(f.Clip, staging);
        var result = await StagingToFinalPublisher.Instance.PublishAsync(staging, start.Authorization.OutputFilePath);
        Assert.True(result.Success); Assert.NotNull(result.Receipt); return result.Receipt!;
    }
    private static bool Complete(Fixture f, FutureWindowStartCommitReceipt start, PublicationReceipt? receipt, long? size = null) =>
        f.Future.CompleteRun(start.Authorization.AuthorizationId, start.RunId, true, "", start.Authorization.OutputFilePath,
            size ?? receipt?.Anchor.Length ?? 12, 1000, DateTimeOffset.UtcNow, receipt, start);

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task ApprovedConsumedRunUsesOriginalSessionOrFinalMuxReceipt(bool audio)
    {
        using var f = new Fixture(); await f.Prepare(audio);
        var pending = f.Pending(audio: audio, realExecutable: true);
        Assert.Null(f.Future.TryCommitStart(pending.AuthorizationId, f.Sid, f.Session,
            new("window_42", (nint)42, 7, DateTime.UtcNow.ToFileTimeUtc(), f.Sid, 1,
                pending.ExecutableIdentity.CanonicalPath, pending.ExecutableIdentity), DateTimeOffset.UtcNow, out _));
        Assert.Equal(0, f.FactoryCalls); Assert.Null(f.Process);
        var approval = f.Coordinator.StartApprovalFlightForTests(pending.AuthorizationId);
        await Until(() => f.Approval.Calls == 1); Assert.Equal("pending", f.Row(pending.AuthorizationId).StatusCode);
        Assert.Equal(0, f.FactoryCalls); f.Approval.Release.TrySetResult(); await approval.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal("active", f.Row(pending.AuthorizationId).StatusCode);
        var start = f.Claim(pending, alreadyApproved: true); var rec = await f.Start(start, audio);
        Assert.Null(f.Engine.ReadFutureWindowPublication(start));
        var tracking = f.Coordinator.TrackRunForTests(start); f.Process!.Finish.TrySetResult();
        await Until(() => rec.IsFinalized); Assert.Equal(RecState.completed, rec.State);
        var receipt = rec.LastMeta!.PublicationReceipt; Assert.NotNull(receipt);
        var media = f.Engine.ReadFutureWindowPublication(start)!; Assert.Same(receipt, media.Receipt);
        Assert.Null(f.Engine.ReadFutureWindowPublication(new(start.Authorization, start.Process, start.RunId,
            start.ProofId, start.ProofNonce, start.CommittedAtUtc))); // exact original handoff object
        await tracking.WaitAsync(TimeSpan.FromSeconds(10)); var row = f.Row(pending.AuthorizationId);
        Assert.Equal("completed", row.StatusCode); Assert.Equal(receipt!.Anchor, f.Anchor(pending.AuthorizationId));
        Assert.Equal(Observe(row.OutputPath!), f.Anchor(pending.AuthorizationId));
        Assert.Empty(f.Writer.ReadPage(100, null).Items); Assert.Equal(0, f.Scalar("SELECT count(*) FROM ordinary_recording_bundles;"));
        if (audio)
        {
            Assert.Null(f.Wgc!.Stop().PublicationReceipt); Assert.True(rec.LastMeta.HasAudioStream);
            Assert.True(f.Audio!.StopCalled); Assert.True(f.Audio.WaitForExitCalled); Assert.True(f.Audio.IsFileHandleReleased);
        }
        Assert.Equal("completed", Assert.Single(f.Page().Items).Status);
        var persisted = f.Anchor(pending.AuthorizationId); Assert.True(f.Writer.DrainAndDispose(TimeSpan.FromSeconds(5)));
        f.Store.Initialize(); f.Future = new(f.Store); f.Writer = new(f.Store); f.Query = new(f.Store, f.Writer);
        Assert.Equal(persisted, f.Anchor(pending.AuthorizationId)); Assert.Equal("completed", Assert.Single(f.Page().Items).Status);
        output.WriteLine($"Synthetic approval/target/events -> consumed future proof -> formal WGC {(audio ? "intermediate+AV final mux" : "silent final publisher")} -> exact Core receipt -> coordinator -> same-transaction V40 anchor -> reopened history. No native screen/audio captured.");
    }

    [Theory]
    [InlineData("path")] [InlineData("length")] [InlineData("authorization")] [InlineData("run")]
    public async Task ForeignPublicationCannotCompleteTheFrozenRun(string mismatch)
    {
        using var f = new Fixture(); var start = f.Claim(f.Pending()); var receipt = await Publish(f, start);
        var before = f.Snapshot();
        if (mismatch is "authorization" or "run")
            Assert.False(f.Future.CompleteRun(mismatch == "authorization" ? "foreign" : start.Authorization.AuthorizationId,
                mismatch == "run" ? "foreign" : start.RunId, true, "", receipt.Anchor.Path, receipt.Anchor.Length, 1000, DateTimeOffset.UtcNow, receipt));
        else Assert.Throws<InvalidDataException>(() => f.Future.CompleteRun(start.Authorization.AuthorizationId, start.RunId,
            true, "", mismatch == "path" ? Path.Combine(f.Root, "foreign.mp4") : receipt.Anchor.Path,
            receipt.Anchor.Length + (mismatch == "length" ? 1 : 0), 1000, DateTimeOffset.UtcNow, receipt));
        Assert.Equal(before, f.Snapshot()); Assert.Null(f.Anchor(start.Authorization.AuthorizationId));
    }

    [Fact]
    public async Task SameFactsAreIdempotentAndLateConflictsCannotOverwriteWinner()
    {
        using var f = new Fixture(); var start = f.Claim(f.Pending()); var receipt = await Publish(f, start);
        Assert.True(Complete(f, start, receipt)); var before = f.Snapshot(); Assert.True(Complete(f, start, receipt));
        Assert.False(Complete(f, start, receipt, receipt.Anchor.Length + 1)); Assert.False(Complete(f, start, null, receipt.Anchor.Length));
        Assert.False(f.Future.CompleteRun(start.Authorization.AuthorizationId, start.RunId, false, "late_failed", null, null, null, DateTimeOffset.UtcNow));
        Assert.Equal(before, f.Snapshot()); Assert.Equal(receipt.Anchor, f.Anchor(start.Authorization.AuthorizationId));
    }

    [Theory]
    [InlineData("revoked")] [InlineData("failed")] [InlineData("started_unknown")] [InlineData("pending")]
    public async Task NonWinnerCannotAcquireAnAnchor(string status)
    {
        using var f = new Fixture(); var pending = f.Pending(); var start = f.Claim(pending); var receipt = await Publish(f, start);
        if (status == "revoked") f.Future.Revoke(pending.AuthorizationId, f.Sid, f.Session, DateTimeOffset.UtcNow);
        if (status == "failed") f.Future.CompleteRun(pending.AuthorizationId, start.RunId, false, "recording_failed", null, null, null, DateTimeOffset.UtcNow);
        if (status == "started_unknown") f.Future.RecoverUncertainStarts(DateTimeOffset.UtcNow);
        if (status == "pending") { pending = f.Pending(); start = new(pending, start.Process, start.RunId, start.ProofId, start.ProofNonce, start.CommittedAtUtc); }
        var before = f.Snapshot(); Assert.False(Complete(f, start, receipt)); Assert.Equal(before, f.Snapshot()); Assert.Null(f.Anchor(pending.AuthorizationId));
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task RevocationAndCompletionHaveOneTransactionalWinner(bool revokeFirst)
    {
        using var f = new Fixture(); var start = f.Claim(f.Pending()); var receipt = await Publish(f, start);
        using var release = new ManualResetEventSlim(); var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!revokeFirst) f.Future.BeforePublicationCommitForTests = (_, _) => { entered.TrySetResult(); Assert.True(release.Wait(TimeSpan.FromSeconds(5))); };
        if (revokeFirst) f.Future.Revoke(start.Authorization.AuthorizationId, f.Sid, f.Session, DateTimeOffset.UtcNow);
        var completing = Task.Run(() => Complete(f, start, receipt));
        Task? revoking = null;
        if (!revokeFirst)
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            revoking = Task.Run(() => f.Future.Revoke(start.Authorization.AuthorizationId, f.Sid, f.Session, DateTimeOffset.UtcNow));
            release.Set();
        }
        var won = await completing; if (revoking is not null) await revoking;
        Assert.Equal(!revokeFirst, won); Assert.Equal(revokeFirst ? "revoked" : "completed", f.Row(start.Authorization.AuthorizationId).StatusCode);
        Assert.Equal(revokeFirst ? null : receipt.Anchor, f.Anchor(start.Authorization.AuthorizationId));
    }

    [Theory]
    [InlineData("anchor_write")] [InlineData("commit")]
    public async Task RealWriteOrCommitFailureRollsBackBothFacts(string fault)
    {
        using var f = new Fixture(); var start = f.Claim(f.Pending()); var receipt = await Publish(f, start); var before = f.Snapshot();
        if (fault == "anchor_write") f.Execute("CREATE TRIGGER owned_anchor_failure BEFORE UPDATE ON future_window_publication_anchors BEGIN SELECT RAISE(ABORT,'owned_write_fault'); END;");
        else f.Future.BeforePublicationCommitForTests = (c, t) =>
        {
            using var cmd = c.CreateCommand(); cmd.Transaction = t;
            cmd.CommandText = "PRAGMA defer_foreign_keys=ON; INSERT INTO future_window_publication_anchors(authorization_id) VALUES('owned_orphan');";
            cmd.ExecuteNonQuery(); // actual SQLite COMMIT constraint failure
        };
        var failure = Assert.Throws<SqliteException>(() => Complete(f, start, receipt)); Assert.Equal(19, failure.SqliteErrorCode);
        if (fault == "anchor_write") f.Execute("DROP TRIGGER owned_anchor_failure;");
        Assert.Equal(before, f.Snapshot()); Assert.Equal("used", f.Row(start.Authorization.AuthorizationId).StatusCode);
        Assert.Null(f.Anchor(start.Authorization.AuthorizationId)); f.Future.BeforePublicationCommitForTests = null;
        Assert.True(Complete(f, start, receipt)); Assert.Equal(receipt.Anchor, f.Anchor(start.Authorization.AuthorizationId));
    }

    [Fact]
    public async Task CoordinatorReportsDurableFailureAndNeverClaimsCompleted()
    {
        using var f = new Fixture(); await f.Prepare(); var start = f.Claim(f.Pending()); var rec = await f.Start(start);
        f.Execute("CREATE TRIGGER owned_anchor_failure BEFORE UPDATE ON future_window_publication_anchors BEGIN SELECT RAISE(ABORT,'owned_fault'); END;");
        var tracking = f.Coordinator.TrackRunForTests(start); f.Process!.Finish.TrySetResult();
        await Assert.ThrowsAsync<SqliteException>(() => tracking); Assert.Equal(RecState.completed, rec.State);
        Assert.Equal("used", f.Row(start.Authorization.AuthorizationId).StatusCode); Assert.Null(f.Anchor(start.Authorization.AuthorizationId));
        var audit = File.ReadAllText(Path.Combine(f.Root, "audit.jsonl")); Assert.Contains("future_window_persistence_unavailable", audit);
        Assert.DoesNotContain("owned_fault", audit); Assert.Empty(f.Writer.ReadPage(100, null).Items);
    }

    [Fact]
    public async Task UnavailableNativeIdentityKeepsSuccessWithExplicitUnknown()
    {
        using var f = new Fixture { UnknownNative = true }; await f.Prepare(); var start = f.Claim(f.Pending()); var rec = await f.Start(start);
        var tracking = f.Coordinator.TrackRunForTests(start); f.Process!.Finish.TrySetResult(); await tracking.WaitAsync(TimeSpan.FromSeconds(15));
        Assert.Equal(RecState.completed, rec.State); Assert.Null(rec.LastMeta!.PublicationReceipt);
        Assert.Equal("completed", f.Row(start.Authorization.AuthorizationId).StatusCode); Assert.Null(f.Anchor(start.Authorization.AuthorizationId));
        Assert.Equal(1, f.Scalar("SELECT count(*) FROM future_window_publication_anchors WHERE run_id IS NOT NULL AND observation_version IS NULL;"));
        Assert.Equal("completed", Assert.Single(f.Page().Items).Status);
    }

    [Fact]
    public async Task ReopenedFactsKeepOriginalIdentityAfterSameSizePathReplacement()
    {
        using var f = new Fixture(); var start = f.Claim(f.Pending()); var receipt = await Publish(f, start); Assert.True(Complete(f, start, receipt));
        var before = f.Snapshot(); var path = receipt.Anchor.Path; File.Move(path, path + ".original"); File.Copy(f.Clip, path);
        Assert.Equal(receipt.Anchor.Length, new FileInfo(path).Length); Assert.NotEqual(receipt.Anchor.Identity, Observe(path).Identity);
        f.Future = new(f.Store); Assert.Equal(receipt.Anchor, f.Anchor(start.Authorization.AuthorizationId));
        using var block = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None);
        Assert.Equal("completed", Assert.Single(f.Page().Items).Status); Assert.Equal(before, f.Snapshot());
    }

    [Theory]
    [InlineData("DELETE FROM future_window_publication_anchors;")]
    [InlineData("UPDATE future_window_publication_anchors SET run_id='foreign';")]
    [InlineData("UPDATE future_window_publication_anchors SET output_path='C:\\foreign.mp4';")]
    [InlineData("UPDATE future_window_publication_anchors SET output_size_bytes=output_size_bytes+1;")]
    [InlineData("UPDATE future_window_publication_anchors SET output_size_bytes=X'01';")]
    [InlineData("UPDATE future_window_publication_anchors SET observation_version=2;")]
    [InlineData("UPDATE future_window_publication_anchors SET source='foreign';")]
    [InlineData("UPDATE future_window_publication_anchors SET volume_id=zeroblob(8);")]
    [InlineData("UPDATE future_window_publication_anchors SET file_id=zeroblob(16);")]
    [InlineData("UPDATE future_window_publication_anchors SET file_id=X'01';")]
    [InlineData("UPDATE future_window_publication_anchors SET observation_version=NULL;")]
    [InlineData("UPDATE future_window_publication_anchors SET volume_id='not_blob';")]
    [InlineData("UPDATE future_window_publication_anchors SET run_id=NULL,output_path=NULL,output_size_bytes=NULL,observation_version=NULL,source=NULL,volume_id=NULL,file_id=NULL;")]
    [InlineData("DROP INDEX idx_future_window_publication_authorization;")]
    [InlineData("DROP INDEX idx_future_window_publication_authorization; CREATE INDEX idx_future_window_publication_authorization ON future_window_publication_anchors(run_id COLLATE BINARY);")]
    [InlineData("DROP TRIGGER future_window_publication_initial;")]
    [InlineData("DELETE FROM schema_migrations WHERE version=40;")]
    [InlineData("UPDATE schema_migrations SET name='foreign' WHERE version=40;")]
    [InlineData("UPDATE schema_migrations SET definition_checksum='0000000000000000000000000000000000000000000000000000000000000000' WHERE version=40;")]
    [InlineData("UPDATE future_window_publication_boundary SET legacy_authorization_rowid=-1;")]
    [InlineData("UPDATE future_window_publication_boundary SET legacy_authorization_rowid=9223372036854775807;")]
    [InlineData("UPDATE future_window_publication_boundary SET legacy_authorization_rowid=X'01';")]
    [InlineData("DELETE FROM future_window_publication_boundary;")]
    [InlineData("UPDATE schema_migrations SET name=CAST(name AS BLOB) WHERE version=40;")]
    [InlineData("UPDATE schema_migrations SET definition_checksum=CAST(definition_checksum AS BLOB) WHERE version=40;")]
    [InlineData("UPDATE future_window_authorizations SET output_path=CAST(output_path AS BLOB);")]
    [InlineData("UPDATE future_window_authorizations SET output_size_bytes=output_size_bytes+0.5;")]
    [InlineData("UPDATE future_window_authorizations SET run_id=CAST(run_id AS BLOB);")]
    [InlineData("UPDATE future_window_authorizations SET proof_nonce=CAST(proof_nonce AS BLOB);")]
    public async Task ModernDamagedFactsAreUnavailableWithoutRepair(string sql)
    {
        using var f = new Fixture(); var start = f.Claim(f.Pending()); var receipt = await Publish(f, start); Assert.True(Complete(f, start, receipt));
        f.Corrupt(sql); var before = f.Snapshot(); Assert.Throws<UnifiedHistoryUnavailableException>(() => f.Page());
        Assert.Equal(before, f.Snapshot());
    }

    [Theory]
    [InlineData("pending")] [InlineData("failed")] [InlineData("revoked")] [InlineData("started_unknown")]
    public void OrphanEvidenceOnNonCompletedRowFailsClosed(string status)
    {
        using var f = new Fixture(); var pending = f.Pending(); FutureWindowStartCommitReceipt? start = null;
        if (status != "pending") start = f.Claim(pending);
        if (status == "failed") f.Future.CompleteRun(pending.AuthorizationId, start!.RunId, false, "recording_failed", null, null, null, DateTimeOffset.UtcNow);
        if (status == "revoked") f.Future.Revoke(pending.AuthorizationId, f.Sid, f.Session, DateTimeOffset.UtcNow);
        if (status == "started_unknown") f.Future.RecoverUncertainStarts(DateTimeOffset.UtcNow);
        f.Corrupt("UPDATE future_window_publication_anchors SET run_id='orphan',output_path='C:\\orphan.mp4',output_size_bytes=12;");
        var before = f.Snapshot(); Assert.Throws<UnifiedHistoryUnavailableException>(() => f.Page()); Assert.Equal(before, f.Snapshot());
    }

    [Fact]
    public void GenuineV39UpgradeKeepsLegacyUnknownAndIsIdempotent()
    {
        using var f = new Fixture(); var path = Path.Combine(f.Root, "v39.db");
        using (var c = new SqliteConnection($"Data Source={path};Pooling=False"))
        {
            c.Open(); SqliteOutputPathCollation.Register(c); using var cmd = c.CreateCommand();
            foreach (var m in SqliteSchemaCatalog.Migrations.Where(m => m.Version <= 39))
            {
                cmd.CommandText = m.Definition; cmd.ExecuteNonQuery(); cmd.CommandText = "INSERT INTO schema_migrations VALUES($v,$n,$c,$at);";
                cmd.Parameters.AddWithValue("$v", m.Version); cmd.Parameters.AddWithValue("$n", m.Name); cmd.Parameters.AddWithValue("$c", m.Checksum);
                cmd.Parameters.AddWithValue("$at", 1000 + m.Version); cmd.ExecuteNonQuery(); cmd.Parameters.Clear();
            }
        }
        var scope = f.Pending("old"); var start = f.Claim(scope); Assert.True(Complete(f, start, null));
        using (var c = new SqliteConnection($"Data Source={path};Pooling=False")) using (var cmd = c.CreateCommand())
        {
            c.Open(); SqliteOutputPathCollation.Register(c); cmd.CommandText = "ATTACH DATABASE $seed AS seed;"; cmd.Parameters.AddWithValue("$seed", f.Store.DatabasePath);
            cmd.ExecuteNonQuery(); cmd.Parameters.Clear();
            cmd.CommandText = "INSERT INTO future_window_authorizations SELECT * FROM seed.future_window_authorizations;";
            cmd.ExecuteNonQuery();
        }
        var legacy = new SqliteOperationalStore(path); var repo = new SqliteFutureWindowAuthorizationRepository(legacy);
        var before = UnifiedHistoryProfileFilterTests.RawSnapshot(path, schemaThrough: 39); legacy.Initialize();
        Assert.Equal(before, UnifiedHistoryProfileFilterTests.RawSnapshot(path, schemaThrough: 39));
        var once = UnifiedHistoryProfileFilterTests.RawSnapshot(path); legacy.Initialize(); Assert.Equal(once, UnifiedHistoryProfileFilterTests.RawSnapshot(path));
        using var writer = new SqliteOrdinaryRecordingHistoryStore(legacy); var query = new UnifiedHistoryQueryService(legacy, writer);
        Assert.Equal("completed", Assert.Single(query.GetPage(f.Sid, f.Session, 100, new("future_window")).Items).Status);
        using var connection = legacy.OpenReadOnlyConnection(); using var transaction = connection.BeginTransaction(System.Data.IsolationLevel.Serializable, deferred: true);
        Assert.Null(FutureWindowPublicationStorage.Read(connection, transaction, new[] { repo.Get("old", f.Sid, f.Session)! })["old"]);
        Assert.Equal(1L, SqliteSchemaV40.Validate(connection, transaction)); transaction.Commit();
        Assert.Equal(41, SqliteOperationalStore.CurrentSchemaVersion); Assert.NotNull(start);
    }

    [Fact]
    public void HundredRowsUseOneIndexedBoundedBatchInOriginalSnapshot()
    {
        using var f = new Fixture(); for (var i = 0; i < 100; i++) { var start = f.Claim(f.Pending("fwa_" + i)); Assert.True(Complete(f, start, null)); }
        var batches = 0;
        f.Query.FuturePublicationCommandForTests = (c, t, sql, args) =>
        {
            batches++; Assert.Equal(100, args.Count); Assert.Contains("LIMIT 101", sql);
            using var cmd = c.CreateCommand(); cmd.Transaction = t; cmd.CommandText = "EXPLAIN QUERY PLAN " + sql;
            foreach (var arg in args) cmd.Parameters.AddWithValue(arg.Key, arg.Value);
            using var reader = cmd.ExecuteReader(); var plans = new List<string>(); while (reader.Read()) plans.Add(reader.GetString(3));
            Assert.Contains(plans, p => p.Contains("SEARCH a USING INDEX " + SqliteSchemaV40.IndexName));
            Assert.DoesNotContain(plans, p => p.StartsWith("SCAN a", StringComparison.Ordinal) || p.StartsWith("SCAN f", StringComparison.Ordinal));
            output.WriteLine(string.Join(" | ", plans));
        };
        Assert.Equal(100, f.Page().Items.Count); Assert.Equal(1, batches);
    }

    [Theory]
    [InlineData("insert")] [InlineData("delete_marker")] [InlineData("complete")]
    public void WalUpdatesCannotMixPageAndPrivateFacts(string mutation)
    {
        using var f = new Fixture(); var scope = f.Pending(); var start = f.Claim(scope);
        if (mutation != "complete") Assert.True(Complete(f, start, null));
        f.Query.SnapshotEstablishedForTests = () =>
        {
            if (mutation == "insert") f.Pending();
            if (mutation == "delete_marker") f.Execute("DELETE FROM future_window_publication_anchors;");
            if (mutation == "complete") Assert.True(Complete(f, start, null));
        };
        Assert.Equal(mutation == "complete" ? "used" : "completed", Assert.Single(f.Page().Items).Status);
        f.Query.SnapshotEstablishedForTests = null;
        if (mutation == "delete_marker") Assert.Throws<UnifiedHistoryUnavailableException>(() => f.Page());
        else Assert.Equal(mutation == "insert" ? 2 : 1, f.Page().Items.Count);
    }

    [Fact]
    public async Task AuthenticatedHttpPreservesDtoCursorIdentityAndUnavailableBoundary()
    {
        using var f = new Fixture(); var start = f.Claim(f.Pending()); var receipt = await Publish(f, start); Assert.True(Complete(f, start, receipt)); f.Pending();
        f.StartApi(); using var unauthorized = f.Client(false);
        using var denied = await unauthorized.GetAsync("/api/v1/history?source=future_window"); Assert.Equal(HttpStatusCode.Unauthorized, denied.StatusCode);
        using var client = f.Client(); using var block = new FileStream(receipt.Anchor.Path, FileMode.Open, FileAccess.Read, FileShare.None);
        using var response = await client.GetAsync("/api/v1/history?source=future_window&limit=1"); Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var raw = await response.Content.ReadAsStringAsync(); using var doc = JsonDocument.Parse(raw); var cursor = doc.RootElement.GetProperty("data").GetProperty("next_cursor").GetString();
        Assert.NotNull(cursor); using var second = await client.GetAsync("/api/v1/history?source=future_window&limit=1&cursor=" + Uri.EscapeDataString(cursor!));
        Assert.Equal(HttpStatusCode.OK, second.StatusCode); var body = await second.Content.ReadAsStringAsync();
        foreach (var forbidden in new[] { "\"volume_id\"", "\"file_id\"", "PublicationReceipt", "observation_version", start.ProofNonce }) { Assert.DoesNotContain(forbidden, raw); Assert.DoesNotContain(forbidden, body); }
        Assert.Empty(f.Page(sid: "foreign").Items); Assert.Empty(f.Page(session: "foreign").Items);
        using var invalid = await client.GetAsync("/api/v1/history?source=future_window&cursor=bad"); Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        using var missing = await client.GetAsync("/api/v1/recording-history/" + start.RunId); Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        f.Corrupt("DELETE FROM future_window_publication_anchors WHERE run_id IS NOT NULL;");
        using var unavailable = await client.GetAsync("/api/v1/history?source=future_window"); Assert.Equal(HttpStatusCode.ServiceUnavailable, unavailable.StatusCode);
        var error = await unavailable.Content.ReadAsStringAsync(); Assert.DoesNotContain("SELECT", error); Assert.DoesNotContain("Modern future", error); Assert.DoesNotContain("file_id", error);
    }

    [Fact]
    public void ConstructionFaultRestoresProvidersResolversAndEnvironment()
    {
        var capacity = RecordingPreflightChecker.StorageCapacityProvider; var helper = RecordingPreflightChecker.AudioHelperPathResolver;
        var probe = RecordingPreflightChecker.AudioHelperProbeRunner; var data = DataDirResolver.HasOverride ? DataDirResolver.Resolve() : null;
        var environment = new[] { "AGENT_RECORDER_DATA_DIR", "AGENT_RECORDER_TEST_MODE", CaptureBackendSelector.WgcEnvVar }.ToDictionary(n => n, Environment.GetEnvironmentVariable);
        Assert.Throws<IOException>(() => new Fixture(failConstruction: true)); Assert.Same(capacity, RecordingPreflightChecker.StorageCapacityProvider);
        Assert.Same(helper, RecordingPreflightChecker.AudioHelperPathResolver); Assert.Same(probe, RecordingPreflightChecker.AudioHelperProbeRunner);
        Assert.Equal(data, DataDirResolver.HasOverride ? DataDirResolver.Resolve() : null);
        foreach (var pair in environment) Assert.Equal(pair.Value, Environment.GetEnvironmentVariable(pair.Key));
    }

    [Fact]
    public async Task FailedSessionDoesNotAcquireMediaOrIdentity()
    {
        using var f = new Fixture(); await f.Prepare(); var start = f.Claim(f.Pending()); var rec = await f.Start(start);
        f.Process!.FailMedia = true; var tracking = f.Coordinator.TrackRunForTests(start); f.Process.Finish.TrySetResult();
        await tracking.WaitAsync(TimeSpan.FromSeconds(15)); Assert.Equal(RecState.failed, rec.State);
        Assert.Null(f.Engine.ReadFutureWindowPublication(start)); Assert.Equal("failed", f.Row(start.Authorization.AuthorizationId).StatusCode);
        Assert.Null(f.Anchor(start.Authorization.AuthorizationId)); Assert.Null(Assert.Single(f.Page().Items).OutputPath);
        Assert.Empty(f.Writer.ReadPage(100, null).Items);
    }

    [Theory]
    [InlineData("path")] [InlineData("length")] [InlineData("ordinary")]
    public async Task CorePrivateReadRejectsChangedOrForeignTerminalFacts(string mutation)
    {
        using var f = new Fixture(); await f.Prepare(); var start = f.Claim(f.Pending()); var rec = await f.Start(start);
        f.Process!.Finish.TrySetResult(); await Until(() => rec.IsFinalized); Assert.NotNull(f.Engine.ReadFutureWindowPublication(start));
        if (mutation == "path") rec.LastMeta!.OutputPath = Path.Combine(f.Root, "foreign.mp4");
        if (mutation == "length") rec.LastMeta!.SizeBytes++;
        if (mutation == "ordinary") rec.IsOrdinaryLocalRequest = true;
        if (mutation == "length") Assert.Throws<InvalidDataException>(() => f.Engine.ReadFutureWindowPublication(start));
        else Assert.Null(f.Engine.ReadFutureWindowPublication(start));
        Assert.Equal("used", f.Row(start.Authorization.AuthorizationId).StatusCode); Assert.Null(f.Anchor(start.Authorization.AuthorizationId));
    }

    [Fact]
    public async Task AnotherRealReceiptCannotSignThisAuthorizationAndUnknownWinnerCannotBeUpgraded()
    {
        using var f = new Fixture(); var first = f.Claim(f.Pending()); var receipt = await Publish(f, first);
        var second = f.Claim(f.Pending()); var secondReceipt = await Publish(f, second); var before = f.Snapshot();
        Assert.Throws<InvalidDataException>(() => Complete(f, first, secondReceipt)); Assert.Equal(before, f.Snapshot());
        Assert.True(Complete(f, first, null, receipt.Anchor.Length)); before = f.Snapshot();
        Assert.False(Complete(f, first, receipt)); Assert.Equal(before, f.Snapshot()); Assert.Null(f.Anchor(first.Authorization.AuthorizationId));
    }

    [Theory]
    [InlineData("missing")] [InlineData("run")] [InlineData("proof")] [InlineData("scope")] [InlineData("process")]
    public async Task NativeReceiptRequiresExactCommittedAuthorizationAndProofBinding(string mutation)
    {
        using var f = new Fixture(); var start = f.Claim(f.Pending()); var receipt = await Publish(f, start); var before = f.Snapshot();
        var binding = mutation == "missing" ? null : new FutureWindowStartCommitReceipt(
            mutation == "scope" ? start.Authorization with { RequestDigest = new string('c', 64) } : start.Authorization,
            mutation == "process" ? start.Process with { ProcessId = 99 } : start.Process,
            mutation == "run" ? "foreign-run" : start.RunId, start.ProofId,
            mutation == "proof" ? new string('f', 32) : start.ProofNonce, start.CommittedAtUtc);
        Assert.Throws<InvalidDataException>(() => f.Future.CompleteRun(start.Authorization.AuthorizationId, start.RunId, true, "",
            receipt.Anchor.Path, receipt.Anchor.Length, 1000, DateTimeOffset.UtcNow, receipt, binding));
        Assert.Equal(before, f.Snapshot()); Assert.Null(f.Anchor(start.Authorization.AuthorizationId));
    }
}
