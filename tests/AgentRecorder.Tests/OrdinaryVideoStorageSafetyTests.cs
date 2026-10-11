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
public sealed class OrdinaryVideoStorageSafetyTests
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

    private sealed class Backend : ICaptureBackend, IFirstFrameObservableCaptureBackend
    {
        internal int Starts, Aborts;
        internal CaptureConfig? Config;
        internal bool FailStart, DeferFrame;
        internal readonly TaskCompletionSource Started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal Action<int, OutputMeta>? Natural;
        public event Action<FirstFrameObservation>? FirstFrameObserved;
        public void Start(CaptureConfig config, CaptureAuthorizationProof proof)
        {
            Starts++; Config = config;
            Assert.NotNull(config.StorageSafety);
            Assert.NotNull(proof);
            if (FailStart) throw new IOException("owned_backend_start_failure");
            Started.TrySetResult();
            if (!DeferFrame) Frame();
        }
        internal void Frame() => FirstFrameObserved?.Invoke(new FirstFrameObservation { FrameNumber = 1, OutTimeUs = 0 });
        public OutputMeta Stop()
        {
            var committed = Config?.StorageSafety?.TryCommit(() => { }, true) == true;
            return new() { OutputFileExists = committed, SizeBytes = committed ? 2048 : 0, DurationSeconds = 60,
                StopReason = Config?.StorageSafety?.FailureCode };
        }
        public OutputMeta Abort(CaptureAbortReason reason) { Interlocked.Increment(ref Aborts); return Stop(); }
        public void OnNaturalExit(Action<int,OutputMeta> callback) => Natural = callback;
        public int ExitCode => 0;
        public void Dispose() { }
    }

    private sealed class Fixture : IDisposable
    {
        internal readonly string Root = Path.GetFullPath(Path.Combine(TestHelper.ProjectRoot, ".local-data/temp/task334", Guid.NewGuid().ToString("N")));
        internal readonly Provider Capacity = new();
        internal readonly Tray Tray = new();
        internal readonly StorageTestClock Clock = new();
        internal readonly SqliteOperationalStore Store;
        internal readonly SqliteOrdinaryRecordingHistoryStore History;
        internal readonly Audit Audit;
        internal readonly RecordingEngine Engine;
        internal readonly ApiServer Api;
        internal readonly HttpClient Client;
        internal readonly List<Backend> Backends = new();
        private readonly IStorageCapacityProvider _priorProvider = RecordingPreflightChecker.StorageCapacityProvider;
        private readonly string? _priorDir = DataDirResolver.HasOverride ? DataDirResolver.Resolve() : null;
        private readonly Dictionary<string,string?> _environment;
        internal Fixture()
        {
            _environment = new[] { "AGENT_RECORDER_TEST_MODE", "AGENT_RECORDER_DATA_DIR", CaptureBackendSelector.DisplayBackendEnvVar,
                CaptureBackendSelector.RegionBackendEnvVar }.ToDictionary(k => k, Environment.GetEnvironmentVariable);
            Directory.CreateDirectory(Root); DataDirResolver.SetOverride(Root);
            Environment.SetEnvironmentVariable("AGENT_RECORDER_DATA_DIR", Root);
            Environment.SetEnvironmentVariable("AGENT_RECORDER_TEST_MODE", null);
            Environment.SetEnvironmentVariable(CaptureBackendSelector.DisplayBackendEnvVar, null);
            Environment.SetEnvironmentVariable(CaptureBackendSelector.RegionBackendEnvVar, null);
            RecordingPreflightChecker.StorageCapacityProvider = Capacity;
            SystemQuery.SetDisplayTopologyProvider(() => new() { new("display_1", "Synthetic", true,
                new(0,0,1920,1080), 1.0, Display.Fingerprint, DisplayIdentityResolutionStatus.Resolved) });
            Store = new(Path.Combine(Root, "history.db")); Store.Initialize(); History = new(Store);
            Audit = new(Path.Combine(Root,"audit.jsonl"));
            Engine = new(Audit, bundleGenerator: new FfmpegRecordingBundleGenerator()) { CountdownSteps = 0, StorageMonitorDelay = Clock.Delay };
            Engine.ConfigureOrdinaryRecordingHistory(History); Engine.SetTray(Tray);
            Engine.BackendFactory = cfg => { var b = new Backend(); Backends.Add(b); return (b, cfg.SourceKind == "region" ? "ffmpeg-region" : "ffmpeg"); };
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
        internal async Task<HttpResponseMessage> Create(string route, string source)
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
                duration_seconds=60,countdown_seconds=0,output=new { directory=Root, filename=Guid.NewGuid().ToString("N")+".mp4" } });
            return await Post("/api/v1/recordings?creation_wait_ms=0",Request(source));
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
            Api.Stop(); Engine.Dispose(); History.Dispose(); Client.Dispose(); SqliteConnection.ClearAllPools();
            RecordingPreflightChecker.StorageCapacityProvider=_priorProvider;
            SystemQuery.SetDisplayTopologyProvider(null);ApiKeyAuth.ResetForTesting(null);
            if (_priorDir is null) DataDirResolver.ClearOverride(); else DataDirResolver.SetOverride(_priorDir);
            foreach(var pair in _environment) Environment.SetEnvironmentVariable(pair.Key,pair.Value);
        }
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
                f.Tray.Approve(selected);await Observe(()=>rec.State==RecState.recording);
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

    private static ObservedFileIdentity Identity(string path)
    {
        var observation=WindowsReadOnlyFileMetadataProbe.Instance.Observe(path);
        Assert.Equal(FileObservationOutcome.Sampled,observation.Outcome);
        return observation.Before!.Identity!;
    }
    private static Task Observe(Func<bool> condition) => Task.Run(() => Assert.True(SpinWait.SpinUntil(condition,TimeSpan.FromSeconds(10)),"Expected lifecycle observation"));
    private static void OutputOnly(Fixture f,Recording rec)
    {
        var paths=rec.Config.WritePaths!;Assert.Null(paths.AvTempDirectory);Assert.Null(paths.WgcTempRoot);
        Assert.Equal(Path.GetDirectoryName(rec.OutputPath),paths.OutputDirectory);
        Assert.All(f.Capacity.Requests,q=>Assert.Equal(new[] { paths.OutputDirectory },q));
    }

    [Theory]
    [InlineData("audio")] [InlineData("microphone")] [InlineData("wgc")]
    [InlineData("screenshots")] [InlineData("window")]
    public void OtherOrdinaryPoliciesKeepTheirExistingStorageScope(string kind)
    {
        var cfg=new CaptureConfig { SourceKind=kind=="window" ? "window" : "display",Mode=kind=="screenshots" ? "screenshot_series" : "video" };
        if(kind=="audio")cfg.AudioSourceKind=AudioCaptureSourceKind.SystemLoopback;
        if(kind=="microphone")cfg.Microphone=true;
        var backend=kind=="wgc" ? "wgc-display" : "ffmpeg";
        var rec=new Recording { Config=cfg,IsOrdinaryLocalRequest=true,ApprovedCapturePlan=new CapturePlan(
            backend,backend,new(backend,backend,"owned_scope_test","not_run",null,false),"display_capture",cfg.SourceKind,null,0,new(0,0,64,64)) };
        Assert.False(RecordingStoragePolicy.RequiresSafety(rec));Assert.Null(cfg.WritePaths);Assert.Null(cfg.StorageSafety);
    }

    [Theory]
    [InlineData("raw","display",false)] [InlineData("raw","region",false)]
    [InlineData("quick","display",false)] [InlineData("quick","region",false)] [InlineData("profile","region",false)]
    [InlineData("raw","display",true)] [InlineData("raw","region",true)]
    [InlineData("quick","display",true)] [InlineData("quick","region",true)] [InlineData("profile","region",true)]
    public async Task EveryProductRouteRejectsLowOrUnknownCapacityBeforeConfirmationAndBackend(string route,string source,bool unknown)
    {
        using var f=new Fixture();f.Capacity.Free=0;
        if(unknown) f.Capacity.Query=(_,_)=>throw new IOException("owned volume unavailable");
        using var response=await f.Create(route,source);Assert.Equal(HttpStatusCode.BadRequest,response.StatusCode);
        using var body=JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(unknown ? WindowStorageSafety.UnavailableCode : WindowStorageSafety.LowSpaceCode,body.RootElement.GetProperty("error").GetProperty("code").GetString());
        Assert.Empty(f.Engine._recs);Assert.Empty(f.Tray.Confirmations);Assert.Empty(f.Backends);
        Assert.NotEmpty(f.Capacity.Requests);Assert.All(f.Capacity.Requests,q=>Assert.Equal(new[] { f.Root },q));
    }

    [Theory]
    [InlineData("raw","display")] [InlineData("raw","region")] [InlineData("quick","display")]
    [InlineData("quick","region")] [InlineData("profile","region")]
    public async Task EveryProductRouteUsesSameSafetyAfterLocalApproval(string route,string source)
    {
        using var f=new Fixture();using var response=await f.Create(route,source);Assert.True(response.IsSuccessStatusCode,await response.Content.ReadAsStringAsync());
        var rec=f.Single;Assert.Equal(source,rec.SourceType);Assert.Empty(f.Backends);Assert.Null(rec.AuthorizationProof);
        f.Tray.Approve();await Observe(()=>rec.State==RecState.recording);await f.Clock.WaitForDelay();
        Assert.Equal(1,Assert.Single(f.Backends).Starts);OutputOnly(f,rec);
        Assert.Equal(source=="display" ? "ffmpeg" : "ffmpeg-region",rec.BackendType);
        if(route=="profile")Assert.NotNull(rec.FixedRegionProfileReference);
        f.Capacity.Free=0;f.Clock.Tick();await Observe(()=>rec.IsFinalized);f.TerminalOnce(rec,WindowStorageSafety.LowSpaceCode);
        await rec.Config.StorageSafety!.MonitorCompletion.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(rec.Config.StorageSafety.TryAbort(WindowStorageSafety.LowSpaceCode));
        Assert.All(f.Clock.Intervals,t=>Assert.Equal(TimeSpan.FromSeconds(5),t));
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task ApprovalRecheckAndActualBackendBoundaryFailBeforePhysicalStart(bool boundary)
    {
        using var f=new Fixture();await f.Accepted();var rec=f.Single;
        if(boundary)f.Engine.BackendFactory=_=> { f.Capacity.Free=0;var b=new Backend();f.Backends.Add(b);return (b,"ffmpeg"); };
        else f.Capacity.Free=0;
        f.Tray.Approve();await Observe(()=>rec.IsFinalized);Assert.All(f.Backends,b=>Assert.Equal(0,b.Starts));
        f.TerminalOnce(rec,WindowStorageSafety.LowSpaceCode);Assert.Equal(f.Root,Path.GetDirectoryName(rec.OutputPath));
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
            Assert.Equal(new[] { selected },paths);if(outcome=="unavailable")throw new IOException();
            return Task.FromResult<IReadOnlyList<StorageVolumeSample>>(paths.Select(p=>new StorageVolumeSample(p,"new-volume",outcome=="low" ? 0 : 100L*1024*1024*1024)).ToArray()); };
        f.Capacity.Requests.Clear();f.Tray.Approve(selected);
        release.TrySetResult(new[] { new StorageVolumeSample(oldPaths.OutputDirectory,"old-volume",0) });
        Assert.Null(await late.WaitAsync(TimeSpan.FromSeconds(5)));Assert.False(old.TryAbort(WindowStorageSafety.LowSpaceCode));
        await Observe(()=>outcome=="healthy" ? rec.State==RecState.recording : rec.IsFinalized);
        Assert.Equal(selected,Path.GetDirectoryName(rec.OutputPath));Assert.NotSame(oldPaths,rec.Config.WritePaths);
        if(outcome=="healthy") {
            Assert.NotSame(old,rec.Config.StorageSafety);Assert.Null(rec.Config.StorageSafety!.FailureCode);
            await f.Clock.WaitForDelay();f.Engine.Stop(rec.Id,"user_requested");await Observe(()=>rec.IsFinalized);
            await rec.Config.StorageSafety.MonitorCompletion.WaitAsync(TimeSpan.FromSeconds(5));
        } else { Assert.All(f.Backends,b=>Assert.Equal(0,b.Starts));f.TerminalOnce(rec,outcome=="low" ? WindowStorageSafety.LowSpaceCode : WindowStorageSafety.UnavailableCode); }
    }

    [Theory]
    [InlineData("low")] [InlineData("exception")] [InlineData("identity")] [InlineData("timeout")]
    public async Task RuntimeFailureStopsOnceAndCannotBecomeUserCompleted(string fault)
    {
        using var f=new Fixture();await f.Accepted();var rec=f.Single;f.Tray.Approve();await Observe(()=>rec.State==RecState.recording);await f.Clock.WaitForDelay();
        var queryEntered=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        if(fault=="low")f.Capacity.Free=0;
        if(fault=="identity")f.Capacity.Volume="replaced-volume";
        if(fault=="exception")f.Capacity.Query=(_,_)=>throw new IOException();
        if(fault=="timeout")f.Capacity.Query=async (_,token)=> { queryEntered.TrySetResult();await Task.Delay(Timeout.InfiniteTimeSpan,token);return Array.Empty<StorageVolumeSample>(); };
        f.Clock.Tick();if(fault=="timeout")await queryEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await Observe(()=>rec.IsFinalized);var reason=fault=="low" ? WindowStorageSafety.LowSpaceCode : WindowStorageSafety.UnavailableCode;
        f.TerminalOnce(rec,reason);f.Engine.Stop(rec.Id,"user_requested");Assert.Equal(reason,rec.StopReason);
        Assert.False(rec.Config.StorageSafety!.TryAbort(reason));Assert.False(rec.Config.StorageSafety.TryCommit(()=>Assert.Fail("Aborted run published"),true));
        await rec.Config.StorageSafety.MonitorCompletion.WaitAsync(TimeSpan.FromSeconds(5));Assert.Equal(1,Assert.Single(f.Backends).Aborts);
    }

    [Fact]
    public async Task NestedOrdinaryRunsKeepIndependentMonitorsAndHistoryParent()
    {
        using var f=new Fixture();var outer=f.Request();outer["nested"]=new JsonObject { ["role"]="outer",["session_id"]="owned-nested" };
        using(var response=await f.Post("/api/v1/recordings?creation_wait_ms=0",outer))Assert.True(response.IsSuccessStatusCode,await response.Content.ReadAsStringAsync());
        var parent=f.Single;f.Tray.Approve();await Observe(()=>parent.State==RecState.recording);await f.Clock.WaitForDelay();
        var innerClock=new StorageTestClock();f.Engine.StorageMonitorDelay=innerClock.Delay;
        var inner=f.Request("region");inner["nested"]=new JsonObject { ["role"]="inner",["session_id"]="owned-nested",["parent_recording_id"]=parent.Id };
        using(var response=await f.Post("/api/v1/recordings?creation_wait_ms=0",inner))Assert.True(response.IsSuccessStatusCode,await response.Content.ReadAsStringAsync());
        var child=f.Engine._recs.Values.Single(r=>r.Id!=parent.Id);f.Tray.Approve();await Observe(()=>child.State==RecState.recording);await innerClock.WaitForDelay();
        Assert.NotSame(parent.Config.StorageSafety,child.Config.StorageSafety);Assert.Equal(parent.Id,child.ParentRecordingId);
        var firstChildQuery=true;f.Capacity.Query=(paths,_)=> { var free=firstChildQuery ? 0 : 100L*1024*1024*1024;firstChildQuery=false;
            return Task.FromResult<IReadOnlyList<StorageVolumeSample>>(paths.Select(p=>new StorageVolumeSample(p,"approved-volume",free)).ToArray()); };
        innerClock.Tick();await Observe(()=>child.IsFinalized);Assert.Equal(RecState.recording,parent.State);Assert.Null(parent.Config.StorageSafety!.FailureCode);
        f.TerminalOnce(child,WindowStorageSafety.LowSpaceCode);Assert.Equal(parent.Id,f.History.Read(child.Id)!.ParentRecordingId);
        f.Capacity.Query=null;f.Capacity.Free=0;f.Clock.Tick();await Observe(()=>parent.IsFinalized);f.TerminalOnce(parent,WindowStorageSafety.LowSpaceCode);
        await Task.WhenAll(parent.Config.StorageSafety.MonitorCompletion,child.Config.StorageSafety!.MonitorCompletion).WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Theory]
    [InlineData("first_frame")] [InlineData("start_failure")] [InlineData("dispose")]
    public async Task FailureOrHostExitReleasesMonitorDuringPreparation(string scenario)
    {
        using var f=new Fixture();var backend=new Backend { DeferFrame=scenario=="first_frame",FailStart=scenario=="start_failure" };
        f.Engine.BackendFactory=_=>(backend,"ffmpeg");await f.Accepted();var rec=f.Single;f.Tray.Approve();
        if(scenario=="start_failure") { await Observe(()=>rec.IsFinalized);Assert.Equal(RecState.failed,rec.State); }
        else {
            await backend.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));await f.Clock.WaitForDelay();
            if(scenario=="dispose")f.Engine.Dispose();
            else { f.Capacity.Free=0;f.Clock.Tick();await Observe(()=>rec.IsFinalized);backend.Frame();backend.Natural?.Invoke(0,backend.Stop());f.TerminalOnce(rec,WindowStorageSafety.LowSpaceCode); }
        }
        await rec.Config.StorageSafety!.MonitorCompletion.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(rec.Config.StorageSafety.TryAbort(WindowStorageSafety.LowSpaceCode));
    }

    [Theory]
    [InlineData(-1)] [InlineData(0)] [InlineData(1)]
    public async Task OutputOnlyAdmissionUsesTwoVideoEstimatesAndOneInclusiveWatermark(long delta)
    {
        var cfg=new CaptureConfig { OutputPath=@"D:\approved\video.mp4" };
        var paths=CaptureWritePaths.FreezeFfmpegVideoOnly(cfg);var video=WindowStorageSafety.VideoEstimate(60);
        var provider=new Provider { Free=WindowStorageSafety.LowWaterBytes+2*video+delta };
        using var safety=new WindowStorageSafety(paths,60,provider);safety.ReserveFfmpegVideoOnlyPublication();
        var budgets=safety.AdmissionBudgets(await provider.QueryAsync(paths.Directories,default));
        Assert.Equal(WindowStorageSafety.LowWaterBytes+2*video,Assert.Single(budgets).Value);
        Assert.Equal(delta<0 ? WindowStorageSafety.LowSpaceCode : null,await safety.CheckAsync(true));
        Assert.All(provider.Requests,p=>Assert.Equal(new[] { paths.OutputDirectory },p));
    }

    [Theory]
    [InlineData("display")] [InlineData("region")]
    public Task UnusedWgcAndAvTempFailureDoesNotPreventRealPublicationOrBundle(string source) => VerifyRealPublication(source,false);

    [Theory]
    [InlineData("display")] [InlineData("region")]
    public Task OrdinaryUserStopPublishesValidSilentMediaAndBundle(string source) => VerifyRealPublication(source,true);

    private async Task VerifyRealPublication(string source,bool manual)
    {
        using var f=new Fixture();var oldTemp=Environment.GetEnvironmentVariable("TEMP");
        var irrelevant=Path.Combine(f.Root,"unused-temp-is-a-file");File.WriteAllText(irrelevant,"owned sentinel");
        Environment.SetEnvironmentVariable("TEMP",irrelevant);
        try {
            var backend=new FfmpegCaptureBackend { VideoInputArgumentsForTests=cfg=>new[] { "-re","-f","lavfi","-t",manual ? "60" : "1","-i","color=c=black:s=64x64:r=15" } };
            var first=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);backend.FirstFrameObserved+=_=>first.TrySetResult();
            f.Engine.BackendFactory=_=>(backend,source=="display" ? "ffmpeg" : "ffmpeg-region");
            var request=f.Request(source,"valid.mp4");request["duration_seconds"]=manual ? 60 : 1;
            using var response=await f.Post("/api/v1/recordings?creation_wait_ms=0",request);Assert.True(response.IsSuccessStatusCode,await response.Content.ReadAsStringAsync());
            var rec=f.Single;f.Tray.Approve();
            if(manual) { await first.Task.WaitAsync(TimeSpan.FromSeconds(5));await Observe(()=>rec.State==RecState.recording);await Task.Run(()=>f.Engine.Stop(rec.Id,"user_requested")).WaitAsync(TimeSpan.FromSeconds(10)); }
            await Observe(()=>rec.IsFinalized);OutputOnly(f,rec);
            Assert.Equal(RecState.completed,rec.State);Assert.True(rec.Config.StorageSafety!.Committed);
            var meta=rec.LastMeta!;Assert.True(meta.OutputFileExists,meta.StderrLog);Assert.NotNull(meta.PublicationReceipt);
            Assert.Equal("mp4",meta.Container);Assert.Equal("h264",meta.Codec);Assert.False(meta.HasAudioStream);
            Assert.Contains("lavfi",backend.ExecutedArguments!);Assert.DoesNotContain("gdigrab",backend.ExecutedArguments!);
            Assert.StartsWith(f.Root,backend.VideoOnlyStagingPath!);Assert.NotEqual(rec.OutputPath,backend.VideoOnlyStagingPath);
            Assert.False(File.Exists(backend.VideoOnlyStagingPath));Assert.False(rec.Config.StorageSafety.TryAbort(WindowStorageSafety.LowSpaceCode));
            var row=f.History.Read(rec.Id)!;Assert.Equal("completed",row.Status);Assert.Equal(1,f.AnchorCount(rec.Id));
            await Observe(()=>f.History.Read(rec.Id)!.Bundle?.Status=="ready");Assert.True(Directory.Exists(f.History.Read(rec.Id)!.Bundle!.Path));
            await rec.Config.StorageSafety.MonitorCompletion.WaitAsync(TimeSpan.FromSeconds(5));Assert.Equal("owned sentinel",File.ReadAllText(irrelevant));
        } finally { Environment.SetEnvironmentVariable("TEMP",oldTemp); }
    }

    [Theory]
    [InlineData("display")] [InlineData("region")]
    public async Task RealCommitWinsLateFailureCandidateWithoutChangingCompletedHistory(string source)
    {
        using var f=new Fixture();var entered=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release=new ManualResetEventSlim();using var candidateEntered=new ManualResetEventSlim();Task? losingFailure=null;
        var publisher=new StagingToFinalPublisher(new PhysicalFileSystemOperations());
        var backend=new FfmpegCaptureBackend { VideoInputArgumentsForTests=_=>new[] { "-f","lavfi","-t","1","-i","color=c=black:s=64x64:r=15" },VideoOnlyPublisherFactory=_=>publisher };
        f.Engine.BackendFactory=_=>(backend,source=="display" ? "ffmpeg" : "ffmpeg-region");
        var request=f.Request(source,"commit-winner.mp4");request["duration_seconds"]=1;
        using var response=await f.Post("/api/v1/recordings?creation_wait_ms=0",request);Assert.True(response.IsSuccessStatusCode,await response.Content.ReadAsStringAsync());
        var rec=f.Single;
        publisher.BeforeOriginalFlushForTests=_=> {
            var safety=rec.Config.StorageSafety!;
            var first=true;
            f.Capacity.Query=(paths,_)=> { var free=first ? 0 : 100L*1024*1024*1024;first=false;
                return Task.FromResult<IReadOnlyList<StorageVolumeSample>>(paths.Select(p=>new StorageVolumeSample(p,"approved-volume",free)).ToArray()); };
            safety.BeforeFailureClaimForTests=()=> { entered.TrySetResult();candidateEntered.Set();Assert.True(release.Wait(TimeSpan.FromSeconds(5))); };
            losingFailure=Task.Run(safety.EnsureRuntimeCapacity);
            Assert.True(candidateEntered.Wait(TimeSpan.FromSeconds(5)));
        };
        try {
            f.Tray.Approve();await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));await Observe(()=>rec.IsFinalized);
            Assert.True(rec.Config.StorageSafety!.Committed);Assert.Equal(RecState.completed,rec.State);
            var winner=f.History.Read(rec.Id)!;release.Set();await losingFailure!.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Null(rec.Config.StorageSafety.FailureCode);Assert.False(rec.Config.StorageSafety.TryAbort(WindowStorageSafety.LowSpaceCode));
            Assert.Equal("completed",f.History.Read(rec.Id)!.Status);Assert.Equal(winner.ReasonCode,f.History.Read(rec.Id)!.ReasonCode);
            Assert.NotNull(rec.LastMeta!.PublicationReceipt);Assert.Equal(1,f.AnchorCount(rec.Id));Assert.Empty(f.Tray.Failures);
        } finally { release.Set(); }
    }

    [Fact]
    public async Task CapacityFailureDuringUserStopRemainsFailedWithSingleHistoryTerminal()
    {
        using var f=new Fixture();await f.Accepted();var rec=f.Single;f.Tray.Approve();
        await Observe(()=>rec.State==RecState.recording);await f.Clock.WaitForDelay();
        f.Engine.BeforeStopForTests=_=> {
            f.Capacity.Free=0;
            var failure=Assert.Throws<StorageSafetyException>(rec.Config.StorageSafety!.EnsureRuntimeCapacity);
            Assert.Equal(WindowStorageSafety.LowSpaceCode,failure.Code);
        };
        await Task.Run(()=>f.Engine.Stop(rec.Id,"user_requested")).WaitAsync(TimeSpan.FromSeconds(5));
        await Observe(()=>rec.IsFinalized);f.TerminalOnce(rec,WindowStorageSafety.LowSpaceCode);
        var winner=f.History.Read(rec.Id)!;Assert.False(rec.Config.StorageSafety!.TryAbort(WindowStorageSafety.UnavailableCode));
        Assert.Equal(winner,f.History.Read(rec.Id));await rec.Config.StorageSafety.MonitorCompletion.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Theory]
    [InlineData("low")] [InlineData("exception")] [InlineData("identity")] [InlineData("copy")]
    public async Task RealLavfiStorageFailurePreservesOldFinalAndNeverPublishesReady(string fault)
    {
        using var f=new Fixture();var final=Path.Combine(f.Root,"old.mp4");var bytes=Encoding.UTF8.GetBytes("owned old final bytes");
        ObservedFileIdentity? identity=null;
        var backend=new FfmpegCaptureBackend { VideoInputArgumentsForTests=_=>new[] { "-f","lavfi","-t","1","-i","color=c=black:s=64x64:r=15" } };
        f.Engine.BackendFactory=_=>(backend,"ffmpeg");
        backend.BeforeStagingCreateForTests=_=> { File.WriteAllBytes(final,bytes);identity=Identity(final); };
        var request=f.Request("display","old.mp4");request["duration_seconds"]=1;
        using var response=await f.Post("/api/v1/recordings?creation_wait_ms=0",request);Assert.True(response.IsSuccessStatusCode,await response.Content.ReadAsStringAsync());
        var rec=f.Single;
        backend.BeforeVideoOnlyFinalizeForTests=_=> {
            if(fault=="low")f.Capacity.Free=0;
            if(fault=="exception")f.Capacity.Query=(_,_)=>throw new IOException();
            if(fault=="identity")f.Capacity.Volume="changed-volume";
        };
        if(fault=="copy") {
            var publisher=new StagingToFinalPublisher(new PhysicalFileSystemOperations());
            publisher.BeforeOriginalFlushForTests=_=> { f.Capacity.Free=WindowStorageSafety.LowWaterBytes-1;rec.Config.StorageSafety!.EnsureRuntimeCapacity(); };
            backend.VideoOnlyPublisherFactory=_=>publisher;
        }
        f.Tray.Approve();await Observe(()=>rec.IsFinalized);f.TerminalOnce(rec,fault is "low" or "copy" ? WindowStorageSafety.LowSpaceCode : WindowStorageSafety.UnavailableCode);
        Assert.Null(rec.LastMeta!.PublicationReceipt);Assert.False(rec.Config.StorageSafety!.Committed);
        Assert.Equal(bytes,File.ReadAllBytes(final));Assert.Equal(identity,Identity(final));
        await rec.Config.StorageSafety.MonitorCompletion.WaitAsync(TimeSpan.FromSeconds(5));
    }

    private static string FormalHostFolder(string host)
    {
        var configuration = new DirectoryInfo(AppContext.BaseDirectory).Parent!;
        Assert.Equal("Release", configuration.Name);
        var build = configuration.Parent!.Name;
        var folder = Path.GetFullPath(Path.Combine(TestHelper.ProjectRoot,
            $"src/AgentRecorder.{host}/bin/{build}/Release/net8.0-windows10.0.19041.0"));
        Assert.True(File.Exists(Path.Combine(folder, $"AgentRecorder.{host}.exe")), folder);
        return folder;
    }

    [Theory]
    [InlineData("App")] [InlineData("Headless")]
    public async Task IsolatedFormalHostsRunNativeProbeWithoutHostInitialization(string host)
    {
        var folder=FormalHostFolder(host);
        if(host=="Headless")Assert.False(File.Exists(Path.Combine(folder,"AgentRecorder.App.exe")));
        var root=Path.GetFullPath(Path.Combine(TestHelper.ProjectRoot,".local-data/temp/task334",Guid.NewGuid().ToString("N")));Directory.CreateDirectory(root);
        var provider=new ProcessStorageCapacityProvider { ExecutablePath=()=>Path.Combine(folder,$"AgentRecorder.{host}.exe") };
        var saved=Environment.GetEnvironmentVariable("AGENT_RECORDER_DATA_DIR");Environment.SetEnvironmentVariable("AGENT_RECORDER_DATA_DIR",root);
        try {
            using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(10));var missing=Path.Combine(root,"not-created");
            var sample=Assert.Single(await provider.QueryAsync(new[] { missing },timeout.Token));
            Assert.Equal(WindowsStorageCapacityProbe.Query(root).VolumeIdentity,sample.VolumeIdentity);Assert.True(sample.AvailableBytes>0);
            Assert.Empty(Directory.EnumerateFileSystemEntries(root));Assert.Throws<ArgumentException>(()=>Process.GetProcessById(provider.LastProbeProcessId));
            using var child=new Process { StartInfo=new(Path.Combine(folder,$"AgentRecorder.{host}.exe"),"--internal-storage-capacity-probe") {
                UseShellExecute=false,CreateNoWindow=true,RedirectStandardInput=true,RedirectStandardOutput=true } };
            Assert.True(child.Start());await child.StandardInput.WriteLineAsync("[]");child.StandardInput.Close();
            await child.WaitForExitAsync(timeout.Token);Assert.Equal(64,child.ExitCode);Assert.Equal("",await child.StandardOutput.ReadToEndAsync());
            Assert.Empty(Directory.EnumerateFileSystemEntries(root));
        } finally { Environment.SetEnvironmentVariable("AGENT_RECORDER_DATA_DIR",saved); }
    }

    [Theory]
    [InlineData("App")] [InlineData("Headless")]
    public async Task FormalProbeProtocolDeadlineTerminatesOnlyOwnedWaitingChild(string host)
    {
        var executable=Path.Combine(FormalHostFolder(host), $"AgentRecorder.{host}.exe");
        var root=Path.GetFullPath(Path.Combine(TestHelper.ProjectRoot,".local-data/temp/task334",Guid.NewGuid().ToString("N")));Directory.CreateDirectory(root);
        Process Child() => new() { StartInfo=new(executable,"--internal-storage-capacity-probe") {
            UseShellExecute=false,CreateNoWindow=true,RedirectStandardInput=true,RedirectStandardOutput=true,
            Environment={ ["AGENT_RECORDER_DATA_DIR"]=root } } };
        using var owned=Child();using var independent=Child();
        try {
            Assert.True(owned.Start());Assert.True(independent.Start());
            // Waiting on the protocol's input is real; no redirector or real disk is hung.
            await Assert.ThrowsAsync<TimeoutException>(()=>owned.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(2)));
            owned.Kill();await owned.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(independent.HasExited);
            await independent.StandardInput.WriteLineAsync(JsonSerializer.Serialize(new[] { root }));independent.StandardInput.Close();
            await independent.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));Assert.Equal(0,independent.ExitCode);
            var output=await independent.StandardOutput.ReadToEndAsync();Assert.InRange(output.Length,1,32767);
            Assert.Single(JsonSerializer.Deserialize<StorageVolumeSample[]>(output)!);Assert.Empty(Directory.EnumerateFileSystemEntries(root));
        } finally {
            foreach(var child in new[] { owned,independent }) { try { if(!child.HasExited)child.Kill();await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5)); } catch(InvalidOperationException) { } }
        }
    }
}
