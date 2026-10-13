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

// Keep the new large SQLite fixtures out of parallel fixed-deadline writer tests.
[Collection("NonParallel-AgentRecorderDataDir")]
public sealed class UnifiedHistoryProfileFilterTests(ITestOutputHelper output)
{
    internal static UnifiedHistoryTestDatabase Database() => new(Path.Combine(".local-data/temp/task320", Guid.NewGuid().ToString("N")));
    internal static void Seed(UnifiedHistoryTestDatabase d, string id, string? profile = "Profile-A", long? version = 1,
        string status = "completed", DateTimeOffset? at = null) => d.Ordinary.Register(d.Recording(id, at, status == "completed") with
        { Status = status, ProfileId = profile, ProfileVersion = profile is null ? null : version,
          ProfileDigest = profile is null ? null : RecurringFixedRegionProfileVersion.DigestPrefix + new string('a', 64) });

    [Fact]
    public void ExactPersistentIdAndVersionsDoNotRequireAnyProfileDirectoryEntry()
    {
        using var d = Database(); Seed(d, "a"); Seed(d, "b", version: 2); Seed(d, "case", "profile-a"); Seed(d, "other", "Profile-B"); Seed(d, "unbound", null);
        var before = d.Snapshot();
        Assert.Equal(new[] { "a", "b" }, d.Page(filters: new("ordinary", ProfileId: "Profile-A")).Items.Select(e => e.RecordingId));
        var v2 = Assert.Single(d.Page(filters: new("ordinary", ProfileId: "Profile-A", ProfileVersion: 2)).Items);
        Assert.Equal("b", v2.RecordingId); Assert.Equal("Profile-A", v2.ProfileId); Assert.Equal(2L, v2.ProfileVersion);
        Assert.Equal("case", Assert.Single(d.Page(filters: new("ordinary", ProfileId: "profile-a")).Items).RecordingId);
        Assert.Empty(d.Page(filters: new("ordinary", ProfileId: "missing")).Items);
        Assert.Empty(d.Page(filters: new("ordinary", ProfileId: "Profile-A", ProfileVersion: long.MaxValue)).Items);
        Assert.Null(d.Page(filters: new("ordinary", ProfileId: "missing")).NextCursor);
        Assert.Null(d.Page(100, new("ordinary")).Items.Single(e => e.RecordingId == "unbound").ProfileId);
        Assert.Equal(0, d.Scalar("SELECT COUNT(*) FROM fixed_region_profile_directory;")); Assert.Equal(before, d.Snapshot());
    }

    internal static RecurringFixedRegionProfileSpecification Specification(string root) => new(
        AuthorizedScopeTargetType.FixedRegion, RecurringFixedRegionRebindPolicy.ExactMatchOnly,
        AuthorizedCaptureSemantics.DesktopRegion, AuthorizedCoordinateSpace.PhysicalVirtualScreen,
        AuthorizedDisplayIdentityStatus.Resolved, "DISPLAY-SYNTHETIC", new(0,0,1920,1080), new(10,20,640,480),
        96,96,1920,1080, AuthorizedDisplayOrientation.Landscape, new string('a',64), AuthorizedCaptureBackend.FfmpegRegion,
        AuthorizedAudioMode.None, TimeSpan.FromMinutes(2),3,root,"synthetic",AuthorizedOutputConflictPolicy.FailIfExists,
        AuthorizedWakePolicy.NaturalWakeOnly,AuthorizedDesktopRequirement.InteractiveDesktopRequired);

