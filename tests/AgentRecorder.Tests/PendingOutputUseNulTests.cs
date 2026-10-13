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
public sealed class PendingOutputUseNulTests(ITestOutputHelper output)
{
    private static RetentionSharedOutputPathTests.Fixture Fixture(bool headless=false) => new(headless,
        Path.Combine(TestHelper.ProjectRoot,".local-data/temp/task344r","Ä-"+Guid.NewGuid().ToString("N")));
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
    private static void Copies(RetentionSharedOutputPathTests.Fixture f,string seed,int copies)
    {
        using var c=f.Db.Store.OpenReadOnlyConnection();using var cmd=c.CreateCommand();cmd.CommandText="PRAGMA table_info(setup_intents);";
        var columns=new List<string>();using(var reader=cmd.ExecuteReader())while(reader.Read())columns.Add(reader.GetString(1));
        var projected=columns.Select(name=>name=="intent_id"?"'copy-'||x":name=="idempotency_key"?"'copy-key-'||x":"seed.\""+name+"\"");
        f.Db.Execute("WITH RECURSIVE n(x) AS(SELECT 1 UNION ALL SELECT x+1 FROM n WHERE x<$copies) INSERT INTO setup_intents SELECT "+string.Join(",",projected)+" FROM setup_intents seed,n WHERE seed.intent_id=$seed;",("$copies",copies),("$seed",seed));
    }

