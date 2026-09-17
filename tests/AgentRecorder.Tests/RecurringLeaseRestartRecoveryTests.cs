using AgentRecorder.Capture;
using AgentRecorder.Core;
using AgentRecorder.Core.Automation;
using AgentRecorder.Persistence;
using Xunit;

namespace AgentRecorder.Tests;

[Collection("NonParallel-AgentRecorderDataDir")]
public sealed class RecurringLeaseRestartRecoveryTests
{
    private const string UserSid = "S-1-5-21-task261";
    private const string SessionBinding = "session-task261";

    [Theory]
    [InlineData("start_committed", "started_unknown", "recovery_after_start_commit")]
    [InlineData("recording", "session_interrupted", "recovery_after_recording_interrupted")]
    [InlineData("finalizing", "session_interrupted", "recovery_during_finalization")]
    [InlineData("media_ready", "session_interrupted", "recovery_before_settlement")]
    public void EachPostStartSourceIsReconciledAtomically(
        string source,
        string expectedRunStatus,
        string expectedReason)
    {
        using var context = RecurringOccurrenceReservationTests.ReservationContext.Create();
        var receipt = Start(context);
        PrepareSource(context, receipt, source);

        var query = new RecurringLeaseRestartRecoveryCandidateQuery(context.Fixture.Store);
        var page = query.ListCandidates(UserSid, SessionBinding, 100);
        var candidate = Assert.Single(page.Candidates);
        var result = new SqliteRecurringLeaseRestartRecoveryTransaction(context.Fixture.Store)
            .Reconcile(candidate, UserSid, SessionBinding, context.Fixture.CreatedAt.AddMinutes(10));

        Assert.Equal(RecurringLeaseRestartRecoveryStatus.Recovered, result.Status);
        Assert.True(result.Changed);
        Assert.Equal(expectedReason, result.Reason);
        Assert.Equal(expectedRunStatus, Read(context, "SELECT status_code FROM recording_runs WHERE id = $id;", ("$id", receipt.Run.Id)));
        Assert.Equal("started_unknown", Read(context, "SELECT status_code FROM recurring_lease_uses WHERE use_id = $id;", ("$id", receipt.Use.Id)));
        Assert.Equal("blocked", Read(context, "SELECT status_code FROM plan_occurrences WHERE id = $id;", ("$id", receipt.Occurrence.Id)));
        Assert.Equal(expectedReason, Read(context, "SELECT terminal_reason_code FROM recording_runs WHERE id = $id;", ("$id", receipt.Run.Id)));
        Assert.Equal(expectedReason, Read(context, "SELECT terminal_reason_code FROM plan_occurrences WHERE id = $id;", ("$id", receipt.Occurrence.Id)));
        Assert.Equal("active", Read(context, "SELECT status_code FROM recurring_consent_leases WHERE lease_id = $id;", ("$id", context.Lease.LeaseId)));
    }

    [Fact]
    public void RestartRecoveryDoesNotCallEnvironmentOrCreateReplacementClaims()
    {
        using var context = RecurringOccurrenceReservationTests.ReservationContext.Create();
        var provider = new CountingEnvironmentProvider(new RecurringOccurrenceReservationTests.MatchingReservationEnvironmentProvider());
        var reservation = Reserve(context, context.Slot, "task275-run", "task275-use");
        Assert.True(reservation.Succeeded);
        var committed = new RecurringOccurrenceStartCommitService(
            context.Fixture.Store,
            () => context.Fixture.CreatedAt,
            provider).Commit(context.Lease.LeaseId, context.Slot.OccurrenceIdentity);
        Assert.Equal(RecurringOccurrenceStartCommitStatus.Committed, committed.Status);
        var beforeRuns = Count(context, "recording_runs");
        var beforeUses = Count(context, "recurring_lease_uses");
        var beforeProviderCalls = provider.CaptureCount;

        var result = new RecurringLeaseStartupRecovery(context.Fixture.Store, () => context.Fixture.CreatedAt.AddMinutes(10))
            .RecoverBatch(UserSid, SessionBinding, 100);

        Assert.Equal(1, result.Scanned);
        Assert.Equal(1, result.Recovered);
        Assert.Equal(0, result.Failed);
        Assert.Equal(beforeRuns, Count(context, "recording_runs"));
        Assert.Equal(beforeUses, Count(context, "recurring_lease_uses"));
        Assert.Equal(beforeProviderCalls, provider.CaptureCount);
    }

    [Theory]
    [InlineData("settled")]
    [InlineData("abnormal")]
    public void LegalTerminalChainIsIdempotentAndDoesNotAdvanceVersions(string terminalKind)
    {
        using var context = RecurringOccurrenceReservationTests.ReservationContext.Create();
        var receipt = Start(context);
        var lifecycle = new SqliteRecurringLeaseLifecycleTransaction(context.Fixture.Store, receipt);
        var at = context.Fixture.CreatedAt.AddMinutes(1);
        if (terminalKind == "settled")
        {
            Assert.True(lifecycle.ObserveFirstFrame(new FirstFrameObservation { FrameNumber = 0, TotalSizeBytes = 100 }, at).Succeeded);
            Assert.True(lifecycle.CompleteTermination(
                RecurringLeaseLifecycleTerminationKind.NaturalExit,
                0,
                new OutputMeta
                {
                    OutputFileExists = true,
                    SizeBytes = 100,
                    DurationSeconds = 1,
                    OutputPath = receipt.Specification.FrozenOutputFilePath,
                    StopReason = "natural",
                },
                at.AddMinutes(1)).Succeeded);
        }
        else
        {
            Assert.True(lifecycle.CompleteTermination(
                RecurringLeaseLifecycleTerminationKind.NaturalExit,
                0,
                null,
                at).Succeeded);
        }

        var before = Versions(context, receipt);
        var result = new SqliteRecurringLeaseRestartRecoveryTransaction(context.Fixture.Store)
            .Reconcile(ReadCandidate(context, receipt), UserSid, SessionBinding, at.AddMinutes(10));
        var after = Versions(context, receipt);

        Assert.Equal(RecurringLeaseRestartRecoveryStatus.AlreadyReconciled, result.Status);
        Assert.False(result.Changed);
        Assert.Equal(before, after);
    }

