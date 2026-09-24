using System.Text;
using AgentRecorder.Api;
using AgentRecorder.Core.Automation;
using AgentRecorder.Infrastructure;
using AgentRecorder.Persistence;
using Microsoft.Data.Sqlite;
using Xunit;

namespace AgentRecorder.Tests;

[Collection("NonParallel-AgentRecorderDataDir")]
public sealed class RecurringPlanSetupIntentTests
{
    [Fact]
    public void ParserAcceptsDailyAndWeeklyAndCanonicalizesEquivalentWeekdayOrder()
    {
        var daily = RecurringPlanApiRequestParser.Parse(Body("daily", "null", "2026-09-20", "2026-09-27", 8, 30, 300, "2026-09-28T00:00:00+08:00"), "daily-key");
        Assert.True(daily.Schedule.IsDaily);
        Assert.Empty(daily.Schedule.WeeklyDays);
        Assert.Equal("D:\\Recordings\\Agent\\", daily.OutputDirectory, ignoreCase: true);

        var mondayFriday = RecurringPlanApiRequestParser.Parse(Body("weekly", "[\"friday\",\"monday\"]", "2026-09-20", "2026-09-27", 2, 30, 300, "2026-09-28T00:00:00+08:00"), "weekly-key");
        var fridayMonday = RecurringPlanApiRequestParser.Parse(Body("weekly", "[\"monday\",\"friday\"]", "2026-09-20", "2026-09-27", 2, 30, 300, "2026-09-28T00:00:00+08:00"), "weekly-key-2");
        Assert.True(mondayFriday.Schedule.IsWeekly);
        Assert.Equal(new[] { DayOfWeek.Monday, DayOfWeek.Friday }, mondayFriday.Schedule.WeeklyDays);
        Assert.Equal(mondayFriday.Schedule.CanonicalDigest, fridayMonday.Schedule.CanonicalDigest);
        Assert.NotEqual(daily.Schedule.CanonicalDigest, mondayFriday.Schedule.CanonicalDigest);
    }

    [Fact]
    public void EquivalentJsonPropertyOrderProducesOneTrustedRequestDigest()
    {
        var firstRequest = RecurringPlanApiRequestParser.Parse(
            Body("weekly", "[\"friday\",\"monday\"]", "2026-09-20", "2026-09-27", 2, 30, 300, "2026-09-28T00:00:00+08:00"),
            "property-order-key");
        var reorderedRequest = RecurringPlanApiRequestParser.Parse(
            """
            {
              "requested_authorization": {"max_total_duration_seconds":60,"max_runs":2,"valid_until":"2026-09-28T00:00:00+08:00","mode":"recurring_lease"},
              "schedule": {"latest_start_grace_seconds":300,"maximum_occurrences":2,"weekdays":["monday","friday"],"local_time":"09:30:00","local_end_date":"2026-09-27","local_start_date":"2026-09-20","time_zone_id":"China Standard Time","kind":"weekly"},
              "recording_spec": {"output":{"filename_prefix":"daily-review","directory":"D:\\Recordings\\Agent"},"backend":"ffmpeg-region","countdown_seconds":0,"duration_seconds":30,"audio":{"mode":"none"},"source":{"type":"fixed_region"}}
            }
            """,
            "property-order-key");

        var first = SnapshotFromRequest(firstRequest, "property-order-intent");
        var reordered = SnapshotFromRequest(reorderedRequest, "property-order-intent");

        Assert.Equal(first.Schedule.CanonicalDigest, reordered.Schedule.CanonicalDigest);
        Assert.Equal(first.RequestDigest, reordered.RequestDigest);
    }

    [Fact]
    public void ParserRejectsUnsupportedShapeWeekdayDuplicatesAndUnderCoveredQuota()
    {
        AssertInvalid(Body("daily", "[]", "2026-09-20", "2026-09-27", 8, 30, 300, "2026-09-28T00:00:00+08:00"));
        AssertInvalid(Body("weekly", "[\"monday\",\"monday\"]", "2026-09-20", "2026-09-27", 2, 30, 300, "2026-09-28T00:00:00+08:00"));
        AssertInvalid(Body("daily", "null", "2026-09-20", "2026-09-27", 8, 30, 300, "2026-09-28T00:00:00"));
        AssertInvalid(Body("daily", "null", "2026-09-20", "2026-09-27", 8, 30, 300, "2026-09-28T00:00:00+08:00", maxTotalDurationSeconds: 1));
        AssertInvalid(Body("daily", "null", "2026-09-20", "2026-09-27", 8, 30, 301, "2026-09-28T00:00:00+08:00"));
        AssertInvalid(Body("daily", "null", "2026-09-20", "2027-10-01", 8, 30, 300, "2027-10-02T00:00:00+08:00"));
    }

    [Fact]
    public void LeaseValidityBoundaryRequiresOneTickAfterLatestPlannedEnd()
    {
        var schedule = RecurringPlanSchedule.CreateDaily(
            "China Standard Time", new DateOnly(2026, 1, 1), new DateOnly(2026, 1, 2),
            new TimeOnly(9, 30), 2, TimeSpan.FromSeconds(30), TimeSpan.FromMinutes(5));
        var latestEnd = RecurringScheduleAuthorizationBounds.GetLatestValidPlannedEndUtc("task-280r-boundary", 1, schedule);

        AssertInvalid(Body("daily", "null", "2026-01-01", "2026-01-02", 2, 30, 300, latestEnd.AddTicks(-1).ToString("O")));
        AssertInvalid(Body("daily", "null", "2026-01-01", "2026-01-02", 2, 30, 300, latestEnd.ToString("O")));
        var accepted = RecurringPlanApiRequestParser.Parse(
            Body("daily", "null", "2026-01-01", "2026-01-02", 2, 30, 300, latestEnd.AddTicks(1).ToString("O")),
            "boundary-key");
        Assert.Equal(latestEnd.AddTicks(1), accepted.LeaseValidUntilUtc);

        using var database = new TemporaryDatabase();
        var before = CreateSnapshot(schedule: schedule, expiresAtUtc: latestEnd, leaseValidUntilUtc: latestEnd);
        var after = CreateSnapshot(schedule: schedule, expiresAtUtc: latestEnd.AddTicks(1), leaseValidUntilUtc: latestEnd.AddTicks(1), intentId: "boundary-after");
        var service = new RecurringSetupIntentService(database.Store, () => At(1));
        Assert.Equal("recurring_setup_intent_snapshot_invalid", service.CreateOrGet(before).Reason);
        Assert.Equal(RecurringSetupIntentResultStatus.Created, service.CreateOrGet(after).Result);
    }

