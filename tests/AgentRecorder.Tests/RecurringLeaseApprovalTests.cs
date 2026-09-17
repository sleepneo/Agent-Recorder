using System.Reflection;
using AgentRecorder.Core;
using AgentRecorder.Core.Automation;
using AgentRecorder.Infrastructure;
using AgentRecorder.Persistence;
using Microsoft.Data.Sqlite;
using Xunit;

namespace AgentRecorder.Tests;

[Collection("NonParallel-AgentRecorderDataDir")]
public sealed class RecurringLeaseApprovalTests
{
    [Fact]
    public void LatestAuthorizationBoundUsesTheLastEffectiveOrdinalWithoutMaterializingOccurrences()
    {
        var schedule = RecurringPlanSchedule.CreateDaily(
            TimeZoneInfo.Utc.Id,
            new DateOnly(2026, 9, 10),
            new DateOnly(2026, 9, 30),
            new TimeOnly(10, 0),
            maximumOccurrences: 3,
            recordingDuration: TimeSpan.FromMinutes(2),
            latestStartGrace: TimeSpan.FromSeconds(1));

        var plannedEnd = RecurringScheduleAuthorizationBounds.GetLatestValidPlannedEndUtc("plan", 1, schedule);
        Assert.Equal(new DateTimeOffset(2026, 9, 12, 10, 2, 1, TimeSpan.Zero), plannedEnd);
    }

    [Fact]
    public void LatestAuthorizationBoundUsesWeeklyAndLocalEndCardinalityCaps()
    {
        var weeklyMaximum = RecurringPlanSchedule.CreateWeekly(
            "UTC", new DateOnly(2026, 9, 1), new DateOnly(2026, 9, 30), new TimeOnly(10, 0),
            new[] { DayOfWeek.Tuesday, DayOfWeek.Thursday }, 2, TimeSpan.FromMinutes(1), TimeSpan.Zero);
        var weeklyEnd = RecurringPlanSchedule.CreateWeekly(
            "UTC", new DateOnly(2026, 9, 1), new DateOnly(2026, 9, 2), new TimeOnly(10, 0),
            new[] { DayOfWeek.Tuesday, DayOfWeek.Thursday }, 20, TimeSpan.FromMinutes(1), TimeSpan.Zero);

        Assert.Equal(new DateTimeOffset(2026, 9, 3, 10, 1, 0, TimeSpan.Zero),
            RecurringScheduleAuthorizationBounds.GetLatestValidPlannedEndUtc("weekly", 1, weeklyMaximum));
        Assert.Equal(new DateTimeOffset(2026, 9, 1, 10, 1, 0, TimeSpan.Zero),
            RecurringScheduleAuthorizationBounds.GetLatestValidPlannedEndUtc("weekly-end", 1, weeklyEnd));
    }

    [Fact]
    public void LatestAuthorizationBoundSkipsInvalidOrdinalAndUsesEarlierAmbiguousUtc()
    {
        var invalidTail = RecurringPlanSchedule.CreateDaily(
            "America/New_York", new DateOnly(2026, 3, 8), new DateOnly(2026, 3, 9),
            new TimeOnly(2, 30), 2, TimeSpan.FromMinutes(1), TimeSpan.Zero);
        var ambiguous = RecurringPlanSchedule.CreateDaily(
            "America/New_York", new DateOnly(2026, 11, 1), new DateOnly(2026, 11, 1),
            new TimeOnly(1, 30), 1, TimeSpan.FromMinutes(1), TimeSpan.Zero);

        Assert.Equal(new DateTimeOffset(2026, 3, 9, 6, 31, 0, TimeSpan.Zero),
            RecurringScheduleAuthorizationBounds.GetLatestValidPlannedEndUtc("dst", 1, invalidTail));
        Assert.Equal(new DateTimeOffset(2026, 11, 1, 5, 31, 0, TimeSpan.Zero),
            RecurringScheduleAuthorizationBounds.GetLatestValidPlannedEndUtc("overlap", 1, ambiguous));
    }

    [Fact]
    public void LatestAuthorizationBoundSkipsWeeklyTerminalInvalidSundayAndReturnsPriorWeek()
    {
        var schedule = RecurringPlanSchedule.CreateWeekly(
            "America/New_York", new DateOnly(2026, 3, 1), new DateOnly(2026, 3, 8),
            new TimeOnly(2, 30), new[] { DayOfWeek.Sunday }, 2,
            TimeSpan.FromMinutes(1), TimeSpan.Zero);

        Assert.Equal(
            TimeSpan.FromDays(7).Ticks,
            RecurringScheduleAuthorizationBounds.GetMaximumMatchingDateGapTicksForTest(schedule));
        Assert.Equal(
            new DateTimeOffset(2026, 3, 1, 7, 31, 0, TimeSpan.Zero),
            RecurringScheduleAuthorizationBounds.GetLatestValidPlannedEndUtc("weekly-invalid-terminal", 1, schedule));
    }

    [Fact]
    public void LatestAuthorizationBoundUsesTheMaximumGapForMultipleWeeklyDays()
    {
        var schedule = RecurringPlanSchedule.CreateWeekly(
            "America/New_York", new DateOnly(2026, 3, 4), new DateOnly(2026, 3, 8),
            new TimeOnly(2, 30), new[] { DayOfWeek.Wednesday, DayOfWeek.Sunday }, 2,
            TimeSpan.FromMinutes(1), TimeSpan.Zero);

        Assert.Equal(
            TimeSpan.FromDays(4).Ticks,
            RecurringScheduleAuthorizationBounds.GetMaximumMatchingDateGapTicksForTest(schedule));
        Assert.Equal(
            new DateTimeOffset(2026, 3, 4, 7, 31, 0, TimeSpan.Zero),
            RecurringScheduleAuthorizationBounds.GetLatestValidPlannedEndUtc("weekly-gap", 1, schedule));
    }

