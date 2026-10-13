using System.Text;
using AgentRecorder.Api;
using AgentRecorder.Core;
using AgentRecorder.Core.Automation;
using AgentRecorder.Infrastructure;
using AgentRecorder.Persistence;
using AgentRecorder.Windows;
using Microsoft.Data.Sqlite;
using Xunit;
using Xunit.Abstractions;
using static AgentRecorder.Tests.UnifiedHistoryTestDatabase;

namespace AgentRecorder.Tests;

[Collection("NonParallel-AgentRecorderDataDir")]
public sealed class RetentionPlannedOutputPathTests(ITestOutputHelper output)
{
    internal static RetentionSharedOutputPathTests.Fixture Fixture(bool headless=false) =>
        new(headless, Path.Combine(TestHelper.ProjectRoot,".local-data/temp/task325r","Ä-"+Guid.NewGuid().ToString("N")));
    internal static void LegacyRun(RetentionSharedOutputPathTests.Fixture f)
    {
        foreach(var index in SqliteSchemaV29.Indexes.Concat(SqliteSchemaV28.Indexes))f.Db.Execute("DROP INDEX "+index.Name+";");
        f.Db.Execute("DELETE FROM schema_migrations WHERE version IN (28,29); DROP TABLE recording_run_output_evidence; DELETE FROM schema_migrations WHERE version=16;");
        f.Db.Store.Initialize();
    }
    internal static async Task<string> Purpose(RetentionSharedOutputPathTests.Fixture f,string source,string stage="pending",string? path=null,bool tail=false)
    {
        path??=f.PathFor();
        if(source=="recurring")
        {
            path=f.Run(stage=="terminal",commit:stage!="pending");if(stage=="terminal")LegacyRun(f);return path;
        }
        if(source=="standing")
        {
            var at=At.AddDays(2);var plan=stage=="committed"?PlanDefinition.Rehydrate("SECRET_STANDING_PLAN",true,PlanDefinitionStatus.Enabled,at,at,0):new PlanDefinition("SECRET_STANDING_PLAN",true,at);
            var occurrence=stage=="committed"?PlanOccurrence.Rehydrate("SECRET_STANDING_OCC",plan.Id,at.AddMinutes(1),at.AddMinutes(10),at,PlanOccurrenceStatus.Authorized,null,null,at,0):PlanOccurrence.CreateFor(plan,"SECRET_STANDING_OCC",at.AddMinutes(1),at.AddMinutes(10),at);
            var lease=stage=="committed"?ConsentLease.Rehydrate("SECRET_STANDING_LEASE",plan.Id,occurrence.Id,at,at.AddMinutes(11),1,TimeSpan.FromMinutes(5),ConsentLeaseStatus.Active,at,0):ConsentLease.CreateFor(plan,occurrence,at,at.AddMinutes(11),1,TimeSpan.FromMinutes(5),"SECRET_STANDING_LEASE");
            new SqlitePlanDefinitionRepository(f.Db.Store).Insert(plan);new SqlitePlanOccurrenceRepository(f.Db.Store).Insert(occurrence);new SqliteConsentLeaseRepository(f.Db.Store).Insert(lease);
            var scope=AuthorizedFixedRegionScope.CreateFor(plan,occurrence,lease,"SECRET_STANDING_SCOPE",at,
                AuthorizedScopeTargetType.FixedRegion,AuthorizedCaptureSemantics.DesktopRegion,AuthorizedCoordinateSpace.PhysicalVirtualScreen,
                AuthorizedDisplayIdentityStatus.Resolved,"stable-display",new(0,0,1920,1080),new(10,20,640,360),96,96,1920,1080,
                AuthorizedDisplayOrientation.Landscape,AuthorizedCaptureBackend.FfmpegRegion,AuthorizedAudioMode.None,TimeSpan.FromMinutes(1),3,
                Path.GetDirectoryName(path)!,Path.GetFileName(path),AuthorizedOutputConflictPolicy.FailIfExists,AuthorizedWakePolicy.NaturalWakeOnly,
                AuthorizedDesktopRequirement.InteractiveDesktopRequired,"foreign-sid","foreign-session",new string('a',64));
            new SqliteAuthorizedCaptureScopeRepository(f.Db.Store).Insert(scope);
            f.Db.BindPlan(plan.Id,"foreign-sid","foreign-session");
            if(stage=="terminal"){Assert.True(lease.TryTransition(ConsentLeaseStatus.Revoked,at.AddMinutes(12)).Succeeded);new SqliteConsentLeaseRepository(f.Db.Store).Update(lease,0);}
            if(stage=="committed")Assert.Equal(Phase3StartGateCommitStatus.Committed,new SqlitePhase3StartGateTransaction(f.Db.Store).Commit(new(plan.Id,occurrence.Id,lease.Id,scope.ScopeId,scope.ScopeDigest,"SECRET_STANDING_RUN","SECRET_STANDING_USE",0,0,0,scope.ReservedDuration,at.AddMinutes(1).AddSeconds(1))).Status);
            return scope.OutputFilePath;
        }
        if(source=="required")
        {
            var directory=Path.GetDirectoryName(path)!;if(tail)directory+=Path.DirectorySeparatorChar;
            await f.Required(Path.Combine(directory,Path.GetFileName(path)),false,claim:stage!="pending",commit:stage=="committed",directory:directory);
            if(stage=="terminal")Assert.True(new SqliteRequiredOnceExecutionRepository(f.Db.Store).SettleBeforeStart("SECRET_REQUIRED_EXECUTION","expired","confirmation_expired",At.AddMinutes(7)).Succeeded);
            return path;
        }
        var dir=Path.GetDirectoryName(path)!;if(tail)dir+=Path.DirectorySeparatorChar;
        var future=new FutureWindowAuthorizationScope("SECRET_FROZEN_FUTURE","private-future-key",new string('a',64),"foreign-sid","foreign-session",
            new FutureWindowExecutableIdentity(1,@"C:\Nonexistent\Player.exe","fixture-file-identity",new string('a',64),null,null),null,null,60,300,
            dir,Path.GetFileName(path),"pending",At.AddDays(2),null,null,null,null,0);
        Assert.Equal(FutureWindowCreateDisposition.Created,f.Db.Future.CreateOrGet(future).Disposition);
        if(stage=="committed")f.Db.StartFuture(future);
        if(stage=="terminal")Assert.Equal("revoked",f.Db.Future.Revoke(future.AuthorizationId,"foreign-sid","foreign-session",future.CreatedAtUtc.AddHours(1))!.StatusCode);
        return future.OutputFilePath;
    }
    [Theory]
    [InlineData("standing","pending")][InlineData("standing","committed")][InlineData("standing","terminal")]
    [InlineData("recurring","pending")][InlineData("recurring","committed")][InlineData("recurring","terminal")]
    [InlineData("required","pending")][InlineData("required","committed")][InlineData("required","terminal")]
    [InlineData("future","pending")][InlineData("future","committed")][InlineData("future","terminal")]
    public async Task FourSourcesProtectFrozenPurposeAcrossIdentityDatesAndLifecycle(string source,string stage)
    {
        using var f=Fixture();var path=await Purpose(f,source,stage);f.Seed(path:path);var before=f.Snapshot();
        var privateItem=Assert.Single(f.Query.GetOrdinaryEvidencePage(Sid,Session,1,f.Before,null).Items);
        Assert.Equal(SharedOutputPathReferenceState.None,privateItem.SharedOutputReference);Assert.Equal(PlannedOutputPathReferenceState.Referenced,privateItem.PlannedOutputReference);
        var page=f.Page(1);var item=Assert.Single(page.Items);Assert.Equal("planned_output_path",item.ReasonCode);Assert.Equal("protected",item.Evaluation);Assert.True(item.AgeCandidate);Assert.Null(item.ObservedFileBytes);
        Assert.Equal(1,page.UnknownCandidateFileCount);Assert.Equal(0,page.ObservedCandidateBytes);Assert.True(page.UnknownCandidateFileCount>0);f.NoProbes();Assert.Equal(before,f.Snapshot());
        Assert.Empty(f.Query.GetPage(Sid,Session,20,new("plan")).Items);Assert.Empty(f.Query.GetPage(Sid,Session,20,new("future_window")).Items);
    }
    [Theory]
    [InlineData("standing","directory_only")][InlineData("standing","name_only")][InlineData("standing","prefix")][InlineData("standing","child")]
    [InlineData("recurring","directory_only")][InlineData("recurring","name_only")][InlineData("recurring","prefix")][InlineData("recurring","child")]
    [InlineData("required","directory_only")][InlineData("required","name_only")][InlineData("required","prefix")][InlineData("required","child")]
    [InlineData("future","directory_only")][InlineData("future","name_only")][InlineData("future","prefix")][InlineData("future","child")]
    public async Task OnlyAConcreteFileMatchesRatherThanItsDirectoryOrName(string source,string kind)
    {
        using var f=Fixture();var planned=await Purpose(f,source);var parent=Path.GetDirectoryName(planned)!;var name=Path.GetFileName(planned);
        var candidate=kind switch{"directory_only"=>Path.Combine(parent,"different.mp4"),"name_only"=>Path.Combine(parent,"Sibling",name),"prefix"=>Path.Combine(parent+"-old",name),_=>Path.Combine(parent,"Child",name)};
        f.Seed(path:candidate);var item=Assert.Single(f.Page().Items);
        Assert.Equal(kind=="prefix"?"outside_videos":"age_rule_match",item.ReasonCode);if(kind=="prefix")f.NoProbes();else Assert.Single(f.Files.Lengths);
    }
    [Theory]
    [InlineData("standing")][InlineData("recurring")][InlineData("required")][InlineData("future")]
    public async Task RawAsciiAndNonAsciiCaseComparisonConservativelyProtects(string source)
    {using var f=Fixture();var path=await Purpose(f,source);f.Seed(path:path.ToUpperInvariant().Replace("Ä","ä"));Assert.Equal("planned_output_path",Assert.Single(f.Page().Items).ReasonCode);f.NoProbes();}
    [Theory]
    [InlineData(false)][InlineData(true)]
    public async Task FutureDirectoryFormsDoNotChangeFrozenDigestOrExecutionPath(bool tail)
    {using var f=Fixture();var path=await Purpose(f,"future",tail:tail);var row=f.Db.Future.Get("SECRET_FROZEN_FUTURE","foreign-sid","foreign-session")!;var digest=row.ToScope().AuthorizationDigest;f.Seed(path:path);Assert.Equal("planned_output_path",Assert.Single(f.Page().Items).ReasonCode);Assert.Equal(digest,f.Db.Future.Get(row.AuthorizationId,"foreign-sid","foreign-session")!.ToScope().AuthorizationDigest);f.NoProbes();}
    [Theory]
    [InlineData(false)][InlineData(true)]
    public async Task RequiredStoredDirectoryFormsMatchWithoutRewritingAuthorizedSpec(bool tail)
    {
        using var f=Fixture();var path=await Purpose(f,"required",tail:tail);f.Seed(path:path);var before=f.Snapshot();
        Assert.Equal("planned_output_path",Assert.Single(f.Page().Items).ReasonCode);Assert.Equal(before,f.Snapshot());f.NoProbes();
    }
    [Theory]
    [InlineData("standing")][InlineData("required")][InlineData("future")]
    public async Task CompositeComparisonDoesNotNormalizeUnicodeFileNames(string source)
    {
        using var f=Fixture();await Purpose(f,source,path:f.PathFor("A\u0308-plan.mp4"));f.Seed(path:f.PathFor("Ä-plan.mp4"));
        Assert.Equal("age_rule_match",Assert.Single(f.Page().Items).ReasonCode);Assert.Single(f.Files.Lengths);
    }
    [Fact]
    public async Task ExpiredFutureAuthorizationStillProtectsItsFrozenUse()
    {
        using var f=Fixture();var path=await Purpose(f,"future");var scope=f.Db.Future.Get("SECRET_FROZEN_FUTURE","foreign-sid","foreign-session")!.ToScope();
        Assert.Throws<InvalidOperationException>(()=>f.Db.StartFuture(scope,1000));Assert.Equal("expired",f.Db.Future.Get(scope.AuthorizationId,"foreign-sid","foreign-session")!.StatusCode);
        f.Seed(path:path);Assert.Equal("planned_output_path",Assert.Single(f.Page().Items).ReasonCode);f.NoProbes();
    }
    [Theory]
    [InlineData(@"C:\root.mp4",@"C:\",@"C:\","root.mp4")]
    [InlineData(@"C:\Videos\a.mp4",@"C:\Videos",@"C:\Videos\","a.mp4")]
    public void CandidateSplitPreservesRawRootAndTwoDirectoryForms(string path,string without,string with,string name)
    {Assert.Equal((without,with,name),UnifiedHistoryQueryService.SplitFrozenTarget(path));}
    [Fact]
    public async Task DriveRootFrozenPurposeUsesTheActualCompositeIndex()
    {
        using var f=Fixture();var root=Path.GetPathRoot(f.Db.Root)!;var path=await Purpose(f,"future",path:root+"root.mp4");f.Seed(path:path);
        var evidence=Assert.Single(f.Query.GetOrdinaryEvidencePage(Sid,Session,1,f.Before,null).Items);Assert.Equal(PlannedOutputPathReferenceState.Referenced,evidence.PlannedOutputReference);
        Assert.Equal("outside_videos",Assert.Single(f.Page().Items).ReasonCode);f.NoProbes();
    }
    [Fact]
    public async Task SeveralPurposesExposeOneReasonThenSharedAndNestedReasonsWin()
    {
        using var f=Fixture();var path=await Purpose(f,"standing");await Purpose(f,"required",path:path);await Purpose(f,"future",path:path);f.Seed(path:path);
        Assert.Equal("planned_output_path",Assert.Single(f.Page().Items).ReasonCode);f.NoProbes();
        f.Seed("other",path,"failed",f.Before.AddDays(1));Assert.Equal("shared_output_path",Assert.Single(f.Page().Items).ReasonCode);f.NoProbes();
        f.Db.Execute("UPDATE ordinary_recording_history SET nested_role='outer' WHERE recording_id='target';");Assert.Equal("nested_relationship",Assert.Single(f.Page().Items).ReasonCode);f.NoProbes();
    }
    [Theory]
    [InlineData("state","not_completed")][InlineData("completion","completion_time_unknown")][InlineData("evidence","output_evidence_missing")]
    [InlineData("cutoff","completion_not_before_cutoff")][InlineData("external","outside_videos")][InlineData("invalid","path_invalid")]
    public async Task OldNonCandidateReasonsRemainAheadOfFrozenPurpose(string kind,string reason)
    {
        using var f=Fixture();var path=await Purpose(f,"future");var row=f.Db.Recording("target",completed:true) with{OutputPath=path};
        row=kind switch{"state"=>row with{Status="failed",StartedAtUtc=null,CompletedAtUtc=null,OutputSizeBytes=null,OutputDurationSeconds=null},"completion"=>row with{CompletedAtUtc=null},"evidence"=>row with{OutputSizeBytes=0},"cutoff"=>row with{CompletedAtUtc=f.Before},"external"=>row with{OutputPath=Path.Combine(f.Db.Root,"Elsewhere","out.mp4")},"invalid"=>row with{OutputPath=f.PathFor("CON.mp4")},_=>row};
        f.Db.Ordinary.Register(row);Assert.Equal(reason,Assert.Single(f.Page().Items).ReasonCode);f.NoProbes();
    }
    [Theory]
    [InlineData("missing")][InlineData("illegal")][InlineData("provider")][InlineData("indeterminate")]
    public void MissingOrUntrustworthyPrivateResultCannotDefaultToNoPurpose(string fault)
    {
        using var f=Fixture();f.Seed();var p=f.Query.GetOrdinaryEvidencePage(Sid,Session,20,f.Before,null);
        var state=fault=="missing"?null:(PlannedOutputPathReferenceState?)(fault=="indeterminate"?PlannedOutputPathReferenceState.Indeterminate:(PlannedOutputPathReferenceState)999);
        var service=new RetentionPreviewQueryService((_,_,_,_,_)=>fault=="provider"?throw new IOException("SECRET_PROVIDER"):p with{Items=p.Items.Select(e=>e with{PlannedOutputReference=state}).ToArray()},f.Db.Root,f.Files,f.Files,()=>At.AddDays(5));
        Assert.Throws<RetentionPreviewUnavailableException>(()=>service.GetPage(Sid,Session,20,f.Before));f.NoProbes();
    }
    [Fact]
    public void UnknownEligiblePurposeOnLaterItemFailsBeforeSamplingEarlierItem()
    {
        using var f=Fixture();f.Seed("first",f.PathFor("first.mp4"));f.Seed("second",f.PathFor("second.mp4"));
        var page=f.Query.GetOrdinaryEvidencePage(Sid,Session,20,f.Before,null);
        var service=new RetentionPreviewQueryService((_,_,_,_,_)=>page with{Items=page.Items.Select((e,i)=>e with{PlannedOutputReference=i==0?PlannedOutputPathReferenceState.None:PlannedOutputPathReferenceState.Indeterminate}).ToArray()},f.Db.Root,f.Files,f.Files,()=>At.AddDays(5));
        Assert.Throws<RetentionPreviewUnavailableException>(()=>service.GetPage(Sid,Session,20,f.Before));f.NoProbes();
    }
    [Theory]
    [InlineData("sql")][InlineData("missing_collation")][InlineData("comparison")]
    public async Task QueryOrCollationFailureMakesWholePageUnavailableBeforeProbes(string fault)
    {
        using var f=Fixture();var path=await Purpose(f,"future");f.Seed(path:path);var before=f.Snapshot();
        f.Query.RetentionPlannedPathCommandForTests=(c,_,_,_)=>{if(fault=="sql")throw new IOException("SECRET_QUERY");if(fault=="missing_collation")c.CreateCollation(SqliteOutputPathCollation.Name,null);else c.CreateCollation(SqliteOutputPathCollation.Name,(_,_)=>throw new IOException("SECRET_COMPARE"));};
        Assert.Throws<RetentionPreviewUnavailableException>(()=>f.Page());f.NoProbes();Assert.Equal(before,f.Snapshot());
    }
    [Theory]
    [InlineData(true)][InlineData(false)]
    public async Task FrozenInsertAndRemovalUseOneSnapshotAndNextRequestObservesChange(bool insert)
    {
        // Frozen rows are immutable in production. Removal below uses the existing
        // isolated corruption fixture and restores exact triggers; no release API.
        using var f=Fixture();if(!insert)await Purpose(f,"future");f.Seed();
        using var entered=new ManualResetEventSlim();using var release=new ManualResetEventSlim();f.Query.EntriesValidatedForTests=(_,_)=>{entered.Set();Assert.True(release.Wait(TimeSpan.FromSeconds(10)));};
        var pending=Task.Run(()=>f.Page(1));try{Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));if(insert)await Purpose(f,"future");else f.Db.Corrupt("DELETE FROM future_window_authorizations WHERE authorization_id='SECRET_FROZEN_FUTURE';");}finally{release.Set();}
        Assert.Equal(insert?"age_rule_match":"planned_output_path",Assert.Single((await pending).Items).ReasonCode);f.Query.EntriesValidatedForTests=null;f.Files.Attributes.Clear();f.Files.Lengths.Clear();f.Files.Observations.Clear();f.Files.ProbeAttributes.Clear();
        Assert.Equal(insert?"planned_output_path":"age_rule_match",Assert.Single(f.Page().Items).ReasonCode);if(insert)f.NoProbes();
    }
    [Fact]
    public async Task NewPurposeBeyondCandidateWatermarkProtectsLaterPage()
    {using var f=Fixture();f.Seed("a",f.PathFor("a.mp4"));f.Seed("b",f.PathFor("b.mp4"));var cursor=f.Page(1).NextCursor!;await Purpose(f,"future",path:f.PathFor("b.mp4"));f.Files.Attributes.Clear();f.Files.Lengths.Clear();f.Files.Observations.Clear();f.Files.ProbeAttributes.Clear();Assert.Equal("planned_output_path",Assert.Single(f.Page(1,cursor).Items).ReasonCode);f.NoProbes();}
    [Fact]
    public void NoFrozenRowsOrActualOutputRowsInventAReference()
    {using var f=Fixture();f.Seed();var e=Assert.Single(f.Query.GetOrdinaryEvidencePage(Sid,Session,1,f.Before,null).Items);Assert.Equal(SharedOutputPathReferenceState.None,e.SharedOutputReference);Assert.Equal(PlannedOutputPathReferenceState.None,e.PlannedOutputReference);Assert.Equal("age_rule_match",Assert.Single(f.Page().Items).ReasonCode);Assert.Single(f.Files.Lengths);}
    public static IEnumerable<object[]> SchemaFaults=>SqliteSchemaV29.Paths.Select(p=>new object[]{"DROP INDEX "+p.Index+";"}).Concat(new[]{
        new object[]{"DROP INDEX idx_authorized_capture_scopes_planned_output_path; CREATE INDEX idx_authorized_capture_scopes_planned_output_path ON authorized_capture_scopes(frozen_file_name COLLATE AGENTRECORDER_PATH_ORDINAL_IGNORE_CASE_V1,output_directory COLLATE AGENTRECORDER_PATH_ORDINAL_IGNORE_CASE_V1);"},
        new object[]{"DROP INDEX idx_authorized_capture_scopes_planned_output_path; CREATE INDEX idx_authorized_capture_scopes_planned_output_path ON required_once_authorized_specs(output_directory COLLATE AGENTRECORDER_PATH_ORDINAL_IGNORE_CASE_V1,frozen_file_name COLLATE AGENTRECORDER_PATH_ORDINAL_IGNORE_CASE_V1);"},
        new object[]{"DROP INDEX idx_future_window_authorizations_planned_output_path; CREATE INDEX idx_future_window_authorizations_planned_output_path ON future_window_authorizations(output_directory COLLATE NOCASE,output_file_name COLLATE AGENTRECORDER_PATH_ORDINAL_IGNORE_CASE_V1);"},
        new object[]{"DROP INDEX idx_future_window_authorizations_planned_output_path; CREATE INDEX idx_future_window_authorizations_planned_output_path ON future_window_authorizations(output_directory COLLATE AGENTRECORDER_PATH_ORDINAL_IGNORE_CASE_V1,output_file_name COLLATE NOCASE);"},
        new object[]{"DROP INDEX idx_recurring_occurrence_execution_specs_planned_output_path; CREATE INDEX idx_recurring_occurrence_execution_specs_planned_output_path ON recurring_occurrence_execution_specs(frozen_output_file_path COLLATE AGENTRECORDER_PATH_ORDINAL_IGNORE_CASE_V1 DESC);"},
        new object[]{"UPDATE schema_migrations SET name='wrong' WHERE version=29;"},new object[]{"UPDATE schema_migrations SET definition_checksum='0000000000000000000000000000000000000000000000000000000000000000' WHERE version=29;"},new object[]{"DELETE FROM schema_migrations WHERE version=29;"}});
    [Theory,MemberData(nameof(SchemaFaults))]
    public void NewIndexOrReceiptDamageNeverCausesReadRepair(string sql)
    {using var f=Fixture();f.Seed();f.Db.Execute(sql);var before=f.Snapshot();Assert.Throws<RetentionPreviewUnavailableException>(()=>f.Page());Assert.Throws<SqliteOperationalStoreException>(()=>f.Db.Store.Initialize());f.NoProbes();Assert.Equal(before,f.Snapshot());}
    [Fact]
    public async Task ActualFirstAndDeepPageQueryUsesAllFourFrozenIndexesWithNonemptyTables()
    {
        using var f=Fixture();var paths=new List<string>{await Purpose(f,"recurring"),await Purpose(f,"standing",path:f.PathFor("001.mp4")),await Purpose(f,"required",path:f.PathFor("002.mp4")),await Purpose(f,"future",path:f.PathFor("003.mp4"))};
        for(var i=4;i<130;i++)paths.Add(f.PathFor($"{i:000}.mp4"));for(var i=0;i<130;i++)f.Seed($"candidate{i:000}",paths[i]);
        // Valid pending scale rows copied from the real repository fixture; no
        // authorization proof/scope JSON is parsed by the production read.
        using(var c=f.Db.Store.OpenConnection())using(var cmd=c.CreateCommand())
        {
            cmd.CommandText="SELECT name FROM pragma_table_info('future_window_authorizations') ORDER BY cid;";var columns=new List<string>();using(var r=cmd.ExecuteReader())while(r.Read())columns.Add(r.GetString(0));
            var expressions=columns.Select(col=>col switch{"authorization_id"=>"'scale-future-'||printf('%08d',n.x)","idempotency_key"=>"'scale-key-'||printf('%08d',n.x)","output_file_name"=>"CASE WHEN n.x<=126 THEN printf('%03d',n.x+3)||'.mp4' ELSE 'noise.mp4' END",_=>"template."+col});
            cmd.CommandText="WITH RECURSIVE n(x) AS(SELECT 1 UNION ALL SELECT x+1 FROM n WHERE x<20000) INSERT INTO future_window_authorizations("+string.Join(",",columns)+") SELECT "+string.Join(",",expressions)+" FROM n CROSS JOIN future_window_authorizations template WHERE template.authorization_id='SECRET_FROZEN_FUTURE';";cmd.ExecuteNonQuery();
        }
        var calls=0;f.Query.RetentionPlannedPathCommandForTests=(c,t,sql,args)=>
        {
            calls++;Assert.InRange(args.Count,5,500);Assert.DoesNotContain("$sid",sql);Assert.DoesNotContain("$before",sql);Assert.DoesNotContain("watermark",sql);
            using var explain=c.CreateCommand();explain.Transaction=t;explain.CommandText="EXPLAIN QUERY PLAN "+sql;foreach(var a in args)explain.Parameters.AddWithValue(a.Key,a.Value);
            var plans=new List<string>();using(var r=explain.ExecuteReader())while(r.Read())plans.Add(r.GetString(3));foreach(var p in SqliteSchemaV29.Paths)Assert.Contains(plans,s=>s.Contains("SEARCH ")&&s.Contains(p.Index)&&p.Columns.All(col=>s.Contains(col+"=?")));
            output.WriteLine($"Candidates={args.Count/5}; parameters={args.Count}; SQL={sql}");output.WriteLine(string.Join(" | ",plans));
        };
        var first=f.Page(100);var next=f.Page(100,first.NextCursor);Assert.Equal(130,first.Items.Count+next.Items.Count);Assert.Equal(2,calls);Assert.All(first.Items.Concat(next.Items),i=>Assert.Equal("planned_output_path",i.ReasonCode));f.NoProbes();
    }
    [Fact]
    public async Task GenuineV28UpgradeOnlyAddsFourIndexesAndReceiptThenReadOnlyReopens()
    {
        using var f=Fixture();var path=await Purpose(f,"future");f.Seed(path:path);var database=Path.Combine(f.Db.Root,"v28.db");
        using(var c=new SqliteConnection($"Data Source={database};Pooling=False"))
        {
            c.Open();SqliteOutputPathCollation.Register(c);using var cmd=c.CreateCommand();foreach(var m in SqliteSchemaCatalog.Migrations.Where(m=>m.Version<=28))
            {cmd.CommandText=m.Definition;cmd.ExecuteNonQuery();cmd.CommandText="INSERT INTO schema_migrations VALUES($v,$n,$c,$at);";cmd.Parameters.AddWithValue("$v",m.Version);cmd.Parameters.AddWithValue("$n",m.Name);cmd.Parameters.AddWithValue("$c",m.Checksum);cmd.Parameters.AddWithValue("$at",1000+m.Version);cmd.ExecuteNonQuery();cmd.Parameters.Clear();}
            cmd.CommandText="ATTACH DATABASE $seed AS seed;";cmd.Parameters.AddWithValue("$seed",f.Db.Store.DatabasePath);cmd.ExecuteNonQuery();cmd.Parameters.Clear();foreach(var table in new[]{"ordinary_recording_history","future_window_authorizations"}){cmd.CommandText=$"INSERT INTO {table} SELECT * FROM seed.{table};";cmd.ExecuteNonQuery();}
        }
        var before=OldSnapshot(database);var store=new SqliteOperationalStore(database);store.Initialize();Assert.Equal(before,OldSnapshot(database));var once=UnifiedHistoryProfileFilterTests.RawSnapshot(database);store.Initialize();Assert.Equal(once,UnifiedHistoryProfileFilterTests.RawSnapshot(database));
        using var writer=new SqliteOrdinaryRecordingHistoryStore(store);var query=new UnifiedHistoryQueryService(store,writer);var service=new RetentionPreviewQueryService(query.GetOrdinaryEvidencePage,f.Db.Root,f.Files,f.Files,()=>At.AddDays(5));Assert.Equal("planned_output_path",Assert.Single(service.GetPage(Sid,Session,20,f.Before).Items).ReasonCode);f.NoProbes();
        using var read=store.OpenReadOnlyConnection();using var check=read.CreateCommand();check.CommandText="PRAGMA integrity_check;";Assert.Equal("ok",check.ExecuteScalar());Assert.Equal(41,SqliteOperationalStore.CurrentSchemaVersion);
    }
    private static string OldSnapshot(string path)
    {
        var rows=UnifiedHistoryProfileFilterTests.RawSnapshot(path,schemaThrough:28).Split('\n').Where(l=>!l.StartsWith("schema_migrations:29|") && !l.StartsWith("schema_migrations:30|"));
        using var c=new SqliteConnection($"Data Source={path};Mode=ReadOnly;Pooling=False");c.Open();SqliteOutputPathCollation.Register(c);using var cmd=c.CreateCommand();cmd.CommandText="SELECT name,sql FROM sqlite_master WHERE type IN ('trigger','index') AND name NOT LIKE 'future_window_publication%' AND name NOT LIKE 'idx_future_window_publication%' AND tbl_name NOT LIKE 'plan_media_publication%' AND name NOT LIKE 'plan_media_publication%' AND name NOT LIKE 'idx_plan_media_publication%' AND tbl_name NOT LIKE 'future_window_publication%' AND name NOT LIKE 'ordinary_bundle_attachment_hash%' AND name NOT LIKE 'idx_ordinary_bundle_attachment_hash%' AND name NOT LIKE 'ordinary_media_hash%' AND name NOT LIKE 'idx_ordinary_media_hash%' AND name NOT LIKE 'ordinary_bundle_identity%' AND name NOT LIKE 'idx_ordinary_bundle_identity%' AND name NOT LIKE 'ordinary_bundle_contents%' AND name NOT LIKE '%planned_output_path' AND name NOT IN ('idx_setup_intents_pending_output_path','idx_setup_intents_pending_output_template','idx_recurring_profiles_pending_output_template','idx_required_once_setup_pending_output_path') AND tbl_name NOT IN ('ordinary_bundle_attachment_hash','ordinary_bundle_attachment_hash_boundary','ordinary_bundle_attachment_hash_items','ordinary_media_hash','ordinary_media_hash_boundary','ordinary_bundle_identity','ordinary_bundle_identity_boundary','ordinary_bundle_identity_items','ordinary_recording_window_targets','ordinary_window_target_boundary','ordinary_bundle_contents','ordinary_bundle_contents_boundary','ordinary_bundle_contents_items','ordinary_publication_anchors','ordinary_recording_bundles','ordinary_recording_targets','ordinary_recording_target_boundary') AND name NOT IN ('ordinary_recording_target_initial','ordinary_window_target_initial') ORDER BY name;";
        var defs=new List<string>();using var r=cmd.ExecuteReader();while(r.Read())defs.Add(r.GetString(0)+":"+(r.IsDBNull(1)?"":r.GetString(1)));return string.Join("\n",rows.Concat(defs));
    }
}
