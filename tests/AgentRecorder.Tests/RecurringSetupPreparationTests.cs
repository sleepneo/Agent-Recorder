using AgentRecorder.Core.Automation;
using AgentRecorder.Infrastructure;
using AgentRecorder.Persistence;
using Microsoft.Data.Sqlite;
using Xunit;

namespace AgentRecorder.Tests;

[Collection("NonParallel-AgentRecorderDataDir")]
public sealed class RecurringSetupPreparationTests
{
    [Fact]
    public void TrustedSelectionCreatesOnePendingRecurringPreparationChainAndExactReplayIsStable()
    {
        using var database = new TemporaryDatabase();
        var request = CreateSnapshot();
        var setup = new RecurringSetupIntentService(database.Store, () => At(1));
        Assert.Equal(RecurringSetupIntentResultStatus.Created, setup.CreateOrGet(request).Result);

        var selection = CreateSelection(selectedAt: At(2));
        var preparation = new RecurringSetupPreparationService(database.Store, () => At(3), new FixedIds());
        var first = preparation.Prepare(selection);

        Assert.Equal(RecurringSetupPreparationResultStatus.Prepared, first.Status);
        Assert.True(first.Changed);
        Assert.Equal("lease_approval_pending", Text(database.Store, "SELECT status_code FROM setup_intents WHERE intent_id = 'recurring-preparation-intent';"));
        Assert.Equal(1L, Scalar(database.Store, "SELECT COUNT(*) FROM recurring_fixed_region_profile_versions;"));
        Assert.Equal(1L, Scalar(database.Store, "SELECT COUNT(*) FROM plans WHERE is_one_time = 0 AND status_code = 'draft' AND version = 0;"));
        Assert.Equal(1L, Scalar(database.Store, "SELECT COUNT(*) FROM recurring_schedule_versions;"));
        Assert.Equal(1L, Scalar(database.Store, "SELECT COUNT(*) FROM recurring_plan_profile_bindings;"));
        Assert.Equal(1L, Scalar(database.Store, "SELECT COUNT(*) FROM recurring_consent_leases WHERE status_code = 'pending' AND version = 0;"));
        Assert.Equal(1L, Scalar(database.Store, "SELECT COUNT(*) FROM recurring_setup_preparations;"));
        Assert.Equal(0L, Scalar(database.Store, "SELECT COUNT(*) FROM plan_occurrences;"));
        Assert.Equal(0L, Scalar(database.Store, "SELECT COUNT(*) FROM recording_runs;"));
        Assert.Equal(0L, Scalar(database.Store, "SELECT COUNT(*) FROM recurring_lease_uses;"));
        Assert.Equal(0L, Scalar(database.Store, "SELECT COUNT(*) FROM setup_intents WHERE plan_id IS NOT NULL OR occurrence_id IS NOT NULL OR lease_id IS NOT NULL OR scope_id IS NOT NULL OR output_directory IS NOT NULL OR frozen_file_name IS NOT NULL;"));

        var before = PreparationRow(database.Store);
        var replay = preparation.Prepare(selection);
        Assert.Equal(RecurringSetupPreparationResultStatus.Existing, replay.Status);
        Assert.False(replay.Changed);
        Assert.Equal(first.PlanId, replay.PlanId);
        Assert.Equal(first.ProfileId, replay.ProfileId);
        Assert.Equal(first.ProfileDigest, replay.ProfileDigest);
        Assert.Equal(first.LeaseId, replay.LeaseId);
        Assert.Equal(first.ConfigurationDigest, replay.ConfigurationDigest);
        Assert.Equal(first.SelectionDigest, replay.SelectionDigest);
        Assert.Equal(before, PreparationRow(database.Store));

        var readback = setup.Get(request.IntentId, request.CurrentUserSid, request.SessionBinding);
        Assert.NotNull(readback);
        Assert.Equal(RecurringSetupIntentStatus.LeaseApprovalPending, readback!.Status);
        Assert.Equal(1L, readback.Version);
    }

