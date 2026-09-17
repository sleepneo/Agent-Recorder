using AgentRecorder.Core.Automation;
using AgentRecorder.Core;
using AgentRecorder.Persistence;
using Xunit;

namespace AgentRecorder.Tests;

[Collection("NonParallel-AgentRecorderDataDir")]
public sealed class RecurringLeaseSafetyControlTests
{
    [Fact]
    public void PendingRevokeUsesExactRecurringLedgerIdentityAndReplayIsReadOnly()
    {
        using var fixture = RecurringLeaseFixture.Create();
        var lease = fixture.CreateLease("task271-pending", fixture.CreatedAt.AddHours(-1), fixture.CreatedAt.AddMinutes(-30), fixture.CreatedAt.AddHours(2));
        new SqliteRecurringConsentLeaseRepository(fixture.Store).InsertPending(lease);
        var now = fixture.CreatedAt.AddMinutes(3);
        var service = CreateService(fixture, now);

        var changed = service.RevokeRecurringLease(lease.LeaseId, "task271-revoke-pending", "task271-test");

        Assert.Equal(StandingLeaseSafetyControlResultStatus.Changed, changed.Status);
        Assert.Equal(StandingLeaseSafetyReasonCodes.LeaseRevoked, changed.Reason);
        Assert.True(changed.DurableOperationCommitted);
        Assert.True(changed.DurableStateChanged);
        var revoked = new SqliteRecurringConsentLeaseRepository(fixture.Store).Get(lease.LeaseId);
        Assert.Equal(ConsentLeaseStatus.Revoked, revoked.Status);
        Assert.Equal(1, revoked.Version);
        Assert.Equal(now, revoked.UpdatedAtUtc);
        Assert.Equal(0L, Count(fixture, "recurring_lease_uses"));
        Assert.Equal(1L, Count(fixture, "standing_lease_safety_operations"));
        Assert.Equal(lease.LeaseId, fixture.Scalar(
            "SELECT lease_id FROM standing_lease_safety_operations WHERE operation_id = $operation;",
            ("$operation", "task271-revoke-pending")));
        var intentValue = fixture.Scalar(
            "SELECT intent_id FROM standing_lease_safety_operations WHERE operation_id = $operation;",
            ("$operation", "task271-revoke-pending"));
        Assert.True(intentValue is null or DBNull);

        var replay = service.RevokeRecurringLease(lease.LeaseId, "task271-revoke-pending", "task271-test");
        Assert.Equal(StandingLeaseSafetyControlResultStatus.AlreadyApplied, replay.Status);
        Assert.False(replay.Changed);
        Assert.True(replay.DurableOperationCommitted);
        Assert.True(replay.DurableStateChanged);
        Assert.False(replay.PhysicalStopFailed);
        Assert.False(replay.PhysicalStopRetryRecommended);
        Assert.Equal(1, new SqliteRecurringConsentLeaseRepository(fixture.Store).Get(lease.LeaseId).Version);
        Assert.Equal(1L, Count(fixture, "standing_lease_safety_operations"));
    }

    [Fact]
    public void ActiveRevokeStopsOnlyWhenExactRecurringLeaseHasNonTerminalRun()
    {
        using var fixture = RecurringLeaseFixture.Create(maxOccurrences: 1);
        EnableUnattended(fixture, "task271-enable-active");
        var lease = fixture.CreateLease("task271-active", fixture.CreatedAt.AddHours(-1), fixture.CreatedAt.AddMinutes(-30), fixture.CreatedAt.AddHours(2));
        new SqliteRecurringConsentLeaseRepository(fixture.Store).InsertPending(lease);
        Activate(fixture, lease, "task271-approval-active");
        var slot = fixture.CreateSlots(1).Single();
        fixture.InsertAccountingRow(
            "task271-active-use",
            lease,
            slot,
            "run-0",
            "reserved",
            fixture.CreatedAt.AddMinutes(4),
            1,
            lease.PerRunDuration,
            null);

        var stopper = new CountingStopper();
        var service = CreateService(fixture, fixture.CreatedAt.AddMinutes(5), stopper: stopper);
        var result = service.RevokeRecurringLease(lease.LeaseId, "task271-revoke-active", "task271-test");

        Assert.Equal(StandingLeaseSafetyControlResultStatus.Changed, result.Status);
        Assert.True(result.RequiresActiveRunStop);
        Assert.Equal(1, stopper.Count);
        Assert.Equal(ConsentLeaseStatus.Revoked, new SqliteRecurringConsentLeaseRepository(fixture.Store).Get(lease.LeaseId).Status);
        Assert.Equal(1L, Count(fixture, "recurring_lease_uses"));
        Assert.Equal("created", fixture.Scalar("SELECT status_code FROM recording_runs WHERE id = 'run-0';"));
    }

    [Fact]
    public void ARunBelongingToAnotherRecurringLeaseDoesNotRequireStopper()
    {
        using var fixture = RecurringLeaseFixture.Create(maxOccurrences: 2);
        var first = fixture.CreateLease("task271-exact-a", fixture.CreatedAt.AddHours(-1), fixture.CreatedAt.AddMinutes(-30), fixture.CreatedAt.AddHours(2));
        var second = fixture.CreateLease("task271-exact-b", fixture.CreatedAt.AddHours(-1), fixture.CreatedAt.AddMinutes(-30), fixture.CreatedAt.AddHours(2));
        var repository = new SqliteRecurringConsentLeaseRepository(fixture.Store);
        repository.InsertPending(first);
        repository.InsertPending(second);
        var slots = fixture.CreateSlots(2);
        fixture.InsertAccountingRow(
            "task271-other-use",
            second,
            slots[0],
            "run-0",
            "reserved",
            fixture.CreatedAt.AddMinutes(4),
            1,
            second.PerRunDuration,
            null);

        var stopper = new CountingStopper();
        var result = CreateService(fixture, fixture.CreatedAt.AddMinutes(5), stopper: stopper)
            .RevokeRecurringLease(first.LeaseId, "task271-revoke-exact-a", "task271-test");

        Assert.Equal(StandingLeaseSafetyControlResultStatus.Changed, result.Status);
        Assert.False(result.RequiresActiveRunStop);
        Assert.Equal(0, stopper.Count);
        Assert.Equal(ConsentLeaseStatus.Revoked, repository.Get(first.LeaseId).Status);
        Assert.Equal(ConsentLeaseStatus.Pending, repository.Get(second.LeaseId).Status);
    }

