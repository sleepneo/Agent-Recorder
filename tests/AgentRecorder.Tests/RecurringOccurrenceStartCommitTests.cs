using AgentRecorder.Core;
using AgentRecorder.Core.Automation;
using AgentRecorder.Persistence;
using AgentRecorder.Windows;
using System.Reflection;
using Xunit;

namespace AgentRecorder.Tests;

[Collection("NonParallel-AgentRecorderDataDir")]
public sealed class RecurringOccurrenceStartCommitTests
{
    [Fact]
    public void MatchingEnvironmentCommitsRunUseAndReceiptExactlyOnce()
    {
        using var context = RecurringOccurrenceReservationTests.ReservationContext.Create();
        var reservation = Reserve(context, "task265-run-commit", "task265-use-commit");
        var provider = new CountingEnvironmentProvider(new RecurringOccurrenceReservationTests.MatchingReservationEnvironmentProvider());
        var now = context.Fixture.CreatedAt;

        var result = new RecurringOccurrenceStartCommitService(
            context.Fixture.Store,
            () => now,
            provider).Commit(context.Lease.LeaseId, context.Slot.OccurrenceIdentity);

        Assert.Equal(RecurringOccurrenceStartCommitStatus.Committed, result.Status);
        Assert.True(result.Changed);
        Assert.True(result.Succeeded);
        Assert.True(result.CommittedNow);
        Assert.True(result.CanIssueProof);
        Assert.NotNull(result.FirstCommitReceipt);
        Assert.Equal(1, provider.CaptureCount);
        Assert.Equal(result.SpecificationDigest, result.FirstCommitReceipt!.Specification.SpecificationDigest);
        Assert.Equal(context.Lease.PerRunDuration, result.FirstCommitReceipt.Specification.Duration);

        var occurrence = new SqlitePlanOccurrenceRepository(context.Fixture.Store).Get(context.Slot.OccurrenceId!);
        var run = new SqliteRecordingRunRepository(context.Fixture.Store).Get(result.RunId!);
        var use = new SqliteRecurringLeaseUseAccountingReader(context.Fixture.Store).TryGetByOccurrence(context.Slot.OccurrenceIdentity);
        var lease = new SqliteRecurringConsentLeaseRepository(context.Fixture.Store).Get(context.Lease.LeaseId);

        Assert.Equal(PlanOccurrenceStatus.RunCreated, occurrence.Status);
        Assert.Equal(result.RunId, occurrence.RunId);
        Assert.Equal(RecordingRunStatus.StartCommitted, run.Status);
        Assert.True(run.HasCrossedStartCommit);
        Assert.Equal(2, run.Version);
        Assert.NotNull(use);
        Assert.Equal(LeaseUseStatus.StartCommitted, use!.Status);
        Assert.Equal(1, use.ReservedUseCount);
        Assert.Equal(context.Lease.PerRunDuration, use.ReservedDuration);
        Assert.Equal(ConsentLeaseStatus.Active, lease.Status);
    }

    [Fact]
    public void SingleUseLeaseIsExhaustedByExactQuotaSnapshot()
    {
        using var context = RecurringOccurrenceReservationTests.ReservationContext.Create(maxUses: 1);
        var reservation = Reserve(context, "task265-run-exhaust", "task265-use-exhaust");
        var now = context.Fixture.CreatedAt;
        var leaseBefore = new SqliteRecurringConsentLeaseRepository(context.Fixture.Store).Get(context.Lease.LeaseId);

        var result = new RecurringOccurrenceStartCommitService(
            context.Fixture.Store,
            () => now,
            new RecurringOccurrenceReservationTests.MatchingReservationEnvironmentProvider())
            .Commit(context.Lease.LeaseId, context.Slot.OccurrenceIdentity);

        Assert.True(result.Status == RecurringOccurrenceStartCommitStatus.Committed,
            $"status={result.Status}; reason={result.ReasonCode}");
        var lease = new SqliteRecurringConsentLeaseRepository(context.Fixture.Store).Get(context.Lease.LeaseId);
        Assert.Equal(ConsentLeaseStatus.Exhausted, lease.Status);
        Assert.Equal(leaseBefore.Version + 1, lease.Version);
    }

    [Fact]
    public void ReplayIsReadOnlyAndDoesNotReissueReceiptOrCallProvider()
    {
        using var context = RecurringOccurrenceReservationTests.ReservationContext.Create();
        _ = Reserve(context, "task265-run-replay", "task265-use-replay");
        var provider = new CountingEnvironmentProvider(new RecurringOccurrenceReservationTests.MatchingReservationEnvironmentProvider());
        var service = new RecurringOccurrenceStartCommitService(context.Fixture.Store, () => context.Fixture.CreatedAt, provider);

        var first = service.Commit(context.Lease.LeaseId, context.Slot.OccurrenceIdentity);
        var runVersion = new SqliteRecordingRunRepository(context.Fixture.Store).Get(first.RunId!).Version;
        var useVersion = Convert.ToInt64(context.Fixture.Scalar(
            "SELECT version FROM recurring_lease_uses WHERE occurrence_identity = $identity;",
            ("$identity", context.Slot.OccurrenceIdentity)));
        var replay = service.Commit(context.Lease.LeaseId, context.Slot.OccurrenceIdentity);

        Assert.Equal(RecurringOccurrenceStartCommitStatus.Committed, first.Status);
        Assert.Equal(RecurringOccurrenceStartCommitStatus.AlreadyCommitted, replay.Status);
        Assert.True(replay.Succeeded);
        Assert.False(replay.CommittedNow);
        Assert.False(replay.CanIssueProof);
        Assert.Null(replay.FirstCommitReceipt);
        Assert.Equal(1, provider.CaptureCount);
        Assert.Equal(runVersion, new SqliteRecordingRunRepository(context.Fixture.Store).Get(first.RunId!).Version);
        Assert.Equal(useVersion, Convert.ToInt64(context.Fixture.Scalar(
            "SELECT version FROM recurring_lease_uses WHERE occurrence_identity = $identity;",
            ("$identity", context.Slot.OccurrenceIdentity))));
    }

    [Fact]
    public void EnvironmentFailureTerminalizesRunUseAndOccurrenceAndReplayIsAlreadyBlocked()
    {
        using var context = RecurringOccurrenceReservationTests.ReservationContext.Create();
        _ = Reserve(context, "task265-run-block", "task265-use-block");
        var rejecting = new CountingEnvironmentProvider(new RejectingEnvironmentProvider());
        var service = new RecurringOccurrenceStartCommitService(context.Fixture.Store, () => context.Fixture.CreatedAt, rejecting);

        var blocked = service.Commit(context.Lease.LeaseId, context.Slot.OccurrenceIdentity);
        Assert.True(blocked.Status == RecurringOccurrenceStartCommitStatus.Blocked,
            $"status={blocked.Status}; reason={blocked.ReasonCode}");
        Assert.Equal(1, rejecting.CaptureCount);
        Assert.Null(blocked.FirstCommitReceipt);

        var occurrence = new SqlitePlanOccurrenceRepository(context.Fixture.Store).Get(context.Slot.OccurrenceId!);
        var run = new SqliteRecordingRunRepository(context.Fixture.Store).Get(blocked.RunId!);
        var use = new SqliteRecurringLeaseUseAccountingReader(context.Fixture.Store).TryGetByOccurrence(context.Slot.OccurrenceIdentity);
        Assert.Equal(PlanOccurrenceStatus.Blocked, occurrence.Status);
        Assert.Equal(RecordingRunStatus.Failed, run.Status);
        Assert.False(run.HasCrossedStartCommit);
        Assert.Equal(run.TerminalReasonCode, occurrence.TerminalReasonCode);
        Assert.NotNull(use);
        Assert.Equal(LeaseUseStatus.Available, use!.Status);
        Assert.Equal(TimeSpan.Zero, use.ReservedDuration);

        var replayProvider = new CountingEnvironmentProvider(new ThrowingEnvironmentProvider());
        var replay = new RecurringOccurrenceStartCommitService(context.Fixture.Store, () => context.Fixture.CreatedAt.AddHours(1), replayProvider)
            .Commit(context.Lease.LeaseId, context.Slot.OccurrenceIdentity);
        Assert.Equal(RecurringOccurrenceStartCommitStatus.AlreadyBlocked, replay.Status);
        Assert.False(blocked.Succeeded);
        Assert.False(blocked.CanIssueProof);
        Assert.False(replay.Succeeded);
        Assert.False(replay.CanIssueProof);
        Assert.Null(replay.FirstCommitReceipt);
        Assert.Equal(0, replayProvider.CaptureCount);

        var reservationReplay = new RecurringOccurrenceReservationService(
            context.Fixture.Store,
            () => context.Fixture.CreatedAt.AddHours(1),
            () => "task265-no-second-run",
            () => "task265-no-second-use")
            .Reserve(context.Lease.LeaseId, context.Slot.OccurrenceIdentity);
        Assert.Equal(RecurringOccurrenceReservationStatus.AlreadyAdvanced, reservationReplay.Status);
        Assert.Equal(RecurringOccurrenceReservationReasonCodes.AlreadyAdvanced, reservationReplay.ReasonCode);
    }