    [Fact]
    public void PreStartAndReservedChainsAreNotRecoveryCandidates()
    {
        using var authorized = RecurringOccurrenceReservationTests.ReservationContext.Create();
        var authorizedPage = new RecurringLeaseRestartRecoveryCandidateQuery(authorized.Fixture.Store)
            .ListCandidates(UserSid, SessionBinding, 100);
        Assert.Empty(authorizedPage.Candidates);

        using var reserved = RecurringOccurrenceReservationTests.ReservationContext.Create();
        var reservation = Reserve(reserved, reserved.Slot, "task275-reserved-run", "task275-reserved-use");
        Assert.True(reservation.Succeeded);
        var reservedPage = new RecurringLeaseRestartRecoveryCandidateQuery(reserved.Fixture.Store)
            .ListCandidates(UserSid, SessionBinding, 100);
        Assert.Empty(reservedPage.Candidates);
        Assert.Equal("run_created", Read(reserved, "SELECT status_code FROM plan_occurrences;"));
        Assert.Equal("created", Read(reserved, "SELECT status_code FROM recording_runs;"));
        Assert.Equal("reserved", Read(reserved, "SELECT status_code FROM recurring_lease_uses;"));
    }

    [Fact]
    public void SidOrSessionMismatchCannotEnumerateCandidates()
    {
        using var context = RecurringOccurrenceReservationTests.ReservationContext.Create();
        _ = Start(context);

        var query = new RecurringLeaseRestartRecoveryCandidateQuery(context.Fixture.Store);
        Assert.Empty(query.ListCandidates("S-1-5-21-other", SessionBinding, 100).Candidates);
        Assert.Empty(query.ListCandidates(UserSid, "session-other", 100).Candidates);
        Assert.Throws<Phase3PersistenceException>(() => query.ListCandidates(UserSid, SessionBinding, 0));
        Assert.Throws<Phase3PersistenceException>(() => query.ListCandidates(UserSid, SessionBinding, 101));
    }

    [Fact]
    public void ForgedObservedVersionIsRejectedWithoutMutation()
    {
        using var context = RecurringOccurrenceReservationTests.ReservationContext.Create();
        var receipt = Start(context);
        var candidate = Assert.Single(new RecurringLeaseRestartRecoveryCandidateQuery(context.Fixture.Store)
            .ListCandidates(UserSid, SessionBinding, 100).Candidates);
        var forged = candidate with { RunVersion = candidate.RunVersion + 1 };
        var before = Versions(context, receipt);

        var result = new SqliteRecurringLeaseRestartRecoveryTransaction(context.Fixture.Store)
            .Reconcile(forged, UserSid, SessionBinding, context.Fixture.CreatedAt.AddMinutes(10));

        Assert.Equal(RecurringLeaseRestartRecoveryStatus.Rejected, result.Status);
        Assert.Equal("recovery_concurrency_conflict", result.Reason);
        Assert.Equal(before, Versions(context, receipt));
    }

    [Theory]
    [InlineData("AfterRunUpdate")]
    [InlineData("AfterUseUpdate")]
    [InlineData("AfterOccurrenceUpdate")]
    [InlineData("BeforeFinalReadback")]
    [InlineData("BeforeCommit")]
    public void AnyRecoveryWriteFailureRollsBackAllThreeRows(string failurePointCode)
    {
        var failurePoint = Enum.Parse<RecurringLeaseRestartRecoveryFailurePoint>(failurePointCode);
        using var context = RecurringOccurrenceReservationTests.ReservationContext.Create();
        var receipt = Start(context);
        var candidate = Assert.Single(new RecurringLeaseRestartRecoveryCandidateQuery(context.Fixture.Store)
            .ListCandidates(UserSid, SessionBinding, 100).Candidates);
        var before = Versions(context, receipt);
        var transaction = new SqliteRecurringLeaseRestartRecoveryTransaction(
            context.Fixture.Store,
            point =>
            {
                if (point == failurePoint)
                    throw new InvalidOperationException("task275 injected failure");
            });

        var result = transaction.Reconcile(candidate, UserSid, SessionBinding, context.Fixture.CreatedAt.AddMinutes(10));

        Assert.Equal(RecurringLeaseRestartRecoveryStatus.Rejected, result.Status);
        Assert.Equal("recovery_sqlite_failure", result.Reason);
        Assert.Equal(before, Versions(context, receipt));
        Assert.Equal("run_created", Read(context, "SELECT status_code FROM plan_occurrences;"));
        Assert.Equal("start_committed", Read(context, "SELECT status_code FROM recording_runs;"));
        Assert.Equal("start_committed", Read(context, "SELECT status_code FROM recurring_lease_uses;"));
    }