    private static (string Table,string Field) Field(string kind) => kind switch {
        "fixed" => ("setup_intents","frozen_file_name"), "setup" => ("setup_intents","recurring_filename_prefix"),
        "profile_prefix" => ("recurring_fixed_region_profile_versions","filename_prefix"),
        "profile_template" => ("recurring_fixed_region_profile_versions","filename_template"), _=>throw new InvalidOperationException()};
    private static void Use(RetentionSharedOutputPathTests.Fixture f,string kind,string prefix="secret-中")
    { if(kind=="fixed")Fixed(f,f.PathFor("different-secret.mp4"));else if(kind=="setup")Recurring(f,DirectoryOf(f),prefix);else Profile(f,DirectoryOf(f),prefix); }
    private static string Broken(string value,string position,int suffix) => position switch {
        "start"=>"\0"+value+new string('x',suffix), "middle"=>value.Insert(value.Length/2,"\0")+new string('x',suffix),
        "after"=>value+"\0"+new string('x',suffix), _=>throw new InvalidOperationException()};
    private static void Damage(RetentionSharedOutputPathTests.Fixture f,string kind,object value)
    {var (table,field)=Field(kind);var text=value is string;f.Db.Corrupt($"UPDATE {table} SET {field}="+(text?"CAST($bad AS TEXT)":"$bad")+";",("$bad",text?System.Text.Encoding.UTF8.GetBytes((string)value):value));}
    // Include complete bytes: GetString-based snapshots alone cannot detect changed NUL suffixes.
    private static string FullSnapshot(RetentionSharedOutputPathTests.Fixture f)
    {
        using var c=f.Db.Store.OpenReadOnlyConnection();using var cmd=c.CreateCommand();var all=new List<string>{f.Snapshot()};
        foreach(var (table,fields) in new[]{("setup_intents","output_directory,frozen_file_name,recurring_output_directory,recurring_filename_prefix"),
            ("recurring_fixed_region_profile_versions","output_directory,filename_prefix,filename_template")})
        {
            cmd.CommandText="SELECT "+string.Join(",",fields.Split(',').Select(s=>"typeof("+s+"),hex(CAST("+s+" AS BLOB))"))+" FROM "+table+" ORDER BY 1,2;";
            using var reader=cmd.ExecuteReader();while(reader.Read())all.Add(string.Join("|",Enumerable.Range(0,reader.FieldCount).Select(i=>reader.GetString(i))));
        }
        cmd.CommandText="SELECT name,sql FROM sqlite_master ORDER BY name;";
        using(var reader=cmd.ExecuteReader())while(reader.Read())all.Add(reader.GetString(0)+":"+(reader.IsDBNull(1)?"":reader.GetString(1)));
        return string.Join("\n",all);
    }
    public static IEnumerable<object[]> NulFields => new[]{"fixed","setup","profile_prefix","profile_template"}
        .SelectMany(k=>new[]{"start","middle","after"}.SelectMany(p=>new[]{3,1000000}.SelectMany(n=>new[]{false,true}.Select(m=>new object[]{k,p,n,m}))));
    [Theory,MemberData(nameof(NulFields))]
    public void CompleteSelectedNulFieldsAreUnknownBeforeAnyProbe(string kind,string position,int suffix,bool looksMatching)
    {
        using var f=Fixture();Use(f,kind);var prefix="secret-中";
        var value=kind=="fixed"?"different-secret.mp4":kind=="profile_template"?prefix+"-{scheduled_utc}-{occurrence_id}.mp4":prefix;
        var bad=Broken(value,position,suffix);Damage(f,kind,bad);
        using(var c=f.Db.Store.OpenReadOnlyConnection())using(var cmd=c.CreateCommand())
        {var (table,field)=Field(kind);cmd.CommandText=$"SELECT instr({field},char(0)),length(CAST({field} AS BLOB)) FROM {table};";using var reader=cmd.ExecuteReader();Assert.True(reader.Read());Assert.True(reader.GetInt64(0)>0);Assert.Equal(System.Text.Encoding.UTF8.GetByteCount(bad),reader.GetInt64(1));}
        f.Seed(path:f.PathFor(looksMatching?(kind=="fixed"?value:Render(prefix)):"ordinary-old.mp4"));
        var before=FullSnapshot(f);var relation=Assert.Single(f.Query.GetOrdinaryEvidencePage(Sid,Session,1,f.Before,null).Items);
        Assert.Equal(PendingOutputUseState.Unknown,relation.PendingOutputUse);AssertProtected(f,"pending_output_use_unknown");Assert.Equal(before,FullSnapshot(f));
    }
    public static IEnumerable<object[]> NulDirectories => new[]{"fixed","setup","profile"}
        .SelectMany(k=>new[]{"start","middle","after"}.SelectMany(p=>new[]{3,1000000}.SelectMany(n=>new[]{false,true}.Select(m=>new object[]{k,p,n,m}))));
    [Theory,MemberData(nameof(NulDirectories))]
    public void RawIndexedDirectorySelectionKeepsDamagedSelectedRowsUnknown(string kind,string position,int suffix,bool related)
    {
        using var f=Fixture();Use(f,kind);var p=SqliteSchemaV38.Paths[kind=="fixed"?0:kind=="setup"?1:2];
        var directory=DirectoryOf(f)+(related?"":"2");var bad=Broken(directory,position,suffix);
        f.Db.Corrupt($"UPDATE {p.Table} SET {p.Columns[0]}=CAST($bad AS TEXT);",("$bad",System.Text.Encoding.UTF8.GetBytes(bad)));f.Seed(path:f.PathFor(kind=="fixed"?"different-secret.mp4":Render("secret-中")));
        long selected;
        using(var c=f.Db.Store.OpenReadOnlyConnection())using(var cmd=c.CreateCommand())
        {
            cmd.CommandText=$"SELECT count(*) FROM {p.Table} INDEXED BY {p.Index} WHERE {p.Columns[0]} COLLATE {SqliteOutputPathCollation.Name}=$dir;";
            cmd.Parameters.AddWithValue("$dir",DirectoryOf(f));selected=(long)cmd.ExecuteScalar()!;
            cmd.Parameters.Clear();cmd.CommandText="SELECT sqlite_version(),length(CAST($bad AS TEXT)),instr(CAST($bad AS TEXT),char(0)),length($bad),CAST($bad AS TEXT) COLLATE "+SqliteOutputPathCollation.Name+"=$dir,CAST($bad AS TEXT);";
            cmd.Parameters.AddWithValue("$bad",System.Text.Encoding.UTF8.GetBytes(bad));cmd.Parameters.AddWithValue("$dir",DirectoryOf(f));using var reader=cmd.ExecuteReader();reader.Read();
            Assert.Equal(selected,reader.GetInt64(4));Assert.True(reader.GetInt64(2)>0);Assert.Equal(System.Text.Encoding.UTF8.GetByteCount(bad),reader.GetInt64(3));
            Assert.Equal(bad.IndexOf('\0'),reader.GetInt64(1));
            output.WriteLine($"SQLite={reader.GetString(0)} source={kind} position={position} suffix={suffix} related={related} selected={selected} SQLchars={reader.GetInt64(1)} bytes={reader.GetInt64(3)} GetStringChars={reader.GetString(5).Length}");
        }
        // Assert the actual indexed comparison, without changing global collation.
        Assert.InRange(selected,0L,1L);if(!related)Assert.Equal(0L,selected);
        var calls=0;f.Query.RetentionPendingOutputCommandForTests=(_,_,sql,args)=>{calls++;Assert.Equal(2,args.Count);Assert.Contains("requested.directory",sql);Assert.Contains("instr(uses."+p.Columns[0]+",char(0))=0",sql);};
        var before=FullSnapshot(f);var item=Assert.Single(f.Page().Items);Assert.Equal(selected==1?"pending_output_use_unknown":"age_rule_match",item.ReasonCode);
        Assert.Equal(1,calls);if(selected==1)f.NoProbes();else Assert.Single(f.Files.Lengths);Assert.Equal(before,FullSnapshot(f));
    }
    [Fact]
    public void RealSQLiteNulTruncationIsRejectedByProductionProjection()
    {
        using var f=Fixture();Use(f,"fixed");Damage(f,"fixed","different-secret.mp4\0"+new string('x',1000000));f.Seed();
        f.Query.RetentionPendingOutputCommandForTests=(c,t,sql,args)=>{
            using var cmd=c.CreateCommand();cmd.Transaction=t;cmd.CommandText=sql;foreach(var a in args)cmd.Parameters.AddWithValue(a.Key,a.Value);
            using var reader=cmd.ExecuteReader();Assert.True(reader.Read());Assert.False(reader.IsDBNull(2));Assert.True(reader.IsDBNull(3));Assert.False(reader.Read());output.WriteLine(sql);
        };AssertProtected(f,"pending_output_use_unknown");
    }
    [Theory]
    [InlineData(false)][InlineData(true)]
    public void ExactThenUnknownThenTemplatePrecedenceAcrossMultipleCandidates(bool reverseInsertion)
    {
        using var f=Fixture();var prefix="secret-中";var name=Render(prefix);
        if(reverseInsertion){Use(f,"setup",prefix);Damage(f,"setup",prefix+"\0bad");Profile(f,DirectoryOf(f),prefix);Fixed(f,f.PathFor(name));}
        else{Fixed(f,f.PathFor(name));Profile(f,DirectoryOf(f),prefix);Use(f,"setup",prefix);Damage(f,"setup",prefix+"\0bad");}
        f.Seed("exact",f.PathFor(name));f.Seed("template",f.PathFor(Render(prefix,'b')));f.Seed("ordinary",f.PathFor());
        var before=FullSnapshot(f);var page=f.Page();Assert.Equal(new[]{"setup_output_path","pending_output_use_unknown","pending_output_use_unknown"},page.Items.Select(i=>i.ReasonCode));f.NoProbes();Assert.Equal(before,FullSnapshot(f));
    }
    [Theory]
    [InlineData(500)][InlineData(501)]
    public void BrokenRelatedRowsConsumeGlobalLimitBeforeAnyCandidateProbe(int count)
    {
        using var f=Fixture();Use(f,"fixed");Damage(f,"fixed","different-secret.mp4\0bad");Copies(f,"SECRET_FIXED",count-3);
        Recurring(f,DirectoryOf(f),"secret-中");Profile(f,DirectoryOf(f),"secret-中");f.Seed("first",f.PathFor());f.Seed("second",f.PathFor(Render("secret-中")));
        var before=FullSnapshot(f);if(count==501)Assert.Throws<RetentionPreviewUnavailableException>(()=>f.Page());
        else Assert.All(f.Page().Items,i=>Assert.Equal("pending_output_use_unknown",i.ReasonCode));f.NoProbes();Assert.Equal(before,FullSnapshot(f));
    }
    public static IEnumerable<object[]> AbnormalTypes => new[]{"fixed","setup","profile_prefix","profile_template"}.SelectMany(k=>new[]{"blob_nul","blob","oversized"}.Select(v=>new object[]{k,v}));
    [Theory,MemberData(nameof(AbnormalTypes))]
    public void BlobAndZeroNulOversizedSelectedEvidenceRemainUnknown(string kind,string type)
    {object bad=type=="oversized"?new string('x',1000000):type=="blob_nul"?new byte[]{97,0,120}:new byte[]{97,98,99};using var f=Fixture();Use(f,kind);Damage(f,kind,bad);f.Seed();var before=FullSnapshot(f);AssertProtected(f,"pending_output_use_unknown");Assert.Equal(before,FullSnapshot(f));}
    [Theory]
    [InlineData("fixed")][InlineData("setup")][InlineData("profile")]
    public void BlobDirectoryDoesNotExpandRawTextIndexedRelation(string kind)
    {
        using var f=Fixture();Use(f,kind);var p=SqliteSchemaV38.Paths[kind=="fixed"?0:kind=="setup"?1:2];
        f.Db.Corrupt($"UPDATE {p.Table} SET {p.Columns[0]}=$bad;",("$bad",System.Text.Encoding.UTF8.GetBytes(DirectoryOf(f))));f.Seed();
        var before=FullSnapshot(f);Assert.Equal("age_rule_match",Assert.Single(f.Page().Items).ReasonCode);Assert.Single(f.Files.Lengths);Assert.Equal(before,FullSnapshot(f));
    }
    [Theory]
    [InlineData("setup",false)][InlineData("profile",false)][InlineData("setup",true)][InlineData("profile",true)]
    public void NonBmpUnicodeRemainsValidWithinOriginalSourceLimitsAndTailRules(string kind,bool alias)
    {
        using var f=Fixture();var prefix=string.Concat(Enumerable.Repeat("\U00010400",kind=="setup"?32:64));var dir=DirectoryOf(f)+(alias?"\\":"");
        if(kind=="setup")Recurring(f,dir,prefix);else Profile(f,dir,prefix);var path=f.PathFor(Render(prefix));f.Seed(path:alias?path.ToUpperInvariant():path);
        AssertProtected(f,"planned_output_template");
    }
    [Fact]
    public async Task AuthenticatedApiNeverExposesTruncatedOrCompleteDamagedFields()
    {
        using var f=Fixture(true);Use(f,"profile_template");Damage(f,"profile_template","secret-中-{scheduled_utc}-{occurrence_id}.mp4\0SECRET_BAD_SUFFIX"+new string('x',1000000));f.Seed();
        var before=FullSnapshot(f);var prior=Environment.GetEnvironmentVariable("AGENT_RECORDER_DATA_DIR");ApiServer? server=null;
        try
        {
            Environment.SetEnvironmentVariable("AGENT_RECORDER_DATA_DIR",f.Db.Root);ApiKeyAuth.InitializeForTesting(f.Db.Root);
            var audit=new RetentionSharedOutputPathTests.QuietAudit();var engine=new RecordingEngine(audit);var tray=new ThrowingTray();engine.SetTray(tray);engine.BackendFactory=_=>throw new InvalidOperationException("No capture permitted.");
            server=new ApiServer(engine,audit,tray,null,null,null,null,null,null,null,null,null,0,retentionPreviewGateway:new SqliteRetentionPreviewGateway(f.Service,()=>(Sid,Session)));server.Start();
            using var client=new HttpClient(new HttpClientHandler{UseProxy=false}){BaseAddress=new($"http://127.0.0.1:{server.BoundPort}")};
            var url="/api/v1/retention/preview?completed_before="+Uri.EscapeDataString(f.Before.ToString("O"));
            using(var unauth=await client.GetAsync(url))Assert.Equal(HttpStatusCode.Unauthorized,unauth.StatusCode);
            client.DefaultRequestHeaders.Add("X-Agent-Recorder-Key",ApiKeyAuth.CurrentApiKey);using var response=await client.GetAsync(url);var text=await response.Content.ReadAsStringAsync();
            Assert.Equal(HttpStatusCode.OK,response.StatusCode);Assert.Contains("pending_output_use_unknown",text);
            foreach(var secret in new[]{"SECRET_","secret-中","filename_prefix","filename_template","referrers","SELECT ","SqliteException"})Assert.DoesNotContain(secret,text);
            f.NoProbes();Assert.Equal(before,FullSnapshot(f));
        }
        finally{server?.Stop();Environment.SetEnvironmentVariable("AGENT_RECORDER_DATA_DIR",prior);}
    }