    [Fact]
    public void ExpiredOrConflictingSelectionWritesNothing()
    {
        using var expiredDatabase = new TemporaryDatabase();
        var expired = CreateSnapshot(expiresAtUtc: At(172800), leaseValidUntilUtc: At(172800));
        Assert.Equal(RecurringSetupIntentResultStatus.Created, new RecurringSetupIntentService(expiredDatabase.Store, () => At(1)).CreateOrGet(expired).Result);
        var expiredResult = new RecurringSetupPreparationService(expiredDatabase.Store, () => At(172800), new FixedIds()).Prepare(CreateSelection(expired.IntentId, At(2)));
        Assert.Equal(RecurringSetupPreparationResultStatus.Expired, expiredResult.Status);
        Assert.Equal(0L, Scalar(expiredDatabase.Store, "SELECT COUNT(*) FROM recurring_setup_preparations;"));
        Assert.Equal(0L, Scalar(expiredDatabase.Store, "SELECT COUNT(*) FROM plans;"));

        using var conflictDatabase = new TemporaryDatabase();
        var request = CreateSnapshot();
        Assert.Equal(RecurringSetupIntentResultStatus.Created, new RecurringSetupIntentService(conflictDatabase.Store, () => At(1)).CreateOrGet(request).Result);
        var conflictPreparation = new RecurringSetupPreparationService(conflictDatabase.Store, () => At(3), new FixedIds());
        Assert.Equal(RecurringSetupPreparationResultStatus.Prepared, conflictPreparation.Prepare(CreateSelection(selectedAt: At(2))).Status);
        var conflict = new RecurringSetupPreparationService(conflictDatabase.Store, () => At(4), new FixedIds()).Prepare(CreateSelection(request.IntentId, At(2), region: new AuthorizedPhysicalRectangle(11, 20, 640, 480)));
        Assert.Equal(RecurringSetupPreparationResultStatus.Conflict, conflict.Status);
        Assert.Equal(1L, Scalar(conflictDatabase.Store, "SELECT COUNT(*) FROM recurring_setup_preparations;"));
        Assert.Equal(1L, Scalar(conflictDatabase.Store, "SELECT COUNT(*) FROM recurring_fixed_region_profile_versions;"));
    }

    [Theory]
    [InlineData("intent")]
    [InlineData("sid")]
    [InlineData("session")]
    [InlineData("stale")]
    [InlineData("future")]
    [InlineData("expiry")]
    [InlineData("unresolved")]
    [InlineData("topology")]
    [InlineData("outside")]
    [InlineData("physical_dimensions")]
    [InlineData("dpi")]
    [InlineData("backend")]
    [InlineData("audio")]
    [InlineData("output_policy")]
    [InlineData("wake_policy")]
    [InlineData("desktop_requirement")]
    public void InvalidTrustedSelectionBoundaryIsRejectedWithoutWrites(string caseName)
    {
        using var database = new TemporaryDatabase();
        var request = CreateSnapshot();
        Assert.Equal(RecurringSetupIntentResultStatus.Created, new RecurringSetupIntentService(database.Store, () => At(1)).CreateOrGet(request).Result);

        var result = new RecurringSetupPreparationService(database.Store, () => At(3), new FixedIds())
            .Prepare(CreateInvalidSelection(caseName));

        Assert.Equal(
            caseName == "intent"
                ? RecurringSetupPreparationResultStatus.Rejected
                : RecurringSetupPreparationResultStatus.Conflict,
            result.Status);
        Assert.Equal("region_selection_pending", Text(database.Store, "SELECT status_code FROM setup_intents WHERE intent_id = 'recurring-preparation-intent';"));
        Assert.Equal(0L, Scalar(database.Store, "SELECT COUNT(*) FROM recurring_fixed_region_profile_versions;"));
        Assert.Equal(0L, Scalar(database.Store, "SELECT COUNT(*) FROM plans;"));
        Assert.Equal(0L, Scalar(database.Store, "SELECT COUNT(*) FROM recurring_setup_preparations;"));
    }

    [Fact]
    public async Task IdenticalConcurrentPreparationsConvergeToOneChain()
    {
        using var database = new TemporaryDatabase();
        var request = CreateSnapshot();
        Assert.Equal(RecurringSetupIntentResultStatus.Created, new RecurringSetupIntentService(database.Store, () => At(1)).CreateOrGet(request).Result);
        using var startGate = new Barrier(2);

        var tasks = Enumerable.Range(0, 2).Select(_ => Task.Run(() =>
        {
            if (!startGate.SignalAndWait(TimeSpan.FromSeconds(10)))
                throw new TimeoutException("The concurrent preparation start gate did not open.");

            return new RecurringSetupPreparationService(database.Store, () => At(3), new FixedIds())
                .Prepare(CreateSelection(selectedAt: At(2)));
        })).ToArray();

        var results = await Task.WhenAll(tasks);
        Assert.Equal(1, results.Count(result => result.Status == RecurringSetupPreparationResultStatus.Prepared));
        Assert.Equal(1, results.Count(result => result.Status == RecurringSetupPreparationResultStatus.Existing));
        Assert.Equal(1L, Scalar(database.Store, "SELECT COUNT(*) FROM recurring_setup_preparations;"));
        Assert.Equal(1L, Scalar(database.Store, "SELECT COUNT(*) FROM recurring_fixed_region_profile_versions;"));
        Assert.Equal(1L, Scalar(database.Store, "SELECT COUNT(*) FROM plans;"));
        Assert.Equal(1L, Scalar(database.Store, "SELECT COUNT(*) FROM recurring_consent_leases;"));
        Assert.Equal(results[0].PlanId, results[1].PlanId);
        Assert.Equal(results[0].ProfileId, results[1].ProfileId);
        Assert.Equal(results[0].LeaseId, results[1].LeaseId);
    }