    [Fact]
    public void FailureAfterPreparingUpdateRollsBackTheEntireChain()
    {
        using var context = RecurringOccurrenceReservationTests.ReservationContext.Create();
        _ = Reserve(context, "task265-run-rollback", "task265-use-rollback");
        var result = new RecurringOccurrenceStartCommitService(
            context.Fixture.Store,
            () => context.Fixture.CreatedAt,
            new RecurringOccurrenceReservationTests.MatchingReservationEnvironmentProvider(),
            point =>
            {
                if (point == RecurringOccurrenceStartCommitFailurePoint.AfterRunPreparingUpdate)
                    throw new InvalidOperationException("injected");
            }).Commit(context.Lease.LeaseId, context.Slot.OccurrenceIdentity);

        Assert.Equal(RecurringOccurrenceStartCommitStatus.Rejected, result.Status);
        var occurrence = new SqlitePlanOccurrenceRepository(context.Fixture.Store).Get(context.Slot.OccurrenceId!);
        var run = new SqliteRecordingRunRepository(context.Fixture.Store).Get(result.RunId ?? "task265-run-rollback");
        var use = new SqliteRecurringLeaseUseAccountingReader(context.Fixture.Store).TryGetByOccurrence(context.Slot.OccurrenceIdentity);
        Assert.Equal(PlanOccurrenceStatus.RunCreated, occurrence.Status);
        Assert.Equal(RecordingRunStatus.Created, run.Status);
        Assert.Equal(0, run.Version);
        Assert.NotNull(use);
        Assert.Equal(LeaseUseStatus.Reserved, use!.Status);
        Assert.Equal(0L, Convert.ToInt64(context.Fixture.Scalar(
            "SELECT version FROM recurring_lease_uses WHERE occurrence_identity = $identity;",
            ("$identity", context.Slot.OccurrenceIdentity))));
    }

    [Fact]
    public void ReceiptLivesInCoreAndOnlyFirstCommitCanIssueProof()
    {
        var persistenceAssembly = typeof(RecurringOccurrenceStartCommitService).Assembly;
        var coreAssembly = typeof(PlanDefinition).Assembly;

        Assert.Equal(coreAssembly, typeof(RecurringStartCommitReceipt).Assembly);
        Assert.NotEqual(persistenceAssembly, typeof(RecurringStartCommitReceipt).Assembly);
        Assert.DoesNotContain(
            typeof(RecurringStartCommitReceipt).GetProperties(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic),
            property => property.PropertyType.Assembly == persistenceAssembly);

        foreach (var status in Enum.GetValues<RecurringOccurrenceStartCommitStatus>())
        {
            var result = RecurringOccurrenceStartCommitResult.Create(status, "test", "occurrence");
            Assert.Equal(status is RecurringOccurrenceStartCommitStatus.Committed or
                RecurringOccurrenceStartCommitStatus.AlreadyCommitted or
                RecurringOccurrenceStartCommitStatus.AlreadyAdvanced, result.Succeeded);
            Assert.False(result.CanIssueProof);
        }
    }

    [Theory]
    [InlineData("recurring_start_commit_lease_expired", true, false)]
    [InlineData("execution_environment_mismatch", true, false)]
    [InlineData("recurring_start_commit_duration_exceeds_planned_end", true, false)]
    [InlineData("recurring_start_commit_current_time_invalid", false, false)]
    [InlineData("recurring_start_commit_persisted_snapshot_invalid", false, false)]
    [InlineData("capture_output_invalid", false, true)]
    [InlineData("session_interrupted", false, true)]
    [InlineData("unknown-task265rr-reason", false, false)]
    public void PreStartAndPostStartReplayReasonPoliciesStayDisjointAndClosed(
        string reason,
        bool expectedPreStart,
        bool expectedPostStart)
    {
        Assert.Equal(expectedPreStart, RecurringPreStartBlockReasonPolicy.IsKnown(reason));
        Assert.Equal(expectedPostStart, RecurringLifecycleTerminalReasonPolicy.IsKnownPostStart(reason));
    }

    [Fact]
    public void DurationBoundaryGuardKeepsPlannedEndAndLeaseReasonsTyped()
    {
        var now = RecurringOccurrenceReservationTests.ReservationContext.DefaultCreatedAt;
        var duration = TimeSpan.FromMinutes(2);

        Assert.Equal(
            RecurringOccurrenceStartCommitReasonCodes.DurationExceedsPlannedEnd,
            SqliteRecurringOccurrenceStartCommitTransaction.EvaluateDurationBoundary(
                now, duration, now.Add(duration).AddTicks(-1), now.AddHours(1)));
        Assert.Equal(
            RecurringOccurrenceStartCommitReasonCodes.DurationExceedsLease,
            SqliteRecurringOccurrenceStartCommitTransaction.EvaluateDurationBoundary(
                now, duration, now.AddHours(1), now.Add(duration).AddTicks(-1)));
        Assert.Equal(
            RecurringOccurrenceStartCommitReasonCodes.CurrentTimeInvalid,
            SqliteRecurringOccurrenceStartCommitTransaction.EvaluateDurationBoundary(
                DateTimeOffset.MaxValue, duration, DateTimeOffset.MaxValue, DateTimeOffset.MaxValue));
    }

    [Fact]
    public async Task ConcurrentIdenticalRequestsConvergeToOneCommitAndOneProviderCall()
    {
        using var context = RecurringOccurrenceReservationTests.ReservationContext.Create();
        _ = Reserve(context, "task265r-run-concurrent", "task265r-use-concurrent");
        var providerA = new CountingEnvironmentProvider(new RecurringOccurrenceReservationTests.MatchingReservationEnvironmentProvider());
        var providerB = new CountingEnvironmentProvider(new RecurringOccurrenceReservationTests.MatchingReservationEnvironmentProvider());
        var serviceA = new RecurringOccurrenceStartCommitService(context.Fixture.Store, () => context.Fixture.CreatedAt, providerA);
        var serviceB = new RecurringOccurrenceStartCommitService(context.Fixture.Store, () => context.Fixture.CreatedAt, providerB);

        var results = await Task.WhenAll(
            Task.Run(() => serviceA.Commit(context.Lease.LeaseId, context.Slot.OccurrenceIdentity)),
            Task.Run(() => serviceB.Commit(context.Lease.LeaseId, context.Slot.OccurrenceIdentity)));

        Assert.Single(results, result => result.Status == RecurringOccurrenceStartCommitStatus.Committed);
        Assert.Single(results, result => result.Status == RecurringOccurrenceStartCommitStatus.AlreadyCommitted);
        Assert.Single(results, result => result.FirstCommitReceipt is not null);
        Assert.Single(results, result => result.FirstCommitReceipt is null && !result.CanIssueProof);
        Assert.Equal(1, providerA.CaptureCount + providerB.CaptureCount);
    }

    [Theory]
    [InlineData("started_unknown", "started_unknown", "backend_start_failed_before_first_frame")]
    [InlineData("session_interrupted", "started_unknown", "session_interrupted")]
    [InlineData("failed", "started_unknown", "capture_output_invalid")]
    public void AbnormalAdvancedReplayIsReadOnlyAndUsesClosedReasonPolicy(
        string runStatus,
        string useStatus,
        string reason)
    {
        using var context = RecurringOccurrenceReservationTests.ReservationContext.Create();
        var reservation = Reserve(context, "task265r-run-abnormal", "task265r-use-abnormal");
        ApplyAdvancedClaim(context, reservation, runStatus, useStatus, reason, reason);
        var before = CaptureClaimProjection(context, reservation.RunId!, reservation.UseId!);
        var provider = new CountingEnvironmentProvider(new ThrowingEnvironmentProvider());

        var result = new RecurringOccurrenceStartCommitService(
            context.Fixture.Store,
            () => context.Fixture.CreatedAt.AddHours(1),
            provider).Commit(context.Lease.LeaseId, context.Slot.OccurrenceIdentity);

        Assert.Equal(RecurringOccurrenceStartCommitStatus.AlreadyAdvanced, result.Status);
        Assert.True(result.Succeeded);
        Assert.False(result.CanIssueProof);
        Assert.Null(result.FirstCommitReceipt);
        Assert.Equal(0, provider.CaptureCount);
        Assert.Equal(before, CaptureClaimProjection(context, reservation.RunId!, reservation.UseId!));
    }