    [Fact]
    public void TerminalRecurringStatesAreStableAndExhaustedWithoutActiveRunIsNotRevocable()
    {
        using var fixture = RecurringLeaseFixture.Create(maxOccurrences: 3);
        var repository = new SqliteRecurringConsentLeaseRepository(fixture.Store);
        var expired = fixture.CreateLease("task271-expired", fixture.CreatedAt.AddHours(-1), fixture.CreatedAt.AddMinutes(-30), fixture.CreatedAt.AddHours(1));
        var rejected = fixture.CreateLease("task271-rejected", fixture.CreatedAt.AddHours(-1), fixture.CreatedAt.AddMinutes(-30), fixture.CreatedAt.AddHours(1));
        var exhausted = fixture.CreateLease("task271-exhausted", fixture.CreatedAt.AddHours(-1), fixture.CreatedAt.AddMinutes(-30), fixture.CreatedAt.AddHours(1));
        repository.InsertPending(expired);
        repository.InsertPending(rejected);
        repository.InsertPending(exhausted);
        ForceRecurringStatus(fixture, expired, "expired", fixture.CreatedAt.AddHours(1), 1);
        ForceRecurringStatus(fixture, rejected, "rejected", fixture.CreatedAt.AddMinutes(1), 1);
        ForceRecurringStatus(fixture, exhausted, "exhausted", fixture.CreatedAt.AddMinutes(2), 1);
        var service = CreateService(fixture, fixture.CreatedAt.AddHours(2));

        Assert.Equal("lease_expired", service.RevokeRecurringLease(expired.LeaseId, "task271-terminal-expired", "task271-test").Reason);
        Assert.Equal("lease_rejected", service.RevokeRecurringLease(rejected.LeaseId, "task271-terminal-rejected", "task271-test").Reason);
        Assert.Equal(StandingLeaseSafetyReasonCodes.LeaseExhausted, service.RevokeRecurringLease(exhausted.LeaseId, "task271-terminal-exhausted", "task271-test").Reason);
        Assert.Equal(ConsentLeaseStatus.Expired, repository.Get(expired.LeaseId).Status);
        Assert.Equal(ConsentLeaseStatus.Rejected, repository.Get(rejected.LeaseId).Status);
        Assert.Equal(ConsentLeaseStatus.Exhausted, repository.Get(exhausted.LeaseId).Status);
    }

    [Fact]
    public void ExhaustedRecurringLeaseWithExactNonTerminalRunIsRevokedOnceAndRequiresStop()
    {
        using var fixture = RecurringLeaseFixture.Create(maxOccurrences: 2);
        var target = fixture.CreateLease(
            "task271-exhausted-active",
            fixture.CreatedAt.AddHours(-1),
            fixture.CreatedAt.AddMinutes(-30),
            fixture.CreatedAt.AddHours(2));
        var other = fixture.CreateLease(
            "task271-exhausted-other",
            fixture.CreatedAt.AddHours(-1),
            fixture.CreatedAt.AddMinutes(-30),
            fixture.CreatedAt.AddHours(2));
        var repository = new SqliteRecurringConsentLeaseRepository(fixture.Store);
        repository.InsertPending(target);
        repository.InsertPending(other);
        var slots = fixture.CreateSlots(2);
        fixture.InsertAccountingRow(
            "task271-exhausted-active-use",
            target,
            slots[0],
            "run-0",
            "reserved",
            fixture.CreatedAt.AddMinutes(4),
            1,
            target.PerRunDuration,
            null);
        fixture.InsertAccountingRow(
            "task271-exhausted-other-use",
            other,
            slots[1],
            "run-1",
            "reserved",
            fixture.CreatedAt.AddMinutes(4),
            1,
            other.PerRunDuration,
            null);
        ForceRecurringStatus(fixture, target, "exhausted", fixture.CreatedAt.AddMinutes(5), 1);
        ForceRecurringStatus(fixture, other, "exhausted", fixture.CreatedAt.AddMinutes(5), 1);

        var stopper = new CountingStopper();
        var result = CreateService(fixture, fixture.CreatedAt.AddMinutes(6), stopper: stopper)
            .RevokeRecurringLease(target.LeaseId, "task271-exhausted-active-revoke", "task271-test");

        Assert.Equal(StandingLeaseSafetyControlResultStatus.Changed, result.Status);
        Assert.True(result.RequiresActiveRunStop);
        Assert.True(result.DurableOperationCommitted);
        Assert.True(result.DurableStateChanged);
        Assert.Equal(1, stopper.Count);
        Assert.Equal(ConsentLeaseStatus.Revoked, repository.Get(target.LeaseId).Status);
        Assert.Equal(2, repository.Get(target.LeaseId).Version);
        Assert.Equal(ConsentLeaseStatus.Exhausted, repository.Get(other.LeaseId).Status);
    }

    [Fact]
    public void ExhaustedRecurringLeaseWithoutExactRunIgnoresAnotherLeasesActiveRun()
    {
        using var fixture = RecurringLeaseFixture.Create(maxOccurrences: 2);
        var target = fixture.CreateLease(
            "task271-exhausted-no-run",
            fixture.CreatedAt.AddHours(-1),
            fixture.CreatedAt.AddMinutes(-30),
            fixture.CreatedAt.AddHours(2));
        var other = fixture.CreateLease(
            "task271-exhausted-other-run",
            fixture.CreatedAt.AddHours(-1),
            fixture.CreatedAt.AddMinutes(-30),
            fixture.CreatedAt.AddHours(2));
        var repository = new SqliteRecurringConsentLeaseRepository(fixture.Store);
        repository.InsertPending(target);
        repository.InsertPending(other);
        var slot = fixture.CreateSlots(1).Single();
        fixture.InsertAccountingRow(
            "task271-exhausted-other-run-use",
            other,
            slot,
            "run-0",
            "reserved",
            fixture.CreatedAt.AddMinutes(4),
            1,
            other.PerRunDuration,
            null);
        ForceRecurringStatus(fixture, target, "exhausted", fixture.CreatedAt.AddMinutes(5), 1);

        var stopper = new CountingStopper();
        var result = CreateService(fixture, fixture.CreatedAt.AddMinutes(6), stopper: stopper)
            .RevokeRecurringLease(target.LeaseId, "task271-exhausted-no-run-revoke", "task271-test");

        Assert.Equal(StandingLeaseSafetyControlResultStatus.Rejected, result.Status);
        Assert.Equal(StandingLeaseSafetyReasonCodes.LeaseExhausted, result.Reason);
        Assert.False(result.RequiresActiveRunStop);
        Assert.True(result.DurableOperationCommitted);
        Assert.False(result.DurableStateChanged);
        Assert.Equal(0, stopper.Count);
        Assert.Equal(ConsentLeaseStatus.Exhausted, repository.Get(target.LeaseId).Status);
        Assert.Equal(ConsentLeaseStatus.Pending, repository.Get(other.LeaseId).Status);
    }

