using System.Net;
using System.Text.Json;
using AgentRecorder.Api;
using AgentRecorder.Core;
using AgentRecorder.Core.Automation;
using AgentRecorder.Infrastructure;
using AgentRecorder.Logging;
using AgentRecorder.Persistence;
using Microsoft.Data.Sqlite;
using Xunit;
using Xunit.Abstractions;
using static AgentRecorder.Tests.UnifiedHistoryTestDatabase;

namespace AgentRecorder.Tests;

[Collection("NonParallel-AgentRecorderDataDir")]
public sealed class RequiredOncePendingOutputUseTests(ITestOutputHelper output)
{
    private static RetentionSharedOutputPathTests.Fixture Fixture(bool headless=false) => new(headless,
        Path.Combine(TestHelper.ProjectRoot,".local-data/temp/task345","Ä-"+Guid.NewGuid().ToString("N")));
    private static void Fixed(RetentionSharedOutputPathTests.Fixture f,string path,string id="SECRET_FIXED")
    {
        var snapshot=StandingSetupIntentSnapshot.CreateForTrustedSetupAdapter(id,id+"-key","SECRET_SID","SECRET_SESSION",At,
            At.AddMinutes(5),At.AddSeconds(10),At.AddSeconds(20),At.AddSeconds(80),TimeSpan.FromSeconds(30),At.AddHours(1),
            Path.GetDirectoryName(path)!,Path.GetFileName(path));
        Assert.Equal(StandingSetupIntentResultStatus.Created,new StandingSetupIntentService(f.Db.Store,()=>At).CreateOrGet(snapshot).Result);
    }
    private static void Recurring(RetentionSharedOutputPathTests.Fixture f,string directory,string prefix="中-Ä-a_b",string id="SECRET_RECURRING")
    {
        var schedule=RecurringPlanSchedule.CreateDaily(TimeZoneInfo.Utc.Id,new(2026,10,5),new(2026,10,6),new(1,0),2,TimeSpan.FromSeconds(30),TimeSpan.FromMinutes(5));
        var snapshot=RecurringSetupIntentSnapshot.CreateForTrustedSetupAdapter(id,id+"-key","SECRET_SID","SECRET_SESSION",At,
            At.AddDays(2),schedule,2,TimeSpan.FromSeconds(60),At.AddDays(2),directory,prefix);
        Assert.Equal(RecurringSetupIntentResultStatus.Created,new RecurringSetupIntentService(f.Db.Store,()=>At).CreateOrGet(snapshot).Result);
    }
    private static RecurringFixedRegionProfileSpecification Spec(string directory,string prefix) => new(
        AuthorizedScopeTargetType.FixedRegion,RecurringFixedRegionRebindPolicy.ExactMatchOnly,AuthorizedCaptureSemantics.DesktopRegion,
        AuthorizedCoordinateSpace.PhysicalVirtualScreen,AuthorizedDisplayIdentityStatus.Resolved,"DISPLAY-SYNTHETIC",new(0,0,1920,1080),new(10,20,640,480),
        96,96,1920,1080,AuthorizedDisplayOrientation.Landscape,new string('a',64),AuthorizedCaptureBackend.FfmpegRegion,AuthorizedAudioMode.None,
        TimeSpan.FromMinutes(2),3,directory,prefix,AuthorizedOutputConflictPolicy.FailIfExists,AuthorizedWakePolicy.NaturalWakeOnly,AuthorizedDesktopRequirement.InteractiveDesktopRequired);
    private static RecurringFixedRegionProfileVersion Profile(RetentionSharedOutputPathTests.Fixture f,string directory,string prefix="中-Ä-a_b",string id="SECRET_PROFILE") =>
        new SqliteRecurringFixedRegionProfileRepository(f.Db.Store).CreateVersion1(id,At,Spec(directory,prefix));
    private static string Render(string prefix="中-Ä-a_b",char digest='a') =>
        RecurringFixedRegionProfileOutput.RenderOutputFileName(prefix,RecurringOccurrenceIdentity.Prefix+new string(digest,64),At);
    private static string DirectoryOf(RetentionSharedOutputPathTests.Fixture f)=>Path.GetDirectoryName(f.PathFor())!;
    private static void AssertProtected(RetentionSharedOutputPathTests.Fixture f,string reason)
    {
        var before=f.Snapshot();var page=f.Page();var item=Assert.Single(page.Items);Assert.Equal(reason,item.ReasonCode);
        Assert.Equal("protected",item.Evaluation);Assert.True(item.AgeCandidate);Assert.Null(item.ObservedFileBytes);
        Assert.Equal(0,page.ObservedCandidateBytes);Assert.Equal(1,page.UnknownCandidateFileCount);f.NoProbes();Assert.Equal(before,f.Snapshot());
    }