    [Fact]
    public void SameNamesRenameDeletionAndNewCurrentVersionCannotRetargetSnapshots()
    {
        using var d = Database(); var repo = new SqliteRecurringFixedRegionProfileRepository(d.Store);
        var one = repo.CreateVersion1("Profile-A", At, Specification(d.Root));
        repo.CreateVersion1("Profile-B", At, Specification(d.Root));
        var two = repo.CreateNext(one.Reference, Specification(d.Root), At.AddMinutes(1));
        d.Ordinary.Register(d.Recording("v1", completed: true) with { ProfileId=one.ProfileId,ProfileVersion=one.ProfileVersion,ProfileDigest=one.ProfileDigest });
        d.Ordinary.Register(d.Recording("v2", completed: true) with { ProfileId=two.ProfileId,ProfileVersion=two.ProfileVersion,ProfileDigest=two.ProfileDigest });
        Seed(d,"other","Profile-B");
        d.Execute("UPDATE fixed_region_profile_directory SET name='Same name';");
        Assert.Equal(2,d.Page(filters:new("ordinary",ProfileId:"Profile-A")).Items.Count);
        d.Execute("UPDATE fixed_region_profile_directory SET name='Renamed',deleted_at_utc=$at,updated_at_utc=$at WHERE profile_id='Profile-A';",("$at",At.AddHours(1).UtcTicks));
        var before=d.Snapshot();var row=Assert.Single(d.Page(filters:new("ordinary",ProfileId:"Profile-A",ProfileVersion:1)).Items);
        Assert.Equal("v1",row.RecordingId);Assert.Equal(1L,row.ProfileVersion);Assert.Equal(2L,repo.Get("Profile-A",2)!.ProfileVersion);Assert.Equal(before,d.Snapshot());
    }

    [Theory]
    [InlineData(1,null)] [InlineData(100,null)] [InlineData(1,2L)] [InlineData(100,2L)]
    public void ProfileStatusTimeFiltersPrecedeLimitAndBinarySeekHasNoGaps(int limit,long? version)
    {
        using var d=Database(); var expected=new List<string>();
        foreach(var id in Enumerable.Range(0,125).Select(i=>$"r{i:000}").Concat(new[]{"A","a","é","中","\U00010000","\uE000"}))
        {Seed(d,id,version:2,status:"failed");expected.Add(id);Seed(d,"noise-"+id,"Other",2,"failed");}
        Seed(d,"outside-old",version:2,status:"failed",at:At.AddTicks(-1));Seed(d,"outside-new",version:2,status:"failed",at:At.AddTicks(1));Seed(d,"wrong-status",version:2);
        if(version is null){Seed(d,"version1",status:"failed");expected.Add("version1");}else Seed(d,"version1",status:"failed");
        var filters=new UnifiedHistoryFilters("ordinary",At,At.AddTicks(1),"failed","Profile-A",version);var seen=new List<string>();string? cursor=null;
        do {var page=d.Page(limit,filters,cursor);Assert.InRange(page.Items.Count,1,limit);seen.AddRange(page.Items.Select(i=>i.RecordingId!));cursor=page.NextCursor;}while(cursor is not null);
        Assert.Equal(expected.OrderBy(id=>Convert.ToHexString(Encoding.UTF8.GetBytes(id)),StringComparer.Ordinal),seen);Assert.Equal(seen.Count,seen.Distinct().Count());
    }

    [Theory]
    [InlineData("all","p",null)] [InlineData("plan","p",null)] [InlineData("future_window","p",null)]
    [InlineData("ordinary",null,1L)] [InlineData("ordinary","p",0L)] [InlineData("ordinary","p",-1L)]
    [InlineData("ordinary","",null)] [InlineData("ordinary"," p",null)] [InlineData("ordinary","p ",null)]
    [InlineData("ordinary","p\0secret",null)] [InlineData("ordinary","p/q",null)] [InlineData("ordinary","p:q",null)]
    public void CoreAndBothHostScopesRejectInvalidProfileFilters(string source,string? id,long? version)
    {
        using var d=Database();var filters=new UnifiedHistoryFilters(source,ProfileId:id,ProfileVersion:version);
        Assert.Throws<UnifiedHistoryArgumentException>(()=>d.Page(filters:filters));
        Assert.Throws<UnifiedHistoryArgumentException>(()=>new UnifiedHistoryQueryService(d.Store,d.Ordinary,true).GetPage(Sid,Session,20,filters));
    }