    [Fact]
    public void SettledAdvancedReplayIsReadOnlyAndDoesNotIssueReceipt()
    {
        using var context = RecurringOccurrenceReservationTests.ReservationContext.Create();
        var reservation = Reserve(context, "task265r-run-settled", "task265r-use-settled");
        ApplyAdvancedClaim(
            context,
            reservation,
            "settled",
            "settled",
            runReason: null,
            occurrenceReason: null,
            occurrenceStatus: "completed",
            actualSettledDuration: TimeSpan.FromMinutes(1));
        var before = CaptureClaimProjection(context, reservation.RunId!, reservation.UseId!);
        var provider = new CountingEnvironmentProvider(new ThrowingEnvironmentProvider());

        var result = new RecurringOccurrenceStartCommitService(
            context.Fixture.Store,
            () => context.Fixture.CreatedAt.AddHours(1),
            provider).Commit(context.Lease.LeaseId, context.Slot.OccurrenceIdentity);

        Assert.Equal(RecurringOccurrenceStartCommitStatus.AlreadyAdvanced, result.Status);
        Assert.Null(result.FirstCommitReceipt);
        Assert.Equal(0, provider.CaptureCount);
        Assert.Equal(before, CaptureClaimProjection(context, reservation.RunId!, reservation.UseId!));
    }

    [Theory]
    [InlineData("unknown_terminal_reason", "unknown_terminal_reason", "started_unknown")]
    [InlineData("session_interrupted", "safety_stop", "started_unknown")]
    [InlineData("session_interrupted", "session_interrupted", "consumed")]
    public void UnknownOrInconsistentAdvancedClaimIsRejectedWithoutProviderOrWrites(
        string runReason,
        string occurrenceReason,
        string useStatus)
    {
        using var context = RecurringOccurrenceReservationTests.ReservationContext.Create();
        var reservation = Reserve(context, "task265r-run-invalid-advanced", "task265r-use-invalid-advanced");
        ApplyAdvancedClaim(context, reservation, "session_interrupted", useStatus, runReason, occurrenceReason);
        var before = CaptureClaimProjection(context, reservation.RunId!, reservation.UseId!);
        var provider = new CountingEnvironmentProvider(new ThrowingEnvironmentProvider());

        var result = new RecurringOccurrenceStartCommitService(
            context.Fixture.Store,
            () => context.Fixture.CreatedAt.AddHours(1),
            provider).Commit(context.Lease.LeaseId, context.Slot.OccurrenceIdentity);

        Assert.Equal(RecurringOccurrenceStartCommitStatus.Rejected, result.Status);
        Assert.Null(result.FirstCommitReceipt);
        Assert.Equal(0, provider.CaptureCount);
        Assert.Equal(before, CaptureClaimProjection(context, reservation.RunId!, reservation.UseId!));
    }

    [Theory]
    [InlineData("AfterRunPreparingUpdate")]
    [InlineData("AfterRunStartCommitUpdate")]
    [InlineData("AfterUseUpdate")]
    [InlineData("AfterLeaseExhaustionUpdate")]
    [InlineData("BeforeFinalReadback")]
    [InlineData("BeforeCommit")]
    public void EverySuccessFailurePointRollsBackToTheReservationChain(
        string failurePointName)
    {
        var failurePoint = Enum.Parse<RecurringOccurrenceStartCommitFailurePoint>(failurePointName);
        using var context = RecurringOccurrenceReservationTests.ReservationContext.Create(maxUses: 1);
        var reservation = Reserve(context, "task265r-run-success-rollback", "task265r-use-success-rollback");
        var before = CaptureClaimProjection(context, reservation.RunId!, reservation.UseId!);
        var provider = new CountingEnvironmentProvider(new RecurringOccurrenceReservationTests.MatchingReservationEnvironmentProvider());

        var result = new RecurringOccurrenceStartCommitService(
            context.Fixture.Store,
            () => context.Fixture.CreatedAt,
            provider,
            point =>
            {
                if (point == failurePoint)
                    throw new InvalidOperationException("task265r injected success failure");
            }).Commit(context.Lease.LeaseId, context.Slot.OccurrenceIdentity);

        Assert.Equal(RecurringOccurrenceStartCommitStatus.Rejected, result.Status);
        Assert.Equal(1, provider.CaptureCount);
        Assert.Equal(before, CaptureClaimProjection(context, reservation.RunId!, reservation.UseId!));
    }

    [Theory]
    [InlineData("AfterBlockedRunUpdate")]
    [InlineData("AfterBlockedUseUpdate")]
    [InlineData("AfterBlockedOccurrenceUpdate")]
    [InlineData("BeforeFinalReadback")]
    [InlineData("BeforeCommit")]
    public void EveryBlockedFailurePointRollsBackToTheReservationChain(
        string failurePointName)
    {
        var failurePoint = Enum.Parse<RecurringOccurrenceStartCommitFailurePoint>(failurePointName);
        using var context = RecurringOccurrenceReservationTests.ReservationContext.Create();
        var reservation = Reserve(context, "task265r-run-blocked-rollback", "task265r-use-blocked-rollback");
        var before = CaptureClaimProjection(context, reservation.RunId!, reservation.UseId!);
        var provider = new CountingEnvironmentProvider(new RejectingEnvironmentProvider());

        var result = new RecurringOccurrenceStartCommitService(
            context.Fixture.Store,
            () => context.Fixture.CreatedAt,
            provider,
            point =>
            {
                if (point == failurePoint)
                    throw new InvalidOperationException("task265r injected blocked failure");
            }).Commit(context.Lease.LeaseId, context.Slot.OccurrenceIdentity);

        Assert.Equal(RecurringOccurrenceStartCommitStatus.Rejected, result.Status);
        Assert.Equal(1, provider.CaptureCount);
        Assert.Equal(before, CaptureClaimProjection(context, reservation.RunId!, reservation.UseId!));
    }

    [Fact]
    public void PersistedTimestampRollbackIsRejectedWithZeroWritesAndNoProvider()
    {
        using var context = RecurringOccurrenceReservationTests.ReservationContext.Create();
        var reservation = Reserve(context, "task265r-run-time-rollback", "task265r-use-time-rollback");
        var before = CaptureClaimProjection(context, reservation.RunId!, reservation.UseId!);
        var provider = new CountingEnvironmentProvider(new ThrowingEnvironmentProvider());

        var result = new RecurringOccurrenceStartCommitService(
            context.Fixture.Store,
            () => context.Fixture.CreatedAt.AddTicks(-1),
            provider).Commit(context.Lease.LeaseId, context.Slot.OccurrenceIdentity);

        Assert.Equal(RecurringOccurrenceStartCommitStatus.Rejected, result.Status);
        Assert.Equal(RecurringOccurrenceStartCommitReasonCodes.CurrentTimeInvalid, result.ReasonCode);
        Assert.Equal(0, provider.CaptureCount);
        Assert.Equal(before, CaptureClaimProjection(context, reservation.RunId!, reservation.UseId!));
    }

    [Fact]
    public void NonUtcClockAndClockExceptionAreRejectedBeforeOpeningTheTransaction()
    {
        using var context = RecurringOccurrenceReservationTests.ReservationContext.Create();
        var reservation = Reserve(context, "task265r-run-clock", "task265r-use-clock");
        var before = CaptureClaimProjection(context, reservation.RunId!, reservation.UseId!);
        var provider = new CountingEnvironmentProvider(new ThrowingEnvironmentProvider());

        var nonUtc = new RecurringOccurrenceStartCommitService(
            context.Fixture.Store,
            () => new DateTimeOffset(context.Fixture.CreatedAt.DateTime, TimeSpan.FromHours(8)),
            provider).Commit(context.Lease.LeaseId, context.Slot.OccurrenceIdentity);
        var throwing = new RecurringOccurrenceStartCommitService(
            context.Fixture.Store,
            () => throw new InvalidOperationException("clock failure"),
            provider).Commit(context.Lease.LeaseId, context.Slot.OccurrenceIdentity);

        Assert.Equal(RecurringOccurrenceStartCommitStatus.Rejected, nonUtc.Status);
        Assert.Equal(RecurringOccurrenceStartCommitReasonCodes.ClockInvalid, nonUtc.ReasonCode);
        Assert.Equal(RecurringOccurrenceStartCommitStatus.Rejected, throwing.Status);
        Assert.Equal(RecurringOccurrenceStartCommitReasonCodes.ClockInvalid, throwing.ReasonCode);
        Assert.Equal(0, provider.CaptureCount);
        Assert.Equal(before, CaptureClaimProjection(context, reservation.RunId!, reservation.UseId!));
    }