    [Fact]
    public async Task ConflictingConcurrentPreparationsHaveOneWinnerAndOneStableConflict()
    {
        using var database = new TemporaryDatabase();
        var request = CreateSnapshot();
        Assert.Equal(RecurringSetupIntentResultStatus.Created, new RecurringSetupIntentService(database.Store, () => At(1)).CreateOrGet(request).Result);
        using var startGate = new Barrier(2);

        var tasks = new[]
        {
            Task.Run(() => PrepareAtBarrier(startGate, database.Store, CreateSelection(selectedAt: At(2)))),
            Task.Run(() => PrepareAtBarrier(startGate, database.Store, CreateSelection(selectedAt: At(2), region: new AuthorizedPhysicalRectangle(11, 20, 640, 480)))),
        };

        var results = await Task.WhenAll(tasks);
        Assert.Equal(1, results.Count(result => result.Status == RecurringSetupPreparationResultStatus.Prepared));
        Assert.Equal(1, results.Count(result => result.Status == RecurringSetupPreparationResultStatus.Conflict));
        Assert.Equal(1L, Scalar(database.Store, "SELECT COUNT(*) FROM recurring_setup_preparations;"));
        Assert.Equal(1L, Scalar(database.Store, "SELECT COUNT(*) FROM recurring_fixed_region_profile_versions;"));
        Assert.Equal(1L, Scalar(database.Store, "SELECT COUNT(*) FROM plans;"));
    }

    [Fact]
    public void NonUtcSelectionTimestampIsRejectedBeforePreparation()
    {
        using var database = new TemporaryDatabase();
        var request = CreateSnapshot();
        Assert.Equal(RecurringSetupIntentResultStatus.Created, new RecurringSetupIntentService(database.Store, () => At(1)).CreateOrGet(request).Result);

        var selection = CreateSelection(selectedAt: At(2).ToOffset(TimeSpan.FromHours(8)));
        var result = new RecurringSetupPreparationService(database.Store, () => At(3), new FixedIds()).Prepare(selection);

        Assert.Equal(RecurringSetupPreparationResultStatus.Conflict, result.Status);
        Assert.Equal("region_selection_pending", Text(database.Store, "SELECT status_code FROM setup_intents WHERE intent_id = 'recurring-preparation-intent';"));
        Assert.Equal(0L, Scalar(database.Store, "SELECT COUNT(*) FROM recurring_setup_preparations;"));
    }

    [Fact]
    public void InitialIntentWithoutPreparationReadsNormally()
    {
        using var database = new TemporaryDatabase();
        var request = CreateSnapshot();
        var setup = new RecurringSetupIntentService(database.Store, () => At(1));
        Assert.Equal(RecurringSetupIntentResultStatus.Created, setup.CreateOrGet(request).Result);

        var readback = setup.Get(request.IntentId, request.CurrentUserSid, request.SessionBinding);

        Assert.NotNull(readback);
        Assert.Equal(RecurringSetupIntentStatus.RegionSelectionPending, readback!.Status);
        Assert.Equal(0L, Scalar(database.Store, "SELECT COUNT(*) FROM recurring_setup_preparations;"));
    }

    [Theory]
    [InlineData("selection_digest")]
    [InlineData("selected_at_utc")]
    [InlineData("preparation_plan_id")]
    [InlineData("preparation_profile_id")]
    [InlineData("preparation_profile_version")]
    [InlineData("preparation_profile_digest")]
    [InlineData("preparation_lease_id")]
    [InlineData("preparation_configuration_digest")]
    [InlineData("preparation_timestamp")]
    [InlineData("preparation_version")]
    [InlineData("missing_schedule")]
    [InlineData("missing_binding")]
    [InlineData("mismatched_binding")]
    [InlineData("lease_status")]
    [InlineData("lease_version")]
    [InlineData("lease_quota")]
    [InlineData("lease_configuration")]
    [InlineData("initial_with_preparation")]
    [InlineData("pending_without_preparation")]
    public void CorruptedPreparedChainFailsClosedThroughSetupReadback(string caseName)
    {
        using var database = new TemporaryDatabase();
        var request = CreateSnapshot();
        var setup = new RecurringSetupIntentService(database.Store, () => At(1));
        Assert.Equal(RecurringSetupIntentResultStatus.Created, setup.CreateOrGet(request).Result);
        var preparation = new RecurringSetupPreparationService(database.Store, () => At(3), new FixedIds());
        Assert.Equal(RecurringSetupPreparationResultStatus.Prepared, preparation.Prepare(CreateSelection(selectedAt: At(2))).Status);

        using (var connection = database.Store.OpenConnection())
            CorruptPreparedChain(connection, caseName);

        Assert.Null(setup.Get(request.IntentId, request.CurrentUserSid, request.SessionBinding));
    }