    [Fact]
    public void FirstNotFoundIsDurablyRejectedAndCannotRevokeLaterInsertedSameId()
    {
        using var fixture = RecurringLeaseFixture.Create();
        var service = CreateService(fixture, fixture.CreatedAt.AddMinutes(3));
        var first = service.RevokeRecurringLease("task271-late-lease", "task271-not-found", "task271-test");
        Assert.Equal(StandingLeaseSafetyControlResultStatus.Rejected, first.Status);
        Assert.Equal("recurring_lease_not_found", first.Reason);
        Assert.Equal("task271-late-lease", fixture.Scalar(
            "SELECT lease_id FROM standing_lease_safety_operations WHERE operation_id = $operation;",
            ("$operation", "task271-not-found")));

        var lateLease = fixture.CreateLease("task271-late-lease", fixture.CreatedAt.AddHours(-1), fixture.CreatedAt.AddMinutes(-30), fixture.CreatedAt.AddHours(2));
        new SqliteRecurringConsentLeaseRepository(fixture.Store).InsertPending(lateLease);
        var replay = service.RevokeRecurringLease(lateLease.LeaseId, "task271-not-found", "task271-test");
        Assert.Equal(StandingLeaseSafetyControlResultStatus.Rejected, replay.Status);
        Assert.Equal("recurring_lease_not_found", replay.Reason);
        Assert.True(first.DurableOperationCommitted);
        Assert.False(first.DurableStateChanged);
        Assert.False(first.Changed);
        Assert.False(first.PhysicalStopFailed);
        Assert.False(first.PhysicalStopRetryRecommended);
        Assert.True(replay.DurableOperationCommitted);
        Assert.False(replay.DurableStateChanged);
        Assert.False(replay.Changed);
        Assert.False(replay.PhysicalStopFailed);
        Assert.False(replay.PhysicalStopRetryRecommended);
        Assert.Equal(ConsentLeaseStatus.Pending, new SqliteRecurringConsentLeaseRepository(fixture.Store).Get(lateLease.LeaseId).Status);
    }

    [Theory]
    [InlineData("lease_revoke", null, null, "lease_revoked", true, false)]
    [InlineData("lease_revoke", null, "task271-corrupt-lease", "lease_revoked", false, false)]
    [InlineData("stop_all_revoke_all", null, "task271-corrupt-lease", "stop_all_applied", true, false)]
    [InlineData("unattended_enable", null, null, "unattended_enabled", false, false)]
    public void CorruptOperationLedgerShapesFailClosed(
        string operationKind,
        string? intentId,
        string? leaseId,
        string resultCode,
        bool changed,
        bool requiresActiveRunStop)
    {
        using var fixture = RecurringLeaseFixture.Create();
        var lease = fixture.CreateLease(
            "task271-corrupt-target",
            fixture.CreatedAt.AddHours(-1),
            fixture.CreatedAt.AddMinutes(-30),
            fixture.CreatedAt.AddHours(2));
        new SqliteRecurringConsentLeaseRepository(fixture.Store).InsertPending(lease);
        fixture.Execute(
            "INSERT INTO standing_lease_safety_operations(operation_id, operation_kind_code, intent_id, lease_id, requested_at_utc, reason_code, result_code, changed, requires_active_run_stop, completed_at_utc) VALUES ($operation, $kind, $intent, $lease, $requested, $reason, $result, $changed, $requires, $completed);",
            ("$operation", "task271-corrupt-operation"),
            ("$kind", operationKind),
            ("$intent", intentId),
            ("$lease", leaseId),
            ("$requested", fixture.CreatedAt.UtcDateTime.Ticks),
            ("$reason", "task271-corrupt-reason"),
            ("$result", resultCode),
            ("$changed", changed ? 1L : 0L),
            ("$requires", requiresActiveRunStop ? 1L : 0L),
            ("$completed", fixture.CreatedAt.UtcDateTime.Ticks));

        var result = CreateService(fixture, fixture.CreatedAt.AddMinutes(3))
            .RevokeRecurringLease(lease.LeaseId, "task271-corrupt-operation", "task271-corrupt-reason");

        Assert.Equal(StandingLeaseSafetyControlResultStatus.Rejected, result.Status);
        Assert.Equal("safety_snapshot_invalid", result.Reason);
        Assert.False(result.DurableOperationCommitted);
        Assert.Equal(ConsentLeaseStatus.Pending, new SqliteRecurringConsentLeaseRepository(fixture.Store).Get(lease.LeaseId).Status);
    }

    [Fact]
    public void OperationIdCannotBeReusedForAnotherRecurringLeaseOrReason()
    {
        using var fixture = RecurringLeaseFixture.Create(maxOccurrences: 1);
        var first = fixture.CreateLease("task271-identity-a", fixture.CreatedAt.AddHours(-1), fixture.CreatedAt.AddMinutes(-30), fixture.CreatedAt.AddHours(2));
        var second = fixture.CreateLease("task271-identity-b", fixture.CreatedAt.AddHours(-1), fixture.CreatedAt.AddMinutes(-30), fixture.CreatedAt.AddHours(2));
        var repository = new SqliteRecurringConsentLeaseRepository(fixture.Store);
        repository.InsertPending(first);
        repository.InsertPending(second);
        var stopper = new CountingStopper();
        var service = CreateService(fixture, fixture.CreatedAt.AddMinutes(3), stopper: stopper);
        Assert.Equal(StandingLeaseSafetyControlResultStatus.Changed, service.RevokeRecurringLease(first.LeaseId, "task271-identity", "task271-test" ).Status);

        var targetConflict = service.RevokeRecurringLease(second.LeaseId, "task271-identity", "task271-test");
        var reasonConflict = service.RevokeRecurringLease(first.LeaseId, "task271-identity", "different-reason");
        AssertOperationConflict(targetConflict);
        AssertOperationConflict(reasonConflict);
        Assert.Equal(ConsentLeaseStatus.Pending, repository.Get(second.LeaseId).Status);
        Assert.Equal(0, stopper.Count);
        Assert.Equal(1L, Count(fixture, "standing_lease_safety_operations"));
        AssertOperationRow(
            fixture,
            "task271-identity",
            StandingLeaseSafetyOperationKindCodes.LeaseRevoke,
            expectedIntentId: null,
            expectedLeaseId: first.LeaseId,
            expectedReasonCode: "task271-test",
            expectedResultCode: StandingLeaseSafetyReasonCodes.LeaseRevoked,
            expectedChanged: true);
    }