    [Theory]
    [InlineData("setup")][InlineData("profile")]
    public void SixtyFourChineseRunesAndNormalRendererNamesKeepOriginalDomain(string kind)
    {using var f=Fixture();var prefix=new string('中',64);Use(f,kind,prefix);f.Seed(path:f.PathFor(Render(prefix)));AssertProtected(f,"planned_output_template");}
    [Fact]
    public void ActualDriverParameterBindingAndCompleteSQLiteTextAreDistinguished()
    {
        using var f=Fixture();using var c=f.Db.Store.OpenReadOnlyConnection();using var cmd=c.CreateCommand();
        var bad="different.mp4\0"+new string('x',1000000);cmd.CommandText="SELECT sqlite_version(),length(CAST($string AS BLOB)),instr($string,char(0)),length(CAST($bytes AS TEXT)),length($bytes),instr(CAST($bytes AS TEXT),char(0)),CAST($bytes AS TEXT);";
        cmd.Parameters.AddWithValue("$string",bad);cmd.Parameters.AddWithValue("$bytes",System.Text.Encoding.UTF8.GetBytes(bad));using var reader=cmd.ExecuteReader();reader.Read();
        Assert.Equal(13,reader.GetInt64(1));Assert.Equal(0,reader.GetInt64(2));Assert.Equal(13,reader.GetInt64(3));Assert.Equal(bad.Length,reader.GetInt64(4));Assert.Equal(14,reader.GetInt64(5));Assert.Equal(bad,reader.GetString(6));
        output.WriteLine($"SQLite={reader.GetString(0)} .NET input chars={bad.Length}; AddWithValue string: stored bytes={reader.GetInt64(1)} instr={reader.GetInt64(2)}; complete SQL TEXT from bytes: length(TEXT)={reader.GetInt64(3)} stored bytes={reader.GetInt64(4)} instr={reader.GetInt64(5)} GetString chars={reader.GetString(6).Length}");
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