    [Fact]
    public void DigestBindsIdempotencyKeySidAndSessionButNotGeneratedIntentId()
    {
        var first = CreateSnapshot();
        var differentIntentId = CreateSnapshot(intentId: "different-generated-id");
        var differentSid = CreateSnapshot(currentUserSid: "S-1-5-21-other");
        var differentSession = CreateSnapshot(sessionBinding: "session-other");

        Assert.Equal(first.RequestDigest, differentIntentId.RequestDigest);
        Assert.NotEqual(first.RequestDigest, differentSid.RequestDigest);
        Assert.NotEqual(first.RequestDigest, differentSession.RequestDigest);
    }

    [Theory]
    [MemberData(nameof(ParserRejectionCases))]
    public void ParserRejectsNamedContractViolation(string caseName)
    {
        if (caseName == "idempotency-empty")
        {
            Assert.Throws<ApiException>(() => RecurringPlanApiRequestParser.NormalizeIdempotencyKey(""));
            return;
        }

        if (caseName == "idempotency-overlong")
        {
            Assert.Throws<ApiException>(() => RecurringPlanApiRequestParser.NormalizeIdempotencyKey(new string('k', 129)));
            return;
        }

        AssertInvalid(ParserInvalidBody(caseName));
    }

    public static IEnumerable<object[]> ParserRejectionCases() => new[]
    {
        new object[] { "root-unknown" }, new object[] { "root-missing" }, new object[] { "root-duplicate" },
        new object[] { "nested-unknown" }, new object[] { "nested-duplicate" }, new object[] { "wrong-type" },
        new object[] { "non-integer" }, new object[] { "coordinates" }, new object[] { "profile-ref" },
        new object[] { "complete-filename" }, new object[] { "unsupported-target" }, new object[] { "unsupported-audio" },
        new object[] { "unsupported-backend" }, new object[] { "unsupported-countdown" }, new object[] { "unsupported-kind" },
        new object[] { "unsupported-auth-mode" }, new object[] { "relative-output" }, new object[] { "traversal-output" },
        new object[] { "unsafe-prefix" }, new object[] { "reserved-prefix" }, new object[] { "overlong-prefix" },
        new object[] { "invalid-date" }, new object[] { "date-order" }, new object[] { "date-span" },
        new object[] { "invalid-time" }, new object[] { "invalid-time-zone" }, new object[] { "weekly-null" },
        new object[] { "weekly-empty" }, new object[] { "weekly-duplicate" }, new object[] { "weekly-case" },
        new object[] { "weekly-code" }, new object[] { "duration-low" }, new object[] { "duration-high" },
        new object[] { "occurrences-low" }, new object[] { "occurrences-high" }, new object[] { "run-mismatch" },
        new object[] { "total-low" }, new object[] { "grace-low" }, new object[] { "grace-high" },
        new object[] { "validity-before" }, new object[] { "idempotency-empty" }, new object[] { "idempotency-overlong" },
    };

    private static string ParserInvalidBody(string caseName)
    {
        var body = Body("daily", "null", "2026-09-20", "2026-09-27", 8, 30, 300, "2026-09-28T00:00:00+08:00");
        return caseName switch
        {
            "root-unknown" => body.Replace("\"recording_spec\":", "\"unknown\":1,\"recording_spec\":", StringComparison.Ordinal),
            "root-missing" => body.Replace("\"recording_spec\":", "\"missing_recording_spec\":", StringComparison.Ordinal),
            "root-duplicate" => body.Replace("\"schedule\": {", "\"recording_spec\":{},\"schedule\": {", StringComparison.Ordinal),
            "nested-unknown" => body.Replace("\"filename_prefix\":\"daily-review\"", "\"filename_prefix\":\"daily-review\",\"profile_ref\":\"p\"", StringComparison.Ordinal),
            "nested-duplicate" => body.Replace("\"filename_prefix\":\"daily-review\"", "\"filename_prefix\":\"daily-review\",\"filename_prefix\":\"daily-review\"", StringComparison.Ordinal),
            "wrong-type" => body.Replace("\"duration_seconds\": 30", "\"duration_seconds\":\"30\"", StringComparison.Ordinal),
            "non-integer" => body.Replace("\"maximum_occurrences\":8", "\"maximum_occurrences\":8.5", StringComparison.Ordinal),
            "coordinates" => body.Replace("\"type\":\"fixed_region\"", "\"type\":\"fixed_region\",\"x\":1,\"y\":2", StringComparison.Ordinal),
            "profile-ref" => body.Replace("\"filename_prefix\":\"daily-review\"", "\"filename_prefix\":\"daily-review\",\"region_profile_ref\":\"p\"", StringComparison.Ordinal),
            "complete-filename" => body.Replace("\"filename_prefix\":\"daily-review\"", "\"filename_prefix\":\"daily-review\",\"filename\":\"capture.mp4\"", StringComparison.Ordinal),
            "unsupported-target" => body.Replace("\"type\":\"fixed_region\"", "\"type\":\"window\"", StringComparison.Ordinal),
            "unsupported-audio" => body.Replace("\"mode\":\"none\"", "\"mode\":\"system\"", StringComparison.Ordinal),
            "unsupported-backend" => body.Replace("\"backend\": \"ffmpeg-region\"", "\"backend\": \"wgc\"", StringComparison.Ordinal),
            "unsupported-countdown" => body.Replace("\"countdown_seconds\": 0", "\"countdown_seconds\": 1", StringComparison.Ordinal),
            "unsupported-kind" => body.Replace("\"kind\":\"daily\"", "\"kind\":\"once\"", StringComparison.Ordinal),
            "unsupported-auth-mode" => body.Replace("\"mode\":\"recurring_lease\"", "\"mode\":\"standing_lease\"", StringComparison.Ordinal),
            "relative-output" => body.Replace("D:\\\\Recordings\\\\Agent", "relative", StringComparison.Ordinal),
            "traversal-output" => body.Replace("D:\\\\Recordings\\\\Agent", "D:\\\\Recordings\\\\.\\\\Agent", StringComparison.Ordinal),
            "unsafe-prefix" => body.Replace("daily-review", "daily.review", StringComparison.Ordinal),
            "reserved-prefix" => body.Replace("daily-review", "CON", StringComparison.Ordinal),
            "overlong-prefix" => body.Replace("daily-review", new string('x', 65), StringComparison.Ordinal),
            "invalid-date" => body.Replace("2026-09-20", "2026-09-31", StringComparison.Ordinal),
            "date-order" => body.Replace("\"local_end_date\":\"2026-09-27\"", "\"local_end_date\":\"2026-09-19\"", StringComparison.Ordinal),
            "date-span" => body.Replace("2026-09-27", "2027-10-01", StringComparison.Ordinal),
            "invalid-time" => body.Replace("09:30:00", "09:30", StringComparison.Ordinal),
            "invalid-time-zone" => body.Replace("China Standard Time", "Invalid/Zone", StringComparison.Ordinal),
            "weekly-null" => body.Replace("\"kind\":\"daily\"", "\"kind\":\"weekly\"", StringComparison.Ordinal),
            "weekly-empty" => body.Replace("\"kind\":\"daily\"", "\"kind\":\"weekly\"", StringComparison.Ordinal).Replace("\"weekdays\":null", "\"weekdays\":[]", StringComparison.Ordinal),
            "weekly-duplicate" => body.Replace("\"kind\":\"daily\"", "\"kind\":\"weekly\"", StringComparison.Ordinal).Replace("\"weekdays\":null", "\"weekdays\":[\"monday\",\"monday\"]", StringComparison.Ordinal),
            "weekly-case" => body.Replace("\"kind\":\"daily\"", "\"kind\":\"weekly\"", StringComparison.Ordinal).Replace("\"weekdays\":null", "\"weekdays\":[\"Monday\"]", StringComparison.Ordinal),
            "weekly-code" => body.Replace("\"kind\":\"daily\"", "\"kind\":\"weekly\"", StringComparison.Ordinal).Replace("\"weekdays\":null", "\"weekdays\":[\"moonday\"]", StringComparison.Ordinal),
            "duration-low" => body.Replace("\"duration_seconds\": 30", "\"duration_seconds\": 0", StringComparison.Ordinal),
            "duration-high" => body.Replace("\"duration_seconds\": 30", "\"duration_seconds\": 601", StringComparison.Ordinal),
            "occurrences-low" => body.Replace("\"maximum_occurrences\":8", "\"maximum_occurrences\":0", StringComparison.Ordinal),
            "occurrences-high" => body.Replace("\"maximum_occurrences\":8", "\"maximum_occurrences\":367", StringComparison.Ordinal),
            "run-mismatch" => body.Replace("\"max_runs\":8", "\"max_runs\":7", StringComparison.Ordinal),
            "total-low" => body.Replace("\"max_total_duration_seconds\":240", "\"max_total_duration_seconds\":239", StringComparison.Ordinal),
            "grace-low" => body.Replace("\"latest_start_grace_seconds\":300", "\"latest_start_grace_seconds\":-1", StringComparison.Ordinal),
            "grace-high" => body.Replace("\"latest_start_grace_seconds\":300", "\"latest_start_grace_seconds\":301", StringComparison.Ordinal),
            "validity-before" => body.Replace("2026-09-28T00:00:00+08:00", "2026-09-27T00:00:00Z", StringComparison.Ordinal),
            _ => throw new ArgumentOutOfRangeException(nameof(caseName), caseName, null),
        };
    }

