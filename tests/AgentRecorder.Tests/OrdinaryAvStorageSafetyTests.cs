using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AgentRecorder.Api;
using AgentRecorder.App;
using AgentRecorder.Capture;
using AgentRecorder.Core;
using AgentRecorder.Core.Automation;
using AgentRecorder.Infrastructure;
using AgentRecorder.Logging;
using AgentRecorder.Persistence;
using AgentRecorder.Windows;
using Microsoft.Data.Sqlite;
using Xunit;

namespace AgentRecorder.Tests;

[Collection("NonParallel-AgentRecorderDataDir")]
public sealed class OrdinaryAvStorageSafetyTests
{
    private sealed class Provider : IStorageCapacityProvider
    {
        internal readonly ConcurrentQueue<string[]> Requests = new();
        internal Func<IReadOnlyList<string>, CancellationToken, Task<IReadOnlyList<StorageVolumeSample>>>? Query;
        internal long Free = 100L * 1024 * 1024 * 1024;
        internal string Volume = "approved-volume";
        public Task<IReadOnlyList<StorageVolumeSample>> QueryAsync(IReadOnlyList<string> paths, CancellationToken token)
        {
            Requests.Enqueue(paths.ToArray());
            return Query?.Invoke(paths, token) ?? Task.FromResult<IReadOnlyList<StorageVolumeSample>>(
                paths.Select(p => new StorageVolumeSample(p, Volume, Free)).ToArray());
        }
    }

    private sealed class Tray : ITrayContext, IRecordingFailureNotifier
    {
        internal readonly ConcurrentQueue<Action<ConfirmationDecision>> Confirmations = new();
        internal readonly ConcurrentQueue<string> Failures = new();
        public string HostMode => "tray";
        public bool SupportsRegionSelectionUi => true;
        public void RequestConfirmation(RecordingConfirmationPresentation p, Action<ConfirmationDecision> callback) => Confirmations.Enqueue(callback);
        public void RequestRegionSelection(int seconds, Action<string,int,int,int,int,string,string> callback) =>
            callback("selected", 0, 0, 64, 64, "display_1", "virtual_screen");
        public void SetRecording(RecordingUiPresentation p) { }
        public void SetIdle(RecordingUiPresentation p) { }
        public void SetAllIdle() { }
        public void ShowError(string text) { }
        public void ShowRecordingFailure(string id, string reason) => Failures.Enqueue(reason);
        internal void Approve(string? directory = null)
        {
            Assert.True(Confirmations.TryDequeue(out var callback));
            callback!(ConfirmationDecision.Approve(directory));
        }
    }

    private sealed class Audit(string path) : AuditLogger(path)
    {
        internal Action<string>? BeforeLog;
        public override void Log(string name,object payload) { BeforeLog?.Invoke(name);base.Log(name,payload); }
    }

    private sealed class Display : IFixedRegionProfileDisplayEnvironmentProvider
    {
        internal const string Fingerprint = "display-stable-v1-bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
        public IReadOnlyList<StandingLeaseDisplayMetadata> GetExecutionMetadata() => new[] {
            new StandingLeaseDisplayMetadata("display_1", Fingerprint, DisplayIdentityResolutionStatus.Resolved,
                new(0,0,1920,1080), 96,96,1920,1080,AuthorizedDisplayOrientation.Landscape) };
        public IReadOnlyList<DisplayTopologySnapshot> GetTopology() => new[] {
            new DisplayTopologySnapshot("display_1", Fingerprint, DisplayIdentityResolutionStatus.Resolved, new(0,0,1920,1080)) };
    }

    // Instance-local seams exercise the actual fixture without changing test scheduling.
    internal sealed class FixtureFaults
    {
        internal Action<string>? AtConstruction;
        internal Action<string>? AfterRelease;
        internal Func<string, FakeAudioHelperDeployment>? CreateHelper;
        internal readonly List<string> Acquired = new();
        internal readonly List<string> Released = new();
        internal readonly Dictionary<string, object> Resources = new();
    }

    internal static IDisposable CreateFixtureForTests(FixtureFaults? faults = null) => new Fixture(true, null, null, 0, faults);

