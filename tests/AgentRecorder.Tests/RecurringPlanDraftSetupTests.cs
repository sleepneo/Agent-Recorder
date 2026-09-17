using System.Globalization;
using AgentRecorder.Core.Automation;
using AgentRecorder.Infrastructure;
using AgentRecorder.Persistence;
using Microsoft.Data.Sqlite;
using Xunit;

namespace AgentRecorder.Tests;

[Collection("NonParallel-AgentRecorderDataDir")]
public sealed class RecurringPlanDraftSetupTests
{
    private static readonly DateTimeOffset CreatedAt = new(2026, 9, 10, 10, 0, 0, TimeSpan.Zero);
    private const string TopologyDigest = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";

    [Fact]
    public void DailyAndWeeklySetupAreAtomicAndRestartRoundTripPreservesRevisionOne()
    {
        using var database = new TestDatabase();
        database.Store.Initialize();
        var profiles = new SqliteRecurringFixedRegionProfileRepository(database.Store);
        var profile = profiles.CreateVersion1(CreateProfile("setup-profile"));
        var daily = CreateSchedule(DateOnly.Parse("2026-09-10", CultureInfo.InvariantCulture), DateOnly.Parse("2026-09-12", CultureInfo.InvariantCulture));
        var weekly = RecurringPlanSchedule.CreateWeekly(
            TimeZoneInfo.Utc.Id,
            new DateOnly(2026, 9, 10),
            new DateOnly(2026, 9, 30),
            new TimeOnly(10, 0),
            new[] { DayOfWeek.Monday, DayOfWeek.Wednesday },
            3,
            profile.Duration,
            TimeSpan.Zero);

        var first = new SqliteRecurringPlanDraftSetupTransaction(database.Store)
            .CreateOrGet("daily-plan", daily, profile.Reference, CreatedAt);
        var second = new SqliteRecurringPlanDraftSetupTransaction(database.Store)
            .CreateOrGet("weekly-plan", weekly, profile.Reference, CreatedAt.AddMinutes(1));

        Assert.Equal(1, first.ScheduleVersion.ScheduleRevision);
        Assert.Equal(1, second.ScheduleVersion.ScheduleRevision);
        Assert.Equal(2L, Scalar(database.Store.DatabasePath, "SELECT COUNT(*) FROM plans;"));
        Assert.Equal(2L, Scalar(database.Store.DatabasePath, "SELECT COUNT(*) FROM recurring_schedule_versions;"));
        Assert.Equal(2L, Scalar(database.Store.DatabasePath, "SELECT COUNT(*) FROM recurring_plan_profile_bindings;"));

        var restarted = new SqliteRecurringPlanDraftSetupTransaction(
            new SqliteOperationalStore(database.Store.DatabasePath))
            .CreateOrGet("daily-plan", daily, profile.Reference, CreatedAt.AddDays(2));
        Assert.Equal(first.Plan.CreatedAtUtc, restarted.Plan.CreatedAtUtc);
        Assert.Equal(first.ScheduleVersion.CreatedAtUtc, restarted.ScheduleVersion.CreatedAtUtc);
        Assert.Equal(first.ProfileBinding.BoundAtUtc, restarted.ProfileBinding.BoundAtUtc);
        Assert.Equal(first.ConfigurationDigest, restarted.ConfigurationDigest);
        Assert.Equal(first.ExactProfile.Reference, restarted.ExactProfile.Reference);
    }