    private static RequiredOncePlanSetupReadback Required(RetentionSharedOutputPathTests.Fixture f,string path,string state="region_selection_pending",string id="SECRET_REQUIRED",string? directory=null,DateTimeOffset? requested=null)
    {
        var at=requested??At;var repo=new SqliteRequiredOncePlanSetupRepository(f.Db.Store);
        var request=new RequiredOncePlanSetupRequestSnapshot(id,id+"-key","SECRET_SID","SECRET_SESSION",at,at.AddMinutes(12),at.AddMinutes(10),at.AddMinutes(11),at.AddMinutes(13),TimeSpan.FromSeconds(30),directory??Path.GetDirectoryName(path)!,Path.GetFileName(path));
        var initial=repo.CreateOrGet(request,state=="initial_expired"?at.AddMinutes(12):at);Assert.Equal(state=="initial_expired"?RequiredOncePlanSetupWriteStatus.Expired:RequiredOncePlanSetupWriteStatus.Created,initial.Status);
        if(state is "creation_approval_pending" or "scheduled")
        {
            var selection=new RequiredOncePlanRegionSelection(at.AddSeconds(1),"DISPLAY-SYNTHETIC",new(0,0,1920,1080),new(10,20,640,480),96,96,1920,1080,AuthorizedDisplayOrientation.Landscape,new string('a',64));
            initial=repo.SaveSelection(id,request.CurrentUserSid,request.SessionBinding,selection,at.AddSeconds(1));Assert.Equal(RequiredOncePlanSetupWriteStatus.Updated,initial.Status);
            if(state=="scheduled"){initial=repo.Activate(id,request.CurrentUserSid,request.SessionBinding,initial.State!.Version,"SECRET_APPROVAL",at.AddSeconds(2));Assert.Equal(RequiredOncePlanSetupWriteStatus.Updated,initial.Status);}
        }
        else if(state is "rejected" or "expired"){initial=repo.SetTerminal(id,request.CurrentUserSid,request.SessionBinding,state,state=="rejected"?"plan_creation_rejected_by_user":"setup_intent_expired",at.AddSeconds(1));Assert.Equal(state=="expired"?RequiredOncePlanSetupWriteStatus.Expired:RequiredOncePlanSetupWriteStatus.Updated,initial.Status);}
        Assert.Equal(state=="initial_expired"?"expired":state,initial.State!.StatusCode);return initial.State;
    }
    private static void Copies(RetentionSharedOutputPathTests.Fixture f,int copies)
    {
        using var c=f.Db.Store.OpenReadOnlyConnection();using var cmd=c.CreateCommand();cmd.CommandText="PRAGMA table_info(required_once_setup_intents);";
        var columns=new List<string>();using(var reader=cmd.ExecuteReader())while(reader.Read())columns.Add(reader.GetString(1));
        var projected=columns.Select(n=>n=="setup_intent_id"?"'copy-'||x":n=="idempotency_key"?"'copy-key-'||x":"seed.\""+n+"\"");
        f.Db.Execute("WITH RECURSIVE n(x) AS(SELECT 1 UNION ALL SELECT x+1 FROM n WHERE x<$copies) INSERT INTO required_once_setup_intents SELECT "+string.Join(",",projected)+" FROM required_once_setup_intents seed,n WHERE seed.setup_intent_id='SECRET_REQUIRED';",("$copies",copies));
    }
    private static void Damage(RetentionSharedOutputPathTests.Fixture f,string field,object value)
    {
        var text=value is string;f.Db.Corrupt("UPDATE required_once_setup_intents SET "+field+"="+(text?"CAST($bad AS TEXT)":"$bad")+";",("$bad",text?System.Text.Encoding.UTF8.GetBytes((string)value):value));
    }
    private static string FullSnapshot(RetentionSharedOutputPathTests.Fixture f,int? through=null)
    {
        using var c=new SqliteConnection($"Data Source={f.Db.Store.DatabasePath};Pooling=False;Mode=ReadOnly");c.Open();SqliteOutputPathCollation.Register(c);using var cmd=c.CreateCommand();cmd.CommandText="SELECT setup_intent_id,typeof(output_directory),hex(CAST(output_directory AS BLOB)),typeof(frozen_file_name),hex(CAST(frozen_file_name AS BLOB)) FROM required_once_setup_intents ORDER BY setup_intent_id;";
        var result=new List<string>{UnifiedHistoryProfileFilterTests.RawSnapshot(f.Db.Store.DatabasePath,schemaThrough:through)};
        using(var reader=cmd.ExecuteReader())while(reader.Read())result.Add(string.Join("|",Enumerable.Range(0,reader.FieldCount).Select(i=>reader.GetString(i))));
        cmd.CommandText="SELECT name,sql FROM sqlite_master ORDER BY name;";using(var reader=cmd.ExecuteReader())while(reader.Read())if(through!=38||reader.GetString(0)!=SqliteSchemaV39.Paths[0].Index)result.Add(reader.GetString(0)+":"+(reader.IsDBNull(1)?"":reader.GetString(1)));
        return string.Join("\n",result);
    }
    [Theory]
    [InlineData("region_selection_pending")][InlineData("creation_approval_pending")][InlineData("scheduled")][InlineData("rejected")][InlineData("expired")][InlineData("initial_expired")]
    public void FormalRepositoryStatesKeepForeignFixedUseWithoutCapture(string state)
    {
        using var f=Fixture();Required(f,f.PathFor(),state);f.Seed();Assert.Equal(state=="scheduled"?1:0,f.Db.Scalar("SELECT COUNT(*) FROM required_once_authorized_specs;"));Assert.Equal(0,f.Db.Scalar("SELECT COUNT(*) FROM recording_runs;"));
        var before=FullSnapshot(f);var evidence=Assert.Single(f.Query.GetOrdinaryEvidencePage(Sid,Session,1,f.Before,null).Items);Assert.Equal(PendingOutputUseState.SetupExact,evidence.PendingOutputUse);
        AssertProtected(f,state=="scheduled"?"planned_output_path":"setup_output_path");Assert.Equal(before,FullSnapshot(f));
    }
    [Theory]
    [InlineData(false,false)][InlineData(true,false)][InlineData(false,true)][InlineData(true,true)]
    public void CaseAliasesAndBothTrailingDirectoryFormsUseCompleteLiteralName(bool tail,bool alias)
    {
        using var f=Fixture();var name="Ä-中文%_[x].mp4";Required(f,f.PathFor(name),directory:DirectoryOf(f)+(tail?"\\":""));f.Seed(path:alias?f.PathFor(name).ToUpperInvariant():f.PathFor(name));AssertProtected(f,"setup_output_path");
    }
    [Fact]
    public void RootFormsDeduplicateAndScopeStillOwnsRootCandidate()
    {
        using var f=Fixture();var root=Path.GetPathRoot(f.Db.Root)!;var path=root+"required.mp4";Required(f,path);f.Seed(path:path);var calls=0;
        f.Query.RetentionPendingOutputCommandForTests=(_,_,_,args)=>{calls++;Assert.Single(args);};
        Assert.Equal(PendingOutputUseState.SetupExact,Assert.Single(f.Query.GetOrdinaryEvidencePage(Sid,Session,1,f.Before,null).Items).PendingOutputUse);
        Assert.Equal("outside_videos",Assert.Single(f.Page().Items).ReasonCode);Assert.Equal(2,calls);f.NoProbes();
    }
    [Theory]
    [InlineData("different_name")][InlineData("name_nfd")][InlineData("directory_nfd")][InlineData("videos2")][InlineData("child")][InlineData("drive")][InlineData("pattern")]
    public void UnrelatedRawDirectoriesAndValidDifferentNamesDoNotProtect(string kind)
    {
        using var f=Fixture();var name="Ä-中文%_[x].mp4";var path=f.PathFor(name);var directory=DirectoryOf(f);var other=kind switch{
            "directory_nfd"=>directory.Replace("Ä","A\u0308"),"videos2"=>directory+"2","child"=>directory+"\\child","drive"=>(directory[0]=='Z'?'Y':'Z')+directory[1..],_=>directory};
        Required(f,path,directory:other);var candidate=kind switch{"different_name"=>"different.mp4","name_nfd"=>name.Replace("Ä","A\u0308"),"pattern"=>"Ä-中文-other.mp4",_=>name};f.Seed(path:f.PathFor(candidate));var before=FullSnapshot(f);
        Assert.Equal("age_rule_match",Assert.Single(f.Page().Items).ReasonCode);Assert.Single(f.Files.Lengths);Assert.Equal(before,FullSnapshot(f));
    }
    [Fact]
    public void FutureForeignRequestOutsidePageAndDateFiltersStillProtectsBothPages()
    {
        using var f=Fixture();f.Seed("a",f.PathFor("a.mp4"));f.Seed("b",f.PathFor("b.mp4"));Required(f,f.PathFor("b.mp4"),requested:At.AddDays(2));
        var first=f.Page(1);Assert.Equal("age_rule_match",Assert.Single(first.Items).ReasonCode);f.Files.Attributes.Clear();f.Files.Lengths.Clear();f.Files.Observations.Clear();f.Files.ProbeAttributes.Clear();
        var next=f.Page(1,first.NextCursor);Assert.Equal("ordinary:b",Assert.Single(next.Items).EntryId);Assert.Equal("setup_output_path",next.Items[0].ReasonCode);Assert.Null(next.NextCursor);f.NoProbes();
    }
    public static IEnumerable<object[]> NulCases => new[]{"frozen_file_name","output_directory"}.SelectMany(field=>new[]{"start","middle","after"}.SelectMany(pos=>new[]{3,1000000}.SelectMany(size=>new[]{false,true}.Select(match=>new object[]{field,pos,size,match}))));
    [Theory,MemberData(nameof(NulCases))]
    public void CompleteNulEvidenceIsNotInterpretedAsTruncatedText(string field,string position,int suffix,bool looksMatching)
    {
        using var f=Fixture();var name="Ä-required.mp4";Required(f,f.PathFor(name));var basis=field=="output_directory"?DirectoryOf(f)+(looksMatching?"":"2"):name;
        var bad=position=="start"?"\0"+basis+new string('x',suffix):position=="middle"?basis.Insert(basis.Length/2,"\0")+new string('x',suffix):basis+"\0"+new string('x',suffix);
        Damage(f,field,bad);var selected=field=="frozen_file_name";
        using(var c=f.Db.Store.OpenReadOnlyConnection())using(var cmd=c.CreateCommand())
        {
            cmd.CommandText=$"SELECT typeof({field}),length(CAST({field} AS BLOB)),instr({field},char(0)),length({field}),{field} FROM required_once_setup_intents;";
            using(var reader=cmd.ExecuteReader()){Assert.True(reader.Read());Assert.Equal("text",reader.GetString(0));Assert.Equal(System.Text.Encoding.UTF8.GetByteCount(bad),reader.GetInt64(1));Assert.Equal(bad.IndexOf('\0')+1,reader.GetInt64(2));Assert.Equal(bad.IndexOf('\0'),reader.GetInt64(3));Assert.Equal(bad,reader.GetString(4));}
            if(field=="output_directory")
            {
                cmd.CommandText="SELECT count(*) FROM required_once_setup_intents INDEXED BY "+SqliteSchemaV39.Paths[0].Index+" WHERE output_directory COLLATE "+SqliteOutputPathCollation.Name+"=$dir;";cmd.Parameters.AddWithValue("$dir",DirectoryOf(f));selected=(long)cmd.ExecuteScalar()! != 0;Assert.False(selected);
                output.WriteLine($"Actual original raw indexed NUL-directory selected={selected}, position={position}, suffix={suffix}, looksMatching={looksMatching}");
            }
        }
        f.Seed(path:f.PathFor(looksMatching?name:"different.mp4"));var before=FullSnapshot(f);var evidence=Assert.Single(f.Query.GetOrdinaryEvidencePage(Sid,Session,1,f.Before,null).Items);
        Assert.Equal(selected?PendingOutputUseState.Unknown:PendingOutputUseState.None,evidence.PendingOutputUse);
        Assert.Equal(selected?"pending_output_use_unknown":"age_rule_match",Assert.Single(f.Page().Items).ReasonCode);if(selected)f.NoProbes();else Assert.Single(f.Files.Lengths);Assert.Equal(before,FullSnapshot(f));
    }
    [Theory]
    [InlineData("blob")][InlineData("blob_nul")][InlineData("oversized")][InlineData("empty")][InlineData("slash")][InlineData("trailing_dot")]
    public void SelectedAbnormalNameIsUnknownAndNeverMaterializedAsValidUse(string kind)
    {
        using var f=Fixture();Required(f,f.PathFor());object value=kind switch{"blob"=>new byte[]{97,98},"blob_nul"=>new byte[]{97,0,98},"oversized"=>new string('a',1000000),"empty"=>"","slash"=>"bad/name.mp4",_=>"bad.mp4."};Damage(f,"frozen_file_name",value);f.Seed();var before=FullSnapshot(f);AssertProtected(f,"pending_output_use_unknown");Assert.Equal(before,FullSnapshot(f));
    }
    [Theory]
    [InlineData("blob")][InlineData("oversized")]
    public void AbnormalUnrelatedDirectoryNeverExpandsTheBatch(string kind)
    {
        using var f=Fixture();Required(f,f.PathFor());Damage(f,"output_directory",kind=="blob"?(object)System.Text.Encoding.UTF8.GetBytes(DirectoryOf(f)):DirectoryOf(f)+new string('x',1000000));f.Seed();var before=FullSnapshot(f);
        Assert.Equal("age_rule_match",Assert.Single(f.Page().Items).ReasonCode);Assert.Single(f.Files.Lengths);Assert.Equal(before,FullSnapshot(f));
    }
    [Theory]
    [InlineData(500,false)][InlineData(501,false)][InlineData(500,true)][InlineData(501,true)]
    public void FourSourcesShareOneLimitIncludingDifferentNamesAndBadSelectedRows(int count,bool unknown)
    {
        using var f=Fixture();Required(f,f.PathFor("different.mp4"));if(unknown)Damage(f,"frozen_file_name","different.mp4\0bad");Copies(f,count-4);Fixed(f,f.PathFor("another.mp4"));Recurring(f,DirectoryOf(f));Profile(f,DirectoryOf(f));f.Seed("first",f.PathFor());f.Seed("second",f.PathFor(Render()));
        var before=FullSnapshot(f);if(count==501)Assert.Throws<RetentionPreviewUnavailableException>(()=>f.Page());else Assert.Equal(unknown?new[]{"pending_output_use_unknown","pending_output_use_unknown"}:new[]{"age_rule_match","planned_output_template"},f.Page().Items.Select(i=>i.ReasonCode));
        if(count==501||unknown)f.NoProbes();else Assert.Single(f.Files.Lengths);Assert.Equal(before,FullSnapshot(f));
    }
    [Theory]
    [InlineData(false)][InlineData(true)]
    public void NewExactUseWinsAcrossOriginalUnknownAndProfileTemplate(bool reverse)
    {
        using var f=Fixture();var name=Render();if(reverse){Recurring(f,DirectoryOf(f));f.Db.Corrupt("UPDATE setup_intents SET recurring_filename_prefix=NULL;");Profile(f,DirectoryOf(f));Required(f,f.PathFor(name));}else{Required(f,f.PathFor(name));Recurring(f,DirectoryOf(f));Profile(f,DirectoryOf(f));f.Db.Corrupt("UPDATE setup_intents SET recurring_filename_prefix=NULL;");}
        f.Seed("exact",f.PathFor(name));f.Seed("other",f.PathFor(Render(digest:'b')));var page=f.Page();Assert.Equal(new[]{"setup_output_path","pending_output_use_unknown"},page.Items.Select(i=>i.ReasonCode));f.NoProbes();
    }
    [Fact]
    public async Task ExistingNestedSharedAndFrozenPriorityStaysBeforeNewExactUse()
    {
        using var f=Fixture();Required(f,f.PathFor());await RetentionPlannedOutputPathTests.Purpose(f,"future",path:f.PathFor());f.Seed();AssertProtected(f,"planned_output_path");f.Seed("foreign",f.PathFor(),"failed",f.Before.AddDays(1));AssertProtected(f,"shared_output_path");f.Db.Execute("UPDATE ordinary_recording_history SET nested_role='outer' WHERE recording_id='target';");AssertProtected(f,"nested_relationship");
    }
    [Theory]
    [InlineData(false)][InlineData(true)]
    public void HundredCandidatesUseDeduplicatedIndexedBatch(bool distinctDirectories)
    {
        using var f=Fixture();for(var i=0;i<100;i++){var path=f.PathFor((distinctDirectories?"d"+i+"\\":"")+"r"+i+".mp4");if(i==0||distinctDirectories)Required(f,path,id:distinctDirectories?"SECRET_REQUIRED"+i:"SECRET_REQUIRED");f.Seed("r"+i.ToString("000"),path);}
        var calls=0;f.Query.RetentionPendingOutputCommandForTests=(c,t,sql,args)=>{calls++;Assert.Equal(distinctDirectories?200:2,args.Count);Assert.Contains("LIMIT 501",sql);Assert.DoesNotContain("LIKE",sql);using var cmd=c.CreateCommand();cmd.Transaction=t;cmd.CommandText="EXPLAIN QUERY PLAN "+sql;foreach(var arg in args)cmd.Parameters.AddWithValue(arg.Key,arg.Value);using var reader=cmd.ExecuteReader();var plans=new List<string>();while(reader.Read())plans.Add(reader.GetString(3));foreach(var p in SqliteSchemaV38.Paths.Concat(SqliteSchemaV39.Paths))Assert.Contains(plans,s=>s.Contains("SEARCH uses USING")&&s.Contains(p.Index));Assert.DoesNotContain(plans,p=>p.StartsWith("SCAN uses",StringComparison.Ordinal));output.WriteLine(sql);output.WriteLine(string.Join(" | ",plans));};
        var before=FullSnapshot(f);var page=f.Page(100);Assert.Equal(1,calls);Assert.Equal(100,page.Items.Count);Assert.Equal(distinctDirectories?100:1,page.Items.Count(i=>i.ReasonCode=="setup_output_path"));if(distinctDirectories)f.NoProbes();else Assert.Equal(99,f.Files.Lengths.Count);Assert.Equal(before,FullSnapshot(f));
    }
    [Theory]
    [InlineData(false)][InlineData(true)]
    public async Task WalMutationAfterPageSnapshotDoesNotMixNewUsesAndAnchors(bool insert)
    {
        using var f=Fixture();if(!insert)Required(f,f.PathFor());f.Seed();using var entered=new ManualResetEventSlim();using var release=new ManualResetEventSlim();f.Query.EntriesValidatedForTests=(_,_)=>{entered.Set();Assert.True(release.Wait(TimeSpan.FromSeconds(10)));};
        var pending=Task.Run(()=>f.Page());try{Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));if(insert)Required(f,f.PathFor());else f.Db.Corrupt("DELETE FROM required_once_setup_intents;");}finally{release.Set();}
        Assert.Equal(insert?"age_rule_match":"setup_output_path",Assert.Single((await pending).Items).ReasonCode);f.Query.EntriesValidatedForTests=null;f.Files.Attributes.Clear();f.Files.Lengths.Clear();f.Files.Observations.Clear();f.Files.ProbeAttributes.Clear();Assert.Equal(insert?"setup_output_path":"age_rule_match",Assert.Single(f.Page().Items).ReasonCode);if(insert)f.NoProbes();
    }
    public static IEnumerable<object[]> SchemaFaults => new[]{
        "DROP INDEX idx_required_once_setup_pending_output_path;",
        "DROP INDEX idx_required_once_setup_pending_output_path; CREATE INDEX idx_required_once_setup_pending_output_path ON required_once_setup_intents(frozen_file_name COLLATE AGENTRECORDER_PATH_ORDINAL_IGNORE_CASE_V1, output_directory COLLATE AGENTRECORDER_PATH_ORDINAL_IGNORE_CASE_V1);",
        "DROP INDEX idx_required_once_setup_pending_output_path; CREATE INDEX idx_required_once_setup_pending_output_path ON required_once_setup_intents(output_directory COLLATE NOCASE,frozen_file_name COLLATE NOCASE);",
        "DROP INDEX idx_required_once_setup_pending_output_path; CREATE INDEX idx_required_once_setup_pending_output_path ON required_once_setup_intents(output_directory COLLATE AGENTRECORDER_PATH_ORDINAL_IGNORE_CASE_V1);",
        "DROP INDEX idx_required_once_setup_pending_output_path; CREATE UNIQUE INDEX idx_required_once_setup_pending_output_path ON required_once_setup_intents(output_directory COLLATE AGENTRECORDER_PATH_ORDINAL_IGNORE_CASE_V1,frozen_file_name COLLATE AGENTRECORDER_PATH_ORDINAL_IGNORE_CASE_V1);",
        "DROP INDEX idx_required_once_setup_pending_output_path; CREATE INDEX idx_required_once_setup_pending_output_path ON required_once_setup_intents(output_directory COLLATE AGENTRECORDER_PATH_ORDINAL_IGNORE_CASE_V1,frozen_file_name COLLATE AGENTRECORDER_PATH_ORDINAL_IGNORE_CASE_V1) WHERE status_code='region_selection_pending';",
        "UPDATE schema_migrations SET name='wrong' WHERE version=39;", "DELETE FROM schema_migrations WHERE version=39;",
        "UPDATE schema_migrations SET definition_checksum='0000000000000000000000000000000000000000000000000000000000000000' WHERE version=39;",
    }.Select(s=>new object[]{s});
    [Theory,MemberData(nameof(SchemaFaults))]
    public void V39DamageFailsClosedWithoutReadRepair(string sql)
    {using var f=Fixture();Required(f,f.PathFor());f.Seed();f.Db.Execute(sql);var before=FullSnapshot(f);Assert.Throws<RetentionPreviewUnavailableException>(()=>f.Page());Assert.Throws<SqliteOperationalStoreException>(()=>f.Db.Store.Initialize());f.NoProbes();Assert.Equal(before,FullSnapshot(f));}
    [Fact]
    public void GenuineV38UpgradeIsOnlyOneIndexAndReceiptThenIdempotent()
    {
        using var f=Fixture();Required(f,f.PathFor());Fixed(f,f.PathFor());Recurring(f,DirectoryOf(f));Profile(f,DirectoryOf(f));f.Seed();var path=Path.Combine(f.Db.Root,"v38.db");
        using(var c=new SqliteConnection($"Data Source={path};Pooling=False")){c.Open();SqliteOutputPathCollation.Register(c);using var cmd=c.CreateCommand();foreach(var m in SqliteSchemaCatalog.Migrations.Where(m=>m.Version<=38)){cmd.CommandText=m.Definition;cmd.ExecuteNonQuery();cmd.CommandText="INSERT INTO schema_migrations VALUES($v,$n,$c,$at);";cmd.Parameters.AddWithValue("$v",m.Version);cmd.Parameters.AddWithValue("$n",m.Name);cmd.Parameters.AddWithValue("$c",m.Checksum);cmd.Parameters.AddWithValue("$at",1000+m.Version);cmd.ExecuteNonQuery();cmd.Parameters.Clear();}cmd.CommandText="ATTACH DATABASE $seed AS seed;";cmd.Parameters.AddWithValue("$seed",f.Db.Store.DatabasePath);cmd.ExecuteNonQuery();cmd.Parameters.Clear();foreach(var table in new[]{"required_once_setup_intents","setup_intents","recurring_fixed_region_profile_versions","ordinary_recording_history"}){cmd.CommandText="INSERT INTO "+table+" SELECT * FROM seed."+table+";";cmd.ExecuteNonQuery();}}
        string Snapshot(){using var c=new SqliteConnection($"Data Source={path};Pooling=False;Mode=ReadOnly");c.Open();SqliteOutputPathCollation.Register(c);using var cmd=c.CreateCommand();cmd.CommandText="SELECT name,sql FROM sqlite_master WHERE tbl_name NOT LIKE 'future_window_publication%' AND name NOT LIKE 'future_window_publication%' AND name NOT LIKE 'idx_future_window_publication%' AND tbl_name NOT LIKE 'plan_media_publication%' AND name NOT LIKE 'plan_media_publication%' AND name NOT LIKE 'idx_plan_media_publication%' AND name!='idx_required_once_setup_pending_output_path' ORDER BY name;";var lines=new List<string>{UnifiedHistoryProfileFilterTests.RawSnapshot(path,schemaThrough:38)};using var reader=cmd.ExecuteReader();while(reader.Read())lines.Add(reader.GetString(0)+":"+(reader.IsDBNull(1)?"":reader.GetString(1)));return string.Join("\n",lines);}
        var before=Snapshot();var store=new SqliteOperationalStore(path);store.Initialize();Assert.Equal(before,Snapshot());var once=UnifiedHistoryProfileFilterTests.RawSnapshot(path);store.Initialize();Assert.Equal(once,UnifiedHistoryProfileFilterTests.RawSnapshot(path));using var verify=store.OpenReadOnlyConnection();using var check=verify.CreateCommand();check.CommandText="PRAGMA integrity_check;";Assert.Equal("ok",check.ExecuteScalar());check.CommandText="SELECT count(*) FROM schema_migrations WHERE version=39;";Assert.Equal(1L,check.ExecuteScalar());Assert.Single(SqliteSchemaV39.Indexes);Assert.Equal(41,SqliteOperationalStore.CurrentSchemaVersion);
    }
    [Theory]
    [InlineData(false,"exact")][InlineData(true,"exact")][InlineData(true,"unknown")][InlineData(false,"overflow")][InlineData(true,"schema")]
    public async Task AuthenticatedApiUsesExistingReasonsAndNeverLeaksSourceFacts(bool headless,string kind)
    {
        using var f=Fixture(headless);Required(f,f.PathFor());if(kind=="unknown")Damage(f,"frozen_file_name","different.mp4\0"+new string('x',1000000));if(kind=="overflow")Copies(f,500);f.Seed();if(kind=="schema")f.Db.Execute("DROP INDEX idx_required_once_setup_pending_output_path;");var before=FullSnapshot(f);var prior=Environment.GetEnvironmentVariable("AGENT_RECORDER_DATA_DIR");ApiServer? server=null;
        try{Environment.SetEnvironmentVariable("AGENT_RECORDER_DATA_DIR",f.Db.Root);ApiKeyAuth.InitializeForTesting(f.Db.Root);var audit=new RetentionSharedOutputPathTests.QuietAudit();var engine=new RecordingEngine(audit);var tray=new ThrowingTray();engine.SetTray(tray);engine.BackendFactory=_=>throw new InvalidOperationException("No capture permitted.");server=new ApiServer(engine,audit,tray,null,null,null,null,null,null,null,null,null,0,retentionPreviewGateway:new SqliteRetentionPreviewGateway(f.Service,()=>(Sid,Session)));server.Start();using var client=new HttpClient(new HttpClientHandler{UseProxy=false}){BaseAddress=new($"http://127.0.0.1:{server.BoundPort}")};var url="/api/v1/retention/preview?completed_before="+Uri.EscapeDataString(f.Before.ToString("O"));using(var denied=await client.GetAsync(url))Assert.Equal(HttpStatusCode.Unauthorized,denied.StatusCode);client.DefaultRequestHeaders.Add("X-Agent-Recorder-Key",ApiKeyAuth.CurrentApiKey);using var response=await client.GetAsync(url);var text=await response.Content.ReadAsStringAsync();Assert.Equal(kind is "schema" or "overflow"?HttpStatusCode.ServiceUnavailable:HttpStatusCode.OK,response.StatusCode);Assert.Contains(kind is "schema" or "overflow"?"RETENTION_PREVIEW_UNAVAILABLE":kind=="unknown"?"pending_output_use_unknown":"setup_output_path",text);foreach(var secret in new[]{"SECRET_","required_once_setup_intents","frozen_file_name","SqliteException","SELECT ","different.mp4"})Assert.DoesNotContain(secret,text);f.NoProbes();Assert.Equal(before,FullSnapshot(f));}finally{server?.Stop();Environment.SetEnvironmentVariable("AGENT_RECORDER_DATA_DIR",prior);}
    }
    private sealed class ThrowingTray:ITrayContext
    {
        public string HostMode=>"headless";public bool SupportsRegionSelectionUi=>false;
        public void RequestConfirmation(RecordingConfirmationPresentation p,Action<ConfirmationDecision> c)=>throw new InvalidOperationException();
        public void RequestRegionSelection(int t,Action<string,int,int,int,int,string,string> c)=>throw new InvalidOperationException();
        public void SetRecording(RecordingUiPresentation p)=>throw new InvalidOperationException();public void SetIdle(RecordingUiPresentation p)=>throw new InvalidOperationException();
        public void SetAllIdle()=>throw new InvalidOperationException();public void ShowError(string text)=>throw new InvalidOperationException();
    }
}