    [Fact]
    public void RepeatedRecoveryWithTheSameCandidateChangesOnlyOnce()
    {
        using var context = RecurringOccurrenceReservationTests.ReservationContext.Create();
        var receipt = Start(context);
        var candidate = Assert.Single(new RecurringLeaseRestartRecoveryCandidateQuery(context.Fixture.Store)
            .ListCandidates(UserSid, SessionBinding, 100).Candidates);
        var transaction = new SqliteRecurringLeaseRestartRecoveryTransaction(context.Fixture.Store);

        var first = transaction.Reconcile(candidate, UserSid, SessionBinding, context.Fixture.CreatedAt.AddMinutes(10));
        var second = transaction.Reconcile(candidate, UserSid, SessionBinding, context.Fixture.CreatedAt.AddMinutes(10));

        Assert.Equal(RecurringLeaseRestartRecoveryStatus.Recovered, first.Status);
        Assert.Equal(RecurringLeaseRestartRecoveryStatus.Rejected, second.Status);
        Assert.Equal("recovery_concurrency_conflict", second.Reason);
        Assert.Equal(3, new SqliteRecordingRunRepository(context.Fixture.Store).Get(receipt.Run.Id).Version);
        Assert.Equal(2, context.Fixture.ReadAccountingRow(receipt.Use.Id).Version);
        Assert.Equal(5, new SqlitePlanOccurrenceRepository(context.Fixture.Store).Get(receipt.Occurrence.Id).Version);
    }

    [Fact]
    public void RecoveryRecomputesQuotaAndPreservesFutureCapacity()
    {
        using (var future = RecurringOccurrenceReservationTests.ReservationContext.Create())
        {
            var receipt = Start(future);
            var candidate = Assert.Single(new RecurringLeaseRestartRecoveryCandidateQuery(future.Fixture.Store)
                .ListCandidates(UserSid, SessionBinding, 100).Candidates);
            var result = new SqliteRecurringLeaseRestartRecoveryTransaction(future.Fixture.Store)
                .Reconcile(candidate, UserSid, SessionBinding, future.Fixture.CreatedAt.AddMinutes(10));

            Assert.Equal(RecurringLeaseRestartRecoveryStatus.Recovered, result.Status);
            Assert.Equal("active", Read(future, "SELECT status_code FROM recurring_consent_leases WHERE lease_id = $id;", ("$id", future.Lease.LeaseId)));
            Assert.Equal("started_unknown", future.Fixture.ReadAccountingRow(receipt.Use.Id).StatusCode);
        }

        using var exhausted = RecurringOccurrenceReservationTests.ReservationContext.Create(
            maxUses: 1,
            maxCumulativeDuration: TimeSpan.FromMinutes(2));
        var exhaustedReceipt = Start(exhausted);
        var exhaustedCandidate = Assert.Single(new RecurringLeaseRestartRecoveryCandidateQuery(exhausted.Fixture.Store)
            .ListCandidates(UserSid, SessionBinding, 100).Candidates);
        var exhaustedResult = new SqliteRecurringLeaseRestartRecoveryTransaction(exhausted.Fixture.Store)
            .Reconcile(exhaustedCandidate, UserSid, SessionBinding, exhausted.Fixture.CreatedAt.AddMinutes(10));

        Assert.Equal(RecurringLeaseRestartRecoveryStatus.Recovered, exhaustedResult.Status);
        Assert.Equal("exhausted", Read(exhausted, "SELECT status_code FROM recurring_consent_leases WHERE lease_id = $id;", ("$id", exhausted.Lease.LeaseId)));
        Assert.Equal("started_unknown", exhausted.Fixture.ReadAccountingRow(exhaustedReceipt.Use.Id).StatusCode);
    }

    [Theory]
    [InlineData("revoked")]
    [InlineData("expired")]
    public void RevokedAndExpiredLeaseStatesAreNotReopened(string terminalLeaseStatus)
    {
        using var context = RecurringOccurrenceReservationTests.ReservationContext.Create();
        var receipt = Start(context);
        var updatedAt = terminalLeaseStatus == "expired"
            ? context.Lease.ValidUntilUtc
            : context.Fixture.CreatedAt.AddMinutes(10);
        context.Fixture.Execute(
            "UPDATE recurring_consent_leases SET status_code = $status, updated_at_utc = $updated, version = version + 1 WHERE lease_id = $id;",
            ("$status", terminalLeaseStatus),
            ("$updated", updatedAt.UtcDateTime.Ticks),
            ("$id", context.Lease.LeaseId));
        var beforeVersion = Convert.ToInt64(context.Fixture.Scalar(
            "SELECT version FROM recurring_consent_leases WHERE lease_id = $id;", ("$id", context.Lease.LeaseId)));
        var candidate = Assert.Single(new RecurringLeaseRestartRecoveryCandidateQuery(context.Fixture.Store)
            .ListCandidates(UserSid, SessionBinding, 100).Candidates);

        var result = new SqliteRecurringLeaseRestartRecoveryTransaction(context.Fixture.Store)
            .Reconcile(candidate, UserSid, SessionBinding, updatedAt.AddMinutes(1));

        Assert.Equal(RecurringLeaseRestartRecoveryStatus.Recovered, result.Status);
        Assert.Equal(terminalLeaseStatus, Read(context, "SELECT status_code FROM recurring_consent_leases WHERE lease_id = $id;", ("$id", context.Lease.LeaseId)));
        Assert.Equal(beforeVersion, Convert.ToInt64(context.Fixture.Scalar(
            "SELECT version FROM recurring_consent_leases WHERE lease_id = $id;", ("$id", context.Lease.LeaseId))));
        Assert.Equal("started_unknown", context.Fixture.ReadAccountingRow(receipt.Use.Id).StatusCode);
    }