    [Fact]
    public void CreateOrGetPersistsImmutableRecurringIntentAndReadbackRehydratesIt()
    {
        using var database = new TemporaryDatabase();
        var snapshot = CreateSnapshot();
        var service = new RecurringSetupIntentService(database.Store, () => At(1));

        var created = service.CreateOrGet(snapshot);

        Assert.Equal(RecurringSetupIntentResultStatus.Created, created.Result);
        Assert.Equal(RecurringSetupIntentStatus.RegionSelectionPending, created.IntentStatus);
        Assert.True(created.Changed);
        Assert.Equal(1L, Scalar(database.Store, "SELECT COUNT(*) FROM setup_intents;"));
        Assert.Equal(0L, Scalar(database.Store, "SELECT COUNT(*) FROM plans;"));
        Assert.Equal(RecurringSetupIntentCodes.IntentKind, Text(database.Store, "SELECT intent_kind_code FROM setup_intents;"));
        Assert.Equal(2L, Scalar(database.Store, "SELECT recurring_maximum_occurrences FROM setup_intents;"));
        Assert.Equal(0L, Scalar(database.Store, "SELECT recurring_countdown_seconds FROM setup_intents;"));

        var readback = service.Get(snapshot.IntentId, snapshot.CurrentUserSid, snapshot.SessionBinding);
        Assert.NotNull(readback);
        Assert.Equal(snapshot.RequestDigest, readback!.Snapshot.RequestDigest);
        Assert.Equal(snapshot.Schedule.CanonicalDigest, readback.Snapshot.Schedule.CanonicalDigest);
        Assert.Equal(snapshot.TimeZoneRulesDigest, readback.Snapshot.TimeZoneRulesDigest);
        Assert.Equal(RecurringSetupIntentStatus.RegionSelectionPending, readback.Status);
    }

    [Fact]
    public void ExactReplayDoesNotMutateAndDifferentRequestOrPrincipalIsolated()
    {
        using var database = new TemporaryDatabase();
        var first = CreateSnapshot();
        var service = new RecurringSetupIntentService(database.Store, () => At(1));
        Assert.Equal(RecurringSetupIntentResultStatus.Created, service.CreateOrGet(first).Result);
        var before = Row(database.Store);

        var replay = service.CreateOrGet(CreateSnapshot(intentId: "different-id"));
        Assert.Equal(RecurringSetupIntentResultStatus.Existing, replay.Result);
        Assert.Equal(first.IntentId, replay.IntentId);
        Assert.Equal(before, Row(database.Store));

        var changed = CreateSnapshot(schedule: RecurringPlanSchedule.CreateDaily("China Standard Time", new DateOnly(2026, 1, 1), new DateOnly(2026, 1, 2), new TimeOnly(9, 30), 2, TimeSpan.FromSeconds(31), TimeSpan.FromMinutes(5)));
        Assert.Equal(RecurringSetupIntentResultStatus.Conflict, service.CreateOrGet(changed).Result);
        Assert.Equal(before, Row(database.Store));

        var otherPrincipal = CreateSnapshot(intentId: "other-principal", currentUserSid: "S-1-5-21-other");
        Assert.Equal(RecurringSetupIntentResultStatus.Created, service.CreateOrGet(otherPrincipal).Result);
        Assert.Equal(2L, Scalar(database.Store, "SELECT COUNT(*) FROM setup_intents;"));
        Assert.Null(service.Get(first.IntentId, "S-1-5-21-other", first.SessionBinding));
        Assert.Null(service.Get(first.IntentId, first.CurrentUserSid, "session-other"));
    }