    [Fact]
    public void OperationIdCannotCrossStandingAndRecurringLeaseFamilies()
    {
        using var fixture = RecurringLeaseFixture.Create();
        var standing = CreateStandingPending(fixture, "cross-family");
        var recurring = fixture.CreateLease(
            "task271-cross-family-recurring",
            fixture.CreatedAt.AddHours(-1),
            fixture.CreatedAt.AddMinutes(-30),
            fixture.CreatedAt.AddHours(2));
        new SqliteRecurringConsentLeaseRepository(fixture.Store).InsertPending(recurring);
        var stopper = new CountingStopper();
        var service = CreateService(fixture, fixture.CreatedAt.AddMinutes(3), stopper: stopper);

        var recurringFirst = service.RevokeRecurringLease(
            recurring.LeaseId,
            "task271-cross-family-operation",
            "task271-test");
        var standingConflict = service.RevokeLease(
            standing.IntentId,
            "task271-cross-family-operation",
            "task271-test");

        Assert.Equal(StandingLeaseSafetyControlResultStatus.Changed, recurringFirst.Status);
        AssertOperationConflict(standingConflict);
        Assert.Equal(ConsentLeaseStatus.Pending, new SqliteConsentLeaseRepository(fixture.Store).Get(standing.LeaseId).Status);
        Assert.Equal(1L, Count(fixture, "standing_lease_safety_operations"));

        var standingFirst = service.RevokeLease(
            standing.IntentId,
            "task271-cross-family-reverse",
            "task271-test");
        var secondRecurring = fixture.CreateLease(
            "task271-cross-family-recurring-second",
            fixture.CreatedAt.AddHours(-1),
            fixture.CreatedAt.AddMinutes(-30),
            fixture.CreatedAt.AddHours(2));
        new SqliteRecurringConsentLeaseRepository(fixture.Store).InsertPending(secondRecurring);
        var recurringConflict = service.RevokeRecurringLease(
            secondRecurring.LeaseId,
            "task271-cross-family-reverse",
            "task271-test");

        Assert.Equal(StandingLeaseSafetyControlResultStatus.Changed, standingFirst.Status);
        AssertOperationConflict(recurringConflict);
        Assert.Equal(ConsentLeaseStatus.Revoked, new SqliteConsentLeaseRepository(fixture.Store).Get(standing.LeaseId).Status);
        Assert.Equal(ConsentLeaseStatus.Revoked, new SqliteRecurringConsentLeaseRepository(fixture.Store).Get(recurring.LeaseId).Status);
        Assert.Equal(ConsentLeaseStatus.Pending, new SqliteRecurringConsentLeaseRepository(fixture.Store).Get(secondRecurring.LeaseId).Status);
        Assert.Equal(0, stopper.Count);
        Assert.Equal(2L, Count(fixture, "standing_lease_safety_operations"));
    }

    [Fact]
    public void OperationIdConflictCannotTurnStopAllIntoDurableCommit()
    {
        using var fixture = RecurringLeaseFixture.Create();
        var lease = fixture.CreateLease(
            "task271-stop-conflict-lease",
            fixture.CreatedAt.AddHours(-1),
            fixture.CreatedAt.AddMinutes(-30),
            fixture.CreatedAt.AddHours(2));
        var repository = new SqliteRecurringConsentLeaseRepository(fixture.Store);
        repository.InsertPending(lease);
        var stopper = new CountingStopper();
        var service = CreateService(fixture, fixture.CreatedAt.AddMinutes(3), stopper: stopper);

        var original = service.RevokeRecurringLease(lease.LeaseId, "task271-stop-conflict-operation", "task271-test");
        var conflict = service.StopAllAndRevokeAll("task271-stop-conflict-operation", "task271-test");

        Assert.Equal(StandingLeaseSafetyControlResultStatus.Changed, original.Status);
        AssertOperationConflict(conflict);
        Assert.Equal(ConsentLeaseStatus.Revoked, repository.Get(lease.LeaseId).Status);
        Assert.Equal(0L, Convert.ToInt64(fixture.Scalar("SELECT stop_all_applied FROM unattended_safety_state WHERE state_id = 'global';")));
        Assert.Equal(0, stopper.Count);
        AssertOperationRow(
            fixture,
            "task271-stop-conflict-operation",
            StandingLeaseSafetyOperationKindCodes.LeaseRevoke,
            expectedIntentId: null,
            expectedLeaseId: lease.LeaseId,
            expectedReasonCode: "task271-test",
            expectedResultCode: StandingLeaseSafetyReasonCodes.LeaseRevoked,
            expectedChanged: true);
    }

    [Fact]
    public void OperationIdConflictCannotTurnUnattendedModeIntoAnotherDurableDecision()
    {
        using var fixture = RecurringLeaseFixture.Create();
        var stopper = new CountingStopper();
        var service = CreateService(fixture, fixture.CreatedAt.AddMinutes(3), stopper: stopper);

        var original = service.EnableUnattended("task271-mode-conflict-operation", "task271-test");
        var kindConflict = service.DisableUnattended("task271-mode-conflict-operation", "task271-test");
        var reasonConflict = service.EnableUnattended("task271-mode-conflict-operation", "different-reason");

        Assert.Equal(StandingLeaseSafetyControlResultStatus.Changed, original.Status);
        AssertOperationConflict(kindConflict);
        AssertOperationConflict(reasonConflict);
        Assert.Equal(1L, Convert.ToInt64(fixture.Scalar("SELECT unattended_mode_code = 'enabled' FROM unattended_safety_state WHERE state_id = 'global';")));
        Assert.Equal(0, stopper.Count);
        AssertOperationRow(
            fixture,
            "task271-mode-conflict-operation",
            StandingLeaseSafetyOperationKindCodes.UnattendedEnable,
            expectedIntentId: null,
            expectedLeaseId: null,
            expectedReasonCode: "task271-test",
            expectedResultCode: StandingLeaseSafetyReasonCodes.UnattendedEnabled,
            expectedChanged: true);
    }

