using System.Diagnostics;
using System.Net;
using System.Reflection;
using System.Text.Json;
using System.Windows.Forms;
using AgentRecorder.Api;
using AgentRecorder.App;
using AgentRecorder.Core;
using AgentRecorder.Headless;
using AgentRecorder.Infrastructure;
using AgentRecorder.Logging;
using Xunit;
using static AgentRecorder.Tests.ObservationHealthGatewayTests;
using static AgentRecorder.Tests.ObservationControllerTests;

namespace AgentRecorder.Tests;

[Collection("NonParallel-AgentRecorderDataDir")]
public sealed class ObservationHealthApiTests : IDisposable
{
    private readonly string _root=Path.GetFullPath(Path.Combine(".local-data/temp/task318",Guid.NewGuid().ToString("N")));
    private readonly string? _prior=Environment.GetEnvironmentVariable("AGENT_RECORDER_DATA_DIR");
    private ApiServer? _server;
    public ObservationHealthApiTests()
    {
        Directory.CreateDirectory(_root);DataDirResolver.SetOverride(_root);
        Environment.SetEnvironmentVariable("AGENT_RECORDER_DATA_DIR",_root);ApiKeyAuth.InitializeForTesting(_root);
    }
    public void Dispose(){_server?.Stop();DataDirResolver.ClearOverride();Environment.SetEnvironmentVariable("AGENT_RECORDER_DATA_DIR",_prior);Directory.Delete(_root,true);}
    private ApiServer Server(ITrayContext? tray=null,RuntimeReadiness? readiness=null)
    {
        var audit=new AuditFake();var engine=new RecordingEngine(audit);tray??=new HeadlessTrayContext(audit);engine.SetTray(tray);
        engine.BackendFactory=_=>throw new InvalidOperationException("SECRET_CAPTURE_FORBIDDEN");
        return _server=new ApiServer(engine,audit,tray,readiness,null,null,null,null,null,null,null,null,0);
    }
    private HttpClient Client(bool auth=true)
    {
        var client=new HttpClient(new HttpClientHandler{UseProxy=false}) {BaseAddress=new Uri($"http://127.0.0.1:{_server!.BoundPort}"),Timeout=TimeSpan.FromSeconds(10)};
        if(auth)client.DefaultRequestHeaders.Add("X-Agent-Recorder-Key",ApiKeyAuth.CurrentApiKey);return client;
    }
    private static async Task<(JsonElement Data,string Text)> Get(HttpClient client,string path="/api/v1/health")
    {
        using var response=await client.GetAsync(path);Assert.Equal(HttpStatusCode.OK,response.StatusCode);
        var text=await response.Content.ReadAsStringAsync();using var document=JsonDocument.Parse(text);
        Assert.True(document.RootElement.GetProperty("ok").GetBoolean());Assert.StartsWith("req_",document.RootElement.GetProperty("request_id").GetString());
        return (document.RootElement.GetProperty("data").Clone(),text);
    }
    private Dictionary<string,byte[]> Files()=>Directory.GetFiles(_root,"*",SearchOption.AllDirectories).ToDictionary(x=>x,File.ReadAllBytes);
    private void Unchanged(Dictionary<string,byte[]> before)
    {Assert.Equal(before.Keys.Order(),Files().Keys.Order());foreach(var pair in before)Assert.Equal(pair.Value,File.ReadAllBytes(pair.Key));}
    [Fact]
    public async Task MissingAndWrongKeyNeverCallProviderAndReturnExisting401Envelope()
    {
        var provider=new ThrowingProvider();var server=Server();server.SetObservationHealthGateway(provider);server.Start();using var client=Client(false);
        foreach(var wrong in new[]{false,true})
        {
            if(wrong)client.DefaultRequestHeaders.Add("X-Agent-Recorder-Key","WRONG_SYNTHETIC_KEY");
            using var response=await client.GetAsync("/api/v1/health?refresh=true");Assert.Equal(HttpStatusCode.Unauthorized,response.StatusCode);
            var text=await response.Content.ReadAsStringAsync();using var doc=JsonDocument.Parse(text);Assert.False(doc.RootElement.GetProperty("ok").GetBoolean());
            Assert.NotNull(doc.RootElement.GetProperty("request_id").GetString());Assert.DoesNotContain(_root,text);Assert.DoesNotContain("SECRET",text);
        }
        Assert.Equal(0,provider.Calls);
    }
    [Fact]
    public async Task NoProviderReturnsNotCheckedAndDiscoveryDoesNotSample()
    {
        var server=Server();server.Start();using var client=Client();var (data,text)=await Get(client);
        Assert.Equal("not_checked",data.GetProperty("sample_status").GetString());Assert.Equal("listening",data.GetProperty("instance").GetProperty("ready_state").GetString());
        Assert.Equal(server.BoundPort,data.GetProperty("instance").GetProperty("listening_port").GetInt32());Assert.DoesNotContain("healthy",text);Assert.DoesNotContain("can_record",text);
        var provider=new ThrowingProvider();server.SetObservationHealthGateway(provider);var (caps,_)=await Get(client,"/api/v1/capabilities");
        var health=caps.GetProperty("health");Assert.Equal("/api/v1/health",health.GetProperty("endpoint").GetString());Assert.True(health.GetProperty("requires_api_key").GetBoolean());Assert.True(health.GetProperty("read_only").GetBoolean());Assert.Equal(0,provider.Calls);
    }
    [Theory]
    [InlineData(false)][InlineData(true)]
    public async Task ProviderOrProjectionExceptionsNeverReachRawApiFallback(bool badProjection)
    {
        var provider=new ThrowingProvider{BadProjection=badProjection};var server=Server();server.SetObservationHealthGateway(provider);server.Start();using var client=Client();var (data,text)=await Get(client);
        Assert.Equal("failed",data.GetProperty("sample_status").GetString());Assert.DoesNotContain("SECRET",text);Assert.DoesNotContain("stack",text);Assert.Equal(1,provider.Calls);
    }
    [Fact]
    public async Task FormalHeadlessUsesFrozenRootActualListenerAndNoUiOrDeviceClaims()
    {
        using var readiness=new RuntimeReadiness("headless",0);var server=Server(readiness:readiness);
        var path=Path.Combine(_root,"runtime","ready.json");Directory.CreateDirectory(Path.GetDirectoryName(path)!);File.WriteAllText(path,"SECRET_OLD_READY");
        var gateway=AgentRecorder.Headless.Program.CreateObservationHealthGateway(server,readiness,_root,Probes());server.SetObservationHealthGateway(gateway);
        Assert.Equal("not_started",ObservationHealthResponse.From(await gateway.ReadAsync(default)).Instance.ReadyState);
        server.Start();using var client=Client();var (listening,_)=await Get(client);Assert.Equal("listening",listening.GetProperty("instance").GetProperty("ready_state").GetString());
        readiness.MarkReady();var before=Files();var other=Path.Combine(_root,"other");Directory.CreateDirectory(other);DataDirResolver.SetOverride(other);
        try
        {
            var (data,text)=await Get(client,"/api/v1/health?refresh=true&watch=true&timeout=100000&unknown=ignored");
            var instance=data.GetProperty("instance");Assert.Equal("complete",data.GetProperty("sample_status").GetString());Assert.Equal("headless",instance.GetProperty("mode").GetString());Assert.Equal(ApiServer.BuildVersion,instance.GetProperty("version").GetString());
            Assert.Equal(Environment.ProcessId,instance.GetProperty("pid").GetInt32());Assert.Equal(server.BoundPort,instance.GetProperty("listening_port").GetInt32());Assert.Equal("ready",instance.GetProperty("ready_state").GetString());Assert.Equal(_root,instance.GetProperty("data_directory").GetString());
            Assert.False(data.GetProperty("desktop").GetProperty("ui_required").GetBoolean());Assert.Equal("unknown",data.GetProperty("desktop").GetProperty("startup").GetProperty("state").GetString());
            foreach(var key in new[]{"stop","chapter"})Assert.Equal("unsupported",data.GetProperty("hotkeys").GetProperty(key).GetProperty("state").GetString());
            foreach(var key in new[]{"microphone","system_audio"}){var audio=data.GetProperty("backends").GetProperty(key);Assert.True(audio.GetProperty("supported").GetBoolean());Assert.Equal("not_checked",audio.GetProperty("device_check").GetString());}
            Assert.Equal("path_only",data.GetProperty("tools").GetProperty("ffmpeg").GetProperty("reason_code").GetString());Assert.DoesNotContain("SECRET",text);Assert.DoesNotContain(ApiKeyAuth.CurrentApiKey!,text);Unchanged(before);
        }finally{DataDirResolver.SetOverride(_root);}
        server.Stop();Assert.Equal("stopped",ObservationHealthResponse.From(await gateway.ReadAsync(default)).Instance.ReadyState);
    }
    [Fact]
    public void FormalAppHttpBeforeWindowCloseReopenLanguageAndUiShareProvider()
    {
        ObservationHealthWindowTests.Sta(()=>
        {
            var audit=new AuditFake();var engine=new RecordingEngine(audit);using var tray=new TrayContext(engine,audit,FakeGlobalStopHotkeyFactory.Create(),uiTextProvider:new UiTextProvider(UiLanguage.EnUs));engine.SetTray(tray);
            ((NotifyIcon)typeof(TrayContext).GetField("_icon",BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(tray)!).Visible=false;
            using var readiness=new RuntimeReadiness("tray",0,Desktop());var server=Server(tray,readiness);
            var ownerThread=Environment.CurrentManagedThreadId;var probeThread=0;var source=Probes();
            var probes=new ObservationHealthProbes{Ffmpeg=()=>{probeThread=Environment.CurrentManagedThreadId;return source.Ffmpeg();},Ffprobe=source.Ffprobe,FfmpegSource=source.FfmpegSource,WgcHelper=source.WgcHelper,AudioHelper=source.AudioHelper,FileExists=source.FileExists,Desktop=()=>Desktop("DESKTOP_LOCKED")};
            var gateway=AgentRecorder.App.Program.CreateObservationHealthGateway(tray,server,readiness,_root,probes);tray.SetObservationHealthGateway(gateway);server.SetObservationHealthGateway(gateway);server.Start();readiness.MarkReady();using var client=Client();var before=Files();
            var first=Get(client);Pump(()=>first.IsCompleted);var data=first.GetAwaiter().GetResult().Data;
            Assert.Equal("complete",data.GetProperty("sample_status").GetString());Assert.NotEqual(ownerThread,probeThread);
            Assert.Equal("idle",data.GetProperty("hotkeys").GetProperty("chapter").GetProperty("state").GetString());Assert.Equal("available",data.GetProperty("desktop").GetProperty("startup").GetProperty("state").GetString());Assert.Equal("unavailable",data.GetProperty("desktop").GetProperty("current").GetProperty("state").GetString());
            using(var window=tray.GetObservationCenter()){Assert.False(window.Visible);window.Close();}
            var second=Get(client);Pump(()=>second.IsCompleted);Assert.Equal("complete",second.GetAwaiter().GetResult().Data.GetProperty("sample_status").GetString());
            using var reopened=tray.GetObservationCenter();Assert.False(reopened.Visible);
            var sample=gateway.SampleAsync(gateway.CaptureOwner(),default).GetAwaiter().GetResult();
            _=ObservationHealthFormatting.Rows(sample,new UiTextProvider(UiLanguage.ZhCn));var a=ApiResponse.Ok(ObservationHealthResponse.From(sample),"fixed");
            _=ObservationHealthFormatting.Rows(sample,new UiTextProvider(UiLanguage.EnUs));Assert.Equal(a,ApiResponse.Ok(ObservationHealthResponse.From(sample),"fixed"));
            Unchanged(before);Assert.Empty(engine._recs);
        });
    }
    [Fact]
    public void ActualObservationWindowsCannotBypassHttpNativeQuotaWhenClosedAndReopened()
    {
        ObservationHealthWindowTests.Sta(()=>
        {
            var audit=new AuditFake();var engine=new RecordingEngine(audit);using var tray=new TrayContext(engine,audit,FakeGlobalStopHotkeyFactory.Create(),uiTextProvider:new UiTextProvider(UiLanguage.EnUs));engine.SetTray(tray);
            ((NotifyIcon)typeof(TrayContext).GetField("_icon",BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(tray)!).Visible=false;
            using var readiness=new RuntimeReadiness("tray",0,Desktop());var server=Server(tray,readiness);
            using var entered=new ManualResetEventSlim();using var release=new ManualResetEventSlim();var calls=0;var source=Probes();
            var probes=new ObservationHealthProbes{Ffmpeg=()=>{Interlocked.Increment(ref calls);entered.Set();release.Wait();return source.Ffmpeg();},Ffprobe=source.Ffprobe,FileExists=source.FileExists,Desktop=source.Desktop};
            var gateway=AgentRecorder.App.Program.CreateObservationHealthGateway(tray,server,readiness,_root,probes);tray.SetObservationHealthGateway(gateway);server.SetObservationHealthGateway(gateway);server.Start();using var client=Client();var pending=Get(client);
            try
            {
                Pump(()=>entered.IsSet);
                for(var i=0;i<2;i++)
                {
                    using var form=tray.GetObservationCenter();_=form.Handle;var tabs=form.Controls.OfType<TableLayoutPanel>().Single().Controls.OfType<TabControl>().Single();_=tabs.Handle;tabs.SelectedIndex=2;
                    Pump(()=>form.HealthOperationForTests.IsCompleted);form.HealthOperationForTests.GetAwaiter().GetResult();Assert.False(form.Visible);
                    var status=(Label)form.Controls.Find("health_status",true).Single();Assert.Equal(ObservationHealthFormatting.Text(new UiTextProvider(UiLanguage.EnUs),"Busy"),status.Text);
                    form.Close();var busy=Get(client);Pump(()=>busy.IsCompleted);Assert.Equal("busy",busy.GetAwaiter().GetResult().Data.GetProperty("sample_status").GetString());
                }
                Assert.Equal(1,calls);
            }finally{release.Set();Pump(()=>pending.IsCompleted);pending.GetAwaiter().GetResult();}
            var retry=Get(client);Pump(()=>retry.IsCompleted);Assert.Equal("complete",retry.GetAwaiter().GetResult().Data.GetProperty("sample_status").GetString());
        });
    }
    [Fact]
    public async Task RealHttpDeadlineReturnsFrozenPartialAndRepeatedHttpAndUiCannotMultiplyNativeWork()
    {
        using var entered=new ManualResetEventSlim();using var release=new ManualResetEventSlim();var calls=0;var source=Probes();
        var probes=new ObservationHealthProbes{Ffmpeg=()=>{Interlocked.Increment(ref calls);entered.Set();release.Wait();return source.Ffmpeg();},Ffprobe=source.Ffprobe,FfmpegSource=source.FfmpegSource,WgcHelper=source.WgcHelper,AudioHelper=source.AudioHelper,FileExists=source.FileExists,Desktop=source.Desktop};
        using var readiness=new RuntimeReadiness("headless",0);var server=Server(readiness:readiness);var gateway=AgentRecorder.Headless.Program.CreateObservationHealthGateway(server,readiness,_root,probes);server.SetObservationHealthGateway(gateway);server.Start();using var client=Client();
        try
        {
            var watch=Stopwatch.StartNew();var pending=Get(client);Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));
            var requests=Enumerable.Range(0,8).Select(_=>Get(client)).ToArray();var results=await Task.WhenAll(requests);Assert.All(results,r=>Assert.Equal("busy",r.Data.GetProperty("sample_status").GetString()));
            Assert.Equal(HealthProbeState.Busy,(await gateway.SampleAsync(Owner(),default)).ProbeState);
            var timed=await pending;Assert.Equal("timeout",timed.Data.GetProperty("sample_status").GetString());Assert.Equal(_root,timed.Data.GetProperty("instance").GetProperty("data_directory").GetString());Assert.InRange(watch.Elapsed.TotalSeconds,2.5,4.5);
            Assert.Equal("busy",(await Get(client)).Data.GetProperty("sample_status").GetString());Assert.Equal(1,calls);
        }finally{release.Set();}
        // Gate release is acknowledged by completing a new read, not a timing sleep.
        ObservationHealthSnapshot retry;var end=Stopwatch.StartNew();do {retry=await gateway.ReadAsync(default);await Task.Yield();}while(retry.ProbeState==HealthProbeState.Busy && end.Elapsed<TimeSpan.FromSeconds(5));Assert.Equal(HealthProbeState.Complete,retry.ProbeState);
    }
    [Fact]
    public async Task OuterHttpDeadlineCancelsProviderEvenWhenItsOwnWaitNeverCompletes()
    {
        var provider=new PendingProvider();var server=Server();server.SetObservationHealthGateway(provider);server.Start();using var client=Client();
        var request=Get(client);await provider.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));var (data,text)=await request;
        Assert.Equal("timeout",data.GetProperty("sample_status").GetString());Assert.True(provider.Token.IsCancellationRequested);Assert.DoesNotContain("SECRET",text);
        provider.Pending.TrySetResult(ObservationHealthSnapshot.Partial(Owner(),HealthProbeState.Failed));
    }
    [Fact]
    public async Task ShutdownCancelsWaitWithoutReleasingNativeQuota()
    {
        using var entered=new ManualResetEventSlim();using var release=new ManualResetEventSlim();var source=Probes();
        var probes=new ObservationHealthProbes{Ffmpeg=()=>{entered.Set();release.Wait();return source.Ffmpeg();},Ffprobe=source.Ffprobe,FileExists=source.FileExists,Desktop=source.Desktop};
        using var readiness=new RuntimeReadiness("headless",0);var server=Server(readiness:readiness);var gateway=AgentRecorder.Headless.Program.CreateObservationHealthGateway(server,readiness,_root,probes);server.SetObservationHealthGateway(gateway);server.Start();using var client=Client();
        var pending=client.GetAsync("/api/v1/health");Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));
        try {await Task.Run(server.Stop).WaitAsync(TimeSpan.FromSeconds(5));await Record.ExceptionAsync(async()=>{using var response=await pending;});Assert.Equal(HealthProbeState.Busy,(await gateway.ReadAsync(default)).ProbeState);}
        finally{release.Set();}
    }
    internal static void Pump(Func<bool> done)
    {var watch=Stopwatch.StartNew();while(!done()&&watch.Elapsed<TimeSpan.FromSeconds(10)){Application.DoEvents();Thread.Yield();}Assert.True(done());Application.DoEvents();}
    private sealed class AuditFake : AuditLogger {public override void Log(string evt,object payload) { }}
    private sealed class PendingProvider : IObservationHealthGateway
    {
        internal readonly TaskCompletionSource Entered=new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource<ObservationHealthSnapshot> Pending=new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal CancellationToken Token;
        public ObservationHealthOwner CaptureOwner()=>throw new Exception("SECRET_OWNER");
        public Task<ObservationHealthSnapshot> SampleAsync(ObservationHealthOwner owner,CancellationToken token)=>throw new Exception("SECRET_NATIVE");
        public Task<ObservationHealthSnapshot> ReadAsync(CancellationToken token){Token=token;Entered.TrySetResult();return Pending.Task;}
    }
    private sealed class ThrowingProvider : IObservationHealthGateway
    {
        internal int Calls;internal bool BadProjection;
        public ObservationHealthOwner CaptureOwner()=>throw new Exception("SECRET_OWNER");
        public Task<ObservationHealthSnapshot> SampleAsync(ObservationHealthOwner owner,CancellationToken token)=>throw new Exception("SECRET_NATIVE");
        public Task<ObservationHealthSnapshot> ReadAsync(CancellationToken token)
        {Interlocked.Increment(ref Calls);if(BadProjection)return Task.FromResult(new ObservationHealthSnapshot(null!,null!,null!,null!,null!,null!,HealthProbeState.Complete));throw new ApiException(500,"SECRET_CODE","SECRET_PROOF",new{nonce="SECRET_NONCE"});}
        public ObservationHealthSnapshot PartialSnapshot(HealthProbeState state)=>throw new Exception("SECRET_FALLBACK");
    }
}
