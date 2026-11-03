using System.Diagnostics;
using System.Security;
using System.Text;
using System.Text.Json;
using AgentRecorder.Infrastructure;
using Xunit;
namespace AgentRecorder.Tests;
[Collection("NonParallel-AgentRecorderDataDir")]
public sealed class BatchDeletion388ProcessCrashTests
{
    private static readonly Lazy<Task<string>> Child=new(BuildChild);
    [Theory][InlineData("parent")][InlineData("reserved")][InlineData("approved")][InlineData("before_native")][InlineData("after_native")][InlineData("item_committed")]
    public async Task ActualProcessExitAndFreshProcessNeverRecoverApprovalOrDispatchRemaining(string stage)
    {
        var root=Path.GetFullPath(Path.Combine(TestHelper.ProjectRoot,".local-data/temp/task388","batch-crash-"+Guid.NewGuid().ToString("N")));Directory.CreateDirectory(root);var exe=await Child.Value;
        var first=await Run(exe,root,stage);Assert.Equal(42,first.Code);using var point=JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(root,"checkpoint.json")));
        Assert.Equal(stage,point.RootElement.GetProperty("stage").GetString());Assert.InRange(point.RootElement.GetProperty("ui_calls").GetInt32(),0,1);
        var media=Path.Combine(root,"Videos","owned0.mp4");
        if(stage is "after_native" or "item_committed")
        {
            using(var h=WindowsFileMetadataApi.Instance.Open(@"\\?\"+media,0x80,7,3,0x00200000,out var error)){Assert.True(h.IsInvalid);Assert.Equal(2,error);}
            using var stream=new FileStream(media,FileMode.CreateNew,FileAccess.Write);stream.Write(Enumerable.Range(0,12).Select(n=>(byte)n).ToArray());
        }
        var original=WindowsReadOnlyFileMetadataProbe.Instance.Observe(media).Before!.Identity;var facts=SqlitePublicationIdentityReferenceTests.OldBytes(Path.Combine(root,"history.db"),true);
        var next=await Run(exe,root,"read");Assert.Equal(0,next.Code);using var read=JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(root,"read.json")));var json=read.RootElement;
        Assert.NotEqual(point.RootElement.GetProperty("host").GetString(),json.GetProperty("host").GetString());Assert.Equal(0,json.GetProperty("ui_calls").GetInt32());Assert.Equal(0,json.GetProperty("native_calls").GetInt32());
        foreach(var name in new[]{"data","replay"}){var data=json.GetProperty(name);Assert.Equal(point.RootElement.GetProperty("id").GetString(),data.GetProperty("batch_deletion_request_id").GetString());Assert.Equal("interrupted",data.GetProperty("batch_state").GetString());Assert.Equal("not_requested",data.GetProperty("items")[1].GetProperty("state").GetString());}
        Assert.Equal(original,WindowsReadOnlyFileMetadataProbe.Instance.Observe(media).Before!.Identity);Assert.Equal(Enumerable.Range(0,12).Select(n=>(byte)n),File.ReadAllBytes(media));Assert.Equal(facts,SqlitePublicationIdentityReferenceTests.OldBytes(Path.Combine(root,"history.db"),true));
        await File.WriteAllTextAsync(Path.Combine(root,"process-result.json"),JsonSerializer.Serialize(new{stage,first,next}),new UTF8Encoding(false));
    }
    private static async Task<(int Code,string Output,string Error)> Run(string exe,string root,string stage)
    {
        var info=new ProcessStartInfo(exe){UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true};info.ArgumentList.Add(root);info.ArgumentList.Add(stage);info.Environment["TASK388_OWNED_ROOT"]=Path.Combine(TestHelper.ProjectRoot,".local-data/temp/task388");
        using var p=Process.Start(info)!;var output=p.StandardOutput.ReadToEndAsync();var error=p.StandardError.ReadToEndAsync();await p.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(25));var result=(p.ExitCode,await output,await error);Assert.True(result.ExitCode is 0 or 42,result.Item3);return result;
    }
    private static async Task<string> BuildChild()
    {
        var root=Path.Combine(TestHelper.ProjectRoot,".local-data/temp/task388","batch-child-host-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);var tested=AppContext.BaseDirectory;
        var names=new[]{"AgentRecorder.Core","AgentRecorder.Infrastructure","AgentRecorder.Persistence","AgentRecorder.Capture","AgentRecorder.Windows","AgentRecorder.Security","AgentRecorder.Logging","AgentRecorder.UI.Geometry","AgentRecorder.Api","Microsoft.Data.Sqlite","SQLitePCLRaw.core","SQLitePCLRaw.batteries_v2","SQLitePCLRaw.provider.e_sqlite3"};
        var project="<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net8.0-windows10.0.19041.0</TargetFramework><UseWindowsForms>true</UseWindowsForms><AssemblyName>AgentRecorder.Tests</AssemblyName><ImplicitUsings>enable</ImplicitUsings><Nullable>enable</Nullable><NuGetAudit>false</NuGetAudit></PropertyGroup><ItemGroup>"+string.Join("",names.Select(n=>$"<Reference Include=\"{n}\"><HintPath>{SecurityElement.Escape(Path.Combine(tested,n+".dll"))}</HintPath></Reference>"))+"</ItemGroup></Project>";
        var path=Path.Combine(root,"Probe.csproj");await File.WriteAllTextAsync(path,project,new UTF8Encoding(false));await File.WriteAllTextAsync(Path.Combine(root,"Program.cs"),ChildSource,new UTF8Encoding(false));
        var info=new ProcessStartInfo("dotnet"){UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true};foreach(var arg in new[]{"build",path,"-c","Release","--nologo","-p:NuGetAudit=false","-p:UseSharedCompilation=false","-nodeReuse:false"})info.ArgumentList.Add(arg);
        using var p=Process.Start(info)!;var output=p.StandardOutput.ReadToEndAsync();var error=p.StandardError.ReadToEndAsync();await p.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(60));
        await File.WriteAllTextAsync(Path.Combine(root,"build.log"),await output+await error,new UTF8Encoding(false));await File.WriteAllTextAsync(Path.Combine(root,"build.exit"),p.ExitCode.ToString(),new UTF8Encoding(false));Assert.Equal(0,p.ExitCode);
        var bin=Path.Combine(root,"bin/Release/net8.0-windows10.0.19041.0");File.Copy(Path.Combine(tested,"runtimes/win-x64/native/e_sqlite3.dll"),Path.Combine(bin,"e_sqlite3.dll"));return Path.Combine(bin,"AgentRecorder.Tests.exe");
    }
    private const string ChildSource="""
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using AgentRecorder.Api;
using AgentRecorder.Core;
using AgentRecorder.Infrastructure;
using AgentRecorder.Persistence;
using AgentRecorder.Capture;
using AgentRecorder.Logging;
using Microsoft.Data.Sqlite;
using Microsoft.Win32.SafeHandles;
var root=Path.GetFullPath(args[0]);var stage=args[1];
if(!root.StartsWith(Path.GetFullPath(Environment.GetEnvironmentVariable("TASK388_OWNED_ROOT")!)+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase))return 2;
_ = Task.Delay(TimeSpan.FromSeconds(20)).ContinueWith(_=>Environment.Exit(90));
DataDirResolver.SetOverride(root);Environment.SetEnvironmentVariable("AGENT_RECORDER_TEST_MODE","1");
var store=new SqliteOperationalStore(Path.Combine(root,"history.db"));var videos=Path.Combine(root,"Videos");
const string sid="S-1-5-21-batch-child",session="batch-child-session",key="batch-process-key";
if(stage!="read"){Directory.CreateDirectory(videos);store.Initialize();}
using var writer=new SqliteOrdinaryRecordingHistoryStore(store);using var engine=new RecordingEngine(new Quiet());engine.ConfigureOrdinaryRecordingHistory(writer);
if(stage!="read")for(var i=0;i<2;i++)
{
 var media=Path.Combine(videos,"owned"+i+".mp4");using(var f=new FileStream(media,FileMode.CreateNew,FileAccess.Write))f.Write(Enumerable.Range(0,12).Select(n=>(byte)n).ToArray());
 var identity=WindowsReadOnlyFileMetadataProbe.Instance.Observe(media).Before!.Identity!;var at=DateTimeOffset.UtcNow.AddDays(-2);
 writer.Register(new OrdinaryRecordingHistorySnapshot("r"+i,at,"completed","display","display_capture","Synthetic batch process",15,"none",null,null,null,null,media,null,null,null,null,null,null,at.AddSeconds(1),at.AddSeconds(16),"duration_reached",12,15){BundleRegistration=new(null),PublicationReceipt=PublicationReceipt.IssueForTests(media,12,identity)});
}
Native native=null!;var ui=new SyntheticUi();
void Checkpoint()
{
 using var c=new SqliteConnection($"Data Source={store.DatabasePath};Mode=ReadOnly;Pooling=False");c.Open();using var cmd=c.CreateCommand();cmd.CommandText="SELECT batch_id FROM ordinary_deletion_batches;";var id=(string)cmd.ExecuteScalar()!;
 File.WriteAllText(Path.Combine(root,"checkpoint.json"),JsonSerializer.Serialize(new{id,pid=Environment.ProcessId,host=DeletionLedger.HostId,stage,ui_calls=ui.Calls,native_calls=native.Calls,native_accepted=native.Accepted}));Environment.Exit(42);
}
native=new(root,stage,Checkpoint);var io=new Io(stage,Checkpoint);
using var authority=new LocalRetentionApproval(engine,store,writer,sid,session,videos,ui,native:native);
using var single=new RetentionDeletionCoordinator(store,sid,session,videos,authority,database:io);
using var batch=new RetentionBatchDeletionCoordinator(store,sid,session,videos,single,database:io);
batch.CheckpointForTests=(name,n)=>{if(stage=="item_committed"&&name=="item_committed"&&n==0)Checkpoint();};
ApiKeyAuth.InitializeForTesting(root);var tray=new Tray();engine.SetTray(tray);
var server=new ApiServer(engine,new Quiet(),tray,null,null,null,null,null,null,null,null,null,0,retentionDeletionGateway:new SqliteRetentionDeletionGateway(single),retentionBatchDeletionGateway:new SqliteRetentionBatchDeletionGateway(batch));server.Start();
using var client=new HttpClient(new HttpClientHandler{UseProxy=false}){BaseAddress=new($"http://127.0.0.1:{server.BoundPort}")};client.DefaultRequestHeaders.Add("X-Agent-Recorder-Key",ApiKeyAuth.CurrentApiKey);
async Task<JsonElement> Post(){using var request=new HttpRequestMessage(HttpMethod.Post,"/api/v1/retention/batch-deletion-requests"){Content=new StringContent("{\"recording_ids\":[\"r0\",\"r1\"]}",Encoding.UTF8,"application/json")};request.Headers.Add("Idempotency-Key",key);using var reply=await client.SendAsync(request);if((int)reply.StatusCode!=202)throw new Exception("POST code "+(int)reply.StatusCode);return JsonDocument.Parse(await reply.Content.ReadAsStringAsync()).RootElement.GetProperty("data").Clone();}
if(stage=="read")
{
 string id;using(var c=new SqliteConnection($"Data Source={store.DatabasePath};Mode=ReadOnly;Pooling=False")){c.Open();using var cmd=c.CreateCommand();cmd.CommandText="SELECT batch_id FROM ordinary_deletion_batches;";id=(string)cmd.ExecuteScalar()!;}
 using var response=await client.GetAsync("/api/v1/retention/batch-deletion-requests/"+id);if((int)response.StatusCode!=200)return 4;
 var data=JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.GetProperty("data").Clone();var replay=await Post();
 File.WriteAllText(Path.Combine(root,"read.json"),JsonSerializer.Serialize(new{id,pid=Environment.ProcessId,host=DeletionLedger.HostId,native_calls=native.Calls,ui_calls=ui.Calls,data,replay}));server.Stop();return 0;
}
await Post();await batch.WaitForIdleAsync();return 5;
sealed class Quiet:AuditLogger{public override void Log(string e,object p){}}
// Synthetic approval for owned crash fixtures only; never a production provider.
sealed class SyntheticUi:ILocalRetentionApprovalUi
{internal int Calls;public LocalRetentionUiSession Begin(LocalRetentionIntent i,CancellationToken c){Calls++;return new(Task.FromResult(new LocalRetentionDecision(i,LocalRetentionUiChoice.Approve)),Task.FromResult(true));}}
sealed class Tray:ITrayContext
{
 public string HostMode=>"tray";public bool SupportsRegionSelectionUi=>false;
 public void RequestConfirmation(RecordingConfirmationPresentation p,Action<ConfirmationDecision> c)=>throw new Exception("No capture UI");
 public void RequestRegionSelection(int t,Action<string,int,int,int,int,string,string> c)=>throw new Exception("No selection");public void SetRecording(RecordingUiPresentation p)=>throw new Exception("No capture");public void SetIdle(RecordingUiPresentation p){}public void SetAllIdle(){}public void ShowError(string s){}
}
sealed class Io(string stage,Action checkpoint):DeletionLedgerDatabase
{
 internal override void Step(string name,SqliteConnection c,SqliteTransaction? t)
 {var match=stage switch{"parent"=>"batchcreate_after_close","reserved"=>"batchreserve_after_close","approved"=>"reserve_after_close",_=>""};if(name==match)checkpoint();}
}
sealed class Native(string root,string stage,Action checkpoint):IOriginalFileHoldNative
{
 private readonly IOriginalFileHoldNative n=WindowsOriginalFileHoldNative.Instance;internal int Calls;internal bool Accepted;
 public bool Supported=>n.Supported;public uint DriveType(string r)=>n.DriveType(r);public SafeFileHandle Open(string p,uint a,uint s,uint d,uint f,out int e)=>n.Open(p,a,s,d,f,out e);
 public bool Basic(SafeFileHandle h,out FileBasicInformation v,out int e)=>n.Basic(h,out v,out e);public bool Standard(SafeFileHandle h,out FileStandardInformation v,out int e)=>n.Standard(h,out v,out e);public bool Identity(SafeFileHandle h,out FileIdentityInformation v,out int e)=>n.Identity(h,out v,out e);public uint FinalPath(SafeFileHandle h,StringBuilder b,int c,out int e)=>n.FinalPath(h,b,c,out e);public OriginalFileCloseOutcome Close(SafeFileHandle h)=>n.Close(h);
 public OriginalFileDispositionResult RequestDisposition(SafeFileHandle h)
 {
  var b=new StringBuilder(2048);if(n.FinalPath(h,b,2048,out _)==0||!b.ToString().StartsWith(@"\\?\"+Path.Combine(root,"Videos")+"\\",StringComparison.OrdinalIgnoreCase))throw new Exception("Outside owned Videos");
  if(stage=="before_native")checkpoint();Calls++;var r=n.RequestDisposition(h);Accepted=r.Outcome==OriginalFileDispositionOutcome.Accepted;
  if(stage=="after_native")checkpoint();return r;
 }
}

""";
}