    [Fact]
    public void OperationIdConflictCannotTurnStandingIntentOrReasonIntoDurableDecision()
    {
        using var fixture = RecurringLeaseFixture.Create();
        var first = CreateStandingPending(fixture, "conflict-first");
        var second = CreateStandingPending(fixture, "conflict-second");
        var stopper = new CountingStopper();
        var service = CreateService(fixture, fixture.CreatedAt.AddMinutes(3), stopper: stopper);

        var original = service.RevokeLease(first.IntentId, "task271-standing-conflict-operation", "task271-test");
        var intentConflict = service.RevokeLease(second.IntentId, "task271-standing-conflict-operation", "task271-test");
        var reasonConflict = service.RevokeLease(first.IntentId, "task271-standing-conflict-operation", "different-reason");

        Assert.Equal(StandingLeaseSafetyControlResultStatus.Changed, original.Status);
        AssertOperationConflict(intentConflict);
        AssertOperationConflict(reasonConflict);
        Assert.Equal(ConsentLeaseStatus.Revoked, new SqliteConsentLeaseRepository(fixture.Store).Get(first.LeaseId).Status);
        Assert.Equal(ConsentLeaseStatus.Pending, new SqliteConsentLeaseRepository(fixture.Store).Get(second.LeaseId).Status);
        Assert.Equal(0, stopper.Count);
        AssertOperationRow(
            fixture,
            "task271-standing-conflict-operation",
            StandingLeaseSafetyOperationKindCodes.LeaseRevoke,
            expectedIntentId: first.IntentId,
            expectedLeaseId: first.LeaseId,
            expectedReasonCode: "task271-test",
            expectedResultCode: StandingLeaseSafetyReasonCodes.LeaseRevoked,
            expectedChanged: true);
    }

    [Fact]
    public void StopAllRevokesPendingAndActiveRecurringLeasesInOneCommit()
    {
        using var fixture = RecurringLeaseFixture.Create(maxOccurrences: 1);
        EnableUnattended(fixture, "task271-stop-enable");
        var active = fixture.CreateLease("task271-stop-active", fixture.CreatedAt.AddHours(-1), fixture.CreatedAt.AddMinutes(-30), fixture.CreatedAt.AddHours(2));
        new SqliteRecurringConsentLeaseRepository(fixture.Store).InsertPending(active);
        Activate(fixture, active, "task271-stop-approval");
        var pending = fixture.CreateLease("task271-stop-pending", fixture.CreatedAt.AddHours(-1), fixture.CreatedAt.AddMinutes(-30), fixture.CreatedAt.AddHours(2));
        new SqliteRecurringConsentLeaseRepository(fixture.Store).InsertPending(pending);
        var slot = fixture.CreateSlots(1).Single();
        fixture.InsertAccountingRow("task271-stop-use", active, slot, "run-0", "reserved", fixture.CreatedAt.AddMinutes(4), 1, active.PerRunDuration, null);
        var stopper = new CountingStopper();
        Assert.Equal(0, stopper.Count);

        var result = CreateService(fixture, fixture.CreatedAt.AddMinutes(5), stopper: stopper)
            .StopAllAndRevokeAll("task271-stop-all", "task271-test");

        Assert.Equal(StandingLeaseSafetyControlResultStatus.Changed, result.Status);
        Assert.True(result.RequiresActiveRunStop);
        Assert.Equal(1, stopper.Count);
        var repository = new SqliteRecurringConsentLeaseRepository(fixture.Store);
        Assert.Equal(ConsentLeaseStatus.Revoked, repository.Get(active.LeaseId).Status);
        Assert.Equal(ConsentLeaseStatus.Revoked, repository.Get(pending.LeaseId).Status);
        Assert.Equal(2L, Count(fixture, "standing_lease_safety_operations"));
        Assert.Equal(1L, Convert.ToInt64(fixture.Scalar("SELECT COUNT(*) FROM standing_lease_safety_operations WHERE operation_kind_code = 'stop_all_revoke_all';")));
        Assert.Equal(1L, Convert.ToInt64(fixture.Scalar("SELECT stop_all_applied FROM unattended_safety_state WHERE state_id = 'global';")));
    }

    [Fact]
    public void StopAllRevokesStandingAndRecurringFamiliesInOneCommit()
    {
        using var fixture = RecurringLeaseFixture.Create(maxOccurrences: 1);
        var standing = CreateStandingPending(fixture, "mixed-stop");
        EnableUnattended(fixture, "task271-mixed-stop-enable");

        var active = fixture.CreateLease(
            "task271-mixed-stop-active",
            fixture.CreatedAt.AddHours(-1),
            fixture.CreatedAt.AddMinutes(-30),
            fixture.CreatedAt.AddHours(2));
        var pending = fixture.CreateLease(
            "task271-mixed-stop-pending",
            fixture.CreatedAt.AddHours(-1),
            fixture.CreatedAt.AddMinutes(-30),
            fixture.CreatedAt.AddHours(2));
        var terminal = fixture.CreateLease(
            "task271-mixed-stop-terminal",
            fixture.CreatedAt.AddHours(-1),
            fixture.CreatedAt.AddMinutes(-30),
            fixture.CreatedAt.AddHours(2));
        var recurringRepository = new SqliteRecurringConsentLeaseRepository(fixture.Store);
        recurringRepository.InsertPending(active);
        Activate(fixture, active, "task271-mixed-stop-approval");
        recurringRepository.InsertPending(pending);
        recurringRepository.InsertPending(terminal);
        ForceRecurringStatus(fixture, terminal, "expired", fixture.CreatedAt.AddMinutes(2), 1);
        var slot = fixture.CreateSlots(1).Single();
        fixture.InsertAccountingRow(
            "task271-mixed-stop-use",
            active,
            slot,
            "run-0",
            "reserved",
            fixture.CreatedAt.AddMinutes(4),
            1,
            active.PerRunDuration,
            null);
        var stopper = new CountingStopper();

        var result = CreateService(fixture, fixture.CreatedAt.AddMinutes(5), stopper: stopper)
            .StopAllAndRevokeAll("task271-mixed-stop-all", "task271-test");

        Assert.Equal(StandingLeaseSafetyControlResultStatus.Changed, result.Status);
        Assert.True(result.DurableOperationCommitted);
        Assert.True(result.DurableStateChanged);
        Assert.True(result.RequiresActiveRunStop);
        Assert.Equal(1, stopper.Count);
        Assert.Equal(ConsentLeaseStatus.Revoked, new SqliteConsentLeaseRepository(fixture.Store).Get(standing.LeaseId).Status);
        Assert.Equal(PlanOccurrenceStatus.Blocked, new SqlitePlanOccurrenceRepository(fixture.Store).Get(standing.OccurrenceId).Status);
        Assert.Equal(ConsentLeaseStatus.Revoked, recurringRepository.Get(active.LeaseId).Status);
        Assert.Equal(ConsentLeaseStatus.Revoked, recurringRepository.Get(pending.LeaseId).Status);
        Assert.Equal(ConsentLeaseStatus.Expired, recurringRepository.Get(terminal.LeaseId).Status);
        Assert.Equal(1, recurringRepository.Get(terminal.LeaseId).Version);
        Assert.Equal(2L, Count(fixture, "standing_lease_safety_operations"));
        Assert.Equal(1L, Convert.ToInt64(fixture.Scalar("SELECT COUNT(*) FROM standing_lease_safety_operations WHERE operation_kind_code = 'stop_all_revoke_all';")));
        Assert.Equal(1L, Convert.ToInt64(fixture.Scalar("SELECT COUNT(*) FROM unattended_safety_state WHERE stop_all_applied = 1;")));
    }

