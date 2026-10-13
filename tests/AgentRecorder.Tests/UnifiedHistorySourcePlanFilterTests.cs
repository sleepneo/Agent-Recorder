using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using AgentRecorder.Core;
using AgentRecorder.Core.Automation;
using AgentRecorder.Infrastructure;
using AgentRecorder.Persistence;
using Microsoft.Data.Sqlite;
using Xunit;
using Xunit.Abstractions;
using static AgentRecorder.Tests.UnifiedHistoryTestDatabase;

namespace AgentRecorder.Tests;

[Collection("NonParallel-AgentRecorderDataDir")]
public sealed class UnifiedHistorySourcePlanFilterTests(ITestOutputHelper output)
{
    internal static UnifiedHistoryTestDatabase Database() => new(Path.Combine(".local-data/temp/task321",Guid.NewGuid().ToString("N")));
    internal static void Seed(UnifiedHistoryTestDatabase d,string id,string type="window",string status="completed",DateTimeOffset? at=null,bool profile=false) =>
        d.Ordinary.Register(d.Recording(id,at,status=="completed") with {SourceType=type,Status=status,
            ProfileId=profile?"Profile-A":null,ProfileVersion=profile?2:null,
            ProfileDigest=profile?RecurringFixedRegionProfileVersion.DigestPrefix+new string('a',64):null});

    [Theory]
    [InlineData("display")] [InlineData("window")] [InlineData("region")] [InlineData("fixed_region_profile")]
    public void FourPersistentTypesFilterBeforeLimitWithoutChangingOldRows(string type)
    {
        using var d=Database();foreach(var t in UnifiedHistoryFilterCodes.SourceTypes)Seed(d,t,t);
        Seed(d,"legacy","legacy-source");var before=d.Snapshot();
        var row=Assert.Single(d.Page(1,new("ordinary",SourceType:type)).Items);Assert.Equal(type,row.RecordingId);Assert.Equal(type,row.SourceType);
        Assert.Equal(5,d.Page(100,new("ordinary")).Items.Count);Assert.Equal(before,d.Snapshot());
    }

    [Fact]
    public void EngineProfileSnapshotReallyPersistsFixedRegionProfileWithoutStartingCapture()
    {
        using var d=Database();var recording=new Recording("profile-recording") {SourceType="region",State=RecState.preparing,
            DurationSeconds=15,OutputPath=Path.Combine(d.Root,"never-created.mp4"),
            FixedRegionProfileReference=new ProfileRef("Profile-A",2,RecurringFixedRegionProfileVersion.DigestPrefix+new string('a',64))};
        var method=typeof(RecordingEngine).GetMethod("CreateOrdinaryHistorySnapshot",BindingFlags.NonPublic|BindingFlags.Static)!;
        var snapshot=Assert.IsType<OrdinaryRecordingHistorySnapshot>(method.Invoke(null,new object[]{recording}));
        Assert.Equal("fixed_region_profile",snapshot.SourceType);d.Ordinary.Register(snapshot);
        Assert.Equal("profile-recording",Assert.Single(d.Page(filters:new("ordinary",ProfileId:"Profile-A",ProfileVersion:2,SourceType:"fixed_region_profile")).Items).RecordingId);
        Assert.Empty(d.Page(filters:new("ordinary",SourceType:"region")).Items);Assert.False(File.Exists(recording.OutputPath));
    }

    [Theory]
    [InlineData(1)] [InlineData(100)]
    public void TypeStatusProfileTimeIntersectionAndUtf8BinaryPaginationHaveNoGaps(int limit)
    {
        using var d=Database();var expected=Enumerable.Range(0,125).Select(i=>$"r{i:000}").Concat(new[]{"A","a","é","中","\U00010000","\uE000"}).ToArray();
        foreach(var id in expected){Seed(d,id,"fixed_region_profile","failed",profile:true);Seed(d,"noise-"+id,"region","failed",profile:true);}
        Seed(d,"old","fixed_region_profile","failed",At.AddTicks(-1),true);Seed(d,"new","fixed_region_profile","failed",At.AddTicks(1),true);
        Seed(d,"status","fixed_region_profile",profile:true);Seed(d,"profile","fixed_region_profile","failed");
        var f=new UnifiedHistoryFilters("ordinary",At,At.AddTicks(1),"failed","Profile-A",2,"fixed_region_profile");
        var seen=new List<string>();string? cursor=null;
        do{var page=d.Page(limit,f,cursor);seen.AddRange(page.Items.Select(e=>e.RecordingId!));cursor=page.NextCursor;}while(cursor is not null);
        Assert.Equal(expected.OrderBy(id=>Convert.ToHexString(Encoding.UTF8.GetBytes(id)),StringComparer.Ordinal),seen);
        Assert.Equal(seen.Count,seen.Distinct().Count());Assert.Empty(d.Page(filters:f with{SourceType="display"}).Items);
    }

