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
public sealed class OrdinaryAvOutputOwnershipTests
{
    private sealed class Provider : IStorageCapacityProvider
    {
        internal readonly ConcurrentQueue<string[]> Requests = new();
        internal Func<IReadOnlyList<string>, CancellationToken, Task<IReadOnlyList<StorageVolumeSample>>>? Query = null;
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
        internal Action<string>? BeforeLog = null;
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

    private sealed class Fixture : IDisposable
    {
        internal readonly string Root = Path.GetFullPath(Path.Combine(TestHelper.ProjectRoot, ".local-data/temp/task335r", Guid.NewGuid().ToString("N")));
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
        internal Fixture(bool ready = true, string? wav = null, string? video = null, int frameDelay = 0,
            Func<IOrdinaryRecordingHistoryStore,IOrdinaryRecordingHistoryStore>? historyFactory = null)
        {
            _environment = new[] { "AGENT_RECORDER_TEST_MODE", "AGENT_RECORDER_DATA_DIR", CaptureBackendSelector.DisplayBackendEnvVar,
                CaptureBackendSelector.RegionBackendEnvVar }.ToDictionary(k => k, Environment.GetEnvironmentVariable);
            Directory.CreateDirectory(Root); DataDirResolver.SetOverride(Root);
            Environment.SetEnvironmentVariable("AGENT_RECORDER_DATA_DIR", Root);
            Environment.SetEnvironmentVariable("AGENT_RECORDER_TEST_MODE", null);
            Environment.SetEnvironmentVariable(CaptureBackendSelector.DisplayBackendEnvVar, null);
            Environment.SetEnvironmentVariable(CaptureBackendSelector.RegionBackendEnvVar, null);
            RecordingPreflightChecker.StorageCapacityProvider = Capacity;
            _helper = new(Root);
            RecordingPreflightChecker.AudioHelperPathResolver = () => _helper.ExecutablePath;
            Audio = new(raiseAudioReadyOnStart: ready, holdFileOpen: wav is not null, holdFileOpenCopyFrom: wav);
            Video = new(firstFrameDelayMs: frameDelay);
            Factory = new(Audio, Video, video);
            Backend = new(Factory, new ExternalProcessRunner(), new TempRetentionPolicy(Root));
            SystemQuery.SetDisplayTopologyProvider(() => new() { new("display_1", "Synthetic", true,
                new(0,0,1920,1080), 1.0, Display.Fingerprint, DisplayIdentityResolutionStatus.Resolved) });
            Store = new(Path.Combine(Root, "history.db")); Store.Initialize(); History = new(Store);
            Audit = new(Path.Combine(Root,"audit.jsonl"));
            Engine = new(Audit, microphoneProvider: new Devices(), systemAudioEndpointProvider: new Devices(), bundleGenerator: new FfmpegRecordingBundleGenerator()) { CountdownSteps = 0, StorageMonitorDelay = Clock.Delay };
            Engine.ConfigureOrdinaryRecordingHistory(historyFactory?.Invoke(History) ?? History); Engine.SetTray(Tray);
            Engine.BackendFactory = cfg => (Backend, cfg.SourceKind == "region" ? "ffmpeg-region-av-split" : "ffmpeg-av-split");
            ApiKeyAuth.InitializeForTesting(Root);
            Api = new(Engine,Audit,Tray,null,null,null,null,null,null,null,null,null,0,
                profileManagementGateway: new SqliteFixedRegionProfileManagementGateway(Store), profileDisplayEnvironmentProvider: new Display());
            Api.Start(); Client = new(new HttpClientHandler { UseProxy = false }) {
                BaseAddress = new Uri($"http://127.0.0.1:{Api.BoundPort}"), Timeout = TimeSpan.FromSeconds(10) };
            Client.DefaultRequestHeaders.Add("X-Agent-Recorder-Key",ApiKeyAuth.CurrentApiKey);
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
        public void Dispose()
        {
            Api.Stop(); Engine.Dispose(); Backend.Dispose(); History.Dispose(); Client.Dispose(); _helper.Dispose(); SqliteConnection.ClearAllPools();
            RecordingPreflightChecker.AudioHelperPathResolver = _priorHelper;
            RecordingPreflightChecker.StorageCapacityProvider=_priorProvider;
            SystemQuery.SetDisplayTopologyProvider(null);ApiKeyAuth.ResetForTesting(null);
            if (_priorDir is null) DataDirResolver.ClearOverride(); else DataDirResolver.SetOverride(_priorDir);
            foreach(var pair in _environment) Environment.SetEnvironmentVariable(pair.Key,pair.Value);
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
        internal Action? BeforeAudio = null, BeforeVideo = null;
        public IAudioCaptureWorker CreateAudioWorker() { BeforeAudio?.Invoke(); return audio; }
        public IAudioCaptureWorker CreateAudioWorker(AudioCaptureSourceKind kind) => CreateAudioWorker();
        public IVideoCaptureWorker CreateVideoWorker() { BeforeVideo?.Invoke(); return new VideoFile(video, fixture); }
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

    private sealed class Pair : IDisposable
    {
        internal readonly Fixture F;
        internal readonly List<(AvSplitCaptureBackend Backend, FakeAudioCaptureWorker Audio, FakeVideoCaptureWorker Video)> Workers = new();
        internal int Starts;
        internal bool Ready = true;
        internal Action? BeforeAudio;
        internal Action? BeforeAudioStop;
        internal Action? BeforeVideoStop;
        internal string? AudioFixture, VideoFixture;
        internal Pair(bool ready = true, IExternalProcessRunner? runner = null,
            Func<IOrdinaryRecordingHistoryStore,IOrdinaryRecordingHistoryStore>? historyFactory = null)
        {
            F = new(historyFactory:historyFactory);
            Ready = ready;
            F.Engine.BackendFactory = cfg => {
                var audio = new FakeAudioCaptureWorker(raiseAudioReadyOnStart: Ready,holdFileOpen:AudioFixture is not null,holdFileOpenCopyFrom:AudioFixture);
                var video = new FakeVideoCaptureWorker(firstFrameDelayMs:!cfg.AudioRequested && !Ready ? -1 : 0);
                var backend = new AvSplitCaptureBackend(new CountingFactory(this, audio, video), runner ?? new FakeExternalProcessRunner(), new TempRetentionPolicy(F.Root));
                Workers.Add((backend,audio,video));
                if(!cfg.AudioRequested)return ((ICaptureBackend)new SilentBackend(this,video),cfg.SourceKind=="region" ? "ffmpeg-region" : "ffmpeg");
                return (backend, cfg.SourceKind == "region" ? cfg.AudioRequested ? "ffmpeg-region-av-split" : "ffmpeg-region" : cfg.AudioRequested ? "ffmpeg-av-split" : "ffmpeg");
            };
        }
        internal JsonObject Request(string directory, string name, string role, Recording? parent = null, bool av = true)
        {
            var request = F.Request(filename:name);request["output"]!["directory"]=directory;
            if(av)request["audio"]=AudioRequest("system_audio");
            request["nested"] = new JsonObject { ["role"]=role,["session_id"]="ownership",["parent_recording_id"]=parent?.Id };
            return request;
        }
        internal async Task<Recording> Accept(JsonObject request)
        {
            using var result=await F.Post("/api/v1/recordings?creation_wait_ms=0",request);
            Assert.True(result.IsSuccessStatusCode,await result.Content.ReadAsStringAsync());
            var data=JsonNode.Parse(await result.Content.ReadAsStringAsync())!["data"]!;
            return F.Engine._recs[data["recording_id"]!.GetValue<string>()];
        }
        public void Dispose() { F.Dispose();foreach(var worker in Workers)worker.Backend.Dispose(); }
    }
    private sealed class SilentBackend(Pair pair,FakeVideoCaptureWorker video) : ICaptureBackend,IFirstFrameObservableCaptureBackend
    {
        public event Action<FirstFrameObservation>? FirstFrameObserved { add=>video.FirstFrameObserved+=value;remove=>video.FirstFrameObserved-=value; }
        public void Start(CaptureConfig cfg,CaptureAuthorizationProof proof) { proof.RequireConsumed();Interlocked.Increment(ref pair.Starts);video.Start(cfg,cfg.OutputPath); }
        public OutputMeta Stop() { pair.BeforeVideoStop?.Invoke();return video.Stop(); }
        public void Cancel()=>Stop();
        public void OnNaturalExit(Action<int,OutputMeta> callback) { }
        public int ExitCode=>video.ExitCode;public void Dispose()=>video.Dispose();
    }
    private sealed class CountingFactory(Pair pair, FakeAudioCaptureWorker audio, FakeVideoCaptureWorker video) : IAvWorkerFactory
    {
        public IAudioCaptureWorker CreateAudioWorker() => CreateAudioWorker(AudioCaptureSourceKind.Microphone);
        public IAudioCaptureWorker CreateAudioWorker(AudioCaptureSourceKind kind) { pair.BeforeAudio?.Invoke();return new CountAudio(pair,audio); }
        public IVideoCaptureWorker CreateVideoWorker() => new CountVideo(pair,video);
    }
    private sealed class CountAudio(Pair pair,FakeAudioCaptureWorker inner) : IAudioCaptureWorker
    {
        public event Action? AudioReady { add=>inner.AudioReady+=value;remove=>inner.AudioReady-=value; }
        public event Action<int,string>? NaturalExit { add=>inner.NaturalExit+=value;remove=>inner.NaturalExit-=value; }
        public bool IsAudioReady=>inner.IsAudioReady;public bool HasExited=>inner.HasExited;public int ExitCode=>inner.ExitCode;
        public DateTime? ReadyAtUtc=>inner.ReadyAtUtc;public long MediaStartAnchorTicks=>inner.MediaStartAnchorTicks;
        public string? OutputPath=>inner.OutputPath;public long RuntimeAudioLostAtMs=>inner.RuntimeAudioLostAtMs;
        public string GetStderrLog()=>inner.GetStderrLog();
        public void SetMicrophoneStatusProvider(IMicrophoneStatusProvider? provider)=>inner.SetMicrophoneStatusProvider(provider);
        public void Start(CaptureConfig cfg,string path) { Interlocked.Increment(ref pair.Starts);inner.Start(cfg,path); }
        public void Stop() { pair.BeforeAudioStop?.Invoke();inner.Stop(); }
        public bool WaitForExit(TimeSpan timeout)=>inner.WaitForExit(timeout);public void Dispose()=>inner.Dispose();
    }
    private sealed class CountVideo(Pair pair,FakeVideoCaptureWorker inner) : IVideoCaptureWorker
    {
        public event Action<FirstFrameObservation>? FirstFrameObserved { add=>inner.FirstFrameObserved+=value;remove=>inner.FirstFrameObserved-=value; }
        public event Action<int,string>? NaturalExit { add=>inner.NaturalExit+=value;remove=>inner.NaturalExit-=value; }
        public bool HasExited=>inner.HasExited;public int ExitCode=>inner.ExitCode;public long? FirstProgressFrame=>inner.FirstProgressFrame;
        public string? OutputPath=>inner.OutputPath;
        public long? FirstProgressOutTimeUs=>inner.FirstProgressOutTimeUs;public double? ProgressAnchorDeltaMs=>inner.ProgressAnchorDeltaMs;
        public long LaunchAnchorTicks=>inner.LaunchAnchorTicks;public long FirstFrameAnchorTicks=>inner.FirstFrameAnchorTicks;
        public void Start(CaptureConfig cfg,string path) { Interlocked.Increment(ref pair.Starts);if(pair.VideoFixture is not null)File.Copy(pair.VideoFixture,path);inner.Start(cfg,path); }
        public OutputMeta Stop()=>inner.Stop();public bool WaitForExit(TimeSpan timeout)=>inner.WaitForExit(timeout);
        public string GetStderrLog()=>inner.GetStderrLog();public void Dispose()=>inner.Dispose();
    }

    [Theory]
    [InlineData(true,true,"exact")] [InlineData(true,false,"exact")] [InlineData(false,true,"exact")]
    [InlineData(true,true,"case")] [InlineData(true,true,"segments")] [InlineData(true,true,"slashes")]
    public async Task FormalApiSamePathRejectsBeforeAnyConflictingWorker(bool outerAv,bool innerAv,string spelling)
    {
        using var p=new Pair();var f=p.F;var outer=await p.Accept(p.Request(f.Root,"same.mp4","outer",av:outerAv));
        f.Tray.Approve();await Until(()=>outer.State==RecState.recording);var starts=p.Starts;
        var dir=spelling switch { "case"=>f.Root.ToUpperInvariant(),"segments"=>Path.Combine(f.Root,"..",Path.GetFileName(f.Root)),"slashes"=>f.Root.Replace('\\','/'),_=>f.Root };
        using var response=await f.Post("/api/v1/recordings?creation_wait_ms=0",p.Request(dir,"same.mp4","inner",outer,innerAv));
        Assert.Equal(HttpStatusCode.Conflict,response.StatusCode);Assert.Contains("OUTPUT_PATH_IN_USE",await response.Content.ReadAsStringAsync());
        Assert.Single(f.Engine._recs);Assert.Empty(f.Tray.Confirmations);Assert.Equal(starts,p.Starts);Assert.Equal(RecState.recording,outer.State);
        Assert.Equal("recording",f.History.Read(outer.Id)!.Status);Assert.False(File.Exists(outer.OutputPath));
        Assert.False(p.Workers[0].Video.StopCalled);if(outerAv)Assert.False(p.Workers[0].Audio.StopCalled);
    }

    [Theory] [InlineData(true)] [InlineData(false)]
    public async Task ApprovalCollisionRejectsWithoutChangingOldRunOrStartingWorker(bool innerAv)
    {
        using var p=new Pair();var f=p.F;var outer=await p.Accept(p.Request(f.Root,"same.mp4","outer"));
        f.Tray.Approve();await Until(()=>outer.State==RecState.recording);
        var child=await p.Accept(p.Request(Path.Combine(f.Root,"other"),"same.mp4","inner",outer,innerAv));
        var old=child.OutputPath;var starts=p.Starts;f.Tray.Approve(f.Root);
        Assert.Equal(RecState.rejected,child.State);Assert.Equal("OUTPUT_PATH_IN_USE",child.StopReason);Assert.Equal(old,child.OutputPath);
        Assert.Equal(starts,p.Starts);Assert.Single(p.Workers);Assert.Equal(RecState.recording,outer.State);
        Assert.Equal("OUTPUT_PATH_IN_USE",f.History.Read(child.Id)!.ReasonCode);Assert.Equal(0,f.AnchorCount(child.Id));
        Assert.False(p.Workers[0].Audio.StopCalled);Assert.False(p.Workers[0].Video.StopCalled);
    }

    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task PendingAndPreparingCancellationReleaseOnlyAfterOwnedCleanup(bool preparing)
    {
        using var p=new Pair(ready:false);var f=p.F;var old=await p.Accept(p.Request(f.Root,"same.mp4","outer"));
        if(preparing) { f.Tray.Approve();Assert.Equal(RecState.preparing,old.State); }
        f.Engine.Stop(old.Id,"user_requested");Assert.Equal(RecState.cancelled,old.State);
        if(preparing) { Assert.True(p.Workers[0].Audio.StopCalled);Assert.True(p.Workers[0].Backend.IsOutputSettled); }
        else Assert.Equal(0,p.Starts);
        var next=await p.Accept(p.Request(f.Root,"same.mp4","outer"));Assert.NotEqual(old.Id,next.Id);Assert.Equal(old.OutputPath,next.OutputPath);
        Assert.Equal(RecState.pending_confirmation,next.State);
    }

    [Fact]
    public async Task ApprovalCannotRenameAroundAnOccupiedPathEvenWhenAFileAlreadyExists()
    {
        using var p=new Pair();var f=p.F;var parent=await p.Accept(p.Request(f.Root,"same.mp4","outer"));f.Tray.Approve();await Until(()=>parent.State==RecState.recording);
        var bytes=new byte[]{3,3,5,7};File.WriteAllBytes(parent.OutputPath,bytes);
        var child=await p.Accept(p.Request(Path.Combine(f.Root,"other"),"same.mp4","inner",parent));var original=child.OutputPath;var starts=p.Starts;
        f.Tray.Approve(f.Root);Assert.Equal(RecState.rejected,child.State);Assert.Equal("OUTPUT_PATH_IN_USE",child.StopReason);
        Assert.Equal(original,child.OutputPath);Assert.Equal(starts,p.Starts);Assert.Equal(RecState.recording,parent.State);
        Assert.Equal(bytes,File.ReadAllBytes(parent.OutputPath));Assert.False(File.Exists(Path.Combine(f.Root,"same-1.mp4")));
        Assert.Equal("OUTPUT_PATH_IN_USE",f.History.Read(child.Id)!.ReasonCode);Assert.Equal(0,f.AnchorCount(child.Id));
        Assert.False(p.Workers[0].Audio.StopCalled);Assert.False(p.Workers[0].Video.StopCalled);
    }

    [Fact]
    public async Task FailedPhysicalStartDoesNotLeavePathOwnedForever()
    {
        using var p=new Pair();var f=p.F;var old=await p.Accept(p.Request(f.Root,"same.mp4","outer"));
        p.BeforeAudio=()=>throw new IOException("owned start failure");f.Tray.Approve();Assert.Equal(RecState.failed,old.State);
        Assert.True(p.Workers[0].Backend.IsOutputSettled);Assert.Equal(0,p.Starts);
        p.BeforeAudio=null;var next=await p.Accept(p.Request(f.Root,"same.mp4","outer"));Assert.Equal(old.OutputPath,next.OutputPath);
    }

    [Fact]
    public async Task ConcurrentInnerRegistrationAcceptsOnlyOneAndDoesNotStartTheLoser()
    {
        using var p=new Pair();var f=p.F;var parent=await p.Accept(p.Request(f.Root,"parent.mp4","outer"));f.Tray.Approve();await Until(()=>parent.State==RecState.recording);
        var request=p.Request(f.Root,"inner.mp4","inner",parent);var replies=await Task.WhenAll(
            f.Post("/api/v1/recordings?creation_wait_ms=0",request.DeepClone()),f.Post("/api/v1/recordings?creation_wait_ms=0",request.DeepClone()));
        try {
            Assert.Single(replies,x=>x.IsSuccessStatusCode);Assert.Single(replies,x=>x.StatusCode==HttpStatusCode.Conflict);
            Assert.Equal(2,f.Engine._recs.Count);Assert.Equal(2,p.Starts);Assert.Single(p.Workers);Assert.Single(f.Tray.Confirmations);
            Assert.Equal(RecState.recording,parent.State);Assert.False(p.Workers[0].Audio.StopCalled);Assert.False(p.Workers[0].Video.StopCalled);
        } finally { foreach(var reply in replies)reply.Dispose(); }
    }

    [Fact]
    public async Task TwoConcurrentApprovalsHaveExactlyOnePathWinner()
    {
        using var p=new Pair();var f=p.F;
        var parent=await p.Accept(p.Request(Path.Combine(f.Root,"parent"),"parent.mp4","outer"));
        f.Tray.Approve();await Until(()=>parent.State==RecState.recording);p.Ready=false;
        var child=await p.Accept(p.Request(Path.Combine(f.Root,"child"),"same.mp4","inner",parent));
        f.Engine.Stop(parent.Id,"user_requested");await Until(()=>parent.IsFinalized);
        var outer=await p.Accept(p.Request(Path.Combine(f.Root,"next"),"same.mp4","outer"));
        Assert.True(f.Tray.Confirmations.TryDequeue(out var a));Assert.True(f.Tray.Confirmations.TryDequeue(out var b));
        using var barrier=new Barrier(2);var target=Path.Combine(f.Root,"winner");
        await Task.WhenAll(Task.Run(()=> { Assert.True(barrier.SignalAndWait(TimeSpan.FromSeconds(5)));a!(ConfirmationDecision.Approve(target)); }),
            Task.Run(()=> { Assert.True(barrier.SignalAndWait(TimeSpan.FromSeconds(5)));b!(ConfirmationDecision.Approve(target)); })).WaitAsync(TimeSpan.FromSeconds(10));
        var records=new[]{child,outer};var winner=Assert.Single(records,x=>x.State==RecState.preparing);var loser=Assert.Single(records,x=>x.State==RecState.rejected);
        Assert.Equal("OUTPUT_PATH_IN_USE",loser.StopReason);Assert.Equal(Path.Combine(target,"same.mp4"),winner.OutputPath);
        Assert.Equal(3,p.Starts);Assert.Equal(2,p.Workers.Count);Assert.Equal(0,f.AnchorCount(loser.Id));
        f.Engine.Stop(winner.Id,"user_requested");Assert.True(winner.IsFinalized);
        var next=await p.Accept(p.Request(target,"same.mp4","outer"));Assert.Equal(winner.OutputPath,next.OutputPath);
    }

    [Fact]
    public async Task TerminalCancellationDoesNotReleasePathWhileWorkerCleanupIsStillRunning()
    {
        using var p=new Pair(ready:false);var f=p.F;var old=await p.Accept(p.Request(f.Root,"same.mp4","outer"));f.Tray.Approve();
        using var entered=new ManualResetEventSlim();using var release=new ManualResetEventSlim();
        p.BeforeAudioStop=()=> { entered.Set();Assert.True(release.Wait(TimeSpan.FromSeconds(10))); };
        var stopping=Task.Run(()=>f.Engine.Stop(old.Id,"user_requested"));
        try {
            Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));Assert.True(old.IsFinalized);Assert.False(p.Workers[0].Backend.IsOutputSettled);
            Assert.True(RuntimeOutputTestEngine.Read(f.Engine,old.OutputPath)!.Paths.Single().InUse);
            using var response=await f.Post("/api/v1/recordings?creation_wait_ms=0",p.Request(f.Root,"same.mp4","outer"));
            Assert.Equal(HttpStatusCode.Conflict,response.StatusCode);Assert.Contains("OUTPUT_PATH_IN_USE",await response.Content.ReadAsStringAsync());Assert.Equal(1,p.Starts);
        } finally { release.Set();await stopping.WaitAsync(TimeSpan.FromSeconds(10));p.BeforeAudioStop=null; }
        var next=await p.Accept(p.Request(f.Root,"same.mp4","outer"));Assert.Equal(old.OutputPath,next.OutputPath);
    }

