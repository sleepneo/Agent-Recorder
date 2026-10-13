using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
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

namespace AgentRecorder.Tests;

internal sealed record HistoryArtifactFixture(SqliteOperationalStore Store,string Root,string EntryId,string Sid,string Session,
    string Path,Action<string> Execute,Action DisposeOwned,Func<PublicationIdentityAnchor?> Anchor,Func<int> Starts) : IDisposable
{
    internal static string Parent=>System.IO.Path.GetFullPath(System.IO.Path.Combine(TestHelper.ProjectRoot,".local-data/temp/task350"));
    public void Dispose()=>DisposeOwned();
}

public sealed partial class PlanMediaPublicationAnchorTests
{
    internal static async Task<HistoryArtifactFixture> ArtifactFixture(string kind,bool completed=true,bool unknown=false,string? terminal=null)
    {
        var f=await Fixture.Open(kind,unknown:unknown,ownedParent:HistoryArtifactFixture.Parent);
        try
        {
            if(completed)f.Complete();
            else if(terminal is not null)
            {
                var interrupted=terminal=="session_interrupted";var meta=f.Backend.Publish();var at=f.Now.AddSeconds(1);
                if(f.Standing is not null)Assert.True(f.Standing.CompleteTermination(interrupted?StandingLeaseLifecycleTerminationKind.SessionInterrupted:StandingLeaseLifecycleTerminationKind.BackendStartFailure,1,meta,at).Succeeded);
                else if(f.Recurring is not null)Assert.True(f.Recurring.CompleteTermination(interrupted?RecurringLeaseLifecycleTerminationKind.SessionInterrupted:RecurringLeaseLifecycleTerminationKind.BackendStartFailure,1,meta,at).Succeeded);
                else Assert.True(f.RequiredRepository!.MarkPostCommitFailure(f.Owner,"task350_failure",at,sessionInterrupted:interrupted,startOutcomeKnownFailed:!interrupted).Succeeded);
                Assert.Equal("blocked",f.Status().LatestOccurrence!.Status);
            }
            return new(new(f.Store.DatabasePath),f.Root,"plan:"+f.Occurrence,f.Sid,f.Session,f.Backend.Path,
                f.Execute,f.Dispose,f.Anchor,()=>f.Backend.Starts);
        }
        catch(Exception primary){try{f.Dispose();}catch(Exception cleanup){throw new AggregateException(primary,cleanup);}throw;}
    }
}

public sealed partial class FutureWindowPublicationAnchorTests
{
    internal static void ArtifactConstructionFault()=>new Fixture(failConstruction:true,ownedParent:HistoryArtifactFixture.Parent);
    internal static async Task<HistoryArtifactFixture> ArtifactFixture(bool completed=true,bool unknown=false,string? terminal=null)
    {
        var f=new Fixture(ownedParent:HistoryArtifactFixture.Parent){UnknownNative=unknown};
        try
        {
            var pending=f.Pending();
            if(completed)
            {
                await f.Prepare();var start=f.Claim(pending);var rec=await f.Start(start);
                var tracking=f.Coordinator.TrackRunForTests(start);f.Process!.Finish.TrySetResult();
                await tracking.WaitAsync(TimeSpan.FromSeconds(10));Assert.Equal("completed",f.Row(pending.AuthorizationId).StatusCode);
                Assert.Equal(RecState.completed,rec.State);
            }
            else if(terminal is not null)
            {
                if(terminal=="revoked")f.Future.Revoke(pending.AuthorizationId,f.Sid,f.Session,DateTimeOffset.UtcNow);
                else if(terminal=="expired")
                {
                    f.Future.Approve(pending.AuthorizationId,f.Sid,f.Session,"task350-expiring",DateTimeOffset.UtcNow);
                    var now=f.Row(pending.AuthorizationId).ExpiresAtUtc!.Value.AddSeconds(1);
                    var process=new AgentRecorder.Windows.FutureWindowProcessSnapshot("window_42",(nint)42,7,now.UtcDateTime.ToFileTimeUtc(),f.Sid,1,
                        pending.ExecutableIdentity.CanonicalPath,pending.ExecutableIdentity);
                    Assert.Null(f.Future.TryCommitStart(pending.AuthorizationId,f.Sid,f.Session,process,now,out _));
                }
                else
                {
                    var start=f.Claim(pending);
                    if(terminal=="started_unknown")f.Future.RecoverUncertainStarts(DateTimeOffset.UtcNow);
                    else Assert.True(f.Future.CompleteRun(pending.AuthorizationId,start.RunId,false,"recording_failed",null,null,null,DateTimeOffset.UtcNow));
                }
                Assert.Equal(terminal=="started_unknown"?"used":terminal,f.Row(pending.AuthorizationId).StatusCode);
            }
            return new(new(f.Store.DatabasePath),f.Root,"future_window:"+pending.AuthorizationId,f.Sid,f.Session,pending.OutputFilePath,
                sql=>f.Corrupt(sql.Replace("PRAGMA foreign_keys=OFF;PRAGMA ignore_check_constraints=ON;","",StringComparison.Ordinal)),f.Dispose,()=>f.Anchor(pending.AuthorizationId),()=>f.FactoryCalls);
        }
        catch(Exception primary){try{f.Dispose();}catch(Exception cleanup){throw new AggregateException(primary,cleanup);}throw;}
    }
}

