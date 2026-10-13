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
public sealed class PendingOutputUseProtectionTests(ITestOutputHelper output)
{
    private static RetentionSharedOutputPathTests.Fixture Fixture(bool headless=false) => new(headless,
        Path.Combine(TestHelper.ProjectRoot,".local-data/temp/task344","Ä-"+Guid.NewGuid().ToString("N")));
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
    [Theory]
    [InlineData("region_selection_pending")][InlineData("confirmation_pending")][InlineData("rejected")][InlineData("expired")][InlineData("cancelled")]
    public void FixedSetupWithoutAuthorizedSpecProtectsAcrossForeignIdentityAndLifecycle(string state)
    {
        using var f=Fixture();Fixed(f,f.PathFor());if(state!="region_selection_pending") f.Db.Corrupt("UPDATE setup_intents SET status_code=$state;",("$state",state));
        f.Seed();Assert.Equal(0,f.Db.Scalar("SELECT COUNT(*) FROM authorized_capture_scopes;"));AssertProtected(f,"setup_output_path");
    }
    [Theory]
    [InlineData("setup",false,false)][InlineData("setup",true,true)][InlineData("profile",false,false)][InlineData("profile",true,true)]
    public void RendererNamesAreProtectedByUninstantiatedTemplates(string source,bool tail,bool alias)
    {
        using var f=Fixture();var directory=DirectoryOf(f)+(tail?"\\":"");
        if(source=="setup") Recurring(f,directory);else Profile(f,directory);
        var path=f.PathFor(Render());f.Seed(path:alias?path.ToUpperInvariant():path);
        Assert.Equal(0,f.Db.Scalar("SELECT COUNT(*) FROM recurring_occurrence_execution_specs;"));
        Assert.Equal(0,f.Db.Scalar("SELECT COUNT(*) FROM recurring_consent_leases;"));AssertProtected(f,"planned_output_template");
    }
    [Fact]
    public void OldImmutableVersionAndLogicallyDeletedDirectoryStillProtect()
    {
        using var f=Fixture();var repo=new SqliteRecurringFixedRegionProfileRepository(f.Db.Store);var old=Profile(f,DirectoryOf(f),"old-中文");
        repo.CreateNext(old.Reference,Spec(DirectoryOf(f),"new-prefix"),At.AddSeconds(1));
        var management=new SqliteFixedRegionProfileManagementRepository(f.Db.Store);var current=management.Get("SECRET_PROFILE")!;
        management.Delete("SECRET_PROFILE",FixedRegionProfileETag.Compute(current.ProfileId,current.Name,current.CurrentVersion.ProfileVersion,current.CurrentVersion.ProfileDigest,current.IsDeleted),At.AddSeconds(2));f.Seed(path:f.PathFor(Render("old-中文")));AssertProtected(f,"planned_output_template");
    }
    [Theory]
    [InlineData("setup")][InlineData("profile")][InlineData("fixed")]
    public void RootDirectoryUsesTwoIdenticalTailFormsWithoutDoubleCounting(string source)
    {
        using var f=Fixture();var root=Path.GetPathRoot(f.Db.Root)!;var path=root+(source=="fixed"?"root.mp4":Render());
        if(source=="setup")Recurring(f,root);else if(source=="profile")Profile(f,root);else Fixed(f,path);
        f.Seed(path:path);var calls=0;f.Query.RetentionPendingOutputCommandForTests=(_,_,_,args)=>{calls++;Assert.Single(args);Assert.Equal(root,args[0].Value);};
        var evidence=Assert.Single(f.Query.GetOrdinaryEvidencePage(Sid,Session,1,f.Before,null).Items);
        Assert.Equal(source=="fixed"?PendingOutputUseState.SetupExact:PendingOutputUseState.Template,evidence.PendingOutputUse);
        Assert.Equal("outside_videos",Assert.Single(f.Page().Items).ReasonCode);Assert.Equal(2,calls);f.NoProbes();
    }
    public static IEnumerable<object[]> Misses => new[]{"setup","profile"}.SelectMany(s=>new[]{"old","prefix","date","time","short_hash","bad_hash","long_hash","extension","short_time","extra","unicode_normalization"}.Select(k=>new object[]{s,k}));
    [Theory,MemberData(nameof(Misses))]
    public void ValidTemplatesDoNotProtectClearlyDifferentNames(string source,string kind)
    {
        using var f=Fixture();if(source=="setup")Recurring(f,DirectoryOf(f));else Profile(f,DirectoryOf(f));
        var name=Render();name=kind switch{
            "old"=>"ordinary-old.mp4","prefix"=>Render("another"),"date"=>name.Replace("20261005","20260230"),
            "time"=>name.Replace("010000Z","256100Z"),"short_hash"=>name.Remove(name.Length-5,1),"bad_hash"=>name.Replace(new string('a',64),new string('g',64)),
            "long_hash"=>name.Insert(name.Length-4,"a"),"extension"=>name+".mp4","short_time"=>name.Replace("T010000Z","T0100Z"),
            "extra"=>name.Insert(name.Length-4,"-extra"),"unicode_normalization"=>name.Replace("Ä","A\u0308"),_=>throw new InvalidOperationException()};
        f.Seed(path:f.PathFor(name));Assert.Equal("age_rule_match",Assert.Single(f.Page().Items).ReasonCode);Assert.Single(f.Files.Lengths);
    }
    [Theory]
    [InlineData("other")][InlineData("videos2")][InlineData("child")][InlineData("drive")][InlineData("wildcards")]
    public void DirectParentEqualityDoesNotExpandDirectoryPatterns(string kind)
    {
        using var f=Fixture();var directory=DirectoryOf(f);var foreign=kind switch{"other"=>Path.Combine(f.Db.Root,"Elsewhere"),"videos2"=>directory+"2",
            "child"=>directory+"\\child","drive"=>(directory[0]=='Z'?'Y':'Z')+directory[1..],"wildcards"=>directory+"%_[x]",_=>throw new InvalidOperationException()};
        Recurring(f,foreign);Profile(f,foreign);Fixed(f,Path.Combine(foreign,Render()));f.Seed(path:f.PathFor(Render()));
        Assert.Equal("age_rule_match",Assert.Single(f.Page().Items).ReasonCode);Assert.Single(f.Files.Lengths);
    }
    [Theory]
    [InlineData("fixed_case")][InlineData("fixed_name")][InlineData("fixed_unicode")][InlineData("fixed_pattern")][InlineData("fixed_tail")]
    public void FixedSetupUsesLiteralCaseInsensitiveNameAndTailComparison(string kind)
    {
        using var f=Fixture();var name=kind=="fixed_pattern"?"file%_[x].mp4":"Ä-Shared.mp4";Fixed(f,f.PathFor(name));
        if(kind=="fixed_tail")f.Db.Corrupt("UPDATE setup_intents SET output_directory=rtrim(output_directory,'\\');");
        var candidate=kind switch{"fixed_case"=>name.ToUpperInvariant(),"fixed_name"=>"different.mp4","fixed_unicode"=>"A\u0308-Shared.mp4","fixed_pattern"=>"file-other.mp4",_=>name};
        f.Seed(path:f.PathFor(candidate));var hit=kind is "fixed_case" or "fixed_tail";
        Assert.Equal(hit?"setup_output_path":"age_rule_match",Assert.Single(f.Page().Items).ReasonCode);if(hit)f.NoProbes();else Assert.Single(f.Files.Lengths);
    }
    [Theory]
    [InlineData("setup_null")][InlineData("setup_bad_prefix")][InlineData("profile_null")][InlineData("profile_conflict")][InlineData("profile_bad_prefix")][InlineData("fixed_null")][InlineData("fixed_bad_name")]
    public void OnlySelectedDirectoryBadOutputFieldsAreLocallyUnknown(string kind)
    {
        using var f=Fixture();if(kind.StartsWith("setup"))Recurring(f,DirectoryOf(f));else if(kind.StartsWith("profile"))Profile(f,DirectoryOf(f));else Fixed(f,f.PathFor());
        var sql=kind switch{"setup_null"=>"UPDATE setup_intents SET recurring_filename_prefix=NULL;","setup_bad_prefix"=>"UPDATE setup_intents SET recurring_filename_prefix='bad%_[x]';",
            "profile_null"=>"UPDATE recurring_fixed_region_profile_versions SET filename_template='';","profile_conflict"=>"UPDATE recurring_fixed_region_profile_versions SET filename_template='conflict';",
            "profile_bad_prefix"=>"UPDATE recurring_fixed_region_profile_versions SET filename_prefix='CON';","fixed_null"=>"UPDATE setup_intents SET frozen_file_name=NULL;",
            "fixed_bad_name"=>"UPDATE setup_intents SET frozen_file_name='bad/name.mp4';",_=>throw new InvalidOperationException()};
        f.Db.Corrupt(sql);f.Seed();AssertProtected(f,"pending_output_use_unknown");
    }
    [Theory]
    [InlineData("fixed")][InlineData("setup")][InlineData("profile")]
    public void OversizedSelectedFieldsAreUnknownAndNotMaterializedUnbounded(string source)
    {
        using var f=Fixture();if(source=="fixed")Fixed(f,f.PathFor());else if(source=="setup")Recurring(f,DirectoryOf(f));else Profile(f,DirectoryOf(f));
        f.Db.Corrupt(source=="profile"?"UPDATE recurring_fixed_region_profile_versions SET filename_template=$huge;":source=="fixed"?"UPDATE setup_intents SET frozen_file_name=$huge;":"UPDATE setup_intents SET recurring_filename_prefix=$huge;",("$huge",new string('a',10000)));
        f.Query.RetentionPendingOutputCommandForTests=(_,_,sql,_)=>{Assert.Contains("CASE WHEN typeof(uses.",sql);Assert.Contains("length(uses.filename_template)<=200",sql);};
        f.Seed();AssertProtected(f,"pending_output_use_unknown");
    }
    [Fact]
    public void UnrelatedDirtyRowsAndEarlyNoOutputIntentDoNotInventPaths()
    {
        using var f=Fixture();Recurring(f,Path.Combine(f.Db.Root,"elsewhere"));Profile(f,Path.Combine(f.Db.Root,"elsewhere"));Fixed(f,f.PathFor());
        f.Db.Corrupt("UPDATE setup_intents SET recurring_filename_prefix=NULL WHERE intent_id='SECRET_RECURRING'; UPDATE recurring_fixed_region_profile_versions SET filename_template=''; UPDATE setup_intents SET output_directory=NULL,frozen_file_name=NULL WHERE intent_id='SECRET_FIXED';");
        f.Seed();Assert.Equal("age_rule_match",Assert.Single(f.Page().Items).ReasonCode);Assert.Single(f.Files.Lengths);
    }
    private static void Copies(RetentionSharedOutputPathTests.Fixture f,string seed,int copies)
    {
        using var c=f.Db.Store.OpenReadOnlyConnection();using var cmd=c.CreateCommand();cmd.CommandText="PRAGMA table_info(setup_intents);";
        var columns=new List<string>();using(var reader=cmd.ExecuteReader())while(reader.Read())columns.Add(reader.GetString(1));
        var projected=columns.Select(name=>name=="intent_id"?"'copy-'||x":name=="idempotency_key"?"'copy-key-'||x":"seed.\""+name+"\"");
        f.Db.Execute("WITH RECURSIVE n(x) AS(SELECT 1 UNION ALL SELECT x+1 FROM n WHERE x<$copies) INSERT INTO setup_intents SELECT "+string.Join(",",projected)+" FROM setup_intents seed,n WHERE seed.intent_id=$seed;",("$copies",copies),("$seed",seed));
    }
    [Theory]
    [InlineData(500)][InlineData(501)]
    public void TotalRowsAcrossAllThreeSourcesAreBoundedBeforeAnyProbe(int count)
    {
        using var f=Fixture();Fixed(f,f.PathFor("different.mp4"));Recurring(f,DirectoryOf(f));Profile(f,DirectoryOf(f));
        // Real normalized seeds, copied only in the isolated DB to exercise the global row budget cheaply.
        Copies(f,"SECRET_FIXED",count-3);
        f.Seed("first",f.PathFor("ordinary.mp4"));f.Seed("second",f.PathFor(Render()));var before=f.Snapshot();
        if(count==501){Assert.Throws<RetentionPreviewUnavailableException>(()=>f.Page());f.NoProbes();}
        else {var page=f.Page();Assert.Equal(new[]{"age_rule_match","planned_output_template"},page.Items.Select(i=>i.ReasonCode));Assert.Single(f.Files.Lengths);}
        Assert.Equal(before,f.Snapshot());
    }
    [Fact]
    public void OneConfigurationIsNotCountedOncePerCandidateAndAllIndexesAreSearched()
    {
        using var f=Fixture();Recurring(f,DirectoryOf(f));Copies(f,"SECRET_RECURRING",5);for(var i=0;i<100;i++)f.Seed("r"+i.ToString("000"),f.PathFor(Render(digest:"abcdef0123456789"[i%16]).Replace(new string("abcdef0123456789"[i%16],64),i.ToString("x64"))));
        var calls=0;f.Query.RetentionPendingOutputCommandForTests=(c,t,sql,args)=>{
            calls++;Assert.Equal(2,args.Count);Assert.Contains("LIMIT 501",sql);Assert.DoesNotContain("LIKE",sql);Assert.DoesNotContain("$sid",sql);Assert.DoesNotContain("$before",sql);
            using var explain=c.CreateCommand();explain.Transaction=t;explain.CommandText="EXPLAIN QUERY PLAN "+sql;foreach(var arg in args)explain.Parameters.AddWithValue(arg.Key,arg.Value);
            using var reader=explain.ExecuteReader();var plans=new List<string>();while(reader.Read())plans.Add(reader.GetString(3));
            foreach(var p in SqliteSchemaV38.Paths)Assert.Contains(plans,s=>s.Contains("SEARCH uses USING")&&s.Contains(p.Index)&&s.Contains(p.Columns[0]+"=?"));
            output.WriteLine(sql);output.WriteLine(string.Join(" | ",plans));
        };
        var before=f.Snapshot();var page=f.Page(100);Assert.Equal(1,calls);Assert.Equal(100,page.Items.Count);Assert.All(page.Items,i=>Assert.Equal("planned_output_template",i.ReasonCode));f.NoProbes();Assert.Equal(before,f.Snapshot());
    }
    public static IEnumerable<object[]> SchemaFaults => SqliteSchemaV38.Paths.Select(p=>new object[]{"DROP INDEX "+p.Index+";"}).Concat(new[]{
        new object[]{"DROP INDEX idx_setup_intents_pending_output_path; CREATE INDEX idx_setup_intents_pending_output_path ON setup_intents(output_directory COLLATE NOCASE,frozen_file_name COLLATE NOCASE);"},
        new object[]{"DROP INDEX idx_setup_intents_pending_output_template; CREATE INDEX idx_setup_intents_pending_output_template ON setup_intents(recurring_filename_prefix COLLATE AGENTRECORDER_PATH_ORDINAL_IGNORE_CASE_V1);"},
        new object[]{"UPDATE schema_migrations SET name='wrong' WHERE version=38;"},new object[]{"DELETE FROM schema_migrations WHERE version=38;"},
        new object[]{"UPDATE schema_migrations SET definition_checksum='0000000000000000000000000000000000000000000000000000000000000000' WHERE version=38;"},
        new object[]{"ALTER TABLE recurring_fixed_region_profile_versions RENAME COLUMN filename_template TO wrong_template;"}});
    [Theory,MemberData(nameof(SchemaFaults))]
    public void DamagedSchemaFailsClosedAndCannotBeRepairedByReading(string sql)
    {
        using var f=Fixture();f.Seed();f.Db.Execute(sql);var before=f.Snapshot();Assert.Throws<RetentionPreviewUnavailableException>(()=>f.Page());
        Assert.Throws<SqliteOperationalStoreException>(()=>f.Db.Store.Initialize());f.NoProbes();Assert.Equal(before,f.Snapshot());
    }
    [Theory]
    [InlineData("sql")][InlineData("collation")][InlineData("comparison")]
    public void BatchExecutionFaultsDoNotSampleFiles(string fault)
    {
        using var f=Fixture();Recurring(f,DirectoryOf(f));f.Seed(path:f.PathFor(Render()));
        f.Query.RetentionPendingOutputCommandForTests=(c,_,_,_)=>{if(fault=="sql")throw new IOException("SECRET_SQL");if(fault=="collation")c.CreateCollation(SqliteOutputPathCollation.Name,null);else c.CreateCollation(SqliteOutputPathCollation.Name,(_,_)=>throw new IOException("SECRET_COMPARE"));};
        Assert.Throws<RetentionPreviewUnavailableException>(()=>f.Page());f.NoProbes();
    }
    [Fact]
    public void HundredDistinctDirectoriesUseOneParameterizedBatchAndNoUnrelatedRows()
    {
        using var f=Fixture();for(var i=0;i<100;i++){var path=f.PathFor("dir"+i+"\\"+Render());Recurring(f,Path.GetDirectoryName(path)!,id:"intent"+i);f.Seed("r"+i.ToString("000"),path);}
        var calls=0;f.Query.RetentionPendingOutputCommandForTests=(_,_,sql,args)=>{calls++;Assert.Equal(200,args.Count);Assert.DoesNotContain("LIKE",sql);Assert.Contains("LIMIT 501",sql);};
        var page=f.Page(100);Assert.Equal(1,calls);Assert.Equal(100,page.Items.Count);Assert.All(page.Items,e=>Assert.Equal("planned_output_template",e.ReasonCode));f.NoProbes();
    }
    [Theory]
    [InlineData("%")][InlineData("[")][InlineData("]")][InlineData("CON")][InlineData("COM1")][InlineData("..")][InlineData(" trailing ")]
    public void InvalidSelectedPrefixCannotBecomeNoUse(string prefix)
    {
        using var f=Fixture();Recurring(f,DirectoryOf(f));f.Db.Corrupt("UPDATE setup_intents SET recurring_filename_prefix=$prefix;",("$prefix",prefix));f.Seed();AssertProtected(f,"pending_output_use_unknown");
    }
    [Theory]
    [InlineData("_")][InlineData("a-b-c")][InlineData("中文-前缀")][InlineData("Ä")]
    public void LegalPrefixSymbolsAreLiteralRendererMatches(string prefix)
    {
        using var f=Fixture();Recurring(f,DirectoryOf(f),prefix);f.Seed(path:f.PathFor(Render(prefix)));AssertProtected(f,"planned_output_template");
    }
    [Theory]
    [InlineData("fixed",true)][InlineData("fixed",false)][InlineData("setup",true)][InlineData("setup",false)][InlineData("profile",true)][InlineData("profile",false)]
    public async Task WalWritesAfterPageSnapshotCannotMixRetentionFacts(string source,bool insert)
    {
        using var f=Fixture();var path=f.PathFor(source=="fixed"?"fixed.mp4":Render());
        void Add(){if(source=="fixed")Fixed(f,path);else if(source=="setup")Recurring(f,DirectoryOf(f));else Profile(f,DirectoryOf(f));}
        if(!insert)Add();f.Seed(path:path);using var entered=new ManualResetEventSlim();using var release=new ManualResetEventSlim();
        f.Query.EntriesValidatedForTests=(_,_)=>{entered.Set();Assert.True(release.Wait(TimeSpan.FromSeconds(10)));};
        var pending=Task.Run(()=>f.Page());try{Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));if(insert)Add();else f.Db.Corrupt(source=="profile"?"DELETE FROM recurring_fixed_region_profile_versions;":"DELETE FROM setup_intents;");}finally{release.Set();}
        var protectedReason=source=="fixed"?"setup_output_path":"planned_output_template";
        Assert.Equal(insert?"age_rule_match":protectedReason,Assert.Single((await pending).Items).ReasonCode);
        f.Query.EntriesValidatedForTests=null;f.Files.Attributes.Clear();f.Files.Lengths.Clear();f.Files.Observations.Clear();f.Files.ProbeAttributes.Clear();
        Assert.Equal(insert?protectedReason:"age_rule_match",Assert.Single(f.Page().Items).ReasonCode);if(insert)f.NoProbes();
    }
    [Fact]
    public void PurposeInsertedAfterWatermarkProtectsNextPageWithoutChangingIdsOrCursor()
    {
        using var f=Fixture();f.Seed("a",f.PathFor("a.mp4"));f.Seed("b",f.PathFor(Render()));var first=f.Page(1);
        Recurring(f,DirectoryOf(f));f.Files.Attributes.Clear();f.Files.Lengths.Clear();f.Files.Observations.Clear();f.Files.ProbeAttributes.Clear();
        var next=f.Page(1,first.NextCursor);Assert.Equal("ordinary:b",Assert.Single(next.Items).EntryId);Assert.Equal("planned_output_template",next.Items[0].ReasonCode);Assert.Null(next.NextCursor);f.NoProbes();
    }
    [Fact]
    public async Task ExistingNestedSharedAndFrozenPrioritiesRemainAheadOfPendingUses()
    {
        using var f=Fixture();Fixed(f,f.PathFor());Recurring(f,DirectoryOf(f));await RetentionPlannedOutputPathTests.Purpose(f,"future",path:f.PathFor());f.Seed();
        AssertProtected(f,"planned_output_path");f.Seed("foreign",f.PathFor(),"failed",f.Before.AddDays(1));AssertProtected(f,"shared_output_path");
        f.Db.Execute("UPDATE ordinary_recording_history SET nested_role='outer' WHERE recording_id='target';");AssertProtected(f,"nested_relationship");
    }
    [Theory]
    [InlineData(false,"fixed")][InlineData(true,"fixed")][InlineData(false,"template")][InlineData(true,"template")][InlineData(false,"unknown")][InlineData(true,"overflow")]
    public async Task ExistingApiReturnsOnlyStableReasonsAndNoReferrerDetails(bool headless,string kind)
    {
        using var f=Fixture(headless);var prior=Environment.GetEnvironmentVariable("AGENT_RECORDER_DATA_DIR");ApiServer? server=null;
        try
        {
            if(kind=="fixed")Fixed(f,f.PathFor());else Recurring(f,DirectoryOf(f));f.Seed(path:kind=="fixed"?f.PathFor():f.PathFor(Render()));
            if(kind=="unknown")f.Db.Corrupt("UPDATE setup_intents SET recurring_filename_prefix=NULL;");
            if(kind=="overflow")Copies(f,"SECRET_RECURRING",500);
            Environment.SetEnvironmentVariable("AGENT_RECORDER_DATA_DIR",f.Db.Root);ApiKeyAuth.InitializeForTesting(f.Db.Root);
            var audit=new RetentionSharedOutputPathTests.QuietAudit();var engine=new RecordingEngine(audit);var tray=new ThrowingTray();engine.SetTray(tray);
            engine.BackendFactory=_=>throw new InvalidOperationException("No capture permitted.");
            server=new ApiServer(engine,audit,tray,null,null,null,null,null,null,null,null,null,0,retentionPreviewGateway:new SqliteRetentionPreviewGateway(f.Service,()=>(Sid,Session)));
            server.Start();using var client=new HttpClient(new HttpClientHandler{UseProxy=false}){BaseAddress=new($"http://127.0.0.1:{server.BoundPort}")};client.DefaultRequestHeaders.Add("X-Agent-Recorder-Key",ApiKeyAuth.CurrentApiKey);
            using var response=await client.GetAsync("/api/v1/retention/preview?completed_before="+Uri.EscapeDataString(f.Before.ToString("O")));var text=await response.Content.ReadAsStringAsync();
            Assert.Equal(kind=="overflow"?HttpStatusCode.ServiceUnavailable:HttpStatusCode.OK,response.StatusCode);
            Assert.Contains(kind switch{"fixed"=>"setup_output_path","template"=>"planned_output_template","unknown"=>"pending_output_use_unknown",_=>"RETENTION_PREVIEW_UNAVAILABLE"},text);
            foreach(var secret in new[]{"SECRET_","filename_prefix","filename_template","referrers","safe_to_delete"})Assert.DoesNotContain(secret,text);f.NoProbes();
        }
        finally{server?.Stop();Environment.SetEnvironmentVariable("AGENT_RECORDER_DATA_DIR",prior);}
    }
    [Fact]
    public void GenuineV37UpgradeAddsOnlyThreeIndexesAndOneReceiptAndIsIdempotent()
    {
        using var f=Fixture();Fixed(f,f.PathFor());Recurring(f,DirectoryOf(f));Profile(f,DirectoryOf(f));f.Seed();var path=Path.Combine(f.Db.Root,"v37.db");
        using(var c=new SqliteConnection($"Data Source={path};Pooling=False"))
        {
            c.Open();SqliteOutputPathCollation.Register(c);using var cmd=c.CreateCommand();foreach(var m in SqliteSchemaCatalog.Migrations.Where(m=>m.Version<=37))
            {cmd.CommandText=m.Definition;cmd.ExecuteNonQuery();cmd.CommandText="INSERT INTO schema_migrations VALUES($v,$n,$c,$at);";cmd.Parameters.AddWithValue("$v",m.Version);cmd.Parameters.AddWithValue("$n",m.Name);cmd.Parameters.AddWithValue("$c",m.Checksum);cmd.Parameters.AddWithValue("$at",1000+m.Version);cmd.ExecuteNonQuery();cmd.Parameters.Clear();}
            cmd.CommandText="ATTACH DATABASE $seed AS seed;";cmd.Parameters.AddWithValue("$seed",f.Db.Store.DatabasePath);cmd.ExecuteNonQuery();cmd.Parameters.Clear();
            foreach(var table in new[]{"setup_intents","recurring_fixed_region_profile_versions","ordinary_recording_history"}){cmd.CommandText="INSERT INTO "+table+" SELECT * FROM seed."+table+";";cmd.ExecuteNonQuery();}
        }
        var before=UnifiedHistoryProfileFilterTests.RawSnapshot(path,schemaThrough:37);var definitions=Definitions(path);var store=new SqliteOperationalStore(path);store.Initialize();
        Assert.Equal(before,UnifiedHistoryProfileFilterTests.RawSnapshot(path,schemaThrough:37));Assert.Equal(definitions,Definitions(path));var once=UnifiedHistoryProfileFilterTests.RawSnapshot(path);store.Initialize();Assert.Equal(once,UnifiedHistoryProfileFilterTests.RawSnapshot(path));
        Assert.Equal(41,SqliteOperationalStore.CurrentSchemaVersion);Assert.Equal(3,SqliteSchemaV38.Indexes.Count);
        using var c2=store.OpenReadOnlyConnection();using var cmd2=c2.CreateCommand();cmd2.CommandText="PRAGMA integrity_check;";Assert.Equal("ok",cmd2.ExecuteScalar());
    }
    private static string Definitions(string path)
    {
        using var c=new SqliteConnection($"Data Source={path};Pooling=False;Mode=ReadOnly");c.Open();using var cmd=c.CreateCommand();cmd.CommandText="SELECT name,sql FROM sqlite_master WHERE tbl_name NOT LIKE 'future_window_publication%' AND name NOT LIKE 'future_window_publication%' AND name NOT LIKE 'idx_future_window_publication%' AND tbl_name NOT LIKE 'plan_media_publication%' AND name NOT LIKE 'plan_media_publication%' AND name NOT LIKE 'idx_plan_media_publication%' AND name NOT IN ('idx_setup_intents_pending_output_path','idx_setup_intents_pending_output_template','idx_recurring_profiles_pending_output_template','idx_required_once_setup_pending_output_path') ORDER BY name;";
        using var r=cmd.ExecuteReader();var lines=new List<string>();while(r.Read())lines.Add(r.GetString(0)+":"+(r.IsDBNull(1)?"":r.GetString(1)));return string.Join("\n",lines);
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