    [Fact]
    public void SameServiceClockRollbackIsRejectedReadOnlyAfterAnEstablishedWatermark()
    {
        using var context = RecurringOccurrenceReservationTests.ReservationContext.Create();
        var reservation = Reserve(context, "task265rr-run-clock", "task265rr-use-clock");
        var provider = new CountingEnvironmentProvider(new RecurringOccurrenceReservationTests.MatchingReservationEnvironmentProvider());
        var samples = new Queue<DateTimeOffset>(new[]
        {
            context.Fixture.CreatedAt,
            context.Fixture.CreatedAt.AddTicks(-1),
        });
        var service = new RecurringOccurrenceStartCommitService(
            context.Fixture.Store,
            () => samples.Dequeue(),
            provider);

        var first = service.Commit(context.Lease.LeaseId, context.Slot.OccurrenceIdentity);
        var beforeRollback = CaptureClaimProjection(context, reservation.RunId!, reservation.UseId!);
        var second = service.Commit(context.Lease.LeaseId, context.Slot.OccurrenceIdentity);

        Assert.Equal(RecurringOccurrenceStartCommitStatus.Committed, first.Status);
        Assert.Equal(RecurringOccurrenceStartCommitStatus.Rejected, second.Status);
        Assert.Equal(RecurringOccurrenceStartCommitReasonCodes.ClockMovedBackwards, second.ReasonCode);
        Assert.Equal(1, provider.CaptureCount);
        Assert.Equal(beforeRollback, CaptureClaimProjection(context, reservation.RunId!, reservation.UseId!));
    }

    [Fact]
    public void LeaseExpiredIsTypedBlockedAndReleasesTheClaimWithoutProviderCall()
    {
        using var context = RecurringOccurrenceReservationTests.ReservationContext.Create();
        var reservation = Reserve(context, "task265rr-run-expired", "task265rr-use-expired");
        context.Fixture.Execute(
            "DROP TRIGGER trg_recurring_consent_leases_lifecycle_update; " +
            "UPDATE recurring_consent_leases SET status_code = 'expired', updated_at_utc = $at, version = version + 1 WHERE lease_id = $leaseId;",
            ("$at", context.Lease.ValidUntilUtc.UtcDateTime.Ticks),
            ("$leaseId", context.Lease.LeaseId));
        var provider = new CountingEnvironmentProvider(new ThrowingEnvironmentProvider());

        var result = new RecurringOccurrenceStartCommitService(
            context.Fixture.Store,
            () => context.Lease.ValidUntilUtc,
            provider).Commit(context.Lease.LeaseId, context.Slot.OccurrenceIdentity);

        Assert.Equal(RecurringOccurrenceStartCommitStatus.Blocked, result.Status);
        Assert.Equal(RecurringOccurrenceStartCommitReasonCodes.LeaseExpired, result.ReasonCode);
        Assert.False(result.Succeeded);
        Assert.False(result.CanIssueProof);
        Assert.Equal(0, provider.CaptureCount);
        Assert.Equal(PlanOccurrenceStatus.Blocked, new SqlitePlanOccurrenceRepository(context.Fixture.Store).Get(context.Slot.OccurrenceId!).Status);
        Assert.Equal(LeaseUseStatus.Available,
            new SqliteRecurringLeaseUseAccountingReader(context.Fixture.Store).TryGetByOccurrence(context.Slot.OccurrenceIdentity)!.Status);
    }

    [Fact]
    public void StopAllAtApprovalBoundaryIsTypedBlockedAndDoesNotCallProvider()
    {
        using var context = RecurringOccurrenceReservationTests.ReservationContext.Create();
        var reservation = Reserve(context, "task265rr-run-stop-all", "task265rr-use-stop-all");
        context.Fixture.Execute(
            "UPDATE unattended_safety_state SET stop_all_applied = 1, stop_all_operation_id = 'task265rr-stop-all', stop_all_reason_code = 'task265rr-test', stop_all_requested_at_utc = $at, stop_all_applied_at_utc = $at, version = version + 1 WHERE state_id = 'global';",
            ("$at", context.Fixture.CreatedAt.UtcDateTime.Ticks));
        var provider = new CountingEnvironmentProvider(new ThrowingEnvironmentProvider());

        var result = new RecurringOccurrenceStartCommitService(
            context.Fixture.Store,
            () => context.Fixture.CreatedAt,
            provider).Commit(context.Lease.LeaseId, context.Slot.OccurrenceIdentity);

        Assert.Equal(RecurringOccurrenceStartCommitStatus.Blocked, result.Status);
        Assert.Equal(RecurringOccurrenceStartCommitReasonCodes.StopAllBoundary, result.ReasonCode);
        Assert.False(result.Succeeded);
        Assert.Equal(0, provider.CaptureCount);
        Assert.Equal(PlanOccurrenceStatus.Blocked, new SqlitePlanOccurrenceRepository(context.Fixture.Store).Get(context.Slot.OccurrenceId!).Status);
        Assert.Equal(LeaseUseStatus.Available,
            new SqliteRecurringLeaseUseAccountingReader(context.Fixture.Store).TryGetByOccurrence(context.Slot.OccurrenceIdentity)!.Status);
    }

    [Theory]
    [InlineData("missing-use")]
    [InlineData("missing-run")]
    [InlineData("wrong-pairing")]
    [InlineData("half-chain")]
    public void MissingOrHalfClaimChainIsPersistedInvalidAndReadOnly(string corruption)
    {
        using var context = RecurringOccurrenceReservationTests.ReservationContext.Create();
        var reservation = Reserve(context, "task265rr-run-half-" + corruption, "task265rr-use-half-" + corruption);
        ApplyClaimTopologyCorruption(context, reservation, corruption);
        var before = CaptureClaimProjection(context, reservation.RunId!, reservation.UseId!);
        var provider = new CountingEnvironmentProvider(new ThrowingEnvironmentProvider());

        var result = new RecurringOccurrenceStartCommitService(
            context.Fixture.Store,
            () => context.Fixture.CreatedAt,
            provider).Commit(context.Lease.LeaseId, context.Slot.OccurrenceIdentity);

        Assert.Equal(RecurringOccurrenceStartCommitStatus.Rejected, result.Status);
        Assert.Equal(RecurringOccurrenceStartCommitReasonCodes.PersistedSnapshotInvalid, result.ReasonCode);
        Assert.False(result.Succeeded);
        Assert.Equal(0, provider.CaptureCount);
        Assert.Equal(before, CaptureClaimProjection(context, reservation.RunId!, reservation.UseId!));
    }

    [Theory]
    [InlineData("run-created")]
    [InlineData("media")]
    [InlineData("bundle")]
    [InlineData("occurrence-reason")]
    [InlineData("actual")]
    [InlineData("max-version")]
    public void ExactCommittedCorruptionCannotBecomeAlreadyCommitted(string corruption)
    {
        using var context = RecurringOccurrenceReservationTests.ReservationContext.Create();
        var reservation = Reserve(context, "task265rr-run-committed-" + corruption, "task265rr-use-committed-" + corruption);
        var first = new RecurringOccurrenceStartCommitService(
            context.Fixture.Store,
            () => context.Fixture.CreatedAt,
            new RecurringOccurrenceReservationTests.MatchingReservationEnvironmentProvider())
            .Commit(context.Lease.LeaseId, context.Slot.OccurrenceIdentity);
        Assert.Equal(RecurringOccurrenceStartCommitStatus.Committed, first.Status);
        ApplyCommittedCorruption(context, reservation, corruption);
        var before = CaptureClaimProjection(context, reservation.RunId!, reservation.UseId!);
        var provider = new CountingEnvironmentProvider(new ThrowingEnvironmentProvider());

        var replay = new RecurringOccurrenceStartCommitService(context.Fixture.Store, () => context.Fixture.CreatedAt, provider)
            .Commit(context.Lease.LeaseId, context.Slot.OccurrenceIdentity);

        Assert.Equal(RecurringOccurrenceStartCommitStatus.Rejected, replay.Status);
        Assert.Equal(RecurringOccurrenceStartCommitReasonCodes.PersistedSnapshotInvalid, replay.ReasonCode);
        Assert.False(replay.Succeeded);
        Assert.False(replay.CanIssueProof);
        Assert.Equal(0, provider.CaptureCount);
        Assert.Equal(before, CaptureClaimProjection(context, reservation.RunId!, reservation.UseId!));
    }