    [Theory]
    [InlineData("AfterProfileInsert")]
    [InlineData("AfterPlanInsert")]
    [InlineData("AfterScheduleRevision1Insert")]
    [InlineData("AfterBindingInsert")]
    [InlineData("AfterLeaseInsert")]
    [InlineData("AfterPreparationInsert")]
    [InlineData("AfterIntentUpdate")]
    [InlineData("BeforeCommitAfterFinalRead")]
    public void EveryInjectedFailureRollsBackTheWholePreparation(string pointName)
    {
        var point = Enum.Parse<RecurringSetupPreparationFailurePoint>(pointName);
        using var database = new TemporaryDatabase();
        var request = CreateSnapshot();
        Assert.Equal(RecurringSetupIntentResultStatus.Created, new RecurringSetupIntentService(database.Store, () => At(1)).CreateOrGet(request).Result);
        var preparation = new RecurringSetupPreparationService(
            database.Store,
            () => At(3),
            new FixedIds(),
            failureHookForTest: actual =>
            {
                if (actual == point) throw new InvalidOperationException("injected");
            });

        var result = preparation.Prepare(CreateSelection(selectedAt: At(2)));

        Assert.Equal(RecurringSetupPreparationResultStatus.Rejected, result.Status);
        Assert.Equal("region_selection_pending", Text(database.Store, "SELECT status_code FROM setup_intents WHERE intent_id = 'recurring-preparation-intent';"));
        Assert.Equal(0L, Scalar(database.Store, "SELECT COUNT(*) FROM recurring_fixed_region_profile_versions;"));
        Assert.Equal(0L, Scalar(database.Store, "SELECT COUNT(*) FROM plans;"));
        Assert.Equal(0L, Scalar(database.Store, "SELECT COUNT(*) FROM recurring_schedule_versions;"));
        Assert.Equal(0L, Scalar(database.Store, "SELECT COUNT(*) FROM recurring_plan_profile_bindings;"));
        Assert.Equal(0L, Scalar(database.Store, "SELECT COUNT(*) FROM recurring_consent_leases;"));
        Assert.Equal(0L, Scalar(database.Store, "SELECT COUNT(*) FROM recurring_setup_preparations;"));
    }

    private static RecurringSetupIntentSnapshot CreateSnapshot(
        DateTimeOffset? expiresAtUtc = null,
        DateTimeOffset? leaseValidUntilUtc = null) =>
        RecurringSetupIntentSnapshot.CreateForTrustedSetupAdapter(
            "recurring-preparation-intent",
            "recurring-preparation-key",
            "S-1-5-21-preparation",
            "session-preparation",
            At(0),
            expiresAtUtc ?? At(172800),
            RecurringPlanSchedule.CreateDaily(
                "China Standard Time",
                new DateOnly(2026, 1, 1),
                new DateOnly(2026, 1, 2),
                new TimeOnly(9, 30),
                2,
                TimeSpan.FromSeconds(30),
                TimeSpan.FromMinutes(5)),
            2,
            TimeSpan.FromSeconds(60),
            leaseValidUntilUtc ?? At(172800),
            "D:\\Recordings\\Agent",
            "daily-review");

    private static RecurringFixedRegionSelectionSnapshot CreateSelection(
        string intentId = "recurring-preparation-intent",
        DateTimeOffset? selectedAt = null,
        AuthorizedPhysicalRectangle? region = null) =>
        RecurringFixedRegionSelectionSnapshot.CreateForTrustedLocalSelectionAdapter(
            intentId,
            "S-1-5-21-preparation",
            "session-preparation",
            selectedAt ?? At(2),
            "display-fingerprint-1",
            new AuthorizedPhysicalRectangle(0, 0, 1920, 1080),
            region ?? new AuthorizedPhysicalRectangle(10, 20, 640, 480),
            96,
            96,
            1920,
            1080,
            AuthorizedDisplayOrientation.Landscape,
            new string('a', 64));

