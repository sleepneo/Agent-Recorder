using AgentRecorder.Infrastructure;
using AgentRecorder.Persistence;
using Microsoft.Data.Sqlite;
using Xunit;
using static AgentRecorder.Tests.UnifiedHistoryTestDatabase;

namespace AgentRecorder.Tests;

public sealed class UnifiedHistoryMigrationTests
{
    [Fact]
    public void V22MixedDataBackfillsOriginalInsertionOrderAndKeepsBusinessFactsAndChecksums()
    {
        using var d = new UnifiedHistoryTestDatabase(); d.SeedOrdinary("recording", completed: true); d.SeedFuture("future");
        d.SeedPlan("owned"); d.SeedOccurrence("owned", "new-date", At.AddDays(1)); d.SeedOccurrence("owned", "old-date", At.AddDays(-1));
        d.SeedPlan("unowned", owner: false); d.SeedOccurrence("unowned", "unowned-occ");
        var business = OriginalRows(d); d.DowngradeToV22();
        d.Store.Initialize(); d.Store.Initialize(); new SqliteOperationalStore(d.Store.DatabasePath).Initialize();
        Assert.Equal(business, OriginalRows(d));
        Assert.Equal(SqliteOperationalStore.CurrentSchemaVersion, d.Scalar("SELECT MAX(version) FROM schema_migrations;"));
        Assert.Equal(3, d.Scalar("SELECT COUNT(*) FROM plan_history_locator;"));
        Assert.Equal(0, d.Scalar("SELECT COUNT(*) FROM plan_occurrences o JOIN plan_history_locator l ON l.occurrence_id=o.id WHERE l.occurrence_sequence!=o.rowid OR l.created_at_utc!=o.created_at_utc;"));
        Assert.DoesNotContain(d.Page().Items, i => i.OccurrenceId == "unowned-occ");
        Assert.Equal(SqliteSchemaV22.MigrationChecksum, Text(d, "SELECT definition_checksum FROM schema_migrations WHERE version=22;"));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void LocatorMaintainedForBothOwnerBindingOrdersAndReplay(bool ownerFirst)
    {
        using var d = new UnifiedHistoryTestDatabase(); d.SeedPlan("p", owner: ownerFirst); d.SeedOccurrence("p", "o");
        if (!ownerFirst) { Assert.Empty(d.Page().Items); d.BindPlan("p"); }
        Assert.Equal("plan:o", Assert.Single(d.Page().Items).EntryId);
        // Idempotent insert using the same key does not create a second locator.
        d.Execute("INSERT INTO plan_occurrences SELECT * FROM plan_occurrences WHERE id='o' ON CONFLICT(id) DO NOTHING;");
        Assert.Equal(1, d.Scalar("SELECT COUNT(*) FROM plan_history_locator;"));
        Assert.Equal(d.Scalar("SELECT rowid FROM plan_occurrences WHERE id='o';"), d.Scalar("SELECT occurrence_sequence FROM plan_history_locator WHERE occurrence_id='o';"));
    }

    [Fact]
    public void OriginalAndLocatorCreationRollbackAtomicallyAndAmbiguousBindingCannotPickAnOwner()
    {
        using var d = new UnifiedHistoryTestDatabase(); d.SeedPlan("p");
        using (var c = d.Store.OpenConnection())
        using (var t = c.BeginTransaction())
        using (var cmd = c.CreateCommand())
        {
            cmd.Transaction = t;
            cmd.CommandText = "INSERT INTO plan_occurrences(id,plan_id,status_code,window_start_utc,window_end_utc,created_at_utc,updated_at_utc,version) VALUES('rolled-back','p','scheduled',1,2,1,1,0);";
            cmd.ExecuteNonQuery(); cmd.CommandText = "SELECT COUNT(*) FROM plan_history_locator;"; Assert.Equal(1L, cmd.ExecuteScalar()); t.Rollback();
        }
        Assert.Equal(0, d.Scalar("SELECT COUNT(*) FROM plan_occurrences;")); Assert.Equal(0, d.Scalar("SELECT COUNT(*) FROM plan_history_locator;"));
        d.SeedOccurrence("p", "o"); var before = d.Snapshot();
        Assert.Throws<SqliteException>(() => d.BindPlan("p", intent: "other-owner"));
        Assert.Equal(before, d.Snapshot());
    }

    [Fact]
    public void OwnerBindingInsideRolledBackTransactionDoesNotGrantVisibility()
    {
        using var d = new UnifiedHistoryTestDatabase(); d.SeedPlan("p", owner: false); d.SeedOccurrence("p", "o");
        d.SeedPlan("q");
        using(var c=d.Store.OpenConnection()) using(var t=c.BeginTransaction()) using(var cmd=c.CreateCommand())
        {
            cmd.Transaction=t; cmd.CommandText="UPDATE setup_intents SET plan_id='p' WHERE plan_id='q';"; cmd.ExecuteNonQuery();
            cmd.CommandText="SELECT COUNT(*) FROM plan_history_locator WHERE current_user_sid IS NOT NULL;"; Assert.Equal(1L,cmd.ExecuteScalar()); t.Rollback();
        }
        Assert.Empty(d.Page().Items); Assert.Equal(0,d.Scalar("SELECT COUNT(*) FROM plan_history_locator WHERE current_user_sid IS NOT NULL;"));
    }

    [Fact]
    public void AmbiguousV22MigrationFailsClosedAndRollsBackEveryNewObject()
    {
        using var d = new UnifiedHistoryTestDatabase(); d.SeedPlan("p"); d.SeedOccurrence("p", "o"); d.DowngradeToV22();
        d.RawExecute("INSERT INTO setup_intents SELECT 'second-intent',intent_kind_code,'second-key',request_digest,current_user_sid,session_binding,status_code,requested_at_utc,expires_at_utc,plan_id,occurrence_id,lease_id,scope_id,created_at_utc,updated_at_utc,version,terminal_reason_code,scheduled_start_utc,latest_start_utc,planned_end_utc,maximum_duration_ms,lease_valid_until_utc,output_directory,frozen_file_name,recurring_schedule_kind_code,recurring_time_zone_id,recurring_time_zone_rules_digest,recurring_schedule_digest,recurring_local_start_date,recurring_local_end_date,recurring_local_wall_clock_seconds,recurring_weekday_mask,recurring_maximum_occurrences,recurring_recording_duration_ticks,recurring_latest_start_grace_ticks,recurring_max_uses,recurring_max_cumulative_duration_ticks,recurring_authorization_valid_until_utc,recurring_target_type_code,recurring_audio_mode_code,recurring_backend_code,recurring_countdown_seconds,recurring_output_directory,recurring_filename_prefix,recurring_output_conflict_policy_code,recurring_wake_policy_code,recurring_desktop_requirement_code FROM setup_intents WHERE plan_id='p';");
        Assert.Throws<SqliteOperationalStoreException>(() => d.Store.Initialize()); AssertV22Remains(d);
    }

    [Fact]
    public void MigrationLateFailureRollsBackAndRetrySucceeds()
    {
        using var d = new UnifiedHistoryTestDatabase(); d.SeedPlan("p"); d.SeedOccurrence("p", "o"); d.DowngradeToV22();
        d.RawExecute("CREATE TRIGGER reject_v23 BEFORE INSERT ON schema_migrations WHEN NEW.version=23 BEGIN SELECT RAISE(ABORT,'injected migration interruption'); END;");
        Assert.Throws<SqliteOperationalStoreException>(() => d.Store.Initialize()); AssertV22Remains(d);
        d.RawExecute("DROP TRIGGER reject_v23;"); d.Store.Initialize(); Assert.Equal("plan:o", Assert.Single(d.Page().Items).EntryId);
    }

    [Theory]
    [InlineData("DROP INDEX idx_future_window_history_identity_created;")]
    [InlineData("DROP INDEX idx_plan_history_identity_created; CREATE INDEX idx_plan_history_identity_created ON plan_history_locator(current_user_sid,session_binding,created_at_utc ASC,occurrence_id COLLATE BINARY ASC);")]
    [InlineData("DROP TRIGGER trg_history_occurrence_insert;")]
    public void MissingOrReorderedIndexAndMissingMaintenanceTriggerFailClosed(string sql)
    {
        using var d = new UnifiedHistoryTestDatabase(); d.Execute(sql);
        Assert.Throws<SqliteOperationalStoreException>(() => d.Store.Initialize());
        Assert.Throws<AgentRecorder.Core.UnifiedHistoryUnavailableException>(() => d.Page());
    }

    [Fact]
    public void LocatorCannotCreateItsOwnAuthorityAndCorruptAssociationIsRejectedOnReadAndReopen()
    {
        using var d = new UnifiedHistoryTestDatabase(); d.SeedPlan("p"); d.SeedOccurrence("p", "o");
        Assert.Throws<SqliteException>(() => d.Execute("UPDATE plan_history_locator SET current_user_sid='foreign';"));
        Assert.Throws<SqliteException>(() => d.Execute("DELETE FROM plan_history_locator;"));
        d.Corrupt("UPDATE plan_history_locator SET plan_id='missing';");
        Assert.Throws<AgentRecorder.Core.UnifiedHistoryUnavailableException>(() => d.Page());
        Assert.Throws<SqliteOperationalStoreException>(() => d.Store.Initialize());
    }

    [Fact]
    public void IndependentV23DefinitionsUpgradeAdditivelyWithProfilePlanLeaseAndHistoryUnchanged()
    {
        using var d = new UnifiedHistoryTestDatabase(); d.SeedOrdinary("legacy-recording", completed: true);
        d.SeedPlan("legacy-plan"); d.SeedOccurrence("legacy-plan", "legacy-occurrence");
        d.Execute("""
            INSERT INTO consent_leases(id,plan_id,occurrence_id,status_code,valid_from_utc,valid_until_utc,max_uses,max_duration_ms,updated_at_utc,version)
            VALUES('legacy-lease','legacy-plan','legacy-occurrence','pending',$at,$end,1,15000,$at,0);
            """, ("$at", At.UtcTicks), ("$end", At.AddMinutes(1).UtcTicks));
        var specification = new AgentRecorder.Core.Automation.RecurringFixedRegionProfileSpecification(
            AgentRecorder.Core.Automation.AuthorizedScopeTargetType.FixedRegion,
            AgentRecorder.Core.Automation.RecurringFixedRegionRebindPolicy.ExactMatchOnly,
            AgentRecorder.Core.Automation.AuthorizedCaptureSemantics.DesktopRegion,
            AgentRecorder.Core.Automation.AuthorizedCoordinateSpace.PhysicalVirtualScreen,
            AgentRecorder.Core.Automation.AuthorizedDisplayIdentityStatus.Resolved, "DISPLAY-FP-1",
            new(-100,50,1920,1080), new(10,20,640,480), 96,144,1920,1080,
            AgentRecorder.Core.Automation.AuthorizedDisplayOrientation.Landscape, new string('a',64),
            AgentRecorder.Core.Automation.AuthorizedCaptureBackend.FfmpegRegion,
            AgentRecorder.Core.Automation.AuthorizedAudioMode.None, TimeSpan.FromMinutes(2),3,d.Root,"legacy",
            AgentRecorder.Core.Automation.AuthorizedOutputConflictPolicy.FailIfExists,
            AgentRecorder.Core.Automation.AuthorizedWakePolicy.NaturalWakeOnly,
            AgentRecorder.Core.Automation.AuthorizedDesktopRequirement.InteractiveDesktopRequired);
        new SqliteRecurringFixedRegionProfileRepository(d.Store).CreateVersion1(
            AgentRecorder.Core.Automation.RecurringFixedRegionProfileVersion.CreateVersion1("legacy-profile", At, specification));
        var path = Path.Combine(d.Root,"independent-v23.db");
        using (var c = new SqliteConnection($"Data Source={path};Pooling=False"))
        {
            c.Open(); using var cmd = c.CreateCommand(); cmd.CommandText="PRAGMA foreign_keys=ON;"; cmd.ExecuteNonQuery();
            // Build a genuine v23 schema from its immutable definitions, never by dropping v24.
            foreach(var migration in SqliteSchemaCatalog.Migrations.Where(m => m.Version <= 23))
            {
                cmd.CommandText = migration.Definition; cmd.ExecuteNonQuery();
                cmd.CommandText="INSERT INTO schema_migrations(version,name,definition_checksum,applied_at_utc) VALUES($v,$n,$c,$a);";
                cmd.Parameters.AddWithValue("$v",migration.Version); cmd.Parameters.AddWithValue("$n",migration.Name);
                cmd.Parameters.AddWithValue("$c",migration.Checksum); cmd.Parameters.AddWithValue("$a",1000+ migration.Version); cmd.ExecuteNonQuery(); cmd.Parameters.Clear();
            }
            cmd.CommandText="ATTACH DATABASE $source AS seed;"; cmd.Parameters.AddWithValue("$source",d.Store.DatabasePath);cmd.ExecuteNonQuery();cmd.Parameters.Clear();
            foreach(var table in new[]{"plans","setup_intents","plan_occurrences","consent_leases","recurring_fixed_region_profile_versions","ordinary_recording_history"})
            { cmd.CommandText=$"INSERT INTO main.{table} SELECT * FROM seed.{table};"; Assert.Equal(1,cmd.ExecuteNonQuery()); }
            cmd.CommandText="DETACH DATABASE seed;";cmd.ExecuteNonQuery();
            cmd.CommandText="SELECT COUNT(*) FROM sqlite_master WHERE name='idx_ordinary_recording_history_status_created';";Assert.Equal(0L,cmd.ExecuteScalar());
        }
        var before=LegacySnapshot(path); var legacyStore=new SqliteOperationalStore(path);
        legacyStore.Initialize(); var once=LegacySnapshot(path,includeV24:true);
        legacyStore.Initialize();new SqliteOperationalStore(path).Initialize();Assert.Equal(once,LegacySnapshot(path,includeV24:true));
        Assert.Equal(before,LegacySnapshot(path));
        using var writer=new SqliteOrdinaryRecordingHistoryStore(legacyStore); var query=new UnifiedHistoryQueryService(legacyStore,writer);
        var page=query.GetPage(Sid,Session,20,new("ordinary",Status:"completed"),null);Assert.Equal("legacy-recording",Assert.Single(page.Items).RecordingId);
        using var upgraded=legacyStore.OpenReadOnlyConnection();using var index=upgraded.CreateCommand();
        index.CommandText="SELECT COUNT(*) FROM sqlite_master WHERE name IN ('idx_ordinary_recording_history_created','idx_ordinary_recording_history_status_created');";Assert.Equal(2L,index.ExecuteScalar());
        index.CommandText="SELECT definition_checksum FROM schema_migrations WHERE version=24;";Assert.Equal(SqliteSchemaV24.MigrationChecksum,index.ExecuteScalar());
    }
    private static string LegacySnapshot(string path,bool includeV24=false)
    {
        using var c=new SqliteConnection($"Data Source={path};Pooling=False;Mode=ReadOnly");c.Open();var tables=new List<string>();
        using(var cmd=c.CreateCommand()){cmd.CommandText="SELECT name FROM sqlite_master WHERE type='table' AND name NOT LIKE 'sqlite_%' ORDER BY name;";using var r=cmd.ExecuteReader();while(r.Read())if(includeV24 || r.GetString(0) is not ("ordinary_recording_targets" or "ordinary_recording_target_boundary" or "ordinary_recording_window_targets" or "ordinary_window_target_boundary") && !r.GetString(0).StartsWith("ordinary_bundle_contents",StringComparison.Ordinal) && !r.GetString(0).StartsWith("ordinary_bundle_identity",StringComparison.Ordinal))tables.Add(r.GetString(0));}
        var rows=new List<string>();foreach(var table in tables){using var cmd=c.CreateCommand();cmd.CommandText=$"SELECT rowid,* FROM {table}"+(table=="schema_migrations"&&!includeV24?" WHERE version<=23":"")+" ORDER BY rowid;";using var r=cmd.ExecuteReader();while(r.Read())rows.Add(table+":"+string.Join("|",Enumerable.Range(0,r.FieldCount).Select(i=>r.GetValue(i).ToString())));}
        return string.Join("\n",rows);
    }

    private static string OriginalRows(UnifiedHistoryTestDatabase d)
    {
        using var c=d.Store.OpenConnection(); var rows=new List<string>();
        foreach(var table in new[]{"ordinary_recording_history","plan_occurrences","setup_intents","future_window_authorizations","plans","consent_leases","lease_uses","recording_runs","recording_run_output_evidence"})
        { using var cmd=c.CreateCommand(); cmd.CommandText=$"SELECT rowid,* FROM {table} ORDER BY rowid;"; using var r=cmd.ExecuteReader(); while(r.Read()) rows.Add(table+":"+string.Join("|",Enumerable.Range(0,r.FieldCount).Select(i=>r.GetValue(i).ToString()))); }
        return string.Join("\n",rows);
    }
    private static string Text(UnifiedHistoryTestDatabase d,string sql)
    { using var c=d.Store.OpenConnection(); using var cmd=c.CreateCommand(); cmd.CommandText=sql; return (string)cmd.ExecuteScalar()!; }
    private static void AssertV22Remains(UnifiedHistoryTestDatabase d)
    {
        using var c=new SqliteConnection($"Data Source={d.Store.DatabasePath};Pooling=False"); c.Open(); using var cmd=c.CreateCommand();
        cmd.CommandText="SELECT MAX(version) FROM schema_migrations;"; Assert.Equal(22L,cmd.ExecuteScalar());
        cmd.CommandText="SELECT COUNT(*) FROM sqlite_master WHERE name='plan_history_locator' OR name='idx_future_window_history_identity_created' OR name LIKE 'trg_history_%';"; Assert.Equal(0L,cmd.ExecuteScalar());
    }
}