    [Fact]
    public void StopAllRollbackLeavesRecurringLeasesGlobalStateAndLedgerUntouched()
    {
        using var fixture = RecurringLeaseFixture.Create();
        var standing = CreateStandingPending(fixture, "mixed-rollback");
        var lease = fixture.CreateLease("task271-stop-rollback", fixture.CreatedAt.AddHours(-1), fixture.CreatedAt.AddMinutes(-30), fixture.CreatedAt.AddHours(2));
        new SqliteRecurringConsentLeaseRepository(fixture.Store).InsertPending(lease);
        var service = new StandingLeaseSafetyControlService(
            fixture.Store,
            () => fixture.CreatedAt.AddMinutes(3),
            beforeCommitForTest: (_, _) => throw new InvalidOperationException("task271 rollback"));

        var result = service.StopAllAndRevokeAll("task271-stop-rollback-op", "task271-test");

        Assert.Equal(StandingLeaseSafetyControlResultStatus.Rejected, result.Status);
        Assert.False(result.DurableOperationCommitted);
        Assert.Equal(ConsentLeaseStatus.Pending, new SqliteConsentLeaseRepository(fixture.Store).Get(standing.LeaseId).Status);
        Assert.NotEqual(PlanOccurrenceStatus.Blocked, new SqlitePlanOccurrenceRepository(fixture.Store).Get(standing.OccurrenceId).Status);
        Assert.Equal(ConsentLeaseStatus.Pending, new SqliteRecurringConsentLeaseRepository(fixture.Store).Get(lease.LeaseId).Status);
        Assert.Equal(0L, Count(fixture, "standing_lease_safety_operations"));
        Assert.Equal(0L, Convert.ToInt64(fixture.Scalar("SELECT stop_all_applied FROM unattended_safety_state WHERE state_id = 'global';")));
    }

    [Fact]
    public void CommittedStopAllSurvivesPhysicalStopFailureAndReplayRetriesOnlyStopper()
    {
        using var fixture = RecurringLeaseFixture.Create(maxOccurrences: 1);
        EnableUnattended(fixture, "task271-stop-failure-enable");
        var lease = fixture.CreateLease(
            "task271-stop-failure-active",
            fixture.CreatedAt.AddHours(-1),
            fixture.CreatedAt.AddMinutes(-30),
            fixture.CreatedAt.AddHours(2));
        var repository = new SqliteRecurringConsentLeaseRepository(fixture.Store);
        repository.InsertPending(lease);
        Activate(fixture, lease, "task271-stop-failure-approval");
        var slot = fixture.CreateSlots(1).Single();
        fixture.InsertAccountingRow(
            "task271-stop-failure-use",
            lease,
            slot,
            "run-0",
            "reserved",
            fixture.CreatedAt.AddMinutes(4),
            1,
            lease.PerRunDuration,
            null);
        var stopper = new FailOnceStopper();
        var service = CreateService(fixture, fixture.CreatedAt.AddMinutes(5), stopper: stopper);

        var result = service.StopAllAndRevokeAll("task271-stop-failure", "task271-test");

        Assert.Equal(StandingLeaseSafetyControlResultStatus.Rejected, result.Status);
        Assert.Equal(StandingLeaseSafetyReasonCodes.ActiveRunStopFailed, result.Reason);
        Assert.True(result.DurableOperationCommitted);
        Assert.True(result.DurableStateChanged);
        Assert.True(result.PhysicalStopFailed);
        Assert.True(result.PhysicalStopRetryRecommended);
        Assert.Equal(ConsentLeaseStatus.Revoked, repository.Get(lease.LeaseId).Status);
        Assert.Equal(1L, Convert.ToInt64(fixture.Scalar("SELECT stop_all_applied FROM unattended_safety_state WHERE state_id = 'global';")));

        var replay = service.StopAllAndRevokeAll("task271-stop-failure", "task271-test");

        Assert.Equal(StandingLeaseSafetyControlResultStatus.AlreadyApplied, replay.Status);
        Assert.True(replay.DurableOperationCommitted);
        Assert.True(replay.DurableStateChanged);
        Assert.False(replay.PhysicalStopFailed);
        Assert.Equal(2, stopper.Count);
        Assert.Equal(2L, Count(fixture, "standing_lease_safety_operations"));
    }

    [Fact]
    public void FreshApprovalAfterStopAllCanActivateWhileUnattendedRemainsEnabled()
    {
        using var fixture = RecurringLeaseFixture.Create(maxOccurrences: 1);
        EnableUnattended(fixture, "task271-fresh-enable");
        var oldLease = fixture.CreateLease("task271-fresh-old", fixture.CreatedAt.AddHours(-1), fixture.CreatedAt.AddMinutes(-30), fixture.CreatedAt.AddHours(2));
        new SqliteRecurringConsentLeaseRepository(fixture.Store).InsertPending(oldLease);
        new SqliteStandingLeaseSafetyControlTransaction(fixture.Store)
            .StopAllAndRevokeAll("task271-fresh-stop", "task271-test", fixture.CreatedAt.AddMinutes(3));

        var freshLease = fixture.CreateLease("task271-fresh-new", fixture.CreatedAt.AddHours(-1), fixture.CreatedAt.AddMinutes(-30), fixture.CreatedAt.AddHours(2));
        new SqliteRecurringConsentLeaseRepository(fixture.Store).InsertPending(freshLease);
        var receipt = RecurringLeaseLocalApprovalReceipt.CreateForTrustedLocalApprovalAdapter(
            "task271-fresh-approval",
            freshLease.LeaseId,
            freshLease.PlanId,
            freshLease.ConfigurationRef.ConfigurationDigest,
            freshLease.AuthorizationDigest,
            "S-1-5-21",
            "task271-session",
            fixture.CreatedAt.AddMinutes(4));

        var activation = new RecurringLeaseLocalApprovalActivationService(
            fixture.Store,
            () => fixture.CreatedAt.AddMinutes(5)).Activate(freshLease.LeaseId, receipt);

        Assert.True(activation.Status == RecurringLeaseLocalApprovalActivationStatus.Activated, activation.Reason);
        Assert.Equal(ConsentLeaseStatus.Active, new SqliteRecurringConsentLeaseRepository(fixture.Store).Get(freshLease.LeaseId).Status);
    }

