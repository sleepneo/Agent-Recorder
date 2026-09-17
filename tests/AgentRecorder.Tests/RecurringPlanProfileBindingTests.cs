using AgentRecorder.Core.Automation;
using AgentRecorder.Infrastructure;
using AgentRecorder.Persistence;
using Microsoft.Data.Sqlite;
using Xunit;

namespace AgentRecorder.Tests;

[Collection("NonParallel-AgentRecorderDataDir")]
public sealed class RecurringPlanProfileBindingTests
{
    private static readonly DateTimeOffset CreatedAt = new(2026, 9, 10, 10, 0, 0, TimeSpan.Zero);
    private const string TopologyDigest = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";

    [Fact]
    public void BindingDomainRejectsDefaultProfileReferenceOnConstructionAndRehydrate()
    {
        var construction = Assert.Throws<Phase3DomainException>(() =>
            new RecurringPlanProfileBinding("default-profile-ref-plan", default, CreatedAt));
        Assert.Equal(RecurringFixedRegionProfileReasonCodes.ProfileIdInvalid, construction.ReasonCode);

        var rehydrate = Assert.Throws<Phase3DomainException>(() =>
            RecurringPlanProfileBinding.Rehydrate("default-profile-ref-plan", default, CreatedAt));
        Assert.Equal(RecurringFixedRegionProfileReasonCodes.ProfileIdInvalid, rehydrate.ReasonCode);
    }

    [Fact]
    public void ExactBindingIsImmutableAndSameReferenceRetryIsIdempotentAfterPlanTransition()
    {
        using var database = new TestDatabase();
        database.Store.Initialize();
        var plans = new SqlitePlanDefinitionRepository(database.Store);
        var profiles = new SqliteRecurringFixedRegionProfileRepository(database.Store);
        var bindings = new SqliteRecurringPlanProfileBindingRepository(database.Store);
        var plan = CreatePlan("periodic-binding", isOneTime: false);
        var profile = profiles.CreateVersion1(CreateProfile("binding-profile"));
        plans.Insert(plan);

        var first = bindings.Bind(plan.Id, profile.Reference, CreatedAt.AddMinutes(1));
        var replay = bindings.Bind(plan.Id, profile.Reference, CreatedAt.AddMinutes(2));

        Assert.Equal(first.BoundAtUtc, replay.BoundAtUtc);
        Assert.Equal(profile.Reference, replay.ProfileRef);
        Assert.Equal(first.BoundAtUtc, bindings.Get(plan.Id).BoundAtUtc);
        Assert.Equal(first.BoundAtUtc, bindings.TryGetByPlanId(plan.Id)!.BoundAtUtc);

        var profileV2 = profiles.CreateNext(profile.Reference, CreateProfileSpecification(countdownSeconds: 4), CreatedAt.AddMinutes(2));
        Assert.NotEqual(profile.Reference, profileV2.Reference);
        Assert.Equal(profile.Reference, bindings.Get(plan.Id).ProfileRef);

        Assert.True(plan.TryTransition(PlanDefinitionStatus.Enabled, CreatedAt.AddMinutes(3)).Succeeded);
        plans.Update(plan, expectedVersion: 0);
        var lateReplay = bindings.Bind(plan.Id, profile.Reference, CreatedAt.AddMinutes(4));
        Assert.Equal(first.BoundAtUtc, lateReplay.BoundAtUtc);
        Assert.Equal(new[] { plan.Id }, bindings.ListReferencingPlanIds(profile.Reference));
    }

    [Fact]
    public void InitialBindRequiresPeriodicDraftAndExistingPlanBindingCannotBeRebound()
    {
        using var database = new TestDatabase();
        database.Store.Initialize();
        var plans = new SqlitePlanDefinitionRepository(database.Store);
        var profiles = new SqliteRecurringFixedRegionProfileRepository(database.Store);
        var bindings = new SqliteRecurringPlanProfileBindingRepository(database.Store);
        var periodic = CreatePlan("periodic-draft", isOneTime: false);
        var oneShot = CreatePlan("one-shot", isOneTime: true);
        var enabled = CreatePlan("periodic-enabled", isOneTime: false);
        var missingProfile = CreatePlan("periodic-missing-profile", isOneTime: false);
        var profileOne = profiles.CreateVersion1(CreateProfile("binding-profile-one"));
        var profileTwo = profiles.CreateVersion1(CreateProfile("binding-profile-two", countdownSeconds: 4));
        plans.Insert(periodic);
        plans.Insert(oneShot);
        plans.Insert(enabled);
        plans.Insert(missingProfile);
        Assert.True(enabled.TryTransition(PlanDefinitionStatus.Enabled, CreatedAt.AddMinutes(1)).Succeeded);
        plans.Update(enabled, expectedVersion: 0);

        AssertCode(RecurringPlanProfileBindingPersistenceReasonCodes.PlanNotPeriodic, () => bindings.Bind(oneShot.Id, profileOne.Reference, CreatedAt));
        AssertCode(RecurringPlanProfileBindingPersistenceReasonCodes.PlanNotDraft, () => bindings.Bind(enabled.Id, profileOne.Reference, CreatedAt));

        bindings.Bind(periodic.Id, profileOne.Reference, CreatedAt);
        AssertCode(RecurringPlanProfileBindingPersistenceReasonCodes.BoundToOtherProfile, () => bindings.Bind(periodic.Id, profileTwo.Reference, CreatedAt.AddMinutes(1)));
        AssertCode(RecurringPlanProfileBindingPersistenceReasonCodes.ProfileNotFound, () => bindings.Bind(missingProfile.Id, new ProfileRef("missing-profile", 1, profileOne.ProfileDigest), CreatedAt));
        Assert.Null(bindings.TryGet("periodic-missing-profile"));
    }