    [Fact]
    public void HistoricalTerminalUsesAndPendingRecoveryUseShareOneQuotaSnapshot()
    {
        using var context = RecurringOccurrenceReservationTests.ReservationContext.Create(
            slotCount: 3,
            maxUses: 4,
            maxCumulativeDuration: TimeSpan.FromMinutes(8));
        var settled = StartAt(context, context.Slots[0], "task275-settled-run", "task275-settled-use");
        FinishSettled(context, settled);
        var abnormal = StartAt(context, context.Slots[1], "task275-abnormal-run", "task275-abnormal-use");
        FinishAbnormal(context, abnormal);
        var pending = StartAt(context, context.Slots[2], "task275-pending-run", "task275-pending-use");

        var result = new RecurringLeaseStartupRecovery(
            context.Fixture.Store,
            () => context.Slots[2].ScheduledStartUtc!.Value.AddMinutes(10))
            .RecoverBatch(UserSid, SessionBinding, 100);

        Assert.Equal(1, result.Scanned);
        Assert.Equal(1, result.Recovered);
        Assert.Equal(0, result.AlreadyReconciled);
        Assert.Equal(0, result.Failed);
        Assert.False(result.HasMore);
        Assert.Equal("settled", context.Fixture.ReadAccountingRow(settled.Use.Id).StatusCode);
        Assert.Equal("started_unknown", context.Fixture.ReadAccountingRow(abnormal.Use.Id).StatusCode);
        Assert.Equal("started_unknown", context.Fixture.ReadAccountingRow(pending.Use.Id).StatusCode);
        Assert.Equal("active", Read(context, "SELECT status_code FROM recurring_consent_leases WHERE lease_id = $id;", ("$id", context.Lease.LeaseId)));
    }

    [Fact]
    public void BoundedQueryUsesStableOrderAndLimitPlusOneHasMore()
    {
        using var context = RecurringOccurrenceReservationTests.ReservationContext.Create(slotCount: 2, maxUses: 3, maxCumulativeDuration: TimeSpan.FromMinutes(6));
        _ = StartAt(context, context.Slots[0], "task275-page-first-run", "task275-page-first-use");
        SeedCommittedSource(context, context.Slots[1], "task275-page-second-run", "task275-page-second-use");

        var query = new RecurringLeaseRestartRecoveryCandidateQuery(context.Fixture.Store);
        var one = query.ListCandidates(UserSid, SessionBinding, 1);
        var all = query.ListCandidates(UserSid, SessionBinding, 100);

        Assert.Single(one.Candidates);
        Assert.True(one.HasMore);
        Assert.Equal(2, all.Candidates.Count);
        Assert.False(all.HasMore);
        Assert.Equal(context.Slots[0].OccurrenceIdentity, all.Candidates[0].OccurrenceIdentity);
        Assert.Equal(context.Slots[1].OccurrenceIdentity, all.Candidates[1].OccurrenceIdentity);
        Assert.Throws<Phase3PersistenceException>(() => query.ListCandidates(UserSid, SessionBinding, -1));
    }

    [Fact]
    public void SmallLimitBatchProgressesAcrossThreeSourceChains()
    {
        using var context = RecurringOccurrenceReservationTests.ReservationContext.Create(
            slotCount: 3,
            maxUses: 4,
            maxCumulativeDuration: TimeSpan.FromMinutes(8));
        for (var index = 0; index < context.Slots.Count; index++)
        {
            SeedCommittedSource(
                context,
                context.Slots[index],
                $"task275-progress-run-{index}",
                $"task275-progress-use-{index}");
        }

        var recovery = new RecurringLeaseStartupRecovery(
            context.Fixture.Store,
            () => context.Slots[2].ScheduledStartUtc!.Value.AddMinutes(10));
        var recoveredIdentities = new List<string>();
        for (var attempt = 0; attempt < 3; attempt++)
        {
            var result = recovery.RecoverBatch(UserSid, SessionBinding, 1);
            Assert.Equal(1, result.Scanned);
            Assert.Equal(1, result.Recovered);
            Assert.Equal(0, result.AlreadyReconciled);
            Assert.Equal(0, result.Failed);
            recoveredIdentities.Add(Assert.Single(result.Outcomes).OccurrenceIdentity!);
            Assert.Equal(attempt < 2, result.HasMore);
        }

        var final = recovery.RecoverBatch(UserSid, SessionBinding, 1);
        Assert.Equal(0, final.Scanned);
        Assert.Equal(0, final.Recovered);
        Assert.False(final.HasMore);
        Assert.Equal(context.Slots.Select(slot => slot.OccurrenceIdentity), recoveredIdentities);
    }

    [Fact]
    public void MoreThanOneHundredHistoricalTerminalsCannotStarveOneSource()
    {
        const int historicalTerminalCount = 101;
        using var context = RecurringOccurrenceReservationTests.ReservationContext.Create(
            slotCount: historicalTerminalCount + 1,
            maxUses: 200,
            maxCumulativeDuration: TimeSpan.FromMinutes(400));
        for (var index = 0; index < historicalTerminalCount; index++)
        {
            SeedAbnormalTerminal(
                context,
                context.Slots[index],
                $"task275-history-run-{index}",
                $"task275-history-use-{index}");
        }

        var source = context.Slots[historicalTerminalCount];
        SeedCommittedSource(context, source, "task275-history-source-run", "task275-history-source-use");
        var result = new RecurringLeaseStartupRecovery(
            context.Fixture.Store,
            () => source.ScheduledStartUtc!.Value.AddMinutes(10))
            .RecoverBatch(UserSid, SessionBinding, 1);

        Assert.Equal(1, result.Scanned);
        Assert.Equal(1, result.Recovered);
        Assert.Equal(0, result.AlreadyReconciled);
        Assert.False(result.HasMore);
        Assert.Equal(source.OccurrenceIdentity, Assert.Single(result.Outcomes).OccurrenceIdentity);
        Assert.Equal("started_unknown", Read(context, "SELECT status_code FROM recording_runs WHERE id = $id;", ("$id", "task275-history-source-run")));
    }