    [Theory]
    [InlineData("unknown")]
    [InlineData("rejected")]
    [InlineData("post-start")]
    [InlineData("timestamp")]
    [InlineData("media")]
    [InlineData("bundle")]
    [InlineData("version")]
    [InlineData("max-version")]
    public void PreStartBlockedReplayUsesTheSharedClosedShapeForStartCommitAndReservation(string corruption)
    {
        using var startContext = RecurringOccurrenceReservationTests.ReservationContext.Create();
        var startReservation = Reserve(startContext, "task265rr-run-blocked-" + corruption, "task265rr-use-blocked-" + corruption);
        var blocked = new RecurringOccurrenceStartCommitService(
            startContext.Fixture.Store,
            () => startContext.Fixture.CreatedAt,
            new RejectingEnvironmentProvider())
            .Commit(startContext.Lease.LeaseId, startContext.Slot.OccurrenceIdentity);
        Assert.Equal(RecurringOccurrenceStartCommitStatus.Blocked, blocked.Status);
        ApplyPreStartBlockedCorruption(startContext, startReservation, corruption);
        var startBefore = CaptureClaimProjection(startContext, startReservation.RunId!, startReservation.UseId!);
        var startProvider = new CountingEnvironmentProvider(new ThrowingEnvironmentProvider());
        var startReplay = new RecurringOccurrenceStartCommitService(startContext.Fixture.Store, () => startContext.Fixture.CreatedAt, startProvider)
            .Commit(startContext.Lease.LeaseId, startContext.Slot.OccurrenceIdentity);

        Assert.Equal(RecurringOccurrenceStartCommitStatus.Rejected, startReplay.Status);
        Assert.Equal(RecurringOccurrenceStartCommitReasonCodes.PersistedSnapshotInvalid, startReplay.ReasonCode);
        Assert.Equal(0, startProvider.CaptureCount);
        Assert.Equal(startBefore, CaptureClaimProjection(startContext, startReservation.RunId!, startReservation.UseId!));

        using var reservationContext = RecurringOccurrenceReservationTests.ReservationContext.Create();
        var reservation = Reserve(reservationContext, "task265rr-run-reservation-blocked-" + corruption, "task265rr-use-reservation-blocked-" + corruption);
        var reservationBlocked = new RecurringOccurrenceStartCommitService(
            reservationContext.Fixture.Store,
            () => reservationContext.Fixture.CreatedAt,
            new RejectingEnvironmentProvider())
            .Commit(reservationContext.Lease.LeaseId, reservationContext.Slot.OccurrenceIdentity);
        Assert.Equal(RecurringOccurrenceStartCommitStatus.Blocked, reservationBlocked.Status);
        ApplyPreStartBlockedCorruption(reservationContext, reservation, corruption);
        var reservationBefore = CaptureClaimProjection(reservationContext, reservation.RunId!, reservation.UseId!);
        var reservationReplay = new RecurringOccurrenceReservationService(
            reservationContext.Fixture.Store,
            () => reservationContext.Fixture.CreatedAt,
            () => "task265rr-no-new-run",
            () => "task265rr-no-new-use")
            .Reserve(reservationContext.Lease.LeaseId, reservationContext.Slot.OccurrenceIdentity);

        Assert.Equal(RecurringOccurrenceReservationStatus.Rejected, reservationReplay.Status);
        Assert.Equal(RecurringOccurrenceReservationReasonCodes.PersistedSnapshotInvalid, reservationReplay.ReasonCode);
        Assert.False(reservationReplay.Succeeded);
        Assert.Equal(reservationBefore, CaptureClaimProjection(reservationContext, reservation.RunId!, reservation.UseId!));
    }

    [Theory]
    [InlineData("timestamp")]
    [InlineData("media")]
    [InlineData("reason")]
    [InlineData("version")]
    [InlineData("max-version")]
    public void AdvancedReplayCorruptionIsRejectedBeforeProvider(string corruption)
    {
        using var context = RecurringOccurrenceReservationTests.ReservationContext.Create();
        var reservation = Reserve(context, "task265rr-run-advanced-" + corruption, "task265rr-use-advanced-" + corruption);
        ApplyAdvancedClaim(context, reservation, "recording", "consumed", null, null, occurrenceStatus: "run_created");
        ApplyAdvancedCorruption(context, reservation, corruption);
        var before = CaptureClaimProjection(context, reservation.RunId!, reservation.UseId!);
        var provider = new CountingEnvironmentProvider(new ThrowingEnvironmentProvider());

        var result = new RecurringOccurrenceStartCommitService(context.Fixture.Store, () => context.Fixture.CreatedAt.AddMinutes(10), provider)
            .Commit(context.Lease.LeaseId, context.Slot.OccurrenceIdentity);

        Assert.Equal(RecurringOccurrenceStartCommitStatus.Rejected, result.Status);
        Assert.Equal(RecurringOccurrenceStartCommitReasonCodes.PersistedSnapshotInvalid, result.ReasonCode);
        Assert.Equal(0, provider.CaptureCount);
        Assert.Equal(before, CaptureClaimProjection(context, reservation.RunId!, reservation.UseId!));
    }

    [Fact]
    public void ReenableFreshnessUsesApprovalTimestampAndBlocksStaleApproval()
    {
        using var context = RecurringOccurrenceReservationTests.ReservationContext.Create();
        var reservation = Reserve(context, "task265r-run-stale-approval", "task265r-use-stale-approval");
        var safety = new SqliteStandingLeaseSafetyControlTransaction(context.Fixture.Store);
        safety.SetUnattendedMode("task265r-disable", false, "task265r-test", context.Fixture.CreatedAt.AddTicks(1));
        safety.SetUnattendedMode("task265r-reenable", true, "task265r-test", context.Fixture.CreatedAt.AddTicks(2));
        var provider = new CountingEnvironmentProvider(new ThrowingEnvironmentProvider());

        var result = new RecurringOccurrenceStartCommitService(
            context.Fixture.Store,
            () => context.Fixture.CreatedAt.AddMinutes(1),
            provider).Commit(context.Lease.LeaseId, context.Slot.OccurrenceIdentity);

        Assert.Equal(RecurringOccurrenceStartCommitStatus.Blocked, result.Status);
        Assert.Equal(RecurringOccurrenceStartCommitReasonCodes.ReenableRequiresNewAuthorization, result.ReasonCode);
        Assert.Equal(0, provider.CaptureCount);
    }

    [Fact]
    public void BeforeLatestStartIsBlockedButBeforeScheduledStartIsRejected()
    {
        using var lateContext = RecurringOccurrenceReservationTests.ReservationContext.Create();
        var lateReservation = Reserve(lateContext, "task265r-run-late", "task265r-use-late");
        var lateProvider = new CountingEnvironmentProvider(new ThrowingEnvironmentProvider());
        var late = new RecurringOccurrenceStartCommitService(
            lateContext.Fixture.Store,
            () => lateContext.Slot.LatestStartUtc!.Value.AddTicks(1),
            lateProvider).Commit(lateContext.Lease.LeaseId, lateContext.Slot.OccurrenceIdentity);

        Assert.Equal(RecurringOccurrenceStartCommitStatus.Blocked, late.Status);
        Assert.Equal(RecurringOccurrenceStartCommitReasonCodes.AfterLatestStart, late.ReasonCode);
        Assert.Equal(0, lateProvider.CaptureCount);
        Assert.Equal(PlanOccurrenceStatus.Blocked, new SqlitePlanOccurrenceRepository(lateContext.Fixture.Store).Get(lateContext.Slot.OccurrenceId!).Status);

        using var earlyContext = RecurringOccurrenceReservationTests.ReservationContext.Create();
        var earlyReservation = Reserve(earlyContext, "task265r-run-early", "task265r-use-early");
        var earlyProvider = new CountingEnvironmentProvider(new ThrowingEnvironmentProvider());
        var early = new RecurringOccurrenceStartCommitService(
            earlyContext.Fixture.Store,
            () => earlyContext.Slot.ScheduledStartUtc!.Value.AddTicks(-1),
            earlyProvider).Commit(earlyContext.Lease.LeaseId, earlyContext.Slot.OccurrenceIdentity);

        Assert.Equal(RecurringOccurrenceStartCommitStatus.Rejected, early.Status);
        Assert.Contains(
            early.ReasonCode,
            new[]
            {
                RecurringOccurrenceStartCommitReasonCodes.BeforeScheduledStart,
                RecurringOccurrenceStartCommitReasonCodes.CurrentTimeInvalid,
            });
        Assert.Equal(0, earlyProvider.CaptureCount);
        Assert.Equal(PlanOccurrenceStatus.RunCreated, new SqlitePlanOccurrenceRepository(earlyContext.Fixture.Store).Get(earlyContext.Slot.OccurrenceId!).Status);
    }