[Collection("NonParallel-AgentRecorderDataDir")]
public sealed class HistoryArtifactCheckTests(ITestOutputHelper output)
{
    private static Task<HistoryArtifactFixture> Open(string kind,bool completed=true,bool unknown=false)=>kind=="future_window"
        ?FutureWindowPublicationAnchorTests.ArtifactFixture(completed,unknown):PlanMediaPublicationAnchorTests.ArtifactFixture(kind,completed,unknown);
    private static TaskCompletionSource Signal()=>new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static string Snapshot(HistoryArtifactFixture f)=>UnifiedHistoryProfileFilterTests.RawSnapshot(f.Store.DatabasePath);
    private static HistoryArtifactCheckService Checked(HistoryArtifactFixture f,Ancestors a,Leaf l)=>new(new HistoryArtifactFactsReader(f.Store).Read,a,l,_=>true);
    private static void Damage(HistoryArtifactFixture f,string sql)
    {
        var match=System.Text.RegularExpressions.Regex.Match(sql,@"^(?:UPDATE|DELETE FROM)\s+(\w+)");
        if(!match.Success){f.Execute(sql);return;}
        using var c=f.Store.OpenConnection();using var cmd=c.CreateCommand();
        cmd.CommandText="SELECT name,sql FROM sqlite_master WHERE type='trigger' AND tbl_name=$table;";
        cmd.Parameters.AddWithValue("$table",match.Groups[1].Value);
        var triggers=new List<(string Name,string Sql)>();using(var r=cmd.ExecuteReader())while(r.Read())triggers.Add((r.GetString(0),r.GetString(1)));
        cmd.Parameters.Clear();
        cmd.CommandText="PRAGMA foreign_keys=OFF;PRAGMA ignore_check_constraints=ON;"+
            string.Concat(triggers.Select(x=>"DROP TRIGGER "+x.Name+";"))+sql+string.Concat(triggers.Select(x=>x.Sql+";"));
        cmd.ExecuteNonQuery();
    }

    [Theory][InlineData("standing",false)][InlineData("recurring",false)][InlineData("required_once",false)][InlineData("future_window",false)]
    [InlineData("standing",true)][InlineData("recurring",true)][InlineData("required_once",true)][InlineData("future_window",true)]
    public async Task FourFormalSuccessesReopenedUseOriginalSizeAndOptionalNativeIdentity(string kind,bool unknown)
    {
        using var f=await Open(kind,unknown:unknown);var before=Snapshot(f);var bytes=File.ReadAllBytes(f.Store.DatabasePath);
        var trace=new WindowsReadOnlyFileMetadataNativeTests.Trace();var a=new Ancestors{Read=File.GetAttributes};
        var service=new HistoryArtifactCheckService(new HistoryArtifactFactsReader(f.Store).Read,a,new WindowsReadOnlyFileMetadataProbe(trace));
        var starts=f.Starts();
        var result=(await service.ReadAsync(f.EntryId,f.Sid,f.Session))!;
        Assert.Equal("complete",result.ProbeState);Assert.Equal("completed",result.MediaHistoryStatus);
        Assert.Equal("present_size_matches",result.Media.Observation);Assert.Equal(unknown?"unverified":"match",result.Media.IdentityState);
        Assert.Equal(new FileInfo(f.Path).Length,result.Media.RecordedSizeBytes);Assert.Equal(result.Media.RecordedSizeBytes,result.Media.ObservedSizeBytes);
        Assert.Single(trace.Handles);Assert.All(trace.Handles,h=>Assert.True(h.IsClosed));Assert.InRange(a.Calls.Count,1,64);
        Assert.Equal(before,Snapshot(f));Assert.Equal(bytes,File.ReadAllBytes(f.Store.DatabasePath));
        Assert.Equal(1,starts);Assert.Equal(0,f.Starts()-starts);output.WriteLine("Formal approved/committed synthetic input -> original publisher -> terminal transaction -> reopened pure facts -> one actual READ_ATTRIBUTES/ShareRead handle. No capture during GET.");
    }

    [Theory][InlineData("standing")][InlineData("recurring")][InlineData("required_once")][InlineData("future_window")]
    public async Task NonCompletedMissingAndForeignIdentityNeverProbe(string kind)
    {
        using var f=await Open(kind,completed:false);var a=new Ancestors();var l=new Leaf();var s=Checked(f,a,l);
        var result=(await s.ReadAsync(f.EntryId,f.Sid,f.Session))!;
        Assert.Equal("not_checked",result.Media.Observation);Assert.Equal("media_not_applicable",result.Media.ReasonCode);
        Assert.Null(await s.ReadAsync(f.EntryId,"foreign",f.Session));Assert.Null(await s.ReadAsync(f.EntryId,f.Sid,"foreign"));
        var source=HistoryArtifactCheckService.ParseEntryId(f.EntryId).Source;
        Assert.Null(await s.ReadAsync(source+":absent",f.Sid,f.Session));Assert.Empty(a.Calls);Assert.Empty(l.Calls);
    }

