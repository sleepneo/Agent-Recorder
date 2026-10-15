using System.Diagnostics;
using System.Security;
using System.Text;
using System.Text.Json;
using AgentRecorder.Infrastructure;
using Xunit;

namespace AgentRecorder.Tests;

[Collection("NonParallel-AgentRecorderDataDir")]
public sealed class DeletionRequestCrashTests
{
    private static readonly Lazy<Task<string>> Child=new(BuildChild);
    [Theory][InlineData("request")][InlineData("reserved")][InlineData("accepted")]
    public async Task ActualProcessExitAndNewProcessReplayNeverRecoverUiOrPermitOrTouchSameNameObject(string stage)
    {
        var root=Path.GetFullPath(Path.Combine(TestHelper.ProjectRoot,".local-data/temp/task362","request-crash-"+Guid.NewGuid().ToString("N")));Directory.CreateDirectory(root);var exe=await Child.Value;
        using var child=Start(exe,root,stage);var output=child.StandardOutput.ReadToEndAsync();var error=child.StandardError.ReadToEndAsync();
        try
        {
            var barrier=Path.Combine(root,"checkpoint.json");var clock=Stopwatch.StartNew();while(!File.Exists(barrier)&&!child.HasExited&&clock.Elapsed<TimeSpan.FromSeconds(15))await Task.Delay(10);
            Assert.True(File.Exists(barrier),child.HasExited?await error:"request checkpoint timeout");var point=JsonDocument.Parse(await File.ReadAllTextAsync(barrier));Assert.Equal(child.Id,point.RootElement.GetProperty("pid").GetInt32());
            child.Kill();await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));Assert.NotEqual(0,child.ExitCode);var media=Path.Combine(root,"Videos","owned.mp4");
            var original=JsonSerializer.Deserialize<ObservedFileIdentity>(point.RootElement.GetProperty("original").GetRawText())!;
            if(stage=="accepted")
            {
                using(var h=WindowsFileMetadataApi.Instance.Open(@"\\?\"+media,0x80,7,3,0x00200000,out var win32)){Assert.True(h.IsInvalid);Assert.Equal(2,win32);}
                using(var f=new FileStream(media,FileMode.CreateNew,FileAccess.Write))f.Write(Enumerable.Range(0,12).Select(i=>(byte)i).ToArray());
            }
            var before=WindowsReadOnlyFileMetadataProbe.Instance.Observe(media).Before!.Identity;if(stage=="accepted")Assert.NotEqual(original,before);else Assert.Equal(original,before);
            using var reader=Start(exe,root,"read");var readOutput=reader.StandardOutput.ReadToEndAsync();var readError=reader.StandardError.ReadToEndAsync();await reader.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15));Assert.True(reader.ExitCode==0,await readError);
            var read=JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(root,"read.json")));var data=read.RootElement;
            Assert.Equal(0,data.GetProperty("native_calls").GetInt32());Assert.Equal(0,data.GetProperty("ui_calls").GetInt32());Assert.Equal(point.RootElement.GetProperty("id").GetString(),data.GetProperty("id").GetString());Assert.NotEqual(point.RootElement.GetProperty("host").GetString(),data.GetProperty("host").GetString());
            Assert.Equal(stage=="request"?"confirmation_invalidated":"unknown",data.GetProperty("request_state").GetString());Assert.Equal(data.GetProperty("request_state").GetString(),data.GetProperty("replay_state").GetString());
            if(stage=="accepted")Assert.Equal("accepted",data.GetProperty("disposition").GetString());else Assert.Equal(JsonValueKind.Null,data.GetProperty("disposition").ValueKind);
            Assert.Equal(before,WindowsReadOnlyFileMetadataProbe.Instance.Observe(media).Before!.Identity);Assert.Equal(Enumerable.Range(0,12).Select(i=>(byte)i),File.ReadAllBytes(media));
            File.WriteAllText(Path.Combine(root,"parent-result.json"),JsonSerializer.Serialize(new{stage,pid=child.Id,exit=child.ExitCode,reader_pid=reader.Id,reader_exit=reader.ExitCode,original,current=before,stdout=await output,stderr=await error,reader_stdout=await readOutput,reader_stderr=await readError}),new UTF8Encoding(false));
        }
        finally{if(!child.HasExited){child.Kill();await child.WaitForExitAsync();}}
    }
    private static Process Start(string exe,string root,string stage)
    {
        var info=new ProcessStartInfo(exe){UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true};info.ArgumentList.Add(root);info.ArgumentList.Add(stage);info.Environment["TASK361_OWNED_ROOT"]=Path.Combine(TestHelper.ProjectRoot,".local-data/temp/task362");return Process.Start(info)!;
    }
    private static async Task<string> BuildChild()
    {
        var root=Path.Combine(TestHelper.ProjectRoot,".local-data/temp/task362","request-child-host-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);var tested=AppContext.BaseDirectory;
        var names=new[]{"AgentRecorder.Core","AgentRecorder.Infrastructure","AgentRecorder.Persistence","AgentRecorder.Capture","AgentRecorder.Windows","AgentRecorder.Security","AgentRecorder.Logging","AgentRecorder.UI.Geometry","AgentRecorder.Api","Microsoft.Data.Sqlite","SQLitePCLRaw.core","SQLitePCLRaw.batteries_v2","SQLitePCLRaw.provider.e_sqlite3"};
        var project="<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net8.0-windows10.0.19041.0</TargetFramework><UseWindowsForms>true</UseWindowsForms><AssemblyName>AgentRecorder.Tests</AssemblyName><ImplicitUsings>enable</ImplicitUsings><Nullable>enable</Nullable><NuGetAudit>false</NuGetAudit></PropertyGroup><ItemGroup>"+string.Join("",names.Select(n=>$"<Reference Include=\"{n}\"><HintPath>{SecurityElement.Escape(Path.Combine(tested,n+".dll"))}</HintPath></Reference>"))+"</ItemGroup></Project>";
        var path=Path.Combine(root,"Probe.csproj");await File.WriteAllTextAsync(path,project,new UTF8Encoding(false));await File.WriteAllTextAsync(Path.Combine(root,"Program.cs"),ChildSource,new UTF8Encoding(false));
        var info=new ProcessStartInfo("dotnet"){UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true};foreach(var arg in new[]{"build",path,"-c","Release","--nologo","-p:NuGetAudit=false"})info.ArgumentList.Add(arg);
        using var p=Process.Start(info)!;var output=p.StandardOutput.ReadToEndAsync();var error=p.StandardError.ReadToEndAsync();try{await p.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(60));}finally{if(!p.HasExited){p.Kill();await p.WaitForExitAsync();}}
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
using AgentRecorder.Logging;
using AgentRecorder.Capture;
using Microsoft.Data.Sqlite;
using Microsoft.Win32.SafeHandles;
var root=Path.GetFullPath(args[0]);var stage=args[1];
if(!root.StartsWith(Path.GetFullPath(Environment.GetEnvironmentVariable("TASK361_OWNED_ROOT")!)+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase))return 2;
DataDirResolver.SetOverride(root);Environment.SetEnvironmentVariable("AGENT_RECORDER_TEST_MODE","1");
var store=new SqliteOperationalStore(Path.Combine(root,"history.db"));var videos=Path.Combine(root,"Videos");var media=Path.Combine(videos,"owned.mp4");
const string sid="S-1-5-21-request-child",session="request-child-session",key="owned-child-key";
if(stage!="read")
{
 Directory.CreateDirectory(videos);using(var f=new FileStream(media,FileMode.CreateNew,FileAccess.Write))f.Write(Enumerable.Range(0,12).Select(i=>(byte)i).ToArray());store.Initialize();
}
using var writer=new SqliteOrdinaryRecordingHistoryStore(store);using var engine=new RecordingEngine(new Quiet());engine.ConfigureOrdinaryRecordingHistory(writer);
ObservedFileIdentity? identity=null;
if(stage!="read")
{
 identity=WindowsReadOnlyFileMetadataProbe.Instance.Observe(media).Before!.Identity!;var at=DateTimeOffset.UtcNow.AddDays(-2);
 writer.Register(new OrdinaryRecordingHistorySnapshot("candidate",at,"completed","display","display_capture","Owned API child",15,"none",null,null,null,null,media,null,null,null,null,null,null,at.AddSeconds(1),at.AddSeconds(16),"duration_reached",12,15){BundleRegistration=new(null),PublicationReceipt=PublicationReceipt.IssueForTests(media,12,identity)});
}
var native=new Native(media);var ui=new Ui();var io=new Io(root,stage,native,ui,identity);
using var authority=new LocalRetentionApproval(engine,store,writer,sid,session,videos,ui,native:native);
using var coordinator=new RetentionDeletionCoordinator(store,sid,session,videos,authority,database:io);
ApiKeyAuth.InitializeForTesting(root);var tray=new Tray();engine.SetTray(tray);
var server=new ApiServer(engine,new Quiet(),tray,null,null,null,null,null,null,null,null,null,0,retentionDeletionGateway:new SqliteRetentionDeletionGateway(coordinator));server.Start();
using var client=new HttpClient(new HttpClientHandler{UseProxy=false}){BaseAddress=new($"http://127.0.0.1:{server.BoundPort}")};client.DefaultRequestHeaders.Add("X-Agent-Recorder-Key",ApiKeyAuth.CurrentApiKey);
async Task<JsonElement> Post(){using var request=new HttpRequestMessage(HttpMethod.Post,"/api/v1/retention/deletion-requests"){Content=new StringContent("{\"recording_id\":\"candidate\"}",Encoding.UTF8,"application/json")};request.Headers.Add("Idempotency-Key",key);using var reply=await client.SendAsync(request);if((int)reply.StatusCode!=202)throw new Exception("POST code "+(int)reply.StatusCode);return JsonDocument.Parse(await reply.Content.ReadAsStringAsync()).RootElement.GetProperty("data").Clone();}
if(stage=="read")
{
 string id;using(var c=new SqliteConnection($"Data Source={store.DatabasePath};Mode=ReadOnly;Pooling=False")){c.Open();using var cmd=c.CreateCommand();cmd.CommandText="SELECT request_id FROM ordinary_deletion_requests;";id=(string)cmd.ExecuteScalar()!;}
 using var reply=await client.GetAsync("/api/v1/retention/deletion-requests/"+id);if((int)reply.StatusCode!=200)return 4;var data=JsonDocument.Parse(await reply.Content.ReadAsStringAsync()).RootElement.GetProperty("data");var replay=await Post();
 File.WriteAllText(Path.Combine(root,"read.json"),JsonSerializer.Serialize(new{id,native_calls=native.Calls,ui_calls=ui.Calls,request_state=data.GetProperty("request_state").GetString(),replay_state=replay.GetProperty("request_state").GetString(),disposition=data.GetProperty("disposition_progress").ValueKind==JsonValueKind.Null?null:data.GetProperty("disposition_progress").GetString(),host=DeletionLedger.HostId}));server.Stop();return 0;
}
await Post();await coordinator.WaitForIdleAsync();return 5;
sealed class Quiet:AuditLogger{public override void Log(string e,object p){}}
sealed class Ui:ILocalRetentionApprovalUi
{internal int Calls;public LocalRetentionUiSession Begin(LocalRetentionIntent i,CancellationToken c){Calls++;return new(Task.FromResult(new LocalRetentionDecision(i,LocalRetentionUiChoice.Approve)),Task.FromResult(true));}}
sealed class Tray:ITrayContext
{
 public string HostMode=>"tray";public bool SupportsRegionSelectionUi=>false;
 public void RequestConfirmation(RecordingConfirmationPresentation p,Action<ConfirmationDecision> c)=>throw new Exception("No capture UI");
 public void RequestRegionSelection(int t,Action<string,int,int,int,int,string,string> c)=>throw new Exception("No selection");public void SetRecording(RecordingUiPresentation p)=>throw new Exception("No capture");public void SetIdle(RecordingUiPresentation p){}public void SetAllIdle(){}public void ShowError(string s){}
}
sealed class Io(string root,string stage,Native native,Ui ui,ObservedFileIdentity? identity):DeletionLedgerDatabase
{
 internal override void Step(string name,SqliteConnection c,SqliteTransaction? t)
 {
  var match=stage switch{"request"=>"create_after_close","reserved"=>"reserve_after_close","accepted"=>"finish_before_open",_=>""};if(name!=match)return;
  string id,phase;using(var reader=new SqliteConnection($"Data Source={Path.Combine(root,"history.db")};Mode=ReadOnly;Pooling=False")){reader.Open();using var cmd=reader.CreateCommand();cmd.CommandText="SELECT request_id,phase FROM ordinary_deletion_requests;";using var row=cmd.ExecuteReader();if(!row.Read())throw new Exception("No committed request");id=row.GetString(0);phase=row.GetString(1);}
  var tmp=Path.Combine(root,"checkpoint.tmp");File.WriteAllText(tmp,JsonSerializer.Serialize(new{pid=Environment.ProcessId,id,phase,stage,native_calls=native.Calls,native_accepted=native.Accepted,ui_calls=ui.Calls,original=identity,host=DeletionLedger.HostId}));File.Move(tmp,Path.Combine(root,"checkpoint.json"));new ManualResetEvent(false).WaitOne();
 }
}
sealed class Native(string media):IOriginalFileHoldNative
{
 private readonly IOriginalFileHoldNative n=WindowsOriginalFileHoldNative.Instance;internal int Calls;internal bool Accepted;
 public bool Supported=>n.Supported;public uint DriveType(string r)=>n.DriveType(r);public SafeFileHandle Open(string p,uint a,uint s,uint d,uint f,out int e)=>n.Open(p,a,s,d,f,out e);
 public bool Basic(SafeFileHandle h,out FileBasicInformation v,out int e)=>n.Basic(h,out v,out e);public bool Standard(SafeFileHandle h,out FileStandardInformation v,out int e)=>n.Standard(h,out v,out e);public bool Identity(SafeFileHandle h,out FileIdentityInformation v,out int e)=>n.Identity(h,out v,out e);public uint FinalPath(SafeFileHandle h,StringBuilder b,int c,out int e)=>n.FinalPath(h,b,c,out e);public OriginalFileCloseOutcome Close(SafeFileHandle h)=>n.Close(h);
 public OriginalFileDispositionResult RequestDisposition(SafeFileHandle h){var b=new StringBuilder(2048);if(n.FinalPath(h,b,2048,out _)==0||b.ToString()!=@"\\?\"+media)throw new Exception("Wrong original target");Calls++;var r=n.RequestDisposition(h);Accepted=r.Outcome==OriginalFileDispositionOutcome.Accepted;return r;}
}
""";
}
