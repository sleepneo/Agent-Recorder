using System.Text;
using AgentRecorder.Api;
using AgentRecorder.Capture;
using AgentRecorder.App;
using AgentRecorder.Core;
using AgentRecorder.Core.Automation;
using AgentRecorder.Infrastructure;
using AgentRecorder.Logging;
using AgentRecorder.Persistence;
using AgentRecorder.Windows;
using Microsoft.Data.Sqlite;
using Xunit;
using Xunit.Abstractions;
using static AgentRecorder.Tests.UnifiedHistoryTestDatabase;

namespace AgentRecorder.Tests;

[Collection("NonParallel-AgentRecorderDataDir")]
public sealed class RetentionSharedOutputPathTests(ITestOutputHelper output)
{
    internal sealed class Fixture : IDisposable
    {
        internal readonly UnifiedHistoryTestDatabase Db;
        internal readonly RetentionPreviewQueryTests.MetadataFake Files = new();
        internal UnifiedHistoryQueryService Query;
        internal RetentionPreviewQueryService Service;
        internal DateTimeOffset Before = At.AddHours(1);
        internal int OldProbes;
        internal Fixture(bool headless=false,string? root=null)
        {
            Db=new(root??Path.Combine(TestHelper.ProjectRoot,".local-data/temp/task325r",Guid.NewGuid().ToString("N")));
            Query=headless?new(Db.Store,Db.Ordinary,true):Db.Query;
            Query.OrdinaryFileProbeForTests=_=>{OldProbes++;throw new IOException("SECRET_EARLY_PROBE");};
            Service=new(Query.GetOrdinaryEvidencePage,Db.Root,Files,Files,()=>At.AddDays(5));
        }
        internal string PathFor(string name="Ä-Shared.mp4")=>Path.Combine(Db.Root,"Videos",name);
        internal OrdinaryRecordingHistorySnapshot Seed(string id="target",string? path=null,string status="completed",DateTimeOffset? at=null,string? role=null)
        {
            var row=Db.Recording(id,at,status=="completed") with{OutputPath=path??PathFor(),Status=status,NestedRole=role};row=PublicationAnchorTestFixture.Anchored(row);if (row.Status == "completed") row = row with { BundleRegistration = new(null) }; Db.Ordinary.Register(row);return row;
        }
        internal RetentionPreviewPage Page(int limit=20,string? cursor=null)=>Service.GetPage(Sid,Session,limit,Before,cursor);
        internal void NoProbes(){Assert.Empty(Files.Attributes);Assert.Empty(Files.Lengths);Assert.Empty(Files.Observations);Assert.Equal(0,OldProbes);}
        internal string Snapshot()=>UnifiedHistoryProfileFilterTests.RawSnapshot(Db.Store.DatabasePath);
        internal void Future(string path,bool completed=true,string id="SECRET_FOREIGN_FUTURE")
        {
            var scope=Db.SeedFuture(id,sid:"foreign-sid",session:"foreign-session",outputPath:completed?path:null);
            if(!completed)return;
            var receipt=Db.StartFuture(scope);Db.Future.CompleteRun(id,receipt.RunId,true,"",path,1024,1000,At.AddSeconds(63));
        }
        // Real trusted setup, persisted claim/commit/settlement. No capture/UI process.
        internal async Task Required(string path,bool completed=true,bool claim=true,bool commit=false,string? directory=null)
        {
            var display=new StandingLeaseDisplayMetadata("DISPLAY-1","stable-display",DisplayIdentityResolutionStatus.Resolved,new(0,0,1920,1080),96,96,1920,1080,AuthorizedDisplayOrientation.Landscape);
            using var setup=new RequiredOncePlanSetupCoordinator(Db.Store,new QuietAudit(),new RequiredUi(),currentDisplaysForTest:()=>new[]{display},
                principalForTest:()=>new RequiredOncePrincipal("foreign-sid","foreign-session"),clockForTest:()=>At,outputReadinessForTest:new ReadyOutput());
            var created=setup.CreateOrGet(new RequiredOncePlanApiRequest("private-required-key",At.AddMinutes(2),At.AddMinutes(3),At.AddMinutes(6),TimeSpan.FromSeconds(60),directory??Path.GetDirectoryName(path)!,Path.GetFileName(path)));
            await setup.WaitForIdleAsync().WaitAsync(TimeSpan.FromSeconds(5));Assert.Equal("scheduled",setup.Get(created.SetupIntentId!)!.StatusCode);
            if(!claim)return;
            var repo=new SqliteRequiredOnceExecutionRepository(Db.Store);var now=At.AddMinutes(2);var candidate=Assert.Single(repo.ListDue("foreign-sid","foreign-session",now));
            Assert.True(repo.ClaimForConfirmation(candidate,"SECRET_REQUIRED_EXECUTION",now).Succeeded);
            if(!completed&&!commit)return;
            Assert.NotNull(repo.CommitStart("SECRET_REQUIRED_EXECUTION",candidate.Specification,"private-approval","private-proof",new string('a',32),now,now,"foreign-sid","foreign-session",()=>null));
            if(!completed)return;
            Assert.True(repo.ObserveFirstFrame("SECRET_REQUIRED_EXECUTION",now.AddSeconds(1)).Succeeded);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);File.WriteAllBytes(path,new byte[1024]);
            Assert.True(repo.SettleFinalization("SECRET_REQUIRED_EXECUTION",true,new OutputMeta{OutputPath=path,SizeBytes=1024,DurationSeconds=10,OutputFileExists=true},0,null,now.AddSeconds(10)).Succeeded);
        }
        internal string Run(bool completed=true,bool commit=true)
        {
            var created=At.AddMinutes(-1);var profile=new SqliteRecurringFixedRegionProfileRepository(Db.Store).CreateVersion1("shared-profile",created,UnifiedHistoryProfileFilterTests.Specification(Path.Combine(Db.Root,"Videos")));
            var schedule=RecurringPlanSchedule.CreateDaily(TimeZoneInfo.Utc.Id,new(2026,10,5),new(2026,10,8),new(1,0),3,TimeSpan.FromMinutes(2),TimeSpan.FromMinutes(5));
            var setup=new SqliteRecurringPlanDraftSetupTransaction(Db.Store).CreateOrGet("SECRET_RECURRING_PLAN",schedule,profile.Reference,created);
            var end=RecurringScheduleAuthorizationBounds.GetLatestValidPlannedEndUtc(setup.Plan.Id,setup.ScheduleVersion.ScheduleRevision,schedule);
            var lease=RecurringConsentLease.CreatePending("shared-lease",setup.ConfigurationRef,created,end.AddHours(2),end,TimeSpan.FromMinutes(2),3,TimeSpan.FromMinutes(6),created);
            new SqliteRecurringConsentLeaseRepository(Db.Store).InsertPending(lease);
            var approval=RecurringLeaseLocalApprovalReceipt.CreateForTrustedLocalApprovalAdapter("shared-approval",lease.LeaseId,lease.PlanId,lease.ConfigurationRef.ConfigurationDigest,lease.AuthorizationDigest,"foreign-sid","foreign-session",At);
            Assert.Equal(RecurringLeaseLocalApprovalActivationStatus.Activated,new RecurringLeaseLocalApprovalActivationService(Db.Store,()=>At).Activate(lease.LeaseId,approval).Status);
            var advance=new SqliteRecurringAdvancementTransaction(Db.Store).AdvanceOne(setup.Plan.Id,1,"shared-op",0,created.AddHours(-1),At);Assert.Equal("scheduled",advance.ResultCode);
            var identity=advance.OccurrenceIdentity!;Assert.Equal(PeriodicOccurrenceDueResultCodes.Due,new SqlitePeriodicOccurrenceDueProjectionTransaction(Db.Store).Project(identity,0,At).ResultCode);
            var environment=new RecurringOccurrenceReservationTests.MatchingReservationEnvironmentProvider();
            Assert.Equal(RecurringOccurrenceEnvironmentRecheckResultStatus.Authorized,new RecurringOccurrenceEnvironmentRecheckService(Db.Store,()=>At,environment).Recheck(lease.LeaseId,identity).Status);
            if(!commit){using var c=Db.Store.OpenReadOnlyConnection();using var cmd=c.CreateCommand();cmd.CommandText="SELECT frozen_output_file_path FROM recurring_occurrence_execution_specs;";return (string)cmd.ExecuteScalar()!;}
            Assert.Equal(RecurringOccurrenceReservationStatus.Reserved,new RecurringOccurrenceReservationService(Db.Store,()=>At,()=>"SECRET_RUN",()=>"shared-use").Reserve(lease.LeaseId,identity).Status);
            var receipt=new RecurringOccurrenceStartCommitService(Db.Store,()=>At,environment).Commit(lease.LeaseId,identity).FirstCommitReceipt!;Assert.NotNull(receipt);
            var lifecycle=new SqliteRecurringLeaseLifecycleTransaction(Db.Store,receipt);var path=receipt.Specification.FrozenOutputFilePath;
            if(completed){Assert.True(lifecycle.ObserveFirstFrame(new FirstFrameObservation{FrameNumber=0,TotalSizeBytes=100},At.AddSeconds(1)).Succeeded);
                Assert.True(lifecycle.CompleteTermination(RecurringLeaseLifecycleTerminationKind.NaturalExit,0,new OutputMeta{OutputPath=path,SizeBytes=1024,OutputFileExists=true,DurationSeconds=60},At.AddMinutes(1)).Succeeded);}
            return path;
        }
        public void Dispose()=>Db.Dispose();
    }
    internal sealed class QuietAudit:AuditLogger {public override void Log(string evt,object payload){}}
    private sealed class RequiredUi:IRequiredOncePlanSetupUi
    {
        public bool IsInteractiveDesktopAvailable=>true;
        public Task<StandingPlanSetupSelection> SelectFreshRegionAsync(CancellationToken token)=>Task.FromResult(new StandingPlanSetupSelection("selected",120,140,640,360,"DISPLAY-1","virtual_screen"));
        public Task<RequiredOnceCreationApprovalResult> ShowCreationApprovalAsync(RequiredOnceCreationApprovalDetails details,CancellationToken token)=>Task.FromResult(RequiredOnceCreationApprovalResult.Approved);
    }
    private sealed class ReadyOutput:IStandingLeaseOutputReadinessProvider
    {
        public StandingLeaseOutputReadinessResult Check(string directory,string name,TimeSpan duration){var path=StandingLeaseOutputPath.NormalizeDirectory(directory);return StandingLeaseOutputReadinessResult.Ready(new(path,Path.GetFullPath(Path.Combine(path,name)),true,false,true,true,long.MaxValue,1));}
    }
    [Theory]
    [InlineData("ordinary")][InlineData("run")][InlineData("required")][InlineData("future")]
    public async Task FourRealTablesProtectBeforeMetadataWithoutReadingForeignEvidence(string source)
    {
        using var f=new Fixture();var path=source=="run"?f.Run():f.PathFor();f.Seed(path:path);
        if(source=="ordinary")f.Seed("SECRET_OTHER",path,status:"failed",at:f.Before.AddDays(1));
        if(source=="required")await f.Required(path);
        if(source=="future")f.Future(path);
        var before=f.Snapshot();var page=f.Page(1);var item=Assert.Single(page.Items);Assert.Equal("shared_output_path",item.ReasonCode);Assert.Equal("protected",item.Evaluation);Assert.True(item.AgeCandidate);Assert.Null(item.ObservedFileBytes);
        Assert.Equal(1,page.UnknownCandidateFileCount);Assert.Equal(0,page.ObservedCandidateBytes);f.NoProbes();Assert.Equal(before,f.Snapshot());
        Assert.Empty(f.Query.GetPage(Sid,Session,20,new("plan")).Items);
    }
    [Theory]
    [InlineData("recording",true)][InlineData("failed",true)][InlineData("preparing",false)][InlineData("completed",false)]
    public void OrdinaryReferenceIgnoresStatusCutoffPageAndFilters(string status,bool newer)
    {
        using var f=new Fixture();f.Seed(at:At.AddMinutes(1));f.Seed("SECRET_OTHER",status:status,at:newer?f.Before.AddDays(1):At.AddDays(-1));
        Assert.Equal("shared_output_path",Assert.Single(f.Page(1).Items).ReasonCode);f.NoProbes();
    }
    [Theory]
    [InlineData("same",true)][InlineData("ascii",true)][InlineData("unicode",true)][InlineData("different",false)][InlineData("normalized_unicode",false)]
    public void RawOrdinalIgnoreCaseIsConservativeWithoutNormalization(string variant,bool matched)
    {
        using var f=new Fixture();var path=f.PathFor();f.Seed(path:path);
        var other=variant switch{"ascii"=>path.ToUpperInvariant(),"unicode"=>path.Replace("Ä","ä"),"different"=>f.PathFor("other.mp4"),"normalized_unicode"=>f.PathFor("A\u0308-Shared.mp4"),_=>path};
        f.Seed("other",other,"failed",f.Before.AddDays(1));var item=Assert.Single(f.Page().Items);
        Assert.Equal(matched?"shared_output_path":"age_rule_match",item.ReasonCode);if(matched)f.NoProbes();else Assert.Single(f.Files.Lengths);
    }
    [Fact]
    public void SelfDoesNotProtectAndManyReferencesOnlyExposeOneReasonWithNestedPriority()
    {
        using var f=new Fixture();f.Seed();Assert.Equal("age_rule_match",Assert.Single(f.Page().Items).ReasonCode);f.Files.Attributes.Clear();f.Files.Lengths.Clear();f.Files.Observations.Clear();f.Files.ProbeAttributes.Clear();
        f.Seed("other",status:"failed",at:f.Before.AddDays(1));f.Future(f.PathFor());Assert.Equal("shared_output_path",Assert.Single(f.Page().Items).ReasonCode);f.NoProbes();
        f.Db.Execute("UPDATE ordinary_recording_history SET nested_role='outer' WHERE recording_id='target';");Assert.Equal("nested_relationship",Assert.Single(f.Page().Items).ReasonCode);f.NoProbes();
    }
    [Fact]
    public void ValidLegacyNullRunPathCannotMatchItsPlannedOutput()
    {
        using var f=new Fixture();var path=f.Run();f.Seed(path:path);
        // Reuse the legitimate v16 migration backfill for a historical settled
        // run. Its immutable evidence is NULL, even when the planned path matches.
        foreach(var index in SqliteSchemaV29.Indexes.Concat(SqliteSchemaV28.Indexes))f.Db.Execute("DROP INDEX "+index.Name+";");
        f.Db.Execute("DELETE FROM schema_migrations WHERE version=29; DELETE FROM schema_migrations WHERE version=28; DROP TABLE recording_run_output_evidence; DELETE FROM schema_migrations WHERE version=16;");
        f.Db.Store.Initialize();Assert.Equal(1,f.Db.Scalar("SELECT COUNT(*) FROM recording_run_output_evidence WHERE output_path IS NULL AND evidence_kind_code='legacy_path_unavailable';"));
        var evidence=Assert.Single(f.Query.GetOrdinaryEvidencePage(Sid,Session,1,f.Before,null).Items);
        Assert.Equal(SharedOutputPathReferenceState.None,evidence.SharedOutputReference);Assert.Equal(PlannedOutputPathReferenceState.Referenced,evidence.PlannedOutputReference);
        var before=f.Snapshot();Assert.Equal("planned_output_path",Assert.Single(f.Page().Items).ReasonCode);f.NoProbes();Assert.Equal(before,f.Snapshot());
    }
    [Theory]
    [InlineData("required")][InlineData("future")][InlineData("run")]
    public async Task ValidNullPathsAndDifferentPathsDoNotInventAReference(string source)
    {
        using var f=new Fixture();f.Seed();if(source=="required")await f.Required(f.PathFor("pending.mp4"),false);if(source=="future")f.Future(f.PathFor(),false);if(source=="run")f.Run(false);
        Assert.Equal("age_rule_match",Assert.Single(f.Page().Items).ReasonCode);Assert.Single(f.Files.Lengths);
    }
    [Theory]
    [InlineData("run")][InlineData("required")][InlineData("future")]
    public async Task DifferentExplicitPlanPathsContinueOrdinaryMetadataEvaluation(string source)
    {
        using var f=new Fixture();f.Seed();
        if(source=="run")f.Run();if(source=="required")await f.Required(f.PathFor("different.mp4"));if(source=="future")f.Future(f.PathFor("different.mp4"));
        Assert.Equal("age_rule_match",Assert.Single(f.Page().Items).ReasonCode);Assert.Single(f.Files.Lengths);Assert.Equal(0,f.OldProbes);
    }
    [Theory]
    [InlineData("state","not_completed")][InlineData("completion","completion_time_unknown")]
    [InlineData("evidence","output_evidence_missing")][InlineData("cutoff","completion_not_before_cutoff")]
    [InlineData("external","outside_videos")][InlineData("reserved","path_invalid")][InlineData("extension","not_mp4")]
    public void PriorReasonsStillWinOverRealSharedPathReferences(string kind,string reason)
    {
        using var f=new Fixture();var row=f.Db.Recording("target",completed:true) with{OutputPath=f.PathFor()};
        row=kind switch{
            "state"=>row with{Status="failed",StartedAtUtc=null,CompletedAtUtc=null,OutputSizeBytes=null,OutputDurationSeconds=null},
            "completion"=>row with{CompletedAtUtc=null},"evidence"=>row with{OutputSizeBytes=0},
            "cutoff"=>row with{CompletedAtUtc=f.Before},"external"=>row with{OutputPath=Path.Combine(f.Db.Root,"Other","out.mp4")},
            "reserved"=>row with{OutputPath=f.PathFor("CON.mp4")},_=>row with{OutputPath=f.PathFor("out.png")}};
        f.Db.Ordinary.Register(row);f.Seed("other",row.OutputPath,"failed",f.Before.AddDays(1));
        var item=Assert.Single(f.Page().Items);Assert.Equal(reason,item.ReasonCode);Assert.False(item.AgeCandidate);f.NoProbes();
    }
    [Theory]
    [InlineData("trim")][InlineData("relative")][InlineData("separator")][InlineData("unicode_normalization")]
    public void RegisteredCollationNeverResolvesTextualAliases(string kind)
    {
        using var f=new Fixture();using var c=f.Db.Store.OpenReadOnlyConnection();using var cmd=c.CreateCommand();var path=f.PathFor();
        var alias=kind switch{"trim"=>path+" ","relative"=>"Videos/Ä-Shared.mp4","separator"=>path.Replace('\\','/'),_=>path.Normalize(NormalizationForm.FormD)};
        cmd.CommandText="SELECT $path COLLATE "+SqliteOutputPathCollation.Name+"=$alias;";cmd.Parameters.AddWithValue("$path",path);cmd.Parameters.AddWithValue("$alias",alias);Assert.Equal(0L,cmd.ExecuteScalar());
    }
    [Theory]
    [InlineData("missing")][InlineData("invalid")][InlineData("throwing")][InlineData("null_provider")]
    public void MissingOrIllegalPrivateEvidenceNeverDefaultsToNoReference(string fault)
    {
        using var f=new Fixture();f.Seed();var page=f.Query.GetOrdinaryEvidencePage(Sid,Session,20,f.Before,null);var changed=page with{Items=page.Items.Select(e=>e with{SharedOutputReference=fault=="missing"?null:(SharedOutputPathReferenceState)999}).ToArray()};
        Func<string,string,int,DateTimeOffset,string?,OrdinaryRetentionEvidencePage> read=(_,_,_,_,_)=>fault=="throwing"?throw new IOException("SECRET_PROVIDER"):changed;
        var service=new RetentionPreviewQueryService(fault=="null_provider"?null!:read,f.Db.Root,f.Files,f.Files,()=>At.AddDays(5));Assert.Throws<RetentionPreviewUnavailableException>(()=>service.GetPage(Sid,Session,20,f.Before));f.NoProbes();
    }
    [Theory]
    [InlineData("sql")][InlineData("collation_missing")][InlineData("comparison")]
    public void SqlAndCollationFaultsFailClosedBeforeFileProbes(string fault)
    {
        using var f=new Fixture();f.Seed();f.Seed("other",status:"failed",at:f.Before.AddDays(1));
        f.Query.RetentionPathCommandForTests=(c,t,sql,args)=>{if(fault=="sql")throw new IOException("SECRET_SQL");if(fault=="collation_missing")c.CreateCollation(SqliteOutputPathCollation.Name,null);else c.CreateCollation(SqliteOutputPathCollation.Name,(_,_)=>throw new IOException("SECRET_COMPARISON"));};
        var before=f.Snapshot();Assert.Throws<RetentionPreviewUnavailableException>(()=>f.Page());f.NoProbes();Assert.Equal(before,f.Snapshot());
    }
    [Theory]
    [InlineData(true)][InlineData(false)]
    public async Task ReferenceInsertAndDeleteAfterSnapshotNeverMixTwoSnapshots(bool insert)
    {
        using var f=new Fixture();f.Seed();if(!insert)f.Seed("other",status:"failed",at:f.Before.AddDays(1));
        using var entered=new ManualResetEventSlim();using var release=new ManualResetEventSlim();f.Query.EntriesValidatedForTests=(_,_)=>{entered.Set();Assert.True(release.Wait(TimeSpan.FromSeconds(10)));};
        var pending=Task.Run(()=>f.Page(1));try{Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));if(insert)f.Seed("other",status:"failed",at:f.Before.AddDays(1));else f.Db.Execute("BEGIN; DELETE FROM ordinary_recording_window_targets WHERE recording_id='other'; DELETE FROM ordinary_recording_targets WHERE recording_id='other'; DELETE FROM ordinary_recording_history WHERE recording_id='other'; COMMIT;");}finally{release.Set();}
        Assert.Equal(insert?"age_rule_match":"shared_output_path",Assert.Single((await pending).Items).ReasonCode);f.Query.EntriesValidatedForTests=null;f.Files.Attributes.Clear();f.Files.Lengths.Clear();f.Files.Observations.Clear();f.Files.ProbeAttributes.Clear();
        Assert.Equal(insert?"shared_output_path":"age_rule_match",Assert.Single(f.Page(1).Items).ReasonCode);if(insert)f.NoProbes();
    }
    [Fact]
    public void ReferencesInsertedBeyondCandidateWatermarkStillProtectLaterPage()
    {
        using var f=new Fixture();f.Seed("a",f.PathFor("a.mp4"));f.Seed("b",f.PathFor("b.mp4"));var cursor=f.Page(1).NextCursor!;
        f.Seed("other",f.PathFor("b.mp4"),"failed",f.Before.AddDays(1));f.Files.Attributes.Clear();f.Files.Lengths.Clear();f.Files.Observations.Clear();f.Files.ProbeAttributes.Clear();Assert.Equal("shared_output_path",Assert.Single(f.Page(1,cursor).Items).ReasonCode);f.NoProbes();
    }
    [Fact]
    public void ActualLargeFirstAndNextPagesUseFourPathIndexesInOneBoundedBatch()
    {
        using var f=new Fixture();for(var i=0;i<130;i++)f.Seed($"candidate{i:000}",f.PathFor($"{i:000}.mp4"));
        f.Db.Execute("""
            WITH RECURSIVE n(x) AS(SELECT 1 UNION ALL SELECT x+1 FROM n WHERE x<20000)
            INSERT INTO ordinary_recording_history(recording_id,history_sequence,created_at_utc,status_code,source_type,capture_semantics,source_title,requested_duration_seconds,audio_mode,output_path,updated_at_utc)
            SELECT 'reference'||printf('%08d',x),130+x,$at,'failed','window','window_capture','private',15,'none',CASE WHEN x<=130 THEN $root||printf('%03d',x-1)||'.mp4' ELSE $noise END,$at FROM n;
            """,("$at",f.Before.AddDays(1).UtcTicks),("$root",Path.Combine(f.Db.Root,"Videos")+Path.DirectorySeparatorChar),("$noise",f.PathFor("noise.mp4")));
        var calls=0;f.Query.RetentionPathCommandForTests=(c,t,sql,args)=>
        {
            calls++;Assert.InRange(args.Count,2,200);Assert.DoesNotContain("$before",sql);Assert.DoesNotContain("$watermark",sql);Assert.DoesNotContain("$sid",sql);
            using var explain=c.CreateCommand();explain.Transaction=t;explain.CommandText="EXPLAIN QUERY PLAN "+sql;foreach(var a in args)explain.Parameters.AddWithValue(a.Key,a.Value);
            using var r=explain.ExecuteReader();var plans=new List<string>();while(r.Read())plans.Add(r.GetString(3));foreach(var p in SqliteSchemaV28.Paths)Assert.Contains(plans,s=>s.Contains("USING ")&&s.Contains(p.Index)&&s.Contains("output_path=?"));output.WriteLine($"Candidates={args.Count/2}; parameters={args.Count}; SQL={sql}");output.WriteLine(string.Join(" | ",plans));
        };
        var first=f.Page(100);var second=f.Page(100,first.NextCursor);Assert.Equal(130,first.Items.Count+second.Items.Count);Assert.Equal(2,calls);Assert.All(first.Items.Concat(second.Items),e=>Assert.Equal("shared_output_path",e.ReasonCode));f.NoProbes();
    }
    public static IEnumerable<object[]> SchemaFaults=>SqliteSchemaV28.Paths.Select(p=>new object[]{"DROP INDEX "+p.Index+";"}).Concat(new[]{
        new object[]{"DROP INDEX idx_ordinary_recording_history_shared_output_path; CREATE INDEX idx_ordinary_recording_history_shared_output_path ON ordinary_recording_history(output_path COLLATE NOCASE);"},
        new object[]{"DROP INDEX idx_future_window_authorizations_shared_output_path; CREATE INDEX idx_future_window_authorizations_shared_output_path ON future_window_authorizations(authorization_id COLLATE AGENTRECORDER_PATH_ORDINAL_IGNORE_CASE_V1);"},
        new object[]{"DROP INDEX idx_ordinary_recording_history_shared_output_path; CREATE INDEX idx_ordinary_recording_history_shared_output_path ON future_window_authorizations(output_path COLLATE AGENTRECORDER_PATH_ORDINAL_IGNORE_CASE_V1);"},
        new object[]{"DROP INDEX idx_ordinary_recording_history_shared_output_path; CREATE INDEX idx_ordinary_recording_history_shared_output_path ON ordinary_recording_history(output_path COLLATE AGENTRECORDER_PATH_ORDINAL_IGNORE_CASE_V1 DESC);"},
        new object[]{"DROP INDEX idx_ordinary_recording_history_shared_output_path; CREATE INDEX idx_ordinary_recording_history_shared_output_path ON ordinary_recording_history(recording_id,output_path COLLATE AGENTRECORDER_PATH_ORDINAL_IGNORE_CASE_V1);"},
        new object[]{"UPDATE schema_migrations SET name='wrong' WHERE version=28;"},new object[]{"UPDATE schema_migrations SET definition_checksum='0000000000000000000000000000000000000000000000000000000000000000' WHERE version=28;"},new object[]{"DELETE FROM schema_migrations WHERE version=28;"}});
    [Theory,MemberData(nameof(SchemaFaults))]
    public void IndexAndReceiptDamageCannotTriggerReadRepair(string sql)
    {using var f=new Fixture();f.Seed();f.Db.Execute(sql);var before=f.Snapshot();Assert.Throws<RetentionPreviewUnavailableException>(()=>f.Page());Assert.Throws<SqliteOperationalStoreException>(()=>f.Db.Store.Initialize());f.NoProbes();Assert.Equal(before,f.Snapshot());}
    [Fact]
    public void NewStoreReopenAndGenuineV27UpgradeOnlyAddIndexesAndReceipt()
    {
        using var f=new Fixture();f.Seed();var path=Path.Combine(f.Db.Root,"v27.db");
        using(var c=new SqliteConnection($"Data Source={path};Pooling=False"))
        {
            c.Open();SqliteOutputPathCollation.Register(c);using var cmd=c.CreateCommand();foreach(var m in SqliteSchemaCatalog.Migrations.Where(m=>m.Version<=27))
            {cmd.CommandText=m.Definition;cmd.ExecuteNonQuery();cmd.CommandText="INSERT INTO schema_migrations VALUES($v,$n,$c,$at);";cmd.Parameters.AddWithValue("$v",m.Version);cmd.Parameters.AddWithValue("$n",m.Name);cmd.Parameters.AddWithValue("$c",m.Checksum);cmd.Parameters.AddWithValue("$at",1000+m.Version);cmd.ExecuteNonQuery();cmd.Parameters.Clear();}
            cmd.CommandText="ATTACH DATABASE $seed AS seed;";cmd.Parameters.AddWithValue("$seed",f.Db.Store.DatabasePath);cmd.ExecuteNonQuery();cmd.Parameters.Clear();cmd.CommandText="INSERT INTO ordinary_recording_history SELECT * FROM seed.ordinary_recording_history;";cmd.ExecuteNonQuery();
        }
        var before=OldSnapshot(path);var store=new SqliteOperationalStore(path);store.Initialize();Assert.Equal(before,OldSnapshot(path));var once=UnifiedHistoryProfileFilterTests.RawSnapshot(path);store.Initialize();Assert.Equal(once,UnifiedHistoryProfileFilterTests.RawSnapshot(path));
        using var writer=new SqliteOrdinaryRecordingHistoryStore(store);var query=new UnifiedHistoryQueryService(store,writer);var service=new RetentionPreviewQueryService(query.GetOrdinaryEvidencePage,f.Db.Root,f.Files,f.Files,()=>At.AddDays(5));Assert.Equal("publication_identity_missing",Assert.Single(service.GetPage(Sid,Session,20,f.Before).Items).ReasonCode);f.NoProbes();
        using var read=store.OpenReadOnlyConnection();using var cmd2=read.CreateCommand();cmd2.CommandText="PRAGMA integrity_check;";Assert.Equal("ok",cmd2.ExecuteScalar());Assert.Equal(41,SqliteOperationalStore.CurrentSchemaVersion);
    }
    private static string OldSnapshot(string path)
    {
        var lines=UnifiedHistoryProfileFilterTests.RawSnapshot(path,schemaThrough:27).Split('\n').Where(l=>!l.StartsWith("schema_migrations:28|")&&!l.StartsWith("schema_migrations:29|"));
        using var c=new SqliteConnection($"Data Source={path};Mode=ReadOnly;Pooling=False");c.Open();SqliteOutputPathCollation.Register(c);using var cmd=c.CreateCommand();cmd.CommandText="SELECT name,sql FROM sqlite_master WHERE type IN ('trigger','index') AND name NOT LIKE 'future_window_publication%' AND name NOT LIKE 'idx_future_window_publication%' AND tbl_name NOT LIKE 'plan_media_publication%' AND name NOT LIKE 'plan_media_publication%' AND name NOT LIKE 'idx_plan_media_publication%' AND tbl_name NOT LIKE 'future_window_publication%' AND name NOT LIKE 'ordinary_bundle_attachment_hash%' AND name NOT LIKE 'idx_ordinary_bundle_attachment_hash%' AND name NOT LIKE 'ordinary_media_hash%' AND name NOT LIKE 'idx_ordinary_media_hash%' AND name NOT LIKE 'ordinary_bundle_identity%' AND name NOT LIKE 'idx_ordinary_bundle_identity%' AND name NOT LIKE 'ordinary_bundle_contents%' AND name NOT LIKE '%shared_output_path' AND name NOT LIKE '%planned_output_path' AND name NOT IN ('idx_setup_intents_pending_output_path','idx_setup_intents_pending_output_template','idx_recurring_profiles_pending_output_template','idx_required_once_setup_pending_output_path') AND tbl_name NOT IN ('ordinary_bundle_attachment_hash','ordinary_bundle_attachment_hash_boundary','ordinary_bundle_attachment_hash_items','ordinary_media_hash','ordinary_media_hash_boundary','ordinary_bundle_identity','ordinary_bundle_identity_boundary','ordinary_bundle_identity_items','ordinary_recording_window_targets','ordinary_window_target_boundary','ordinary_bundle_contents','ordinary_bundle_contents_boundary','ordinary_bundle_contents_items','ordinary_publication_anchors','ordinary_recording_bundles','ordinary_recording_targets','ordinary_recording_target_boundary') AND name NOT IN ('ordinary_recording_target_initial','ordinary_window_target_initial') ORDER BY name;";
        var definitions=new List<string>();using var r=cmd.ExecuteReader();while(r.Read())definitions.Add(r.GetString(0)+":"+(r.IsDBNull(1)?"":r.GetString(1)));return string.Join("\n",lines.Concat(definitions));
    }
}
