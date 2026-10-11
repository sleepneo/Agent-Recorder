using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using AgentRecorder.Api;
using AgentRecorder.Core;
using AgentRecorder.Infrastructure;
using AgentRecorder.Persistence;
using AgentRecorder.Windows;
using Microsoft.Data.Sqlite;
using Xunit;
using Xunit.Abstractions;
using static AgentRecorder.Tests.OrdinaryArtifactHistoryTests;
using static AgentRecorder.Tests.UnifiedHistoryTestDatabase;

namespace AgentRecorder.Tests;

[Collection("NonParallel-AgentRecorderDataDir")]
public sealed class OrdinaryArtifactTransportTests(ITestOutputHelper output)
{
    private const string Endpoint="/api/v1/recording-history/r/artifacts/check";
    private static TaskCompletionSource Signal()=>new(TaskCreationOptions.RunContinuationsAsynchronously);
    private sealed class Fixture : IDisposable
    {
        internal readonly string Root=Path.GetFullPath(Path.Combine(TestHelper.ProjectRoot,".local-data/temp/task333r",Guid.NewGuid().ToString("N")));
        internal readonly UnifiedHistoryTestDatabase Db;
        internal readonly string[] Paths;
        internal Fixture()
        {
            Db=new(Root);var row=Db.Recording("r",completed:true);Directory.CreateDirectory(Path.GetDirectoryName(row.OutputPath)!);
            var op=OrdinaryRecordingBundleOperation.Create("r",row.OutputPath);Directory.CreateDirectory(op.BundlePath);
            var items=BundleContentsGenerationTests.Items();Paths=new[]{row.OutputPath}.Concat(items.Select(i=>Path.Combine(op.BundlePath,i.Name))).ToArray();
            File.WriteAllBytes(Paths[0],new byte[12]);foreach(var item in items)File.WriteAllBytes(Path.Combine(op.BundlePath,item.Name),new byte[checked((int)item.SizeBytes)]);
            Db.Ordinary.Register(row with {BundleRegistration=new(op)});
            Assert.True(Db.Ordinary.TryPublishBundle(new(op,new("ready",op.BundlePath,null,new(items)))));Assert.Equal("ready",Db.Ordinary.Read("r")!.Bundle!.Status);
        }
        internal string FileState()=>string.Join("|",Paths.Select(p=>new FileInfo(p).Length+":"+File.GetLastWriteTimeUtc(p).Ticks));
        public void Dispose(){Db.Ordinary.Dispose();SqliteConnection.ClearAllPools();} // Retain owned evidence.
    }
    private sealed class Tray(bool headless) : ITrayContext
    {
        public string HostMode=>headless?"headless":"tray";public bool SupportsRegionSelectionUi=>false;
        public void RequestConfirmation(RecordingConfirmationPresentation p,Action<ConfirmationDecision> c)=>throw new InvalidOperationException("No UI");
        public void RequestRegionSelection(int t,Action<string,int,int,int,int,string,string> c)=>throw new InvalidOperationException("No UI");
        public void SetRecording(RecordingUiPresentation r)=>throw new InvalidOperationException("No capture");public void SetIdle(RecordingUiPresentation r){}public void SetAllIdle(){}public void ShowError(string s)=>throw new InvalidOperationException("No UI");
    }
    private sealed class Host : IDisposable
    {
        private readonly string? _prior=Environment.GetEnvironmentVariable("AGENT_RECORDER_DATA_DIR");
        private readonly RecordingEngine _engine;
        internal readonly ApiServer Api;
        internal Host(Fixture f,bool headless,IOrdinaryArtifactCheckGateway gateway)
        {
            Environment.SetEnvironmentVariable("AGENT_RECORDER_DATA_DIR",f.Root);ApiKeyAuth.InitializeForTesting(f.Root);
            var audit=new RetentionSharedOutputPathTests.QuietAudit();_engine=new(audit);_engine.ConfigureOrdinaryRecordingHistory(f.Db.Ordinary);
            var tray=new Tray(headless);_engine.SetTray(tray);_engine.BackendFactory=_=>throw new InvalidOperationException("Capture forbidden");
            Api=new(_engine,audit,tray,null,null,null,null,null,null,null,null,null,0,artifactCheckGateway:gateway);Api.Start();
        }
        internal HttpClient Client()
        {var c=new HttpClient(new HttpClientHandler{UseProxy=false}){BaseAddress=new($"http://127.0.0.1:{Api.BoundPort}"),Timeout=TimeSpan.FromSeconds(8)};c.DefaultRequestHeaders.Add("X-Agent-Recorder-Key",ApiKeyAuth.CurrentApiKey);return c;}
        public void Dispose(){Api.Stop();_engine.Dispose();ApiKeyAuth.ResetForTesting(null);Environment.SetEnvironmentVariable("AGENT_RECORDER_DATA_DIR",_prior);}
    }
    private sealed class Gateway : IOrdinaryArtifactCheckGateway
    {
        internal readonly TaskCompletionSource Entered=Signal(),Release=Signal(),Returned=Signal();
        internal CancellationToken Token;
        internal bool CancelledAtReturn;
        internal OrdinaryArtifactCheckResult? Result;
        internal Func<string,CancellationToken,Task<OrdinaryArtifactCheckResult?>>? Read;
        public async Task<OrdinaryArtifactCheckResult?> ReadAsync(string id,CancellationToken token)
        {
            Token=token;Entered.TrySetResult();
            if(Read is not null)Result=await Read(id,token);
            else
            {
                var at=DateTimeOffset.UtcNow;var state="complete";
                try {await Release.Task.WaitAsync(token);}catch(OperationCanceledException){state="cancelled";}
                Result=new(id,at,DateTimeOffset.UtcNow,state,"completed",new("media",12,null,"not_checked",state,null,"unknown"),new(null,null,null));
            }
            CancelledAtReturn=token.IsCancellationRequested;Returned.TrySetResult();return Result;
        }
    }
    private static async Task<TcpClient> Send(Host host)
    {
        var client=new TcpClient();await client.ConnectAsync(IPAddress.Loopback,host.Api.BoundPort);
        await client.GetStream().WriteAsync(Encoding.UTF8.GetBytes($"GET {Endpoint} HTTP/1.1\r\nHost: localhost\r\nX-Agent-Recorder-Key: {ApiKeyAuth.CurrentApiKey}\r\nConnection: close\r\n\r\n"));return client;
    }
    private static async Task<JsonElement> Receive(NetworkStream stream)
    {
        using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(5));using var buffer=new MemoryStream();await stream.CopyToAsync(buffer,timeout.Token);
        var response=Encoding.UTF8.GetString(buffer.ToArray());Assert.StartsWith("HTTP/1.1 200 OK\r\n",response,StringComparison.Ordinal);
        using var doc=JsonDocument.Parse(response[(response.IndexOf("\r\n\r\n",StringComparison.Ordinal)+4)..]);return doc.RootElement.GetProperty("data").Clone();
    }
    private static TaskCompletionSource Observe(Host host,Gateway gateway,bool eof)
    {
        var seen=Signal();host.Api.ArtifactTransportObservedForTests=value=>{if(value==eof&&gateway.Entered.Task.IsCompleted)seen.TrySetResult();};return seen;
    }
    private static async Task AssertBusy(Host host)
    {
        using var client=host.Client();using var response=await client.GetAsync(Endpoint);Assert.Equal(HttpStatusCode.OK,response.StatusCode);
        using var doc=JsonDocument.Parse(await response.Content.ReadAsStringAsync());Assert.Equal("busy",doc.RootElement.GetProperty("data").GetProperty("probe_state").GetString());
    }
    private static Leaf BlockingLeaf(TaskCompletionSource entered,ManualResetEventSlim release)=>new(){Read=path=>
    {entered.TrySetResult();Assert.True(release.Wait(TimeSpan.FromSeconds(5)));return Sample(path);}};

    [Theory] [InlineData(false,false)] [InlineData(false,true)] [InlineData(true,false)] [InlineData(true,true)]
    public async Task CompleteRequestAllowsSendHalfCloseAndWaitsForResponse(bool headless,bool halfClose)
    {
        using var f=new Fixture();var before=f.Db.Snapshot();var gateway=new Gateway();using var host=new Host(f,headless,gateway);var observed=Observe(host,gateway,halfClose);
        using var raw=await Send(host);var stream=raw.GetStream();await gateway.Entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        if(halfClose)raw.Client.Shutdown(SocketShutdown.Send);
        try {await observed.Task.WaitAsync(TimeSpan.FromSeconds(2));Assert.False(gateway.Token.IsCancellationRequested);}
        finally {gateway.Release.TrySetResult();}
        var data=await Receive(stream);Assert.Equal("complete",data.GetProperty("probe_state").GetString());Assert.False(gateway.CancelledAtReturn);Assert.Equal(before,f.Db.Snapshot());
        output.WriteLine($"scope={headless}; send_half_close={halfClose}; transport observed while gateway blocked; HTTP 200/complete; cancellation_at_return=false.");
    }

    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task HalfClosedRealServiceContinuesAllRegisteredSamplesReadOnly(bool headless)
    {
        using var f=new Fixture();var before=f.Db.Snapshot();var files=f.FileState();using var release=new ManualResetEventSlim();var entered=Signal();var exited=Signal();
        var leaf=new Leaf{Read=path=>{if(path==f.Paths[0]){entered.TrySetResult();Assert.True(release.Wait(TimeSpan.FromSeconds(5)));}return Sample(path,new FileInfo(path).Length);}};
        var service=new OrdinaryArtifactCheckService(f.Db.Ordinary.ReadArtifactFacts,new Ancestors(),leaf,_=>true){WorkerExitedForTests=()=>exited.TrySetResult()};
        var gateway=new Gateway{Read=service.ReadAsync};using var host=new Host(f,headless,gateway);var eof=Observe(host,gateway,true);using var raw=await Send(host);var stream=raw.GetStream();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));raw.Client.Shutdown(SocketShutdown.Send);
        try {await eof.Task.WaitAsync(TimeSpan.FromSeconds(2));Assert.False(gateway.Token.IsCancellationRequested);}
        finally {release.Set();}
        var data=await Receive(stream);await exited.Task.WaitAsync(TimeSpan.FromSeconds(2));Assert.Equal("complete",data.GetProperty("probe_state").GetString());Assert.False(gateway.CancelledAtReturn);
        Assert.Equal(6,leaf.Calls.Count);Assert.All(gateway.Result!.Bundle.Contents!,i=>Assert.Equal("present_size_matches",i.Observation));Assert.Equal(before,f.Db.Snapshot());Assert.Equal(files,f.FileState());
        output.WriteLine($"scope={headless}; actual writer/read-only service + synchronous sample adapter; observed FIN; HTTP 200/complete, six registered leaves, cancellation=false, SQL/files unchanged.");
    }

    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task HalfCloseStillUsesTotalDeadlineRetainsQuotaAndFreezesPartialResult(bool headless)
    {
        using var f=new Fixture();var before=f.Db.Snapshot();using var release=new ManualResetEventSlim();var entered=Signal();var exited=Signal();var deadline=Signal();
        var leaf=BlockingLeaf(entered,release);var service=new OrdinaryArtifactCheckService(f.Db.Ordinary.ReadArtifactFacts,new Ancestors(),leaf,_=>true)
        {DeadlineForTests=_=>deadline.Task,WorkerExitedForTests=()=>exited.TrySetResult()};var gateway=new Gateway{Read=service.ReadAsync};
        using var host=new Host(f,headless,gateway);var eof=Observe(host,gateway,true);using var raw=await Send(host);var stream=raw.GetStream();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));raw.Client.Shutdown(SocketShutdown.Send);
        try
        {
            await eof.Task.WaitAsync(TimeSpan.FromSeconds(2));Assert.False(gateway.Token.IsCancellationRequested);deadline.TrySetResult();var data=await Receive(stream);
            Assert.Equal("timeout",data.GetProperty("probe_state").GetString());Assert.False(gateway.CancelledAtReturn);Assert.Equal("unavailable",gateway.Result!.Media.Observation);
            Assert.All(gateway.Result.Bundle.Contents!,i=>Assert.Equal("not_checked",i.Observation));var timeoutResult=gateway.Result;var frozen=JsonSerializer.Serialize(timeoutResult);
            for(var i=0;i<3;i++){await AssertBusy(host);Assert.Equal("busy",(await new OrdinaryArtifactCheckService(f.Db.Ordinary).ReadAsync("r"))!.ProbeState);}
            Assert.Single(leaf.Calls);release.Set();await exited.Task.WaitAsync(TimeSpan.FromSeconds(2));Assert.Equal(frozen,JsonSerializer.Serialize(timeoutResult));
        }
        finally {release.Set();await exited.Task.WaitAsync(TimeSpan.FromSeconds(2));}
        Assert.Single(leaf.Calls);Assert.Equal("complete",(await new OrdinaryArtifactCheckService(f.Db.Ordinary.ReadArtifactFacts,new Ancestors(),new Leaf(),_=>true).ReadAsync("r"))!.ProbeState);Assert.Equal(before,f.Db.Snapshot());
        output.WriteLine($"scope={headless}; FIN preserved request; controlled total deadline HTTP 200/timeout, cancellation_at_return=false, active worker retained quota, busy six retries, late exit immutable/restored.");
    }

    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task ResetCancelsActualServiceButCannotReleaseBlockedWorker(bool headless)
    {
        using var f=new Fixture();var before=f.Db.Snapshot();using var release=new ManualResetEventSlim();var entered=Signal();var exited=Signal();var leaf=BlockingLeaf(entered,release);
        var service=new OrdinaryArtifactCheckService(f.Db.Ordinary.ReadArtifactFacts,new Ancestors(),leaf,_=>true){WorkerExitedForTests=()=>exited.TrySetResult()};var gateway=new Gateway{Read=service.ReadAsync};
        using var host=new Host(f,headless,gateway);using var raw=await Send(host);await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));raw.Client.LingerState=new(true,0);raw.Client.Close(0);
        try {await gateway.Returned.Task.WaitAsync(TimeSpan.FromSeconds(2));Assert.True(gateway.CancelledAtReturn);Assert.Equal("cancelled",gateway.Result!.ProbeState);await AssertBusy(host);Assert.Single(leaf.Calls);}
        finally {release.Set();await exited.Task.WaitAsync(TimeSpan.FromSeconds(2));}
        Assert.Single(leaf.Calls);Assert.Equal("complete",(await new OrdinaryArtifactCheckService(f.Db.Ordinary.ReadArtifactFacts,new Ancestors(),new Leaf(),_=>true).ReadAsync("r"))!.ProbeState);Assert.Equal(before,f.Db.Snapshot());
        output.WriteLine($"scope={headless}; actual loopback RST => cancellation=true/cancelled; peer cannot receive; blocked worker busy then late exit/restored; no next leaf.");
    }

    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task ApiStopExplicitlyCancelsAfterObservedFin(bool headless)
    {
        using var f=new Fixture();var gateway=new Gateway();using var host=new Host(f,headless,gateway);var eof=Observe(host,gateway,true);using var raw=await Send(host);var stream=raw.GetStream();
        await gateway.Entered.Task.WaitAsync(TimeSpan.FromSeconds(2));raw.Client.Shutdown(SocketShutdown.Send);
        await eof.Task.WaitAsync(TimeSpan.FromSeconds(2));Assert.False(gateway.Token.IsCancellationRequested);Assert.True(host.Api.Stop(TimeSpan.FromSeconds(2)));
        await gateway.Returned.Task.WaitAsync(TimeSpan.FromSeconds(2));Assert.True(gateway.CancelledAtReturn);Assert.Equal("cancelled",gateway.Result!.ProbeState);
        output.WriteLine($"scope={headless}; FIN initially uncancelled; explicit API Stop => cancellation=true/cancelled; handler drained (no HTTP delivery guarantee at shutdown).");
    }
}