    [Fact]
    public void CommittedRevokeSurvivesStopperAndAuditFailures()
    {
        using var fixture = RecurringLeaseFixture.Create(maxOccurrences: 1);
        EnableUnattended(fixture, "task271-failure-enable");
        var lease = fixture.CreateLease("task271-failure-boundary", fixture.CreatedAt.AddHours(-1), fixture.CreatedAt.AddMinutes(-30), fixture.CreatedAt.AddHours(2));
        new SqliteRecurringConsentLeaseRepository(fixture.Store).InsertPending(lease);
        Activate(fixture, lease, "task271-failure-approval");
        var slot = fixture.CreateSlots(1).Single();
        fixture.InsertAccountingRow(
            "task271-failure-use",
            lease,
            slot,
            "run-0",
            "reserved",
            fixture.CreatedAt.AddMinutes(4),
            1,
            lease.PerRunDuration,
            null);
        var stopper = new FailOnceStopper();
        var service = new StandingLeaseSafetyControlService(
            fixture.Store,
            () => fixture.CreatedAt.AddMinutes(5),
            auditForTest: (_, _) => throw new InvalidOperationException("audit"),
            activeRunStopper: stopper);

        var result = service.RevokeRecurringLease(lease.LeaseId, "task271-failure-op", "task271-test");

        Assert.Equal(StandingLeaseSafetyControlResultStatus.Rejected, result.Status);
        Assert.Equal(StandingLeaseSafetyReasonCodes.ActiveRunStopFailed, result.Reason);
        Assert.False(result.Changed);
        Assert.True(result.DurableOperationCommitted);
        Assert.True(result.DurableStateChanged);
        Assert.True(result.PhysicalStopFailed);
        Assert.True(result.PhysicalStopRetryRecommended);
        Assert.Equal(ConsentLeaseStatus.Revoked, new SqliteRecurringConsentLeaseRepository(fixture.Store).Get(lease.LeaseId).Status);

        var replay = service.RevokeRecurringLease(lease.LeaseId, "task271-failure-op", "task271-test");

        Assert.Equal(StandingLeaseSafetyControlResultStatus.AlreadyApplied, replay.Status);
        Assert.False(replay.Changed);
        Assert.True(replay.DurableOperationCommitted);
        Assert.True(replay.DurableStateChanged);
        Assert.False(replay.PhysicalStopFailed);
        Assert.Equal(2, stopper.Count);
        Assert.Equal(2, new SqliteRecurringConsentLeaseRepository(fixture.Store).Get(lease.LeaseId).Version);
        Assert.Equal(2L, Count(fixture, "standing_lease_safety_operations"));
        Assert.Equal(1L, Convert.ToInt64(fixture.Scalar(
            "SELECT COUNT(*) FROM standing_lease_safety_operations WHERE operation_id = 'task271-failure-op';")));
    }

    [Fact]
    public void ClockFailureAndNonUtcTimeFailClosedBeforePersistence()
    {
        using var fixture = RecurringLeaseFixture.Create();
        var lease = fixture.CreateLease("task271-clock", fixture.CreatedAt.AddHours(-1), fixture.CreatedAt.AddMinutes(-30), fixture.CreatedAt.AddHours(2));
        new SqliteRecurringConsentLeaseRepository(fixture.Store).InsertPending(lease);
        var clock = new StandingLeaseSafetyControlService(fixture.Store, () => null);
        var unavailable = clock.RevokeRecurringLease(lease.LeaseId, "task271-clock-op", "task271-test");
        Assert.Equal("safety_clock_unavailable", unavailable.Reason);
        Assert.False(unavailable.DurableOperationCommitted);
        Assert.False(unavailable.DurableStateChanged);

        var nonUtc = new StandingLeaseSafetyControlService(fixture.Store, () => fixture.CreatedAt.ToOffset(TimeSpan.FromHours(8)));
        var wrongZone = nonUtc.RevokeRecurringLease(lease.LeaseId, "task271-non-utc-op", "task271-test");
        Assert.Equal("safety_time_not_utc", wrongZone.Reason);
        Assert.False(wrongZone.DurableOperationCommitted);
        Assert.False(wrongZone.DurableStateChanged);
        Assert.Equal(ConsentLeaseStatus.Pending, new SqliteRecurringConsentLeaseRepository(fixture.Store).Get(lease.LeaseId).Status);
        Assert.Equal(0L, Count(fixture, "standing_lease_safety_operations"));
    }

    private static StandingLeaseSafetyControlService CreateService(
        RecurringLeaseFixture fixture,
        DateTimeOffset now,
        IStandingLeaseActiveRunStopper? stopper = null,
        Action<Microsoft.Data.Sqlite.SqliteConnection, Microsoft.Data.Sqlite.SqliteTransaction>? beforeCommit = null) =>
        new(fixture.Store, () => now, activeRunStopper: stopper, beforeCommitForTest: beforeCommit);

    private static void EnableUnattended(RecurringLeaseFixture fixture, string operationId) =>
        new SqliteStandingLeaseSafetyControlTransaction(fixture.Store)
            .SetUnattendedMode(operationId, true, "task271-test", fixture.CreatedAt.AddMinutes(1));

    private static void Activate(RecurringLeaseFixture fixture, RecurringConsentLease lease, string approvalId)
    {
        var receipt = RecurringLeaseLocalApprovalReceipt.CreateForTrustedLocalApprovalAdapter(
            approvalId,
            lease.LeaseId,
            lease.PlanId,
            lease.ConfigurationRef.ConfigurationDigest,
            lease.AuthorizationDigest,
            "S-1-5-21",
            "task271-session",
            fixture.CreatedAt.AddMinutes(2));
        var result = new RecurringLeaseLocalApprovalActivationService(
            fixture.Store,
            () => fixture.CreatedAt.AddMinutes(3)).Activate(lease.LeaseId, receipt);
        Assert.True(result.Status == RecurringLeaseLocalApprovalActivationStatus.Activated, result.Reason);
    }

    private static void ForceRecurringStatus(
        RecurringLeaseFixture fixture,
        RecurringConsentLease lease,
        string statusCode,
        DateTimeOffset updatedAtUtc,
        long version)
    {
        var trigger = fixture.Scalar(
            "SELECT sql FROM sqlite_master WHERE type = 'trigger' AND name = 'trg_recurring_consent_leases_lifecycle_update';") as string
            ?? throw new InvalidOperationException("Recurring lease lifecycle trigger was not found.");
        fixture.Execute("DROP TRIGGER trg_recurring_consent_leases_lifecycle_update;");
        try
        {
            fixture.Execute(
                "UPDATE recurring_consent_leases SET status_code = $status, updated_at_utc = $updated, version = $version WHERE lease_id = $lease;",
                ("$status", statusCode),
                ("$updated", updatedAtUtc.UtcDateTime.Ticks),
                ("$version", version),
                ("$lease", lease.LeaseId));
        }
        finally
        {
            fixture.Execute(trigger);
        }
    }

