using System.Text.Json;
using AgentRecorder.Core;
using AgentRecorder.Infrastructure;
using AgentRecorder.Persistence;
using Microsoft.Data.Sqlite;
using Xunit;
using Xunit.Abstractions;
using static AgentRecorder.Tests.UnifiedHistoryTestDatabase;

namespace AgentRecorder.Tests;

public sealed class RetentionBundleNamespace386Tests(ITestOutputHelper output)
{
    internal static readonly string[] Tables = { "ordinary_recording_bundles", "ordinary_bundle_contents", "ordinary_bundle_identity", "ordinary_media_hash", "ordinary_bundle_attachment_hash" };
    public static IEnumerable<object[]> Sources => Tables.Select(t=>new object[] {t});
    public static IEnumerable<object[]> Descendants => Tables.SelectMany(t=>new[] {new object[] {t,false},new object[] {t,true}});
    internal static void Insert(UnifiedHistoryTestDatabase db,string table,string directory,PublicationIdentityAnchor anchor,string owner="SECRET_FOREIGN")
    {
        // Orphan/bad business fixture: the only matching fact is bundle_path.
        RetentionBundle385Tests.Insert(db,table+":path","C:\\SECRET\\unrelated.mp4",anchor,owner);
        db.Corrupt($"UPDATE {table} SET bundle_path=$path WHERE recording_id=$owner;",("$path",directory),("$owner",owner));
    }
    [Theory, MemberData(nameof(Descendants))]
    public void IsolatedDirectAndDeepDescendantProtectsBeforeFileProbe(string table,bool deep)
    {
        using var f=new RetentionPreviewQueryTests.Fixture();var directory=Path.Combine(f.Videos,"saved.bundle");
        var row=f.Seed("candidate",path:Path.Combine(directory,deep?"sub\\deeper\\inside.mp4":"inside.mp4"));
        Insert(f.Db,table,directory,row.PublicationReceipt!.Anchor);
        var page=f.Page();Assert.Equal("shared_output_path",Assert.Single(page.Items).ReasonCode);
        RetentionRuntimeOutputTests.NoProbes(f);Assert.DoesNotContain("SECRET",JsonSerializer.Serialize(page));
    }
    [Theory, MemberData(nameof(Sources))]
    public async Task GuardAndApprovedDeletionRecheckNewDirectory(string table)
    {
        using(var f=new RetentionPreviewQueryTests.Fixture())
        {
            var row=f.Seed("candidate");Assert.Equal("age_rule_match",Assert.Single(f.Page().Items).ReasonCode);
            Insert(f.Db,table,Path.GetDirectoryName(row.OutputPath)!,row.PublicationReceipt!.Anchor);
            using var guard=new RetentionReferenceGuard(f.Db.Store,f.Db.Ordinary,Sid,Session,new());var calls=0;
            Assert.Equal(RetentionGuardReason.SharedPathProtected,await RetentionGuardFixture.Finish(guard.Execute(row.RecordingId,row.PublicationReceipt.Anchor,f.Before,_=>{calls++;return RetentionGuardContinuationResult.Completed;})));
            Assert.Equal(0,calls);
        }
        using var local=new LocalRetentionDeletionFixture();using var service=local.Service();
        var pending=service.Request(local.Row.RecordingId);var proof=await local.Approve(service,pending);
        Insert(local.Db,table,Path.GetDirectoryName(local.Media)!,local.Anchor);
        var result=await LocalRetentionDeletionFixture.Finish(service.RequestDeletion(proof,local.Row.RecordingId),pending);
        Assert.Equal(LocalDeletionProgress.NotAttempted,result.Progress);Assert.Equal(0,local.Native.Calls);Assert.Equal(local.Bytes,File.ReadAllBytes(local.Media));
    }
    [Theory, MemberData(nameof(Sources))]
    public void CaseTrailingSeparatorRootAndExactDirectory(string table)
    {
        foreach(var kind in new[] {"case","tail","root","self","self_tail"})
        {
            using var f=new RetentionPreviewQueryTests.Fixture();var row=f.Seed("candidate");
            var directory=kind switch {"case"=>Path.GetDirectoryName(row.OutputPath)!.ToUpperInvariant(),"tail"=>Path.GetDirectoryName(row.OutputPath)+"\\","root"=>Path.GetPathRoot(row.OutputPath)!,"self_tail"=>row.OutputPath+"\\",_=>row.OutputPath};
            Insert(f.Db,table,directory,row.PublicationReceipt!.Anchor);
            Assert.Equal("shared_output_path",Assert.Single(f.Page().Items).ReasonCode);RetentionRuntimeOutputTests.NoProbes(f);
        }
    }
    [Theory, MemberData(nameof(Sources))]
    public void AdjacentPrefixOtherDriveAndUnrelatedDoNotMatch(string table)
    {
        foreach(var kind in new[] {"prefix","drive","other","relative_root","damaged"})
        {
            using var f=new RetentionPreviewQueryTests.Fixture();var root=Path.Combine(f.Videos,"a.bundle");
            var row=f.Seed("candidate",path:Path.Combine(root+"2","inside.mp4"));
            var directory=kind switch {"prefix"=>root,"drive"=>(row.OutputPath[0]=='C'?"D":"C")+row.OutputPath[1..Path.GetDirectoryName(row.OutputPath)!.Length],"other"=>Path.Combine(f.Videos,"other"),"relative_root"=>Path.GetPathRoot(row.OutputPath)![..2],_=>root+"\0"};
            Insert(f.Db,table,directory,row.PublicationReceipt!.Anchor);
            Assert.Equal("age_rule_match",Assert.Single(f.Page().Items).ReasonCode);
        }
    }
    [Theory, MemberData(nameof(Sources))]
    public void PageWatermarkDoesNotHideNewOrphanScope(string table)
    {
        using var f=new RetentionPreviewQueryTests.Fixture();f.Seed("a");var b=f.Seed("b");var first=f.Page(1);Assert.NotNull(first.NextCursor);
        Insert(f.Db,table,Path.GetDirectoryName(b.OutputPath)!,b.PublicationReceipt!.Anchor);
        Assert.Equal("shared_output_path",Assert.Single(f.Page(1,first.NextCursor).Items).ReasonCode);
    }
    [Theory, MemberData(nameof(Sources))]
    public void SameIdDirectoryRoleIsNeverExcluded(string table)
    {
        using var f=new RetentionPreviewQueryTests.Fixture();var row=f.Seed("candidate");
        f.Db.Corrupt($"DELETE FROM {table} WHERE recording_id='candidate';");
        Insert(f.Db,table,Path.GetDirectoryName(row.OutputPath)!,row.PublicationReceipt!.Anchor,"candidate");
        using var c=f.Db.Store.OpenReadOnlyConnection();using var t=c.BeginTransaction();
        Assert.True(BundleNamespaceReferenceStorage.Read(c,t,new Dictionary<string,string?> { [row.RecordingId]=row.OutputPath })[row.RecordingId]);
        // These deliberately invalid own business facts may also make public
        // history unavailable; no normal Bundle publication is claimed here.
    }
    [Fact]
    public void HundredWorstDepthCandidatesCoverEveryKeyInBoundedBatches()
    {
        using var f=new RetentionPreviewQueryTests.Fixture();var row=f.Seed("seed");
        var path="C:\\"+string.Join("\\",Enumerable.Range(0,31).Select(i=>"d"+i))+"\\out.mp4";
        Insert(f.Db,Tables[0],"C:\\d0\\d1\\d2",row.PublicationReceipt!.Anchor);
        var candidates=Enumerable.Range(0,100).ToDictionary(i=>"id"+i,_=>(string?)path,StringComparer.Ordinal);
        using var c=f.Db.Store.OpenReadOnlyConnection();using var t=c.BeginTransaction();
        var calls=0;var keys=new HashSet<(string,string)>();var parameterCount=0;
        var flags=BundleNamespaceReferenceStorage.Read(c,t,candidates,(connection,transaction,sql,args)=>
        {
            calls++;Assert.Same(c,connection);Assert.Same(t,transaction);Assert.InRange(args.Count,2,512);parameterCount+=args.Count;
            Assert.DoesNotContain("JOIN",sql);Assert.DoesNotContain("LIKE",sql);Assert.DoesNotContain("recording_id<>",sql);
            for(var i=0;i<args.Count;i+=2)Assert.True(keys.Add(((string)args[i].Value,(string)args[i+1].Value)));
            if(calls is 1 or 26)
            {
                using var cmd=c.CreateCommand();cmd.Transaction=t;cmd.CommandText="EXPLAIN QUERY PLAN "+sql;
                foreach(var p in args)cmd.Parameters.AddWithValue(p.Key,p.Value);
                var plan=new List<string>();using var rd=cmd.ExecuteReader();while(rd.Read())plan.Add(rd.GetString(3));
                foreach(var index in SqliteSchemaV47.Indexes)Assert.Contains(plan,p=>p.Contains("SEARCH scope USING COVERING INDEX "+index.Name)&&p.Contains("bundle_path=?"));
                Assert.DoesNotContain(plan,p=>p.Contains("SCAN scope"));output.WriteLine(string.Join("\n",plan));
            }
        });
        Assert.Equal(26,calls);Assert.Equal(6500,keys.Count);Assert.Equal(13000,parameterCount);
        foreach(var candidate in candidates)foreach(var key in BundleNamespaceReferenceStorage.AncestorKeys(path)!)Assert.Contains((candidate.Key,key),keys);
        Assert.All(flags.Values,v=>Assert.True(v));RetentionRuntimeOutputTests.NoProbes(f);
    }
    [Fact]
    public void OnlyLastKeyMatchAndLateBadResultsCannotYieldPartialSuccess()
    {
        using var f=new RetentionPreviewQueryTests.Fixture();var row=f.Seed("seed");
        var prefix="C:\\"+string.Join("\\",Enumerable.Range(0,31).Select(i=>"d"+i))+"\\";
        var candidates=Enumerable.Range(0,100).ToDictionary(i=>"id"+i,i=>(string?)(prefix+i+".mp4"),StringComparer.Ordinal);
        Insert(f.Db,Tables[4],candidates["id99"]+"\\",row.PublicationReceipt!.Anchor);
        using var c=f.Db.Store.OpenReadOnlyConnection();using var t=c.BeginTransaction();
        var flags=BundleNamespaceReferenceStorage.Read(c,t,candidates);
        Assert.Single(flags,p=>p.Value);Assert.True(flags["id99"]);
        var batches=0;
        Assert.Throws<InvalidDataException>(()=>BundleNamespaceReferenceStorage.Read(c,t,candidates,
            transform:rows=>++batches==26 ? Array.Empty<(object,object)>() : rows));
        Assert.Equal(26,batches);RetentionRuntimeOutputTests.NoProbes(f);
    }
    [Theory]
    [InlineData("missing")][InlineData("duplicate")][InlineData("foreign")][InlineData("flag")][InlineData("typed")][InlineData("throw")]
    public void BrokenProviderFailsClosedBeforeFilesystem(string fault)
    {
        using var f=new RetentionPreviewQueryTests.Fixture();f.Seed("a");f.Seed("b");
        f.Db.Query.RetentionNamespaceResultsForTests=rows=>fault switch
        {
            "missing"=>Array.Empty<(object,object)>(),"duplicate"=>new[] {rows[0],rows[0]},
            "foreign"=>new[] {((object)"SECRET",(object)0L),rows[1]},
            "flag"=>new[] {(rows[0].CandidateId,(object)2L),rows[1]},
            "typed"=>new[] {(rows[0].CandidateId,(object)"0"),rows[1]},_=>throw new IOException("SECRET")
        };
        Assert.Throws<RetentionPreviewUnavailableException>(()=>f.Page());RetentionRuntimeOutputTests.NoProbes(f);
    }
    [Fact]
    public void LexicalLimitsAreCompleteOrConservativeAndPreserveEarlyReasons()
    {
        var path="C:\\"+string.Join("\\",Enumerable.Repeat("x",31))+"\\out.mp4";
        Assert.Equal(65,BundleNamespaceReferenceStorage.AncestorKeys(path)!.Count);
        Assert.Null(BundleNamespaceReferenceStorage.AncestorKeys("C:\\x\\"+path[3..]));
        Assert.Equal(new[] {"C:\\"},BundleNamespaceReferenceStorage.AncestorKeys("C:\\"));
        Assert.Null(BundleNamespaceReferenceStorage.AncestorKeys("C:out.mp4"));
        Assert.Null(BundleNamespaceReferenceStorage.AncestorKeys("C:\\x\\..\\out.mp4"));
        Assert.Null(BundleNamespaceReferenceStorage.AncestorKeys("C:\\CON\\out.mp4"));
        var longest="C:\\"+new string('x',1013)+"\\out.mp4";Assert.Equal(1024,longest.Length);
        Assert.NotNull(BundleNamespaceReferenceStorage.AncestorKeys(longest));
        Assert.Null(BundleNamespaceReferenceStorage.AncestorKeys(longest+"x"));
        using var f=new RetentionPreviewQueryTests.Fixture();
        var tooDeep=f.Videos+"\\"+string.Join("\\",Enumerable.Repeat("x",32))+"\\out.mp4";
        f.Seed("candidate",path:tooDeep);Assert.Equal("path_check_limit",Assert.Single(f.Page().Items).ReasonCode);RetentionRuntimeOutputTests.NoProbes(f);
        using var c=f.Db.Store.OpenReadOnlyConnection();using var t=c.BeginTransaction();
        Assert.True(BundleNamespaceReferenceStorage.Read(c,t,new Dictionary<string,string?> { ["candidate"]=tooDeep })["candidate"]);
        Assert.Throws<InvalidDataException>(()=>BundleNamespaceReferenceStorage.Read(c,t,Enumerable.Range(0,101).ToDictionary(i=>"id"+i,_=>(string?)path)));
    }
    [Fact]
    public void V46UpgradePreservesOldTypedRowsAndOnlyAddsFiveIndexesAndReceipt()
    {
        using var f=new RetentionPreviewQueryTests.Fixture();f.Seed("candidate");
        f.Db.RawExecute("DROP TABLE ordinary_deletion_host_rejections; DELETE FROM schema_migrations WHERE version=51; DROP TABLE ordinary_deletion_batch_policy_children; DROP TABLE ordinary_deletion_batch_policies; DELETE FROM schema_migrations WHERE version=50; DROP TABLE ordinary_deletion_operation_policies; DROP TABLE ordinary_deletion_request_policies; DELETE FROM schema_migrations WHERE version=49; DROP TABLE ordinary_deletion_batch_items; DROP TABLE ordinary_deletion_batches; DELETE FROM schema_migrations WHERE version=48; "+string.Join(";",SqliteSchemaV47.Indexes.Select(i=>"DROP INDEX "+i.Name))+"; DELETE FROM schema_migrations WHERE version=47;");
        var before=SqlitePublicationIdentityReferenceTests.OldBytes(f.Db.Store.DatabasePath,true,46);
        Assert.Throws<RetentionPreviewUnavailableException>(()=>f.Page());RetentionRuntimeOutputTests.NoProbes(f);
        f.Db.Store.Initialize();Assert.Equal(before,SqlitePublicationIdentityReferenceTests.OldBytes(f.Db.Store.DatabasePath,true,46));
        var once=SqlitePublicationIdentityReferenceTests.OldBytes(f.Db.Store.DatabasePath,true);f.Db.Store.Initialize();
        Assert.Equal(once,SqlitePublicationIdentityReferenceTests.OldBytes(f.Db.Store.DatabasePath,true));
        Assert.Equal(51,f.Db.Scalar("SELECT max(version) FROM schema_migrations;"));Assert.Equal(5,SqliteSchemaV47.Indexes.Length);
    }
}