    private static RecurringFixedRegionSelectionSnapshot CreateInvalidSelection(string caseName)
    {
        var intentId = "recurring-preparation-intent";
        var sid = "S-1-5-21-preparation";
        var session = "session-preparation";
        var selectedAt = At(2);
        var display = new AuthorizedPhysicalRectangle(0, 0, 1920, 1080);
        var region = new AuthorizedPhysicalRectangle(10, 20, 640, 480);
        var dpiX = 96;
        var dpiY = 96;
        var physicalWidth = 1920;
        var physicalHeight = 1080;
        var topology = new string('a', 64);
        var displayIdentity = AuthorizedDisplayIdentityStatus.Resolved;
        var backend = AuthorizedCaptureBackend.FfmpegRegion;
        var audio = AuthorizedAudioMode.None;
        var outputPolicy = AuthorizedOutputConflictPolicy.FailIfExists;
        var wakePolicy = AuthorizedWakePolicy.NaturalWakeOnly;
        var desktopRequirement = AuthorizedDesktopRequirement.InteractiveDesktopRequired;

        switch (caseName)
        {
            case "intent": intentId = "another-intent"; break;
            case "sid": sid = "S-1-5-21-other"; break;
            case "session": session = "another-session"; break;
            case "stale": selectedAt = At(0); break;
            case "future": selectedAt = At(4); break;
            case "expiry": selectedAt = At(172800); break;
            case "unresolved": displayIdentity = AuthorizedDisplayIdentityStatus.Unresolved; break;
            case "topology": topology = "not-a-lower-hex-digest"; break;
            case "outside": region = new AuthorizedPhysicalRectangle(1900, 20, 640, 480); break;
            case "physical_dimensions": physicalWidth = 1919; break;
            case "dpi": dpiX = 0; break;
            case "backend": backend = AuthorizedCaptureBackend.Wgc; break;
            case "audio": audio = AuthorizedAudioMode.SystemAudio; break;
            case "output_policy": outputPolicy = AuthorizedOutputConflictPolicy.Rename; break;
            case "wake_policy": wakePolicy = AuthorizedWakePolicy.ScheduledWake; break;
            case "desktop_requirement": desktopRequirement = AuthorizedDesktopRequirement.AnyDesktop; break;
            default: throw new ArgumentOutOfRangeException(nameof(caseName), caseName, null);
        }

        return RecurringFixedRegionSelectionSnapshot.CreateForTest(
            intentId,
            sid,
            session,
            selectedAt,
            "display-fingerprint-1",
            display,
            region,
            dpiX,
            dpiY,
            physicalWidth,
            physicalHeight,
            AuthorizedDisplayOrientation.Landscape,
            topology,
            displayIdentityStatus: displayIdentity,
            backend: backend,
            audioMode: audio,
            outputConflictPolicy: outputPolicy,
            wakePolicy: wakePolicy,
            desktopRequirement: desktopRequirement);
    }

    private static RecurringSetupPreparationResult PrepareAtBarrier(
        Barrier startGate,
        SqliteOperationalStore store,
        RecurringFixedRegionSelectionSnapshot selection)
    {
        if (!startGate.SignalAndWait(TimeSpan.FromSeconds(10)))
            throw new TimeoutException("The concurrent preparation start gate did not open.");

        return new RecurringSetupPreparationService(store, () => At(3), new FixedIds()).Prepare(selection);
    }