    [Fact]
    public void CumulativeDurationBecomesExhaustedOnlyAfterRecoveryAndRollsBackAtLeaseWrite()
    {
        using var context = RecurringOccurrenceReservationTests.ReservationContext.Create(
            maxUses: 3,
            maxCumulativeDuration: TimeSpan.FromMinutes(3));
        var receipt = Start(context);
        var leaseBefore = new SqliteRecurringConsentLeaseRepository(context.Fixture.Store).Get(context.Lease.LeaseId);
        var sourceEntry = RecurringLeaseUseAccountingEntry.CreateFor(
            leaseBefore,
            receipt.Use.Id,
            context.Slot.OccurrenceIdentity,
            receipt.Run.Id,
            LeaseUseStatus.StartCommitted,
            reservedUseCount: 1,
            leaseBefore.PerRunDuration,
            actualSettledDuration: null);
        var beforeQuota = RecurringLeaseQuotaCalculator.Calculate(leaseBefore, new[] { sourceEntry });
        Assert.True(beforeQuota.IsTemporarilyUnavailable);
        Assert.False(beforeQuota.IsTerminallyExhausted);
        Assert.Equal("active", leaseBefore.StatusCode);

        var candidate = Assert.Single(new RecurringLeaseRestartRecoveryCandidateQuery(context.Fixture.Store)
            .ListCandidates(UserSid, SessionBinding, 100).Candidates);
        var before = Versions(context, receipt);
        var failing = new SqliteRecurringLeaseRestartRecoveryTransaction(
            context.Fixture.Store,
            point =>
            {
                if (point == RecurringLeaseRestartRecoveryFailurePoint.AfterLeaseExhaustionUpdate)
                    throw new InvalidOperationException("task275R injected lease exhaustion failure");
            });
        var failed = failing.Reconcile(candidate, UserSid, SessionBinding, context.Fixture.CreatedAt.AddMinutes(10));

        Assert.Equal(RecurringLeaseRestartRecoveryStatus.Rejected, failed.Status);
        Assert.Equal("recovery_sqlite_failure", failed.Reason);
        Assert.Equal(before, Versions(context, receipt));
        Assert.Equal("active", Read(context, "SELECT status_code FROM recurring_consent_leases WHERE lease_id = $id;", ("$id", context.Lease.LeaseId)));
        Assert.Equal("run_created", Read(context, "SELECT status_code FROM plan_occurrences;"));
        Assert.Equal("start_committed", Read(context, "SELECT status_code FROM recording_runs;"));
        Assert.Equal("start_committed", Read(context, "SELECT status_code FROM recurring_lease_uses;"));

        var recovered = new SqliteRecurringLeaseRestartRecoveryTransaction(context.Fixture.Store)
            .Reconcile(candidate, UserSid, SessionBinding, context.Fixture.CreatedAt.AddMinutes(10));
        Assert.Equal(RecurringLeaseRestartRecoveryStatus.Recovered, recovered.Status);
        Assert.Equal("exhausted", Read(context, "SELECT status_code FROM recurring_consent_leases WHERE lease_id = $id;", ("$id", context.Lease.LeaseId)));
        Assert.Equal(before.Run + 1, new SqliteRecordingRunRepository(context.Fixture.Store).Get(receipt.Run.Id).Version);
        Assert.Equal(before.Use + 1, context.Fixture.ReadAccountingRow(receipt.Use.Id).Version);
        Assert.Equal(before.Occurrence + 1, new SqlitePlanOccurrenceRepository(context.Fixture.Store).Get(receipt.Occurrence.Id).Version);
        Assert.Equal(before.Lease + 1, new SqliteRecurringConsentLeaseRepository(context.Fixture.Store).Get(context.Lease.LeaseId).Version);
    }

    [Fact]
    public void NullEmptyAndNonCanonicalTransactionContextIsRejectedWithoutMutation()
    {
        using var context = RecurringOccurrenceReservationTests.ReservationContext.Create();
        var receipt = Start(context);
        var candidate = Assert.Single(new RecurringLeaseRestartRecoveryCandidateQuery(context.Fixture.Store)
            .ListCandidates(UserSid, SessionBinding, 100).Candidates);
        var before = Versions(context, receipt);
        var transaction = new SqliteRecurringLeaseRestartRecoveryTransaction(context.Fixture.Store);

        var results = new[]
        {
            transaction.Reconcile(candidate, null, SessionBinding, context.Fixture.CreatedAt.AddMinutes(10)),
            transaction.Reconcile(candidate, UserSid, null, context.Fixture.CreatedAt.AddMinutes(10)),
            transaction.Reconcile(candidate, " " + UserSid, SessionBinding, context.Fixture.CreatedAt.AddMinutes(10)),
            transaction.Reconcile(candidate, UserSid, "session-task261 ", context.Fixture.CreatedAt.AddMinutes(10)),
        };

        Assert.All(results, result =>
        {
            Assert.Equal(RecurringLeaseRestartRecoveryStatus.Rejected, result.Status);
            Assert.Equal("recovery_concurrency_conflict", result.Reason);
        });
        Assert.Equal(before, Versions(context, receipt));
    }