    [Fact]
    public void DurationMustMatchExactlyAndOneMillisecondMismatchIsRejectedBeforePlanInsert()
    {
        using var database = new TestDatabase();
        database.Store.Initialize();
        var profiles = new SqliteRecurringFixedRegionProfileRepository(database.Store);
        var profile = profiles.CreateVersion1(CreateProfile("duration-profile"));
        var schedule = CreateSchedule(
            new DateOnly(2026, 9, 10),
            new DateOnly(2026, 9, 12),
            profile.Duration.Add(TimeSpan.FromMilliseconds(1)));
        var oneTickMismatch = CreateSchedule(
            new DateOnly(2026, 9, 10),
            new DateOnly(2026, 9, 12),
            TimeSpan.FromTicks(profile.Duration.Ticks + 1));

        AssertCode(
            RecurringPlanDraftSetupPersistenceReasonCodes.DurationMismatch,
            () => new SqliteRecurringPlanDraftSetupTransaction(database.Store)
                .CreateOrGet("duration-plan", schedule, profile.Reference, CreatedAt));
        AssertCode(
            RecurringPlanDraftSetupPersistenceReasonCodes.DurationMismatch,
            () => new SqliteRecurringPlanDraftSetupTransaction(database.Store)
                .CreateOrGet("duration-tick-plan", oneTickMismatch, profile.Reference, CreatedAt));
        Assert.Equal(0L, Scalar(database.Store.DatabasePath, "SELECT COUNT(*) FROM plans;"));
        Assert.Equal(0L, Scalar(database.Store.DatabasePath, "SELECT COUNT(*) FROM recurring_schedule_versions;"));
        Assert.Equal(0L, Scalar(database.Store.DatabasePath, "SELECT COUNT(*) FROM recurring_plan_profile_bindings;"));
        Assert.Equal(1L, Scalar(database.Store.DatabasePath, "SELECT COUNT(*) FROM recurring_fixed_region_profile_versions;"));
    }

    [Fact]
    public void ReplayIsIdempotentAcrossEnabledPausedCancelledAndDoesNotChangePlanVersion()
    {
        using var database = new TestDatabase();
        database.Store.Initialize();
        var profiles = new SqliteRecurringFixedRegionProfileRepository(database.Store);
        var plans = new SqlitePlanDefinitionRepository(database.Store);
        var profile = profiles.CreateVersion1(CreateProfile("late-replay-profile"));
        var schedule = CreateSchedule(new DateOnly(2026, 9, 10), new DateOnly(2026, 9, 12));
        var setup = new SqliteRecurringPlanDraftSetupTransaction(database.Store);
        var first = setup.CreateOrGet("late-replay-plan", schedule, profile.Reference, CreatedAt);
        var plan = plans.Get(first.Plan.Id);

        foreach (var status in new[] { PlanDefinitionStatus.Enabled, PlanDefinitionStatus.Paused, PlanDefinitionStatus.Cancelled })
        {
            Assert.True(plan.TryTransition(status, CreatedAt.AddMinutes(plan.Version + 1)).Succeeded);
            plans.Update(plan, plan.Version - 1);
            var before = plans.Get(plan.Id);
            var replay = setup.CreateOrGet(plan.Id, schedule, profile.Reference, CreatedAt.AddDays(1));
            var after = plans.Get(plan.Id);
            Assert.Equal(first.ConfigurationDigest, replay.ConfigurationDigest);
            Assert.Equal(before.Status, after.Status);
            Assert.Equal(before.Version, after.Version);
            Assert.Equal(before.UpdatedAtUtc, after.UpdatedAtUtc);
        }
    }