    private static long Count(RecurringLeaseFixture fixture, string table) =>
        Convert.ToInt64(fixture.Scalar($"SELECT COUNT(*) FROM {table};"));

    private static void AssertOperationConflict(StandingLeaseSafetyControlResult result)
    {
        Assert.Equal(StandingLeaseSafetyControlResultStatus.Rejected, result.Status);
        Assert.Equal("safety_operation_conflict", result.Reason);
        Assert.False(result.DurableOperationCommitted);
        Assert.False(result.DurableStateChanged);
        Assert.False(result.Changed);
        Assert.False(result.PhysicalStopFailed);
        Assert.False(result.PhysicalStopRetryRecommended);
        Assert.False(result.RequiresActiveRunStop);
    }

    private static void AssertOperationRow(
        RecurringLeaseFixture fixture,
        string operationId,
        string expectedOperationKind,
        string? expectedIntentId,
        string? expectedLeaseId,
        string expectedReasonCode,
        string expectedResultCode,
        bool expectedChanged)
    {
        Assert.Equal(1L, Convert.ToInt64(fixture.Scalar(
            "SELECT COUNT(*) FROM standing_lease_safety_operations WHERE operation_id = $operation;",
            ("$operation", operationId))));
        Assert.Equal(expectedOperationKind, fixture.Scalar(
            "SELECT operation_kind_code FROM standing_lease_safety_operations WHERE operation_id = $operation;",
            ("$operation", operationId)));
        AssertNullableText(
            expectedIntentId,
            fixture.Scalar(
                "SELECT intent_id FROM standing_lease_safety_operations WHERE operation_id = $operation;",
                ("$operation", operationId)));
        AssertNullableText(
            expectedLeaseId,
            fixture.Scalar(
                "SELECT lease_id FROM standing_lease_safety_operations WHERE operation_id = $operation;",
                ("$operation", operationId)));
        Assert.Equal(expectedReasonCode, fixture.Scalar(
            "SELECT reason_code FROM standing_lease_safety_operations WHERE operation_id = $operation;",
            ("$operation", operationId)));
        Assert.Equal(expectedResultCode, fixture.Scalar(
            "SELECT result_code FROM standing_lease_safety_operations WHERE operation_id = $operation;",
            ("$operation", operationId)));
        Assert.Equal(expectedChanged ? 1L : 0L, Convert.ToInt64(fixture.Scalar(
            "SELECT changed FROM standing_lease_safety_operations WHERE operation_id = $operation;",
            ("$operation", operationId))));
    }

    private static void AssertNullableText(string? expected, object? actual)
    {
        if (expected is null)
        {
            Assert.True(actual is null or DBNull);
            return;
        }

        Assert.Equal(expected, actual);
    }

    private static StandingLeaseTestBinding CreateStandingPending(
        RecurringLeaseFixture fixture,
        string suffix)
    {
        var intentId = "task271-standing-intent-" + suffix;
        var planId = "task271-standing-plan-" + suffix;
        var occurrenceId = "task271-standing-occurrence-" + suffix;
        var leaseId = "task271-standing-lease-" + suffix;
        var scopeId = "task271-standing-scope-" + suffix;
        var userSid = "S-1-5-21-task271";
        var sessionBinding = "task271-session-" + suffix;
        var requestedAt = fixture.CreatedAt;
        var intent = StandingSetupIntentSnapshot.CreateForTrustedSetupAdapter(
            intentId,
            "task271-request-" + suffix,
            userSid,
            sessionBinding,
            requestedAt,
            requestedAt.AddHours(1),
            requestedAt.AddMinutes(1),
            requestedAt.AddMinutes(2),
            requestedAt.AddMinutes(3),
            TimeSpan.FromSeconds(30),
            requestedAt.AddHours(1),
            Path.Combine(fixture.RootPath, "standing-captures"),
            "standing-" + suffix + ".mp4");
        var created = new StandingSetupIntentService(fixture.Store, () => requestedAt)
            .CreateOrGet(intent);
        Assert.True(
            created.Result == StandingSetupIntentResultStatus.Created,
            $"standing intent create result={created.Result}; reason={created.Reason}");

        var selection = StandingFixedRegionSelectionSnapshot.CreateForTrustedLocalSelectionAdapter(
            intentId,
            userSid,
            sessionBinding,
            requestedAt.AddMinutes(1),
            "DISPLAY-TASK271-" + suffix,
            new AuthorizedPhysicalRectangle(0, 0, 1920, 1080),
            new AuthorizedPhysicalRectangle(10, 20, 640, 480),
            96,
            96,
            1920,
            1080,
            AuthorizedDisplayOrientation.Landscape,
            "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef");
        var prepared = new StandingLeasePreparationService(
            fixture.Store,
            () => requestedAt.AddMinutes(1),
            new StandingTestIds(planId, occurrenceId, leaseId, scopeId)).Prepare(selection);
        Assert.Equal(StandingLeasePreparationResultStatus.Prepared, prepared.Status);
        return new StandingLeaseTestBinding(intentId, planId, occurrenceId, leaseId, scopeId);
    }

    private sealed record StandingLeaseTestBinding(
        string IntentId,
        string PlanId,
        string OccurrenceId,
        string LeaseId,
        string ScopeId);

    private sealed class StandingTestIds : IStandingLeasePreparationIdProvider
    {
        private readonly string _planId;
        private readonly string _occurrenceId;
        private readonly string _leaseId;
        private readonly string _scopeId;

        internal StandingTestIds(string planId, string occurrenceId, string leaseId, string scopeId)
        {
            _planId = planId;
            _occurrenceId = occurrenceId;
            _leaseId = leaseId;
            _scopeId = scopeId;
        }

        public string CreatePlanId() => _planId;
        public string CreateOccurrenceId() => _occurrenceId;
        public string CreateLeaseId() => _leaseId;
        public string CreateScopeId() => _scopeId;
    }

    private sealed class CountingStopper : IStandingLeaseActiveRunStopper
    {
        public int Count { get; private set; }

        public void StopAll(string reason)
        {
            Count++;
        }
    }

    private sealed class FailOnceStopper : IStandingLeaseActiveRunStopper
    {
        public int Count { get; private set; }

        public void StopAll(string reason)
        {
            Count++;
            if (Count == 1)
                throw new InvalidOperationException("stopper-first-call");
        }
    }
}