    [Fact]
    public void ExactReferenceListUsesBoundedDescendingPagination()
    {
        using var database = new TestDatabase();
        database.Store.Initialize();
        var plans = new SqlitePlanDefinitionRepository(database.Store);
        var profiles = new SqliteRecurringFixedRegionProfileRepository(database.Store);
        var bindings = new SqliteRecurringPlanProfileBindingRepository(database.Store);
        var profile = profiles.CreateVersion1(CreateProfile("pagination-binding-profile"));
        foreach (var planId in new[] { "plan-a", "plan-b", "plan-c" })
        {
            plans.Insert(CreatePlan(planId, isOneTime: false));
            bindings.Bind(planId, profile.Reference, CreatedAt);
        }

        Assert.Equal(new[] { "plan-c", "plan-b" }, bindings.ListReferencingPlanIds(profile.Reference, limit: 2));
        Assert.Equal(new[] { "plan-a" }, bindings.ListReferencingPlanIds(profile.Reference, beforePlanIdExclusive: "plan-b", limit: 2));
        AssertCode(RecurringPlanProfileBindingPersistenceReasonCodes.ListLimitInvalid, () => bindings.ListReferencingPlanIds(profile.Reference, limit: 0));
        AssertCode(RecurringPlanProfileBindingPersistenceReasonCodes.ListLimitInvalid, () => bindings.ListReferencingPlanIds(profile.Reference, limit: 101));
        AssertCode(RecurringPlanProfileBindingPersistenceReasonCodes.ListCursorInvalid, () => bindings.ListReferencingPlanIds(profile.Reference, beforePlanIdExclusive: " "));
    }

    [Fact]
    public async Task ConcurrentIdenticalBindsConvergeOnOneStoredTimestamp()
    {
        using var database = new TestDatabase();
        database.Store.Initialize();
        var planRepository = new SqlitePlanDefinitionRepository(database.Store);
        var profileRepository = new SqliteRecurringFixedRegionProfileRepository(database.Store);
        var firstRepository = new SqliteRecurringPlanProfileBindingRepository(database.Store);
        var secondRepository = new SqliteRecurringPlanProfileBindingRepository(new SqliteOperationalStore(database.Store.DatabasePath));
        var plan = CreatePlan("concurrent-binding", isOneTime: false);
        var profile = profileRepository.CreateVersion1(CreateProfile("concurrent-binding-profile"));
        planRepository.Insert(plan);

        var results = await Task.WhenAll(
            Task.Run(() => firstRepository.Bind(plan.Id, profile.Reference, CreatedAt.AddMinutes(1))),
            Task.Run(() => secondRepository.Bind(plan.Id, profile.Reference, CreatedAt.AddMinutes(2))));

        Assert.Equal(results[0].BoundAtUtc, results[1].BoundAtUtc);
        Assert.Equal(1L, Scalar(database.Store.DatabasePath, "SELECT COUNT(*) FROM recurring_plan_profile_bindings;"));
    }

    [Fact]
    public async Task ConcurrentDifferentReferencesHaveOneWinnerAndStableConflictLoser()
    {
        using var database = new TestDatabase();
        database.Store.Initialize();
        var plans = new SqlitePlanDefinitionRepository(database.Store);
        var profiles = new SqliteRecurringFixedRegionProfileRepository(database.Store);
        var firstRepository = new SqliteRecurringPlanProfileBindingRepository(database.Store);
        var secondRepository = new SqliteRecurringPlanProfileBindingRepository(new SqliteOperationalStore(database.Store.DatabasePath));
        var plan = CreatePlan("concurrent-different-binding", isOneTime: false);
        var firstProfile = profiles.CreateVersion1(CreateProfile("concurrent-different-profile-one"));
        var secondProfile = profiles.CreateVersion1(CreateProfile("concurrent-different-profile-two", countdownSeconds: 4));
        plans.Insert(plan);
        using var startGate = new Barrier(2);

        var attempts = await Task.WhenAll(
            Task.Run(() => CaptureConcurrentBind(startGate, () => firstRepository.Bind(plan.Id, firstProfile.Reference, CreatedAt.AddMinutes(1)))),
            Task.Run(() => CaptureConcurrentBind(startGate, () => secondRepository.Bind(plan.Id, secondProfile.Reference, CreatedAt.AddMinutes(2)))));

        Assert.Equal(1, attempts.Count(attempt => attempt.Binding is not null));
        Assert.Equal(1, attempts.Count(attempt => attempt.Exception?.Code == RecurringPlanProfileBindingPersistenceReasonCodes.BoundToOtherProfile));
        var winner = Assert.Single(attempts.Where(attempt => attempt.Binding is not null)).Binding!;
        var loser = Assert.Single(attempts.Where(attempt => attempt.Exception is not null)).Exception!;
        Assert.Equal(RecurringPlanProfileBindingPersistenceReasonCodes.BoundToOtherProfile, loser.Code);
        Assert.Equal(winner.ProfileRef, database.Bindings().Get(plan.Id).ProfileRef);
        Assert.Equal(1L, Scalar(database.Store.DatabasePath, "SELECT COUNT(*) FROM recurring_plan_profile_bindings;"));
    }