    [Fact]
    public void StandingAndRecurringReuseOfTheSameIdentityIsAStableCrossKindConflict()
    {
        using var database = new TemporaryDatabase();
        var recurring = CreateSnapshot();
        var standing = StandingSetupIntentSnapshot.CreateForTrustedSetupAdapter(
            "standing-id", recurring.IdempotencyKey, recurring.CurrentUserSid, recurring.SessionBinding,
            At(0), At(300), At(10), At(20), At(80), TimeSpan.FromSeconds(30), At(360),
            Path.Combine(Path.GetTempPath(), "AgentRecorderSetupIntent", "captures"), "capture.mp4");

        Assert.Equal(RecurringSetupIntentResultStatus.Created, new RecurringSetupIntentService(database.Store, () => At(1)).CreateOrGet(recurring).Result);
        Assert.Equal(StandingSetupIntentResultStatus.Conflict, new StandingSetupIntentService(database.Store, () => At(1)).CreateOrGet(standing).Result);

        using var reverseDatabase = new TemporaryDatabase();
        Assert.Equal(StandingSetupIntentResultStatus.Created, new StandingSetupIntentService(reverseDatabase.Store, () => At(1)).CreateOrGet(standing).Result);
        Assert.Equal(RecurringSetupIntentResultStatus.Conflict, new RecurringSetupIntentService(reverseDatabase.Store, () => At(1)).CreateOrGet(recurring).Result);
    }

    [Fact]
    public async Task ConcurrentIdenticalWritersConvergeToOneDurableIntent()
    {
        using var database = new TemporaryDatabase();
        var snapshot = CreateSnapshot();
        var first = new RecurringSetupIntentService(database.Store, () => At(1));
        var second = new RecurringSetupIntentService(database.Store, () => At(1));

        var results = await Task.WhenAll(Task.Run(() => first.CreateOrGet(snapshot)), Task.Run(() => second.CreateOrGet(snapshot)));

        Assert.Equal(1, results.Count(result => result.Result == RecurringSetupIntentResultStatus.Created));
        Assert.Equal(1, results.Count(result => result.Result == RecurringSetupIntentResultStatus.Existing));
        Assert.Equal(1L, Scalar(database.Store, "SELECT COUNT(*) FROM setup_intents;"));
    }

    [Fact]
    public async Task ConcurrentConflictingWritersProduceOneWinnerAndOneStableConflict()
    {
        using var database = new TemporaryDatabase();
        var first = CreateSnapshot(intentId: "conflicting-first");
        var second = CreateSnapshot(
            intentId: "conflicting-second",
            schedule: RecurringPlanSchedule.CreateDaily("China Standard Time", new DateOnly(2026, 1, 1), new DateOnly(2026, 1, 2), new TimeOnly(9, 30), 2, TimeSpan.FromSeconds(31), TimeSpan.FromMinutes(5)));
        var firstService = new RecurringSetupIntentService(database.Store, () => At(1));
        var secondService = new RecurringSetupIntentService(new SqliteOperationalStore(database.Store.DatabasePath), () => At(1));
        using var barrier = new Barrier(2);

        var results = await Task.WhenAll(
            Task.Run(() => { barrier.SignalAndWait(); return firstService.CreateOrGet(first); }),
            Task.Run(() => { barrier.SignalAndWait(); return secondService.CreateOrGet(second); }));

        Assert.Equal(1, results.Count(result => result.Result == RecurringSetupIntentResultStatus.Created));
        Assert.Equal(1, results.Count(result => result.Result == RecurringSetupIntentResultStatus.Conflict));
        Assert.Equal("recurring_setup_intent_request_conflict", results.Single(result => result.Result == RecurringSetupIntentResultStatus.Conflict).Reason);
        Assert.Equal(1L, Scalar(database.Store, "SELECT COUNT(*) FROM setup_intents;"));
    }

    [Fact]
    public void ExpiryReconciliationIsBoundedAndIdempotent()
    {
        using var database = new TemporaryDatabase();
        var snapshot = CreateSnapshot(schedule: ShortSchedule(), expiresAtUtc: At(10), leaseValidUntilUtc: At(10));
        var service = new RecurringSetupIntentService(database.Store, () => At(1));
        Assert.Equal(RecurringSetupIntentResultStatus.Created, service.CreateOrGet(snapshot).Result);

        var expiredService = new RecurringSetupIntentService(database.Store, () => At(10));
        Assert.Equal(1, expiredService.ReconcileExpiredPending(1));
        Assert.Equal(0, expiredService.ReconcileExpiredPending(1));
        var readback = expiredService.Get(snapshot.IntentId, snapshot.CurrentUserSid, snapshot.SessionBinding);
        Assert.NotNull(readback);
        Assert.Equal(RecurringSetupIntentStatus.Expired, readback!.Status);
        Assert.Equal("recurring_setup_intent_expired", readback.TerminalReasonCode);
        Assert.Equal(1L, Scalar(database.Store, "SELECT version FROM setup_intents;"));
    }

