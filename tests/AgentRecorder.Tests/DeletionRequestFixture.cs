using System.Net;
using System.Text;
using System.Text.Json;
using System.Collections.Concurrent;
using AgentRecorder.Api;
using AgentRecorder.Core;
using AgentRecorder.Infrastructure;
using AgentRecorder.Persistence;
using Microsoft.Data.Sqlite;
using Xunit;

namespace AgentRecorder.Tests;

internal sealed class DeletionRequestFixture:IDisposable
{
    internal const string Endpoint="/api/v1/retention/deletion-requests";
    internal readonly LocalRetentionDeletionFixture F;
    internal readonly Io Database;
    internal readonly LocalRetentionApproval Authority;
    internal readonly RetentionDeletionCoordinator Coordinator;
    internal readonly ApiServer Server;
    internal readonly HttpClient Client;
    private readonly string? _prior=Environment.GetEnvironmentVariable("AGENT_RECORDER_DATA_DIR");
    internal DeletionRequestFixture(bool real=false,bool headless=false,bool configured=true,Io? database=null,DeletionLedgerDatabase? ledger=null,
        RetentionGuardDatabase? guard=null,RuntimeReadiness? readiness=null)
    {
        F=new(real);Database=database??new();Authority=F.Service(ledgerDatabase:ledger,database:guard);
        Coordinator=new(F.Db.Store,UnifiedHistoryTestDatabase.Sid,UnifiedHistoryTestDatabase.Session,F.Videos,Authority,F.Clock,Database,new(),new());
        var tray=new Tray(headless);F.Engine.SetTray(tray);F.Engine.BackendFactory=_=>throw new IOException("No capture permitted.");
        Environment.SetEnvironmentVariable("AGENT_RECORDER_DATA_DIR",F.Db.Root);ApiKeyAuth.InitializeForTesting(F.Db.Root);
        Server=new(F.Engine,new RetentionSharedOutputPathTests.QuietAudit(),tray,readiness,null,null,null,null,null,null,null,null,0,
            retentionDeletionGateway:configured?new SqliteRetentionDeletionGateway(Coordinator):null);
        Server.Start();Client=new(new HttpClientHandler{UseProxy=false}){BaseAddress=new($"http://127.0.0.1:{Server.BoundPort}"),Timeout=TimeSpan.FromSeconds(12)};
        Client.DefaultRequestHeaders.Add("X-Agent-Recorder-Key",ApiKeyAuth.CurrentApiKey);
    }
    internal async Task<(HttpStatusCode Status,JsonElement Json)> Post(string key="owned-key",string? body=null)
    {
        using var request=new HttpRequestMessage(HttpMethod.Post,Endpoint){Content=new StringContent(body??JsonSerializer.Serialize(new{recording_id=F.Row.RecordingId}),Encoding.UTF8,"application/json")};
        request.Headers.Add("Idempotency-Key",key);using var response=await Client.SendAsync(request);return (response.StatusCode,JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.Clone());
    }
    internal async Task<string> Create(string key="owned-key")
    {var r=await Post(key);Assert.Equal(HttpStatusCode.Accepted,r.Status);return r.Json.GetProperty("data").GetProperty("deletion_request_id").GetString()!;}
    internal async Task<JsonElement> Read(string id)
    {using var r=await Client.GetAsync(Endpoint+"/"+id);Assert.Equal(HttpStatusCode.OK,r.StatusCode);return JsonDocument.Parse(await r.Content.ReadAsStringAsync()).RootElement.GetProperty("data").Clone();}
    internal async Task Approve(bool retire=true)
    {await F.Ui.Entered.Task.WaitAsync(TimeSpan.FromSeconds(6));F.Ui.Decide(LocalRetentionUiChoice.Approve);if(retire)F.Ui.Retire.TrySetResult(true);}
    internal async Task<JsonElement> Finish(string id)
    {Assert.True(await Coordinator.WaitForIdleAsync().WaitAsync(TimeSpan.FromSeconds(9)));return await Read(id);}
    internal long Scalar(string sql)
    {using var c=new SqliteConnection($"Data Source={F.Db.Store.DatabasePath};Mode=ReadOnly;Pooling=False");c.Open();using var cmd=c.CreateCommand();cmd.CommandText=sql;return (long)cmd.ExecuteScalar()!;}
    internal async Task<string> Raw(string path,string? body=null,string extra="",bool auth=true,byte[]? raw=null)
    {
        using var tcp=new System.Net.Sockets.TcpClient();await tcp.ConnectAsync(IPAddress.Loopback,Server.BoundPort);using var stream=tcp.GetStream();
        var bytes=raw??Encoding.UTF8.GetBytes(body??"");var head=$"{(body is null&&raw is null?"GET":"POST")} {path} HTTP/1.1\r\nHost: localhost\r\n"+(auth?"X-Agent-Recorder-Key: "+ApiKeyAuth.CurrentApiKey+"\r\n":"")+extra+$"Content-Length: {bytes.Length}\r\nConnection: close\r\n\r\n";
        await stream.WriteAsync(Encoding.UTF8.GetBytes(head));await stream.WriteAsync(bytes);
        using var reply=new MemoryStream();var buffer=new byte[2048];
        while(true)
        {
            int count;try{count=await stream.ReadAsync(buffer).AsTask().WaitAsync(TimeSpan.FromSeconds(6));}catch(IOException) when(reply.Length>0){break;}
            if(count==0)break;reply.Write(buffer,0,count);var data=reply.ToArray();var text=Encoding.UTF8.GetString(data);var end=text.IndexOf("\r\n\r\n",StringComparison.Ordinal);
            if(end>=0)
            {
                var length=text[..end].Split("\r\n").Single(x=>x.StartsWith("Content-Length:",StringComparison.OrdinalIgnoreCase));
                if(data.Length>=Encoding.UTF8.GetByteCount(text[..(end+4)])+int.Parse(length.Split(':')[1]))break;
            }
        }
        return Encoding.UTF8.GetString(reply.ToArray());
    }
    public void Dispose()
    {
        Coordinator.Dispose();F.Ui.Retire.TrySetResult(true);Client.Dispose();Server.Stop(TimeSpan.FromSeconds(9));
        ApiKeyAuth.ResetForTesting(null);Environment.SetEnvironmentVariable("AGENT_RECORDER_DATA_DIR",_prior);F.Dispose();
    }
    internal sealed class Io:DeletionLedgerDatabase
    {
        internal readonly ConcurrentQueue<(string Stage,int Thread)> Calls=new();
        private readonly ThreadLocal<string> _stage=new(()=>"");
        internal Action<string,SqliteConnection,SqliteTransaction?>? OnStep;
        internal string? Fault,UnknownCommit,UnknownClose;
        internal override void Step(string stage,SqliteConnection c,SqliteTransaction? t)
        {
            Calls.Enqueue((stage,Environment.CurrentManagedThreadId));_stage.Value=stage.Split('_')[0];
            if(stage.EndsWith("before_begin")&&_stage.Value is not("query" or "lookup"))
            {using var cmd=c.CreateCommand();cmd.CommandText="PRAGMA synchronous;";Assert.Equal(2L,cmd.ExecuteScalar());cmd.CommandText="PRAGMA busy_timeout;";Assert.Equal(1000L,cmd.ExecuteScalar());Assert.Equal(1,c.DefaultTimeout);}
            OnStep?.Invoke(stage,c,t);if(stage==Fault)throw new IOException("private_request_step");
        }
        internal override void Commit(SqliteTransaction t){base.Commit(t);if(_stage.Value==UnknownCommit)throw new IOException("private_request_commit_unknown");}
        internal override void Close(SqliteConnection c){if(_stage.Value==UnknownClose)throw new IOException("private_request_close_unknown");base.Close(c);}
    }
    internal sealed class Tray(bool headless):ITrayContext
    {
        public string HostMode=>headless?"headless":"tray";public bool SupportsRegionSelectionUi=>false;
        public void RequestConfirmation(RecordingConfirmationPresentation p,Action<ConfirmationDecision> c)=>throw new IOException("No recording UI.");
        public void RequestRegionSelection(int t,Action<string,int,int,int,int,string,string> c)=>throw new IOException("No selection UI.");
        public void SetRecording(RecordingUiPresentation p)=>throw new IOException("No capture.");
        public void SetIdle(RecordingUiPresentation p){}public void SetAllIdle(){}public void ShowError(string s){}
    }
}