    [Theory]
    [InlineData("throw", RecurringOccurrenceStartCommitReasonCodes.EnvironmentUnavailable)]
    [InlineData("null", RecurringOccurrenceStartCommitReasonCodes.EnvironmentUnavailable)]
    [InlineData("time", RecurringOccurrenceStartCommitReasonCodes.EnvironmentUnavailable)]
    [InlineData("interactive", "execution_environment_mismatch")]
    [InlineData("sid", "execution_environment_mismatch")]
    [InlineData("session", "execution_environment_mismatch")]
    [InlineData("display-identity", "execution_display_identity_unavailable")]
    [InlineData("display-missing", "execution_display_missing")]
    [InlineData("display-ambiguous", "execution_display_ambiguous")]
    [InlineData("display-metadata", "execution_display_metadata_unavailable")]
    [InlineData("display-mismatch", "execution_display_metadata_mismatch")]
    [InlineData("topology-missing", "execution_topology_unavailable")]
    [InlineData("topology", "execution_topology_mismatch")]
    [InlineData("output-missing", "execution_output_environment_unavailable")]
    [InlineData("output-directory", "execution_output_directory_mismatch")]
    [InlineData("output-file", "execution_output_file_path_mismatch")]
    [InlineData("output-directory-unavailable", "execution_output_directory_unavailable")]
    [InlineData("output-unwritable", "execution_output_directory_unwritable")]
    [InlineData("output-exists", "execution_output_file_exists")]
    [InlineData("disk-unavailable", "execution_disk_space_unavailable")]
    [InlineData("disk-insufficient", "execution_disk_space_insufficient")]
    public void ProviderAndEnvironmentFailureCategoriesBlockAndRelease(string category, string expectedReason)
    {
        using var context = RecurringOccurrenceReservationTests.ReservationContext.Create();
        var reservation = Reserve(context, "task265r-run-environment", "task265r-use-environment");
        var provider = new CountingEnvironmentProvider(CreateFailureProvider(category));

        var result = new RecurringOccurrenceStartCommitService(
            context.Fixture.Store,
            () => context.Fixture.CreatedAt,
            provider).Commit(context.Lease.LeaseId, context.Slot.OccurrenceIdentity);

        Assert.Equal(RecurringOccurrenceStartCommitStatus.Blocked, result.Status);
        Assert.Equal(expectedReason, result.ReasonCode);
        Assert.False(result.CanIssueProof);
        Assert.Equal(1, provider.CaptureCount);
        Assert.Equal(PlanOccurrenceStatus.Blocked, new SqlitePlanOccurrenceRepository(context.Fixture.Store).Get(context.Slot.OccurrenceId!).Status);
        Assert.Equal(LeaseUseStatus.Available,
            new SqliteRecurringLeaseUseAccountingReader(context.Fixture.Store).TryGetByOccurrence(context.Slot.OccurrenceIdentity)!.Status);
    }

    [Fact]
    public void PausedPlanRevokedLeaseAndDisabledSafetyAreBlockedBeforeProvider()
    {
        using var paused = RecurringOccurrenceReservationTests.ReservationContext.Create();
        var pausedReservation = Reserve(paused, "task265r-run-paused", "task265r-use-paused");
        var pausedPlan = new SqlitePlanDefinitionRepository(paused.Fixture.Store).Get(paused.Fixture.PlanId);
        Assert.True(pausedPlan.TryTransition(PlanDefinitionStatus.Paused, paused.Fixture.CreatedAt.AddTicks(1)).Succeeded);
        new SqlitePlanDefinitionRepository(paused.Fixture.Store).Update(pausedPlan, pausedPlan.Version - 1);
        var pausedProvider = new CountingEnvironmentProvider(new ThrowingEnvironmentProvider());
        var pausedResult = new RecurringOccurrenceStartCommitService(paused.Fixture.Store, () => paused.Fixture.CreatedAt.AddMinutes(1), pausedProvider)
            .Commit(paused.Lease.LeaseId, paused.Slot.OccurrenceIdentity);

        Assert.Equal(RecurringOccurrenceStartCommitStatus.Blocked, pausedResult.Status);
        Assert.Equal(0, pausedProvider.CaptureCount);

        using var revoked = RecurringOccurrenceReservationTests.ReservationContext.Create();
        var revokedReservation = Reserve(revoked, "task265r-run-revoked", "task265r-use-revoked");
        revoked.Fixture.Execute(
            "DROP TRIGGER trg_recurring_consent_leases_lifecycle_update; UPDATE recurring_consent_leases SET status_code = 'revoked', updated_at_utc = $at, version = version + 1 WHERE lease_id = $leaseId;",
            ("$at", revoked.Fixture.CreatedAt.AddTicks(1).UtcDateTime.Ticks),
            ("$leaseId", revoked.Lease.LeaseId));
        var revokedProvider = new CountingEnvironmentProvider(new ThrowingEnvironmentProvider());
        var revokedResult = new RecurringOccurrenceStartCommitService(revoked.Fixture.Store, () => revoked.Fixture.CreatedAt.AddMinutes(1), revokedProvider)
            .Commit(revoked.Lease.LeaseId, revoked.Slot.OccurrenceIdentity);

        Assert.Equal(RecurringOccurrenceStartCommitStatus.Blocked, revokedResult.Status);
        Assert.Equal(RecurringOccurrenceStartCommitReasonCodes.LeaseRevoked, revokedResult.ReasonCode);
        Assert.Equal(0, revokedProvider.CaptureCount);

        using var disabled = RecurringOccurrenceReservationTests.ReservationContext.Create();
        var disabledReservation = Reserve(disabled, "task265r-run-disabled", "task265r-use-disabled");
        new SqliteStandingLeaseSafetyControlTransaction(disabled.Fixture.Store)
            .SetUnattendedMode("task265r-disable-safety", false, "task265r-test", disabled.Fixture.CreatedAt.AddTicks(1));
        var disabledProvider = new CountingEnvironmentProvider(new ThrowingEnvironmentProvider());
        var disabledResult = new RecurringOccurrenceStartCommitService(disabled.Fixture.Store, () => disabled.Fixture.CreatedAt.AddMinutes(1), disabledProvider)
            .Commit(disabled.Lease.LeaseId, disabled.Slot.OccurrenceIdentity);

        Assert.Equal(RecurringOccurrenceStartCommitStatus.Blocked, disabledResult.Status);
        Assert.Equal(RecurringOccurrenceStartCommitReasonCodes.UnattendedDisabled, disabledResult.ReasonCode);
        Assert.Equal(0, disabledProvider.CaptureCount);
    }

    [Fact]
    public void CorruptSpecificationIsRejectedBeforeProviderAndLeavesReservationUntouched()
    {
        using var context = RecurringOccurrenceReservationTests.ReservationContext.Create();
        var reservation = Reserve(context, "task265r-run-corrupt-spec", "task265r-use-corrupt-spec");
        var before = CaptureClaimProjection(context, reservation.RunId!, reservation.UseId!);
        context.Fixture.Execute(
            "DROP TRIGGER trg_recurring_occurrence_execution_specs_immutable_update; UPDATE recurring_occurrence_execution_specs SET specification_digest = $digest WHERE occurrence_identity = $identity;",
            ("$digest", "recurring-occurrence-execution-spec/v1:" + new string('a', 64)),
            ("$identity", context.Slot.OccurrenceIdentity));
        var provider = new CountingEnvironmentProvider(new ThrowingEnvironmentProvider());

        var result = new RecurringOccurrenceStartCommitService(context.Fixture.Store, () => context.Fixture.CreatedAt, provider)
            .Commit(context.Lease.LeaseId, context.Slot.OccurrenceIdentity);

        Assert.Equal(RecurringOccurrenceStartCommitStatus.Rejected, result.Status);
        Assert.Equal(0, provider.CaptureCount);
        Assert.Equal(before, CaptureClaimProjection(context, reservation.RunId!, reservation.UseId!));
    }

    private static RecurringOccurrenceReservationResult Reserve(
        RecurringOccurrenceReservationTests.ReservationContext context,
        string runId,
        string useId) =>
        new RecurringOccurrenceReservationService(
            context.Fixture.Store,
            () => context.Fixture.CreatedAt,
            () => runId,
            () => useId).Reserve(context.Lease.LeaseId, context.Slot.OccurrenceIdentity);

