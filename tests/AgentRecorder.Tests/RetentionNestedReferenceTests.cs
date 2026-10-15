using System.Text;
using AgentRecorder.Core;
using AgentRecorder.Infrastructure;
using AgentRecorder.Persistence;
using Microsoft.Data.Sqlite;
using Xunit;
using static AgentRecorder.Tests.UnifiedHistoryTestDatabase;

namespace AgentRecorder.Tests;

public sealed class RetentionNestedReferenceTests
{
    internal sealed class Fixture : IDisposable
    {
        internal readonly UnifiedHistoryTestDatabase Db=new(Path.Combine(TestHelper.ProjectRoot,".local-data/temp/task325r",Guid.NewGuid().ToString("N")));
        internal readonly RetentionPreviewQueryTests.MetadataFake Files=new();
        internal UnifiedHistoryQueryService Query;
        internal RetentionPreviewQueryService Service;
        internal readonly DateTimeOffset Before=At.AddHours(1);
        internal int Reads,OldProbes;
        internal bool KeepFixtureForDiagnostics;
        internal Fixture(bool headless=false)
        {
            Query=headless?new(Db.Store,Db.Ordinary,true):Db.Query;
            Query.OrdinaryFileProbeForTests=_=>{OldProbes++;throw new InvalidOperationException("SECRET_EARLY_FILE_PROBE");};
            Service=new((sid,session,limit,before,cursor)=>{Reads++;return Query.GetOrdinaryEvidencePage(sid,session,limit,before,cursor);},Db.Root,Files,Files,()=>At.AddDays(5), TestRuntimeOutputUse.None);
        }
        internal OrdinaryRecordingHistorySnapshot Seed(string id,string? role=null,string? parent=null,string? session=null,string status="completed",DateTimeOffset? at=null)
        {
            var row=Db.Recording(id,at,status=="completed") with{Status=status,NestedRole=role,ParentRecordingId=parent,NestedSessionId=session,OutputPath=Path.Combine(Db.Root,"Videos",id+".mp4")};row=PublicationAnchorTestFixture.Anchored(row);if (row.Status == "completed") row = row with { BundleRegistration = new(null) }; Db.Ordinary.Register(row);return row;
        }
        internal RetentionPreviewPage Page(int limit=20,string? cursor=null)=>Service.GetPage(Sid,Session,limit,Before,cursor);
        internal void NoProbes(){Assert.Empty(Files.Attributes);Assert.Empty(Files.Lengths);Assert.Empty(Files.Observations);Assert.Equal(0,OldProbes);}
        internal string RawSnapshot()
        {
            using var c=new SqliteConnection($"Data Source={Db.Store.DatabasePath};Pooling=False;Mode=ReadOnly");c.Open();
            using var names=c.CreateCommand();names.CommandText="SELECT name FROM sqlite_master WHERE type='table' AND name NOT LIKE 'sqlite_%' ORDER BY name;";
            var tables=new List<string>();using(var r=names.ExecuteReader())while(r.Read())tables.Add(r.GetString(0));
            var rows=new List<string>();foreach(var table in tables)
            {using var cmd=c.CreateCommand();cmd.CommandText="SELECT * FROM \""+table+"\";";using var r=cmd.ExecuteReader();while(r.Read())rows.Add(table+":"+string.Join("|",Enumerable.Range(0,r.FieldCount).Select(i=>r.GetValue(i).ToString())));}
            rows.Sort(StringComparer.Ordinal);return string.Join("\n",rows);
        }
        public void Dispose()
        {
            if(KeepFixtureForDiagnostics){Db.Ordinary.Dispose();SqliteConnection.ClearAllPools();}
            else Db.Dispose();
        }
    }
    [Theory]
    [InlineData("outer",null,null)][InlineData("inner","missing-parent",null)]
    [InlineData(null,"missing-parent",null)][InlineData(null,null,"session-only")][InlineData("outer",null,"finished-session")]
    public void OwnRelationshipIsProtectedEvenWhenOtherMembersAreMissing(string? role,string? parent,string? session)
    {
        using var f=new Fixture();f.Seed("target",role,parent,session);var before=f.Db.Snapshot();var page=f.Page();var item=Assert.Single(page.Items);
        Assert.Equal("protected",item.Evaluation);Assert.Equal("nested_relationship",item.ReasonCode);Assert.True(item.AgeCandidate);Assert.Null(item.ObservedFileBytes);Assert.Equal(12,item.RecordedBytes);
        Assert.Equal(1,page.AgeCandidateCount);Assert.Equal(1,page.UnknownCandidateFileCount);Assert.Equal(0,page.ObservedCandidateBytes);Assert.Equal(before,f.Db.Snapshot());f.NoProbes();
    }
    [Theory]
    [InlineData("completed",false)][InlineData("failed",true)][InlineData("recording",true)][InlineData("preparing",false)]
    public void ParentReferenceIsIndependentOfChildDateStatusOrPage(string status,bool newer)
    {
        using var f=new Fixture();f.Seed("parent",at:At.AddMinutes(1));f.Seed("PRIVATE_CHILD",parent:"parent",status:status,at:newer?f.Before.AddDays(1):At.AddDays(-1));
        var before=f.Db.Snapshot();var page=f.Page(1);var item=Assert.Single(page.Items);Assert.Equal("ordinary:parent",item.EntryId);Assert.Equal("nested_parent_referenced",item.ReasonCode);Assert.True(item.AgeCandidate);f.NoProbes();
        Assert.Equal(!newer,page.NextCursor is not null);Assert.Equal(before,f.Db.Snapshot());
    }
    [Fact]
    public void SelfReferenceAndCyclesAreProtectedWithoutTraversingGraph()
    {
        using var f=new Fixture();f.Seed("self",parent:"self");f.Seed("a",parent:"b");f.Seed("b",parent:"a");
        Assert.All(f.Page().Items,item=>Assert.Equal("nested_relationship",item.ReasonCode));f.NoProbes();
    }
    [Theory]
    [InlineData("nested_role","")][InlineData("nested_role"," outer")][InlineData("nested_role","other")]
    [InlineData("parent_recording_id","")][InlineData("parent_recording_id"," ")][InlineData("parent_recording_id","bad/id")][InlineData("parent_recording_id","bad\\id")]
    [InlineData("parent_recording_id","long")][InlineData("nested_session_id","")][InlineData("nested_session_id","bad ")][InlineData("nested_session_id","long")][InlineData("nested_session_id","nul")]
    public void MalformedOwnFieldsAreExplicitlyProtected(string column,string input)
    {
        using var f=new Fixture();f.Seed("target");var value=input=="long"?new string('x',129):input=="nul"?"secret\0session":input;
        f.Db.Corrupt("UPDATE ordinary_recording_history SET "+column+"=$value;",("$value",value));var before=f.Db.Snapshot();
        var item=Assert.Single(f.Page().Items);Assert.Equal("nested_reference_invalid",item.ReasonCode);Assert.True(item.AgeCandidate);f.NoProbes();Assert.Equal(before,f.Db.Snapshot());
    }
    [Theory]
    [InlineData("status","not_completed")][InlineData("completion","completion_time_unknown")][InlineData("evidence","output_evidence_missing")]
    [InlineData("cutoff","completion_not_before_cutoff")][InlineData("external","outside_videos")]
    public void EarlierEvidenceAndLexicalReasonsKeepTheirPriority(string fault,string reason)
    {
        using var f=new Fixture();f.Seed("target","outer");
        // Legacy evidence has neither an anchor nor a Bundle registration.
        // Corrupt bound facts are tested separately and must fail the page.
        f.Db.Execute("DELETE FROM ordinary_bundle_attachment_hash_items; DELETE FROM ordinary_bundle_attachment_hash; DELETE FROM ordinary_media_hash; DELETE FROM ordinary_bundle_identity_items; DELETE FROM ordinary_bundle_identity; DELETE FROM ordinary_bundle_contents_items; DELETE FROM ordinary_bundle_contents; DELETE FROM ordinary_recording_bundles; DELETE FROM ordinary_publication_anchors;");
        var sql=fault switch{"status"=>"status_code='recording'","completion"=>"completed_at_utc=NULL","evidence"=>"output_size_bytes=0","cutoff"=>"completed_at_utc="+f.Before.UtcTicks,_=>"output_path='C:\\External\\target.mp4'"};
        f.Db.Corrupt("UPDATE ordinary_recording_history SET "+sql+";");var item=Assert.Single(f.Page().Items);Assert.Equal(reason,item.ReasonCode);Assert.False(item.AgeCandidate);f.NoProbes();
    }
    [Fact]
    public void ProtectedCandidatesContributeUnknownBytesWhileStandaloneKeepsOldSamples()
    {
        using var f=new Fixture();f.Seed("nested","outer");f.Seed("standalone");var page=f.Page();Assert.Equal(2,page.AgeCandidateCount);Assert.Equal(12,page.ObservedCandidateBytes);Assert.Equal(1,page.ObservedCandidateFileCount);Assert.Equal(1,page.UnknownCandidateFileCount);
        Assert.Equal("age_rule_match",page.Items.Single(x=>x.RecordingId=="standalone").ReasonCode);Assert.Single(f.Files.Lengths);Assert.All(f.Files.Attributes,p=>Assert.DoesNotContain("nested.mp4",p));
    }
    [Fact]
    public async Task RelationReadUsesThePageTransactionDespiteConcurrentChildInsertion()
    {
        using var f=new Fixture();f.Seed("parent");using var entered=new ManualResetEventSlim();using var release=new ManualResetEventSlim();
        f.Query.EntriesValidatedForTests=(_,_)=>{entered.Set();Assert.True(release.Wait(TimeSpan.FromSeconds(10)));};var pending=Task.Run(()=>f.Page(1));
        try {Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));f.Seed("new-child",parent:"parent",at:f.Before.AddDays(1));}finally{release.Set();}
        var first=Assert.Single((await pending).Items);Assert.Equal("age_rule_match",first.ReasonCode);
        f.Query.EntriesValidatedForTests=null;f.Files.Attributes.Clear();f.Files.Lengths.Clear();f.Files.Observations.Clear();f.Files.ProbeAttributes.Clear();Assert.Equal("nested_parent_referenced",Assert.Single(f.Page(1).Items).ReasonCode);f.NoProbes();
    }
    [Fact]
    public void NewlyInsertedChildBeyondCursorWatermarkStillProtectsNextParent()
    {
        using var f=new Fixture();f.Seed("a");f.Seed("b");var cursor=f.Page(1).NextCursor;Assert.NotNull(cursor);
        f.Seed("PRIVATE_NEW_CHILD",parent:"b",status:"failed",at:f.Before.AddDays(1));f.Files.Attributes.Clear();f.Files.Lengths.Clear();f.Files.Observations.Clear();f.Files.ProbeAttributes.Clear();
        var item=Assert.Single(f.Page(1,cursor).Items);Assert.Equal("ordinary:b",item.EntryId);Assert.Equal("nested_parent_referenced",item.ReasonCode);f.NoProbes();
    }
    [Theory]
    [InlineData("missing")][InlineData("invalid")][InlineData("null_provider")][InlineData("throwing")][InlineData("duplicate")]
    public void MissingOrUntrustworthyRelationEvidenceFailsClosedBeforeAnySampling(string fault)
    {
        using var f=new Fixture();f.Seed("target");var actual=f.Query.GetOrdinaryEvidencePage(Sid,Session,20,f.Before,null);var item=actual.Items[0];
        var altered=actual with{Items=fault=="duplicate"?new[]{item,item}:new[]{item with{NestedReference=fault=="missing"?null:(OrdinaryNestedReferenceState)999}}};
        Func<string,string,int,DateTimeOffset,string?,OrdinaryRetentionEvidencePage> read=(_,_,_,_,_)=>fault=="throwing"?throw new IOException("SECRET_SQL"):altered;
        var service=new RetentionPreviewQueryService(fault=="null_provider"?null!:read,f.Db.Root,f.Files,f.Files,()=>At.AddDays(5), TestRuntimeOutputUse.None);Assert.Throws<RetentionPreviewUnavailableException>(()=>service.GetPage(Sid,Session,20,f.Before));f.NoProbes();
    }
    [Fact]
    public void LargeFixtureUsesOneBoundedBatchWithIndexedParentExists()
    {
        using var f=new Fixture();for(var i=0;i<100;i++)f.Seed("parent"+i.ToString("D3"));
        for(var i=0;i<1000;i++)f.Seed("child"+i.ToString("D4"),parent:"parent"+(i%100).ToString("D3"),status:"failed",at:f.Before.AddDays(1));
        var commands=0;string? sql=null;IReadOnlyList<KeyValuePair<string,object>>? args=null;
        f.Query.RetentionReferenceCommandForTests=(s,p)=>{commands++;sql=s;args=p;};var before=f.Db.Snapshot();var page=f.Page(100);
        Assert.Equal(100,page.EvaluatedCount);Assert.Equal(1,commands);Assert.Equal(100,args!.Count);Assert.All(page.Items,x=>Assert.Equal("nested_parent_referenced",x.ReasonCode));f.NoProbes();
        using var c=f.Db.Store.OpenReadOnlyConnection();using var cmd=c.CreateCommand();cmd.CommandText="EXPLAIN QUERY PLAN "+sql;foreach(var p in args)cmd.Parameters.AddWithValue(p.Key,p.Value);
        using var reader=cmd.ExecuteReader();var plan=new List<string>();while(reader.Read())plan.Add(reader.GetString(3));Assert.Contains(plan,x=>x.Contains("SEARCH child USING COVERING INDEX idx_ordinary_recording_history_parent (parent_recording_id=?)"));
        Assert.Equal(before,f.Db.Snapshot());
    }
    [Theory]
    [InlineData("DROP INDEX idx_ordinary_recording_history_parent;")]
    [InlineData("DROP INDEX idx_ordinary_recording_history_parent; CREATE INDEX idx_ordinary_recording_history_parent ON ordinary_recording_history(parent_recording_id COLLATE NOCASE);")]
    [InlineData("UPDATE schema_migrations SET definition_checksum='0000000000000000000000000000000000000000000000000000000000000000' WHERE version=25;")]
    public void V25DamageIsUnavailableAndNeverRepairedByRead(string sql)
    {
        using var f=new Fixture();f.Seed("target");f.Db.Execute(sql);var before=f.RawSnapshot();Assert.Throws<RetentionPreviewUnavailableException>(()=>f.Page());Assert.Throws<SqliteOperationalStoreException>(()=>f.Db.Store.Initialize());f.NoProbes();Assert.Equal(before,f.RawSnapshot());
    }
    [Fact]
    public void V24UpgradesByOnlyOneIndexAndRetainsEveryHistoricalByteAndChecksum()
    {
        using var f=new Fixture();f.Seed("parent","outer");f.Seed("child","inner","parent","finished-session");var path=Path.Combine(f.Db.Root,"genuine-v24.db");
        using(var c=new SqliteConnection($"Data Source={path};Pooling=False"))
        {
            c.Open();using var cmd=c.CreateCommand();cmd.CommandText="PRAGMA foreign_keys=ON;";cmd.ExecuteNonQuery();
            foreach(var migration in SqliteSchemaCatalog.Migrations.Where(m=>m.Version<=24))
            {
                cmd.CommandText=migration.Definition;cmd.ExecuteNonQuery();cmd.CommandText="INSERT INTO schema_migrations VALUES($v,$n,$c,$at);";cmd.Parameters.AddWithValue("$v",migration.Version);cmd.Parameters.AddWithValue("$n",migration.Name);cmd.Parameters.AddWithValue("$c",migration.Checksum);cmd.Parameters.AddWithValue("$at",1000+migration.Version);cmd.ExecuteNonQuery();cmd.Parameters.Clear();
            }
            cmd.CommandText="ATTACH DATABASE $source AS seed;";cmd.Parameters.AddWithValue("$source",f.Db.Store.DatabasePath);cmd.ExecuteNonQuery();cmd.Parameters.Clear();cmd.CommandText="INSERT INTO ordinary_recording_history SELECT * FROM seed.ordinary_recording_history;";Assert.Equal(2,cmd.ExecuteNonQuery());
        }
        var store=new SqliteOperationalStore(path);var before=OldBytes(store);store.Initialize();store.Initialize();Assert.Equal(before,OldBytes(store));
        using var read=store.OpenReadOnlyConnection();using var check=read.CreateCommand();check.CommandText="SELECT name,definition_checksum FROM schema_migrations WHERE version=25;";using var r=check.ExecuteReader();Assert.True(r.Read());Assert.Equal(SqliteSchemaV25.MigrationName,r.GetString(0));Assert.Equal("67593a68f47a292736305488562badfe2656c3eef45d8c88c2298db9a4427a5a",r.GetString(1));Assert.False(r.Read());
        using var writer=new SqliteOrdinaryRecordingHistoryStore(store);var query=new UnifiedHistoryQueryService(store,writer);var service=new RetentionPreviewQueryService(query.GetOrdinaryEvidencePage,f.Db.Root,f.Files,f.Files,()=>At.AddDays(5), TestRuntimeOutputUse.None);Assert.All(service.GetPage(Sid,Session,20,f.Before).Items,x=>Assert.Equal("nested_relationship",x.ReasonCode));f.NoProbes();
    }
    private static byte[] OldBytes(SqliteOperationalStore store)
    {
        using var c=new SqliteConnection($"Data Source={store.DatabasePath};Pooling=False;Mode=ReadOnly");c.Open();var parts=new List<string>();
        foreach(var sql in new[]{"SELECT * FROM ordinary_recording_history ORDER BY recording_id;","SELECT * FROM schema_migrations WHERE version<=24 ORDER BY version;","SELECT name,sql FROM sqlite_master WHERE type='table' AND name NOT IN ('plan_media_publication_anchors','plan_media_publication_boundary','future_window_publication_anchors','future_window_publication_boundary','ordinary_bundle_attachment_hash','ordinary_bundle_attachment_hash_boundary','ordinary_bundle_attachment_hash_items','ordinary_media_hash','ordinary_media_hash_boundary','ordinary_bundle_identity','ordinary_bundle_identity_boundary','ordinary_bundle_identity_items','ordinary_recording_window_targets','ordinary_window_target_boundary','ordinary_bundle_contents','ordinary_bundle_contents_boundary','ordinary_bundle_contents_items','ordinary_publication_anchors','ordinary_recording_bundles','ordinary_recording_targets','ordinary_recording_target_boundary') ORDER BY name;"})
        {using var cmd=c.CreateCommand();cmd.CommandText=sql;using var r=cmd.ExecuteReader();while(r.Read()){
            // This projection is specifically v24; retain every v24 field and
            // definition, excluding only the newly introduced v43 table DDL.
            if(r.FieldCount==2&&r.GetValue(0) is string name&&(name==SqliteSchemaV43.Table||name==SqliteSchemaV44.Table))continue;
            parts.Add(string.Join("|",Enumerable.Range(0,r.FieldCount).Select(i=>r.GetValue(i).ToString())));}}
        return Encoding.UTF8.GetBytes(string.Join("\n",parts));
    }
}