    [Fact]
    public void ScheduleAndProfileDifferencesConflictAndIncompleteSetupIsNeverRepaired()
    {
        using var database = new TestDatabase();
        database.Store.Initialize();
        var profiles = new SqliteRecurringFixedRegionProfileRepository(database.Store);
        var plans = new SqlitePlanDefinitionRepository(database.Store);
        var schedules = new SqliteRecurringScheduleVersionRepository(database.Store);
        var bindings = new SqliteRecurringPlanProfileBindingRepository(database.Store);
        var profile = profiles.CreateVersion1(CreateProfile("conflict-profile"));
        var otherProfile = profiles.CreateVersion1(CreateProfile("other-profile", countdownSeconds: 4));
        var schedule = CreateSchedule(new DateOnly(2026, 9, 10), new DateOnly(2026, 9, 12));
        var otherSchedule = CreateSchedule(new DateOnly(2026, 9, 11), new DateOnly(2026, 9, 13));
        var setup = new SqliteRecurringPlanDraftSetupTransaction(database.Store);
        setup.CreateOrGet("conflict-plan", schedule, profile.Reference, CreatedAt);
        AssertCode(RecurringPlanDraftSetupPersistenceReasonCodes.ContentConflict, () => setup.CreateOrGet("conflict-plan", otherSchedule, profile.Reference, CreatedAt));
        AssertCode(RecurringPlanDraftSetupPersistenceReasonCodes.ContentConflict, () => setup.CreateOrGet("conflict-plan", schedule, otherProfile.Reference, CreatedAt));

        plans.Insert(new PlanDefinition("plan-only", false, CreatedAt));
        AssertCode(RecurringPlanDraftSetupPersistenceReasonCodes.IncompletePersistedData, () => setup.CreateOrGet("plan-only", schedule, profile.Reference, CreatedAt));

        plans.Insert(new PlanDefinition("schedule-only", false, CreatedAt));
        schedules.CreateOrGet("schedule-only", 1, schedule, CreatedAt);
        AssertCode(RecurringPlanDraftSetupPersistenceReasonCodes.IncompletePersistedData, () => setup.CreateOrGet("schedule-only", schedule, profile.Reference, CreatedAt));

        plans.Insert(new PlanDefinition("binding-only", false, CreatedAt));
        bindings.Bind("binding-only", profile.Reference, CreatedAt);
        AssertCode(RecurringPlanDraftSetupPersistenceReasonCodes.IncompletePersistedData, () => setup.CreateOrGet("binding-only", schedule, profile.Reference, CreatedAt));

        plans.Insert(new PlanDefinition("one-shot", true, CreatedAt));
        AssertCode(RecurringPlanDraftSetupPersistenceReasonCodes.ExistingOneShotPlan, () => setup.CreateOrGet("one-shot", schedule, profile.Reference, CreatedAt));
    }

    [Fact]
    public void ProfileV2AndScheduleV2DoNotReplaceOrBecomeTheDraftSetupIdentity()
    {
        using var database = new TestDatabase();
        database.Store.Initialize();
        var profiles = new SqliteRecurringFixedRegionProfileRepository(database.Store);
        var schedules = new SqliteRecurringScheduleVersionRepository(database.Store);
        var profile = profiles.CreateVersion1(CreateProfile("versioned-profile"));
        var profileV2 = profiles.CreateNext(profile.Reference, CreateProfileSpecification(countdownSeconds: 4), CreatedAt.AddMinutes(1));
        var schedule = CreateSchedule(new DateOnly(2026, 9, 10), new DateOnly(2026, 9, 12));
        var scheduleV2 = CreateSchedule(new DateOnly(2026, 9, 13), new DateOnly(2026, 9, 15));
        var setup = new SqliteRecurringPlanDraftSetupTransaction(database.Store);
        var first = setup.CreateOrGet("versioned-plan", schedule, profile.Reference, CreatedAt);

        schedules.CreateOrGet("versioned-plan", 2, scheduleV2, CreatedAt.AddMinutes(1));
        var replay = setup.CreateOrGet("versioned-plan", schedule, profile.Reference, CreatedAt.AddMinutes(2));

        Assert.Equal(1, replay.ScheduleVersion.ScheduleRevision);
        Assert.Equal(schedule.CanonicalDigest, replay.ScheduleVersion.ScheduleDigest);
        Assert.Equal(profile.Reference, replay.ProfileBinding.ProfileRef);
        Assert.Equal(first.ConfigurationDigest, replay.ConfigurationDigest);
        AssertCode(RecurringPlanDraftSetupPersistenceReasonCodes.ContentConflict, () => setup.CreateOrGet("versioned-plan", scheduleV2, profile.Reference, CreatedAt));
        AssertCode(RecurringPlanDraftSetupPersistenceReasonCodes.ContentConflict, () => setup.CreateOrGet("versioned-plan", schedule, profileV2.Reference, CreatedAt));
    }