    [Fact]
    public void LatestAuthorizationBoundIncludesVolgogradStandardOffsetNearUtcMaximum()
    {
        const string timeZoneId = "Volgograd Standard Time";
        TimeZoneInfo timeZone;
        try
        {
            timeZone = TimeZoneInfo.FindSystemTimeZoneById(timeZoneId);
        }
        catch (TimeZoneNotFoundException exception)
        {
            throw new Xunit.Sdk.XunitException($"The Windows target time zone '{timeZoneId}' is required for this deterministic regression test: {exception.Message}");
        }
        catch (InvalidTimeZoneException exception)
        {
            throw new Xunit.Sdk.XunitException($"The Windows target time zone '{timeZoneId}' is invalid on this test host: {exception.Message}");
        }

        var localDateTime = new DateTime(2018, 12, 15, 0, 0, 0, DateTimeKind.Unspecified);
        var actualOffset = timeZone.GetUtcOffset(localDateTime);
        Assert.Equal(TimeSpan.FromHours(4), actualOffset);

        var targetUtcTicks = DateTimeOffset.MaxValue.UtcDateTime.Ticks;
        var localTicks = localDateTime.Ticks;
        var recordingDurationTicks = checked(targetUtcTicks - localTicks + actualOffset.Ticks);
        var schedule = RecurringPlanSchedule.CreateDaily(
            timeZoneId, new DateOnly(2018, 12, 15), new DateOnly(2018, 12, 15),
            new TimeOnly(0, 0), 1, TimeSpan.FromTicks(recordingDurationTicks), TimeSpan.Zero);

        Assert.Equal(
            DateTimeOffset.MaxValue,
            RecurringScheduleAuthorizationBounds.GetLatestValidPlannedEndUtc("volgograd-standard", 1, schedule));
    }

    [Fact]
    public void LatestAuthorizationBoundHandlesAnOverflowTailLongerThanThirtyTwoSlots()
    {
        var schedule = RecurringPlanSchedule.CreateDaily(
            "UTC", new DateOnly(9999, 10, 1), new DateOnly(9999, 12, 31), new TimeOnly(0, 0),
            92, TimeSpan.FromDays(60), TimeSpan.Zero);

        var plannedEnd = RecurringScheduleAuthorizationBounds.GetLatestValidPlannedEndUtc("overflow-tail", 1, schedule);

        Assert.Equal(new DateTimeOffset(9999, 12, 31, 0, 0, 0, TimeSpan.Zero), plannedEnd);
    }

    [Fact]
    public void LatestAuthorizationBoundReturnsStableNoValidAndUtcOverflowReasons()
    {
        var noValid = RecurringPlanSchedule.CreateDaily(
            "UTC", new DateOnly(9999, 12, 31), new DateOnly(9999, 12, 31), new TimeOnly(0, 0),
            1, TimeSpan.FromDays(1), TimeSpan.Zero);
        var durationOverflow = RecurringPlanSchedule.CreateDaily(
            "UTC", new DateOnly(2026, 1, 1), new DateOnly(2026, 1, 1), new TimeOnly(0, 0),
            1, TimeSpan.MaxValue, TimeSpan.FromSeconds(1));

        Assert.Equal(RecurringScheduleAuthorizationReasonCodes.NoValidOccurrence,
            Assert.Throws<Phase3DomainException>(() => RecurringScheduleAuthorizationBounds.GetLatestValidPlannedEndUtc("none", 1, noValid)).ReasonCode);
        Assert.Equal(RecurringScheduleAuthorizationReasonCodes.UtcOverflow,
            Assert.Throws<Phase3DomainException>(() => RecurringScheduleAuthorizationBounds.GetLatestValidPlannedEndUtc("overflow", 1, durationOverflow)).ReasonCode);
    }

    [Fact]
    public void LatestAuthorizationBoundUsesBoundedCandidateEvaluationForLongRanges()
    {
        var schedule = RecurringPlanSchedule.CreateDaily(
            "UTC", new DateOnly(1900, 1, 1), new DateOnly(9999, 12, 31), new TimeOnly(0, 0),
            int.MaxValue, TimeSpan.FromMinutes(1), TimeSpan.Zero);
        var evaluations = 0;

        var plannedEnd = RecurringScheduleAuthorizationBounds.GetLatestValidPlannedEndUtcWithEvaluator(
            "long-range", 1, schedule, () => evaluations++);

        Assert.Equal(new DateTimeOffset(9999, 12, 31, 0, 1, 0, TimeSpan.Zero), plannedEnd);
        Assert.InRange(evaluations, 1, 2);
    }