    public static IEnumerable<object[]> Invalid => new UnifiedHistoryFilters[] {
        new(SourceType:"window"),new("Ordinary",SourceType:"window"),new("plan",SourceType:"window"),new("future_window",SourceType:"window"),
        new("ordinary",SourceType:""),new("ordinary",SourceType:"Window"),new("ordinary",SourceType:"window,display"),new("ordinary",SourceType:"unknown"),
        new(PlanId:"p"),new("ordinary",PlanId:"p"),new("future_window",PlanId:"p"),new("plan",PlanId:""),new("plan",PlanId:" p"),
        new("plan",PlanId:"p "),new("plan",PlanId:"p/q"),new("plan",PlanId:"p'"),new("plan",PlanId:"é"),new("plan",PlanId:new string('p',129)),
        new("plan",Status:"completed",PlanId:"p"),new("plan",ProfileId:"p",PlanId:"p"),new("plan",SourceType:"window",PlanId:"p")
    }.Select(f=>new object[]{f.Source,f.SourceType!,f.PlanId!,f.Status!,f.ProfileId!});
    [Theory,MemberData(nameof(Invalid))]
    public void CoreRejectsInvalidTypesIdsAndCrossSourceCombinations(string source,string? sourceType,string? planId,string? status,string? profileId)
    {
        // Primitive data keeps every theory case independently addressable in TRX.
        using var d=Database();var filters=new UnifiedHistoryFilters(source,Status:status,ProfileId:profileId,SourceType:sourceType,PlanId:planId);
        Assert.Throws<UnifiedHistoryArgumentException>(()=>d.Page(filters:filters));
    }

    [Fact]
    public void ExactPlanIdOwnershipPrecedesProjectionAndForeignCorruptionRemainsInvisible()
    {
        using var d=Database();d.SeedPlan("Plan-A");d.SeedPlan("plan-a");d.SeedPlan("foreign","foreign-sid","foreign-session");
        d.SeedOccurrence("Plan-A","a");d.SeedOccurrence("plan-a","b");d.SeedOccurrence("foreign","foreign-occ");
        d.Corrupt("UPDATE plans SET status_code='SECRET_CORRUPT' WHERE id='foreign';");
        var projections=0;d.Query.EntriesValidatedForTests=(_,n)=>projections+=n;
        Assert.Equal("a",Assert.Single(d.Page(1,new("plan",PlanId:"Plan-A")).Items).OccurrenceId);
        Assert.Equal("b",Assert.Single(d.Page(1,new("plan",PlanId:"plan-a")).Items).OccurrenceId);Assert.Equal(2,projections);
        foreach(var id in new[]{"foreign","unknown",new string('p',128),"Plan_A.1-2"})Assert.Empty(d.Page(filters:new("plan",PlanId:id)).Items);
        Assert.Equal(2,projections);Assert.Empty(d.Page(filters:new("plan",PlanId:"Plan-A"),sid:"other").Items);
        Assert.Empty(d.Page(filters:new("plan",PlanId:"Plan-A"),session:"other").Items);
        var foreign=Assert.Throws<UnifiedHistoryUnavailableException>(()=>d.Page(filters:new("plan",PlanId:"foreign"),sid:"foreign-sid",session:"foreign-session"));
        Assert.DoesNotContain("SECRET",foreign.Message);
    }

    [Fact]
    public void SelectedLocatorCannotReplaceOriginalOccurrenceOwnerCrossValidation()
    {
        using var d=Database();d.SeedPlan("p");d.SeedPlan("q");d.SeedOccurrence("p","a-valid");d.SeedOccurrence("p","z-corrupt");
        d.Corrupt("UPDATE plan_history_locator SET plan_id='q' WHERE occurrence_id='z-corrupt';");
        Assert.Throws<UnifiedHistoryUnavailableException>(()=>d.Page(100,new("plan",PlanId:"q")));
        Assert.Throws<UnifiedHistoryUnavailableException>(()=>d.Page(100,new("plan")));
    }

