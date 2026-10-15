using System.Diagnostics;
using System.Security;
using System.Text;
using System.Text.Json;
using AgentRecorder.Persistence;
using AgentRecorder.Infrastructure;
using Xunit;

namespace AgentRecorder.Tests;

[Collection("NonParallel-AgentRecorderDataDir")]
public sealed class DeletionLedgerCrashTests
{
    private static readonly Lazy<Task<string>> Child=new(BuildChild);
    [Theory]
    [InlineData("before_commit")][InlineData("prepared")][InlineData("accepted")][InlineData("settled")]
    public async Task ActualProcessExitNeverReplaysOldOperationAndNewProcessReadsOnly(string stage)
    {
        var root=Path.GetFullPath(Path.Combine(TestHelper.ProjectRoot,".local-data/temp/task362","crash-"+Guid.NewGuid().ToString("N")));Directory.CreateDirectory(root);
        var exe=await Child.Value;using var child=Start(exe,root,stage);var stdout=child.StandardOutput.ReadToEndAsync();var stderr=child.StandardError.ReadToEndAsync();
        try
        {
            var barrier=Path.Combine(root,"checkpoint.json");var watch=Stopwatch.StartNew();
            while(!File.Exists(barrier)&&!child.HasExited&&watch.Elapsed<TimeSpan.FromSeconds(15))await Task.Delay(10);
            Assert.True(File.Exists(barrier),child.HasExited?await stderr:"checkpoint timeout");
            var point=JsonDocument.Parse(await File.ReadAllTextAsync(barrier,Encoding.UTF8));Assert.Equal(child.Id,point.RootElement.GetProperty("pid").GetInt32());
            Assert.Equal(stage,point.RootElement.GetProperty("stage").GetString());child.Kill();await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));Assert.NotEqual(0,child.ExitCode);
            var media=Path.Combine(root,"Videos","owned.mp4");
            ObservedFileIdentity? replacement=null;
            if(stage is "accepted" or "settled")
            {
                using(var h=WindowsFileMetadataApi.Instance.Open(@"\\?\"+media,0x80,7,3,0x00200000,out var error)){Assert.True(h.IsInvalid);Assert.Equal(2,error);}
                using(var fresh=new FileStream(media,FileMode.CreateNew,FileAccess.Write))fresh.Write(Enumerable.Range(0,12).Select(i=>(byte)i).ToArray());
                replacement=WindowsReadOnlyFileMetadataProbe.Instance.Observe(media).Before!.Identity;
                Assert.NotEqual(JsonSerializer.Deserialize<ObservedFileIdentity>(point.RootElement.GetProperty("original_identity").GetRawText()),replacement);
            }
            using var reader=Start(exe,root,"read");var readOut=reader.StandardOutput.ReadToEndAsync();var readErr=reader.StandardError.ReadToEndAsync();await reader.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15));Assert.True(reader.ExitCode==0,await readErr);
            var result=JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(root,"read.json"),Encoding.UTF8));Assert.Equal(0,result.RootElement.GetProperty("native_calls").GetInt32());
            Assert.NotEqual(point.RootElement.GetProperty("host").GetString(),result.RootElement.GetProperty("host").GetString());
            Assert.Equal(stage=="before_commit"?0:1,result.RootElement.GetProperty("count").GetInt32());
            if(stage!="before_commit")
            {
                Assert.Equal(stage=="settled"?"Settled":"OtherHostUnknown",result.RootElement.GetProperty("state").GetString());
                if(stage=="settled")Assert.Equal("DispositionAccepted",result.RootElement.GetProperty("progress").GetString());
                else Assert.Equal(JsonValueKind.Null,result.RootElement.GetProperty("progress").ValueKind);
            }
            Assert.Equal(Enumerable.Range(0,12).Select(i=>(byte)i),File.ReadAllBytes(media));
            if(replacement is not null)Assert.Equal(replacement,WindowsReadOnlyFileMetadataProbe.Instance.Observe(media).Before!.Identity);
            File.WriteAllText(Path.Combine(root,"parent-result.json"),JsonSerializer.Serialize(new{stage,pid=child.Id,exit=child.ExitCode,reader_pid=reader.Id,reader_exit=reader.ExitCode,reader_stdout=await readOut,reader_stderr=await readErr,stdout=await stdout,stderr=await stderr}),new UTF8Encoding(false));
        }
        finally{if(!child.HasExited){child.Kill();await child.WaitForExitAsync();}}
    }
    private static Process Start(string exe,string root,string stage)
    {
        var info=new ProcessStartInfo(exe){UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true};info.ArgumentList.Add(root);info.ArgumentList.Add(stage);info.Environment["TASK360_OWNED_ROOT"]=Path.Combine(TestHelper.ProjectRoot,".local-data/temp/task362");return Process.Start(info)!;
    }
    private static async Task<string> BuildChild()
    {
        var root=Path.Combine(TestHelper.ProjectRoot,".local-data/temp/task362","child-host-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);
        var tested=AppContext.BaseDirectory;
        var names=new[]{"AgentRecorder.Core","AgentRecorder.Infrastructure","AgentRecorder.Persistence","AgentRecorder.Capture","AgentRecorder.Windows","AgentRecorder.Security","AgentRecorder.Logging","AgentRecorder.UI.Geometry","Microsoft.Data.Sqlite","SQLitePCLRaw.core","SQLitePCLRaw.batteries_v2","SQLitePCLRaw.provider.e_sqlite3"};
        var project="<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net8.0-windows10.0.19041.0</TargetFramework><UseWindowsForms>true</UseWindowsForms><AssemblyName>AgentRecorder.Tests</AssemblyName><ImplicitUsings>enable</ImplicitUsings><Nullable>enable</Nullable><NuGetAudit>false</NuGetAudit></PropertyGroup><ItemGroup>"+
            string.Join("",names.Select(n=>$"<Reference Include=\"{n}\"><HintPath>{SecurityElement.Escape(Path.Combine(tested,n+".dll"))}</HintPath></Reference>"))+"</ItemGroup></Project>";
        var csproj=Path.Combine(root,"Probe.csproj");await File.WriteAllTextAsync(csproj,project,new UTF8Encoding(false));await File.WriteAllTextAsync(Path.Combine(root,"Program.cs"),ChildSource,new UTF8Encoding(false));
        var info=new ProcessStartInfo("dotnet"){UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true};foreach(var arg in new[]{"build",csproj,"-c","Release","--nologo","-p:NuGetAudit=false"})info.ArgumentList.Add(arg);
        using var p=Process.Start(info)!;var output=p.StandardOutput.ReadToEndAsync();var error=p.StandardError.ReadToEndAsync();
        try{await p.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(60));}finally{if(!p.HasExited){p.Kill();await p.WaitForExitAsync();}}
        await File.WriteAllTextAsync(Path.Combine(root,"build.log"),await output+await error,new UTF8Encoding(false));await File.WriteAllTextAsync(Path.Combine(root,"build.exit"),p.ExitCode.ToString(),new UTF8Encoding(false));Assert.Equal(0,p.ExitCode);
        var bin=Path.Combine(root,"bin/Release/net8.0-windows10.0.19041.0");File.Copy(Path.Combine(tested,"runtimes/win-x64/native/e_sqlite3.dll"),Path.Combine(bin,"e_sqlite3.dll"));
        return Path.Combine(bin,"AgentRecorder.Tests.exe");
    }
    private const string ChildSource="""
using System.Diagnostics;
using System.Text.Json;
using AgentRecorder.Core;
using AgentRecorder.Capture;
using AgentRecorder.Infrastructure;
using AgentRecorder.Persistence;
using AgentRecorder.Logging;
using Microsoft.Data.Sqlite;
using Microsoft.Win32.SafeHandles;
using System.Text;
var root=Path.GetFullPath(args[0]);var stage=args[1];
if(!root.StartsWith(Path.GetFullPath(Environment.GetEnvironmentVariable("TASK360_OWNED_ROOT")!)+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase))return 2;
DataDirResolver.SetOverride(root);Environment.SetEnvironmentVariable("AGENT_RECORDER_TEST_MODE","1");
var store=new SqliteOperationalStore(Path.Combine(root,"history.db"));var videos=Path.Combine(root,"Videos");
const string sid="S-1-5-21-ledger-child",session="ledger-child-session";
if(stage=="read")
{
 var rows=new DeletionLedger(store,sid,session,videos,TimeProvider.System).Page();var row=rows.SingleOrDefault();
 File.WriteAllText(Path.Combine(root,"read.json"),JsonSerializer.Serialize(new{native_calls=Native.Total,count=rows.Count,state=row?.State.ToString(),progress=row?.Progress?.ToString(),host=DeletionLedger.HostId,binding=row?.Binding}));return 0;
}
Directory.CreateDirectory(videos);var media=Path.Combine(videos,"owned.mp4");using(var f=new FileStream(media,FileMode.CreateNew,FileAccess.Write))f.Write(Enumerable.Range(0,12).Select(i=>(byte)i).ToArray());
store.Initialize();using var writer=new SqliteOrdinaryRecordingHistoryStore(store);using var engine=new RecordingEngine(new Quiet());engine.ConfigureOrdinaryRecordingHistory(writer);
var at=DateTimeOffset.UtcNow.AddDays(-2);var identity=WindowsReadOnlyFileMetadataProbe.Instance.Observe(media).Before!.Identity!;
var rowFacts=new OrdinaryRecordingHistorySnapshot("candidate",at,"completed","display","display_capture","Owned child",15,"none",null,null,null,null,media,null,null,null,null,null,null,at.AddSeconds(1),at.AddSeconds(16),"duration_reached",12,15)
 {BundleRegistration=new(null),PublicationReceipt=PublicationReceipt.IssueForTests(media,12,identity)};
writer.Register(rowFacts);var native=new Native(media);var io=new Io(root,stage,native,identity);using var approval=new LocalRetentionApproval(engine,store,writer,sid,session,videos,new Ui(),ledgerDatabase:io,native:native);
var request=approval.Request("candidate");var accepted=await request.Response.WaitAsync(TimeSpan.FromSeconds(5));if(accepted.Permit is null)return 3;
var operation=approval.RequestDeletion(accepted.Permit,"candidate");await operation.Retirement;return 4;
sealed class Quiet:AuditLogger{public override void Log(string e,object p){}}
sealed class Ui:ILocalRetentionApprovalUi{public LocalRetentionUiSession Begin(LocalRetentionIntent i,CancellationToken c)=>new(Task.FromResult(new LocalRetentionDecision(i,LocalRetentionUiChoice.Approve)),Task.FromResult(true));}
sealed class Io(string root,string stage,Native native,ObservedFileIdentity identity):DeletionLedgerDatabase
{
 internal override void Step(string name,SqliteConnection c,SqliteTransaction? t)
 {
  var match=stage switch{"before_commit"=>"prepare_before_commit","prepared"=>"prepare_after_close","accepted"=>"settle_before_open","settled"=>"settle_after_close",_=>""};if(name!=match)return;
  var tmp=Path.Combine(root,"checkpoint.tmp");File.WriteAllText(tmp,JsonSerializer.Serialize(new{pid=Environment.ProcessId,stage,native_calls=native.Calls,native_accepted=native.Accepted,original_identity=identity,host=DeletionLedger.HostId}));File.Move(tmp,Path.Combine(root,"checkpoint.json"));
  new ManualResetEvent(false).WaitOne();
 }
}
sealed class Native(string media):IOriginalFileHoldNative
{
 private readonly IOriginalFileHoldNative n=WindowsOriginalFileHoldNative.Instance;internal int Calls;internal bool Accepted;internal static int Total;
 public bool Supported=>n.Supported;public uint DriveType(string r)=>n.DriveType(r);
 public SafeFileHandle Open(string p,uint a,uint s,uint d,uint f,out int e)=>n.Open(p,a,s,d,f,out e);
 public bool Basic(SafeFileHandle h,out FileBasicInformation v,out int e)=>n.Basic(h,out v,out e);
 public bool Standard(SafeFileHandle h,out FileStandardInformation v,out int e)=>n.Standard(h,out v,out e);
 public bool Identity(SafeFileHandle h,out FileIdentityInformation v,out int e)=>n.Identity(h,out v,out e);
 public uint FinalPath(SafeFileHandle h,StringBuilder b,int c,out int e)=>n.FinalPath(h,b,c,out e);
 public OriginalFileCloseOutcome Close(SafeFileHandle h)=>n.Close(h);
 public OriginalFileDispositionResult RequestDisposition(SafeFileHandle h)
 {
  var path=new StringBuilder(2048);if(n.FinalPath(h,path,2048,out _)==0||path.ToString()!=@"\\?\"+media)throw new InvalidOperationException();
  Calls++;Interlocked.Increment(ref Total);var r=n.RequestDisposition(h);Accepted=r.Outcome==OriginalFileDispositionOutcome.Accepted;return r;
 }
}
""";
}