    [Fact]
    public void BatchContinuesAfterOneInvalidCandidateAndAuditFailure()
    {
        using var context = RecurringOccurrenceReservationTests.ReservationContext.Create(slotCount: 2, maxUses: 3, maxCumulativeDuration: TimeSpan.FromMinutes(6));
        var invalid = StartAt(context, context.Slots[0], "task275-invalid-run", "task275-invalid-use");
        SeedCommittedSource(context, context.Slots[1], "task275-valid-run", "task275-valid-use");
        context.Fixture.Execute(
            "UPDATE recording_runs SET terminal_reason_code = 'unknown-recovery-reason' WHERE id = $id;",
            ("$id", invalid.Run.Id));

        var result = new RecurringLeaseStartupRecovery(
            context.Fixture.Store,
            () => context.Slots[1].ScheduledStartUtc!.Value.AddMinutes(10),
            (_, _) => throw new InvalidOperationException("audit unavailable"))
            .RecoverBatch(UserSid, SessionBinding, 100);

        Assert.Equal(2, result.Scanned);
        Assert.Equal(1, result.Recovered);
        Assert.Equal(0, result.AlreadyReconciled);
        Assert.Equal(1, result.Failed);
        Assert.True(result.Succeeded is false);
        Assert.Equal("unknown-recovery-reason", Read(context, "SELECT terminal_reason_code FROM recording_runs WHERE id = $id;", ("$id", invalid.Run.Id)));
        Assert.Equal("started_unknown", Read(context, "SELECT status_code FROM recording_runs WHERE id = $id;", ("$id", "task275-valid-run")));
    }

    [Fact]
    public void StartupContextMismatchIsRejectedWithoutMutation()
    {
        using var context = RecurringOccurrenceReservationTests.ReservationContext.Create();
        var receipt = Start(context);
        var candidate = Assert.Single(new RecurringLeaseRestartRecoveryCandidateQuery(context.Fixture.Store)
            .ListCandidates(UserSid, SessionBinding, 100).Candidates);
        var before = Versions(context, receipt);

        var result = new SqliteRecurringLeaseRestartRecoveryTransaction(context.Fixture.Store)
            .Reconcile(candidate, "S-1-5-21-other", SessionBinding, context.Fixture.CreatedAt.AddMinutes(10));

        Assert.Equal(RecurringLeaseRestartRecoveryStatus.Rejected, result.Status);
        Assert.Equal("recovery_concurrency_conflict", result.Reason);
        Assert.Equal(before, Versions(context, receipt));
    }

    [Fact]
    public void OverflowedAccountingEvidenceFailsClosedWithoutMutation()
    {
        using var context = RecurringOccurrenceReservationTests.ReservationContext.Create();
        var receipt = Start(context);
        var candidate = Assert.Single(new RecurringLeaseRestartRecoveryCandidateQuery(context.Fixture.Store)
            .ListCandidates(UserSid, SessionBinding, 100).Candidates);
        context.Fixture.TamperAccountingDuration(receipt.Use.Id, long.MaxValue);
        var before = Versions(context, receipt);

        var result = new SqliteRecurringLeaseRestartRecoveryTransaction(context.Fixture.Store)
            .Reconcile(candidate, UserSid, SessionBinding, context.Fixture.CreatedAt.AddMinutes(10));

        Assert.Equal(RecurringLeaseRestartRecoveryStatus.Rejected, result.Status);
        Assert.Equal("recovery_snapshot_invalid", result.Reason);
        Assert.Equal(before, Versions(context, receipt));
    }

    [Fact]
    public async Task ConcurrentCallersAdvanceOneChainOnlyOnce()
    {
        using var context = RecurringOccurrenceReservationTests.ReservationContext.Create();
        var receipt = Start(context);
        var candidate = Assert.Single(new RecurringLeaseRestartRecoveryCandidateQuery(context.Fixture.Store)
            .ListCandidates(UserSid, SessionBinding, 100).Candidates);
        var first = Task.Run(() => new SqliteRecurringLeaseRestartRecoveryTransaction(context.Fixture.Store)
            .Reconcile(candidate, UserSid, SessionBinding, context.Fixture.CreatedAt.AddMinutes(10)));
        var second = Task.Run(() => new SqliteRecurringLeaseRestartRecoveryTransaction(context.Fixture.Store)
            .Reconcile(candidate, UserSid, SessionBinding, context.Fixture.CreatedAt.AddMinutes(10)));
        var results = await Task.WhenAll(first, second);

        Assert.Equal(1, results.Count(result => result.Status == RecurringLeaseRestartRecoveryStatus.Recovered));
        Assert.Equal(1, results.Count(result => result.Status == RecurringLeaseRestartRecoveryStatus.Rejected));
        Assert.Contains(results, result => result.Reason == "recovery_concurrency_conflict");
        Assert.Equal(3, new SqliteRecordingRunRepository(context.Fixture.Store).Get(receipt.Run.Id).Version);
        Assert.Equal(2, context.Fixture.ReadAccountingRow(receipt.Use.Id).Version);
    }

    private static RecurringStartCommitReceipt Start(
        RecurringOccurrenceReservationTests.ReservationContext context)
        => StartAt(context, context.Slot, "task275-start-run", "task275-start-use", context.Fixture.CreatedAt);