    private static void CorruptPreparedChain(SqliteConnection connection, string caseName)
    {
        const string LeaseLifecycleTrigger = """
            CREATE TRIGGER trg_recurring_consent_leases_lifecycle_update
                BEFORE UPDATE ON recurring_consent_leases
                BEGIN
                    SELECT RAISE(ABORT, 'recurring_consent_leases_identity_immutable')
                    WHERE NEW.lease_id IS NOT OLD.lease_id OR NEW.plan_id IS NOT OLD.plan_id
                       OR NEW.schedule_revision IS NOT OLD.schedule_revision OR NEW.schedule_digest IS NOT OLD.schedule_digest
                       OR NEW.time_zone_rules_digest IS NOT OLD.time_zone_rules_digest OR NEW.profile_id IS NOT OLD.profile_id
                       OR NEW.profile_version IS NOT OLD.profile_version OR NEW.profile_digest IS NOT OLD.profile_digest
                       OR NEW.configuration_digest IS NOT OLD.configuration_digest OR NEW.valid_from_utc IS NOT OLD.valid_from_utc
                       OR NEW.valid_until_utc IS NOT OLD.valid_until_utc OR NEW.authorized_plan_latest_end_utc IS NOT OLD.authorized_plan_latest_end_utc
                       OR NEW.per_run_duration_ticks IS NOT OLD.per_run_duration_ticks OR NEW.max_uses IS NOT OLD.max_uses
                       OR NEW.max_cumulative_duration_ticks IS NOT OLD.max_cumulative_duration_ticks
                       OR NEW.authorization_digest IS NOT OLD.authorization_digest OR NEW.created_at_utc IS NOT OLD.created_at_utc;
                    SELECT RAISE(ABORT, 'recurring_consent_leases_same_state_update_rejected') WHERE NEW.status_code = OLD.status_code;
                    SELECT RAISE(ABORT, 'recurring_consent_leases_invalid_transition')
                    WHERE NOT ((OLD.status_code = 'pending' AND NEW.status_code IN ('active', 'rejected', 'revoked', 'expired'))
                        OR (OLD.status_code = 'active' AND NEW.status_code IN ('revoked', 'expired', 'exhausted'))
                        OR (OLD.status_code = 'exhausted' AND NEW.status_code = 'revoked'));
                    SELECT RAISE(ABORT, 'recurring_consent_leases_version_or_time_not_monotonic')
                    WHERE NEW.version <> OLD.version + 1 OR NEW.updated_at_utc < OLD.updated_at_utc;
                    SELECT RAISE(ABORT, 'recurring_consent_leases_active_after_validity')
                    WHERE NEW.status_code = 'active' AND NEW.updated_at_utc >= NEW.valid_until_utc;
                    SELECT RAISE(ABORT, 'recurring_consent_leases_expiration_before_validity')
                    WHERE NEW.status_code = 'expired' AND NEW.updated_at_utc < NEW.valid_until_utc;
                END;
            """;
        var droppedTriggers = new List<(string Name, string Definition)>();
        var foreignKeysDisabled = false;
        var checkConstraintsIgnored = false;

        void DropTrigger(string name, string definition)
        {
            Execute(connection, $"DROP TRIGGER {name};");
            droppedTriggers.Add((name, definition));
        }

        void DisableForeignKeys()
        {
            if (foreignKeysDisabled) return;
            Execute(connection, "PRAGMA foreign_keys = OFF;");
            Assert.Equal(0L, Scalar(connection, "PRAGMA foreign_keys;"));
            foreignKeysDisabled = true;
        }

        void IgnoreChecks()
        {
            if (checkConstraintsIgnored) return;
            Execute(connection, "PRAGMA ignore_check_constraints = ON;");
            Assert.Equal(1L, Scalar(connection, "PRAGMA ignore_check_constraints;"));
            checkConstraintsIgnored = true;
        }

        try
        {
            switch (caseName)
            {
                case "selection_digest":
                    DropTrigger(
                        "trg_recurring_setup_preparations_immutable_update",
                        "CREATE TRIGGER trg_recurring_setup_preparations_immutable_update BEFORE UPDATE ON recurring_setup_preparations BEGIN SELECT RAISE(ABORT, 'recurring_setup_preparations_are_immutable'); END;");
                    Execute(connection, "UPDATE recurring_setup_preparations SET selection_digest = 'recurring-fixed-region-selection/v1:' || printf('%064d', 1) WHERE intent_id = 'recurring-preparation-intent';");
                    break;
                case "selected_at_utc":
                    DropTrigger(
                        "trg_recurring_setup_preparations_immutable_update",
                        "CREATE TRIGGER trg_recurring_setup_preparations_immutable_update BEFORE UPDATE ON recurring_setup_preparations BEGIN SELECT RAISE(ABORT, 'recurring_setup_preparations_are_immutable'); END;");
                    Execute(connection, "UPDATE recurring_setup_preparations SET selected_at_utc = selected_at_utc + 1 WHERE intent_id = 'recurring-preparation-intent';");
                    break;
                case "preparation_plan_id":
                    DisableForeignKeys();
                    DropTrigger(
                        "trg_recurring_setup_preparations_immutable_update",
                        "CREATE TRIGGER trg_recurring_setup_preparations_immutable_update BEFORE UPDATE ON recurring_setup_preparations BEGIN SELECT RAISE(ABORT, 'recurring_setup_preparations_are_immutable'); END;");
                    Execute(connection, "UPDATE recurring_setup_preparations SET plan_id = 'tampered-plan' WHERE intent_id = 'recurring-preparation-intent';");
                    break;
                case "preparation_profile_id":
                    DisableForeignKeys();
                    DropTrigger(
                        "trg_recurring_setup_preparations_immutable_update",
                        "CREATE TRIGGER trg_recurring_setup_preparations_immutable_update BEFORE UPDATE ON recurring_setup_preparations BEGIN SELECT RAISE(ABORT, 'recurring_setup_preparations_are_immutable'); END;");
                    Execute(connection, "UPDATE recurring_setup_preparations SET profile_id = 'tampered-profile' WHERE intent_id = 'recurring-preparation-intent';");
                    break;
                case "preparation_profile_version":
                    DisableForeignKeys();
                    IgnoreChecks();
                    DropTrigger(
                        "trg_recurring_setup_preparations_immutable_update",
                        "CREATE TRIGGER trg_recurring_setup_preparations_immutable_update BEFORE UPDATE ON recurring_setup_preparations BEGIN SELECT RAISE(ABORT, 'recurring_setup_preparations_are_immutable'); END;");
                    Execute(connection, "UPDATE recurring_setup_preparations SET profile_version = 2 WHERE intent_id = 'recurring-preparation-intent';");
                    break;
                case "preparation_profile_digest":
                    DisableForeignKeys();
                    DropTrigger(
                        "trg_recurring_setup_preparations_immutable_update",
                        "CREATE TRIGGER trg_recurring_setup_preparations_immutable_update BEFORE UPDATE ON recurring_setup_preparations BEGIN SELECT RAISE(ABORT, 'recurring_setup_preparations_are_immutable'); END;");
                    Execute(connection, "UPDATE recurring_setup_preparations SET profile_digest = 'recurring-fixed-region-profile/v1:' || printf('%064d', 2) WHERE intent_id = 'recurring-preparation-intent';");
                    break;
                case "preparation_lease_id":
                    DisableForeignKeys();
                    DropTrigger(
                        "trg_recurring_setup_preparations_immutable_update",
                        "CREATE TRIGGER trg_recurring_setup_preparations_immutable_update BEFORE UPDATE ON recurring_setup_preparations BEGIN SELECT RAISE(ABORT, 'recurring_setup_preparations_are_immutable'); END;");
                    Execute(connection, "UPDATE recurring_setup_preparations SET lease_id = 'tampered-lease' WHERE intent_id = 'recurring-preparation-intent';");
                    break;
                case "preparation_configuration_digest":
                    DropTrigger(
                        "trg_recurring_setup_preparations_immutable_update",
                        "CREATE TRIGGER trg_recurring_setup_preparations_immutable_update BEFORE UPDATE ON recurring_setup_preparations BEGIN SELECT RAISE(ABORT, 'recurring_setup_preparations_are_immutable'); END;");
                    Execute(connection, "UPDATE recurring_setup_preparations SET configuration_digest = 'recurring-plan-configuration/v1:' || printf('%064d', 3) WHERE intent_id = 'recurring-preparation-intent';");
                    break;
                case "preparation_timestamp":
                    DropTrigger(
                        "trg_recurring_setup_preparations_immutable_update",
                        "CREATE TRIGGER trg_recurring_setup_preparations_immutable_update BEFORE UPDATE ON recurring_setup_preparations BEGIN SELECT RAISE(ABORT, 'recurring_setup_preparations_are_immutable'); END;");
                    Execute(connection, "UPDATE recurring_setup_preparations SET prepared_at_utc = prepared_at_utc + 1 WHERE intent_id = 'recurring-preparation-intent';");
                    break;
                case "preparation_version":
                    IgnoreChecks();
                    DropTrigger(
                        "trg_recurring_setup_preparations_immutable_update",
                        "CREATE TRIGGER trg_recurring_setup_preparations_immutable_update BEFORE UPDATE ON recurring_setup_preparations BEGIN SELECT RAISE(ABORT, 'recurring_setup_preparations_are_immutable'); END;");
                    Execute(connection, "UPDATE recurring_setup_preparations SET preparation_version = 2 WHERE intent_id = 'recurring-preparation-intent';");
                    break;
                case "missing_schedule":
                    DisableForeignKeys();
                    Execute(connection, "DELETE FROM recurring_schedule_versions WHERE plan_id = 'plan-preparation-1' AND schedule_revision = 1;");
                    break;
                case "missing_binding":
                    DisableForeignKeys();
                    DropTrigger(
                        "trg_recurring_plan_profile_bindings_immutable_delete",
                        "CREATE TRIGGER trg_recurring_plan_profile_bindings_immutable_delete BEFORE DELETE ON recurring_plan_profile_bindings BEGIN SELECT RAISE(ABORT, 'recurring_plan_profile_bindings_are_immutable'); END;");
                    Execute(connection, "DELETE FROM recurring_plan_profile_bindings WHERE plan_id = 'plan-preparation-1';");
                    break;
                case "mismatched_binding":
                    DisableForeignKeys();
                    DropTrigger(
                        "trg_recurring_plan_profile_bindings_immutable_update",
                        "CREATE TRIGGER trg_recurring_plan_profile_bindings_immutable_update BEFORE UPDATE ON recurring_plan_profile_bindings BEGIN SELECT RAISE(ABORT, 'recurring_plan_profile_bindings_are_immutable'); END;");
                    Execute(connection, "UPDATE recurring_plan_profile_bindings SET profile_digest = 'recurring-fixed-region-profile/v1:' || printf('%064d', 4) WHERE plan_id = 'plan-preparation-1';");
                    break;
                case "lease_status":
                    DropTrigger(
                        "trg_recurring_consent_leases_lifecycle_update",
                        LeaseLifecycleTrigger);
                    Execute(connection, "UPDATE recurring_consent_leases SET status_code = 'active' WHERE lease_id = 'lease-preparation-1';");
                    break;
                case "lease_version":
                    IgnoreChecks();
                    DropTrigger(
                        "trg_recurring_consent_leases_lifecycle_update",
                        LeaseLifecycleTrigger);
                    Execute(connection, "UPDATE recurring_consent_leases SET version = 1 WHERE lease_id = 'lease-preparation-1';");
                    break;
                case "lease_quota":
                    DropTrigger(
                        "trg_recurring_consent_leases_lifecycle_update",
                        LeaseLifecycleTrigger);
                    Execute(connection, "UPDATE recurring_consent_leases SET max_uses = max_uses + 1 WHERE lease_id = 'lease-preparation-1';");
                    break;
                case "lease_configuration":
                    DropTrigger(
                        "trg_recurring_consent_leases_lifecycle_update",
                        LeaseLifecycleTrigger);
                    Execute(connection, "UPDATE recurring_consent_leases SET configuration_digest = 'recurring-plan-configuration/v1:' || printf('%064d', 5) WHERE lease_id = 'lease-preparation-1';");
                    break;
                case "initial_with_preparation":
                    Execute(connection, "UPDATE setup_intents SET status_code = 'region_selection_pending', version = 0 WHERE intent_id = 'recurring-preparation-intent';");
                    break;
                case "pending_without_preparation":
                    DisableForeignKeys();
                    DropTrigger(
                        "trg_recurring_setup_preparations_immutable_delete",
                        "CREATE TRIGGER trg_recurring_setup_preparations_immutable_delete BEFORE DELETE ON recurring_setup_preparations BEGIN SELECT RAISE(ABORT, 'recurring_setup_preparations_are_immutable'); END;");
                    Execute(connection, "DELETE FROM recurring_setup_preparations WHERE intent_id = 'recurring-preparation-intent';");
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(caseName), caseName, null);
            }
        }
        finally
        {
            if (checkConstraintsIgnored)
            {
                Execute(connection, "PRAGMA ignore_check_constraints = OFF;");
                Assert.Equal(0L, Scalar(connection, "PRAGMA ignore_check_constraints;"));
            }

            if (foreignKeysDisabled)
            {
                Execute(connection, "PRAGMA foreign_keys = ON;");
                Assert.Equal(1L, Scalar(connection, "PRAGMA foreign_keys;"));
            }

            foreach (var (_, definition) in droppedTriggers)
                Execute(connection, definition);
            foreach (var (name, _) in droppedTriggers)
                Assert.Equal(1L, Scalar(connection, $"SELECT COUNT(*) FROM sqlite_master WHERE type = 'trigger' AND name = '{name}';"));
        }
    }

    private static DateTimeOffset At(int seconds) =>
        new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero).AddSeconds(seconds);

    private static DateTimeOffset At(int seconds, int milliseconds) => At(seconds).AddMilliseconds(milliseconds);

    private static long Scalar(SqliteOperationalStore store, string sql)
    {
        using var connection = store.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt64(command.ExecuteScalar());
    }

    private static string Text(SqliteOperationalStore store, string sql)
    {
        using var connection = store.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToString(command.ExecuteScalar())!;
    }

    private static long Scalar(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt64(command.ExecuteScalar());
    }

    private static void Execute(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private static object?[] PreparationRow(SqliteOperationalStore store)
    {
        using var connection = store.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT intent_id, selection_digest, selected_at_utc, plan_id, profile_id, profile_version, profile_digest, lease_id, configuration_digest, prepared_at_utc, preparation_version FROM recurring_setup_preparations;";
        using var reader = command.ExecuteReader();
        Assert.True(reader.Read());
        var row = new object?[reader.FieldCount];
        for (var index = 0; index < reader.FieldCount; index++) row[index] = reader.GetValue(index);
        return row;
    }

    private sealed class FixedIds : IRecurringSetupPreparationIdProvider
    {
        public string CreateProfileId() => "profile-preparation-1";
        public string CreatePlanId() => "plan-preparation-1";
        public string CreateLeaseId() => "lease-preparation-1";
    }

    private sealed class TemporaryDatabase : IDisposable
    {
        private readonly string _directory = Path.Combine(Path.GetTempPath(), "AgentRecorderRecurringPreparation_" + Guid.NewGuid().ToString("N"));

        internal TemporaryDatabase()
        {
            Directory.CreateDirectory(_directory);
            Store = new SqliteOperationalStore(Path.Combine(_directory, "state", "agent-recorder.db"));
            Store.Initialize();
        }

        internal SqliteOperationalStore Store { get; }

        public void Dispose()
        {
            try { Directory.Delete(_directory, true); } catch { }
        }
    }
}