    private static void ApplyClaimTopologyCorruption(
        RecurringOccurrenceReservationTests.ReservationContext context,
        RecurringOccurrenceReservationResult reservation,
        string corruption)
    {
        switch (corruption)
        {
            case "missing-use":
                context.Fixture.Execute(
                    "DROP TRIGGER trg_recurring_lease_uses_immutable_delete; DELETE FROM recurring_lease_uses WHERE use_id = $useId;",
                    ("$useId", reservation.UseId!));
                break;
            case "missing-run":
                context.Fixture.Execute(
                    "PRAGMA foreign_keys = OFF; DELETE FROM recording_runs WHERE id = $runId;",
                    ("$runId", reservation.RunId!));
                break;
            case "wrong-pairing":
                context.Fixture.Execute(
                    "PRAGMA foreign_keys = OFF; DROP TRIGGER trg_recurring_lease_uses_lifecycle_update; UPDATE recurring_lease_uses SET run_id = 'task265rr-missing-pair-run' WHERE use_id = $useId;",
                    ("$useId", reservation.UseId!));
                break;
            case "half-chain":
                context.Fixture.Execute(
                    "UPDATE plan_occurrences SET run_id = NULL WHERE id = $occurrenceId;",
                    ("$occurrenceId", context.Slot.OccurrenceId!));
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(corruption));
        }
    }

    private static void ApplyCommittedCorruption(
        RecurringOccurrenceReservationTests.ReservationContext context,
        RecurringOccurrenceReservationResult reservation,
        string corruption)
    {
        switch (corruption)
        {
            case "run-created":
                context.Fixture.Execute(
                    "UPDATE recording_runs SET created_at_utc = created_at_utc - 1 WHERE id = $runId;",
                    ("$runId", reservation.RunId!));
                break;
            case "media":
                context.Fixture.Execute(
                    "UPDATE recording_runs SET media_artifact_id = 'task265rr-corrupt-media' WHERE id = $runId;",
                    ("$runId", reservation.RunId!));
                break;
            case "bundle":
                context.Fixture.Execute(
                    "UPDATE recording_runs SET bundle_id = 'task265rr-corrupt-bundle' WHERE id = $runId;",
                    ("$runId", reservation.RunId!));
                break;
            case "occurrence-reason":
                context.Fixture.Execute(
                    "UPDATE plan_occurrences SET terminal_reason_code = 'task265rr-illegal-reason' WHERE id = $occurrenceId;",
                    ("$occurrenceId", context.Slot.OccurrenceId!));
                break;
            case "actual":
                context.Fixture.Execute(
                    "DROP TRIGGER trg_recurring_lease_uses_lifecycle_update; PRAGMA ignore_check_constraints = ON; UPDATE recurring_lease_uses SET actual_settled_duration_ticks = 1 WHERE use_id = $useId;",
                    ("$useId", reservation.UseId!));
                break;
            case "max-version":
                context.Fixture.Execute(
                    "UPDATE recording_runs SET version = $version WHERE id = $runId;",
                    ("$version", long.MaxValue),
                    ("$runId", reservation.RunId!));
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(corruption));
        }
    }

    private static void ApplyPreStartBlockedCorruption(
        RecurringOccurrenceReservationTests.ReservationContext context,
        RecurringOccurrenceReservationResult reservation,
        string corruption)
    {
        switch (corruption)
        {
            case "unknown":
                UpdateBlockedReasons(context, reservation, "task265rr-unknown-pre-start-reason");
                break;
            case "rejected":
                UpdateBlockedReasons(context, reservation, RecurringOccurrenceStartCommitReasonCodes.CurrentTimeInvalid);
                break;
            case "post-start":
                UpdateBlockedReasons(context, reservation, "capture_output_invalid");
                break;
            case "timestamp":
                context.Fixture.Execute(
                    "UPDATE plan_occurrences SET updated_at_utc = updated_at_utc + 1 WHERE id = $occurrenceId;",
                    ("$occurrenceId", context.Slot.OccurrenceId!));
                break;
            case "media":
                context.Fixture.Execute(
                    "UPDATE recording_runs SET media_artifact_id = 'task265rr-corrupt-media' WHERE id = $runId;",
                    ("$runId", reservation.RunId!));
                break;
            case "bundle":
                context.Fixture.Execute(
                    "UPDATE recording_runs SET bundle_id = 'task265rr-corrupt-bundle' WHERE id = $runId;",
                    ("$runId", reservation.RunId!));
                break;
            case "version":
                context.Fixture.Execute(
                    "UPDATE recording_runs SET version = version + 1 WHERE id = $runId;",
                    ("$runId", reservation.RunId!));
                break;
            case "max-version":
                context.Fixture.Execute(
                    "UPDATE recording_runs SET version = $version WHERE id = $runId;",
                    ("$version", long.MaxValue),
                    ("$runId", reservation.RunId!));
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(corruption));
        }
    }

    private static void UpdateBlockedReasons(
        RecurringOccurrenceReservationTests.ReservationContext context,
        RecurringOccurrenceReservationResult reservation,
        string reason) =>
        context.Fixture.Execute(
            "UPDATE recording_runs SET terminal_reason_code = $reason WHERE id = $runId; UPDATE plan_occurrences SET terminal_reason_code = $reason WHERE id = $occurrenceId;",
            ("$reason", reason),
            ("$runId", reservation.RunId!),
            ("$occurrenceId", context.Slot.OccurrenceId!));

    private static void ApplyAdvancedCorruption(
        RecurringOccurrenceReservationTests.ReservationContext context,
        RecurringOccurrenceReservationResult reservation,
        string corruption)
    {
        switch (corruption)
        {
            case "timestamp":
                context.Fixture.Execute(
                    "UPDATE recurring_lease_uses SET updated_at_utc = updated_at_utc - 1 WHERE use_id = $useId;",
                    ("$useId", reservation.UseId!));
                break;
            case "media":
                context.Fixture.Execute(
                    "UPDATE recording_runs SET media_artifact_id = 'task265rr-corrupt-media' WHERE id = $runId;",
                    ("$runId", reservation.RunId!));
                break;
            case "reason":
                context.Fixture.Execute(
                    "UPDATE recording_runs SET terminal_reason_code = 'task265rr-illegal-normal-reason' WHERE id = $runId;",
                    ("$runId", reservation.RunId!));
                break;
            case "version":
                context.Fixture.Execute(
                    "UPDATE recording_runs SET version = 99 WHERE id = $runId;",
                    ("$runId", reservation.RunId!));
                break;
            case "max-version":
                context.Fixture.Execute(
                    "UPDATE recording_runs SET version = $version WHERE id = $runId;",
                    ("$version", long.MaxValue),
                    ("$runId", reservation.RunId!));
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(corruption));
        }
    }

    private static void ApplyAdvancedClaim(
        RecurringOccurrenceReservationTests.ReservationContext context,
        RecurringOccurrenceReservationResult reservation,
        string runStatus,
        string useStatus,
        string? runReason,
        string? occurrenceReason,
        string occurrenceStatus = "blocked",
        TimeSpan? actualSettledDuration = null)
    {
        var updatedAt = context.Fixture.CreatedAt.AddMinutes(10).UtcDateTime.Ticks;
        context.Fixture.Execute(
            "DROP TRIGGER trg_recurring_lease_uses_lifecycle_update; " +
            "UPDATE recording_runs SET status_code = $runStatus, has_crossed_start_commit = 1, terminal_reason_code = $runReason, updated_at_utc = $updatedAt, version = CASE WHEN $runStatus = 'settled' THEN 6 WHEN $runStatus = 'started_unknown' THEN 3 WHEN $runStatus = 'recording' THEN 3 WHEN $runStatus = 'finalizing' THEN 4 WHEN $runStatus = 'media_ready' THEN 5 ELSE 4 END WHERE id = $runId; " +
            "UPDATE recurring_lease_uses SET status_code = $useStatus, reserved_use_count = 1, reserved_duration_ticks = $duration, actual_settled_duration_ticks = $actual, updated_at_utc = $updatedAt, version = CASE WHEN $useStatus = 'settled' THEN 3 WHEN $runStatus = 'started_unknown' THEN 2 WHEN $runStatus = 'recording' OR $runStatus = 'finalizing' OR $runStatus = 'media_ready' THEN 2 ELSE 3 END WHERE use_id = $useId; " +
            "UPDATE plan_occurrences SET status_code = $occurrenceStatus, run_id = $runId, terminal_reason_code = $occurrenceReason, updated_at_utc = $updatedAt, version = 5 WHERE id = $occurrenceId;",
            ("$runStatus", runStatus),
            ("$useStatus", useStatus),
            ("$runReason", runReason),
            ("$occurrenceReason", occurrenceReason),
            ("$occurrenceStatus", occurrenceStatus),
            ("$actual", actualSettledDuration?.Ticks),
            ("$duration", context.Lease.PerRunDuration.Ticks),
            ("$updatedAt", updatedAt),
            ("$runId", reservation.RunId!),
            ("$useId", reservation.UseId!),
            ("$occurrenceId", context.Slot.OccurrenceId!));
    }

    private static string CaptureClaimProjection(
        RecurringOccurrenceReservationTests.ReservationContext context,
        string runId,
        string useId) =>
        string.Join("|",
            context.Fixture.Scalar(
                "SELECT status_code || '|' || IFNULL(run_id, '') || '|' || IFNULL(terminal_reason_code, '') || '|' || created_at_utc || '|' || updated_at_utc || '|' || version FROM plan_occurrences WHERE id = $id;",
                ("$id", context.Slot.OccurrenceId!)),
            context.Fixture.Scalar(
                "SELECT status_code || '|' || has_crossed_start_commit || '|' || IFNULL(media_artifact_id, '') || '|' || IFNULL(bundle_id, '') || '|' || IFNULL(terminal_reason_code, '') || '|' || created_at_utc || '|' || updated_at_utc || '|' || version FROM recording_runs WHERE id = $id;",
                ("$id", runId)),
            context.Fixture.Scalar(
                "SELECT status_code || '|' || reserved_use_count || '|' || reserved_duration_ticks || '|' || IFNULL(actual_settled_duration_ticks, '') || '|' || created_at_utc || '|' || updated_at_utc || '|' || version FROM recurring_lease_uses WHERE use_id = $id;",
                ("$id", useId)),
            context.Fixture.Scalar(
                "SELECT status_code || '|' || version || '|' || updated_at_utc FROM recurring_consent_leases WHERE lease_id = $id;",
                ("$id", context.Lease.LeaseId)));

    private static IRecurringOccurrenceEnvironmentProvider CreateFailureProvider(string category) =>
        category switch
        {
            "throw" => new ThrowingEnvironmentProvider(),
            "null" => new NullEnvironmentProvider(),
            "time" => new NonTrustedTimeEnvironmentProvider(),
            _ => new MismatchingEnvironmentProvider(category),
        };

    private sealed class CountingEnvironmentProvider : IRecurringOccurrenceEnvironmentProvider
    {
        private readonly IRecurringOccurrenceEnvironmentProvider _inner;

        internal CountingEnvironmentProvider(IRecurringOccurrenceEnvironmentProvider inner) => _inner = inner;

        internal int CaptureCount { get; private set; }

        public StandingLeaseExecutionEnvironment Capture(RecurringOccurrenceEnvironmentCaptureRequest request)
        {
            CaptureCount++;
            return _inner.Capture(request);
        }
    }

    private sealed class RejectingEnvironmentProvider : IRecurringOccurrenceEnvironmentProvider
    {
        public StandingLeaseExecutionEnvironment Capture(RecurringOccurrenceEnvironmentCaptureRequest request) =>
            new(request.TrustedNowUtc, request.Requirements.CurrentUserSid, request.Requirements.SessionBinding, isInteractiveDesktop: false);
    }

    private sealed class ThrowingEnvironmentProvider : IRecurringOccurrenceEnvironmentProvider
    {
        public StandingLeaseExecutionEnvironment Capture(RecurringOccurrenceEnvironmentCaptureRequest request) =>
            throw new InvalidOperationException("must not be called on blocked replay");
    }

    private sealed class NullEnvironmentProvider : IRecurringOccurrenceEnvironmentProvider
    {
        public StandingLeaseExecutionEnvironment Capture(RecurringOccurrenceEnvironmentCaptureRequest request) => null!;
    }

    private sealed class NonTrustedTimeEnvironmentProvider : IRecurringOccurrenceEnvironmentProvider
    {
        public StandingLeaseExecutionEnvironment Capture(RecurringOccurrenceEnvironmentCaptureRequest request) =>
            new(request.TrustedNowUtc.AddTicks(1), request.Requirements.CurrentUserSid, request.Requirements.SessionBinding, true);
    }

    private sealed class MismatchingEnvironmentProvider : IRecurringOccurrenceEnvironmentProvider
    {
        private readonly string _category;

        internal MismatchingEnvironmentProvider(string category) => _category = category;

        public StandingLeaseExecutionEnvironment Capture(RecurringOccurrenceEnvironmentCaptureRequest request)
        {
            var matching = new RecurringOccurrenceReservationTests.MatchingReservationEnvironmentProvider().Capture(request);
            var displays = matching.Displays;
            var display = matching.Displays[0];
            if (_category == "display-identity")
            {
                displays = new[]
                {
                    new StandingLeaseDisplayMetadata(
                        display.PublicId,
                        null,
                        DisplayIdentityResolutionStatus.Unresolved,
                        display.PhysicalBounds,
                        display.DpiX,
                        display.DpiY,
                        display.PhysicalWidth,
                        display.PhysicalHeight,
                        display.Orientation),
                };
            }
            else if (_category == "display-missing")
            {
                displays = new[]
                {
                    new StandingLeaseDisplayMetadata(
                        display.PublicId,
                        display.StableDisplayFingerprint + "-missing",
                        display.IdentityStatus,
                        display.PhysicalBounds,
                        display.DpiX,
                        display.DpiY,
                        display.PhysicalWidth,
                        display.PhysicalHeight,
                        display.Orientation),
                };
            }
            else if (_category == "display-ambiguous")
                displays = matching.Displays.Concat(matching.Displays).ToArray();
            else if (_category == "display-metadata")
            {
                displays = new[]
                {
                    new StandingLeaseDisplayMetadata(
                        display.PublicId,
                        display.StableDisplayFingerprint,
                        display.IdentityStatus,
                        display.PhysicalBounds,
                        null,
                        display.DpiY,
                        display.PhysicalWidth,
                        display.PhysicalHeight,
                        display.Orientation),
                };
            }
            else if (_category == "display-mismatch")
            {
                displays = new[]
                {
                    new StandingLeaseDisplayMetadata(
                        display.PublicId,
                        display.StableDisplayFingerprint,
                        display.IdentityStatus,
                        display.PhysicalBounds,
                        display.DpiX + 1,
                        display.DpiY,
                        display.PhysicalWidth,
                        display.PhysicalHeight,
                        display.Orientation),
                };
            }

            var output = matching.OutputFileSystem;
            var requiredFreeBytes = RecordingPreflightChecker.RequiredFreeSpaceBytes(request.Requirements.ReservedDuration);
            if (_category == "output-missing")
                output = null;
            else if (_category == "output-directory")
            {
                output = new StandingLeaseOutputFileSystemSnapshot(
                    matching.OutputFileSystem!.NormalizedOutputDirectory + "-mismatch",
                    matching.OutputFileSystem.FrozenOutputFilePath,
                    true,
                    false,
                    true,
                    true,
                    matching.OutputFileSystem.AvailableFreeBytes,
                    requiredFreeBytes);
            }
            else if (_category == "output-file")
                output = new StandingLeaseOutputFileSystemSnapshot(
                    matching.OutputFileSystem!.NormalizedOutputDirectory,
                    matching.OutputFileSystem.FrozenOutputFilePath + "-mismatch",
                    true, false, true, true,
                    matching.OutputFileSystem.AvailableFreeBytes, requiredFreeBytes);
            else if (_category == "output-directory-unavailable")
                output = new StandingLeaseOutputFileSystemSnapshot(
                    matching.OutputFileSystem!.NormalizedOutputDirectory,
                    matching.OutputFileSystem.FrozenOutputFilePath,
                    false, false, true, true,
                    matching.OutputFileSystem.AvailableFreeBytes, requiredFreeBytes);
            else if (_category == "output-unwritable")
                output = new StandingLeaseOutputFileSystemSnapshot(
                    matching.OutputFileSystem!.NormalizedOutputDirectory,
                    matching.OutputFileSystem.FrozenOutputFilePath,
                    true, false, false, true,
                    matching.OutputFileSystem.AvailableFreeBytes, requiredFreeBytes);
            else if (_category == "output-exists")
                output = new StandingLeaseOutputFileSystemSnapshot(
                    matching.OutputFileSystem!.NormalizedOutputDirectory,
                    matching.OutputFileSystem.FrozenOutputFilePath,
                    true, true, true, true,
                    matching.OutputFileSystem.AvailableFreeBytes, requiredFreeBytes);
            else if (_category == "disk-unavailable")
                output = new StandingLeaseOutputFileSystemSnapshot(
                    matching.OutputFileSystem!.NormalizedOutputDirectory,
                    matching.OutputFileSystem.FrozenOutputFilePath,
                    true, false, true, false,
                    matching.OutputFileSystem.AvailableFreeBytes, requiredFreeBytes);
            else if (_category == "disk-insufficient")
                output = new StandingLeaseOutputFileSystemSnapshot(
                    matching.OutputFileSystem!.NormalizedOutputDirectory,
                    matching.OutputFileSystem.FrozenOutputFilePath,
                    true, false, true, true,
                    requiredFreeBytes - 1, requiredFreeBytes);

            return new StandingLeaseExecutionEnvironment(
                request.TrustedNowUtc,
                _category == "sid" ? matching.CurrentUserSid + "-mismatch" : matching.CurrentUserSid,
                _category == "session" ? matching.SessionBinding + "-mismatch" : matching.SessionBinding,
                _category != "interactive",
                displays,
                _category == "topology-missing" ? null :
                    _category == "topology" ? matching.TopologyDigest + "-mismatch" : matching.TopologyDigest,
                output);
        }
    }
}