    [Fact]
    public void PersistedOneShotPlanTamperingFailsClosedForEveryExistingBindingReadPath()
    {
        using var database = new TestDatabase();
        database.Store.Initialize();
        var plans = new SqlitePlanDefinitionRepository(database.Store);
        var profiles = new SqliteRecurringFixedRegionProfileRepository(database.Store);
        var bindings = new SqliteRecurringPlanProfileBindingRepository(database.Store);
        var plan = CreatePlan("tampered-periodic-binding", isOneTime: false);
        var profile = profiles.CreateVersion1(CreateProfile("tampered-periodic-profile"));
        plans.Insert(plan);
        bindings.Bind(plan.Id, profile.Reference, CreatedAt);

        using (var connection = OpenRaw(database.Store.DatabasePath))
        {
            Execute(connection, "PRAGMA foreign_keys = OFF;");
            Execute(connection, "UPDATE plans SET is_one_time = 1 WHERE id = $planId;", ("$planId", plan.Id));
        }

        AssertCode(RecurringPlanProfileBindingPersistenceReasonCodes.PersistedDataInvalid, () => bindings.Get(plan.Id));
        AssertCode(RecurringPlanProfileBindingPersistenceReasonCodes.PersistedDataInvalid, () => bindings.TryGet(plan.Id));
        AssertCode(RecurringPlanProfileBindingPersistenceReasonCodes.PersistedDataInvalid, () => bindings.GetByPlanId(plan.Id));
        AssertCode(RecurringPlanProfileBindingPersistenceReasonCodes.PersistedDataInvalid, () => bindings.TryGetByPlanId(plan.Id));
        AssertCode(RecurringPlanProfileBindingPersistenceReasonCodes.PersistedDataInvalid, () => bindings.Bind(plan.Id, profile.Reference, CreatedAt.AddMinutes(1)));
        AssertCode(RecurringPlanProfileBindingPersistenceReasonCodes.PersistedDataInvalid, () => bindings.ListReferencingPlanIds(profile.Reference));
    }