    [Theory]
    [InlineData(1)] [InlineData(100)]
    public void PlanFilterTimeAndUtf8TiesPaginateOnlyMatchingPlanByCreation(int limit)
    {
        using var d=Database();d.SeedPlan("p");d.SeedPlan("noise");
        var ids=Enumerable.Range(0,125).Select(i=>$"o{i:000}").Concat(new[]{"A","a","é","中","\U00010000","\uE000"}).ToArray();
        foreach(var id in ids){d.SeedOccurrence("p",id);d.SeedOccurrence("noise","noise-"+id);}
        d.SeedOccurrence("p","old",At.AddTicks(-1));d.SeedOccurrence("p","new",At.AddTicks(1));
        // Window times deliberately differ from creation order; unified uses creation.
        d.Execute("UPDATE plan_occurrences SET window_start_utc=$start,window_end_utc=$end WHERE id='a';",("$start",At.AddDays(2).UtcTicks),("$end",At.AddDays(2).AddMinutes(1).UtcTicks));
        var f=new UnifiedHistoryFilters("plan",At,At.AddTicks(1),PlanId:"p");var seen=new List<string>();string? cursor=null;
        do{var page=d.Page(limit,f,cursor);Assert.All(page.Items,e=>Assert.Equal("p",e.PlanId));seen.AddRange(page.Items.Select(e=>e.OccurrenceId!));cursor=page.NextCursor;}while(cursor is not null);
        Assert.Equal(ids.OrderBy(id=>Convert.ToHexString(Encoding.UTF8.GetBytes(id)),StringComparer.Ordinal),seen);Assert.Equal(seen.Count,seen.Distinct().Count());
    }

    [Fact]
    public void ForgedLocatorIdentityCannotBypassOriginalForeignOwnerExists()
    {
        using var d=Database();d.SeedPlan("foreign","other-sid","other-session");d.SeedOccurrence("foreign","o");
        d.Corrupt("UPDATE plan_history_locator SET current_user_sid=$sid,session_binding=$session; UPDATE plans SET status_code='SECRET_CORRUPT';",("$sid",Sid),("$session",Session));
        var candidates=0;var validated=0;var probes=0;d.Query.CandidatesSelectedForTests=(_,n)=>candidates+=n;d.Query.EntriesValidatedForTests=(_,n)=>validated+=n;
        d.Query.OrdinaryFileProbeForTests=_=>{probes++;throw new Exception("SECRET_PROBE");};
        var before=UnifiedHistoryProfileFilterTests.RawSnapshot(d.Store.DatabasePath);
        Assert.Empty(d.Page(filters:new("plan",PlanId:"foreign")).Items);Assert.Equal(0,candidates);Assert.Equal(0,validated);Assert.Equal(0,probes);
        Assert.Equal(before,UnifiedHistoryProfileFilterTests.RawSnapshot(d.Store.DatabasePath));
    }