    private static RecurringStartCommitReceipt StartAt(
        RecurringOccurrenceReservationTests.ReservationContext context,
        RecurringOccurrenceSlotSnapshot slot,
        string runId,
        string useId,
        DateTimeOffset? nowUtc = null)
    {
        var result = Reserve(context, slot, runId, useId, nowUtc ?? slot.ScheduledStartUtc!.Value);
        Assert.Equal(RecurringOccurrenceReservationStatus.Reserved, result.Status);
        var committed = new RecurringOccurrenceStartCommitService(
            context.Fixture.Store,
            () => nowUtc ?? slot.ScheduledStartUtc!.Value,
            new RecurringOccurrenceReservationTests.MatchingReservationEnvironmentProvider())
            .Commit(context.Lease.LeaseId, slot.OccurrenceIdentity);
        Assert.Equal(RecurringOccurrenceStartCommitStatus.Committed, committed.Status);
        return committed.FirstCommitReceipt ?? throw new InvalidOperationException("The test start commit did not return a receipt.");
    }

    private static RecurringOccurrenceReservationResult Reserve(
        RecurringOccurrenceReservationTests.ReservationContext context,
        RecurringOccurrenceSlotSnapshot slot,
        string runId,
        string useId,
        DateTimeOffset? nowUtc = null) =>
        new RecurringOccurrenceReservationService(
            context.Fixture.Store,
            () => nowUtc ?? slot.ScheduledStartUtc!.Value,
            () => runId,
            () => useId)
            .Reserve(context.Lease.LeaseId, slot.OccurrenceIdentity);

    private static void SeedCommittedSource(
        RecurringOccurrenceReservationTests.ReservationContext context,
        RecurringOccurrenceSlotSnapshot slot,
        string runId,
        string useId)
    {
        var at = slot.ScheduledStartUtc!.Value;
        context.Fixture.Execute(
            """
            INSERT INTO recording_runs(
                id, occurrence_id, status_code, has_crossed_start_commit,
                media_artifact_id, bundle_id, terminal_reason_code,
                created_at_utc, updated_at_utc, version)
            VALUES ($run_id, $occurrence_id, 'start_committed', 1, NULL, NULL, NULL,
                    $at, $at, 2);
            INSERT INTO recurring_lease_uses(
                use_id, lease_id, plan_id, occurrence_identity, occurrence_id, run_id,
                status_code, reserved_use_count, reserved_duration_ticks,
                actual_settled_duration_ticks, created_at_utc, updated_at_utc, version)
            VALUES ($use_id, $lease_id, $plan_id, $occurrence_identity, $occurrence_id, $run_id,
                    'start_committed', 1, $duration_ticks, NULL, $at, $at, 1);
            UPDATE plan_occurrences
            SET status_code = 'run_created', run_id = $run_id,
                terminal_reason_code = NULL, updated_at_utc = $at, version = version + 1
            WHERE id = $occurrence_id AND plan_id = $plan_id
              AND status_code = 'authorized' AND run_id IS NULL;
            """,
            ("$run_id", runId),
            ("$use_id", useId),
            ("$lease_id", context.Lease.LeaseId),
            ("$plan_id", context.Fixture.PlanId),
            ("$occurrence_identity", slot.OccurrenceIdentity),
            ("$occurrence_id", slot.OccurrenceId!),
            ("$duration_ticks", context.Lease.PerRunDuration.Ticks),
            ("$at", at.UtcDateTime.Ticks));
    }

    private static void SeedAbnormalTerminal(
        RecurringOccurrenceReservationTests.ReservationContext context,
        RecurringOccurrenceSlotSnapshot slot,
        string runId,
        string useId)
    {
        var at = slot.ScheduledStartUtc!.Value;
        context.Fixture.Execute(
            """
            INSERT INTO recording_runs(
                id, occurrence_id, status_code, has_crossed_start_commit,
                media_artifact_id, bundle_id, terminal_reason_code,
                created_at_utc, updated_at_utc, version)
            VALUES ($run_id, $occurrence_id, 'started_unknown', 1, NULL, NULL,
                    'recovery_after_start_commit', $at, $at, 3);
            INSERT INTO recurring_lease_uses(
                use_id, lease_id, plan_id, occurrence_identity, occurrence_id, run_id,
                status_code, reserved_use_count, reserved_duration_ticks,
                actual_settled_duration_ticks, created_at_utc, updated_at_utc, version)
            VALUES ($use_id, $lease_id, $plan_id, $occurrence_identity, $occurrence_id, $run_id,
                    'started_unknown', 1, $duration_ticks, NULL, $at, $at, 2);
            UPDATE plan_occurrences
            SET status_code = 'blocked', run_id = $run_id,
                terminal_reason_code = 'recovery_after_start_commit',
                updated_at_utc = $at, version = 5
            WHERE id = $occurrence_id AND plan_id = $plan_id
              AND status_code = 'authorized' AND run_id IS NULL;
            """,
            ("$run_id", runId),
            ("$use_id", useId),
            ("$lease_id", context.Lease.LeaseId),
            ("$plan_id", context.Fixture.PlanId),
            ("$occurrence_identity", slot.OccurrenceIdentity),
            ("$occurrence_id", slot.OccurrenceId!),
            ("$duration_ticks", context.Lease.PerRunDuration.Ticks),
            ("$at", at.UtcDateTime.Ticks));
    }