    [Fact]
    public void TamperedRecurringScheduleFailsClosedOnReadback()
    {
        using var database = new TemporaryDatabase();
        var snapshot = CreateSnapshot();
        var service = new RecurringSetupIntentService(database.Store, () => At(1));
        Assert.Equal(RecurringSetupIntentResultStatus.Created, service.CreateOrGet(snapshot).Result);
        using (var connection = database.Store.OpenConnection())
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "UPDATE setup_intents SET recurring_max_uses = 1 WHERE intent_id = $id;";
            command.Parameters.AddWithValue("$id", snapshot.IntentId);
            command.ExecuteNonQuery();
        }
        Assert.Null(service.Get(snapshot.IntentId, snapshot.CurrentUserSid, snapshot.SessionBinding));
    }

    [Fact]
    public void RecurringShapeRejectsLegacyOutputDirectoryOnNormalUpdateAndInsert()
    {
        using var database = new TemporaryDatabase();
        var snapshot = CreateSnapshot();
        var service = new RecurringSetupIntentService(database.Store, () => At(1));
        Assert.Equal(RecurringSetupIntentResultStatus.Created, service.CreateOrGet(snapshot).Result);

        Assert.Throws<SqliteException>(() => Execute(database.Store, "UPDATE setup_intents SET output_directory = 'D:\\Legacy\\' WHERE intent_id = 'recurring-intent-1';"));
        Assert.Throws<SqliteException>(() => Execute(database.Store, """
            INSERT INTO setup_intents
            SELECT 'recurring-invalid-insert', intent_kind_code, 'recurring-invalid-insert-key', request_digest,
                   current_user_sid, session_binding, status_code, requested_at_utc, expires_at_utc,
                   plan_id, occurrence_id, lease_id, scope_id, created_at_utc, updated_at_utc, version,
                   terminal_reason_code, scheduled_start_utc, latest_start_utc, planned_end_utc, maximum_duration_ms,
                   lease_valid_until_utc, 'D:\Legacy\', frozen_file_name,
                   recurring_schedule_kind_code, recurring_time_zone_id, recurring_time_zone_rules_digest,
                   recurring_schedule_digest, recurring_local_start_date, recurring_local_end_date,
                   recurring_local_wall_clock_seconds, recurring_weekday_mask, recurring_maximum_occurrences,
                   recurring_recording_duration_ticks, recurring_latest_start_grace_ticks, recurring_max_uses,
                   recurring_max_cumulative_duration_ticks, recurring_authorization_valid_until_utc,
                   recurring_target_type_code, recurring_audio_mode_code, recurring_backend_code,
                   recurring_countdown_seconds, recurring_output_directory, recurring_filename_prefix,
                   recurring_output_conflict_policy_code, recurring_wake_policy_code, recurring_desktop_requirement_code
            FROM setup_intents
            WHERE intent_id = 'recurring-intent-1';
            """));
        Assert.NotNull(service.Get(snapshot.IntentId, snapshot.CurrentUserSid, snapshot.SessionBinding));
    }

    [Theory]
    [MemberData(nameof(ForbiddenRecurringShapeTamperCases))]
    public void ForbiddenRecurringShapeFieldsFailClosedAfterCheckConstraintBypass(string category, string updateSql)
    {
        using var database = new TemporaryDatabase();
        var snapshot = CreateSnapshot();
        var service = new RecurringSetupIntentService(database.Store, () => At(1));
        Assert.Equal(RecurringSetupIntentResultStatus.Created, service.CreateOrGet(snapshot).Result);

        ExecuteWithCheckConstraintsIgnored(database.Store, updateSql);

        Assert.True(service.Get(snapshot.IntentId, snapshot.CurrentUserSid, snapshot.SessionBinding) is null, category);
    }

    public static IEnumerable<object[]> ForbiddenRecurringShapeTamperCases() => new[]
    {
        new object[] { "plan-association", "UPDATE setup_intents SET plan_id = 'plan-tampered' WHERE intent_id = 'recurring-intent-1';" },
        new object[] { "occurrence-association", "UPDATE setup_intents SET occurrence_id = 'occurrence-tampered' WHERE intent_id = 'recurring-intent-1';" },
        new object[] { "lease-association", "UPDATE setup_intents SET lease_id = 'lease-tampered' WHERE intent_id = 'recurring-intent-1';" },
        new object[] { "scope-association", "UPDATE setup_intents SET scope_id = 'scope-tampered' WHERE intent_id = 'recurring-intent-1';" },
        new object[] { "scheduled-window", "UPDATE setup_intents SET scheduled_start_utc = 1 WHERE intent_id = 'recurring-intent-1';" },
        new object[] { "latest-window", "UPDATE setup_intents SET latest_start_utc = 2 WHERE intent_id = 'recurring-intent-1';" },
        new object[] { "planned-window", "UPDATE setup_intents SET planned_end_utc = 3 WHERE intent_id = 'recurring-intent-1';" },
        new object[] { "maximum-duration", "UPDATE setup_intents SET maximum_duration_ms = 30000 WHERE intent_id = 'recurring-intent-1';" },
        new object[] { "legacy-output-directory", "UPDATE setup_intents SET output_directory = 'D:\\Legacy\\' WHERE intent_id = 'recurring-intent-1';" },
        new object[] { "frozen-filename", "UPDATE setup_intents SET frozen_file_name = 'capture.mp4' WHERE intent_id = 'recurring-intent-1';" },
    };

    [Theory]
    [MemberData(nameof(PersistedTamperCases))]
    public void EveryMajorPersistedTamperFailsClosed(string category, string updateSql, string readSid, string readSession)
    {
        using var database = new TemporaryDatabase();
        var snapshot = CreateSnapshot();
        var service = new RecurringSetupIntentService(database.Store, () => At(1));
        Assert.Equal(RecurringSetupIntentResultStatus.Created, service.CreateOrGet(snapshot).Result);

        var rejectedBySqlite = false;
        try
        {
            Execute(database.Store, updateSql);
        }
        catch (SqliteException)
        {
            rejectedBySqlite = true;
        }

        var closed = rejectedBySqlite || service.Get(snapshot.IntentId, readSid, readSession) is null;
        Assert.True(closed, category);
    }

    public static IEnumerable<object[]> PersistedTamperCases()
    {
        var scheduleDigest = "recurring-schedule/v1:" + new string('0', 64);
        var timeZoneDigest = "recurring-timezone-rules/v1:" + new string('0', 64);
        return new[]
        {
            new object[] { "intent-kind", "UPDATE setup_intents SET intent_kind_code = 'standing_once_fixed_region' WHERE intent_id = 'recurring-intent-1';", "S-1-5-21-test", "session-1" },
            new object[] { "status-pending", "UPDATE setup_intents SET status_code = 'lease_approval_pending' WHERE intent_id = 'recurring-intent-1';", "S-1-5-21-test", "session-1" },
            new object[] { "status-expired-version", "UPDATE setup_intents SET status_code = 'expired', terminal_reason_code = 'recurring_setup_intent_expired' WHERE intent_id = 'recurring-intent-1';", "S-1-5-21-test", "session-1" },
            new object[] { "status-rejected", "UPDATE setup_intents SET status_code = 'rejected', terminal_reason_code = 'tampered' WHERE intent_id = 'recurring-intent-1';", "S-1-5-21-test", "session-1" },
            new object[] { "terminal-reason", "UPDATE setup_intents SET terminal_reason_code = 'tampered' WHERE intent_id = 'recurring-intent-1';", "S-1-5-21-test", "session-1" },
            new object[] { "version", "UPDATE setup_intents SET version = 2 WHERE intent_id = 'recurring-intent-1';", "S-1-5-21-test", "session-1" },
            new object[] { "identity-sid", "UPDATE setup_intents SET current_user_sid = 'S-1-5-21-substituted' WHERE intent_id = 'recurring-intent-1';", "S-1-5-21-substituted", "session-1" },
            new object[] { "identity-session", "UPDATE setup_intents SET session_binding = 'session-substituted' WHERE intent_id = 'recurring-intent-1';", "S-1-5-21-test", "session-substituted" },
            new object[] { "identity-key", "UPDATE setup_intents SET idempotency_key = 'tampered-key' WHERE intent_id = 'recurring-intent-1';", "S-1-5-21-test", "session-1" },
            new object[] { "request-digest", "UPDATE setup_intents SET request_digest = '0000000000000000000000000000000000000000000000000000000000000000' WHERE intent_id = 'recurring-intent-1';", "S-1-5-21-test", "session-1" },
            new object[] { "schedule-kind", "UPDATE setup_intents SET recurring_schedule_kind_code = 'weekly' WHERE intent_id = 'recurring-intent-1';", "S-1-5-21-test", "session-1" },
            new object[] { "schedule-scalar", "UPDATE setup_intents SET recurring_local_wall_clock_seconds = 1 WHERE intent_id = 'recurring-intent-1';", "S-1-5-21-test", "session-1" },
            new object[] { "schedule-digest", $"UPDATE setup_intents SET recurring_schedule_digest = '{scheduleDigest}' WHERE intent_id = 'recurring-intent-1';", "S-1-5-21-test", "session-1" },
            new object[] { "timezone-digest", $"UPDATE setup_intents SET recurring_time_zone_rules_digest = '{timeZoneDigest}' WHERE intent_id = 'recurring-intent-1';", "S-1-5-21-test", "session-1" },
            new object[] { "quota-runs", "UPDATE setup_intents SET recurring_max_uses = 1 WHERE intent_id = 'recurring-intent-1';", "S-1-5-21-test", "session-1" },
            new object[] { "quota-duration", "UPDATE setup_intents SET recurring_max_cumulative_duration_ticks = 10000000 WHERE intent_id = 'recurring-intent-1';", "S-1-5-21-test", "session-1" },
            new object[] { "authorization-validity", "UPDATE setup_intents SET recurring_authorization_valid_until_utc = recurring_authorization_valid_until_utc + 1 WHERE intent_id = 'recurring-intent-1';", "S-1-5-21-test", "session-1" },
            new object[] { "lease-validity", "UPDATE setup_intents SET lease_valid_until_utc = lease_valid_until_utc + 1 WHERE intent_id = 'recurring-intent-1';", "S-1-5-21-test", "session-1" },
            new object[] { "target-policy", "UPDATE setup_intents SET recurring_target_type_code = 'window' WHERE intent_id = 'recurring-intent-1';", "S-1-5-21-test", "session-1" },
            new object[] { "audio-policy", "UPDATE setup_intents SET recurring_audio_mode_code = 'system' WHERE intent_id = 'recurring-intent-1';", "S-1-5-21-test", "session-1" },
            new object[] { "backend-policy", "UPDATE setup_intents SET recurring_backend_code = 'wgc' WHERE intent_id = 'recurring-intent-1';", "S-1-5-21-test", "session-1" },
            new object[] { "countdown-policy", "UPDATE setup_intents SET recurring_countdown_seconds = 1 WHERE intent_id = 'recurring-intent-1';", "S-1-5-21-test", "session-1" },
            new object[] { "wake-policy", "UPDATE setup_intents SET recurring_wake_policy_code = 'interactive' WHERE intent_id = 'recurring-intent-1';", "S-1-5-21-test", "session-1" },
            new object[] { "desktop-policy", "UPDATE setup_intents SET recurring_desktop_requirement_code = 'noninteractive' WHERE intent_id = 'recurring-intent-1';", "S-1-5-21-test", "session-1" },
            new object[] { "output-conflict-policy", "UPDATE setup_intents SET recurring_output_conflict_policy_code = 'overwrite' WHERE intent_id = 'recurring-intent-1';", "S-1-5-21-test", "session-1" },
            new object[] { "output-directory", "UPDATE setup_intents SET recurring_output_directory = 'D:\\Other\\' WHERE intent_id = 'recurring-intent-1';", "S-1-5-21-test", "session-1" },
            new object[] { "filename-prefix", "UPDATE setup_intents SET recurring_filename_prefix = 'other' WHERE intent_id = 'recurring-intent-1';", "S-1-5-21-test", "session-1" },
            new object[] { "plan-association", "UPDATE setup_intents SET plan_id = 'plan-tampered' WHERE intent_id = 'recurring-intent-1';", "S-1-5-21-test", "session-1" },
            new object[] { "occurrence-association", "UPDATE setup_intents SET occurrence_id = 'occ-tampered' WHERE intent_id = 'recurring-intent-1';", "S-1-5-21-test", "session-1" },
            new object[] { "lease-association", "UPDATE setup_intents SET lease_id = 'lease-tampered' WHERE intent_id = 'recurring-intent-1';", "S-1-5-21-test", "session-1" },
            new object[] { "scope-association", "UPDATE setup_intents SET scope_id = 'scope-tampered' WHERE intent_id = 'recurring-intent-1';", "S-1-5-21-test", "session-1" },
            new object[] { "one-shot-scheduled-window", "UPDATE setup_intents SET scheduled_start_utc = 1 WHERE intent_id = 'recurring-intent-1';", "S-1-5-21-test", "session-1" },
            new object[] { "one-shot-filename", "UPDATE setup_intents SET frozen_file_name = 'capture.mp4' WHERE intent_id = 'recurring-intent-1';", "S-1-5-21-test", "session-1" },
        };
    }

    [Fact]
    public void PopulatedV13StandingIntentUpgradesToV14WithoutChangingItsEvidence()
    {
        using var database = new TemporaryDatabase(initialize: false);
        CreateV13Fixture(database.Store);

        database.Store.Initialize();

        Assert.Equal(15L, Scalar(database.Store, "SELECT COUNT(*) FROM schema_migrations;"));
        Assert.Equal("standing_once_fixed_region", Text(database.Store, "SELECT intent_kind_code FROM setup_intents WHERE intent_id = 'v13-standing';"));
        Assert.Equal("v13-key", Text(database.Store, "SELECT idempotency_key FROM setup_intents WHERE intent_id = 'v13-standing';"));
        Assert.Equal("S-1-5-21-v13", Text(database.Store, "SELECT current_user_sid FROM setup_intents WHERE intent_id = 'v13-standing';"));
        Assert.Equal("v13-session", Text(database.Store, "SELECT session_binding FROM setup_intents WHERE intent_id = 'v13-standing';"));
        Assert.Equal("expired", Text(database.Store, "SELECT status_code FROM setup_intents WHERE intent_id = 'v13-standing';"));
        Assert.Equal("v13_fixture_expired", Text(database.Store, "SELECT terminal_reason_code FROM setup_intents WHERE intent_id = 'v13-standing';"));
        Assert.Equal(100L, Scalar(database.Store, "SELECT requested_at_utc FROM setup_intents WHERE intent_id = 'v13-standing';"));
        Assert.Equal(200L, Scalar(database.Store, "SELECT expires_at_utc FROM setup_intents WHERE intent_id = 'v13-standing';"));
        Assert.Equal("plan-v13", Text(database.Store, "SELECT plan_id FROM setup_intents WHERE intent_id = 'v13-standing';"));
        Assert.Equal("occ-v13", Text(database.Store, "SELECT occurrence_id FROM setup_intents WHERE intent_id = 'v13-standing';"));
        Assert.Equal("lease-v13", Text(database.Store, "SELECT lease_id FROM setup_intents WHERE intent_id = 'v13-standing';"));
        Assert.Equal("scope-v13", Text(database.Store, "SELECT scope_id FROM setup_intents WHERE intent_id = 'v13-standing';"));
        Assert.Equal(300L, Scalar(database.Store, "SELECT updated_at_utc FROM setup_intents WHERE intent_id = 'v13-standing';"));
        Assert.Equal(7L, Scalar(database.Store, "SELECT version FROM setup_intents WHERE intent_id = 'v13-standing';"));
        Assert.Equal(110L, Scalar(database.Store, "SELECT scheduled_start_utc FROM setup_intents WHERE intent_id = 'v13-standing';"));
        Assert.Equal(120L, Scalar(database.Store, "SELECT latest_start_utc FROM setup_intents WHERE intent_id = 'v13-standing';"));
        Assert.Equal(160L, Scalar(database.Store, "SELECT planned_end_utc FROM setup_intents WHERE intent_id = 'v13-standing';"));
        Assert.Equal(30000L, Scalar(database.Store, "SELECT maximum_duration_ms FROM setup_intents WHERE intent_id = 'v13-standing';"));
        Assert.Equal(400L, Scalar(database.Store, "SELECT lease_valid_until_utc FROM setup_intents WHERE intent_id = 'v13-standing';"));
        Assert.Equal("D:\\Recordings\\", Text(database.Store, "SELECT output_directory FROM setup_intents WHERE intent_id = 'v13-standing';"));
        Assert.Equal("capture.mp4", Text(database.Store, "SELECT frozen_file_name FROM setup_intents WHERE intent_id = 'v13-standing';"));
        Assert.Equal(1L, Scalar(database.Store, "SELECT COUNT(*) FROM setup_intents WHERE intent_id = 'v13-standing' AND recurring_schedule_kind_code IS NULL AND recurring_time_zone_id IS NULL AND recurring_time_zone_rules_digest IS NULL AND recurring_schedule_digest IS NULL AND recurring_local_start_date IS NULL AND recurring_local_end_date IS NULL AND recurring_local_wall_clock_seconds IS NULL AND recurring_weekday_mask IS NULL AND recurring_maximum_occurrences IS NULL AND recurring_recording_duration_ticks IS NULL AND recurring_latest_start_grace_ticks IS NULL AND recurring_max_uses IS NULL AND recurring_max_cumulative_duration_ticks IS NULL AND recurring_authorization_valid_until_utc IS NULL AND recurring_target_type_code IS NULL AND recurring_audio_mode_code IS NULL AND recurring_backend_code IS NULL AND recurring_countdown_seconds IS NULL AND recurring_output_directory IS NULL AND recurring_filename_prefix IS NULL AND recurring_output_conflict_policy_code IS NULL AND recurring_wake_policy_code IS NULL AND recurring_desktop_requirement_code IS NULL;"));

        using var connection = OpenRaw(database.Store.DatabasePath);
        using var duplicate = connection.CreateCommand();
        duplicate.CommandText = "INSERT INTO setup_intents (intent_id, intent_kind_code, idempotency_key, request_digest, current_user_sid, session_binding, status_code, requested_at_utc, expires_at_utc, created_at_utc, updated_at_utc, version) VALUES ('v13-standing-duplicate', 'standing_once_fixed_region', 'v13-key', 'aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa', 'S-1-5-21-v13', 'v13-session', 'region_selection_pending', 100, 400, 100, 100, 0);";
        Assert.Throws<SqliteException>(() => duplicate.ExecuteNonQuery());
    }

    private static void CreateV13Fixture(SqliteOperationalStore store)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(store.DatabasePath)!);
        using var connection = OpenRaw(store.DatabasePath);
        foreach (var migration in SqliteSchemaCatalog.Migrations.Take(13))
        {
            Execute(connection, migration.Definition);
            using var history = connection.CreateCommand();
            history.CommandText = "INSERT INTO schema_migrations(version, name, definition_checksum, applied_at_utc) VALUES ($version, $name, $checksum, $applied);";
            history.Parameters.AddWithValue("$version", migration.Version);
            history.Parameters.AddWithValue("$name", migration.Name);
            history.Parameters.AddWithValue("$checksum", migration.Checksum);
            history.Parameters.AddWithValue("$applied", 1000L + migration.Version);
            history.ExecuteNonQuery();
        }

        Execute(connection, "INSERT INTO setup_intents (intent_id, intent_kind_code, idempotency_key, request_digest, current_user_sid, session_binding, status_code, requested_at_utc, expires_at_utc, plan_id, occurrence_id, lease_id, scope_id, created_at_utc, updated_at_utc, version, terminal_reason_code, scheduled_start_utc, latest_start_utc, planned_end_utc, maximum_duration_ms, lease_valid_until_utc, output_directory, frozen_file_name) VALUES ('v13-standing', 'standing_once_fixed_region', 'v13-key', 'aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa', 'S-1-5-21-v13', 'v13-session', 'expired', 100, 200, 'plan-v13', 'occ-v13', 'lease-v13', 'scope-v13', 100, 300, 7, 'v13_fixture_expired', 110, 120, 160, 30000, 400, 'D:\\Recordings\\', 'capture.mp4');");
    }

    private static RecurringSetupIntentSnapshot CreateSnapshot(
        string intentId = "recurring-intent-1",
        string currentUserSid = "S-1-5-21-test",
        string sessionBinding = "session-1",
        DateTimeOffset? expiresAtUtc = null,
        DateTimeOffset? leaseValidUntilUtc = null,
        RecurringPlanSchedule? schedule = null)
    {
        var effectiveSchedule = schedule ?? RecurringPlanSchedule.CreateDaily(
            "China Standard Time", new DateOnly(2026, 1, 1), new DateOnly(2026, 1, 2),
            new TimeOnly(9, 30), 2, TimeSpan.FromSeconds(30), TimeSpan.FromMinutes(5));
        var expiry = expiresAtUtc ?? new DateTimeOffset(2026, 1, 3, 0, 0, 0, TimeSpan.Zero);
        return RecurringSetupIntentSnapshot.CreateForTrustedSetupAdapter(
            intentId, "recurring-key-1", currentUserSid, sessionBinding, At(0), expiry,
            effectiveSchedule, effectiveSchedule.MaximumOccurrences,
            TimeSpan.FromSeconds(effectiveSchedule.MaximumOccurrences * effectiveSchedule.RecordingDuration.TotalSeconds),
            leaseValidUntilUtc ?? expiry,
            "D:\\Recordings\\Agent", "daily-review");
    }

    private static RecurringPlanSchedule ShortSchedule() => RecurringPlanSchedule.CreateDaily(
        "China Standard Time", new DateOnly(2025, 12, 31), new DateOnly(2025, 12, 31),
        new TimeOnly(0, 0), 1, TimeSpan.FromSeconds(1), TimeSpan.Zero);

    private static string Body(string kind, string weekdays, string start, string end, int maximumOccurrences, int durationSeconds, int graceSeconds, string validUntil, int? maxTotalDurationSeconds = null)
    {
        var total = maxTotalDurationSeconds ?? maximumOccurrences * durationSeconds;
        return $$"""
        {
          "recording_spec": {
            "source": {"type":"fixed_region"},
            "audio": {"mode":"none"},
            "duration_seconds": {{durationSeconds}},
            "countdown_seconds": 0,
            "backend": "ffmpeg-region",
            "output": {"directory":"D:\\Recordings\\Agent", "filename_prefix":"daily-review"}
          },
          "schedule": {
            "kind":"{{kind}}",
            "time_zone_id":"China Standard Time",
            "local_start_date":"{{start}}",
            "local_end_date":"{{end}}",
            "local_time":"09:30:00",
            "weekdays":{{weekdays}},
            "maximum_occurrences":{{maximumOccurrences}},
            "latest_start_grace_seconds":{{graceSeconds}}
          },
          "requested_authorization": {
            "mode":"recurring_lease",
            "valid_until":"{{validUntil}}",
            "max_runs":{{maximumOccurrences}},
            "max_total_duration_seconds":{{total}}
          }
        }
        """;
    }

    private static void AssertInvalid(string body) => Assert.Throws<ApiException>(() => RecurringPlanApiRequestParser.Parse(body, "key-1"));
    private static RecurringSetupIntentSnapshot SnapshotFromRequest(RecurringPlanApiRequest request, string intentId) =>
        RecurringSetupIntentSnapshot.CreateForTrustedSetupAdapter(
            intentId, request.IdempotencyKey, "S-1-5-21-test", "session-1", At(0), request.LeaseValidUntilUtc,
            request.Schedule, request.MaxRuns, request.MaxTotalDuration, request.LeaseValidUntilUtc,
            request.OutputDirectory, request.FilenamePrefix);
    private static DateTimeOffset At(int seconds) => new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero).AddSeconds(seconds);
    private static void Execute(SqliteOperationalStore store, string sql) { using var connection = store.OpenConnection(); Execute(connection, sql); }
    private static void Execute(SqliteConnection connection, string sql) { using var command = connection.CreateCommand(); command.CommandText = sql; command.ExecuteNonQuery(); }
    private static void ExecuteWithCheckConstraintsIgnored(SqliteOperationalStore store, string sql)
    {
        using var connection = store.OpenConnection();
        Execute(connection, "PRAGMA ignore_check_constraints = ON;");
        try
        {
            Execute(connection, sql);
        }
        finally
        {
            Execute(connection, "PRAGMA ignore_check_constraints = OFF;");
            Assert.Equal(0L, Scalar(connection, "PRAGMA ignore_check_constraints;"));
        }
    }
    private static SqliteConnection OpenRaw(string databasePath)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Default,
            Pooling = false,
        }.ToString());
        connection.Open();
        return connection;
    }
    private static long Scalar(SqliteOperationalStore store, string sql) { using var connection = store.OpenConnection(); using var command = connection.CreateCommand(); command.CommandText = sql; return Convert.ToInt64(command.ExecuteScalar()); }
    private static long Scalar(SqliteConnection connection, string sql) { using var command = connection.CreateCommand(); command.CommandText = sql; return Convert.ToInt64(command.ExecuteScalar()); }
    private static string Text(SqliteOperationalStore store, string sql) { using var connection = store.OpenConnection(); using var command = connection.CreateCommand(); command.CommandText = sql; return Convert.ToString(command.ExecuteScalar())!; }
    private static object?[] Row(SqliteOperationalStore store) { using var connection = store.OpenConnection(); using var command = connection.CreateCommand(); command.CommandText = "SELECT intent_id, intent_kind_code, idempotency_key, request_digest, current_user_sid, session_binding, status_code, requested_at_utc, expires_at_utc, version FROM setup_intents ORDER BY intent_id;"; using var reader = command.ExecuteReader(); var values = new List<object?>(); while (reader.Read()) for (var index = 0; index < reader.FieldCount; index++) values.Add(reader.IsDBNull(index) ? null : reader.GetValue(index)); return values.ToArray(); }

    private sealed class TemporaryDatabase : IDisposable
    {
        private readonly string _directory = Path.Combine(Path.GetTempPath(), "AgentRecorderRecurringSetupIntent_" + Guid.NewGuid().ToString("N"));
        internal TemporaryDatabase(bool initialize = true) { Directory.CreateDirectory(_directory); Store = new SqliteOperationalStore(Path.Combine(_directory, "state", "agent-recorder.db")); if (initialize) Store.Initialize(); }
        internal SqliteOperationalStore Store { get; }
        public void Dispose() { try { Directory.Delete(_directory, true); } catch { } }
    }
}