    [Fact]
    public void IdBoundsAndSqlLookingIdUseExistingValidationAndParameterizedExactEquality()
    {
        using var d=Database();var id="x' OR 1=1 --";Seed(d,"only",id);Seed(d,"other");
        d.Query.CandidateCommandForTests=(_,sql,args)=>{Assert.DoesNotContain(id,sql);Assert.Contains(args,a=>a.Key=="$profile_id"&&Equals(a.Value,id));Assert.DoesNotContain("LIKE",sql);};
        Assert.Equal("only",Assert.Single(d.Page(filters:new("ordinary",ProfileId:id)).Items).RecordingId);
        d.Query.CandidateCommandForTests=null;
        Assert.Empty(d.Page(filters:new("ordinary",ProfileId:new string('x',128))).Items);
        Assert.Throws<UnifiedHistoryArgumentException>(()=>d.Page(filters:new("ordinary",ProfileId:new string('x',129))));
    }

    [Fact]
    public void CursorsBindProfilesAndKeepLegacyStatuslessCursorsWithoutChangingWatermark()
    {
        using var d=Database();using var other=Database();foreach(var id in new[]{"a","b","c"})Seed(d,id);
        var f=new UnifiedHistoryFilters("ordinary",ProfileId:"Profile-A");var cursor=d.Page(1,f).NextCursor!;
        foreach(var changed in new[]{f with{ProfileId="Other"},f with{ProfileVersion=1},f with{ProfileId=null},f with{Status="completed"},f with{CreatedFrom=At},new UnifiedHistoryFilters("all")})
            Assert.Throws<UnifiedHistoryArgumentException>(()=>d.Page(1,changed,cursor));
        var exact=f with{ProfileVersion=1};var exactCursor=d.Page(1,exact).NextCursor!;Assert.Throws<UnifiedHistoryArgumentException>(()=>d.Page(1,f,exactCursor));
        Assert.Throws<UnifiedHistoryArgumentException>(()=>d.Page(1,exact with{ProfileVersion=2},exactCursor));
        Assert.Throws<UnifiedHistoryArgumentException>(()=>d.Page(1,f,cursor,sid:"other"));Assert.Throws<UnifiedHistoryArgumentException>(()=>d.Page(1,f,cursor,session:"other"));
        Assert.Throws<UnifiedHistoryArgumentException>(()=>other.Page(1,f,cursor));Assert.Throws<UnifiedHistoryArgumentException>(()=>new UnifiedHistoryQueryService(d.Store,d.Ordinary,true).GetPage(Sid,Session,1,f,cursor));
        var old=d.Page(1,new("ordinary")).NextCursor!;var legacy=LegacyCursor(old);
        var statusOnly=new UnifiedHistoryFilters("ordinary",Status:"completed");var oldStatus=LegacyCursor(d.Page(1,statusOnly).NextCursor!,removeStatus:false);
        Seed(d,"B");Seed(d,"late-old",at:At.AddDays(-1));
        Assert.Equal(new[]{"b","c"},d.Page(100,statusOnly,oldStatus).Items.Select(e=>e.RecordingId));
        Assert.Equal(new[]{"b","c"},d.Page(100,new("ordinary"),legacy).Items.Select(e=>e.RecordingId));
        Assert.Equal(new[]{"b","c"},d.Page(100,f,cursor).Items.Select(e=>e.RecordingId));
        Assert.Throws<UnifiedHistoryArgumentException>(()=>d.Page(1,f,legacy));
        Assert.Equal(new[]{"b","c"},d.Page(100,new("ordinary"),old).Items.Select(e=>e.RecordingId));
        Assert.Contains(d.Page(100,f).Items,e=>e.RecordingId=="late-old");
    }
    internal static string LegacyCursor(string cursor,bool removeStatus=true)
    {
        var body=cursor.Split('.')[0];var json=JsonNode.Parse(Convert.FromBase64String(body.Replace('-','+').Replace('_','/')+new string('=',(4-body.Length%4)%4)))!;
        foreach(var key in new[]{"profileId","profileVersion"})json["filters"]!.AsObject().Remove(key);
        if(removeStatus)json["filters"]!.AsObject().Remove("status");
        body=Convert.ToBase64String(Encoding.UTF8.GetBytes(json.ToJsonString())).TrimEnd('=').Replace('+','-').Replace('/','_');
        return body+"."+Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(body))).ToLowerInvariant();
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void SparseLargeFixtureUsesProfileIndexForActualFirstAndDeepSeeks(bool exact)
    {
        using var d=Database();using(var c=d.Store.OpenConnection())using(var cmd=c.CreateCommand())
        {
            cmd.CommandText="""
                WITH RECURSIVE n(x) AS(SELECT 1 UNION ALL SELECT x+1 FROM n WHERE x<20000)
                INSERT INTO ordinary_recording_history(recording_id,history_sequence,created_at_utc,status_code,source_type,capture_semantics,source_title,requested_duration_seconds,audio_mode,output_path,profile_id,profile_version,profile_digest,updated_at_utc)
                SELECT 'r'||printf('%08d',x),x,$at-(x/10000),'failed','display','display_capture','Synthetic',15,'none',$path,
                  CASE WHEN x%100=0 THEN 'Profile-A' ELSE 'Other' END,CASE WHEN x%200=0 THEN 2 ELSE 1 END,$digest,$at FROM n;
                """;
            cmd.Parameters.AddWithValue("$at",At.UtcTicks);cmd.Parameters.AddWithValue("$path",Path.Combine(d.Root,"synthetic.mp4"));cmd.Parameters.AddWithValue("$digest",RecurringFixedRegionProfileVersion.DigestPrefix+new string('a',64));cmd.ExecuteNonQuery();
        }
        var calls=0;var plans=new List<string>();var index=exact?SqliteSchemaV26.VersionIndexName:SqliteSchemaV26.IdIndexName;
        d.Query.CandidatesSelectedForTests=(_,count)=>Assert.InRange(count,0,21);d.Query.EntriesValidatedForTests=(_,count)=>Assert.InRange(count,1,20);
        d.Query.CandidateCommandForTests=(_,sql,args)=>
        {
            calls++;Assert.Contains("profile_id COLLATE BINARY=$profile_id",sql);Assert.Contains("status_code=$status",sql);Assert.DoesNotContain("JOIN",sql);Assert.DoesNotContain("OFFSET",sql);
            using var c=d.Store.OpenReadOnlyConnection();using var cmd=c.CreateCommand();cmd.CommandText="EXPLAIN QUERY PLAN "+sql;foreach(var a in args)cmd.Parameters.AddWithValue(a.Key,a.Value);
            using var r=cmd.ExecuteReader();var current=new List<string>();while(r.Read())current.Add(r.GetString(3));plans.AddRange(current);
            Assert.Contains(current,p=>p.Contains("SEARCH ordinary_recording_history USING INDEX "+index)&&p.Contains("profile_id=?"));
            Assert.DoesNotContain(current,p=>p.Contains("TEMP B-TREE"));
            output.WriteLine(string.Join(" | ",current));
        };
        var f=new UnifiedHistoryFilters("ordinary",At.AddTicks(-3),At.AddTicks(1),"failed","Profile-A",exact?2:null);var seen=new List<string>();string? cursor=null;
        do{var page=d.Page(20,f,cursor);seen.AddRange(page.Items.Select(e=>e.RecordingId!));cursor=page.NextCursor;}while(cursor is not null);
        Assert.Equal(exact?100:200,seen.Count);Assert.Equal(seen.Count,seen.Distinct().Count());Assert.True(calls>=5);
        Assert.Contains(plans,p=>p.Contains("created_at_utc=? AND recording_id>?"));Assert.Contains(plans,p=>p.Contains("created_at_utc<?"));
    }

    [Theory]
    [InlineData("DROP INDEX idx_ordinary_recording_history_profile_created;")]
    [InlineData("DROP INDEX idx_ordinary_recording_history_profile_version_created;")]
    [InlineData("DROP INDEX idx_ordinary_recording_history_profile_created; CREATE INDEX idx_ordinary_recording_history_profile_created ON ordinary_recording_history(profile_id COLLATE NOCASE,created_at_utc DESC,recording_id COLLATE BINARY ASC);")]
    [InlineData("DROP INDEX idx_ordinary_recording_history_profile_version_created; CREATE INDEX idx_ordinary_recording_history_profile_version_created ON ordinary_recording_history(profile_version,profile_id COLLATE BINARY,created_at_utc DESC,recording_id COLLATE BINARY ASC);")]
    [InlineData("UPDATE schema_migrations SET definition_checksum='0000000000000000000000000000000000000000000000000000000000000000' WHERE version=26;")]
    [InlineData("UPDATE schema_migrations SET name='wrong' WHERE version=26;")]
    [InlineData("DELETE FROM schema_migrations WHERE version=26;")]
    public void MissingWrongIndexAndBadReceiptFailClosedWithoutReadRepair(string sql)
    {
        using var d=Database();Seed(d,"r");d.Execute(sql);var before=RawSnapshot(d.Store.DatabasePath);
        Assert.Throws<UnifiedHistoryUnavailableException>(()=>d.Page(filters:new("ordinary",ProfileId:"Profile-A")));
        Assert.Throws<SqliteOperationalStoreException>(()=>d.Store.Initialize());Assert.Equal(before,RawSnapshot(d.Store.DatabasePath));
    }

    [Fact]
    public void GroupedProfileSnapshotValidationRemainsStrictAndNeverRepairsRows()
    {
        using var d=Database();Seed(d,"r");d.Corrupt("UPDATE ordinary_recording_history SET profile_digest=NULL;");var before=RawSnapshot(d.Store.DatabasePath);
        Assert.Throws<UnifiedHistoryUnavailableException>(()=>d.Page(filters:new("ordinary",ProfileId:"Profile-A")));Assert.Equal(before,RawSnapshot(d.Store.DatabasePath));
    }

    [Fact]
    public void GenuineV25UpgradesOnlyIndexesAndReceiptAndPreservesTablesRowsAndOldChecksums()
    {
        using var d=Database();Seed(d,"r",version:2);var path=Path.Combine(d.Root,"v25.db");
        using(var c=new SqliteConnection($"Data Source={path};Pooling=False"))
        {
            c.Open();using var cmd=c.CreateCommand();
            foreach(var m in SqliteSchemaCatalog.Migrations.Where(m=>m.Version<=25))
            {
                cmd.CommandText=m.Definition;cmd.ExecuteNonQuery();cmd.CommandText="INSERT INTO schema_migrations VALUES($v,$n,$c,$at);";
                cmd.Parameters.AddWithValue("$v",m.Version);cmd.Parameters.AddWithValue("$n",m.Name);cmd.Parameters.AddWithValue("$c",m.Checksum);cmd.Parameters.AddWithValue("$at",1000+m.Version);cmd.ExecuteNonQuery();cmd.Parameters.Clear();
            }
            cmd.CommandText="ATTACH DATABASE $source AS seed;";cmd.Parameters.AddWithValue("$source",d.Store.DatabasePath);cmd.ExecuteNonQuery();cmd.Parameters.Clear();cmd.CommandText="INSERT INTO ordinary_recording_history SELECT * FROM seed.ordinary_recording_history;";Assert.Equal(1,cmd.ExecuteNonQuery());
        }
        var before=RawSnapshot(path,old:true);var store=new SqliteOperationalStore(path);store.Initialize();var once=RawSnapshot(path);store.Initialize();Assert.Equal(once,RawSnapshot(path));Assert.Equal(before,RawSnapshot(path,old:true));
        using var writer=new SqliteOrdinaryRecordingHistoryStore(store);var query=new UnifiedHistoryQueryService(store,writer);
        Assert.Equal(2L,Assert.Single(query.GetPage(Sid,Session,1,new("ordinary",ProfileId:"Profile-A",ProfileVersion:2)).Items).ProfileVersion);
        Assert.Equal(41,SqliteOperationalStore.CurrentSchemaVersion);Assert.Equal(SqliteSchemaV26.MigrationChecksum,SqliteSchemaCatalog.Migrations.Single(m=>m.Version==26).Checksum);
    }
    internal static string RawSnapshot(string path,bool old=false,int? schemaThrough=null)
    {
        schemaThrough ??= old ? 25 : null;
        using var c=new SqliteConnection($"Data Source={path};Pooling=False;Mode=ReadOnly");c.Open();var parts=new List<string>();var tables=new List<string>();
        using(var cmd=c.CreateCommand()){cmd.CommandText="SELECT name FROM sqlite_master WHERE type='table' AND name NOT LIKE 'sqlite_%' ORDER BY name;";using var r=cmd.ExecuteReader();while(r.Read())if(schemaThrough is null || r.GetString(0)!="ordinary_publication_anchors" && (schemaThrough>=31 || r.GetString(0)!="ordinary_recording_bundles") && (schemaThrough>=32 || r.GetString(0) is not ("ordinary_recording_targets" or "ordinary_recording_target_boundary")) && (schemaThrough>=33 || !r.GetString(0).StartsWith("ordinary_bundle_contents",StringComparison.Ordinal)) && (schemaThrough>=34 || r.GetString(0) is not ("ordinary_recording_window_targets" or "ordinary_window_target_boundary")) && (schemaThrough>=35 || !r.GetString(0).StartsWith("ordinary_bundle_identity",StringComparison.Ordinal)) && (schemaThrough>=36 || !r.GetString(0).StartsWith("ordinary_media_hash",StringComparison.Ordinal)) && (schemaThrough>=37 || !r.GetString(0).StartsWith("ordinary_bundle_attachment_hash",StringComparison.Ordinal)) && (schemaThrough>=40 || !r.GetString(0).StartsWith("future_window_publication",StringComparison.Ordinal)) && (schemaThrough>=41 || !r.GetString(0).StartsWith("plan_media_publication",StringComparison.Ordinal)))tables.Add(r.GetString(0));}
        foreach(var table in tables)
        {using var cmd=c.CreateCommand();cmd.CommandText="SELECT rowid,* FROM \""+table+"\""+(table=="schema_migrations"&&schemaThrough is not null?" WHERE version<="+schemaThrough.Value:"")+" ORDER BY rowid;";using var r=cmd.ExecuteReader();while(r.Read())parts.Add(table+":"+string.Join("|",Enumerable.Range(0,r.FieldCount).Select(i=>r.GetValue(i).ToString())));}
        using(var cmd=c.CreateCommand()){cmd.CommandText="SELECT name,sql FROM sqlite_master WHERE type='table' ORDER BY name;";using var r=cmd.ExecuteReader();while(r.Read())if(schemaThrough is null || r.GetString(0)!="ordinary_publication_anchors" && (schemaThrough>=31 || r.GetString(0)!="ordinary_recording_bundles") && (schemaThrough>=32 || r.GetString(0) is not ("ordinary_recording_targets" or "ordinary_recording_target_boundary")) && (schemaThrough>=33 || !r.GetString(0).StartsWith("ordinary_bundle_contents",StringComparison.Ordinal)) && (schemaThrough>=34 || r.GetString(0) is not ("ordinary_recording_window_targets" or "ordinary_window_target_boundary")) && (schemaThrough>=35 || !r.GetString(0).StartsWith("ordinary_bundle_identity",StringComparison.Ordinal)) && (schemaThrough>=36 || !r.GetString(0).StartsWith("ordinary_media_hash",StringComparison.Ordinal)) && (schemaThrough>=37 || !r.GetString(0).StartsWith("ordinary_bundle_attachment_hash",StringComparison.Ordinal)) && (schemaThrough>=40 || !r.GetString(0).StartsWith("future_window_publication",StringComparison.Ordinal)) && (schemaThrough>=41 || !r.GetString(0).StartsWith("plan_media_publication",StringComparison.Ordinal)))parts.Add(r.GetString(0)+":"+r.GetString(1));}
        return string.Join("\n",parts);
    }
}