    private sealed class Fixture : IDisposable
    {
        internal readonly string Root = Path.GetFullPath(Path.Combine(TestHelper.ProjectRoot, ".local-data/temp/task335", Guid.NewGuid().ToString("N")));
        internal readonly Provider Capacity = new();
        internal readonly Tray Tray = new();
        internal readonly StorageTestClock Clock = new();
        internal readonly SqliteOperationalStore Store;
        internal readonly SqliteOrdinaryRecordingHistoryStore History;
        internal readonly Audit Audit;
        internal readonly RecordingEngine Engine;
        internal readonly ApiServer Api;
        internal readonly HttpClient Client;
        internal readonly FakeAudioCaptureWorker Audio;
        internal readonly FakeVideoCaptureWorker Video;
        internal readonly AvSplitCaptureBackend Backend;
        internal readonly Factory Factory;
        private readonly FakeAudioHelperDeployment _helper;
        private readonly RecordingPreflightChecker.TryResolveAudioHelper _priorHelper = RecordingPreflightChecker.AudioHelperPathResolver;
        private readonly IStorageCapacityProvider _priorProvider = RecordingPreflightChecker.StorageCapacityProvider;
        private readonly string? _priorDir = DataDirResolver.HasOverride ? DataDirResolver.Resolve() : null;
        private readonly Dictionary<string,string?> _environment;
        private readonly List<(string Name, Action Release)> _resources = new();
        private readonly FixtureFaults? _faults;
        private int _disposed;
        private bool _topologyChanged, _authChanged;
        internal Fixture(bool ready = true, string? wav = null, string? video = null, int frameDelay = 0)
            : this(ready, wav, video, frameDelay, null) { }
        internal Fixture(bool ready, string? wav, string? video, int frameDelay, FixtureFaults? faults)
        {
            _faults = faults;
            _environment = new[] { "AGENT_RECORDER_TEST_MODE", "AGENT_RECORDER_DATA_DIR", CaptureBackendSelector.DisplayBackendEnvVar,
                CaptureBackendSelector.RegionBackendEnvVar }.ToDictionary(k => k, Environment.GetEnvironmentVariable);
            try
            {
            Directory.CreateDirectory(Root); DataDirResolver.SetOverride(Root);
            Environment.SetEnvironmentVariable("AGENT_RECORDER_DATA_DIR", Root);
            Environment.SetEnvironmentVariable("AGENT_RECORDER_TEST_MODE", null);
            Environment.SetEnvironmentVariable(CaptureBackendSelector.DisplayBackendEnvVar, null);
            Environment.SetEnvironmentVariable(CaptureBackendSelector.RegionBackendEnvVar, null);
            RecordingPreflightChecker.StorageCapacityProvider = Capacity;
            _faults?.AtConstruction?.Invoke("before-helper");
            _helper = _faults?.CreateHelper?.Invoke(Root) ?? new(Root);
            Own("helper", _helper.Dispose, _helper);
            _faults?.AtConstruction?.Invoke("after-helper");
            RecordingPreflightChecker.AudioHelperPathResolver = () => _helper.ExecutablePath;
            Audio = new(raiseAudioReadyOnStart: ready, holdFileOpen: wav is not null, holdFileOpenCopyFrom: wav);
            Own("audio", () => { if (Factory is null || !Factory.AudioTransferred) Audio.Dispose(); }, Audio);
            _faults?.AtConstruction?.Invoke("after-audio");
            Video = new(firstFrameDelayMs: frameDelay);
            Own("video", () => { if (Factory is null || !Factory.VideoTransferred) Video.Dispose(); }, Video);
            Factory = new(Audio, Video, video);
            Backend = new(Factory, new ExternalProcessRunner(), new TempRetentionPolicy(Root));
            Own("backend", Backend.Dispose, Backend);
            _faults?.AtConstruction?.Invoke("after-backend");
            _topologyChanged = true;
            SystemQuery.SetDisplayTopologyProvider(() => new() { new("display_1", "Synthetic", true,
                new(0,0,1920,1080), 1.0, Display.Fingerprint, DisplayIdentityResolutionStatus.Resolved) });
            Store = new(Path.Combine(Root, "history.db")); Store.Initialize(); History = new(Store);
            Own("history", History.Dispose, History);
            Audit = new(Path.Combine(Root,"audit.jsonl"));
            Engine = new(Audit, microphoneProvider: new Devices(), systemAudioEndpointProvider: new Devices(), bundleGenerator: new FfmpegRecordingBundleGenerator()) { CountdownSteps = 0, StorageMonitorDelay = Clock.Delay };
            Own("engine", Engine.Dispose, Engine);
            Engine.ConfigureOrdinaryRecordingHistory(History); Engine.SetTray(Tray);
            Engine.BackendFactory = cfg => (Backend, cfg.SourceKind == "region" ? "ffmpeg-region-av-split" : "ffmpeg-av-split");
            _authChanged = true;
            ApiKeyAuth.InitializeForTesting(Root);
            Api = new(Engine,Audit,Tray,null,null,null,null,null,null,null,null,null,0,
                profileManagementGateway: new SqliteFixedRegionProfileManagementGateway(Store), profileDisplayEnvironmentProvider: new Display());
            Own("api", () => {
                if (!Api.Stop(TimeSpan.FromSeconds(10))) throw new TimeoutException("Owned AV fixture API did not drain.");
            }, Api);
            Api.Start();
            _faults?.AtConstruction?.Invoke("after-api-start");
            Client = new(new HttpClientHandler { UseProxy = false }) {
                BaseAddress = new Uri($"http://127.0.0.1:{Api.BoundPort}"), Timeout = TimeSpan.FromSeconds(10) };
            Own("client", Client.Dispose, Client);
            Client.DefaultRequestHeaders.Add("X-Agent-Recorder-Key",ApiKeyAuth.CurrentApiKey);
            _faults?.AtConstruction?.Invoke("after-client");
            }
            catch (Exception original)
            {
                var failures = ReleaseAndRestore();
                if (failures.Count != 0)
                    throw new AggregateException("AV fixture construction and cleanup failed.", new[] { original }.Concat(failures));
                throw;
            }
        }
        private void Own(string name, Action release, object resource)
        {
            _resources.Add((name, () => { release(); _faults?.AfterRelease?.Invoke(name); }));
            _faults?.Acquired.Add(name);
            _faults?.Resources.Add(name, resource);
        }
        internal JsonObject Request(string source = "display", string? filename = null) => new() {
            ["mode"] = "video", ["source"] = source == "display" ? new JsonObject { ["type"]="display", ["display_id"]="display_1" }
                : new JsonObject { ["type"]="region", ["display_id"]="display_1", ["bounds"]=new JsonObject { ["x"]=0,["y"]=0,["width"]=64,["height"]=64 },["coordinate_space"]="virtual_screen" },
            ["video"]=new JsonObject { ["fps"]=15 }, ["duration_seconds"]=60, ["countdown_seconds"]=0,
            ["output"]=new JsonObject { ["directory"]=Root,["filename"]=filename ?? Guid.NewGuid().ToString("N")+".mp4",["conflict_policy"]="fail" } };
        internal async Task<HttpResponseMessage> Post(string path, object value) => await Client.PostAsync(path,
            new StringContent(value is JsonNode n ? n.ToJsonString() : JsonSerializer.Serialize(value),Encoding.UTF8,"application/json"));
        internal async Task<HttpResponseMessage> Create(string route, string source, string audio = "system_audio", int duration = 60)
        {
            if (route == "profile")
            {
                using var selected = await Post("/api/v1/regions/select",new { purpose="profile" });
                Assert.True(selected.IsSuccessStatusCode,await selected.Content.ReadAsStringAsync());
                using var selection = JsonDocument.Parse(await selected.Content.ReadAsStringAsync());
                using var message = new HttpRequestMessage(HttpMethod.Post,"/api/v1/profiles") { Content = new StringContent(JsonSerializer.Serialize(new {
                    name="Owned storage test",source_selection_ref=selection.RootElement.GetProperty("data").GetProperty("selection_ref").GetString(),
                    changes=new { duration_seconds=60,countdown_seconds=0,output_directory=Root } }),Encoding.UTF8,"application/json") };
                message.Headers.Add("Idempotency-Key",Guid.NewGuid().ToString("N"));
                using var created = await Client.SendAsync(message);
                Assert.True(created.IsSuccessStatusCode,await created.Content.ReadAsStringAsync());
                using var profile = JsonDocument.Parse(await created.Content.ReadAsStringAsync());
                return await Post("/api/v1/recordings?creation_wait_ms=0",new {
                    profile_ref=profile.RootElement.GetProperty("data").GetProperty("profile").GetProperty("profile_ref").Clone() });
            }
            if (route == "quick") return await Post("/api/v1/recordings/quick",new {
                target=new { type=source == "display" ? "primary_display" : "selected_region" },
                duration_seconds=duration,countdown_seconds=0,audio=AudioRequest(audio),output=new { directory=Root, filename=Guid.NewGuid().ToString("N")+".mp4" } });
            var request = Request(source); request["duration_seconds"] = duration; request["audio"] = AudioRequest(audio);
            return await Post("/api/v1/recordings?creation_wait_ms=0",request);
        }
        internal Recording Single => Assert.Single(Engine._recs.Values);
        internal async Task Accepted(string source="display")
        {
            using var response=await Create("raw",source);Assert.True(response.IsSuccessStatusCode,await response.Content.ReadAsStringAsync());
        }
        internal void TerminalOnce(Recording rec, string reason)
        {
            Assert.Equal(RecState.failed,rec.State);Assert.Equal(reason,rec.StopReason);
            var history=History.Read(rec.Id)!;Assert.Equal("failed",history.Status);Assert.Equal(reason,history.ReasonCode);
            Assert.Equal(0,AnchorCount(rec.Id));Assert.NotEqual("ready",history.Bundle?.Status);
        }
        internal long AnchorCount(string id)
        {
            using var c=Store.OpenReadOnlyConnection();using var command=c.CreateCommand();
            command.CommandText="SELECT count(*) FROM ordinary_publication_anchors WHERE recording_id=$id";
            command.Parameters.AddWithValue("$id",id);return (long)command.ExecuteScalar()!;
        }
        // Consume ownership once, including on construction failure. Failed deletion is
        // reported once; a repeated Dispose cannot restore over a later fixture's state.
        private List<Exception> ReleaseAndRestore()
        {
            var failures = new List<Exception>();
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return failures;
            void Attempt(Action action)
            {
                try { action(); } catch (Exception ex) { failures.Add(ex); }
            }
            try
            {
                for (var i = _resources.Count - 1; i >= 0; i--)
                {
                    var resource = _resources[i];
                    Attempt(resource.Release);
                    _faults?.Released.Add(resource.Name);
                }
                _resources.Clear();
                Attempt(SqliteConnection.ClearAllPools);
            }
            finally
            {
                Attempt(() => RecordingPreflightChecker.AudioHelperPathResolver = _priorHelper);
                Attempt(() => RecordingPreflightChecker.StorageCapacityProvider = _priorProvider);
                if (_topologyChanged) Attempt(() => SystemQuery.SetDisplayTopologyProvider(null));
                if (_authChanged) Attempt(() => ApiKeyAuth.ResetForTesting(null));
                Attempt(() => { if (_priorDir is null) DataDirResolver.ClearOverride(); else DataDirResolver.SetOverride(_priorDir); });
                foreach (var pair in _environment)
                    Attempt(() => Environment.SetEnvironmentVariable(pair.Key, pair.Value));
            }
            return failures;
        }
        public void Dispose()
        {
            var failures = ReleaseAndRestore();
            if (failures.Count == 1) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failures[0]).Throw();
            if (failures.Count > 1) throw new AggregateException("AV fixture cleanup failed.", failures);
        }
    }

    private static JsonObject AudioRequest(string kind) => new() { [kind] = new JsonObject { ["enabled"] = true } };
    private sealed class Devices : IMicrophoneDeviceProvider, ISystemAudioEndpointProvider
    {
        private static readonly SystemAudioEndpointInfo Endpoint = new("owned-endpoint", "Synthetic speakers", "render", "active", true);
        public Task<IReadOnlyList<MicrophoneDeviceInfo>> GetDevicesAsync(CancellationToken t = default) => Task.FromResult<IReadOnlyList<MicrophoneDeviceInfo>>(new[] { new MicrophoneDeviceInfo("owned-mic", "Synthetic microphone", true, "active") });
        public Task<IReadOnlyList<SystemAudioEndpointInfo>> GetRenderEndpointsAsync(CancellationToken t = default) => Task.FromResult<IReadOnlyList<SystemAudioEndpointInfo>>(new[] { Endpoint });
        public Task<SystemAudioEndpointInfo?> GetDefaultMultimediaRenderEndpointAsync(CancellationToken t = default) => Task.FromResult<SystemAudioEndpointInfo?>(Endpoint);
        public Task<SystemAudioEndpointInfo?> GetEndpointAsync(string id, CancellationToken t = default) => Task.FromResult<SystemAudioEndpointInfo?>(id == Endpoint.Id ? Endpoint : null);
    }
    private sealed class Factory(FakeAudioCaptureWorker audio, FakeVideoCaptureWorker video, string? fixture) : IAvWorkerFactory
    {
        internal Action? BeforeAudio, BeforeVideo;
        internal bool AudioTransferred, VideoTransferred;
        public IAudioCaptureWorker CreateAudioWorker() { BeforeAudio?.Invoke(); AudioTransferred = true; return audio; }
        public IAudioCaptureWorker CreateAudioWorker(AudioCaptureSourceKind kind) => CreateAudioWorker();
        public IVideoCaptureWorker CreateVideoWorker() { BeforeVideo?.Invoke(); VideoTransferred = true; return new VideoFile(video, fixture); }
    }
    private sealed class VideoFile(FakeVideoCaptureWorker inner, string? fixture) : IVideoCaptureWorker
    {
        public event Action<FirstFrameObservation>? FirstFrameObserved { add => inner.FirstFrameObserved += value; remove => inner.FirstFrameObserved -= value; }
        public event Action<int,string>? NaturalExit { add => inner.NaturalExit += value; remove => inner.NaturalExit -= value; }
        public string? OutputPath => inner.OutputPath;
        public int ExitCode => inner.ExitCode; public bool HasExited => inner.HasExited;
        public long? FirstProgressFrame => inner.FirstProgressFrame; public long? FirstProgressOutTimeUs => inner.FirstProgressOutTimeUs; public double? ProgressAnchorDeltaMs => inner.ProgressAnchorDeltaMs;
        public long LaunchAnchorTicks => inner.LaunchAnchorTicks; public long FirstFrameAnchorTicks => inner.FirstFrameAnchorTicks;
        public void Start(CaptureConfig cfg, string path) { if (fixture is not null) File.Copy(fixture,path); inner.Start(cfg,path); }
        public OutputMeta Stop() => inner.Stop(); public bool WaitForExit(TimeSpan timeout) => inner.WaitForExit(timeout);
        public string GetStderrLog() => inner.GetStderrLog(); public void Dispose() => inner.Dispose();
    }
    private static Task Until(Func<bool> condition) => Task.Run(() => Assert.True(SpinWait.SpinUntil(condition,TimeSpan.FromSeconds(10)),"Expected AV lifecycle"));
    private static void Layout(Fixture f, Recording rec)
    {
        Assert.True(RecordingStoragePolicy.IsOrdinaryAv(rec));
        Assert.Equal(Path.GetDirectoryName(rec.OutputPath),rec.Config.WritePaths!.OutputDirectory);
        Assert.Equal(Path.GetFullPath(Path.Combine(f.Root,"temp")),rec.Config.WritePaths.AvTempDirectory);
        Assert.Null(rec.Config.WritePaths.WgcTempRoot);
        Assert.All(f.Capacity.Requests, paths => Assert.DoesNotContain(CaptureWritePaths.ResolveWgcTempRoot(),paths));
    }
    [Theory]
    [InlineData("raw","display","system_audio",false)] [InlineData("raw","region","system_audio",true)]
    [InlineData("quick","display","system_audio",true)] [InlineData("quick","region","system_audio",false)]
    [InlineData("raw","display","microphone",true)] [InlineData("raw","region","microphone",false)]
    [InlineData("quick","display","microphone",false)] [InlineData("quick","region","microphone",true)]
    [InlineData("raw","display","system_audio",true)] [InlineData("raw","region","system_audio",false)]
    [InlineData("quick","display","system_audio",false)] [InlineData("quick","region","system_audio",true)]
    [InlineData("raw","display","microphone",false)] [InlineData("raw","region","microphone",true)]
    [InlineData("quick","display","microphone",true)] [InlineData("quick","region","microphone",false)]
    public async Task AdmissionFailurePrecedesConfirmationAndBothWorkers(string route,string source,string audio,bool unknown)
    {
        using var f=new Fixture(); if(unknown)f.Capacity.Query=(_,_)=>throw new IOException("unavailable"); else f.Capacity.Free=0;
        using var response=await f.Create(route,source,audio);var text=await response.Content.ReadAsStringAsync();
        Assert.False(response.IsSuccessStatusCode,text);Assert.Contains(unknown ? WindowStorageSafety.UnavailableCode : WindowStorageSafety.LowSpaceCode,text);
        Assert.Empty(f.Tray.Confirmations);Assert.Null(f.Audio.OutputPath);Assert.Null(f.Video.OutputPath);
    }
    [Theory]
    [InlineData("system_audio",false)] [InlineData("microphone",true)]
    public async Task ApprovalRechecksActualAvTempAndChangedOutput(string audio,bool unavailable)
    {
        using var f=new Fixture();using var response=await f.Create("raw","region",audio);Assert.True(response.IsSuccessStatusCode,await response.Content.ReadAsStringAsync());
        Assert.Null(f.Audio.OutputPath);Assert.Null(f.Video.OutputPath);
        var selected=Path.Combine(f.Root,"approved");f.Capacity.Query=(paths,_)=>Task.FromResult<IReadOnlyList<StorageVolumeSample>>(paths.Select(p=>new StorageVolumeSample(p,"volume",p.EndsWith("temp") ? unavailable ? -1 : 0 : 100L<<30)).ToArray());
        f.Tray.Approve(selected);await Until(()=>f.Single.IsFinalized);Layout(f,f.Single);Assert.Null(f.Audio.OutputPath);Assert.Null(f.Video.OutputPath);
        f.TerminalOnce(f.Single,unavailable ? WindowStorageSafety.UnavailableCode : WindowStorageSafety.LowSpaceCode);
    }
    [Theory]
    [InlineData("audio",false)] [InlineData("audio",true)] [InlineData("video",false)] [InlineData("video",true)]
    public async Task PhysicalWorkerConstructionCannotHideNewFailure(string boundary,bool unknown)
    {
        using var f=new Fixture();using var response=await f.Create("raw","display");Assert.True(response.IsSuccessStatusCode);
        void Fail() { if(unknown)f.Capacity.Query=(_,_)=>throw new IOException("unknown");else f.Capacity.Free=0; }
        if(boundary=="audio")f.Factory.BeforeAudio=Fail;else f.Factory.BeforeVideo=Fail;
        f.Tray.Approve();await Until(()=>f.Single.IsFinalized);Layout(f,f.Single);
        Assert.Null(f.Video.OutputPath);if(boundary=="audio")Assert.Null(f.Audio.OutputPath);else Assert.True(f.Audio.StopCalled);
        f.TerminalOnce(f.Single,unknown ? WindowStorageSafety.UnavailableCode : WindowStorageSafety.LowSpaceCode);
    }
    [Theory]
    [InlineData(false,"low")] [InlineData(false,"unknown")] [InlineData(true,"low")] [InlineData(true,"remount")] [InlineData(false,"timeout")]
    public async Task PreparingOrRecordingMonitorStopsOwnedWorkers(bool ready,string fault)
    {
        using var f=new Fixture(ready);using var response=await f.Create("raw","region","microphone");Assert.True(response.IsSuccessStatusCode);
        f.Tray.Approve();await Until(()=>f.Single.State==(ready ? RecState.recording : RecState.preparing));await f.Clock.WaitForDelay();Layout(f,f.Single);
        if(fault=="timeout")f.Capacity.Query=async (_,t)=> { await Task.Delay(Timeout.InfiniteTimeSpan,t);return Array.Empty<StorageVolumeSample>(); };
        else if(fault=="low")f.Capacity.Free=WindowStorageSafety.LowWaterBytes-1;
        else if(fault=="remount")f.Capacity.Volume="replacement";else f.Capacity.Query=(_,_)=>throw new IOException("unknown");
        f.Clock.Tick();await Until(()=>f.Single.IsFinalized && f.Audio.StopCalled);Assert.True(f.Audio.HasExited);
        if(ready)Assert.True(f.Video.StopCalled);else { Assert.Null(f.Video.OutputPath);f.Backend.StartVideo();Assert.Null(f.Video.OutputPath); }
        var code=fault=="low" ? WindowStorageSafety.LowSpaceCode : WindowStorageSafety.UnavailableCode;f.TerminalOnce(f.Single,code);
        Assert.False(f.Single.Config.StorageSafety!.TryAbort(code));await f.Single.Config.StorageSafety.MonitorCompletion.WaitAsync(TimeSpan.FromSeconds(5));
    }
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task PrepStopAndDisposeReleaseGuardWithoutLateVideo(bool dispose)
    {
        using var f=new Fixture(false);using var response=await f.Create("raw","display");Assert.True(response.IsSuccessStatusCode);f.Tray.Approve();
        await Until(()=>f.Single.State==RecState.preparing);await f.Clock.WaitForDelay();var guard=f.Single.Config.StorageSafety!;
        if(dispose)f.Engine.Dispose();else f.Engine.Stop(f.Single.Id,"user_requested");
        await guard.MonitorCompletion.WaitAsync(TimeSpan.FromSeconds(5));Assert.True(f.Audio.StopCalled);Assert.Null(f.Video.OutputPath);
        f.Backend.StartVideo();Assert.Null(f.Video.OutputPath);
    }
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task AvLayoutBudgetsMergeByVolumeAtInclusiveBoundary(bool separate)
    {
        using var f=new Fixture();var cfg=new CaptureConfig { OutputPath=Path.Combine(f.Root,"output","x.mp4"),AudioSourceKind=AudioCaptureSourceKind.Microphone };
        var paths=CaptureWritePaths.FreezeFfmpegAv(cfg);var video=WindowStorageSafety.VideoEstimate(60);
        using var guard=new WindowStorageSafety(paths,60,f.Capacity);guard.EnableFfmpegAvPublication();
        var samples=paths.Directories.Select(p=>new StorageVolumeSample(p,separate && p==paths.AvTempDirectory ? "av" : "out",100L<<30)).ToArray();
        var budgets=guard.AdmissionBudgets(samples);Assert.Equal(separate ? 2 : 1,budgets.Count);
        Assert.Equal(WindowStorageSafety.LowWaterBytes+(separate ? 2*video : 4*video+4096),budgets["out"]);
        if(separate)Assert.Equal(WindowStorageSafety.LowWaterBytes+2*video+4096,budgets["av"]);
        f.Capacity.Query=(ps,_)=>Task.FromResult<IReadOnlyList<StorageVolumeSample>>(samples.Select(s=>s with { AvailableBytes=budgets[s.VolumeIdentity] }).ToArray());
        guard.EnsureAdmission();Assert.Equal(0,guard.RemainingPublicationCopyBytes);
        f.Capacity.Query=(ps,_)=>Task.FromResult<IReadOnlyList<StorageVolumeSample>>(samples.Select(s=>s with { AvailableBytes=budgets[s.VolumeIdentity]-1 }).ToArray());
        Assert.Equal(WindowStorageSafety.LowSpaceCode,await guard.CheckAsync(true));
    }
    [Theory]
    [InlineData("raw","display","system_audio",false,false)] [InlineData("quick","region","microphone",true,false)]
    [InlineData("raw","region","system_audio",false,true)] [InlineData("quick","display","microphone",true,true)]
    [InlineData("raw","region","system_audio",false,false)] [InlineData("quick","display","system_audio",true,false)]
    [InlineData("quick","region","system_audio",false,false)] [InlineData("raw","display","microphone",false,false)]
    [InlineData("raw","region","microphone",true,false)] [InlineData("quick","display","microphone",false,false)]
    public async Task RealLavfiAvNativePublicationHistoryAndBundle(string route,string source,string audio,bool natural,bool fail)
    {
        var media=Path.GetFullPath(Path.Combine(TestHelper.ProjectRoot,".local-data/temp/task335",Guid.NewGuid().ToString("N")));Directory.CreateDirectory(media);
        var v=Path.Combine(media,"video.mp4");var a=Path.Combine(media,"audio.wav");
        await Generate(new[] { "-y","-f","lavfi","-i","color=c=blue:s=64x64:r=15","-t","1","-c:v","libx264","-pix_fmt","yuv420p",v });
        await Generate(new[] { "-y","-f","lavfi","-i","sine=frequency=440:sample_rate=48000","-t","4","-c:a","pcm_s16le",a });
        using var f=new Fixture(true,a,v);var publisher=new StagingToFinalPublisher(new PhysicalFileSystemOperations());f.Backend.FinalOutputPublisher=publisher;
        f.Capacity.Query=(paths,_)=>Task.FromResult<IReadOnlyList<StorageVolumeSample>>(paths.Select(p=>new StorageVolumeSample(p,"approved-volume",p==CaptureWritePaths.ResolveWgcTempRoot() ? 0 : f.Capacity.Free)).ToArray());
        using var response=await f.Create(route,source,audio,duration:1);Assert.True(response.IsSuccessStatusCode,await response.Content.ReadAsStringAsync());f.Tray.Approve();await Until(()=>f.Single.State==RecState.recording);
        var rec=f.Single;Layout(f,rec);var guard=rec.Config.StorageSafety!;
        var oldBytes=new byte[] { 3,3,5,9 };ObservedFileIdentity? oldIdentity=null;
        if(fail) { File.WriteAllBytes(rec.OutputPath,oldBytes);oldIdentity=WindowsReadOnlyFileMetadataProbe.Instance.Observe(rec.OutputPath).Before!.Identity; }
        // Use actual source/copy bytes while simulating an exact free-space boundary.
        publisher.BeforeOriginalFlushForTests=_=> { Assert.Equal(0,guard.RemainingPublicationCopyBytes);f.Capacity.Free=WindowStorageSafety.LowWaterBytes-(fail ? 1 : 0); };
        if(natural)f.Video.EmitNaturalExit(0,"");else f.Engine.Stop(rec.Id,"user_requested");
        await Until(()=>rec.IsFinalized);if(!fail)await Until(()=>f.History.Read(rec.Id)?.Bundle?.Status == "ready");
        Assert.True(f.Audio.StopCalled);Assert.True(f.Audio.HasExited);Assert.True(f.Video.HasExited);
        if(fail) { f.TerminalOnce(rec,WindowStorageSafety.LowSpaceCode);Assert.Equal(oldBytes,File.ReadAllBytes(rec.OutputPath));Assert.Equal(oldIdentity,WindowsReadOnlyFileMetadataProbe.Instance.Observe(rec.OutputPath).Before!.Identity); }
        else {
            Assert.Equal(RecState.completed,rec.State);Assert.True(guard.Committed);Assert.Equal(1,f.AnchorCount(rec.Id));
            var meta=new FfmpegOutputProber().Probe(rec.OutputPath);Assert.True(meta.HasAudioStream);Assert.Equal("h264",meta.Codec);Assert.Equal("aac",meta.AudioCodec);Assert.True(meta.DurationSeconds>0);
            Assert.Equal("ready",f.History.Read(rec.Id)!.Bundle!.Status);Assert.True(rec.LastMeta!.AudioPreRollMs>0);
            Assert.False(guard.TryAbort(WindowStorageSafety.LowSpaceCode));Assert.Equal("completed",f.History.Read(rec.Id)!.Status);
        }
        AvMuxOwnershipTests.AssertNoOwnedMux(rec.OutputPath);
        Assert.False(File.Exists(rec.OutputPath+".muxing.partial.mp4"));await guard.MonitorCompletion.WaitAsync(TimeSpan.FromSeconds(5));
    }
    private static async Task Generate(string[] args)
    {
        var info=new ProcessStartInfo(FfmpegLocator.FfmpegPath) { UseShellExecute=false,CreateNoWindow=true,RedirectStandardError=true };
        foreach(var arg in args)info.ArgumentList.Add(arg);using var process=Process.Start(info)!;var stderr=process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30));Assert.True(process.ExitCode==0,await stderr);
    }
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task QueuedOldCallbackAndDirectoryApprovalHaveOneLayoutWinner(bool failureClaimsFirst)
    {
        using var f=new Fixture();await f.Accepted();var rec=f.Single;var oldPath=rec.OutputPath;
        using var old=RecordingStoragePolicy.CreateSafety(rec,f.Capacity);old.EnsureAdmission();rec.Config.StorageSafety=old;
        var entered=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var done=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);using var release=new ManualResetEventSlim();
        var handle=typeof(RecordingEngine).GetMethod("HandleStorageFailure",System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic)!;
        if(failureClaimsFirst) f.Audit.BeforeLog=name=> { if(name=="recording.storage_abort") { entered.TrySetResult();Assert.True(release.Wait(TimeSpan.FromSeconds(5))); } };
        old.Start(code=> {
            try {
                if(!failureClaimsFirst) { entered.TrySetResult();Assert.True(release.Wait(TimeSpan.FromSeconds(5))); }
                handle.Invoke(f.Engine,new object?[] { rec,old,code,"owned-layout-race",f.Tray });done.TrySetResult();
            } catch(Exception ex) { done.TrySetException(ex); }
        });
        Assert.True(old.TryAbort(WindowStorageSafety.LowSpaceCode));await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var selected=Path.Combine(f.Root,"new-approved");
        try {
            if(failureClaimsFirst) {
                var apply=typeof(RecordingEngine).GetMethod("ApplyConfirmationOutputDirectory",System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic)!;
                Assert.False((bool)apply.Invoke(f.Engine,new object[] { rec,ConfirmationDecision.Approve(selected),"owned-approval" })!);
                Assert.Equal(oldPath,rec.OutputPath);Assert.Equal(RecState.finalizing,rec.State);
            } else {
                f.Tray.Approve(selected);await Until(()=>rec.State==RecState.recording);
                Assert.Equal(selected,Path.GetDirectoryName(rec.OutputPath));Assert.NotSame(old,rec.Config.StorageSafety);
            }
            release.Set();await done.Task.WaitAsync(TimeSpan.FromSeconds(5));
            if(failureClaimsFirst)f.TerminalOnce(rec,WindowStorageSafety.LowSpaceCode);
            else {
                Assert.Equal(RecState.recording,rec.State);Assert.Null(rec.Config.StorageSafety!.FailureCode);
                Assert.Null(rec.TrustedLifecycleAbortReason);await f.Clock.WaitForDelay();f.Engine.Stop(rec.Id,"user_requested");
                await rec.Config.StorageSafety.MonitorCompletion.WaitAsync(TimeSpan.FromSeconds(5));
            }
        } finally { release.Set(); }
        await old.MonitorCompletion.WaitAsync(TimeSpan.FromSeconds(5));
    }


    [Theory]
    [InlineData("low")] [InlineData("unknown")]
    public async Task VisibleCountdownRechecksBeforeVideoWithoutChangingMediaZero(string fault)
    {
        using var f=new Fixture();f.Engine.CountdownInterval=TimeSpan.FromMilliseconds(1);
        var request=f.Request();request["audio"]=AudioRequest("system_audio");request["countdown_seconds"]=3;
        using var response=await f.Post("/api/v1/recordings?creation_wait_ms=0",request);Assert.True(response.IsSuccessStatusCode);
        f.Engine.BeforeStartActionForTests=(rec,stage)=> { if(stage=="start_video") { if(fault=="low")f.Capacity.Free=0;else f.Capacity.Query=(_,_)=>throw new IOException("unknown"); } };
        f.Tray.Approve();await Until(()=>f.Single.IsFinalized);
        Assert.NotNull(f.Audio.OutputPath);Assert.True(f.Audio.StopCalled);Assert.Null(f.Video.OutputPath);Assert.Equal(default,f.Single.StartedAtUtc);
        f.TerminalOnce(f.Single,fault=="low" ? WindowStorageSafety.LowSpaceCode : WindowStorageSafety.UnavailableCode);
    }
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task FirstFrameFailureRaceDoesNotReviveStoppedRun(bool failureFirst)
    {
        using var f=new Fixture();using var response=await f.Create("raw","display");Assert.True(response.IsSuccessStatusCode);
        f.Video.FirstFrameObserved+=_=> { if(failureFirst)Assert.True(f.Single.Config.StorageSafety!.TryAbort(WindowStorageSafety.LowSpaceCode)); };
        f.Tray.Approve();if(!failureFirst) { await Until(()=>f.Single.State==RecState.recording);Assert.True(f.Single.Config.StorageSafety!.TryAbort(WindowStorageSafety.LowSpaceCode)); }
        await Until(()=>f.Single.IsFinalized);Assert.True(f.Audio.StopCalled);Assert.True(f.Video.StopCalled);f.TerminalOnce(f.Single,WindowStorageSafety.LowSpaceCode);
        f.Video.EmitFirstFrame();f.Engine.Stop(f.Single.Id,"user_requested");Assert.Equal(RecState.failed,f.Single.State);
    }

    [Fact]
    public async Task NestedAvRunsOwnWorkersMonitorsAndHistorySeparately()
    {
        using var f=new Fixture();var owned=new List<(AvSplitCaptureBackend Backend,FakeAudioCaptureWorker Audio,FakeVideoCaptureWorker Video)>();
        f.Engine.BackendFactory=cfg=> { var a=new FakeAudioCaptureWorker(raiseAudioReadyOnStart:true);var v=new FakeVideoCaptureWorker();var b=new AvSplitCaptureBackend(new FakeAvWorkerFactory { AudioWorker=a,VideoWorker=v },new FakeExternalProcessRunner(),new TempRetentionPolicy(f.Root));owned.Add((b,a,v));return(b,cfg.SourceKind=="region" ? "ffmpeg-region-av-split" : "ffmpeg-av-split"); };
        try {
            var outer=f.Request();outer["audio"]=AudioRequest("system_audio");outer["nested"]=new JsonObject { ["role"]="outer",["session_id"]="owned-av-nested" };
            using(var response=await f.Post("/api/v1/recordings?creation_wait_ms=0",outer))Assert.True(response.IsSuccessStatusCode);
            var parent=f.Single;f.Tray.Approve();await Until(()=>parent.State==RecState.recording);await f.Clock.WaitForDelay();
            var clock=new StorageTestClock();f.Engine.StorageMonitorDelay=clock.Delay;
            var inner=f.Request("region");inner["audio"]=AudioRequest("microphone");inner["nested"]=new JsonObject { ["role"]="inner",["session_id"]="owned-av-nested",["parent_recording_id"]=parent.Id };
            using(var response=await f.Post("/api/v1/recordings?creation_wait_ms=0",inner))Assert.True(response.IsSuccessStatusCode);
            var child=f.Engine._recs.Values.Single(r=>r.Id!=parent.Id);f.Tray.Approve();await Until(()=>child.State==RecState.recording);await clock.WaitForDelay();
            Assert.NotSame(parent.Config.StorageSafety,child.Config.StorageSafety);Assert.Equal(parent.Id,child.ParentRecordingId);
            var first=true;f.Capacity.Query=(paths,_)=> { var free=first ? 0 : 100L<<30;first=false;return Task.FromResult<IReadOnlyList<StorageVolumeSample>>(paths.Select(p=>new StorageVolumeSample(p,"approved-volume",free)).ToArray()); };
            clock.Tick();await Until(()=>child.IsFinalized);f.TerminalOnce(child,WindowStorageSafety.LowSpaceCode);Assert.Equal(parent.Id,f.History.Read(child.Id)!.ParentRecordingId);
            Assert.True(owned[1].Audio.StopCalled);Assert.True(owned[1].Video.StopCalled);Assert.False(owned[0].Audio.StopCalled);Assert.False(owned[0].Video.StopCalled);
            Assert.Equal(RecState.recording,parent.State);Assert.Null(parent.Config.StorageSafety!.FailureCode);f.Engine.Stop(parent.Id,"user_requested");
            await Task.WhenAll(parent.Config.StorageSafety.MonitorCompletion,child.Config.StorageSafety!.MonitorCompletion).WaitAsync(TimeSpan.FromSeconds(5));
        } finally { foreach(var item in owned)item.Backend.Dispose(); }
    }
    [Theory]
    [InlineData("healthy")] [InlineData("low")] [InlineData("unavailable")]
    public async Task ApprovedDirectoryAtomicallyReplacesOldSafetyAndIgnoresLateOldSample(string outcome)
    {
        using var f=new Fixture();await f.Accepted();var rec=f.Single;var oldPaths=rec.Config.WritePaths!;
        var entered=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release=new TaskCompletionSource<IReadOnlyList<StorageVolumeSample>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var oldProvider=new Provider { Query=(paths,_)=> { entered.TrySetResult();return release.Task; } };
        using var old=new WindowStorageSafety(oldPaths,60,oldProvider);rec.Config.StorageSafety=old;
        var late=old.CheckAsync(false);await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var selected=Path.Combine(f.Root,"locally-approved");
        f.Capacity.Query=(paths,_)=> {
            Assert.Equal(new[] { selected,Path.Combine(f.Root,"temp") },paths);if(outcome=="unavailable")throw new IOException();
            return Task.FromResult<IReadOnlyList<StorageVolumeSample>>(paths.Select(p=>new StorageVolumeSample(p,"new-volume",outcome=="low" ? 0 : 100L*1024*1024*1024)).ToArray()); };
        f.Capacity.Requests.Clear();f.Tray.Approve(selected);
        release.TrySetResult(oldPaths.Directories.Select(p=>new StorageVolumeSample(p,"old-volume",0)).ToArray());
        Assert.Null(await late.WaitAsync(TimeSpan.FromSeconds(5)));Assert.False(old.TryAbort(WindowStorageSafety.LowSpaceCode));
        await Until(()=>outcome=="healthy" ? rec.State==RecState.recording : rec.IsFinalized);
        Assert.Equal(selected,Path.GetDirectoryName(rec.OutputPath));Assert.NotSame(oldPaths,rec.Config.WritePaths);
        if(outcome=="healthy") {
            Assert.NotSame(old,rec.Config.StorageSafety);Assert.Null(rec.Config.StorageSafety!.FailureCode);
            await f.Clock.WaitForDelay();f.Engine.Stop(rec.Id,"user_requested");await Until(()=>rec.IsFinalized);
            await rec.Config.StorageSafety.MonitorCompletion.WaitAsync(TimeSpan.FromSeconds(5));
        } else { Assert.Null(f.Audio.OutputPath);Assert.Null(f.Video.OutputPath);f.TerminalOnce(rec,outcome=="low" ? WindowStorageSafety.LowSpaceCode : WindowStorageSafety.UnavailableCode); }
    }


    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task PreparationCancellationAndFailureUseOneArbiter(bool failureWins)
    {
        using var f=new Fixture(false);using var response=await f.Create("raw","display");Assert.True(response.IsSuccessStatusCode);f.Tray.Approve();
        await Until(()=>f.Single.State==RecState.preparing);await f.Clock.WaitForDelay();var guard=f.Single.Config.StorageSafety!;
        if(failureWins)Assert.True(guard.TryAbort(WindowStorageSafety.LowSpaceCode));
        f.Engine.Stop(f.Single.Id,"user_requested");
        if(!failureWins)Assert.False(guard.TryAbort(WindowStorageSafety.LowSpaceCode));
        await Until(()=>f.Single.IsFinalized);await guard.MonitorCompletion.WaitAsync(TimeSpan.FromSeconds(5));Assert.True(f.Audio.StopCalled);Assert.Null(f.Video.OutputPath);
        if(failureWins)f.TerminalOnce(f.Single,WindowStorageSafety.LowSpaceCode);else { Assert.Equal(RecState.cancelled,f.Single.State);Assert.Equal("cancelled",f.History.Read(f.Single.Id)!.Status); }
    }
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task WorkerFailureReleasesMonitorAndCannotRestartVideo(bool ready)
    {
        using var f=new Fixture(ready);using var response=await f.Create("raw","region","microphone");Assert.True(response.IsSuccessStatusCode);f.Tray.Approve();
        await Until(()=>f.Single.State==(ready ? RecState.recording : RecState.preparing));await f.Clock.WaitForDelay();
        f.Audio.EmitNaturalExit(1,"owned_helper_failure");await Until(()=>f.Single.IsFinalized);Assert.Equal(RecState.failed,f.Single.State);
        await f.Single.Config.StorageSafety!.MonitorCompletion.WaitAsync(TimeSpan.FromSeconds(5));Assert.True(f.Audio.HasExited);
        if(ready)Assert.True(f.Video.StopCalled);else { f.Backend.StartVideo();Assert.Null(f.Video.OutputPath); }
        Assert.Equal(0,f.AnchorCount(f.Single.Id));Assert.NotEqual("ready",f.History.Read(f.Single.Id)!.Bundle?.Status);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task StopOrDisposeDuringFirstFrameWaitStopsBothExistingWorkers(bool dispose)
    {
        using var f=new Fixture(frameDelay:-1);using var response=await f.Create("raw","region");Assert.True(response.IsSuccessStatusCode);f.Tray.Approve();
        await Until(()=>f.Video.OutputPath is not null && f.Single.State==RecState.preparing);var guard=f.Single.Config.StorageSafety!;
        Assert.Equal(default,f.Single.StartedAtUtc);
        if(dispose)f.Engine.Dispose();else f.Engine.Stop(f.Single.Id,"user_requested");
        Assert.True(f.Video.StopCalled);Assert.True(f.Video.HasExited);Assert.True(f.Audio.StopCalled);Assert.True(f.Audio.HasExited);
        await guard.MonitorCompletion.WaitAsync(TimeSpan.FromSeconds(5));Assert.True(f.Single.IsFinalized);Assert.Equal(RecState.cancelled,f.Single.State);
        f.Video.EmitFirstFrame();f.Backend.StartVideo();Assert.Equal(RecState.cancelled,f.Single.State);Assert.Equal(0,f.AnchorCount(f.Single.Id));
    }

    [Fact]
    public async Task NestedSameBasenameDifferentOutputsCannotShareOrRetainOtherRunInputs()
    {
        using var f=new Fixture();var owned=new List<(AvSplitCaptureBackend Backend,FakeAudioCaptureWorker Audio,FakeVideoCaptureWorker Video)>();
        f.Engine.BackendFactory=cfg=> { var audio=new FakeAudioCaptureWorker(raiseAudioReadyOnStart:true);var video=new FakeVideoCaptureWorker();var backend=new AvSplitCaptureBackend(new FakeAvWorkerFactory { AudioWorker=audio,VideoWorker=video },new FakeExternalProcessRunner(),new TempRetentionPolicy(f.Root));owned.Add((backend,audio,video));return(backend,cfg.SourceKind=="region" ? "ffmpeg-region-av-split" : "ffmpeg-av-split"); };
        try {
            var outer=f.Request(filename:"same.mp4");outer["audio"]=AudioRequest("system_audio");outer["nested"]=new JsonObject { ["role"]="outer",["session_id"]="owned-av-same-name" };
            using(var response=await f.Post("/api/v1/recordings?creation_wait_ms=0",outer))Assert.True(response.IsSuccessStatusCode);
            var parent=f.Single;f.Tray.Approve();await Until(()=>parent.State==RecState.recording);await f.Clock.WaitForDelay();
            var parentVideo=new byte[] { 3,3,5,1 };var parentAudio=new byte[] { 3,3,5,2 };
            File.WriteAllBytes(owned[0].Backend.TempVideoPath!,parentVideo);File.WriteAllBytes(owned[0].Backend.TempAudioPath!,parentAudio);
            var clock=new StorageTestClock();f.Engine.StorageMonitorDelay=clock.Delay;
            var inner=f.Request("region","same.mp4");inner["output"]!["directory"]=Path.Combine(f.Root,"inner");inner["audio"]=AudioRequest("microphone");inner["nested"]=new JsonObject { ["role"]="inner",["session_id"]="owned-av-same-name",["parent_recording_id"]=parent.Id };
            using(var response=await f.Post("/api/v1/recordings?creation_wait_ms=0",inner))Assert.True(response.IsSuccessStatusCode,await response.Content.ReadAsStringAsync());
            var child=f.Engine._recs.Values.Single(r=>r.Id!=parent.Id);f.Tray.Approve();await Until(()=>child.State==RecState.recording);await clock.WaitForDelay();
            Assert.Equal(Path.GetFileName(parent.OutputPath),Path.GetFileName(child.OutputPath));Assert.NotEqual(parent.OutputPath,child.OutputPath);
            Assert.NotEqual(owned[0].Backend.TempVideoPath,owned[1].Backend.TempVideoPath);Assert.NotEqual(owned[0].Backend.TempAudioPath,owned[1].Backend.TempAudioPath);
            Assert.Equal(parent.Id+"_video.mp4",Path.GetFileName(owned[0].Backend.TempVideoPath));Assert.Equal(child.Id+"_audio.wav",Path.GetFileName(owned[1].Backend.TempAudioPath));
            Assert.Equal(Path.GetDirectoryName(owned[0].Backend.TempVideoPath),Path.GetDirectoryName(owned[1].Backend.TempVideoPath));
            var first=true;f.Capacity.Query=(paths,_)=> { var free=first ? 0 : 100L<<30;first=false;return Task.FromResult<IReadOnlyList<StorageVolumeSample>>(paths.Select(p=>new StorageVolumeSample(p,"approved-volume",free)).ToArray()); };
            clock.Tick();await Until(()=>child.IsFinalized);f.TerminalOnce(child,WindowStorageSafety.LowSpaceCode);
            Assert.Equal(parentVideo,File.ReadAllBytes(owned[0].Backend.TempVideoPath!));Assert.Equal(parentAudio,File.ReadAllBytes(owned[0].Backend.TempAudioPath!));
            Assert.False(owned[0].Video.StopCalled);Assert.False(owned[0].Audio.StopCalled);Assert.Equal(RecState.recording,parent.State);
            Assert.True(owned[1].Video.StopCalled);Assert.True(owned[1].Audio.StopCalled);
            File.Delete(owned[0].Backend.TempVideoPath!);File.Delete(owned[0].Backend.TempAudioPath!);f.Engine.Stop(parent.Id,"user_requested");
            await Task.WhenAll(parent.Config.StorageSafety!.MonitorCompletion,child.Config.StorageSafety!.MonitorCompletion).WaitAsync(TimeSpan.FromSeconds(5));
        } finally { foreach(var item in owned)item.Backend.Dispose(); }
    }
}