    private static RecurringLeaseRestartRecoveryCandidate ReadCandidate(
        RecurringOccurrenceReservationTests.ReservationContext context,
        RecurringStartCommitReceipt receipt)
    {
        return new RecurringLeaseRestartRecoveryCandidate(
            context.Fixture.PlanId,
            context.Lease.LeaseId,
            context.Slot.OccurrenceIdentity,
            receipt.Occurrence.Id,
            receipt.Run.Id,
            receipt.Use.Id,
            Read(context, "SELECT status_code FROM recording_runs WHERE id = $id;", ("$id", receipt.Run.Id)),
            Read(context, "SELECT status_code FROM recurring_lease_uses WHERE use_id = $id;", ("$id", receipt.Use.Id)),
            Read(context, "SELECT status_code FROM plan_occurrences WHERE id = $id;", ("$id", receipt.Occurrence.Id)),
            Convert.ToInt64(context.Fixture.Scalar("SELECT version FROM recording_runs WHERE id = $id;", ("$id", receipt.Run.Id))),
            Convert.ToInt64(context.Fixture.Scalar("SELECT version FROM recurring_lease_uses WHERE use_id = $id;", ("$id", receipt.Use.Id))),
            Convert.ToInt64(context.Fixture.Scalar("SELECT version FROM plan_occurrences WHERE id = $id;", ("$id", receipt.Occurrence.Id))),
            Convert.ToInt64(context.Fixture.Scalar("SELECT version FROM recurring_consent_leases WHERE lease_id = $id;", ("$id", context.Lease.LeaseId))),
            Convert.ToInt64(context.Fixture.Scalar("SELECT version FROM plans WHERE id = $id;", ("$id", context.Fixture.PlanId))));
    }

    private static void FinishSettled(
        RecurringOccurrenceReservationTests.ReservationContext context,
        RecurringStartCommitReceipt receipt)
    {
        var at = receipt.Specification.ScheduledStartUtc.AddMinutes(1);
        var lifecycle = new SqliteRecurringLeaseLifecycleTransaction(context.Fixture.Store, receipt);
        Assert.True(lifecycle.ObserveFirstFrame(new FirstFrameObservation { FrameNumber = 0, TotalSizeBytes = 100 }, at).Succeeded);
        Assert.True(lifecycle.CompleteTermination(
            RecurringLeaseLifecycleTerminationKind.NaturalExit,
            0,
            new OutputMeta
            {
                OutputFileExists = true,
                SizeBytes = 100,
                DurationSeconds = 1,
                OutputPath = receipt.Specification.FrozenOutputFilePath,
                StopReason = "natural",
            },
            at.AddMinutes(1)).Succeeded);
    }

    private static void FinishAbnormal(
        RecurringOccurrenceReservationTests.ReservationContext context,
        RecurringStartCommitReceipt receipt)
    {
        var at = receipt.Specification.ScheduledStartUtc.AddMinutes(1);
        Assert.True(new SqliteRecurringLeaseLifecycleTransaction(context.Fixture.Store, receipt)
            .CompleteTermination(RecurringLeaseLifecycleTerminationKind.NaturalExit, 0, null, at).Succeeded);
    }

    private static void PrepareSource(
        RecurringOccurrenceReservationTests.ReservationContext context,
        RecurringStartCommitReceipt receipt,
        string source)
    {
        var lifecycle = new SqliteRecurringLeaseLifecycleTransaction(context.Fixture.Store, receipt);
        var firstFrameAt = context.Fixture.CreatedAt.AddMinutes(1);
        if (source is "recording" or "finalizing" or "media_ready")
        {
            Assert.True(lifecycle.ObserveFirstFrame(
                new FirstFrameObservation { FrameNumber = 0, TotalSizeBytes = 100 },
                firstFrameAt).Succeeded);
        }

        if (source is "finalizing" or "media_ready")
        {
            Assert.True(lifecycle.ObserveCaptureEnded(firstFrameAt.AddMinutes(1)).Succeeded);
        }

        if (source == "media_ready")
        {
            context.Fixture.Execute(
                "UPDATE recording_runs SET status_code = 'media_ready', updated_at_utc = $updated, version = version + 1 WHERE id = $id AND status_code = 'finalizing';",
                ("$updated", context.Fixture.CreatedAt.AddMinutes(3).UtcDateTime.Ticks),
                ("$id", receipt.Run.Id));
        }
    }

    private static (long Run, long Use, long Occurrence, long Lease, long Plan) Versions(
        RecurringOccurrenceReservationTests.ReservationContext context,
        RecurringStartCommitReceipt receipt) =>
        (
            new SqliteRecordingRunRepository(context.Fixture.Store).Get(receipt.Run.Id).Version,
            context.Fixture.ReadAccountingRow(receipt.Use.Id).Version,
            new SqlitePlanOccurrenceRepository(context.Fixture.Store).Get(receipt.Occurrence.Id).Version,
            new SqliteRecurringConsentLeaseRepository(context.Fixture.Store).Get(context.Lease.LeaseId).Version,
            Convert.ToInt64(context.Fixture.Scalar("SELECT version FROM plans WHERE id = $id;", ("$id", context.Fixture.PlanId))));

    private static long Count(RecurringOccurrenceReservationTests.ReservationContext context, string table) =>
        Convert.ToInt64(context.Fixture.Scalar($"SELECT COUNT(*) FROM {table};"));

    private static string Read(
        RecurringOccurrenceReservationTests.ReservationContext context,
        string sql,
        params (string Name, object? Value)[] parameters) =>
        Convert.ToString(context.Fixture.Scalar(sql, parameters)) ?? "";

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
}