    [Fact]
    public void EveryFailureInjectionPointRollsBackAllThreeBusinessRowsAndPreservesProfile()
    {
        foreach (var point in Enum.GetValues<RecurringPlanDraftSetupFailurePoint>())
        {
            using var database = new TestDatabase();
            database.Store.Initialize();
            var profiles = new SqliteRecurringFixedRegionProfileRepository(database.Store);
            var profile = profiles.CreateVersion1(CreateProfile("rollback-" + point));
            var transaction = new SqliteRecurringPlanDraftSetupTransaction(
                database.Store,
                failurePoint =>
                {
                    if (failurePoint == point)
                    {
                        throw new InvalidOperationException("injected draft setup failure");
                    }
                });

            AssertCode(RecurringPlanDraftSetupPersistenceReasonCodes.AtomicPersistenceFailed, () =>
                transaction.CreateOrGet(
                    "rollback-plan",
                    CreateSchedule(new DateOnly(2026, 9, 10), new DateOnly(2026, 9, 12)),
                    profile.Reference,
                    CreatedAt));
            Assert.Equal(0L, Scalar(database.Store.DatabasePath, "SELECT COUNT(*) FROM plans;"));
            Assert.Equal(0L, Scalar(database.Store.DatabasePath, "SELECT COUNT(*) FROM recurring_schedule_versions;"));
            Assert.Equal(0L, Scalar(database.Store.DatabasePath, "SELECT COUNT(*) FROM recurring_plan_profile_bindings;"));
            Assert.Equal(1L, Scalar(database.Store.DatabasePath, "SELECT COUNT(*) FROM recurring_fixed_region_profile_versions;"));
        }
    }

    [Fact]
    public async Task ConcurrentIdenticalSetupConvergesAndDifferentContentHasOneStableConflict()
    {
        using var database = new TestDatabase();
        database.Store.Initialize();
        var profiles = new SqliteRecurringFixedRegionProfileRepository(database.Store);
        var profile = profiles.CreateVersion1(CreateProfile("concurrent-profile"));
        var otherProfile = profiles.CreateVersion1(CreateProfile("concurrent-other-profile", countdownSeconds: 4));
        var schedule = CreateSchedule(new DateOnly(2026, 9, 10), new DateOnly(2026, 9, 12));
        var otherSchedule = CreateSchedule(new DateOnly(2026, 9, 11), new DateOnly(2026, 9, 13));

        var identical = await RunConcurrent(
            database.Store.DatabasePath,
            new[]
            {
                () => new SqliteRecurringPlanDraftSetupTransaction(new SqliteOperationalStore(database.Store.DatabasePath)).CreateOrGet("concurrent-identical", schedule, profile.Reference, CreatedAt),
                () => new SqliteRecurringPlanDraftSetupTransaction(new SqliteOperationalStore(database.Store.DatabasePath)).CreateOrGet("concurrent-identical", schedule, profile.Reference, CreatedAt.AddMinutes(1)),
            });
        Assert.Equal(2, identical.Count(attempt => attempt.Snapshot is not null));
        Assert.Equal(identical[0].Snapshot!.ConfigurationDigest, identical[1].Snapshot!.ConfigurationDigest);
        Assert.Equal(1L, Scalar(database.Store.DatabasePath, "SELECT COUNT(*) FROM plans WHERE id = 'concurrent-identical';"));

        var differentSchedule = await RunConcurrent(
            database.Store.DatabasePath,
            new[]
            {
                () => new SqliteRecurringPlanDraftSetupTransaction(new SqliteOperationalStore(database.Store.DatabasePath)).CreateOrGet("concurrent-schedule", schedule, profile.Reference, CreatedAt),
                () => new SqliteRecurringPlanDraftSetupTransaction(new SqliteOperationalStore(database.Store.DatabasePath)).CreateOrGet("concurrent-schedule", otherSchedule, profile.Reference, CreatedAt),
            });
        Assert.Equal(1, differentSchedule.Count(attempt => attempt.Snapshot is not null));
        Assert.Equal(1, differentSchedule.Count(attempt => attempt.Exception?.Code == RecurringPlanDraftSetupPersistenceReasonCodes.ContentConflict));

        var differentProfile = await RunConcurrent(
            database.Store.DatabasePath,
            new[]
            {
                () => new SqliteRecurringPlanDraftSetupTransaction(new SqliteOperationalStore(database.Store.DatabasePath)).CreateOrGet("concurrent-profile", schedule, profile.Reference, CreatedAt),
                () => new SqliteRecurringPlanDraftSetupTransaction(new SqliteOperationalStore(database.Store.DatabasePath)).CreateOrGet("concurrent-profile", schedule, otherProfile.Reference, CreatedAt),
            });
        Assert.Equal(1, differentProfile.Count(attempt => attempt.Snapshot is not null));
        Assert.Equal(1, differentProfile.Count(attempt => attempt.Exception?.Code == RecurringPlanDraftSetupPersistenceReasonCodes.ContentConflict));
    }