    [Fact]
    public void SuccessfulBindingHasNoExecutionAuthorizationOrCaptureFileSideEffects()
    {
        using var database = new TestDatabase();
        database.Store.Initialize();
        var plans = new SqlitePlanDefinitionRepository(database.Store);
        var profiles = new SqliteRecurringFixedRegionProfileRepository(database.Store);
        var bindings = new SqliteRecurringPlanProfileBindingRepository(database.Store);
        var plan = CreatePlan("binding-no-side-effects", isOneTime: false);
        var outputDirectory = Path.Combine(database.RootPath, "capture-output");
        Directory.CreateDirectory(outputDirectory);
        var profile = profiles.CreateVersion1(CreateProfile("binding-no-side-effects-profile", outputDirectory: outputDirectory));
        plans.Insert(plan);

        var bound = bindings.Bind(plan.Id, profile.Reference, CreatedAt);

        Assert.Equal(profile.Reference, bound.ProfileRef);
        Assert.Equal(1L, Scalar(database.Store.DatabasePath, "SELECT COUNT(*) FROM recurring_plan_profile_bindings;"));
        foreach (var table in new[]
        {
            "plan_occurrences", "recording_runs", "consent_leases", "lease_uses",
            "authorized_capture_scopes", "setup_intents",
        })
        {
            Assert.Equal(0L, Scalar(database.Store.DatabasePath, $"SELECT COUNT(*) FROM {table};"));
        }

        Assert.Empty(Directory.GetFiles(outputDirectory, "*", SearchOption.AllDirectories));
        var artifactFiles = Directory.GetFiles(database.RootPath, "*", SearchOption.AllDirectories)
            .Where(path => path.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase) ||
                           path.EndsWith(".mkv", StringComparison.OrdinalIgnoreCase) ||
                           path.EndsWith(".webm", StringComparison.OrdinalIgnoreCase) ||
                           path.EndsWith(".mov", StringComparison.OrdinalIgnoreCase) ||
                           path.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) ||
                           path.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase) ||
                           path.EndsWith(".part", StringComparison.OrdinalIgnoreCase))
            .ToArray();
        Assert.Empty(artifactFiles);
    }

    [Fact]
    public void BindingReferenceRemainsByteStableWhenRecurringScheduleAdvances()
    {
        using var database = new TestDatabase();
        database.Store.Initialize();
        var plans = new SqlitePlanDefinitionRepository(database.Store);
        var profiles = new SqliteRecurringFixedRegionProfileRepository(database.Store);
        var schedules = new SqliteRecurringScheduleVersionRepository(database.Store);
        var bindings = new SqliteRecurringPlanProfileBindingRepository(database.Store);
        var plan = CreatePlan("binding-schedule-stability", isOneTime: false);
        var profile = profiles.CreateVersion1(CreateProfile("binding-schedule-profile"));
        plans.Insert(plan);
        var firstSchedule = RecurringPlanSchedule.CreateDaily(TimeZoneInfo.Utc.Id, new DateOnly(2026, 9, 10), new DateOnly(2026, 9, 12), new TimeOnly(10, 0), 3, TimeSpan.FromMinutes(1), TimeSpan.Zero);
        var secondSchedule = RecurringPlanSchedule.CreateDaily(TimeZoneInfo.Utc.Id, new DateOnly(2026, 9, 13), new DateOnly(2026, 9, 15), new TimeOnly(11, 0), 3, TimeSpan.FromMinutes(2), TimeSpan.Zero);
        schedules.CreateOrGet(plan.Id, 1, firstSchedule, CreatedAt);
        var before = bindings.Bind(plan.Id, profile.Reference, CreatedAt.AddMinutes(1));

        schedules.CreateOrGet(plan.Id, 2, secondSchedule, CreatedAt.AddMinutes(2));

        var after = bindings.Get(plan.Id);
        Assert.Equal(before.PlanId, after.PlanId);
        Assert.Equal(before.ProfileRef, after.ProfileRef);
        Assert.Equal(before.BoundAtUtc, after.BoundAtUtc);
        Assert.Equal(new[] { plan.Id }, bindings.ListReferencingPlanIds(profile.Reference));
    }

    [Fact]
    public void RawSqlCannotBypassPeriodicityDraftOrImmutabilityAndExactFkIsRequired()
    {
        using var database = new TestDatabase();
        database.Store.Initialize();
        var plans = new SqlitePlanDefinitionRepository(database.Store);
        var profiles = new SqliteRecurringFixedRegionProfileRepository(database.Store);
        var bindings = new SqliteRecurringPlanProfileBindingRepository(database.Store);
        var oneShot = CreatePlan("raw-one-shot", isOneTime: true);
        var enabled = CreatePlan("raw-enabled", isOneTime: false);
        var draft = CreatePlan("raw-draft", isOneTime: false);
        var wrongReference = CreatePlan("raw-wrong-reference", isOneTime: false);
        var profile = profiles.CreateVersion1(CreateProfile("raw-binding-profile"));
        plans.Insert(oneShot);
        plans.Insert(enabled);
        plans.Insert(draft);
        plans.Insert(wrongReference);
        Assert.True(enabled.TryTransition(PlanDefinitionStatus.Enabled, CreatedAt.AddMinutes(1)).Succeeded);
        plans.Update(enabled, expectedVersion: 0);

        Assert.Throws<SqliteException>(() => Execute(database.Store.DatabasePath, BindingInsertSql, ("$planId", oneShot.Id), ("$profileId", profile.ProfileId), ("$profileVersion", profile.ProfileVersion), ("$profileDigest", profile.ProfileDigest), ("$boundAtUtc", CreatedAt.Ticks)));
        Assert.Throws<SqliteException>(() => Execute(database.Store.DatabasePath, BindingInsertSql, ("$planId", enabled.Id), ("$profileId", profile.ProfileId), ("$profileVersion", profile.ProfileVersion), ("$profileDigest", profile.ProfileDigest), ("$boundAtUtc", CreatedAt.Ticks)));

        bindings.Bind(draft.Id, profile.Reference, CreatedAt);
        Assert.Throws<SqliteException>(() => Execute(database.Store.DatabasePath, $"UPDATE {BindingTable} SET bound_at_utc = $boundAtUtc WHERE plan_id = $planId;", ("$planId", draft.Id), ("$boundAtUtc", CreatedAt.AddMinutes(1).Ticks)));
        Assert.Throws<SqliteException>(() => Execute(database.Store.DatabasePath, $"DELETE FROM {BindingTable} WHERE plan_id = $planId;", ("$planId", draft.Id)));
        Assert.Throws<SqliteException>(() => Execute(database.Store.DatabasePath, BindingInsertSql, ("$planId", wrongReference.Id), ("$profileId", profile.ProfileId), ("$profileVersion", profile.ProfileVersion), ("$profileDigest", profile.ProfileDigest[..^1] + "0"), ("$boundAtUtc", CreatedAt.Ticks)));
    }

    [Fact]
    public void SchemaV13ShapeTamperFailsClosedAndNoBindingHasCaptureSideEffects()
    {
        using var database = new TestDatabase();
        database.Store.Initialize();
        Assert.Equal(13L, Scalar(database.Store.DatabasePath, "SELECT COUNT(*) FROM schema_migrations;"));
        Assert.Equal("schema_v10_recurring_plan_profile_bindings", ScalarString(database.Store.DatabasePath, "SELECT name FROM schema_migrations WHERE version = 10;"));
        Assert.Equal("3ed33a8d8176dc56b35fa035f45a5bc2dd6ef95c70979065b73a399adee4e727", ScalarString(database.Store.DatabasePath, "SELECT definition_checksum FROM schema_migrations WHERE version = 10;"));
        Assert.Equal(0L, Scalar(database.Store.DatabasePath, "SELECT COUNT(*) FROM recurring_plan_profile_bindings;"));
        Assert.Equal(1L, Scalar(database.Store.DatabasePath, "SELECT COUNT(*) FROM sqlite_master WHERE type = 'index' AND name = 'idx_recurring_plan_profile_bindings_profile_ref_plan_id';"));
        Assert.Equal(4L, Scalar(database.Store.DatabasePath, "SELECT COUNT(*) FROM sqlite_master WHERE type = 'trigger' AND tbl_name = 'recurring_plan_profile_bindings';"));

        Execute(database.Store.DatabasePath, "DROP INDEX idx_recurring_plan_profile_bindings_profile_ref_plan_id;");
        var exception = Assert.Throws<SqliteOperationalStoreException>(() => database.Store.Initialize());
        Assert.Equal("sqlite_corrupt", exception.Code);
    }

    [Fact]
    public void V10TriggerShapeTamperingFailsClosedForAssociationWhenOperationTableAndMessageDrift()
    {
        var tamperCases = new[]
        {
            ("trg_recurring_plan_profile_bindings_periodic_only", "CREATE TRIGGER trg_recurring_plan_profile_bindings_periodic_only BEFORE INSERT ON recurring_plan_profile_bindings BEGIN SELECT RAISE(ABORT, 'recurring_plan_profile_bindings_require_periodic_plan') WHERE EXISTS (SELECT 1 FROM plans WHERE is_one_time = 1); END;"),
            ("trg_recurring_plan_profile_bindings_periodic_only", "CREATE TRIGGER trg_recurring_plan_profile_bindings_periodic_only BEFORE INSERT ON recurring_plan_profile_bindings BEGIN SELECT RAISE(ABORT, 'recurring_plan_profile_bindings_require_periodic_plan') WHERE EXISTS (SELECT 1 FROM plans WHERE id = NEW.profile_id AND is_one_time = 1); END;"),
            ("trg_recurring_plan_profile_bindings_periodic_only", "CREATE TRIGGER trg_recurring_plan_profile_bindings_periodic_only BEFORE INSERT ON recurring_plan_profile_bindings WHEN 0 BEGIN SELECT RAISE(ABORT, 'recurring_plan_profile_bindings_require_periodic_plan') WHERE EXISTS (SELECT 1 FROM plans WHERE id = NEW.plan_id AND is_one_time = 1); END;"),
            ("trg_recurring_plan_profile_bindings_periodic_only", "CREATE TRIGGER trg_recurring_plan_profile_bindings_periodic_only BEFORE INSERT ON recurring_plan_profile_bindings BEGIN SELECT RAISE(ABORT, 'recurring_plan_profile_bindings_require_periodic_plan') WHERE EXISTS (SELECT 1 FROM plans WHERE id = NEW.plan_id AND is_one_time = 0); END;"),
            ("trg_recurring_plan_profile_bindings_periodic_only", "CREATE TRIGGER trg_recurring_plan_profile_bindings_periodic_only AFTER INSERT ON recurring_plan_profile_bindings BEGIN SELECT RAISE(ABORT, 'recurring_plan_profile_bindings_require_periodic_plan') WHERE EXISTS (SELECT 1 FROM plans WHERE id = NEW.plan_id AND is_one_time = 1); END;"),
            ("trg_recurring_plan_profile_bindings_periodic_only", "CREATE TRIGGER trg_recurring_plan_profile_bindings_periodic_only BEFORE INSERT ON plans BEGIN SELECT RAISE(ABORT, 'recurring_plan_profile_bindings_require_periodic_plan') WHERE EXISTS (SELECT 1 FROM plans WHERE id = NEW.plan_id AND is_one_time = 1); END;"),
            ("trg_recurring_plan_profile_bindings_periodic_only", "CREATE TRIGGER trg_recurring_plan_profile_bindings_periodic_only BEFORE INSERT ON recurring_plan_profile_bindings BEGIN SELECT RAISE(ABORT, 'tampered_message') WHERE EXISTS (SELECT 1 FROM plans WHERE id = NEW.plan_id AND is_one_time = 1); END;"),
            ("trg_recurring_plan_profile_bindings_immutable_update", "CREATE TRIGGER trg_recurring_plan_profile_bindings_immutable_update BEFORE UPDATE ON recurring_plan_profile_bindings WHEN 0 BEGIN SELECT RAISE(ABORT, 'recurring_plan_profile_bindings_are_immutable'); END;"),
            ("trg_recurring_plan_profile_bindings_immutable_update", "CREATE TRIGGER trg_recurring_plan_profile_bindings_immutable_update BEFORE DELETE ON recurring_plan_profile_bindings BEGIN SELECT RAISE(ABORT, 'recurring_plan_profile_bindings_are_immutable'); END;"),
        };

        foreach (var (name, definition) in tamperCases)
        {
            using var database = new TestDatabase();
            database.Store.Initialize();
            Execute(database.Store.DatabasePath, $"DROP TRIGGER {name};");
            Execute(database.Store.DatabasePath, definition);

            var exception = Assert.Throws<SqliteOperationalStoreException>(() => database.Store.Initialize());
            Assert.Equal("sqlite_corrupt", exception.Code);
        }
    }

    [Fact]
    public void V9FixtureUpgradesOnlyByAddingV10BindingSchemaAndPreservesEarlierRows()
    {
        using var database = new TestDatabase();
        var profile = CreateProfile("v9-profile");
        var schedule = RecurringPlanSchedule.CreateDaily(
            TimeZoneInfo.Utc.Id,
            new DateOnly(2026, 9, 10),
            new DateOnly(2026, 9, 12),
            new TimeOnly(10, 0),
            3,
            TimeSpan.FromMinutes(1),
            TimeSpan.Zero);
        var afterUtc = CreatedAt.AddDays(-1);
        var candidate = new RecurringOccurrenceCalculator()
            .CalculateNext("v9-periodic-plan", 1, schedule, afterUtc)
            .Candidate!;
        CreatePreloadedSchema(database.Store, SqliteSchemaV1.Migrations.Single().Definition);
        using (var connection = OpenRaw(database.Store.DatabasePath))
        {
            foreach (var migration in new[]
            {
                SqliteSchemaV2.Migrations.Single(),
                SqliteSchemaV3.Migrations.Single(),
                SqliteSchemaV4.Migrations.Single(),
                SqliteSchemaV5.Migrations.Single(),
                SqliteSchemaV6.Migrations.Single(),
                SqliteSchemaV7.Migrations.Single(),
                SqliteSchemaV8.Migrations.Single(),
                SqliteSchemaV9.Migrations.Single(),
            })
            {
                Execute(connection, migration.Definition);
                Execute(
                    connection,
                    "INSERT INTO schema_migrations(version, name, definition_checksum, applied_at_utc) VALUES ($version, $name, $checksum, $appliedAtUtc);",
                    ("$version", migration.Version),
                    ("$name", migration.Name),
                    ("$checksum", migration.Checksum),
                    ("$appliedAtUtc", 1000L + migration.Version));
            }

            Execute(connection, "INSERT INTO plans(id, is_one_time, status_code, created_at_utc, updated_at_utc, version) VALUES ('v9-periodic-plan', 0, 'draft', 1000, 1000, 0);");
            Execute(
                connection,
                "INSERT INTO recurring_schedule_versions(plan_id, schedule_revision, schedule_digest, schedule_kind_code, time_zone_id, time_zone_rules_digest, local_start_date, local_end_date, local_wall_clock_seconds, weekday_mask, maximum_occurrences, recording_duration_ticks, latest_start_grace_ticks, created_at_utc) VALUES ($planId, 1, $scheduleDigest, 'daily', $timeZoneId, $rulesDigest, $startDate, $endDate, $wallClockSeconds, 0, $maximumOccurrences, $durationTicks, $graceTicks, $createdAt);",
                ("$planId", "v9-periodic-plan"),
                ("$scheduleDigest", schedule.CanonicalDigest),
                ("$timeZoneId", schedule.TimeZoneId),
                ("$rulesDigest", RecurringTimeZoneRulesDigest.Compute(schedule.TimeZoneInfo)),
                ("$startDate", schedule.LocalStartDate.ToString("yyyy-MM-dd")),
                ("$endDate", schedule.LocalEndDate.ToString("yyyy-MM-dd")),
                ("$wallClockSeconds", schedule.LocalWallClockTime.Ticks / TimeSpan.TicksPerSecond),
                ("$maximumOccurrences", schedule.MaximumOccurrences),
                ("$durationTicks", schedule.RecordingDuration.Ticks),
                ("$graceTicks", schedule.LatestStartGrace.Ticks),
                ("$createdAt", CreatedAt.UtcDateTime.Ticks));
            Execute(
                connection,
                "INSERT INTO plan_occurrences(id, plan_id, status_code, window_start_utc, window_end_utc, created_at_utc, updated_at_utc, version) VALUES ($occurrenceId, $planId, 'scheduled', $windowStart, $windowEnd, $createdAt, $createdAt, 0);",
                ("$occurrenceId", candidate.Identity.Value),
                ("$planId", "v9-periodic-plan"),
                ("$windowStart", candidate.ScheduledStartUtc!.Value.UtcDateTime.Ticks),
                ("$windowEnd", candidate.PlannedEndUtc!.Value.UtcDateTime.Ticks),
                ("$createdAt", CreatedAt.UtcDateTime.Ticks));
            Execute(
                connection,
                "INSERT INTO recurring_occurrence_slots(occurrence_identity, plan_id, schedule_revision, schedule_digest, local_date, local_wall_clock_seconds, time_zone_id, slot_status_code, scheduled_start_utc, latest_start_utc, planned_end_utc, resolution_code, terminal_reason_code, occurrence_id, created_at_utc) VALUES ($identity, $planId, 1, $scheduleDigest, $localDate, $wallClockSeconds, $timeZoneId, 'scheduled', $scheduledStart, $latestStart, $plannedEnd, 'schedule_exact', NULL, $occurrenceId, $createdAt);",
                ("$identity", candidate.Identity.Value),
                ("$planId", "v9-periodic-plan"),
                ("$scheduleDigest", schedule.CanonicalDigest),
                ("$localDate", candidate.LocalDate.ToString("yyyy-MM-dd")),
                ("$wallClockSeconds", schedule.LocalWallClockTime.Ticks / TimeSpan.TicksPerSecond),
                ("$timeZoneId", schedule.TimeZoneId),
                ("$scheduledStart", candidate.ScheduledStartUtc!.Value.UtcDateTime.Ticks),
                ("$latestStart", candidate.LatestStartUtc!.Value.UtcDateTime.Ticks),
                ("$plannedEnd", candidate.PlannedEndUtc!.Value.UtcDateTime.Ticks),
                ("$occurrenceId", candidate.Identity.Value),
                ("$createdAt", CreatedAt.UtcDateTime.Ticks));
            Execute(
                connection,
                "INSERT INTO recurring_schedule_cursors(plan_id, schedule_revision, schedule_digest, time_zone_rules_digest, initial_after_utc, last_local_date, last_schedule_ordinal, is_exhausted, created_at_utc, updated_at_utc, version) VALUES ($planId, 1, $scheduleDigest, $rulesDigest, $initialAfter, $lastDate, 1, 0, $createdAt, $createdAt, 1);",
                ("$planId", "v9-periodic-plan"),
                ("$scheduleDigest", schedule.CanonicalDigest),
                ("$rulesDigest", RecurringTimeZoneRulesDigest.Compute(schedule.TimeZoneInfo)),
                ("$initialAfter", afterUtc.UtcDateTime.Ticks),
                ("$lastDate", candidate.LocalDate.ToString("yyyy-MM-dd")),
                ("$createdAt", CreatedAt.UtcDateTime.Ticks));
            Execute(
                connection,
                "INSERT INTO recurring_advancement_operations(operation_id, plan_id, schedule_revision, request_digest, expected_cursor_version, result_code, occurrence_identity, result_cursor_version, created_at_utc) VALUES ('v9-advancement', $planId, 1, $requestDigest, 0, 'scheduled', $occurrenceIdentity, 1, $createdAt);",
                ("$planId", "v9-periodic-plan"),
                ("$requestDigest", RecurringAdvancementRequestDigest.Compute("v9-periodic-plan", 1, 0, afterUtc)),
                ("$occurrenceIdentity", candidate.Identity.Value),
                ("$createdAt", CreatedAt.UtcDateTime.Ticks));
            InsertProfileRow(connection, profile);
        }

        database.Store.Initialize();

        Assert.Equal(13L, Scalar(database.Store.DatabasePath, "SELECT COUNT(*) FROM schema_migrations;"));
        Assert.Equal(1L, Scalar(database.Store.DatabasePath, "SELECT COUNT(*) FROM plans WHERE id = 'v9-periodic-plan';"));
        Assert.Equal(1L, Scalar(database.Store.DatabasePath, "SELECT COUNT(*) FROM recurring_schedule_versions WHERE plan_id = 'v9-periodic-plan' AND schedule_revision = 1;"));
        Assert.Equal(schedule.CanonicalDigest, ScalarString(database.Store.DatabasePath, "SELECT schedule_digest FROM recurring_schedule_versions WHERE plan_id = 'v9-periodic-plan' AND schedule_revision = 1;"));
        Assert.Equal(1L, Scalar(database.Store.DatabasePath, "SELECT COUNT(*) FROM recurring_occurrence_slots WHERE plan_id = 'v9-periodic-plan';"));
        Assert.Equal(candidate.Identity.Value, ScalarString(database.Store.DatabasePath, "SELECT occurrence_identity FROM recurring_occurrence_slots WHERE plan_id = 'v9-periodic-plan';"));
        Assert.Equal(1L, Scalar(database.Store.DatabasePath, "SELECT COUNT(*) FROM recurring_schedule_cursors WHERE plan_id = 'v9-periodic-plan';"));
        Assert.Equal(1L, Scalar(database.Store.DatabasePath, "SELECT version FROM recurring_schedule_cursors WHERE plan_id = 'v9-periodic-plan';"));
        Assert.Equal(1L, Scalar(database.Store.DatabasePath, "SELECT COUNT(*) FROM recurring_advancement_operations WHERE plan_id = 'v9-periodic-plan';"));
        Assert.Equal(profile.Reference.ProfileDigest, ScalarString(database.Store.DatabasePath, "SELECT profile_digest FROM recurring_fixed_region_profile_versions WHERE profile_id = 'v9-profile' AND profile_version = 1;"));
        Assert.Equal(1L, Scalar(database.Store.DatabasePath, "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = 'recurring_plan_profile_bindings';"));
        Assert.Equal(0L, Scalar(database.Store.DatabasePath, "SELECT COUNT(*) FROM recurring_plan_profile_bindings;"));
        Assert.Equal("c5f3846be6a761dbeab25824e2c8de8184e691e14738669a0ac726ff4b09d11a", ScalarString(database.Store.DatabasePath, "SELECT definition_checksum FROM schema_migrations WHERE version = 9;"));

        foreach (var migration in SqliteSchemaCatalog.Migrations.Take(9))
        {
            Assert.Equal(migration.Name, ScalarString(database.Store.DatabasePath, $"SELECT name FROM schema_migrations WHERE version = {migration.Version};"));
            Assert.Equal(migration.Checksum, ScalarString(database.Store.DatabasePath, $"SELECT definition_checksum FROM schema_migrations WHERE version = {migration.Version};"));
        }

        var bindings = new SqliteRecurringPlanProfileBindingRepository(database.Store);
        var bound = bindings.Bind("v9-periodic-plan", profile.Reference, CreatedAt.AddMinutes(1));
        Assert.Equal(profile.Reference, bound.ProfileRef);
        Assert.Equal(1L, Scalar(database.Store.DatabasePath, "SELECT COUNT(*) FROM recurring_plan_profile_bindings;"));
    }

    private const string BindingTable = "recurring_plan_profile_bindings";
    private const string BindingInsertSql = "INSERT INTO recurring_plan_profile_bindings (plan_id, profile_id, profile_version, profile_digest, bound_at_utc) VALUES ($planId, $profileId, $profileVersion, $profileDigest, $boundAtUtc);";

    private static PlanDefinition CreatePlan(string id, bool isOneTime) => new(id, isOneTime, CreatedAt);

    private static RecurringFixedRegionProfileVersion CreateProfile(string id, int countdownSeconds = 3, string? outputDirectory = null) =>
        RecurringFixedRegionProfileVersion.CreateVersion1(id, CreatedAt, CreateProfileSpecification(countdownSeconds, outputDirectory));

    private static RecurringFixedRegionProfileSpecification CreateProfileSpecification(int countdownSeconds = 3, string? outputDirectory = null) =>
        new(
                AuthorizedScopeTargetType.FixedRegion,
                RecurringFixedRegionRebindPolicy.ExactMatchOnly,
                AuthorizedCaptureSemantics.DesktopRegion,
                AuthorizedCoordinateSpace.PhysicalVirtualScreen,
                AuthorizedDisplayIdentityStatus.Resolved,
                "DISPLAY-FP-1",
                new AuthorizedPhysicalRectangle(-100, 50, 1920, 1080),
                new AuthorizedPhysicalRectangle(10, 20, 640, 480),
                96,
                144,
                1920,
                1080,
                AuthorizedDisplayOrientation.Landscape,
                TopologyDigest,
                AuthorizedCaptureBackend.FfmpegRegion,
                AuthorizedAudioMode.None,
                TimeSpan.FromMinutes(2),
                countdownSeconds,
                outputDirectory ?? Path.Combine(Path.GetTempPath(), "task256-output"),
                "demo",
                AuthorizedOutputConflictPolicy.FailIfExists,
                AuthorizedWakePolicy.NaturalWakeOnly,
                AuthorizedDesktopRequirement.InteractiveDesktopRequired);

    private static void AssertCode(string expected, Action action)
    {
        var exception = Assert.Throws<Phase3PersistenceException>(action);
        Assert.Equal(expected, exception.Code);
    }

    private static (RecurringPlanProfileBinding? Binding, Phase3PersistenceException? Exception) CaptureConcurrentBind(
        Barrier startGate,
        Func<RecurringPlanProfileBinding> action)
    {
        if (!startGate.SignalAndWait(TimeSpan.FromSeconds(5)))
        {
            throw new TimeoutException("The concurrent binding start gate did not open.");
        }

        try
        {
            return (action(), null);
        }
        catch (Phase3PersistenceException exception)
        {
            return (null, exception);
        }
    }

    private static void Execute(string databasePath, string sql, params (string Name, object Value)[] parameters)
    {
        using var connection = OpenRaw(databasePath);
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        command.ExecuteNonQuery();
    }

    private static void CreatePreloadedSchema(SqliteOperationalStore store, string definition)
    {
        using var connection = OpenRaw(store.DatabasePath);
        Execute(connection, definition);
        var migration = SqliteSchemaV1.Migrations.Single();
        Execute(
            connection,
            "INSERT INTO schema_migrations(version, name, definition_checksum, applied_at_utc) VALUES ($version, $name, $checksum, $appliedAtUtc);",
            ("$version", migration.Version),
            ("$name", migration.Name),
            ("$checksum", migration.Checksum),
            ("$appliedAtUtc", 1000L));
    }

    private static void InsertProfileRow(SqliteConnection connection, RecurringFixedRegionProfileVersion profile)
    {
        Execute(
            connection,
            "INSERT INTO recurring_fixed_region_profile_versions (profile_id, profile_version, profile_digest, created_at_utc, target_type_code, rebind_policy_code, capture_semantics_code, coordinate_space_code, display_identity_status_code, stable_display_fingerprint, display_bounds_x, display_bounds_y, display_bounds_width, display_bounds_height, region_x, region_y, region_width, region_height, dpi_x, dpi_y, physical_width, physical_height, orientation_code, topology_digest, backend_code, audio_mode_code, duration_ms, countdown_seconds, output_directory, filename_prefix, filename_template, output_conflict_policy_code, wake_policy_code, desktop_requirement_code) VALUES ($profileId, $profileVersion, $profileDigest, $createdAtUtc, $targetType, $rebindPolicy, $captureSemantics, $coordinateSpace, $displayIdentityStatus, $stableDisplayFingerprint, $displayBoundsX, $displayBoundsY, $displayBoundsWidth, $displayBoundsHeight, $regionX, $regionY, $regionWidth, $regionHeight, $dpiX, $dpiY, $physicalWidth, $physicalHeight, $orientation, $topologyDigest, $backend, $audioMode, $durationMs, $countdownSeconds, $outputDirectory, $filenamePrefix, $filenameTemplate, $outputConflictPolicy, $wakePolicy, $desktopRequirement);",
            ("$profileId", profile.ProfileId),
            ("$profileVersion", profile.ProfileVersion),
            ("$profileDigest", profile.ProfileDigest),
            ("$createdAtUtc", profile.CreatedAtUtc.UtcDateTime.Ticks),
            ("$targetType", RecurringFixedRegionProfileCode.ToCode(profile.TargetType)),
            ("$rebindPolicy", RecurringFixedRegionProfileCode.ToCode(profile.RebindPolicy)),
            ("$captureSemantics", RecurringFixedRegionProfileCode.ToCode(profile.CaptureSemantics)),
            ("$coordinateSpace", RecurringFixedRegionProfileCode.ToCode(profile.CoordinateSpace)),
            ("$displayIdentityStatus", RecurringFixedRegionProfileCode.ToCode(profile.DisplayIdentityStatus)),
            ("$stableDisplayFingerprint", profile.StableDisplayFingerprint),
            ("$displayBoundsX", profile.DisplayBounds.X),
            ("$displayBoundsY", profile.DisplayBounds.Y),
            ("$displayBoundsWidth", profile.DisplayBounds.Width),
            ("$displayBoundsHeight", profile.DisplayBounds.Height),
            ("$regionX", profile.RegionWithinDisplay.X),
            ("$regionY", profile.RegionWithinDisplay.Y),
            ("$regionWidth", profile.RegionWithinDisplay.Width),
            ("$regionHeight", profile.RegionWithinDisplay.Height),
            ("$dpiX", profile.DpiX),
            ("$dpiY", profile.DpiY),
            ("$physicalWidth", profile.PhysicalWidth),
            ("$physicalHeight", profile.PhysicalHeight),
            ("$orientation", RecurringFixedRegionProfileCode.ToCode(profile.Orientation)),
            ("$topologyDigest", profile.TopologyDigest),
            ("$backend", RecurringFixedRegionProfileCode.ToCode(profile.Backend)),
            ("$audioMode", RecurringFixedRegionProfileCode.ToCode(profile.AudioMode)),
            ("$durationMs", checked((long)profile.Duration.TotalMilliseconds)),
            ("$countdownSeconds", profile.CountdownSeconds),
            ("$outputDirectory", profile.OutputDirectory),
            ("$filenamePrefix", profile.FilenamePrefix),
            ("$filenameTemplate", profile.FilenameTemplate),
            ("$outputConflictPolicy", RecurringFixedRegionProfileCode.ToCode(profile.OutputConflictPolicy)),
            ("$wakePolicy", RecurringFixedRegionProfileCode.ToCode(profile.WakePolicy)),
            ("$desktopRequirement", RecurringFixedRegionProfileCode.ToCode(profile.DesktopRequirement)));
    }

    private static void Execute(SqliteConnection connection, string sql, params (string Name, object Value)[] parameters)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        command.ExecuteNonQuery();
    }

    private static long Scalar(string databasePath, string sql)
    {
        using var connection = OpenRaw(databasePath);
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt64(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
    }

    private static string ScalarString(string databasePath, string sql)
    {
        using var connection = OpenRaw(databasePath);
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToString(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty;
    }

    private static SqliteConnection OpenRaw(string path)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Default,
            Pooling = false,
        }.ToString());
        connection.Open();
        return connection;
    }

    private sealed class TestDatabase : IDisposable
    {
        private readonly string root = Path.Combine(Path.GetTempPath(), "agent-recorder-task256-" + Guid.NewGuid().ToString("N"));

        public TestDatabase()
        {
            Directory.CreateDirectory(root);
            Store = new SqliteOperationalStore(Path.Combine(root, "state.db"));
        }

        public SqliteOperationalStore Store { get; }

        public string RootPath => root;

        public SqliteRecurringPlanProfileBindingRepository Bindings() => new(Store);

        public void Dispose()
        {
            try
            {
                Directory.Delete(root, recursive: true);
            }
            catch (DirectoryNotFoundException)
            {
            }
        }
    }
}