    [Fact]
    public void RetentionPrivateEvidenceHasNoNewPredicatesAndRetainsAllSourceCategories()
    {
        using var d=Database();foreach(var type in UnifiedHistoryFilterCodes.SourceTypes)Seed(d,type,type);Seed(d,"legacy","legacy-source");
        d.Query.OrdinaryFileProbeForTests=_=>throw new Exception("SECRET_EARLY_FILE_PROBE");
        d.Query.CandidateCommandForTests=(_,sql,args)=>{Assert.DoesNotContain("$source_type",sql);Assert.DoesNotContain("$plan_id",sql);};
        var page=d.Query.GetOrdinaryEvidencePage(Sid,Session,100,At.AddDays(1),null);
        Assert.Equal(5,page.Items.Count);Assert.Null(page.Filters.SourceType);Assert.Null(page.Filters.PlanId);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void DailyAndWeeklyOwnersReuseOriginalRecurringValidation(bool weekly)
    {
        using var f=new RecurringPlanSetupTestFixture();
        if(!weekly)f.Create("owned",true);
        else
        {
            var r=f.Request("weekly-key");var schedule=RecurringPlanSchedule.CreateWeekly("China Standard Time",new(2026,1,1),new(2026,1,2),new(9,30),new[]{DayOfWeek.Thursday,DayOfWeek.Friday},2,TimeSpan.FromSeconds(30),TimeSpan.FromMinutes(5));
            var snapshot=RecurringSetupIntentSnapshot.CreateForTrustedSetupAdapter("owned",r.IdempotencyKey,RecurringPlanSetupTestFixture.Sid,RecurringPlanSetupTestFixture.Session,f.Now!.Value,r.LeaseValidUntilUtc,schedule,r.MaxRuns,r.MaxTotalDuration,r.LeaseValidUntilUtc,r.OutputDirectory,r.FilenamePrefix);
            Assert.Equal(RecurringSetupIntentResultStatus.Created,new RecurringSetupIntentService(f.Store,()=>f.Now).CreateOrGet(snapshot).Result);
            var selection=RecurringFixedRegionSelectionSnapshot.CreateForTrustedLocalSelectionAdapter("owned",RecurringPlanSetupTestFixture.Sid,RecurringPlanSetupTestFixture.Session,f.Now.Value,"stable-display",new(100,200,1920,1080),new(10,20,640,480),96,96,1920,1080,AuthorizedDisplayOrientation.Landscape,new string('a',64));
            Assert.Equal(RecurringSetupPreparationResultStatus.Prepared,new RecurringSetupPreparationService(f.Store,()=>f.Now).Prepare(selection).Status);
        }
        f.Activate("owned");f.Create("noise",true);f.Activate("noise");
        var id=f.Query.Get("owned",RecurringPlanSetupTestFixture.Sid,RecurringPlanSetupTestFixture.Session)!.PlanId!;
        var noise=f.Query.Get("noise",RecurringPlanSetupTestFixture.Sid,RecurringPlanSetupTestFixture.Session)!.PlanId!;
        var advance=new SqliteRecurringAdvancementTransaction(f.Store);
        Assert.Equal("scheduled",advance.AdvanceOne(id,1,"op",0,RecurringPlanSetupTestFixture.At(0),RecurringPlanSetupTestFixture.At(10)).ResultCode);
        advance.AdvanceOne(noise,1,"noise-op",0,RecurringPlanSetupTestFixture.At(0),RecurringPlanSetupTestFixture.At(10));
        using var writer=new SqliteOrdinaryRecordingHistoryStore(f.Store);var query=new UnifiedHistoryQueryService(f.Store,writer);var before=f.State();
        var item=Assert.Single(query.GetPage(RecurringPlanSetupTestFixture.Sid,RecurringPlanSetupTestFixture.Session,1,new("plan",PlanId:id)).Items);
        Assert.Equal(id,item.PlanId);Assert.Null(item.SourceType);Assert.Equal(before,f.State());
    }

    [Theory]
    [InlineData("ordinary")] [InlineData("plan")]
    public void NewFiltersBindCursorIdentityScopeAndInsertionWatermark(string source)
    {
        using var d=Database();using var other=Database();d.SeedPlan("p");
        foreach(var id in new[]{"a","b","c"}){Seed(d,id);d.SeedOccurrence("p",id);}
        var f=source=="ordinary"?new UnifiedHistoryFilters("ordinary",SourceType:"window"):new("plan",PlanId:"p");var cursor=d.Page(1,f).NextCursor!;
        var changes=source=="ordinary"?new[]{f with{SourceType="display"},f with{SourceType=null},f with{Status="completed"},f with{ProfileId="p"}}:new[]{f with{PlanId="q"},f with{PlanId=null}};
        foreach(var changed in changes.Concat(new[]{f with{CreatedFrom=At},new UnifiedHistoryFilters("all")}))Assert.Throws<UnifiedHistoryArgumentException>(()=>d.Page(1,changed,cursor));
        Assert.Throws<UnifiedHistoryArgumentException>(()=>d.Page(1,f,cursor,sid:"other"));Assert.Throws<UnifiedHistoryArgumentException>(()=>d.Page(1,f,cursor,session:"other"));
        Assert.Throws<UnifiedHistoryArgumentException>(()=>other.Page(1,f,cursor));Assert.Throws<UnifiedHistoryArgumentException>(()=>new UnifiedHistoryQueryService(d.Store,d.Ordinary,true).GetPage(Sid,Session,1,f,cursor));
        Seed(d,"B");Seed(d,"late-old",at:At.AddDays(-1));d.SeedOccurrence("p","B");d.SeedOccurrence("p","late-old",At.AddDays(-1));
        Assert.Equal(new[]{"b","c"},d.Page(100,f,cursor).Items.Select(e=>e.RecordingId??e.OccurrenceId));
    }

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)] [InlineData(3)]
    public void OldCursorOptionalFieldStagesPreserveWatermark(int stage)
    {
        using var d=Database();foreach(var id in new[]{"a","b","c"})Seed(d,id);
        var f=stage==0?new UnifiedHistoryFilters("ordinary"):stage==1?new("ordinary",Status:"completed"):new("ordinary",ProfileId:"Profile-A");
        if(stage>=2){foreach(var id in new[]{"pa","pb","pc"})Seed(d,id,profile:true);}
        var cursor=LegacyCursor(d.Page(1,f).NextCursor!,stage);Seed(d,"late",at:At.AddDays(-1),profile:stage>=2);
        Assert.Equal(stage>=2?new[]{"pb","pc"}:new[]{"b","c"},d.Page(100,f,cursor).Items.Select(e=>e.RecordingId));
        Assert.Throws<UnifiedHistoryArgumentException>(()=>d.Page(1,f with{SourceType="window"},cursor));
    }
    internal static string LegacyCursor(string cursor,int stage=0)
    {
        var body=cursor.Split('.')[0];var json=JsonNode.Parse(Convert.FromBase64String(body.Replace('-','+').Replace('_','/')+new string('=',(4-body.Length%4)%4)))!;
        var filters=json["filters"]!.AsObject();foreach(var key in new[]{"sourceType","planId"}){if(stage==3)filters[key]=null;else filters.Remove(key);}
        if(stage==0)filters.Remove("status");if(stage<=1){filters.Remove("profileId");filters.Remove("profileVersion");}
        body=Convert.ToBase64String(Encoding.UTF8.GetBytes(json.ToJsonString())).TrimEnd('=').Replace('+','-').Replace('/','_');
        return body+"."+Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(body))).ToLowerInvariant();
    }

    [Theory]
    [InlineData("ordinary")] [InlineData("plan")]
    public void ActualLargeFirstAndDeepSeekSqlUsesNewIndexAndBoundsProjection(string source)
    {
        using var d=Database();d.SeedPlan("p");d.SeedPlan("noise");
        if(source=="ordinary")d.Execute("""
            WITH RECURSIVE n(x) AS(SELECT 1 UNION ALL SELECT x+1 FROM n WHERE x<20000)
            INSERT INTO ordinary_recording_history(recording_id,history_sequence,created_at_utc,status_code,source_type,capture_semantics,source_title,requested_duration_seconds,audio_mode,output_path,updated_at_utc)
            SELECT 'r'||printf('%08d',x),x,$at-(x/10000),'failed',CASE WHEN x%100=0 THEN 'window' ELSE 'display' END,'display_capture','Synthetic',15,'none',$path,$at FROM n;
            """,("$at",At.UtcTicks),("$path",Path.Combine(d.Root,"never.mp4")));
        else d.Execute("""
            WITH RECURSIVE n(x) AS(SELECT 1 UNION ALL SELECT x+1 FROM n WHERE x<20000)
            INSERT INTO plan_occurrences(id,plan_id,window_start_utc,window_end_utc,status_code,created_at_utc,updated_at_utc,version)
            SELECT 'o'||printf('%08d',x),CASE WHEN x%100=0 THEN 'p' ELSE 'noise' END,$at,$end,'scheduled',$at-(x/10000),$at,0 FROM n;
            """,("$at",At.UtcTicks),("$end",At.AddMinutes(1).UtcTicks));
        var index=source=="ordinary"?SqliteSchemaV27.TypeIndexName:SqliteSchemaV27.PlanIndexName;var plans=new List<string>();var calls=0;
        d.Query.CandidatesSelectedForTests=(_,n)=>Assert.InRange(n,0,21);d.Query.EntriesValidatedForTests=(_,n)=>Assert.InRange(n,1,20);
        d.Query.CandidateCommandForTests=(_,sql,args)=>
        {
            calls++;Assert.DoesNotContain("OFFSET",sql);Assert.Contains(source=="ordinary"?"source_type COLLATE BINARY=$source_type":"plan_id COLLATE BINARY=$plan_id",sql);
            using var c=d.Store.OpenReadOnlyConnection();using var cmd=c.CreateCommand();cmd.CommandText="EXPLAIN QUERY PLAN "+sql;foreach(var a in args)cmd.Parameters.AddWithValue(a.Key,a.Value);
            using var r=cmd.ExecuteReader();var current=new List<string>();while(r.Read())current.Add(r.GetString(3));plans.AddRange(current);
            Assert.Contains(current,p=>p.Contains("USING INDEX "+index));Assert.DoesNotContain(current,p=>p.Contains("TEMP B-TREE"));output.WriteLine(string.Join(" | ",current));
        };
        var f=source=="ordinary"?new UnifiedHistoryFilters("ordinary",At.AddTicks(-3),At.AddTicks(1),"failed",SourceType:"window"):new("plan",At.AddTicks(-3),At.AddTicks(1),PlanId:"p");
        var seen=new List<string>();string? cursor=null;do{var page=d.Page(20,f,cursor);seen.AddRange(page.Items.Select(e=>e.RecordingId??e.OccurrenceId!));cursor=page.NextCursor;}while(cursor is not null);
        Assert.Equal(200,seen.Count);Assert.Equal(200,seen.Distinct().Count());Assert.True(calls>=10);
        Assert.Contains(plans,p=>p.Contains("created_at_utc=?"));Assert.Contains(plans,p=>p.Contains("created_at_utc<?"));
    }

    [Theory]
    [InlineData("DROP INDEX idx_ordinary_recording_history_type_created;")]
    [InlineData("DROP INDEX idx_plan_history_identity_plan_created;")]
    [InlineData("DROP INDEX idx_ordinary_recording_history_type_created; CREATE INDEX idx_ordinary_recording_history_type_created ON ordinary_recording_history(source_type COLLATE NOCASE,created_at_utc DESC,recording_id COLLATE BINARY ASC);")]
    [InlineData("DROP INDEX idx_plan_history_identity_plan_created; CREATE INDEX idx_plan_history_identity_plan_created ON plan_history_locator(current_user_sid,session_binding,plan_id COLLATE BINARY,created_at_utc ASC,occurrence_id COLLATE BINARY ASC);")]
    [InlineData("UPDATE schema_migrations SET name='wrong' WHERE version=27;")]
    [InlineData("UPDATE schema_migrations SET definition_checksum='0000000000000000000000000000000000000000000000000000000000000000' WHERE version=27;")]
    [InlineData("DELETE FROM schema_migrations WHERE version=27;")]
    public void DamagedNewIndexesAndReceiptFailClosedWithoutRepair(string sql)
    {
        using var d=Database();Seed(d,"r");d.Execute(sql);var before=UnifiedHistoryProfileFilterTests.RawSnapshot(d.Store.DatabasePath);
        Assert.Throws<UnifiedHistoryUnavailableException>(()=>d.Page(filters:new("ordinary",SourceType:"window")));
        Assert.Throws<SqliteOperationalStoreException>(()=>d.Store.Initialize());Assert.Equal(before,UnifiedHistoryProfileFilterTests.RawSnapshot(d.Store.DatabasePath));
    }

    [Fact]
    public void GenuineV26MigrationAddsOnlyTwoIndexesAndReceiptPreservingAllOldEvidence()
    {
        using var d=Database();Seed(d,"r",profile:true);d.SeedPlan("p");d.SeedOccurrence("p","o");var path=Path.Combine(d.Root,"v26.db");
        using(var c=new SqliteConnection($"Data Source={path};Pooling=False"))
        {
            c.Open();using var cmd=c.CreateCommand();foreach(var m in SqliteSchemaCatalog.Migrations.Where(m=>m.Version<=26))
            {cmd.CommandText=m.Definition;cmd.ExecuteNonQuery();cmd.CommandText="INSERT INTO schema_migrations VALUES($v,$n,$c,$at);";cmd.Parameters.AddWithValue("$v",m.Version);cmd.Parameters.AddWithValue("$n",m.Name);cmd.Parameters.AddWithValue("$c",m.Checksum);cmd.Parameters.AddWithValue("$at",1000+m.Version);cmd.ExecuteNonQuery();cmd.Parameters.Clear();}
            cmd.CommandText="ATTACH DATABASE $source AS seed;";cmd.Parameters.AddWithValue("$source",d.Store.DatabasePath);cmd.ExecuteNonQuery();cmd.Parameters.Clear();
            foreach(var table in new[]{"ordinary_recording_history","plans","setup_intents","plan_occurrences"}){cmd.CommandText=$"INSERT INTO {table} SELECT * FROM seed.{table};";cmd.ExecuteNonQuery();}
        }
        var before=OldSnapshot(path);var store=new SqliteOperationalStore(path);store.Initialize();Assert.Equal(before,OldSnapshot(path));var once=UnifiedHistoryProfileFilterTests.RawSnapshot(path);store.Initialize();Assert.Equal(once,UnifiedHistoryProfileFilterTests.RawSnapshot(path));
        using var writer=new SqliteOrdinaryRecordingHistoryStore(store);var query=new UnifiedHistoryQueryService(store,writer);
        Assert.Single(query.GetPage(Sid,Session,1,new("ordinary",SourceType:"window")).Items);Assert.Single(query.GetPage(Sid,Session,1,new("plan",PlanId:"p")).Items);
        Assert.Equal(40,SqliteOperationalStore.CurrentSchemaVersion);Assert.Equal(SqliteSchemaV27.MigrationChecksum,SqliteSchemaCatalog.Migrations.Single(m=>m.Version==27).Checksum);
    }
    private static string OldSnapshot(string path)
    {
        var rows=UnifiedHistoryProfileFilterTests.RawSnapshot(path,schemaThrough:26).Split('\n').Where(l=>!l.StartsWith("schema_migrations:27|")&&!l.StartsWith("schema_migrations:28|")&&!l.StartsWith("schema_migrations:29|"));
        using var c=new SqliteConnection($"Data Source={path};Pooling=False;Mode=ReadOnly");c.Open();using var cmd=c.CreateCommand();
        cmd.CommandText="SELECT name,sql FROM sqlite_master WHERE type IN ('trigger','index') AND name NOT LIKE 'future_window_publication%' AND name NOT LIKE 'idx_future_window_publication%' AND tbl_name NOT LIKE 'future_window_publication%' AND name NOT IN ('idx_setup_intents_pending_output_path','idx_setup_intents_pending_output_template','idx_recurring_profiles_pending_output_template','idx_required_once_setup_pending_output_path') AND name NOT LIKE 'ordinary_bundle_attachment_hash%' AND name NOT LIKE 'idx_ordinary_bundle_attachment_hash%' AND name NOT LIKE 'ordinary_media_hash%' AND name NOT LIKE 'idx_ordinary_media_hash%' AND name NOT LIKE 'ordinary_bundle_identity%' AND name NOT LIKE 'idx_ordinary_bundle_identity%' AND name NOT LIKE 'ordinary_bundle_contents%' AND tbl_name NOT IN ('ordinary_bundle_attachment_hash','ordinary_bundle_attachment_hash_boundary','ordinary_bundle_attachment_hash_items','ordinary_media_hash','ordinary_media_hash_boundary','ordinary_bundle_identity','ordinary_bundle_identity_boundary','ordinary_bundle_identity_items','ordinary_recording_window_targets','ordinary_window_target_boundary','ordinary_bundle_contents','ordinary_bundle_contents_boundary','ordinary_bundle_contents_items','ordinary_publication_anchors','ordinary_recording_bundles','ordinary_recording_targets','ordinary_recording_target_boundary') AND name NOT LIKE '%shared_output_path' AND name NOT LIKE '%planned_output_path' AND name NOT IN ('idx_ordinary_recording_history_type_created','idx_plan_history_identity_plan_created','ordinary_recording_target_initial','ordinary_window_target_initial') ORDER BY name;";
        using var r=cmd.ExecuteReader();var definitions=new List<string>();while(r.Read())definitions.Add(r.GetString(0)+":"+(r.IsDBNull(1)?"":r.GetString(1)));
        return string.Join("\n",rows.Concat(definitions));
    }
}