    [Fact]
    public void TamperedImmutableFieldsFailClosedAndSetupHasNoExecutionOrMediaSideEffects()
    {
        using var database = new TestDatabase();
        database.Store.Initialize();
        var outputDirectory = Path.Combine(database.RootPath, "output");
        Directory.CreateDirectory(outputDirectory);
        var profiles = new SqliteRecurringFixedRegionProfileRepository(database.Store);
        var profile = profiles.CreateVersion1(CreateProfile("tamper-profile", outputDirectory: outputDirectory));
        var schedule = CreateSchedule(new DateOnly(2026, 9, 10), new DateOnly(2026, 9, 12));
        var setup = new SqliteRecurringPlanDraftSetupTransaction(database.Store);
        setup.CreateOrGet("tamper-plan", schedule, profile.Reference, CreatedAt);

        var tamperedDigest = schedule.CanonicalDigest[..^1] + (schedule.CanonicalDigest[^1] == '0' ? '1' : '0');
        Execute(database.Store.DatabasePath, "PRAGMA foreign_keys = OFF; UPDATE recurring_schedule_versions SET schedule_digest = $digest WHERE plan_id = 'tamper-plan' AND schedule_revision = 1;", ("$digest", tamperedDigest));
        AssertCode(RecurringPlanDraftSetupPersistenceReasonCodes.PersistedDataInvalid, () => setup.CreateOrGet("tamper-plan", schedule, profile.Reference, CreatedAt));

        foreach (var table in new[]
        {
            "plan_occurrences", "recurring_schedule_cursors", "recurring_advancement_operations",
            "consent_leases", "lease_uses", "recording_runs", "authorized_capture_scopes", "setup_intents",
        })
        {
            Assert.Equal(0L, Scalar(database.Store.DatabasePath, $"SELECT COUNT(*) FROM {table};"));
        }
        Assert.Empty(Directory.GetFiles(outputDirectory, "*", SearchOption.AllDirectories));
    }