    private sealed class FailedRegistration(IOrdinaryRecordingHistoryStore inner) : IOrdinaryRecordingHistoryStore
    {
        internal bool Fail=true;
        public void Register(OrdinaryRecordingHistorySnapshot snapshot) { if(Fail)throw new IOException("owned registration failure");inner.Register(snapshot); }
        public bool TryPublish(OrdinaryRecordingHistorySnapshot snapshot)=>inner.TryPublish(snapshot);
        public OrdinaryRecordingHistoryPage ReadPage(int limit,string? cursor)=>inner.ReadPage(limit,cursor);
        public OrdinaryRecordingHistorySnapshot? Read(string id)=>inner.Read(id);
        public void RecoverIncomplete(DateTimeOffset now)=>inner.RecoverIncomplete(now);
    }
    [Fact]
    public async Task SilentPartnerCancellationAlsoRetainsPathUntilItsCleanupEnds()
    {
        using var p=new Pair(ready:false);var f=p.F;var old=await p.Accept(p.Request(f.Root,"same.mp4","outer",av:false));f.Tray.Approve();
        await Until(()=>p.Starts==1 && old.State==RecState.preparing);
        using var entered=new ManualResetEventSlim();using var release=new ManualResetEventSlim();
        p.BeforeVideoStop=()=> { entered.Set();Assert.True(release.Wait(TimeSpan.FromSeconds(10))); };
        var stopping=Task.Run(()=>f.Engine.Stop(old.Id,"user_requested"));
        try {
            Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));Assert.True(old.IsFinalized);
            Assert.True(RuntimeOutputTestEngine.Read(f.Engine,old.OutputPath)!.Paths.Single().InUse);
            using var response=await f.Post("/api/v1/recordings?creation_wait_ms=0",p.Request(f.Root,"same.mp4","outer"));
            Assert.Equal(HttpStatusCode.Conflict,response.StatusCode);Assert.Contains("OUTPUT_PATH_IN_USE",await response.Content.ReadAsStringAsync());Assert.Equal(1,p.Starts);
        } finally { release.Set();await stopping.WaitAsync(TimeSpan.FromSeconds(10));p.BeforeVideoStop=null; }
        var next=await p.Accept(p.Request(f.Root,"same.mp4","outer"));Assert.Equal(old.OutputPath,next.OutputPath);Assert.True(old.IsFinalized);
    }
    [Fact]
    public async Task DurableRegistrationFailureDoesNotLeaveAnOutputClaim()
    {
        FailedRegistration? history=null;using var p=new Pair(historyFactory:store=>history=new(store));var f=p.F;
        using(var response=await f.Post("/api/v1/recordings?creation_wait_ms=0",p.Request(f.Root,"same.mp4","outer"))) {
            Assert.Equal(HttpStatusCode.ServiceUnavailable,response.StatusCode);Assert.Contains("RECORDING_HISTORY_UNAVAILABLE",await response.Content.ReadAsStringAsync());
        }
        Assert.Empty(f.Engine._recs);Assert.Equal(0,p.Starts);history!.Fail=false;
        Assert.False(RuntimeOutputTestEngine.Read(f.Engine,Path.Combine(f.Root,"same.mp4"))!.Paths.Single().InUse);
        var next=await p.Accept(p.Request(f.Root,"same.mp4","outer"));Assert.Equal(RecState.pending_confirmation,next.State);
    }

