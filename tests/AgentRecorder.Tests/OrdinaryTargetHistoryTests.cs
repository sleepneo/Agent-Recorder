using System.Text;
using System.Text.Json.Nodes;
using System.Security.Cryptography;
using AgentRecorder.Core;
using AgentRecorder.Infrastructure;
using AgentRecorder.Persistence;
using Microsoft.Data.Sqlite;
using Xunit;
using Xunit.Abstractions;
using static AgentRecorder.Tests.UnifiedHistoryTestDatabase;

namespace AgentRecorder.Tests;

public sealed class OrdinaryTargetHistoryTests(ITestOutputHelper output)
{
    internal static UnifiedHistoryTestDatabase Database() => new(Path.Combine(TestHelper.ProjectRoot,".local-data/temp/task331",Guid.NewGuid().ToString("N")));
    internal static string Key => OrdinaryScreenTargetKey.Create(OrdinaryScreenTargetKeyTests.Plan(kind:"display"))!;
    internal static string OtherKey => "target:v1:display:" + new string('b',64);
    internal static OrdinaryRecordingHistorySnapshot Row(UnifiedHistoryTestDatabase d,string id,bool completed=false) => d.Recording(id,completed:completed) with { TargetKey=Key };

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void AcceptedNullAndKeyAreDurableImmutableAndReplayIdempotent(bool hasTarget)
    {
        using var d=Database();var row=Row(d,"r") with {TargetKey=hasTarget?Key:null};d.Ordinary.Register(row);d.Ordinary.Register(row);
        Assert.Throws<InvalidOperationException>(()=>d.Ordinary.Register(row with {TargetKey=hasTarget?OtherKey:Key}));
        Assert.True(d.Ordinary.TryPublish(row with {Status="recording"}));Assert.Equal(row.TargetKey,d.Ordinary.Read("r")!.TargetKey);
        var terminal=row with {Status="failed",CompletedAtUtc=At.AddMinutes(1),ReasonCode="test_failure",BundleRegistration=new(null)};
        Assert.True(d.Ordinary.TryPublish(terminal));Assert.True(d.Ordinary.TryPublish(terminal));var actual=d.Ordinary.Read("r")!;
        Assert.Equal(row.TargetKey,actual.TargetKey);Assert.Equal("failed",actual.Status);
        Assert.Equal(row.TargetKey,Assert.Single(d.Ordinary.ReadPage(1,null).Items).TargetKey);Assert.Equal(row.TargetKey,Assert.Single(d.Page().Items).TargetKey);
        using var reopened=new SqliteOrdinaryRecordingHistoryStore(new SqliteOperationalStore(d.Store.DatabasePath));
        Assert.Equal(row.TargetKey,reopened.Read("r")!.TargetKey);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void LateLifecycleAndTerminalCannotRetargetAndFailuresRemainVisible(bool terminal)
    {
        using var d=Database();var row=Row(d,"r",terminal);d.Ordinary.Register(row);
        Assert.True(d.Ordinary.TryPublish(row with {TargetKey=OtherKey,Status=terminal?"completed":"recording"}));
        Assert.Throws<OrdinaryRecordingHistoryUnavailableException>(()=>d.Ordinary.Read("r"));
        Assert.True(d.Ordinary.TryPublish(row));Assert.Throws<UnifiedHistoryUnavailableException>(()=>d.Page());
        using var reopened=new SqliteOrdinaryRecordingHistoryStore(d.Store);Assert.Equal(Key,reopened.Read("r")!.TargetKey);
    }

    [Fact]
    public void FirstTargetFailureRollsBackMediaTargetAnchorAndBundle()
    {
        using var d=Database();d.Execute("CREATE TRIGGER fail_target BEFORE UPDATE ON ordinary_recording_targets BEGIN SELECT RAISE(ABORT,'injected target fault'); END;");
        var row=PublicationAnchorTestFixture.Anchored(Row(d,"r",true)) with {BundleRegistration=new(null)};
        Assert.Throws<SqliteException>(()=>d.Ordinary.Register(row));
        foreach(var table in new[]{"ordinary_recording_history","ordinary_recording_targets","ordinary_publication_anchors","ordinary_recording_bundles"}) Assert.Equal(0,d.Scalar("SELECT COUNT(*) FROM "+table+";"));
        d.Execute("DROP TRIGGER fail_target;");d.Ordinary.Register(row);Assert.Equal(Key,d.Ordinary.Read("r")!.TargetKey);
    }

    [Fact]
    public void BundleResultRecoveryAndMediaReplayKeepTargetAndAnchor()
    {
        using var d=Database();var row=PublicationAnchorTestFixture.Anchored(Row(d,"r",true));var op=OrdinaryRecordingBundleOperation.Create("r",row.OutputPath);
        row=row with {BundleRegistration=new(op)};d.Ordinary.Register(row);
        Assert.True(d.Ordinary.TryPublishBundle(new(op,new("ready",op.BundlePath,null))));Assert.True(d.Ordinary.TryPublish(row));
        Assert.Equal(Key,d.Ordinary.Read("r")!.TargetKey);Assert.Equal("ready",d.Ordinary.Read("r")!.Bundle!.Status);
        var before=UnifiedHistoryProfileFilterTests.RawSnapshot(d.Store.DatabasePath);
        using var reopened=new SqliteOrdinaryRecordingHistoryStore(d.Store);reopened.RecoverIncomplete(At.AddDays(1));
        Assert.Equal(before,UnifiedHistoryProfileFilterTests.RawSnapshot(d.Store.DatabasePath));Assert.Equal(Key,reopened.Read("r")!.TargetKey);
    }

    [Fact]
    public void StartupGeneratingRecoveryAndNullAsyncConflictKeepFrozenIdentity()
    {
        using var d=Database();var row=PublicationAnchorTestFixture.Anchored(Row(d,"r",true));var op=OrdinaryRecordingBundleOperation.Create("r",row.OutputPath);
        row=row with {BundleRegistration=new(op)};d.Ordinary.Register(row);
        using var reopened=new SqliteOrdinaryRecordingHistoryStore(d.Store);reopened.RecoverIncomplete(At.AddDays(1));
        var recovered=reopened.Read("r")!;Assert.Equal(Key,recovered.TargetKey);Assert.Equal("completed",recovered.Status);Assert.Equal("interrupted_unknown",recovered.Bundle!.Status);
        Assert.True(reopened.TryPublishBundle(new(op,new("ready",op.BundlePath,null))));Assert.Equal(recovered,reopened.Read("r"));
        d.Ordinary.Register(d.Recording("null"));Assert.True(d.Ordinary.TryPublish(d.Recording("null") with {Status="recording",TargetKey=Key}));
        Assert.Throws<OrdinaryRecordingHistoryUnavailableException>(()=>d.Ordinary.Read("null"));
        using var restart=new SqliteOrdinaryRecordingHistoryStore(d.Store);Assert.Null(restart.Read("null")!.TargetKey);
    }

    [Fact]
    public void TrueV31UpgradeDoesNotBackfillAndOldNullCannotBeFilled()
    {
        using var d=Database();var path=Path.Combine(d.Root,"v31.db");
        using(var c=new SqliteConnection($"Data Source={path};Pooling=False"))
        {
            c.Open();SqliteOutputPathCollation.Register(c);using var cmd=c.CreateCommand();
            foreach(var migration in SqliteSchemaCatalog.Migrations.Where(m=>m.Version<=31))
            {
                cmd.CommandText=migration.Definition;cmd.ExecuteNonQuery();cmd.CommandText="INSERT INTO schema_migrations VALUES($v,$n,$c,$t);";
                cmd.Parameters.AddWithValue("$v",migration.Version);cmd.Parameters.AddWithValue("$n",migration.Name);cmd.Parameters.AddWithValue("$c",migration.Checksum);cmd.Parameters.AddWithValue("$t",migration.Version+1000);cmd.ExecuteNonQuery();cmd.Parameters.Clear();
            }
            cmd.CommandText="INSERT INTO ordinary_recording_history(recording_id,history_sequence,created_at_utc,status_code,source_type,capture_semantics,source_title,audio_mode,output_path,updated_at_utc) VALUES('old',1,$at,'failed','display','display_capture','Synthetic','none',$path,$at);";
            cmd.Parameters.AddWithValue("$at",At.UtcTicks);cmd.Parameters.AddWithValue("$path",Path.Combine(d.Root,"old.mp4"));cmd.ExecuteNonQuery();
        }
        var before=UnifiedHistoryProfileFilterTests.RawSnapshot(path,schemaThrough:31);var store=new SqliteOperationalStore(path);store.Initialize();store.Initialize();
        Assert.Equal(before,UnifiedHistoryProfileFilterTests.RawSnapshot(path,schemaThrough:31));
        using var writer=new SqliteOrdinaryRecordingHistoryStore(store);var old=writer.Read("old")!;Assert.Null(old.TargetKey);writer.Register(old);
        Assert.Throws<InvalidOperationException>(()=>writer.Register(old with {TargetKey=Key}));
        using var connection=store.OpenReadOnlyConnection();using var count=connection.CreateCommand();count.CommandText="SELECT COUNT(*) FROM ordinary_recording_targets;";Assert.Equal(0L,count.ExecuteScalar());
    }

    [Theory]
    [InlineData("ALTER TABLE ordinary_recording_targets ADD COLUMN extra TEXT;")]
    [InlineData("DROP INDEX idx_ordinary_recording_targets_key_created;")]
    [InlineData("DROP INDEX idx_ordinary_recording_targets_key_created; CREATE INDEX idx_ordinary_recording_targets_key_created ON ordinary_recording_targets(target_key COLLATE NOCASE,created_at_utc DESC,recording_id ASC);")]
    [InlineData("UPDATE schema_migrations SET name='bad' WHERE version=32;")]
    [InlineData("UPDATE schema_migrations SET definition_checksum='bad' WHERE version=32;")]
    [InlineData("DELETE FROM ordinary_recording_targets;")]
    [InlineData("UPDATE ordinary_recording_targets SET created_at_utc=created_at_utc+1;")]
    [InlineData("UPDATE ordinary_recording_targets SET target_key='target:v1:region:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa';")]
    [InlineData("DELETE FROM ordinary_recording_target_boundary;")]
    public void DamagedSchemaReceiptAndAssociationFailHistoryRetentionBeforeAnyProbe(string sql)
    {
        using var d=Database();d.Ordinary.Register(Row(d,"r",true));d.RawExecute(sql.Replace("definition_checksum='bad'", "definition_checksum='"+new string('f',64)+"'",StringComparison.Ordinal));
        var probes=0;d.Query.OrdinaryFileProbeForTests=_=>{probes++;return true;};var files=new RetentionPreviewQueryTests.MetadataFake();
        var retention=new RetentionPreviewQueryService(d.Query.GetOrdinaryEvidencePage,d.Root,files,files,()=>At.AddDays(2), TestRuntimeOutputUse.None);
        Assert.Throws<UnifiedHistoryUnavailableException>(()=>d.Page());Assert.Throws<RetentionPreviewUnavailableException>(()=>retention.GetPage(Sid,Session,1,At.AddHours(1)));
        if(sql.Contains("schema_migrations",StringComparison.Ordinal)) Assert.Throws<SqliteOperationalStoreException>(()=>d.Ordinary.Read("r"));
        else Assert.Throws<OrdinaryRecordingHistoryUnavailableException>(()=>d.Ordinary.Read("r"));
        Assert.Equal(0,probes);Assert.Empty(files.Attributes);Assert.Empty(files.Observations);Assert.Empty(files.Lengths);
    }

    [Theory]
    [InlineData(1)] [InlineData(20)] [InlineData(100)]
    public void SparseDeepCombinedPagingUsesIndexBinarySeeksAndWatermark(int limit)
    {
        using var d=Database();using(var c=d.Store.OpenConnection())using(var cmd=c.CreateCommand())
        {
            cmd.CommandText="WITH RECURSIVE n(x) AS(SELECT 1 UNION ALL SELECT x+1 FROM n WHERE x<20000) INSERT INTO ordinary_recording_history(recording_id,history_sequence,created_at_utc,status_code,source_type,capture_semantics,source_title,audio_mode,output_path,profile_id,profile_version,profile_digest,updated_at_utc) SELECT 'r'||printf('%08d',x),x,$at-(x/10000),'failed','display','display_capture','Synthetic','none',$path,'P',2,$digest,$at FROM n; UPDATE ordinary_recording_targets SET target_key=$key WHERE CAST(substr(recording_id,2) AS INTEGER)%100=0;";
            cmd.Parameters.AddWithValue("$at",At.UtcTicks);cmd.Parameters.AddWithValue("$path",Path.Combine(d.Root,"none.mp4"));cmd.Parameters.AddWithValue("$digest",AgentRecorder.Core.Automation.RecurringFixedRegionProfileVersion.DigestPrefix+new string('a',64));cmd.Parameters.AddWithValue("$key",Key);cmd.ExecuteNonQuery();
        }
        var expected=Enumerable.Range(1,200).Select(i=>"r"+(i*100).ToString("00000000")).ToList();
        foreach(var id in new[]{"A","a","é","中","\U00010000","\uE000"}) {d.Ordinary.Register(Row(d,id) with {Status="failed",ProfileId="P",ProfileVersion=2,ProfileDigest=AgentRecorder.Core.Automation.RecurringFixedRegionProfileVersion.DigestPrefix+new string('a',64)});expected.Add(id);}
        var filter=new UnifiedHistoryFilters("ordinary",At.AddTicks(-3),At.AddTicks(1),"failed","P",2,"display",TargetKey:Key);
        var plans=new List<string>();d.Query.CandidatesSelectedForTests=(_,count)=>Assert.InRange(count,0,limit+1);d.Query.EntriesValidatedForTests=(_,count)=>Assert.InRange(count,1,limit);
        d.Query.CandidateCommandForTests=(_,sql,args)=>
        {
            Assert.DoesNotContain("OFFSET",sql);Assert.DoesNotContain(Key,sql);Assert.Contains("t.target_key=$target",sql);
            using var c=d.Store.OpenReadOnlyConnection();using var cmd=c.CreateCommand();cmd.CommandText="EXPLAIN QUERY PLAN "+sql;foreach(var a in args)cmd.Parameters.AddWithValue(a.Key,a.Value);
            using var r=cmd.ExecuteReader();var current=new List<string>();while(r.Read())current.Add(r.GetString(3));plans.AddRange(current);
            Assert.Contains(current,p=>p.Contains("SEARCH t USING COVERING INDEX "+SqliteSchemaV32.IndexName)&&p.Contains("target_key=?"));Assert.DoesNotContain(current,p=>p.Contains("TEMP B-TREE"));
        };
        var batches=0;d.Query.TargetCommandForTests=(sql,args)=>
        {
            batches++;Assert.InRange(args.Count,1,limit);Assert.Contains("WITH requested",sql);
            using var c=d.Store.OpenReadOnlyConnection();using var cmd=c.CreateCommand();cmd.CommandText="EXPLAIN QUERY PLAN "+sql;foreach(var a in args)cmd.Parameters.AddWithValue(a.Key,a.Value);
            using var r=cmd.ExecuteReader();var current=new List<string>();while(r.Read())current.Add(r.GetString(3));
            Assert.Contains(current,p=>p.Contains("SEARCH t USING INDEX sqlite_autoindex_ordinary_recording_targets_1"));
        };
        var pages=1;var page=d.Page(limit,filter);var seen=page.Items.Select(i=>i.RecordingId!).ToList();
        d.Ordinary.Register(Row(d,"late") with {Status="failed",ProfileId="P",ProfileVersion=2,ProfileDigest=AgentRecorder.Core.Automation.RecurringFixedRegionProfileVersion.DigestPrefix+new string('a',64)});
        while(page.NextCursor is not null){pages++;page=d.Page(limit,filter,page.NextCursor);seen.AddRange(page.Items.Select(i=>i.RecordingId!));}
        Assert.Equal(pages,batches);
        var sorted=expected.OrderByDescending(id=>id.StartsWith("r",StringComparison.Ordinal)?At.UtcTicks-int.Parse(id[1..])/10000:At.UtcTicks)
            .ThenBy(id=>Convert.ToHexString(Encoding.UTF8.GetBytes(id)),StringComparer.Ordinal);
        Assert.Equal(sorted,seen);Assert.Equal(expected.Count,seen.Distinct().Count());Assert.DoesNotContain("late",seen);
        Assert.Contains(plans,p=>p.Contains("created_at_utc=? AND recording_id>?"));Assert.Contains(plans,p=>p.Contains("created_at_utc<?"));
        output.WriteLine(string.Join(" | ",plans.Distinct()));
    }

    [Fact]
    public void CursorBindsTargetAndLegacyMissingPropertyStillWorks()
    {
        using var d=Database();foreach(var id in new[]{"a","b","c"})d.Ordinary.Register(Row(d,id));
        var filters=new UnifiedHistoryFilters("ordinary",TargetKey:Key);var cursor=d.Page(1,filters).NextCursor!;
        foreach(var changed in new[]{filters with {TargetKey=null},filters with {TargetKey=OtherKey},filters with {SourceType="display"},filters with {Status="failed"}})
            Assert.Throws<UnifiedHistoryArgumentException>(()=>d.Page(1,changed,cursor));
        var old=d.Page(1,new("ordinary")).NextCursor!;var body=old.Split('.')[0];var node=JsonNode.Parse(Convert.FromBase64String(body.Replace('-','+').Replace('_','/')+new string('=',(4-body.Length%4)%4)))!;
        node["filters"]!.AsObject().Remove("targetKey");body=Convert.ToBase64String(Encoding.UTF8.GetBytes(node.ToJsonString())).TrimEnd('=').Replace('+','-').Replace('/','_');
        old=body+"."+Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(body))).ToLowerInvariant();
        Assert.Equal(new[]{"b","c"},d.Page(100,new("ordinary"),old).Items.Select(i=>i.RecordingId));
        Assert.Empty(d.Page(filters:new("ordinary",TargetKey:OtherKey)).Items);
    }

    [Fact]
    public void TargetMediaBundleStayInOneSnapshotUnderConcurrentUpdates()
    {
        using var d=Database();var accepted=Row(d,"r");d.Ordinary.Register(accepted);
        var terminal=PublicationAnchorTestFixture.Anchored(Row(d,"r",true));var op=OrdinaryRecordingBundleOperation.Create("r",terminal.OutputPath);terminal=terminal with {BundleRegistration=new(op)};
        d.Query.SnapshotEstablishedForTests=()=>
        {
            Assert.True(d.Ordinary.TryPublish(terminal));Assert.True(d.Ordinary.TryPublishBundle(new(op,new("ready",op.BundlePath,null))));
            Assert.Equal("ready",d.Ordinary.Read("r")!.Bundle!.Status);d.Ordinary.Register(Row(d,"late"));
            d.Query.SnapshotEstablishedForTests=null;
        };
        var first=Assert.Single(d.Page().Items);Assert.Equal(Key,first.TargetKey);Assert.Equal("preparing",first.Status);Assert.Null(first.Bundle);
        var next=d.Page().Items.Single(i=>i.RecordingId=="r");Assert.Equal(Key,next.TargetKey);Assert.Equal("completed",next.Status);Assert.Equal("ready",next.Bundle!.Status);Assert.True(next.OutputPathRecorded);
    }
}
