using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using AgentRecorder.Api;
using AgentRecorder.Capture;
using AgentRecorder.Core;
using AgentRecorder.Infrastructure;
using AgentRecorder.Persistence;
using AgentRecorder.Windows;
using Microsoft.Data.Sqlite;
using Xunit;
using static AgentRecorder.Tests.UnifiedHistoryTestDatabase;

namespace AgentRecorder.Tests;

// Decisions are explicitly synthetic test fixtures, never a production approval provider.
internal sealed class BatchDeletion388Fixture:IDisposable
{
    internal const string Endpoint="/api/v1/retention/batch-deletion-requests";
    internal readonly LocalRetentionDeletionFixture F;
    internal readonly Ui Prompts;
    internal readonly LocalRetentionDeletionFixture.Adapter Native;
    internal readonly LocalRetentionApproval Authority;
    internal readonly RetentionDeletionCoordinator Single;
    internal readonly RetentionBatchDeletionCoordinator Batch;
    internal readonly DeletionRequestFixture.Io BatchIo,ChildIo;
    internal readonly DeletionRequestAdmission Admission=new();
    internal readonly DeletionQueryAdmission Queries=new();
    internal readonly ApiServer Server;
    internal readonly HttpClient Client;
    internal readonly List<OrdinaryRecordingHistorySnapshot> Rows=new();
    private readonly bool _real;
    private readonly string? _prior=Environment.GetEnvironmentVariable("AGENT_RECORDER_DATA_DIR");
    internal BatchDeletion388Fixture(int count=2,bool real=false,bool headless=false,bool configured=true,RuntimeReadiness? ready=null,
        DeletionRequestFixture.Io? batchIo=null,DeletionRequestFixture.Io? childIo=null,DeletionLedgerDatabase? ledger=null,RetentionGuardDatabase? guard=null,
        RetentionHostKeepLatestProtection? hostProtection=null)
    {
        _real=real;F=new(real);Rows.Add(F.Row);
        for(var n=1;n<count;n++)
        {
            var path=Path.Combine(F.Videos,"synthetic-"+n+".mp4");File.WriteAllBytes(path,F.Bytes);
            var identity=real?WindowsReadOnlyFileMetadataProbe.Instance.Observe(path).Before!.Identity!:new ObservedFileIdentity(123,456+(ulong)n,789);
            var row=F.Db.Recording("r"+n,completed:true) with {OutputPath=path,BundleRegistration=new(null),PublicationReceipt=PublicationReceipt.IssueForTests(path,12,identity)};
            F.Db.Ordinary.Register(row);Rows.Add(row);
        }
        Prompts=new(intent=>{F.Files.Anchor=intent.Facts.Anchor;F.Files.Api.Anchor=intent.Facts.Anchor;});
        Native=real?new(WindowsOriginalFileHoldNative.Instance,null):F.Native;
        if(real)Native.BeforeRequest=h=>
        {
            var b=new StringBuilder(2048);Assert.True(WindowsOriginalFileHoldNative.Instance.FinalPath(h,b,b.Capacity,out _)>0);
            var path=b.ToString()[4..];Assert.StartsWith(Path.GetFullPath(F.Videos)+"\\",Path.GetFullPath(path),StringComparison.OrdinalIgnoreCase);
            var row=Rows.Single(r=>r.OutputPath==path);Assert.True(WindowsOriginalFileHoldNative.Instance.Identity(h,out var id,out _));
            Assert.Equal(row.PublicationReceipt!.Anchor.Identity,new ObservedFileIdentity(id.VolumeSerialNumber,id.IdLow,id.IdHigh));
        };
        if(real)Native.FakeRequest=()=>
        {
            var result=WindowsOriginalFileHoldNative.Instance.RequestDisposition(Native.Leaf!);
            if(result.Outcome==OriginalFileDispositionOutcome.Accepted)
            {Assert.True(WindowsOriginalFileHoldNative.Instance.Standard(Native.Leaf!,out var standard,out _));Assert.NotEqual(0,standard.DeletePending);}
            return result;
        };
        Authority=new(F.Engine,F.Db.Store,F.Db.Ordinary,Sid,Session,F.Videos,Prompts,F.Clock,F.Admission,Native,F.Files.Admission,F.DatabaseAdmission,F.ComposedAdmission,guard,ledgerDatabase:ledger,hostProtection:hostProtection);
        ChildIo=childIo??new();BatchIo=batchIo??new();
        Single=new(F.Db.Store,Sid,Session,F.Videos,Authority,F.Clock,ChildIo,Admission,Queries);
        Batch=new(F.Db.Store,Sid,Session,F.Videos,Single,F.Clock,BatchIo,Queries);
        var tray=new DeletionRequestFixture.Tray(headless);F.Engine.SetTray(tray);F.Engine.BackendFactory=_=>throw new IOException("No capture permitted");
        Environment.SetEnvironmentVariable("AGENT_RECORDER_DATA_DIR",F.Db.Root);ApiKeyAuth.InitializeForTesting(F.Db.Root);
        Server=new(F.Engine,new RetentionSharedOutputPathTests.QuietAudit(),tray,ready,null,null,null,null,null,null,null,null,0,
            retentionDeletionGateway:new SqliteRetentionDeletionGateway(Single),retentionBatchDeletionGateway:configured?new SqliteRetentionBatchDeletionGateway(Batch):null,
            retentionEvaluationGateway:new SqliteRetentionEvaluationGateway(new RetentionEvaluationService(F.Db.Query,F.Db.Root,F.Engine),()=>(Sid,Session)));
        Server.ConfigureHostShutdown(()=>Batch.HostShutdownBusyReason??Single.HostShutdownBusyReason,()=>{});Server.Start();
        Client=new(new HttpClientHandler{UseProxy=false}){BaseAddress=new($"http://127.0.0.1:{Server.BoundPort}"),Timeout=TimeSpan.FromSeconds(9)};
        Client.DefaultRequestHeaders.Add("X-Agent-Recorder-Key",ApiKeyAuth.CurrentApiKey);
    }
    internal string[] Ids=>Rows.Select(r=>r.RecordingId).ToArray();
    internal async Task<(HttpStatusCode Status,JsonElement Json)> Post(string key="batch-key",string? body=null,string suffix="")
    {
        using var request=new HttpRequestMessage(HttpMethod.Post,Endpoint+suffix){Content=new StringContent(body??JsonSerializer.Serialize(new{recording_ids=Ids}),Encoding.UTF8,"application/json")};
        if(key!="NO_HEADER")request.Headers.Add("Idempotency-Key",key);using var response=await Client.SendAsync(request);
        return (response.StatusCode,JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.Clone());
    }
    internal async Task<string> Create()
    {var response=await Post();Assert.Equal(HttpStatusCode.Accepted,response.Status);return response.Json.GetProperty("data").GetProperty("batch_deletion_request_id").GetString()!;}
    internal async Task<RetentionBatchDeletionRequestResult> Finish(string id)
    {Assert.True(await Batch.WaitForIdleAsync().WaitAsync(TimeSpan.FromSeconds(9)));return (await Batch.ReadAsync(id))!;}
    internal long Scalar(string sql)=>F.Db.Scalar(sql);
    internal void PreserveBytes(){foreach(var r in Rows)Assert.Equal(F.Bytes,File.ReadAllBytes(r.OutputPath));}
    public void Dispose()
    {
        Batch.Dispose();Single.Dispose();Prompts.CloseAll();Client.Dispose();Server.Stop(TimeSpan.FromSeconds(8));
        ApiKeyAuth.ResetForTesting(null);Environment.SetEnvironmentVariable("AGENT_RECORDER_DATA_DIR",_prior);
        File.WriteAllText(Path.Combine(F.Db.Root,"batch-native-fixture.json"),JsonSerializer.Serialize(new {
            synthetic_local_decisions=true,real_windows_adapter=_real,actual_adapter_calls=Native.Calls,ui_calls=Prompts.Begins,
            results=Native.Results.Select(x=>new{outcome=x.Outcome.ToString(),x.Win32Error}),recording_ids=Ids,
            owned_root=F.Videos,scope="new_owned_synthetic_files_only"}),new UTF8Encoding(false));
        if(_real&&!File.Exists(F.Media))F.Removed=true;F.Dispose();
    }
    internal sealed class Prompt(LocalRetentionIntent intent,CancellationToken cancellation)
    {
        internal readonly LocalRetentionIntent Intent=intent;internal readonly CancellationToken Cancellation=cancellation;
        internal readonly TaskCompletionSource<LocalRetentionDecision> Choice=new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource<bool> Retired=new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal void Decide(LocalRetentionUiChoice choice,bool retired=true){Choice.TrySetResult(new(Intent,choice));if(retired)Retired.TrySetResult(true);}
    }
    internal sealed class Ui(Action<LocalRetentionIntent> before):ILocalRetentionApprovalUi
    {
        private readonly Channel<Prompt> _pending=Channel.CreateBounded<Prompt>(5);
        internal readonly ConcurrentQueue<Prompt> All=new();internal int Begins;internal bool AutoRetireCancellation=true;
        public LocalRetentionUiSession Begin(LocalRetentionIntent intent,CancellationToken cancellation)
        {
            before(intent);var p=new Prompt(intent,cancellation);All.Enqueue(p);Interlocked.Increment(ref Begins);Assert.True(_pending.Writer.TryWrite(p));
            cancellation.Register(()=>{if(AutoRetireCancellation)p.Decide(LocalRetentionUiChoice.Closed);});return new(p.Choice.Task,p.Retired.Task);
        }
        internal Task<Prompt> Next()=>_pending.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(6));
        internal void CloseAll(){foreach(var p in All)p.Decide(LocalRetentionUiChoice.Closed);}
    }
    internal sealed class Barrier:IDisposable
    {
        internal readonly TaskCompletionSource Entered=new(TaskCreationOptions.RunContinuationsAsynchronously);private readonly ManualResetEventSlim _release=new();
        internal void Block(){Entered.TrySetResult();Assert.True(_release.Wait(TimeSpan.FromSeconds(8)));}
        internal void Release()=>_release.Set();public void Dispose(){Release();_release.Dispose();}
    }
}