    [Theory][InlineData("standing")][InlineData("recurring")][InlineData("required_once")][InlineData("future_window")]
    public async Task ActualSameSizeReplacementMissingChangedLengthLockedAndHardlinkDoNotRewriteHistory(string kind)
    {
        using var f=await Open(kind);var before=Snapshot(f);var anchor=f.Anchor();var data=File.ReadAllBytes(f.Path);
        File.Move(f.Path,f.Path+".original");File.WriteAllBytes(f.Path,data);
        var s=new HistoryArtifactCheckService(f.Store);
        var result=(await s.ReadAsync(f.EntryId,f.Sid,f.Session))!;
        Assert.Equal("present_size_matches",result.Media.Observation);Assert.Equal("mismatch",result.Media.IdentityState);
        Assert.Equal(anchor,f.Anchor());File.Delete(f.Path);
        Assert.Equal("missing",(await s.ReadAsync(f.EntryId,f.Sid,f.Session))!.Media.Observation);
        File.WriteAllBytes(f.Path,new byte[data.Length+1]);
        Assert.Equal("size_mismatch",(await s.ReadAsync(f.EntryId,f.Sid,f.Session))!.Media.Observation);
        using(var locked=new FileStream(f.Path,FileMode.Open,FileAccess.ReadWrite,FileShare.None))
            Assert.Equal("size_mismatch",(await s.ReadAsync(f.EntryId,f.Sid,f.Session))!.Media.Observation);
        Assert.True(CreateHardLink(f.Path+".hardlink",f.Path,IntPtr.Zero));
        Assert.Equal("multiple_links",(await s.ReadAsync(f.EntryId,f.Sid,f.Session))!.Media.ReasonCode);
        Assert.Equal(before,Snapshot(f));output.WriteLine("Actual NTFS replacement/missing/length/exclusive handle/hardlink; historical anchor unchanged.");
    }
    [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true)]
    private static extern bool CreateHardLink(string newName,string existingName,IntPtr security);

    [Theory]
    [InlineData("standing","failed")][InlineData("recurring","failed")][InlineData("required_once","failed")]
    [InlineData("standing","session_interrupted")][InlineData("recurring","session_interrupted")][InlineData("required_once","session_interrupted")]
    [InlineData("future_window","failed")][InlineData("future_window","revoked")][InlineData("future_window","expired")][InlineData("future_window","started_unknown")]
    public async Task FormalFailedRevokedExpiredAndUncertainOutcomesNeverProbe(string kind,string terminal)
    {
        using var f=kind=="future_window"?await FutureWindowPublicationAnchorTests.ArtifactFixture(completed:false,terminal:terminal):
            await PlanMediaPublicationAnchorTests.ArtifactFixture(kind,completed:false,terminal:terminal);
        var before=Snapshot(f);var a=new Ancestors();var l=new Leaf();
        var result=(await Checked(f,a,l).ReadAsync(f.EntryId,f.Sid,f.Session))!;
        Assert.Equal("complete",result.ProbeState);Assert.Equal("not_checked",result.Media.Observation);
        Assert.Equal("media_not_applicable",result.Media.ReasonCode);Assert.Empty(a.Calls);Assert.Empty(l.Calls);Assert.Equal(before,Snapshot(f));
    }

    public static IEnumerable<object[]> PlanDamage()
    {
        foreach(var sql in new[]{
            "DELETE FROM plan_media_publication_anchors;",
            "UPDATE plan_media_publication_anchors SET plan_id='foreign';",
            "UPDATE plan_media_publication_anchors SET occurrence_id='foreign';",
            "UPDATE plan_media_publication_anchors SET owner_id='foreign';",
            "UPDATE plan_media_publication_anchors SET owner_kind='other';",
            "UPDATE plan_media_publication_anchors SET frozen_output_path='C:\\foreign.mp4';",
            "UPDATE plan_media_publication_anchors SET output_path='C:\\foreign.mp4';",
            "UPDATE plan_media_publication_anchors SET output_size_bytes=output_size_bytes+1;",
            "UPDATE plan_media_publication_anchors SET output_size_bytes=1.5;",
            "UPDATE plan_media_publication_anchors SET owner_id=CAST(owner_id AS BLOB);",
            "UPDATE plan_media_publication_anchors SET owner_id=owner_id||char(0)||'tail';",
            "UPDATE plan_media_publication_anchors SET source=source||char(0)||'tail';",
            "UPDATE plan_media_publication_anchors SET volume_id=zeroblob(7);",
            "UPDATE plan_media_publication_anchors SET file_id=zeroblob(16);",
            "UPDATE plan_media_publication_anchors SET observation_version=2;",
            "UPDATE plan_media_publication_anchors SET receipt_size_bytes=receipt_size_bytes+1;",
            "UPDATE plan_media_publication_anchors SET source='foreign';",
            "DROP INDEX idx_plan_media_publication_run;",
            "DROP TRIGGER plan_media_publication_initial;",
            "DELETE FROM schema_migrations WHERE version=41;",
            "UPDATE schema_migrations SET name=name||char(0)||'tail' WHERE version=41;",
            "UPDATE schema_migrations SET definition_checksum=definition_checksum||char(0)||'tail' WHERE version=41;",
            "UPDATE schema_migrations SET name=CAST(name AS BLOB) WHERE version=41;",
            "UPDATE required_once_execution_authorizations SET run_id='foreign';",
            "UPDATE plan_media_publication_boundary SET legacy_run_rowid=999999;",
            "DELETE FROM plan_history_locator;",
            "UPDATE plan_history_locator SET occurrence_sequence=occurrence_sequence+100;",
            "UPDATE plan_history_locator SET plan_id='foreign';",
            "UPDATE plan_history_locator SET current_user_sid='foreign';",
            "UPDATE plan_history_locator SET owner_kind='recurring';",
            "UPDATE required_once_execution_authorizations SET proof_nonce=proof_nonce||char(0)||'tail';",
            "UPDATE required_once_execution_authorizations SET output_path=output_path||char(0)||'tail';",
            "UPDATE required_once_execution_authorizations SET output_size_bytes=CAST(output_size_bytes AS BLOB);",
            "UPDATE required_once_authorized_specs SET creation_approval_id=creation_approval_id||char(0)||'tail';",
            "UPDATE recording_runs SET terminal_reason_code=char(0)||'tail';"}) yield return [sql];
    }
    [Theory][MemberData(nameof(PlanDamage))]
    public async Task PlanModernDamageFailsBeforeAllMediaIO(string sql)
    {
        using var f=await Open("required_once");Damage(f,sql);var before=Snapshot(f);var a=new Ancestors();var l=new Leaf();
        await Assert.ThrowsAnyAsync<Exception>(()=>Checked(f,a,l).ReadAsync(f.EntryId,f.Sid,f.Session));
        Assert.Empty(a.Calls);Assert.Empty(l.Calls);Assert.Equal(before,Snapshot(f));
    }

    public static IEnumerable<object[]> FutureDamage()
    {
        foreach(var sql in new[]{
            "DELETE FROM future_window_publication_anchors;",
            "UPDATE future_window_publication_anchors SET run_id='foreign';",
            "UPDATE future_window_publication_anchors SET output_path='C:\\foreign.mp4';",
            "UPDATE future_window_publication_anchors SET output_size_bytes=output_size_bytes+1;",
            "UPDATE future_window_publication_anchors SET output_size_bytes=1.5;",
            "UPDATE future_window_publication_anchors SET run_id=run_id||char(0)||'tail';",
            "UPDATE future_window_publication_anchors SET output_path=output_path||char(0)||'tail';",
            "UPDATE future_window_publication_anchors SET source=source||char(0)||'tail';",
            "UPDATE future_window_publication_anchors SET observation_version=2;",
            "UPDATE future_window_publication_anchors SET source='foreign';",
            "UPDATE future_window_publication_anchors SET volume_id=zeroblob(7);",
            "UPDATE future_window_publication_anchors SET file_id=zeroblob(16);",
            "DROP INDEX idx_future_window_publication_authorization;",
            "DROP TRIGGER future_window_publication_initial;",
            "DELETE FROM schema_migrations WHERE version=40;",
            "UPDATE schema_migrations SET name=name||char(0)||'tail' WHERE version=40;",
            "UPDATE schema_migrations SET definition_checksum=definition_checksum||char(0)||'tail' WHERE version=40;",
            "UPDATE future_window_authorizations SET run_id='foreign';",
            "UPDATE future_window_publication_boundary SET legacy_authorization_rowid=999999;",
            "UPDATE future_window_authorizations SET output_path=output_path||char(0)||'tail';",
            "UPDATE future_window_authorizations SET output_size_bytes=CAST(output_size_bytes AS BLOB);",
            "UPDATE future_window_authorizations SET proof_nonce=proof_nonce||char(0)||'tail';",
            "UPDATE future_window_authorizations SET output_file_name=output_file_name||char(0)||'tail';"})yield return [sql];
    }
    [Theory][MemberData(nameof(FutureDamage))]
    public async Task FutureModernDamageFailsBeforeAllMediaIO(string sql)
    {
        using var f=await Open("future_window");Damage(f,sql);var before=Snapshot(f);var a=new Ancestors();var l=new Leaf();
        await Assert.ThrowsAnyAsync<Exception>(()=>Checked(f,a,l).ReadAsync(f.EntryId,f.Sid,f.Session));
        Assert.Empty(a.Calls);Assert.Empty(l.Calls);Assert.Equal(before,Snapshot(f));
    }

    [Theory][InlineData("standing")][InlineData("recurring")][InlineData("required_once")][InlineData("future_window")]
    public async Task LegacyKnownSizeIsObservedButUnknownSizeIsNeverInvented(string kind)
    {
        using var f=await Open(kind);var plan=kind!="future_window";
        Damage(f,plan?"DELETE FROM plan_media_publication_anchors;UPDATE plan_media_publication_boundary SET legacy_run_rowid=(SELECT max(rowid) FROM recording_runs);":
            "DELETE FROM future_window_publication_anchors;UPDATE future_window_publication_boundary SET legacy_authorization_rowid=(SELECT max(rowid) FROM future_window_authorizations);");
        var a=new Ancestors{Read=File.GetAttributes};var l=new Leaf{Read=WindowsReadOnlyFileMetadataProbe.Instance.Observe};
        var result=(await Checked(f,a,l).ReadAsync(f.EntryId,f.Sid,f.Session))!;
        if(kind is "standing" or "recurring")
        {Assert.Equal("unknown",result.Media.Observation);Assert.Null(result.Media.RecordedSizeBytes);Assert.Empty(a.Calls);Assert.Empty(l.Calls);}
        else {Assert.Equal("present_size_matches",result.Media.Observation);Assert.Equal("unverified",result.Media.IdentityState);Assert.Single(l.Calls);}
    }

    [Theory][InlineData("standing")][InlineData("recurring")][InlineData("required_once")][InlineData("future_window")]
    public async Task IndexedExactLookupAndWalFreezeOriginalFactsInOneReadSnapshot(string kind)
    {
        using var f=await Open(kind);var changed=false;var commands=0;var traces=new List<string>();
        var facts=new HistoryArtifactFactsReader(f.Store){SnapshotEstablishedForTests=()=>
        {
            if(changed)return;changed=true;
            Damage(f,kind=="future_window"?"UPDATE future_window_publication_anchors SET source='foreign';":"UPDATE plan_media_publication_anchors SET source='foreign';");
        },CommandForTests=(c,t,sql,args)=>
        {
            commands++;using var cmd=c.CreateCommand();cmd.Transaction=t;cmd.CommandText="EXPLAIN QUERY PLAN "+sql;
            foreach(var p in args)cmd.Parameters.AddWithValue(p.Key,p.Value);using var r=cmd.ExecuteReader();
            while(r.Read())traces.Add(r.GetString(3));
        }};
        var a=new Ancestors{Read=File.GetAttributes};var l=new Leaf{Read=WindowsReadOnlyFileMetadataProbe.Instance.Observe};
        var s=new HistoryArtifactCheckService(facts.Read,a,l);
        Assert.Equal("match",(await s.ReadAsync(f.EntryId,f.Sid,f.Session))!.Media.IdentityState);
        var count=l.Calls.Count;
        await Assert.ThrowsAnyAsync<Exception>(()=>s.ReadAsync(f.EntryId,f.Sid,f.Session));Assert.Equal(count,l.Calls.Count);
        Assert.Contains(traces,x=>x.Contains(kind=="future_window"?"idx_future_window_publication":"idx_plan_media_publication_run"));
        Assert.DoesNotContain(traces,x=>x.StartsWith("SCAN ") && !x.Contains("CONSTANT") && !x.Contains("requested"));
        Assert.InRange(commands,4,80);output.WriteLine(string.Join("\n",traces.Distinct()));
    }

    [Theory][InlineData("C:\\x.mp4:stream","path_invalid")][InlineData("\\\\server\\share\\x.mp4","path_invalid")]
    [InlineData("\\\\?\\C:\\x.mp4","path_invalid")][InlineData("C:\\NUL.mp4","path_invalid")]
    [InlineData("C:\\..\\x.mp4","path_invalid")][InlineData("C:/x.mp4","path_invalid")]
    [InlineData("C:\\x.\\a.mp4","path_invalid")][InlineData("ancestor_limit","ancestor_check_limit")]
    [InlineData("drive","local_drive_unavailable")][InlineData("ancestor_reparse","ancestor_reparse_point")]
    [InlineData("leaf_reparse","leaf_reparse_point")][InlineData("unstable","metadata_changed")]
    [InlineData("links","multiple_links")][InlineData("path_mismatch","resolved_path_mismatch")]
    public async Task SharedSamplerRetainsLexicalAncestryAndNativeBoundaries(string scenario,string expected)
    {
        var path=scenario.Contains(':')||scenario.StartsWith('\\')?scenario:@"C:\owned\x.mp4";
        if(scenario=="ancestor_limit")path="C:\\"+string.Join("\\",Enumerable.Repeat("a",64))+"\\x.mp4";
        var a=new Ancestors();var l=new Leaf{Read=p=>
        {
            if(scenario=="leaf_reparse")return new(FileObservationOutcome.ReparsePoint);
            var o=Sample(p);return scenario switch {
                "unstable"=>o with {After=o.After! with {Length=13}},
                "links"=>o with {Before=o.Before! with {Links=2},After=o.After! with {Links=2}},
                "path_mismatch"=>o with {AfterPath=@"C:\other.mp4"},_=>o};
        }};
        if(scenario=="ancestor_reparse")a.Read=_=>FileAttributes.Directory|FileAttributes.ReparsePoint;
        var s=new HistoryArtifactCheckService((id,_,_)=>new(id,"plan","completed",path,12,null),a,l,_=>scenario!="drive");
        Assert.Equal(expected,(await s.ReadAsync("plan:entry","sid","session"))!.Media.ReasonCode);
        Assert.InRange(a.Calls.Count,0,64);Assert.InRange(l.Calls.Count,0,1);
        output.WriteLine("Internal boundary injection; actual path/handle cases covered separately. No native creation claimed.");
    }

    [Theory][InlineData(true,false)][InlineData(false,false)][InlineData(true,true)][InlineData(false,true)]
    public async Task OrdinaryAndNewShareActualWorkerThroughTimeoutOrCancellation(bool newFirst,bool cancel)
    {
        using var f=await Open("standing");using var release=new ManualResetEventSlim();var entered=Signal();var exited=Signal();var deadline=Signal();
        var leaf=new Leaf{Read=p=>{entered.TrySetResult();release.Wait();return Sample(p);}};var reads=0;
        var reader=new HistoryArtifactFactsReader(f.Store);
        var s=new HistoryArtifactCheckService((id,sid,session)=>{reads++;return reader.Read(id,sid,session);},new Ancestors(),leaf,_=>true)
            {DeadlineForTests=_=>deadline.Task,WorkerExitedForTests=()=>exited.TrySetResult()};
        using var db=new UnifiedHistoryTestDatabase(System.IO.Path.Combine(f.Root,"ordinary"));
        db.Ordinary.Register(db.Recording("ordinary",completed:true));
        var ordinary=new OrdinaryArtifactCheckService(id=>{reads++;return db.Ordinary.ReadArtifactFacts(id);},new Ancestors(),leaf,_=>true)
            {DeadlineForTests=_=>deadline.Task,WorkerExitedForTests=()=>exited.TrySetResult()};
        using var cancellation=new CancellationTokenSource();
        Task<HistoryArtifactCheckResult?>? newWork=null;Task<OrdinaryArtifactCheckResult?>? oldWork=null;
        if(newFirst)newWork=s.ReadAsync(f.EntryId,f.Sid,f.Session,cancellation.Token);else oldWork=ordinary.ReadAsync("ordinary",cancellation.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        try
        {
            if(cancel)cancellation.Cancel();deadline.TrySetResult();
            object result=newFirst?(await newWork!)!:(await oldWork!)!;
            Assert.Equal(cancel?"cancelled":"timeout",newFirst?((HistoryArtifactCheckResult)result).ProbeState:((OrdinaryArtifactCheckResult)result).ProbeState);
            var frozen=JsonSerializer.Serialize(result);var beforeReads=reads;
            Assert.Equal("busy",(await s.ReadAsync(f.EntryId,f.Sid,f.Session))!.ProbeState);
            Assert.Equal("busy",(await ordinary.ReadAsync("ordinary"))!.ProbeState);Assert.Equal(beforeReads,reads);Assert.Single(leaf.Calls);
            release.Set();await exited.Task.WaitAsync(TimeSpan.FromSeconds(5));Assert.Equal(frozen,JsonSerializer.Serialize(result));
        }
        finally {release.Set();await exited.Task.WaitAsync(TimeSpan.FromSeconds(5));}
        Assert.Equal("complete",(await new HistoryArtifactCheckService(f.Store).ReadAsync(f.EntryId,f.Sid,f.Session))!.ProbeState);
    }

    [Theory][InlineData("standing")][InlineData("recurring")][InlineData("required_once")][InlineData("future_window")]
    public async Task OrphanPrivateEvidenceOnUnfinishedRowsFailsBeforeMetadata(string kind)
    {
        using var f=await Open(kind,completed:false);
        Damage(f,kind=="future_window"?"UPDATE future_window_publication_anchors SET output_path='C:\\orphan.mp4';":
            "UPDATE plan_media_publication_anchors SET output_path='C:\\orphan.mp4';");
        var a=new Ancestors();var l=new Leaf();await Assert.ThrowsAnyAsync<Exception>(()=>Checked(f,a,l).ReadAsync(f.EntryId,f.Sid,f.Session));
        Assert.Empty(a.Calls);Assert.Empty(l.Calls);
    }

    [Theory][InlineData("standing")][InlineData("recurring")][InlineData("required_once")][InlineData("future_window")]
    public async Task EncodedCanonicalIdAndOriginalIdentityIsolationUseRealHttp(string kind)
    {
        using var f=await Open(kind);var a=new Ancestors();var l=new Leaf();
        using(var foreign=new Host(f,true,new SqliteHistoryArtifactCheckGateway(Checked(f,a,l),()=> (f.Sid,"foreign"))))
        using(var c=foreign.Client())using(var r=await c.GetAsync(Endpoint(Uri.EscapeDataString(f.EntryId))))
            Assert.Equal(HttpStatusCode.NotFound,r.StatusCode);
        Assert.Empty(a.Calls);Assert.Empty(l.Calls);
        using var host=new Host(f,true,new SqliteHistoryArtifactCheckGateway(new(f.Store),()=> (f.Sid,f.Session)));
        using var client=host.Client();using var response=await client.GetAsync(Endpoint(Uri.EscapeDataString(f.EntryId)));
        Assert.Equal(HttpStatusCode.OK,response.StatusCode);
        using var doc=JsonDocument.Parse(await response.Content.ReadAsStringAsync());Assert.Equal(f.EntryId,doc.RootElement.GetProperty("data").GetProperty("entry_id").GetString());
        using var doubleEncoded=await client.GetAsync(Endpoint(Uri.EscapeDataString(Uri.EscapeDataString(f.EntryId))));
        Assert.Equal(HttpStatusCode.BadRequest,doubleEncoded.StatusCode);
    }

    [Fact]
    public async Task DispatchDatabaseFaultAndPrecancelReleaseSharedSlotWithoutMediaIO()
    {
        using var f=await Open("standing");var a=new Ancestors();var l=new Leaf();var reads=0;
        var s=new HistoryArtifactCheckService((_,_,_)=>{reads++;throw new InvalidDataException("owned_db_fault");},a,l);
        using(var cancel=new CancellationTokenSource())
        {
            cancel.Cancel();Assert.Equal("cancelled",(await s.ReadAsync(f.EntryId,f.Sid,f.Session,cancel.Token))!.ProbeState);Assert.Equal(0,reads);
        }
        await Assert.ThrowsAsync<InvalidDataException>(()=>s.ReadAsync(f.EntryId,f.Sid,f.Session));
        s.DeadlineForTests=_=>throw new IOException("owned_dispatch_fault");
        Assert.Equal("failed",(await s.ReadAsync(f.EntryId,f.Sid,f.Session))!.ProbeState);Assert.Equal(1,reads);
        Assert.Empty(a.Calls);Assert.Empty(l.Calls);
        Assert.Equal("complete",(await new HistoryArtifactCheckService(f.Store).ReadAsync(f.EntryId,f.Sid,f.Session))!.ProbeState);
        var prior=RecordingPreflightChecker.StorageCapacityProvider;
        Assert.Throws<IOException>(()=>FutureWindowPublicationAnchorTests.ArtifactConstructionFault());
        Assert.Same(prior,RecordingPreflightChecker.StorageCapacityProvider);
    }

    private sealed class Tray(bool headless) : ITrayContext
    {
        public string HostMode=>headless?"headless":"tray";public bool SupportsRegionSelectionUi=>false;
        public void RequestConfirmation(RecordingConfirmationPresentation p,Action<ConfirmationDecision> c)=>throw new InvalidOperationException("No UI");
        public void RequestRegionSelection(int t,Action<string,int,int,int,int,string,string> c)=>throw new InvalidOperationException("No UI");
        public void SetRecording(RecordingUiPresentation p)=>throw new InvalidOperationException("Capture forbidden");
        public void SetIdle(RecordingUiPresentation p){}public void SetAllIdle(){}public void ShowError(string s)=>throw new InvalidOperationException("No UI");
    }
    private sealed class Host : IDisposable
    {
        private readonly string? prior=Environment.GetEnvironmentVariable("AGENT_RECORDER_DATA_DIR");
        private RecordingEngine? engine;internal ApiServer Api=null!;
        internal Host(HistoryArtifactFixture f,bool headless,IHistoryArtifactCheckGateway? gateway)
        {
            try
            {
                Environment.SetEnvironmentVariable("AGENT_RECORDER_DATA_DIR",f.Root);ApiKeyAuth.InitializeForTesting(f.Root);
                var audit=new RetentionSharedOutputPathTests.QuietAudit();engine=new(audit);engine.SetTray(new Tray(headless));
                engine.BackendFactory=_=>throw new InvalidOperationException("GET cannot capture");
                Api=new(engine,audit,new Tray(headless),null,null,null,null,null,null,null,null,null,0,historyArtifactCheckGateway:gateway);Api.Start();
            }
            catch(Exception primary){try{Dispose();}catch(Exception cleanup){throw new AggregateException(primary,cleanup);}throw;}
        }
        internal HttpClient Client(bool auth=true)
        {var c=new HttpClient(new HttpClientHandler{UseProxy=false}){BaseAddress=new($"http://127.0.0.1:{Api.BoundPort}")};if(auth)c.DefaultRequestHeaders.Add("X-Agent-Recorder-Key",ApiKeyAuth.CurrentApiKey);return c;}
        public void Dispose()
        {
            var errors=new List<Exception>();void Clean(Action a){try{a();}catch(Exception e){errors.Add(e);}}
            Clean(()=>Api?.Stop());Clean(()=>engine?.Dispose());Clean(()=>ApiKeyAuth.ResetForTesting(null));
            Clean(()=>Environment.SetEnvironmentVariable("AGENT_RECORDER_DATA_DIR",prior));if(errors.Count>0)throw new AggregateException(errors);
        }
    }
    private static string Endpoint(string id)=>"/api/v1/history/"+id+"/artifacts/check";
    [Theory][InlineData(false)][InlineData(true)]
    public async Task RealHttpAuthArgumentsDtoCapabilitiesMissingAndDamage(bool headless)
    {
        using var f=await Open("standing");var reads=0;var a=new Ancestors{Read=File.GetAttributes};var l=new Leaf{Read=WindowsReadOnlyFileMetadataProbe.Instance.Observe};
        var facts=new HistoryArtifactFactsReader(f.Store);var service=new HistoryArtifactCheckService((id,sid,session)=>{reads++;return facts.Read(id,sid,session);},a,l);
        using var host=new Host(f,headless,new SqliteHistoryArtifactCheckGateway(service,()=> (f.Sid,f.Session)));
        using var denied=host.Client(false);using(var r=await denied.GetAsync(Endpoint(f.EntryId)))Assert.Equal(HttpStatusCode.Unauthorized,r.StatusCode);
        denied.DefaultRequestHeaders.Add("X-Agent-Recorder-Key","wrong");using(var r=await denied.GetAsync(Endpoint("ordinary:any")))Assert.Equal(HttpStatusCode.Unauthorized,r.StatusCode);
        using var client=host.Client();
        foreach(var id in new[]{"ordinary:r","other:r","plan:","plan:bad%2Fid","plan:bad%5Cid","plan:bad%3Aid","plan:bad%252Fid","plan:bad%00tail","plan:"+new string('a',129)})
        {using var r=await client.GetAsync(Endpoint(id));Assert.Equal(HttpStatusCode.BadRequest,r.StatusCode);}
        using(var r=await client.GetAsync(Endpoint(f.EntryId)+"?sid=foreign"))Assert.Equal(HttpStatusCode.BadRequest,r.StatusCode);
        using(var req=new HttpRequestMessage(HttpMethod.Get,Endpoint(f.EntryId)){Content=new StringContent("{}")})
        using(var r=await client.SendAsync(req))Assert.Equal(HttpStatusCode.BadRequest,r.StatusCode);
        Assert.Equal(0,reads);Assert.Empty(a.Calls);Assert.Empty(l.Calls);
        using(var r=await client.GetAsync(Endpoint("plan:absent")))Assert.Equal(HttpStatusCode.NotFound,r.StatusCode);
        using(var r=await client.GetAsync(Endpoint(f.EntryId.Replace(":","%3A"))))
        {
            Assert.Equal(HttpStatusCode.OK,r.StatusCode);var text=await r.Content.ReadAsStringAsync();using var doc=JsonDocument.Parse(text);var data=doc.RootElement.GetProperty("data");
            Assert.Equal(f.EntryId,data.GetProperty("entry_id").GetString());Assert.Equal("plan",data.GetProperty("source").GetString());
            Assert.Equal("main_media",data.GetProperty("scope").GetString());Assert.Equal("match",data.GetProperty("media").GetProperty("identity_state").GetString());
            foreach(var flag in new[]{"read_only","metadata_only"})Assert.True(data.GetProperty(flag).GetBoolean());
            foreach(var flag in new[]{"content_verification","bundle_included","deletion_supported"})Assert.False(data.GetProperty(flag).GetBoolean());
            foreach(var secret in new[]{f.Root,"volume_id","file_id","proof","nonce","output_path","receipt"})Assert.DoesNotContain(secret,text);
        }
        using(var r=await client.GetAsync("/api/v1/capabilities"))
        {
            using var doc=JsonDocument.Parse(await r.Content.ReadAsStringAsync());var cap=doc.RootElement.GetProperty("data").GetProperty("history_artifact_check");
            Assert.True(cap.GetProperty("supported").GetBoolean());Assert.Equal(new[]{"plan","future_window"},cap.GetProperty("supported_sources").EnumerateArray().Select(x=>x.GetString()));
            Assert.Equal(1,cap.GetProperty("max_files").GetInt32());Assert.Equal(3000,cap.GetProperty("deadline_ms").GetInt32());
        }
        Damage(f,"DELETE FROM plan_media_publication_anchors;");var probes=l.Calls.Count;
        using(var r=await client.GetAsync(Endpoint(f.EntryId)))
        {Assert.Equal(HttpStatusCode.ServiceUnavailable,r.StatusCode);var text=await r.Content.ReadAsStringAsync();Assert.Contains("HISTORY_UNAVAILABLE",text);Assert.DoesNotContain("SQLite",text);Assert.DoesNotContain(f.Root,text);}
        Assert.Equal(probes,l.Calls.Count);
    }

    [Theory][InlineData(false)][InlineData(true)]
    public async Task MissingGatewayAdvertisesNoSourcesAndSanitized503(bool headless)
    {
        using var f=await Open("future_window",completed:false);using var host=new Host(f,headless,null);using var client=host.Client();
        using(var r=await client.GetAsync(Endpoint(f.EntryId)))Assert.Equal(HttpStatusCode.ServiceUnavailable,r.StatusCode);
        using var response=await client.GetAsync("/api/v1/capabilities");using var doc=JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var cap=doc.RootElement.GetProperty("data").GetProperty("history_artifact_check");Assert.False(cap.GetProperty("supported").GetBoolean());Assert.Empty(cap.GetProperty("supported_sources").EnumerateArray());
    }

    private sealed class ObservedGateway(HistoryArtifactCheckService service,HistoryArtifactFixture f) : IHistoryArtifactCheckGateway
    {
        public IReadOnlyList<string> SupportedSources=>new[]{"plan","future_window"};
        internal readonly TaskCompletionSource Returned=Signal();internal HistoryArtifactCheckResult? Result;
        public async Task<HistoryArtifactCheckResult?> ReadAsync(string id,CancellationToken cancellation=default)
        {try{return Result=await service.ReadAsync(id,f.Sid,f.Session,cancellation);}finally{Returned.TrySetResult();}}
    }
    [Theory][InlineData(false,false)][InlineData(true,false)][InlineData(false,true)][InlineData(true,true)]
    public async Task RealHttpResetOrHostStopCancelsWithoutRetiringBlockedNativeWorker(bool headless,bool stop)
    {
        using var f=await Open("standing");using var release=new ManualResetEventSlim();var entered=Signal();var exited=Signal();
        var leaf=new Leaf{Read=p=>{entered.TrySetResult();release.Wait();return Sample(p);}};
        var service=Checked(f,new Ancestors(),leaf);service.WorkerExitedForTests=()=>exited.TrySetResult();
        var gateway=new ObservedGateway(service,f);using var host=new Host(f,headless,gateway);using var raw=new TcpClient();
        await raw.ConnectAsync(IPAddress.Loopback,host.Api.BoundPort);var stream=raw.GetStream();
        await stream.WriteAsync(Encoding.ASCII.GetBytes($"GET {Endpoint(f.EntryId)} HTTP/1.1\r\nHost: localhost\r\nX-Agent-Recorder-Key: {ApiKeyAuth.CurrentApiKey}\r\n\r\n"));
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        try
        {
            if(stop)host.Api.Stop();else {raw.Client.LingerState=new(true,0);raw.Client.Close(0);}
            await gateway.Returned.Task.WaitAsync(TimeSpan.FromSeconds(5));Assert.Equal("cancelled",gateway.Result!.ProbeState);
            Assert.Equal("busy",(await new OrdinaryArtifactCheckService(_=>throw new InvalidOperationException("Busy DB access"),new Ancestors(),new Leaf()).ReadAsync("ordinary"))!.ProbeState);
            Assert.Single(leaf.Calls);var frozen=JsonSerializer.Serialize(gateway.Result);
            release.Set();await exited.Task.WaitAsync(TimeSpan.FromSeconds(5));Assert.Equal(frozen,JsonSerializer.Serialize(gateway.Result));
        }
        finally {release.Set();await exited.Task.WaitAsync(TimeSpan.FromSeconds(5));}
    }
}