    [Fact]
    public async Task RealPublicationRetainsClaimUntilMuxEndsAndThenAllowsNextRun()
    {
        var runner=new AvMuxOwnershipTests.PausedMux(true,"success");using var p=new Pair(runner:runner);var f=p.F;
        p.VideoFixture=Path.Combine(f.Root,"blue.mp4");p.AudioFixture=Path.Combine(f.Root,"440.wav");
        await AvMuxOwnershipTests.Generate(p.VideoFixture,"color=c=blue:s=64x64:r=15",false);
        await AvMuxOwnershipTests.Generate(p.AudioFixture,"sine=frequency=440:sample_rate=48000",true);
        var request=p.Request(f.Root,"same.mp4","outer");request["duration_seconds"]=1;var old=await p.Accept(request);f.Tray.Approve();await Until(()=>old.State==RecState.recording);
        var ending=Task.Run(()=>f.Engine.Stop(old.Id,"user_requested"));
        try {
            await runner.Written.Task.WaitAsync(TimeSpan.FromSeconds(15));Assert.False(old.IsFinalized);Assert.False(p.Workers[0].Backend.IsOutputSettled);
            Assert.Contains(old.State,new[]{RecState.stopping,RecState.finalizing});
            Assert.True(RuntimeOutputTestEngine.Read(f.Engine,old.OutputPath)!.Paths.Single().InUse);
            using var denied=await f.Post("/api/v1/recordings?creation_wait_ms=0",p.Request(f.Root,"same.mp4","outer"));Assert.Equal(HttpStatusCode.Conflict,denied.StatusCode);Assert.Equal(2,p.Starts);
        } finally { runner.Release.TrySetResult();await ending.WaitAsync(TimeSpan.FromSeconds(20)); }
        Assert.Equal(RecState.completed,old.State);Assert.True(p.Workers[0].Backend.IsOutputSettled);Assert.NotNull(old.LastMeta!.PublicationReceipt);
        // Reading alone cannot perform the business-path retirement even after
        // physical settling; the next admission still owns that responsibility.
        Assert.True(RuntimeOutputTestEngine.Read(f.Engine,old.OutputPath)!.Paths.Single().InUse);
        await Until(()=>f.History.Read(old.Id)?.Bundle?.Status=="ready");Assert.Equal(1,f.AnchorCount(old.Id));await AvMuxOwnershipTests.VerifyBlue440(old.OutputPath);
        var nextRequest=p.Request(f.Root,"same.mp4","outer");nextRequest["output"]!["conflict_policy"]="rename";
        var next=await p.Accept(nextRequest);Assert.Equal(RecState.pending_confirmation,next.State);Assert.NotEqual(old.OutputPath,next.OutputPath);
        var uses=RuntimeOutputTestEngine.Read(f.Engine,old.OutputPath,next.OutputPath)!.Paths;
        Assert.False(uses[0].InUse);Assert.True(uses[1].InUse);
        Assert.True(File.Exists(old.OutputPath));Assert.Equal("completed",f.History.Read(old.Id)!.Status);
    }
}