    [Fact]
    public void LocalApprovalReceiptHasNoPublicConstructionOrDeserializerSurface()
    {
        var type = typeof(RecurringLeaseLocalApprovalReceipt);
        Assert.Empty(type.GetConstructors(BindingFlags.Public | BindingFlags.Instance));
        Assert.DoesNotContain(type.GetMethods(BindingFlags.Public | BindingFlags.Static), method =>
            method.Name.Contains("Deserialize", StringComparison.OrdinalIgnoreCase) ||
            method.Name.Contains("From", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void ReceiptDigestIsDeterministicAndFieldSensitive()
    {
        var first = CreateDetachedReceipt("approval-digest", approvedAtUtc: new DateTimeOffset(2026, 9, 10, 10, 2, 0, TimeSpan.Zero));
        var same = CreateDetachedReceipt("approval-digest", approvedAtUtc: new DateTimeOffset(2026, 9, 10, 10, 2, 0, TimeSpan.Zero));
        var changed = CreateDetachedReceipt("approval-digest-other", approvedAtUtc: new DateTimeOffset(2026, 9, 10, 10, 2, 0, TimeSpan.Zero));

        Assert.Equal(first.ApprovalDigest, same.ApprovalDigest);
        Assert.NotEqual(first.ApprovalDigest, changed.ApprovalDigest);
    }

    [Fact]
    public void FutureValidLeaseCanActivateBeforeValidFromAndOnlyChangesApprovalAggregates()
    {
        using var fixture = RecurringLeaseFixture.Create(maxOccurrences: 1);
        EnableUnattended(fixture, "future-valid-enable");
        var lease = RecurringConsentLease.CreatePending(
            "future-valid", fixture.Setup.ConfigurationRef,
            fixture.CreatedAt.AddMinutes(1), fixture.CreatedAt.AddHours(2), fixture.CreatedAt.AddMinutes(2),
            TimeSpan.FromMinutes(2), 10, TimeSpan.FromMinutes(20), fixture.CreatedAt.AddHours(-1));
        new SqliteRecurringConsentLeaseRepository(fixture.Store).InsertPending(lease);
        var receipt = CreateReceipt(lease, "future-valid-approval", fixture.CreatedAt.AddSeconds(2));
        var before = CaptureActivationRows(fixture, lease);

        var result = new RecurringLeaseLocalApprovalActivationService(fixture.Store, () => fixture.CreatedAt.AddSeconds(30))
            .Activate(lease.LeaseId, receipt);

        Assert.Equal(RecurringLeaseLocalApprovalActivationStatus.Activated, result.Status);
        Assert.Equal(before.OccurrenceSlots, Count(fixture, "recurring_occurrence_slots"));
        Assert.Equal(before.Runs, Count(fixture, "recording_runs"));
        Assert.Equal(before.Uses, Count(fixture, "recurring_lease_uses"));
        Assert.Equal(before.Scopes, Count(fixture, "authorized_capture_scopes"));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(1)]
    public void AuthorizationLatestEndPlusOrMinusOneTickIsRejectedWithoutWrites(int tickDelta)
    {
        using var fixture = RecurringLeaseFixture.Create(maxOccurrences: 1);
        EnableUnattended(fixture, "bound-tick-" + tickDelta);
        var expected = RecurringScheduleAuthorizationBounds.GetLatestValidPlannedEndUtc(
            fixture.PlanId, fixture.Setup.ScheduleVersion.ScheduleRevision, fixture.Schedule);
        var lease = RecurringConsentLease.CreatePending(
            "bound-tick-" + tickDelta, fixture.Setup.ConfigurationRef,
            fixture.CreatedAt.AddHours(-1), fixture.CreatedAt.AddHours(2), expected.AddTicks(tickDelta),
            TimeSpan.FromMinutes(2), 10, TimeSpan.FromMinutes(20), fixture.CreatedAt.AddHours(-1));
        new SqliteRecurringConsentLeaseRepository(fixture.Store).InsertPending(lease);
        var receipt = CreateReceipt(lease, "bound-tick-approval-" + tickDelta, fixture.CreatedAt.AddMinutes(2));

        var result = new RecurringLeaseLocalApprovalActivationService(fixture.Store, () => fixture.CreatedAt.AddMinutes(3))
            .Activate(lease.LeaseId, receipt);

        Assert.Equal(RecurringLeaseLocalApprovalActivationStatus.Rejected, result.Status);
        Assert.Equal(PlanDefinitionStatus.Draft, new SqlitePlanDefinitionRepository(fixture.Store).Get(fixture.PlanId).Status);
        Assert.Equal(ConsentLeaseStatus.Pending, new SqliteRecurringConsentLeaseRepository(fixture.Store).Get(lease.LeaseId).Status);
        Assert.Null(new SqliteRecurringLeaseLocalApprovalEvidenceReader(fixture.Store).TryGetByLease(lease.LeaseId));
    }

    [Fact]
    public void MalformedReceiptShapeIsRejectedWithZeroWrites()
    {
        var malformedFactories = new Func<RecurringConsentLease, RecurringLeaseLocalApprovalReceipt>[]
        {
            lease => RecurringLeaseLocalApprovalReceipt.CreateForTest("bad-kind", lease.LeaseId, lease.PlanId, lease.ConfigurationRef.ConfigurationDigest, lease.AuthorizationDigest, "S-1-5-21", "session", new DateTimeOffset(2026, 9, 10, 10, 2, 0, TimeSpan.Zero), "wrong_kind"),
            lease => RecurringLeaseLocalApprovalReceipt.CreateForTest("bad-version", lease.LeaseId, lease.PlanId, lease.ConfigurationRef.ConfigurationDigest, lease.AuthorizationDigest, "S-1-5-21", "session", new DateTimeOffset(2026, 9, 10, 10, 2, 0, TimeSpan.Zero), approvalVersion: 2),
            lease => RecurringLeaseLocalApprovalReceipt.CreateForTest("bad-sid", lease.LeaseId, lease.PlanId, lease.ConfigurationRef.ConfigurationDigest, lease.AuthorizationDigest, " ", "session", new DateTimeOffset(2026, 9, 10, 10, 2, 0, TimeSpan.Zero)),
            lease => RecurringLeaseLocalApprovalReceipt.CreateForTest("bad-session", lease.LeaseId, lease.PlanId, lease.ConfigurationRef.ConfigurationDigest, lease.AuthorizationDigest, "S-1-5-21", " ", new DateTimeOffset(2026, 9, 10, 10, 2, 0, TimeSpan.Zero)),
            lease => RecurringLeaseLocalApprovalReceipt.CreateForTest("bad-time", lease.LeaseId, lease.PlanId, lease.ConfigurationRef.ConfigurationDigest, lease.AuthorizationDigest, "S-1-5-21", "session", new DateTimeOffset(2026, 9, 10, 10, 2, 0, TimeSpan.FromHours(1))),
        };

        foreach (var factory in malformedFactories)
        {
            using var fixture = RecurringLeaseFixture.Create(maxOccurrences: 1);
            EnableUnattended(fixture, "malformed-" + Guid.NewGuid().ToString("N"));
            var lease = fixture.CreateLease("malformed-" + Guid.NewGuid().ToString("N"), fixture.CreatedAt.AddHours(-1), fixture.CreatedAt.AddMinutes(-30), fixture.CreatedAt.AddHours(2));
            new SqliteRecurringConsentLeaseRepository(fixture.Store).InsertPending(lease);
            var result = new RecurringLeaseLocalApprovalActivationService(fixture.Store, () => fixture.CreatedAt.AddMinutes(3))
                .Activate(lease.LeaseId, factory(lease));

            Assert.Equal(RecurringLeaseLocalApprovalActivationStatus.Rejected, result.Status);
            Assert.Equal(PlanDefinitionStatus.Draft, new SqlitePlanDefinitionRepository(fixture.Store).Get(fixture.PlanId).Status);
            Assert.Equal(ConsentLeaseStatus.Pending, new SqliteRecurringConsentLeaseRepository(fixture.Store).Get(lease.LeaseId).Status);
            Assert.Equal(0L, Count(fixture, "recurring_lease_local_approvals"));
        }
    }

    [Fact]
    public void ExactReplayAfterLeaseExpiryIsAlreadyActiveAndDifferentReceiptIsStableConflict()
    {
        using var fixture = RecurringLeaseFixture.Create(maxOccurrences: 1);
        EnableUnattended(fixture, "late-replay-enable");
        var lease = fixture.CreateLease("late-replay", fixture.CreatedAt.AddHours(-1), fixture.CreatedAt.AddMinutes(-30), fixture.CreatedAt.AddMinutes(10));
        new SqliteRecurringConsentLeaseRepository(fixture.Store).InsertPending(lease);
        var receipt = CreateReceipt(lease, "late-replay-approval", fixture.CreatedAt.AddMinutes(2));
        var first = new RecurringLeaseLocalApprovalActivationService(fixture.Store, () => fixture.CreatedAt.AddMinutes(3)).Activate(lease.LeaseId, receipt);
        Assert.Equal(RecurringLeaseLocalApprovalActivationStatus.Activated, first.Status);
        var planBefore = new SqlitePlanDefinitionRepository(fixture.Store).Get(fixture.PlanId);
        var leaseBefore = new SqliteRecurringConsentLeaseRepository(fixture.Store).Get(lease.LeaseId);
        var evidenceCount = Count(fixture, "recurring_lease_local_approvals");

        var lateService = new RecurringLeaseLocalApprovalActivationService(fixture.Store, () => lease.ValidUntilUtc.AddHours(1));
        var replay = lateService.Activate(lease.LeaseId, receipt);
        var different = lateService.Activate(lease.LeaseId, CreateReceipt(lease, "late-replay-different", fixture.CreatedAt.AddMinutes(2)));

        Assert.Equal(RecurringLeaseLocalApprovalActivationStatus.AlreadyActive, replay.Status);
        Assert.Equal(RecurringLeaseLocalApprovalActivationStatus.Conflict, different.Status);
        Assert.Equal(planBefore.Version, new SqlitePlanDefinitionRepository(fixture.Store).Get(fixture.PlanId).Version);
        Assert.Equal(planBefore.UpdatedAtUtc, new SqlitePlanDefinitionRepository(fixture.Store).Get(fixture.PlanId).UpdatedAtUtc);
        Assert.Equal(leaseBefore.Version, new SqliteRecurringConsentLeaseRepository(fixture.Store).Get(lease.LeaseId).Version);
        Assert.Equal(leaseBefore.UpdatedAtUtc, new SqliteRecurringConsentLeaseRepository(fixture.Store).Get(lease.LeaseId).UpdatedAtUtc);
        Assert.Equal(evidenceCount, Count(fixture, "recurring_lease_local_approvals"));
    }

    [Fact]
    public void ReceiptIdentityConfigurationAuthorizationSidSessionAndTimeMismatchNeverWrites()
    {
        using var fixture = RecurringLeaseFixture.Create(maxOccurrences: 1);
        EnableUnattended(fixture, "identity-mismatch-enable");
        var lease = fixture.CreateLease("identity-mismatch", fixture.CreatedAt.AddHours(-1), fixture.CreatedAt.AddMinutes(-30), fixture.CreatedAt.AddHours(2));
        new SqliteRecurringConsentLeaseRepository(fixture.Store).InsertPending(lease);
        var receipt = CreateReceipt(lease, "identity-original", fixture.CreatedAt.AddMinutes(2));
        var service = new RecurringLeaseLocalApprovalActivationService(fixture.Store, () => fixture.CreatedAt.AddMinutes(3));
        Assert.Equal(RecurringLeaseLocalApprovalActivationStatus.Activated, service.Activate(lease.LeaseId, receipt).Status);
        var planBefore = new SqlitePlanDefinitionRepository(fixture.Store).Get(fixture.PlanId);
        var leaseBefore = new SqliteRecurringConsentLeaseRepository(fixture.Store).Get(lease.LeaseId);
        var variants = new[]
        {
            CreateReceipt(lease, "identity-other", fixture.CreatedAt.AddMinutes(2)),
            RecurringLeaseLocalApprovalReceipt.CreateForTrustedLocalApprovalAdapter("identity-original", lease.LeaseId, "other-plan", lease.ConfigurationRef.ConfigurationDigest, lease.AuthorizationDigest, "S-1-5-21", "session", fixture.CreatedAt.AddMinutes(2)),
            RecurringLeaseLocalApprovalReceipt.CreateForTrustedLocalApprovalAdapter("identity-original", lease.LeaseId, lease.PlanId, ChangeDigest(lease.ConfigurationRef.ConfigurationDigest), lease.AuthorizationDigest, "S-1-5-21", "session", fixture.CreatedAt.AddMinutes(2)),
            RecurringLeaseLocalApprovalReceipt.CreateForTrustedLocalApprovalAdapter("identity-original", lease.LeaseId, lease.PlanId, lease.ConfigurationRef.ConfigurationDigest, ChangeDigest(lease.AuthorizationDigest), "S-1-5-21", "session", fixture.CreatedAt.AddMinutes(2)),
            RecurringLeaseLocalApprovalReceipt.CreateForTrustedLocalApprovalAdapter("identity-original", lease.LeaseId, lease.PlanId, lease.ConfigurationRef.ConfigurationDigest, lease.AuthorizationDigest, "S-1-5-21-other", "session", fixture.CreatedAt.AddMinutes(2)),
            RecurringLeaseLocalApprovalReceipt.CreateForTrustedLocalApprovalAdapter("identity-original", lease.LeaseId, lease.PlanId, lease.ConfigurationRef.ConfigurationDigest, lease.AuthorizationDigest, "S-1-5-21", "session-other", fixture.CreatedAt.AddMinutes(2)),
            RecurringLeaseLocalApprovalReceipt.CreateForTrustedLocalApprovalAdapter("identity-original", lease.LeaseId, lease.PlanId, lease.ConfigurationRef.ConfigurationDigest, lease.AuthorizationDigest, "S-1-5-21", "session", fixture.CreatedAt.AddMinutes(3)),
        };

        foreach (var variant in variants)
        {
            var result = service.Activate(lease.LeaseId, variant);
            Assert.NotEqual(RecurringLeaseLocalApprovalActivationStatus.Activated, result.Status);
        }

        Assert.Equal(planBefore.Version, new SqlitePlanDefinitionRepository(fixture.Store).Get(fixture.PlanId).Version);
        Assert.Equal(leaseBefore.Version, new SqliteRecurringConsentLeaseRepository(fixture.Store).Get(lease.LeaseId).Version);
    }

    [Fact]
    public void DisabledSafetyAndStopAllBoundaryRejectWithoutActivationWrites()
    {
        using (var disabled = RecurringLeaseFixture.Create(maxOccurrences: 1))
        {
            var lease = disabled.CreateLease("safety-disabled", disabled.CreatedAt.AddHours(-1), disabled.CreatedAt.AddMinutes(-30), disabled.CreatedAt.AddHours(2));
            new SqliteRecurringConsentLeaseRepository(disabled.Store).InsertPending(lease);
            var result = new RecurringLeaseLocalApprovalActivationService(disabled.Store, () => disabled.CreatedAt.AddMinutes(3))
                .Activate(lease.LeaseId, CreateReceipt(lease, "safety-disabled-approval", disabled.CreatedAt.AddMinutes(2)));
            Assert.Equal(RecurringLeaseLocalApprovalActivationStatus.Rejected, result.Status);
            Assert.Equal(0L, Count(disabled, "recurring_lease_local_approvals"));
        }

        using var stopAll = RecurringLeaseFixture.Create(maxOccurrences: 1);
        EnableUnattended(stopAll, "stop-all-enable");
        new SqliteStandingLeaseSafetyControlTransaction(stopAll.Store)
            .StopAllAndRevokeAll("stop-all-before-lease", "task260R-test", stopAll.CreatedAt.AddMinutes(3));
        var leaseAfterStopAll = stopAll.CreateLease("stop-all-boundary", stopAll.CreatedAt, stopAll.CreatedAt.AddMinutes(-1), stopAll.CreatedAt.AddHours(2));
        new SqliteRecurringConsentLeaseRepository(stopAll.Store).InsertPending(leaseAfterStopAll);
        var resultAfterStopAll = new RecurringLeaseLocalApprovalActivationService(stopAll.Store, () => stopAll.CreatedAt.AddMinutes(4))
            .Activate(leaseAfterStopAll.LeaseId, CreateReceipt(leaseAfterStopAll, "stop-all-old-approval", stopAll.CreatedAt.AddMinutes(2)));
        Assert.Equal(RecurringLeaseLocalApprovalActivationStatus.Rejected, resultAfterStopAll.Status);
        Assert.Equal(0L, Count(stopAll, "recurring_lease_local_approvals"));
    }

    [Fact]
    public void PartialPersistedEvidenceAndScheduleProfileDurationMismatchFailClosed()
    {
        using (var partial = RecurringLeaseFixture.Create(maxOccurrences: 1))
        {
            EnableUnattended(partial, "partial-enable");
            var lease = partial.CreateLease("partial-state", partial.CreatedAt.AddHours(-1), partial.CreatedAt.AddMinutes(-30), partial.CreatedAt.AddHours(2));
            new SqliteRecurringConsentLeaseRepository(partial.Store).InsertPending(lease);
            var receipt = CreateReceipt(lease, "partial-approval", partial.CreatedAt.AddMinutes(2));
            partial.Execute("""
                INSERT INTO recurring_lease_local_approvals
                    (approval_id, lease_id, plan_id, configuration_digest, authorization_digest,
                     current_user_sid, session_binding, approved_at_utc, approval_kind_code,
                     approval_version, approval_digest)
                VALUES ($approval, $lease, $plan, $configuration, $authorization, $sid, $session,
                        $approved, $kind, $version, $digest);
                """,
                ("$approval", receipt.ApprovalId), ("$lease", receipt.LeaseId), ("$plan", receipt.PlanId),
                ("$configuration", receipt.ConfigurationDigest), ("$authorization", receipt.AuthorizationDigest),
                ("$sid", receipt.CurrentUserSid), ("$session", receipt.SessionBinding),
                ("$approved", receipt.ApprovedAtUtc.UtcDateTime.Ticks), ("$kind", receipt.ApprovalKind),
                ("$version", receipt.ApprovalVersion), ("$digest", receipt.ApprovalDigest));

            var result = new RecurringLeaseLocalApprovalActivationService(partial.Store, () => partial.CreatedAt.AddMinutes(3))
                .Activate(lease.LeaseId, receipt);
            Assert.Equal(RecurringLeaseLocalApprovalActivationStatus.Rejected, result.Status);
            Assert.Equal(PlanDefinitionStatus.Draft, new SqlitePlanDefinitionRepository(partial.Store).Get(partial.PlanId).Status);
            Assert.Equal(ConsentLeaseStatus.Pending, new SqliteRecurringConsentLeaseRepository(partial.Store).Get(lease.LeaseId).Status);
        }

        using var mismatch = RecurringLeaseFixture.Create(maxOccurrences: 1);
        EnableUnattended(mismatch, "duration-mismatch-enable");
        var mismatchedSchedule = RecurringPlanSchedule.CreateDaily(
            "UTC", new DateOnly(2026, 9, 10), new DateOnly(2026, 9, 10), new TimeOnly(10, 0), 1,
            TimeSpan.FromTicks(checked(mismatch.Setup.ExactProfile.Duration.Ticks + 1)), TimeSpan.Zero);
        var version = new SqliteRecurringScheduleVersionRepository(mismatch.Store)
            .CreateOrGet(mismatch.PlanId, 2, mismatchedSchedule, mismatch.CreatedAt.AddMinutes(1));
        var configuration = new RecurringPlanConfigurationRef(
            mismatch.PlanId, 2, version.ScheduleDigest, version.TimeZoneRulesDigest, mismatch.Setup.ProfileBinding.ProfileRef);
        var mismatchedLease = RecurringConsentLease.CreatePending(
            "duration-mismatch", configuration, mismatch.CreatedAt.AddHours(-1), mismatch.CreatedAt.AddHours(2),
            mismatch.CreatedAt.AddHours(1), TimeSpan.FromMinutes(2), 10, TimeSpan.FromMinutes(20), mismatch.CreatedAt.AddHours(-1));
        new SqliteRecurringConsentLeaseRepository(mismatch.Store).InsertPending(mismatchedLease);
        var mismatchResult = new RecurringLeaseLocalApprovalActivationService(mismatch.Store, () => mismatch.CreatedAt.AddMinutes(3))
            .Activate(mismatchedLease.LeaseId, CreateReceipt(mismatchedLease, "duration-mismatch-approval", mismatch.CreatedAt.AddMinutes(2)));
        Assert.Equal(RecurringLeaseLocalApprovalActivationStatus.Rejected, mismatchResult.Status);
        Assert.Equal(0L, Count(mismatch, "recurring_lease_local_approvals"));
        Assert.Equal(PlanDefinitionStatus.Draft, new SqlitePlanDefinitionRepository(mismatch.Store).Get(mismatch.PlanId).Status);
        Assert.Equal(ConsentLeaseStatus.Pending, new SqliteRecurringConsentLeaseRepository(mismatch.Store).Get(mismatchedLease.LeaseId).Status);
    }

    [Theory]
    [InlineData("approval_id", "tampered-approval")]
    [InlineData("current_user_sid", "tampered-sid")]
    [InlineData("session_binding", "tampered-session")]
    [InlineData("approved_at_utc", "unused")]
    [InlineData("approval_digest", "tampered-digest")]
    public void PersistedEvidenceFieldTamperingFailsClosed(string column, string value)
    {
        using var fixture = RecurringLeaseFixture.Create(maxOccurrences: 1);
        EnableUnattended(fixture, "evidence-tamper-enable");
        var lease = fixture.CreateLease("evidence-tamper-" + column, fixture.CreatedAt.AddHours(-1), fixture.CreatedAt.AddMinutes(-30), fixture.CreatedAt.AddHours(2));
        new SqliteRecurringConsentLeaseRepository(fixture.Store).InsertPending(lease);
        var receipt = CreateReceipt(lease, "evidence-tamper-approval-" + column, fixture.CreatedAt.AddMinutes(2));
        var service = new RecurringLeaseLocalApprovalActivationService(fixture.Store, () => fixture.CreatedAt.AddMinutes(3));
        Assert.Equal(RecurringLeaseLocalApprovalActivationStatus.Activated, service.Activate(lease.LeaseId, receipt).Status);
        fixture.Execute("DROP TRIGGER trg_recurring_lease_local_approvals_immutable_update;");
        object persistedValue = value;
        if (column == "approved_at_utc")
        {
            persistedValue = fixture.CreatedAt.AddMinutes(4).UtcDateTime.Ticks;
        }
        else if (column == "approval_digest")
        {
            persistedValue = RecurringLeaseLocalApprovalDigest.Prefix + new string('c', 64);
        }
        fixture.Execute($"UPDATE recurring_lease_local_approvals SET {column} = $value WHERE lease_id = $lease;", ("$value", persistedValue), ("$lease", lease.LeaseId));

        var readerException = Assert.Throws<Phase3PersistenceException>(() => new SqliteRecurringLeaseLocalApprovalEvidenceReader(fixture.Store).TryGetByLease(lease.LeaseId));
        Assert.Equal(RecurringLeaseLocalApprovalPersistenceReasonCodes.PersistedDataInvalid, readerException.Code);
        var result = service.Activate(lease.LeaseId, receipt);
        Assert.Equal(RecurringLeaseLocalApprovalActivationStatus.Rejected, result.Status);
    }

    [Fact]
    public void V12ActivePlanIndexAndSemanticDriftAreEnforcedByInitialization()
    {
        using (var index = RecurringLeaseFixture.Create(maxOccurrences: 1))
        {
            var repository = new SqliteRecurringConsentLeaseRepository(index.Store);
            var first = index.CreateLease("active-index-first", index.CreatedAt.AddHours(-1), index.CreatedAt.AddMinutes(-30), index.CreatedAt.AddHours(2));
            var second = index.CreateLease("active-index-second", index.CreatedAt.AddHours(-1), index.CreatedAt.AddMinutes(-30), index.CreatedAt.AddHours(2));
            repository.InsertPending(first);
            repository.InsertPending(second);
            index.Execute("DROP TRIGGER trg_recurring_consent_leases_lifecycle_update;");
            index.Execute("UPDATE recurring_consent_leases SET status_code = 'active', version = 1, updated_at_utc = $updated WHERE lease_id = $lease;", ("$updated", index.CreatedAt.AddMinutes(3).UtcDateTime.Ticks), ("$lease", first.LeaseId));
            Assert.Throws<SqliteException>(() => index.Execute("UPDATE recurring_consent_leases SET status_code = 'active', version = 1, updated_at_utc = $updated WHERE lease_id = $lease;", ("$updated", index.CreatedAt.AddMinutes(3).UtcDateTime.Ticks), ("$lease", second.LeaseId)));
        }

        using (var predicate = RecurringLeaseFixture.Create(maxOccurrences: 1))
        {
            predicate.Execute("DROP INDEX ux_recurring_consent_leases_one_active_per_plan; CREATE UNIQUE INDEX ux_recurring_consent_leases_one_active_per_plan ON recurring_consent_leases(plan_id) WHERE status_code = 'pending';");
            Assert.Equal("sqlite_corrupt", Assert.Throws<SqliteOperationalStoreException>(() => new SqliteOperationalStore(predicate.Store.DatabasePath).Initialize()).Code);
        }

        using var trigger = RecurringLeaseFixture.Create(maxOccurrences: 1);
        trigger.ReplaceTriggerDefinition(
            "trg_recurring_lease_local_approvals_immutable_update",
            "CREATE TRIGGER trg_recurring_lease_local_approvals_immutable_update BEFORE UPDATE ON recurring_lease_local_approvals BEGIN SELECT 1; END;");
        Assert.Equal("sqlite_corrupt", Assert.Throws<SqliteOperationalStoreException>(() => new SqliteOperationalStore(trigger.Store.DatabasePath).Initialize()).Code);
    }

    [Fact]
    public void ApprovalActivationIsAtomicAndExactReplayIsReadOnly()
    {
        using var fixture = RecurringLeaseFixture.Create(maxOccurrences: 1);
        var safety = new SqliteStandingLeaseSafetyControlTransaction(fixture.Store);
        safety.SetUnattendedMode("task260-enable", true, "task260-test", fixture.CreatedAt.AddMinutes(1));

        var lease = fixture.CreateLease("task260-lease", fixture.CreatedAt.AddHours(-1), fixture.CreatedAt.AddMinutes(-30), fixture.CreatedAt.AddHours(2));
        new SqliteRecurringConsentLeaseRepository(fixture.Store).InsertPending(lease);
        var receipt = RecurringLeaseLocalApprovalReceipt.CreateForTrustedLocalApprovalAdapter(
            "approval-1", lease.LeaseId, lease.PlanId, lease.ConfigurationRef.ConfigurationDigest,
            lease.AuthorizationDigest, "S-1-5-21", "session-1", fixture.CreatedAt.AddMinutes(2));

        var service = new RecurringLeaseLocalApprovalActivationService(
            fixture.Store,
            () => fixture.CreatedAt.AddMinutes(3));
        var activated = service.Activate(lease.LeaseId, receipt);
        Assert.Equal(RecurringLeaseLocalApprovalActivationStatus.Activated, activated.Status);

        var planBeforeReplay = new SqlitePlanDefinitionRepository(fixture.Store).Get(fixture.PlanId);
        var leaseBeforeReplay = new SqliteRecurringConsentLeaseRepository(fixture.Store).Get(lease.LeaseId);
        var replay = service.Activate(lease.LeaseId, receipt);
        Assert.Equal(RecurringLeaseLocalApprovalActivationStatus.AlreadyActive, replay.Status);
        Assert.Equal(planBeforeReplay.Version, new SqlitePlanDefinitionRepository(fixture.Store).Get(fixture.PlanId).Version);
        Assert.Equal(leaseBeforeReplay.Version, new SqliteRecurringConsentLeaseRepository(fixture.Store).Get(lease.LeaseId).Version);

        var evidence = new SqliteRecurringLeaseLocalApprovalEvidenceReader(fixture.Store).TryGetByLease(lease.LeaseId);
        Assert.NotNull(evidence);
        Assert.Equal(receipt.ApprovalDigest, evidence!.ApprovalDigest);
    }

    [Fact]
    public void FailureBeforeCommitLeavesPendingAggregatesAndNoEvidence()
    {
        using var fixture = RecurringLeaseFixture.Create(maxOccurrences: 1);
        new SqliteStandingLeaseSafetyControlTransaction(fixture.Store)
            .SetUnattendedMode("task260-enable-rollback", true, "task260-test", fixture.CreatedAt.AddMinutes(1));
        var lease = fixture.CreateLease("task260-rollback", fixture.CreatedAt.AddHours(-1), fixture.CreatedAt.AddMinutes(-30), fixture.CreatedAt.AddHours(2));
        new SqliteRecurringConsentLeaseRepository(fixture.Store).InsertPending(lease);
        var receipt = RecurringLeaseLocalApprovalReceipt.CreateForTrustedLocalApprovalAdapter(
            "approval-rollback", lease.LeaseId, lease.PlanId, lease.ConfigurationRef.ConfigurationDigest,
            lease.AuthorizationDigest, "S-1-5-21", "session-rollback", fixture.CreatedAt.AddMinutes(2));

        var service = new RecurringLeaseLocalApprovalActivationService(
            fixture.Store,
            () => fixture.CreatedAt.AddMinutes(3),
            beforeCommitForTest: (_, _) => throw new InvalidOperationException("injected"));
        var result = service.Activate(lease.LeaseId, receipt);
        Assert.Equal(RecurringLeaseLocalApprovalActivationStatus.Rejected, result.Status);
        Assert.Equal(ConsentLeaseStatus.Pending, new SqliteRecurringConsentLeaseRepository(fixture.Store).Get(lease.LeaseId).Status);
        Assert.Equal(PlanDefinitionStatus.Draft, new SqlitePlanDefinitionRepository(fixture.Store).Get(fixture.PlanId).Status);
        Assert.Null(new SqliteRecurringLeaseLocalApprovalEvidenceReader(fixture.Store).TryGetByLease(lease.LeaseId));
    }

    [Fact]
    public void EveryActivationFailurePointRollsBackAllThreeWrites()
    {
        foreach (var point in Enum.GetValues<RecurringLeaseLocalApprovalActivationFailurePoint>())
        {
            using var fixture = RecurringLeaseFixture.Create(maxOccurrences: 1);
            new SqliteStandingLeaseSafetyControlTransaction(fixture.Store)
                .SetUnattendedMode("task260-enable-" + point, true, "task260-test", fixture.CreatedAt.AddMinutes(1));
            var lease = fixture.CreateLease("task260-failure-" + point, fixture.CreatedAt.AddHours(-1), fixture.CreatedAt.AddMinutes(-30), fixture.CreatedAt.AddHours(2));
            new SqliteRecurringConsentLeaseRepository(fixture.Store).InsertPending(lease);
            var receipt = RecurringLeaseLocalApprovalReceipt.CreateForTrustedLocalApprovalAdapter(
                "approval-" + point, lease.LeaseId, lease.PlanId, lease.ConfigurationRef.ConfigurationDigest,
                lease.AuthorizationDigest, "S-1-5-21", "session-" + point, fixture.CreatedAt.AddMinutes(2));
            var service = new RecurringLeaseLocalApprovalActivationService(
                fixture.Store,
                () => fixture.CreatedAt.AddMinutes(3),
                failureHookForTest: failurePoint =>
                {
                    if (failurePoint == point)
                    {
                        throw new InvalidOperationException("injected activation failure");
                    }
                });

            var result = service.Activate(lease.LeaseId, receipt);
            Assert.Equal(RecurringLeaseLocalApprovalActivationStatus.Rejected, result.Status);
            Assert.Equal(PlanDefinitionStatus.Draft, new SqlitePlanDefinitionRepository(fixture.Store).Get(fixture.PlanId).Status);
            Assert.Equal(ConsentLeaseStatus.Pending, new SqliteRecurringConsentLeaseRepository(fixture.Store).Get(lease.LeaseId).Status);
            Assert.Null(new SqliteRecurringLeaseLocalApprovalEvidenceReader(fixture.Store).TryGetByLease(lease.LeaseId));
        }
    }

    [Fact]
    public async Task ConcurrentIdenticalApprovalConvergesAndDifferentReceiptConflicts()
    {
        using var fixture = RecurringLeaseFixture.Create(maxOccurrences: 1);
        new SqliteStandingLeaseSafetyControlTransaction(fixture.Store)
            .SetUnattendedMode("task260-enable-concurrent", true, "task260-test", fixture.CreatedAt.AddMinutes(1));
        var lease = fixture.CreateLease("task260-concurrent", fixture.CreatedAt.AddHours(-1), fixture.CreatedAt.AddMinutes(-30), fixture.CreatedAt.AddHours(2));
        new SqliteRecurringConsentLeaseRepository(fixture.Store).InsertPending(lease);
        var receipt = RecurringLeaseLocalApprovalReceipt.CreateForTrustedLocalApprovalAdapter(
            "approval-concurrent", lease.LeaseId, lease.PlanId, lease.ConfigurationRef.ConfigurationDigest,
            lease.AuthorizationDigest, "S-1-5-21", "session-concurrent", fixture.CreatedAt.AddMinutes(2));

        var identical = await Task.WhenAll(
            Task.Run(() => new RecurringLeaseLocalApprovalActivationService(new SqliteOperationalStore(fixture.Store.DatabasePath), () => fixture.CreatedAt.AddMinutes(3)).Activate(lease.LeaseId, receipt)),
            Task.Run(() => new RecurringLeaseLocalApprovalActivationService(new SqliteOperationalStore(fixture.Store.DatabasePath), () => fixture.CreatedAt.AddMinutes(3)).Activate(lease.LeaseId, receipt)));
        Assert.Equal(1, identical.Count(result => result.Status == RecurringLeaseLocalApprovalActivationStatus.Activated));
        Assert.Equal(1, identical.Count(result => result.Status == RecurringLeaseLocalApprovalActivationStatus.AlreadyActive));

        var differentReceipt = RecurringLeaseLocalApprovalReceipt.CreateForTrustedLocalApprovalAdapter(
            "approval-different", lease.LeaseId, lease.PlanId, lease.ConfigurationRef.ConfigurationDigest,
            lease.AuthorizationDigest, "S-1-5-21-other", "session-other", fixture.CreatedAt.AddMinutes(2));
        var conflict = new RecurringLeaseLocalApprovalActivationService(
            new SqliteOperationalStore(fixture.Store.DatabasePath),
            () => fixture.CreatedAt.AddMinutes(3)).Activate(lease.LeaseId, differentReceipt);
        Assert.Equal(RecurringLeaseLocalApprovalActivationStatus.Conflict, conflict.Status);
    }

    private static void EnableUnattended(RecurringLeaseFixture fixture, string operationId) =>
        new SqliteStandingLeaseSafetyControlTransaction(fixture.Store)
            .SetUnattendedMode(operationId, true, "task260R-test", fixture.CreatedAt.AddMinutes(1));

    private static RecurringLeaseLocalApprovalReceipt CreateReceipt(
        RecurringConsentLease lease,
        string approvalId,
        DateTimeOffset approvedAtUtc) =>
        RecurringLeaseLocalApprovalReceipt.CreateForTrustedLocalApprovalAdapter(
            approvalId,
            lease.LeaseId,
            lease.PlanId,
            lease.ConfigurationRef.ConfigurationDigest,
            lease.AuthorizationDigest,
            "S-1-5-21",
            "session-260R",
            approvedAtUtc);

    private static RecurringLeaseLocalApprovalReceipt CreateDetachedReceipt(string approvalId, DateTimeOffset approvedAtUtc) =>
        RecurringLeaseLocalApprovalReceipt.CreateForTrustedLocalApprovalAdapter(
            approvalId,
            "detached-lease",
            "detached-plan",
            RecurringPlanConfigurationRef.DigestPrefix + new string('a', 64),
            RecurringConsentLeaseAuthorizationRef.DigestPrefix + new string('b', 64),
            "S-1-5-21",
            "detached-session",
            approvedAtUtc);

    private static string ChangeDigest(string digest)
    {
        var last = digest[^1];
        return digest[..^1] + (last == '0' ? '1' : '0');
    }

    private static long Count(RecurringLeaseFixture fixture, string table) =>
        Convert.ToInt64(fixture.Scalar($"SELECT COUNT(*) FROM {table};"));

    private static ActivationRows CaptureActivationRows(RecurringLeaseFixture fixture, RecurringConsentLease lease) => new(
        new SqlitePlanDefinitionRepository(fixture.Store).Get(fixture.PlanId).Version,
        new SqliteRecurringConsentLeaseRepository(fixture.Store).Get(lease.LeaseId).Version,
        Count(fixture, "recurring_occurrence_slots"),
        Count(fixture, "recording_runs"),
        Count(fixture, "recurring_lease_uses"),
        Count(fixture, "authorized_capture_scopes"));

    private readonly record struct ActivationRows(
        long PlanVersion,
        long LeaseVersion,
        long OccurrenceSlots,
        long Runs,
        long Uses,
        long Scopes);
}