    [Fact]
    public void PlanRulesAndBindingTamperingFailClosed()
    {
        foreach (var tamper in new[] { TamperKind.PlanPeriodicity, TamperKind.RulesDigest, TamperKind.BindingProfileDigest })
        {
            using var database = new TestDatabase();
            database.Store.Initialize();
            var profiles = new SqliteRecurringFixedRegionProfileRepository(database.Store);
            var profile = profiles.CreateVersion1(CreateProfile("tamper-matrix-" + tamper));
            var schedule = CreateSchedule(new DateOnly(2026, 9, 10), new DateOnly(2026, 9, 12));
            var setup = new SqliteRecurringPlanDraftSetupTransaction(database.Store);
            setup.CreateOrGet("tamper-matrix-plan", schedule, profile.Reference, CreatedAt);

            switch (tamper)
            {
                case TamperKind.PlanPeriodicity:
                    Execute(database.Store.DatabasePath, "PRAGMA foreign_keys = OFF; UPDATE plans SET is_one_time = 1 WHERE id = 'tamper-matrix-plan';");
                    AssertCode(RecurringPlanDraftSetupPersistenceReasonCodes.ExistingOneShotPlan, () => setup.CreateOrGet("tamper-matrix-plan", schedule, profile.Reference, CreatedAt));
                    break;
                case TamperKind.RulesDigest:
                    var rulesDigest = RecurringTimeZoneRulesDigest.Compute(schedule.TimeZoneInfo);
                    var tamperedRules = rulesDigest[..^1] + (rulesDigest[^1] == '0' ? '1' : '0');
                    Execute(database.Store.DatabasePath, "PRAGMA foreign_keys = OFF; UPDATE recurring_schedule_versions SET time_zone_rules_digest = $digest WHERE plan_id = 'tamper-matrix-plan' AND schedule_revision = 1;", ("$digest", tamperedRules));
                    AssertCode(RecurringPlanDraftSetupPersistenceReasonCodes.PersistedDataInvalid, () => setup.CreateOrGet("tamper-matrix-plan", schedule, profile.Reference, CreatedAt));
                    break;
                case TamperKind.BindingProfileDigest:
                    Execute(database.Store.DatabasePath, "PRAGMA foreign_keys = OFF; DROP TRIGGER trg_recurring_plan_profile_bindings_immutable_update; UPDATE recurring_plan_profile_bindings SET profile_digest = $digest WHERE plan_id = 'tamper-matrix-plan';", ("$digest", profile.ProfileDigest[..^1] + (profile.ProfileDigest[^1] == '0' ? '1' : '0')));
                    AssertCode(RecurringPlanDraftSetupPersistenceReasonCodes.PersistedDataInvalid, () => setup.CreateOrGet("tamper-matrix-plan", schedule, profile.Reference, CreatedAt));
                    break;
            }
        }
    }

    private static RecurringPlanSchedule CreateSchedule(DateOnly start, DateOnly end, TimeSpan? duration = null) =>
        RecurringPlanSchedule.CreateDaily(
            TimeZoneInfo.Utc.Id,
            start,
            end,
            new TimeOnly(10, 0),
            3,
            duration ?? TimeSpan.FromMinutes(2),
            TimeSpan.Zero);

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
            outputDirectory ?? Path.Combine(Path.GetTempPath(), "task257-output"),
            "demo",
            AuthorizedOutputConflictPolicy.FailIfExists,
            AuthorizedWakePolicy.NaturalWakeOnly,
            AuthorizedDesktopRequirement.InteractiveDesktopRequired);

    private static async Task<Attempt[]> RunConcurrent(string databasePath, IReadOnlyList<Func<RecurringPlanDraftSetupSnapshot>> actions)
    {
        using var barrier = new Barrier(actions.Count);
        var tasks = actions.Select(action => Task.Run(() =>
        {
            if (!barrier.SignalAndWait(TimeSpan.FromSeconds(5)))
            {
                throw new TimeoutException("The setup concurrency barrier did not open.");
            }

            try
            {
                return new Attempt(action(), null);
            }
            catch (Phase3PersistenceException exception)
            {
                return new Attempt(null, exception);
            }
        })).ToArray();
        return await Task.WhenAll(tasks).WaitAsync(TimeSpan.FromSeconds(10));
    }

    private static void AssertCode(string expected, Action action)
    {
        var exception = Assert.Throws<Phase3PersistenceException>(action);
        Assert.Equal(expected, exception.Code);
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

    private static long Scalar(string databasePath, string sql)
    {
        using var connection = OpenRaw(databasePath);
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture);
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

    private sealed record Attempt(RecurringPlanDraftSetupSnapshot? Snapshot, Phase3PersistenceException? Exception);

    private enum TamperKind
    {
        PlanPeriodicity,
        RulesDigest,
        BindingProfileDigest,
    }

    private sealed class TestDatabase : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "agent-recorder-task257-" + Guid.NewGuid().ToString("N"));

        public TestDatabase()
        {
            Directory.CreateDirectory(_root);
            Store = new SqliteOperationalStore(Path.Combine(_root, "state.db"));
        }

        public SqliteOperationalStore Store { get; }

        public string RootPath => _root;

        public void Dispose()
        {
            try
            {
                Directory.Delete(_root, recursive: true);
            }
            catch (DirectoryNotFoundException)
            {
            }
        }
    }
}
