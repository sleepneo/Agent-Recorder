using System.Collections.Concurrent;
using System.Reflection;
using System.Text.Json;
using AgentRecorder.Capture;
using AgentRecorder.Core;
using AgentRecorder.Core.Automation;
using AgentRecorder.Infrastructure;
using AgentRecorder.Persistence;
using Xunit;

namespace AgentRecorder.Tests;

[Collection("NonParallel-AgentRecorderDataDir")]
public sealed class RecurringLeaseUseProofTests
{
    [Fact]
    public void FirstCommittedRecurringUseIssuesOneBoundProofWithExactFields()
    {
        using var context = RecurringOccurrenceReservationTests.ReservationContext.Create();
        var reservation = Reserve(context, "task266-run-fields", "task266-use-fields");
        var result = Commit(context, reservation);

        Assert.Equal(RecurringOccurrenceStartCommitStatus.Committed, result.Status);
        Assert.True(result.Succeeded);
        Assert.True(result.CommittedNow);
        Assert.True(result.HasFirstCommitProof);
        Assert.True(result.CanStartCapture);
        Assert.True(result.CanIssueProof);
        Assert.NotNull(result.FirstCommitReceipt);

        var receipt = result.FirstCommitReceipt!;
        var proof = Assert.IsType<RecurringLeaseUseProof>(result.FirstCommitProof);
        Assert.Single(receipt.LeaseEntries);
        AssertExactQuota(
            RecurringLeaseQuotaCalculator.Calculate(receipt.Lease, receipt.LeaseEntries),
            receipt.Quota);
        Assert.Equal(CaptureAuthorizationProofKind.RecurringLeaseUse, proof.Kind);
        Assert.True(proof.CheckAvailableAt(receipt.CommittedAtUtc, out var availabilityReason), availabilityReason);
        Assert.Equal(receipt.Run.Id, proof.RunId);
        Assert.Equal(receipt.Run.Id, proof.RecordingId);
        Assert.Equal(receipt.Lease.Id, proof.LeaseId);
        Assert.Equal(receipt.Use.Id, proof.LeaseUseId);
        Assert.Equal(receipt.Specification.OccurrenceIdentity, proof.OccurrenceIdentity);
        Assert.Equal(receipt.Specification.OccurrenceId, receipt.Occurrence.Id);
        Assert.Equal(receipt.Specification.SpecificationDigest, proof.SpecificationDigest);
        Assert.Equal(
            CaptureAuthorizationProofIssuer.RecurringAuthorizationSourceId(receipt.Lease.Id, receipt.Use.Id),
            proof.AuthorizationSourceId);
        Assert.Equal(receipt.Approval.CurrentUserSid, proof.CurrentUserSid);
        Assert.Equal(receipt.Approval.SessionBinding, proof.SessionBinding);
        Assert.Equal(proof.CurrentUserSid + "|" + proof.SessionBinding, proof.UserSessionBinding);
        Assert.Equal(receipt.Specification.Duration, proof.MaxDuration);
        Assert.Equal(receipt.Specification.Duration.Ticks / TimeSpan.TicksPerMillisecond, proof.MaxDurationMilliseconds);
        Assert.Null(proof.MaxDurationSeconds);
        Assert.Null(proof.MaxFrameCount);
        Assert.Equal(receipt.CommittedAtUtc, proof.IssuedAtUtc);
        Assert.Equal(receipt.CommittedAtUtc, receipt.Run.UpdatedAtUtc);
        Assert.Equal(receipt.CommittedAtUtc, receipt.Use.UpdatedAtUtc);
        Assert.True(proof.ExpiresAtUtc <= receipt.Specification.PlannedEndUtc);
        Assert.True(proof.ExpiresAtUtc <= receipt.Lease.ValidUntilUtc);
        Assert.Matches("^[0-9a-f]{32}$", proof.OneTimeNonce);
        Assert.Empty(typeof(RecurringLeaseUseProof).GetConstructors(BindingFlags.Public | BindingFlags.Instance));
        Assert.Equal(
            0L,
            Convert.ToInt64(context.Fixture.Scalar(
                "SELECT COUNT(*) FROM sqlite_master WHERE lower(name) LIKE '%proof%' OR lower(name) LIKE '%nonce%';")));

        var serializedResult = JsonSerializer.Serialize(result);
        Assert.DoesNotContain("FirstCommitProof", serializedResult, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(proof.ProofId, serializedResult, StringComparison.Ordinal);
        Assert.DoesNotContain(proof.OneTimeNonce, serializedResult, StringComparison.Ordinal);
        Assert.DoesNotContain("nonce", serializedResult, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ReceiptFreezesCompleteLeaseAccountingEvidenceBeforeIssuerRecalculation()
    {
        using var context = RecurringOccurrenceReservationTests.ReservationContext.Create();
        var reservation = Reserve(context, "task266R-run-freeze", "task266R-use-freeze");
        var result = Commit(context, reservation);
        var sourceEntries = result.FirstCommitReceipt!.LeaseEntries.ToList();
        var receipt = CopyReceipt(result.FirstCommitReceipt!, leaseEntries: sourceEntries);

        sourceEntries[0] = RecurringLeaseUseAccountingEntry.CreateFor(
            context.Lease,
            "task266R-replaced-use",
            "task266R-replaced-occurrence",
            "task266R-replaced-run",
            LeaseUseStatus.Available,
            0,
            TimeSpan.Zero,
            null);

        var proof = CaptureAuthorizationProofIssuer.IssueRecurringLeaseUse(receipt);

        Assert.Equal(result.FirstCommitReceipt!.Use.Id, receipt.LeaseEntries.Single().UseId);
        Assert.Equal(result.FirstCommitReceipt!.Use.Id, proof.LeaseUseId);
    }

    [Theory]
    [InlineData("admission-uses")]
    [InlineData("admission-duration")]
    [InlineData("permanent-uses")]
    [InlineData("charged-duration")]
    [InlineData("remaining-uses")]
    [InlineData("remaining-duration")]
    [InlineData("in-flight")]
    [InlineData("temporary")]
    [InlineData("terminal")]
    [InlineData("reason")]
    [InlineData("lease-version")]
    [InlineData("lease-id")]
    [InlineData("authorization-digest")]
    public void IssuerRejectsEveryTamperedQuotaField(string field)
    {
        using var context = RecurringOccurrenceReservationTests.ReservationContext.Create();
        var reservation = Reserve(context, "task266R-run-quota-" + field, "task266R-use-quota-" + field);
        var result = Commit(context, reservation);
        var tampered = CopyReceipt(result.FirstCommitReceipt!, quota: TamperQuota(result.FirstCommitReceipt!.Quota, field));

        Assert.Throws<InvalidOperationException>(
            () => CaptureAuthorizationProofIssuer.IssueRecurringLeaseUse(tampered));
    }

    [Fact]
    public void RemainingUsesMustEqualCapacityMinusAdmissionUsesEvenWhenLocallyBounded()
    {
        using var context = RecurringOccurrenceReservationTests.ReservationContext.Create();
        var reservation = Reserve(context, "task266R-run-remaining-equation", "task266R-use-remaining-equation");
        var result = Commit(context, reservation);
        var source = result.FirstCommitReceipt!.Quota;
        var tampered = TamperQuota(source, "remaining-uses");

        Assert.InRange(tampered.RemainingReservableUses, 0, context.Lease.MaxUses);
        Assert.NotEqual(context.Lease.MaxUses - tampered.AdmissionUsedUses, tampered.RemainingReservableUses);
        Assert.Throws<InvalidOperationException>(
            () => CaptureAuthorizationProofIssuer.IssueRecurringLeaseUse(
                CopyReceipt(result.FirstCommitReceipt!, quota: tampered)));
    }

    [Fact]
    public void LeaseStatusMustMatchRecomputedTerminalQuota()
    {
        using (var activeContext = RecurringOccurrenceReservationTests.ReservationContext.Create())
        {
            var reservation = Reserve(activeContext, "task266R-run-exhausted-status", "task266R-use-exhausted-status");
            var result = Commit(activeContext, reservation);
            var tamperedLease = RehydrateLease(result.FirstCommitReceipt!.Lease, ConsentLeaseStatus.Exhausted);

            Assert.Throws<InvalidOperationException>(
                () => CaptureAuthorizationProofIssuer.IssueRecurringLeaseUse(
                    CopyReceipt(result.FirstCommitReceipt!, lease: tamperedLease)));
        }

        using (var exhaustedContext = RecurringOccurrenceReservationTests.ReservationContext.Create(maxUses: 1))
        {
            var reservation = Reserve(exhaustedContext, "task266R-run-active-status", "task266R-use-active-status");
            var result = Commit(exhaustedContext, reservation);
            var tamperedLease = RehydrateLease(result.FirstCommitReceipt!.Lease, ConsentLeaseStatus.Active);

            Assert.Throws<InvalidOperationException>(
                () => CaptureAuthorizationProofIssuer.IssueRecurringLeaseUse(
                    CopyReceipt(result.FirstCommitReceipt!, lease: tamperedLease)));
        }
    }

    [Theory]
    [InlineData("missing-current-use")]
    [InlineData("duplicate-current-use")]
    [InlineData("duplicate-run")]
    [InlineData("duplicate-occurrence")]
    [InlineData("cross-lease")]
    [InlineData("wrong-run")]
    [InlineData("wrong-occurrence")]
    [InlineData("wrong-status")]
    [InlineData("wrong-count")]
    [InlineData("wrong-duration")]
    [InlineData("wrong-actual")]
    [InlineData("domain-invalid-duration")]
    public void IssuerRejectsIncompleteOrInconsistentAccountingEvidence(string variant)
    {
        using var context = RecurringOccurrenceReservationTests.ReservationContext.Create();
        var reservation = Reserve(context, "task266R-run-entry-" + variant, "task266R-use-entry-" + variant);
        var result = Commit(context, reservation);
        var tamperedEntries = TamperEntries(result.FirstCommitReceipt!, variant);

        Assert.Throws<InvalidOperationException>(
            () => CaptureAuthorizationProofIssuer.IssueRecurringLeaseUse(
                CopyReceipt(result.FirstCommitReceipt!, leaseEntries: tamperedEntries)));
    }

    [Fact]
    public void ExactRecalculationAcceptsMixedLegalHistoricalAccountingStates()
    {
        using var context = RecurringOccurrenceReservationTests.ReservationContext.Create();
        var reservation = Reserve(context, "task266R-run-mixed", "task266R-use-mixed");
        var result = Commit(context, reservation);
        var receipt = result.FirstCommitReceipt!;
        var entries = receipt.LeaseEntries.ToList();
        entries.Add(CreateEntry(receipt.Lease, "settled", LeaseUseStatus.Settled, TimeSpan.FromSeconds(30)));
        entries.Add(CreateEntry(receipt.Lease, "available", LeaseUseStatus.Available));
        entries.Add(CreateEntry(receipt.Lease, "reserved", LeaseUseStatus.Reserved));
        entries.Add(CreateEntry(receipt.Lease, "started-unknown", LeaseUseStatus.StartedUnknown));
        var exactQuota = RecurringLeaseQuotaCalculator.Calculate(receipt.Lease, entries);
        var mixedReceipt = CopyReceipt(receipt, quota: exactQuota, leaseEntries: entries);

        var proof = CaptureAuthorizationProofIssuer.IssueRecurringLeaseUse(mixedReceipt);

        Assert.NotNull(proof);
        AssertExactQuota(exactQuota, mixedReceipt.Quota);
        Assert.Equal(5, mixedReceipt.LeaseEntries.Count);
        Assert.Equal(1, mixedReceipt.LeaseEntries.Count(entry => entry.UseId == mixedReceipt.Use.Id));
    }

    [Fact]
    public void QuotaEvidenceFailureAfterCommitIsDurableAndReplayCannotRepairIt()
    {
        using var context = RecurringOccurrenceReservationTests.ReservationContext.Create();
        var reservation = Reserve(context, "task266R-run-post-commit-quota", "task266R-use-post-commit-quota");
        var provider = new CountingEnvironmentProvider(
            new RecurringOccurrenceReservationTests.MatchingReservationEnvironmentProvider());
        var issuerCalls = 0;
        Func<RecurringStartCommitReceipt, RecurringLeaseUseProof> tamperingIssuer = receipt =>
        {
            issuerCalls++;
            return CaptureAuthorizationProofIssuer.IssueRecurringLeaseUse(
                CopyReceipt(receipt, quota: TamperQuota(receipt.Quota, "remaining-uses")));
        };
        var service = new RecurringOccurrenceStartCommitService(
            context.Fixture.Store,
            () => context.Fixture.CreatedAt,
            provider,
            recurringProofIssuerForTest: tamperingIssuer);

        var result = service.Commit(context.Lease.LeaseId, context.Slot.OccurrenceIdentity);

        Assert.Equal(RecurringOccurrenceStartCommitStatus.CommittedWithoutProof, result.Status);
        Assert.Equal(RecurringOccurrenceStartCommitReasonCodes.ProofUnavailableAfterCommit, result.ReasonCode);
        Assert.False(result.Succeeded);
        Assert.False(result.CanStartCapture);
        Assert.Null(result.FirstCommitProof);
        Assert.Null(result.FirstCommitReceipt);
        Assert.Equal(1, issuerCalls);
        Assert.Equal(RecordingRunStatus.StartCommitted,
            new SqliteRecordingRunRepository(context.Fixture.Store).Get(reservation.RunId!).Status);
        Assert.Equal(LeaseUseStatus.StartCommitted,
            new SqliteRecurringLeaseUseAccountingReader(context.Fixture.Store)
                .TryGetByOccurrence(context.Slot.OccurrenceIdentity)!.Status);

        var replay = service.Commit(context.Lease.LeaseId, context.Slot.OccurrenceIdentity);
        Assert.Equal(RecurringOccurrenceStartCommitStatus.AlreadyCommitted, replay.Status);
        Assert.Null(replay.FirstCommitProof);
        Assert.Null(replay.FirstCommitReceipt);
        Assert.Equal(1, issuerCalls);
        Assert.Equal(1, provider.CaptureCount);
    }

    [Fact]
    public void DifferentCommittedOccurrencesUseUniqueProofAndNonceAndStableSeparatedDigests()
    {
        using var firstContext = RecurringOccurrenceReservationTests.ReservationContext.Create();
        using var secondContext = RecurringOccurrenceReservationTests.ReservationContext.Create(slotCount: 2);
        var firstReservation = ReserveSlot(firstContext, firstContext.Slots[0], "task266-run-one", "task266-use-one");
        var secondReservation = ReserveSlot(secondContext, secondContext.Slots[1], "task266-run-two", "task266-use-two");
        var provider = new CountingEnvironmentProvider(
            new RecurringOccurrenceReservationTests.MatchingReservationEnvironmentProvider());
        var service = new RecurringOccurrenceStartCommitService(
            firstContext.Fixture.Store,
            () => firstContext.Slots[0].ScheduledStartUtc!.Value,
            provider);

        var first = service.Commit(firstContext.Lease.LeaseId, firstContext.Slots[0].OccurrenceIdentity);
        var second = new RecurringOccurrenceStartCommitService(
            secondContext.Fixture.Store,
            () => secondContext.Slots[1].ScheduledStartUtc!.Value,
            provider).Commit(secondContext.Lease.LeaseId, secondContext.Slots[1].OccurrenceIdentity);

        Assert.True(first.Status == RecurringOccurrenceStartCommitStatus.Committed,
            $"status={first.Status}; reason={first.ReasonCode}");
        Assert.True(second.Status == RecurringOccurrenceStartCommitStatus.Committed,
            $"status={second.Status}; reason={second.ReasonCode}");
        var firstProof = Assert.IsType<RecurringLeaseUseProof>(first.FirstCommitProof);
        var secondProof = Assert.IsType<RecurringLeaseUseProof>(second.FirstCommitProof);
        Assert.NotEqual(firstProof.ProofId, secondProof.ProofId);
        Assert.NotEqual(firstProof.OneTimeNonce, secondProof.OneTimeNonce);
        Assert.NotEqual(firstProof.CapturePlanDigest, firstProof.ScopeDigest);
        Assert.NotEqual(secondProof.CapturePlanDigest, secondProof.ScopeDigest);
        Assert.NotEqual(firstProof.CapturePlanDigest, secondProof.CapturePlanDigest);
        Assert.NotEqual(firstProof.ScopeDigest, secondProof.ScopeDigest);
        Assert.Equal(2, provider.CaptureCount);

        CaptureAuthorizationProofIssuer.ValidateRecurringStartCommitReceipt(first.FirstCommitReceipt!);
        CaptureAuthorizationProofIssuer.ValidateRecurringStartCommitReceipt(second.FirstCommitReceipt!);
        Assert.Equal(
            firstProof.CapturePlanDigest,
            CaptureAuthorizationProofIssuer.ComputeRecurringLeaseUsePlanDigest(
                first.FirstCommitReceipt!.Plan,
                first.FirstCommitReceipt.Occurrence,
                first.FirstCommitReceipt.Specification));
        Assert.Equal(
            firstProof.ScopeDigest,
            CaptureAuthorizationProofIssuer.ComputeRecurringLeaseUseScopeDigest(
                first.FirstCommitReceipt.Specification,
                first.FirstCommitReceipt.Lease,
                first.FirstCommitReceipt.Approval));
    }

    [Theory]
    [InlineData("plan-status")]
    [InlineData("occurrence-plan")]
    [InlineData("lease-status")]
    [InlineData("lease-digest")]
    [InlineData("approval-sid")]
    [InlineData("run-status")]
    [InlineData("run-media")]
    [InlineData("run-version")]
    [InlineData("use-status")]
    [InlineData("use-duration")]
    [InlineData("timestamp")]
    public void IssuerRejectsTamperedReceiptWithoutCreatingProof(string tamper)
    {
        using var context = RecurringOccurrenceReservationTests.ReservationContext.Create();
        var reservation = Reserve(context, "task266-run-tamper-" + tamper, "task266-use-tamper-" + tamper);
        var result = Commit(context, reservation);
        var receipt = result.FirstCommitReceipt!;
        var tampered = TamperReceipt(receipt, tamper);

        Assert.Throws<InvalidOperationException>(
            () => CaptureAuthorizationProofIssuer.IssueRecurringLeaseUse(tampered));
    }

    [Fact]
    public void IssuerRejectsAValidSpecificationFromAnotherOccurrence()
    {
        using var firstContext = RecurringOccurrenceReservationTests.ReservationContext.Create();
        using var secondContext = RecurringOccurrenceReservationTests.ReservationContext.Create(slotCount: 2);
        var firstReservation = ReserveSlot(firstContext, firstContext.Slots[0], "task266-run-spec-one", "task266-use-spec-one");
        var secondReservation = ReserveSlot(secondContext, secondContext.Slots[1], "task266-run-spec-two", "task266-use-spec-two");
        var first = CommitSlot(firstContext, firstReservation, firstContext.Slots[0].ScheduledStartUtc!.Value);
        var second = CommitSlot(secondContext, secondReservation, secondContext.Slots[1].ScheduledStartUtc!.Value);
        var firstReceipt = first.FirstCommitReceipt!;
        var secondReceipt = second.FirstCommitReceipt!;
        var tampered = CopyReceipt(firstReceipt, specification: secondReceipt.Specification);

        Assert.Throws<InvalidOperationException>(
            () => CaptureAuthorizationProofIssuer.IssueRecurringLeaseUse(tampered));
    }

    [Fact]
    public void ReplayBlockedAndAdvancedResultsNeverCarryAProofOrReceipt()
    {
        using (var replayContext = RecurringOccurrenceReservationTests.ReservationContext.Create())
        {
            var reservation = Reserve(replayContext, "task266-run-replay", "task266-use-replay");
            var service = new RecurringOccurrenceStartCommitService(
                replayContext.Fixture.Store,
                () => replayContext.Fixture.CreatedAt,
                new RecurringOccurrenceReservationTests.MatchingReservationEnvironmentProvider());
            var first = service.Commit(replayContext.Lease.LeaseId, replayContext.Slot.OccurrenceIdentity);
            var replay = service.Commit(replayContext.Lease.LeaseId, replayContext.Slot.OccurrenceIdentity);

            Assert.NotNull(first.FirstCommitProof);
            Assert.Equal(RecurringOccurrenceStartCommitStatus.AlreadyCommitted, replay.Status);
            Assert.Null(replay.FirstCommitProof);
            Assert.Null(replay.FirstCommitReceipt);
            Assert.False(replay.HasFirstCommitProof);
            Assert.False(replay.CanStartCapture);
        }

        using (var blockedContext = RecurringOccurrenceReservationTests.ReservationContext.Create())
        {
            var reservation = Reserve(blockedContext, "task266-run-blocked", "task266-use-blocked");
            var provider = new CountingEnvironmentProvider(new ThrowingEnvironmentProvider());
            var service = new RecurringOccurrenceStartCommitService(
                blockedContext.Fixture.Store,
                () => blockedContext.Fixture.CreatedAt,
                provider);
            var blocked = service.Commit(blockedContext.Lease.LeaseId, blockedContext.Slot.OccurrenceIdentity);
            var replay = service.Commit(blockedContext.Lease.LeaseId, blockedContext.Slot.OccurrenceIdentity);

            Assert.Equal(RecurringOccurrenceStartCommitStatus.Blocked, blocked.Status);
            Assert.Null(blocked.FirstCommitProof);
            Assert.Equal(RecurringOccurrenceStartCommitStatus.AlreadyBlocked, replay.Status);
            Assert.Null(replay.FirstCommitProof);
            Assert.Null(replay.FirstCommitReceipt);
        }

        using (var advancedContext = RecurringOccurrenceReservationTests.ReservationContext.Create())
        {
            var reservation = Reserve(advancedContext, "task266-run-advanced", "task266-use-advanced");
            var first = Commit(advancedContext, reservation);
            var advancedAt = advancedContext.Fixture.CreatedAt.AddSeconds(1);
            advancedContext.Fixture.Execute(
                "DROP TRIGGER trg_recurring_lease_uses_lifecycle_update; " +
                "UPDATE recording_runs SET status_code = 'recording', has_crossed_start_commit = 1, updated_at_utc = $at, version = 3 WHERE id = $runId; " +
                "UPDATE recurring_lease_uses SET status_code = 'consumed', updated_at_utc = $at, version = 2 WHERE use_id = $useId;",
                ("$at", advancedAt.UtcDateTime.Ticks),
                ("$runId", first.RunId!),
                ("$useId", first.UseId!));
            var replay = new RecurringOccurrenceStartCommitService(
                advancedContext.Fixture.Store,
                () => advancedAt,
                new RecurringOccurrenceReservationTests.MatchingReservationEnvironmentProvider())
                .Commit(advancedContext.Lease.LeaseId, advancedContext.Slot.OccurrenceIdentity);

            Assert.Equal(RecurringOccurrenceStartCommitStatus.AlreadyAdvanced, replay.Status);
            Assert.Null(replay.FirstCommitProof);
            Assert.Null(replay.FirstCommitReceipt);
        }
    }

    [Fact]
    public void ConcurrentIdenticalStartCommitsPublishAtMostOneProof()
    {
        using var context = RecurringOccurrenceReservationTests.ReservationContext.Create();
        _ = Reserve(context, "task266-run-concurrent", "task266-use-concurrent");
        var providerA = new CountingEnvironmentProvider(
            new RecurringOccurrenceReservationTests.MatchingReservationEnvironmentProvider());
        var providerB = new CountingEnvironmentProvider(
            new RecurringOccurrenceReservationTests.MatchingReservationEnvironmentProvider());

        var results = Task.WhenAll(
            Task.Run(() => new RecurringOccurrenceStartCommitService(
                new SqliteOperationalStore(context.Fixture.Store.DatabasePath),
                () => context.Fixture.CreatedAt,
                providerA).Commit(context.Lease.LeaseId, context.Slot.OccurrenceIdentity)),
            Task.Run(() => new RecurringOccurrenceStartCommitService(
                new SqliteOperationalStore(context.Fixture.Store.DatabasePath),
                () => context.Fixture.CreatedAt,
                providerB).Commit(context.Lease.LeaseId, context.Slot.OccurrenceIdentity))).GetAwaiter().GetResult();

        Assert.Single(results, result => result.Status == RecurringOccurrenceStartCommitStatus.Committed);
        Assert.Single(results, result => result.Status == RecurringOccurrenceStartCommitStatus.AlreadyCommitted);
        Assert.Single(results, result => result.FirstCommitProof is not null);
        Assert.Single(results, result => result.FirstCommitProof is null && result.FirstCommitReceipt is null);
        Assert.Equal(1, providerA.CaptureCount + providerB.CaptureCount);
    }

    [Theory]
    [InlineData("AfterRunPreparingUpdate")]
    [InlineData("AfterRunStartCommitUpdate")]
    [InlineData("AfterUseUpdate")]
    [InlineData("AfterLeaseExhaustionUpdate")]
    [InlineData("BeforeFinalReadback")]
    [InlineData("BeforeCommit")]
    public void TransactionFailurePointsNeverPublishAProof(string failurePointName)
    {
        var failurePoint = Enum.Parse<RecurringOccurrenceStartCommitFailurePoint>(failurePointName);
        using var context = RecurringOccurrenceReservationTests.ReservationContext.Create(maxUses: 1);
        var reservation = Reserve(context, "task266-run-rollback-" + failurePointName, "task266-use-rollback-" + failurePointName);
        var provider = new CountingEnvironmentProvider(
            new RecurringOccurrenceReservationTests.MatchingReservationEnvironmentProvider());
        var result = new RecurringOccurrenceStartCommitService(
            context.Fixture.Store,
            () => context.Fixture.CreatedAt,
            provider,
            failureHookForTest: point =>
            {
                if (point == failurePoint)
                    throw new InvalidOperationException("task266 rollback injection");
            }).Commit(context.Lease.LeaseId, context.Slot.OccurrenceIdentity);

        Assert.Equal(RecurringOccurrenceStartCommitStatus.Rejected, result.Status);
        Assert.Null(result.FirstCommitProof);
        Assert.Null(result.FirstCommitReceipt);
        Assert.False(result.CommittedNow);
        Assert.Equal(RecordingRunStatus.Created,
            new SqliteRecordingRunRepository(context.Fixture.Store).Get(reservation.RunId!).Status);
        Assert.Equal(LeaseUseStatus.Reserved,
            new SqliteRecurringLeaseUseAccountingReader(context.Fixture.Store).TryGetByOccurrence(context.Slot.OccurrenceIdentity)!.Status);
    }

    [Fact]
    public void PostCommitIssuerFailureIsDurableAndCannotBeRetriedForProof()
    {
        using var context = RecurringOccurrenceReservationTests.ReservationContext.Create();
        var reservation = Reserve(context, "task266-run-post-commit", "task266-use-post-commit");
        var provider = new CountingEnvironmentProvider(
            new RecurringOccurrenceReservationTests.MatchingReservationEnvironmentProvider());
        var issuerCalls = 0;
        Func<RecurringStartCommitReceipt, RecurringLeaseUseProof> failingIssuer = _ =>
        {
            issuerCalls++;
            throw new InvalidOperationException("task266 issuer failure");
        };
        var service = new RecurringOccurrenceStartCommitService(
            context.Fixture.Store,
            () => context.Fixture.CreatedAt,
            provider,
            recurringProofIssuerForTest: failingIssuer);

        var result = service.Commit(context.Lease.LeaseId, context.Slot.OccurrenceIdentity);
        Assert.Equal(RecurringOccurrenceStartCommitStatus.CommittedWithoutProof, result.Status);
        Assert.Equal(RecurringOccurrenceStartCommitReasonCodes.ProofUnavailableAfterCommit, result.ReasonCode);
        Assert.True(result.CommittedNow);
        Assert.False(result.Succeeded);
        Assert.False(result.HasFirstCommitProof);
        Assert.False(result.CanStartCapture);
        Assert.Null(result.FirstCommitProof);
        Assert.Null(result.FirstCommitReceipt);
        Assert.Equal(1, issuerCalls);

        var run = new SqliteRecordingRunRepository(context.Fixture.Store).Get(reservation.RunId!);
        var use = new SqliteRecurringLeaseUseAccountingReader(context.Fixture.Store)
            .TryGetByOccurrence(context.Slot.OccurrenceIdentity);
        Assert.Equal(RecordingRunStatus.StartCommitted, run.Status);
        Assert.True(run.HasCrossedStartCommit);
        Assert.Equal(LeaseUseStatus.StartCommitted, use!.Status);

        var replay = service.Commit(context.Lease.LeaseId, context.Slot.OccurrenceIdentity);
        Assert.Equal(RecurringOccurrenceStartCommitStatus.AlreadyCommitted, replay.Status);
        Assert.Null(replay.FirstCommitProof);
        Assert.Null(replay.FirstCommitReceipt);
        Assert.Equal(1, issuerCalls);
        Assert.Equal(1, provider.CaptureCount);
    }

    [Fact]
    public void RecurringProofIsRejectedByInteractiveAndOneShotStandingGatesWithoutConsumption()
    {
        using var context = RecurringOccurrenceReservationTests.ReservationContext.Create();
        var reservation = Reserve(context, "task266-run-cross-gate", "task266-use-cross-gate");
        var result = Commit(context, reservation);
        var proof = Assert.IsType<RecurringLeaseUseProof>(result.FirstCommitProof);
        var currentPlan = new CapturePlan(
            "wgc-continuous",
            "wgc-continuous",
            new CaptureBackendSelectionEvidence("wgc-continuous", "wgc-continuous", "test", "cached", null, false),
            "display_surface",
            "display",
            null,
            nint.Zero,
            null);
        var recording = new Recording("task266-interactive-recording");

        Assert.False(CaptureAuthorizationGate.TryConsumeInteractive(
            proof,
            recording,
            currentPlan,
            confirmation: null,
            allowSyntheticTestAuthorization: true,
            nowUtc: proof.IssuedAtUtc,
            out var interactiveReason));
        Assert.Equal("proof_kind_not_allowed", interactiveReason);
        Assert.True(proof.CheckAvailableAt(proof.IssuedAtUtc, out var interactiveAvailabilityReason), interactiveAvailabilityReason);

        Assert.False(StandingLeaseExecutionGate.TryAuthorizeAndConsumeStandingLeaseUseInTransaction(
            proof,
            snapshot: null,
            environment: null,
            out var standingReason));
        Assert.Equal("proof_kind_not_allowed", standingReason);
        Assert.True(proof.CheckAvailableAt(proof.IssuedAtUtc, out var standingAvailabilityReason), standingAvailabilityReason);
    }

    [Fact]
    public void RecurringProofTryConsumeIsAtomicAndOneTime()
    {
        using var context = RecurringOccurrenceReservationTests.ReservationContext.Create();
        var reservation = Reserve(context, "task266-run-consume", "task266-use-consume");
        var result = Commit(context, reservation);
        var proof = Assert.IsType<RecurringLeaseUseProof>(result.FirstCommitProof);
        var outcomes = new ConcurrentBag<bool>();

        Parallel.For(0, 64, _ =>
        {
            proof.TryConsume(proof.IssuedAtUtc, out var reason);
            outcomes.Add(string.IsNullOrEmpty(reason));
        });

        Assert.Equal(1, outcomes.Count(value => value));
        Assert.Equal(63, outcomes.Count(value => !value));
        Assert.Equal(CaptureAuthorizationProofState.Consumed, proof.State);
    }

    private static RecurringOccurrenceReservationResult Reserve(
        RecurringOccurrenceReservationTests.ReservationContext context,
        string runId,
        string useId) =>
        ReserveSlot(context, context.Slot, runId, useId);

    private static RecurringOccurrenceReservationResult ReserveSlot(
        RecurringOccurrenceReservationTests.ReservationContext context,
        RecurringOccurrenceSlotSnapshot slot,
        string runId,
        string useId) =>
        new RecurringOccurrenceReservationService(
            context.Fixture.Store,
            () => slot.ScheduledStartUtc!.Value,
            () => runId,
            () => useId)
        .Reserve(context.Lease.LeaseId, slot.OccurrenceIdentity);

    private static RecurringOccurrenceStartCommitResult Commit(
        RecurringOccurrenceReservationTests.ReservationContext context,
        RecurringOccurrenceReservationResult reservation) =>
        CommitSlot(context, reservation, context.Slot.ScheduledStartUtc!.Value);

    private static RecurringOccurrenceStartCommitResult CommitSlot(
        RecurringOccurrenceReservationTests.ReservationContext context,
        RecurringOccurrenceReservationResult reservation,
        DateTimeOffset nowUtc) =>
        new RecurringOccurrenceStartCommitService(
            context.Fixture.Store,
            () => nowUtc,
            new RecurringOccurrenceReservationTests.MatchingReservationEnvironmentProvider())
        .Commit(context.Lease.LeaseId, reservation.OccurrenceIdentity);

    private static RecurringStartCommitReceipt CopyReceipt(
        RecurringStartCommitReceipt source,
        PlanDefinition? plan = null,
        PlanOccurrence? occurrence = null,
        RecurringConsentLease? lease = null,
        RecurringOccurrenceExecutionSpecification? specification = null,
        RecordingRun? run = null,
        LeaseUse? use = null,
        RecurringLeaseLocalApprovalEvidence? approval = null,
        DateTimeOffset? committedAtUtc = null,
        RecurringLeaseQuotaSnapshot? quota = null,
        IEnumerable<RecurringLeaseUseAccountingEntry>? leaseEntries = null) =>
        new(
            plan ?? source.Plan,
            occurrence ?? source.Occurrence,
            lease ?? source.Lease,
            specification ?? source.Specification,
            run ?? source.Run,
            use ?? source.Use,
            approval ?? source.Approval,
            committedAtUtc ?? source.CommittedAtUtc,
            quota ?? source.Quota,
            leaseEntries ?? source.LeaseEntries);

    private static RecurringLeaseQuotaSnapshot TamperQuota(
        RecurringLeaseQuotaSnapshot source,
        string field)
    {
        var admissionUses = source.AdmissionUsedUses;
        var admissionDuration = source.AdmissionUsedDuration;
        var permanentUses = source.PermanentlyConsumedUses;
        var chargedDuration = source.ConservativelyChargedDuration;
        var remainingUses = source.RemainingReservableUses;
        var remainingDuration = source.RemainingReservableDuration;
        var inFlight = source.InFlightUseCount;
        var temporary = source.IsTemporarilyUnavailable;
        var terminal = source.IsTerminallyExhausted;
        var reason = source.ReasonCode;
        var leaseVersion = source.LeaseVersion;
        var leaseId = source.LeaseId;
        var authorizationDigest = source.LeaseAuthorizationDigest;

        switch (field)
        {
            case "admission-uses":
                admissionUses++;
                break;
            case "admission-duration":
                admissionDuration += TimeSpan.FromMilliseconds(1);
                break;
            case "permanent-uses":
                permanentUses++;
                break;
            case "charged-duration":
                chargedDuration += TimeSpan.FromMilliseconds(1);
                break;
            case "remaining-uses":
                remainingUses = source.RemainingReservableUses == 0 ? 1 : source.RemainingReservableUses - 1;
                break;
            case "remaining-duration":
                remainingDuration += TimeSpan.FromMilliseconds(1);
                break;
            case "in-flight":
                inFlight++;
                break;
            case "temporary":
                temporary = !temporary;
                break;
            case "terminal":
                terminal = !terminal;
                break;
            case "reason":
                reason = "task266R-tampered-quota-reason";
                break;
            case "lease-version":
                leaseVersion++;
                break;
            case "lease-id":
                leaseId = "task266R-tampered-lease";
                break;
            case "authorization-digest":
                authorizationDigest = "task266R-tampered-authorization";
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(field), field, null);
        }

        return new RecurringLeaseQuotaSnapshot(
            leaseId,
            authorizationDigest,
            leaseVersion,
            admissionUses,
            admissionDuration,
            permanentUses,
            chargedDuration,
            remainingUses,
            remainingDuration,
            inFlight,
            temporary,
            terminal,
            reason);
    }

    private static IReadOnlyList<RecurringLeaseUseAccountingEntry> TamperEntries(
        RecurringStartCommitReceipt source,
        string variant)
    {
        var entries = source.LeaseEntries.ToList();
        var current = entries.Single(entry => entry.UseId == source.Use.Id);
        switch (variant)
        {
            case "missing-current-use":
                entries.RemoveAll(entry => entry.UseId == source.Use.Id);
                break;
            case "duplicate-current-use":
                entries.Add(current);
                break;
            case "duplicate-run":
                entries.Add(RecurringLeaseUseAccountingEntry.Rehydrate(
                    source.Lease.Id,
                    "task266R-duplicate-run-use",
                    "task266R-duplicate-run-occurrence",
                    current.RunId,
                    LeaseUseStatus.Available,
                    0,
                    TimeSpan.Zero,
                    null));
                break;
            case "duplicate-occurrence":
                entries.Add(RecurringLeaseUseAccountingEntry.Rehydrate(
                    source.Lease.Id,
                    "task266R-duplicate-occurrence-use",
                    current.OccurrenceIdentity,
                    "task266R-duplicate-occurrence-run",
                    LeaseUseStatus.Available,
                    0,
                    TimeSpan.Zero,
                    null));
                break;
            case "cross-lease":
                entries[0] = RecurringLeaseUseAccountingEntry.Rehydrate(
                    "task266R-other-lease",
                    current.UseId,
                    current.OccurrenceIdentity,
                    current.RunId,
                    current.Status,
                    current.ReservedUseCount,
                    current.ReservedDuration,
                    current.ActualSettledDuration);
                break;
            case "wrong-run":
                entries[0] = RecurringLeaseUseAccountingEntry.Rehydrate(
                    current.LeaseId,
                    current.UseId,
                    current.OccurrenceIdentity,
                    "task266R-wrong-run",
                    current.Status,
                    current.ReservedUseCount,
                    current.ReservedDuration,
                    current.ActualSettledDuration);
                break;
            case "wrong-occurrence":
                entries[0] = RecurringLeaseUseAccountingEntry.Rehydrate(
                    current.LeaseId,
                    current.UseId,
                    "task266R-wrong-occurrence",
                    current.RunId,
                    current.Status,
                    current.ReservedUseCount,
                    current.ReservedDuration,
                    current.ActualSettledDuration);
                break;
            case "wrong-status":
                entries[0] = RecurringLeaseUseAccountingEntry.Rehydrate(
                    current.LeaseId,
                    current.UseId,
                    current.OccurrenceIdentity,
                    current.RunId,
                    LeaseUseStatus.Consumed,
                    1,
                    current.ReservedDuration,
                    null);
                break;
            case "wrong-count":
                entries[0] = RecurringLeaseUseAccountingEntry.Rehydrate(
                    current.LeaseId,
                    current.UseId,
                    current.OccurrenceIdentity,
                    current.RunId,
                    LeaseUseStatus.Available,
                    0,
                    TimeSpan.Zero,
                    null);
                break;
            case "wrong-duration":
                entries[0] = RecurringLeaseUseAccountingEntry.Rehydrate(
                    current.LeaseId,
                    current.UseId,
                    current.OccurrenceIdentity,
                    current.RunId,
                    LeaseUseStatus.StartCommitted,
                    1,
                    current.ReservedDuration + TimeSpan.FromMilliseconds(1),
                    null);
                break;
            case "wrong-actual":
                entries[0] = RecurringLeaseUseAccountingEntry.Rehydrate(
                    current.LeaseId,
                    current.UseId,
                    current.OccurrenceIdentity,
                    current.RunId,
                    LeaseUseStatus.Settled,
                    1,
                    current.ReservedDuration,
                    TimeSpan.FromSeconds(1));
                break;
            case "domain-invalid-duration":
                entries[0] = RecurringLeaseUseAccountingEntry.Rehydrate(
                    current.LeaseId,
                    current.UseId,
                    current.OccurrenceIdentity,
                    current.RunId,
                    LeaseUseStatus.StartCommitted,
                    1,
                    TimeSpan.MaxValue,
                    null);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(variant), variant, null);
        }

        return entries;
    }

    private static RecurringLeaseUseAccountingEntry CreateEntry(
        RecurringConsentLease lease,
        string suffix,
        LeaseUseStatus status,
        TimeSpan? actualSettledDuration = null)
    {
        var reserved = status == LeaseUseStatus.Available ? 0 : 1;
        var duration = reserved == 0 ? TimeSpan.Zero : lease.PerRunDuration;
        var actual = status == LeaseUseStatus.Settled
            ? actualSettledDuration ?? TimeSpan.FromSeconds(30)
            : (TimeSpan?)null;
        return RecurringLeaseUseAccountingEntry.CreateFor(
            lease,
            "task266R-history-use-" + suffix,
            "task266R-history-occurrence-" + suffix,
            "task266R-history-run-" + suffix,
            status,
            reserved,
            duration,
            actual);
    }

    private static void AssertExactQuota(
        RecurringLeaseQuotaSnapshot expected,
        RecurringLeaseQuotaSnapshot actual)
    {
        Assert.Equal(expected.LeaseId, actual.LeaseId);
        Assert.Equal(expected.LeaseAuthorizationDigest, actual.LeaseAuthorizationDigest);
        Assert.Equal(expected.LeaseVersion, actual.LeaseVersion);
        Assert.Equal(expected.AdmissionUsedUses, actual.AdmissionUsedUses);
        Assert.Equal(expected.AdmissionUsedDuration, actual.AdmissionUsedDuration);
        Assert.Equal(expected.PermanentlyConsumedUses, actual.PermanentlyConsumedUses);
        Assert.Equal(expected.ConservativelyChargedDuration, actual.ConservativelyChargedDuration);
        Assert.Equal(expected.RemainingReservableUses, actual.RemainingReservableUses);
        Assert.Equal(expected.RemainingReservableDuration, actual.RemainingReservableDuration);
        Assert.Equal(expected.InFlightUseCount, actual.InFlightUseCount);
        Assert.Equal(expected.IsTemporarilyUnavailable, actual.IsTemporarilyUnavailable);
        Assert.Equal(expected.IsTerminallyExhausted, actual.IsTerminallyExhausted);
        Assert.Equal(expected.ReasonCode, actual.ReasonCode);
    }

    private static RecurringStartCommitReceipt TamperReceipt(
        RecurringStartCommitReceipt source,
        string tamper)
    {
        var plan = source.Plan;
        var occurrence = source.Occurrence;
        var lease = source.Lease;
        var run = source.Run;
        var use = source.Use;
        var approval = source.Approval;
        var committedAt = source.CommittedAtUtc;

        switch (tamper)
        {
            case "plan-status":
                plan = PlanDefinition.Rehydrate(plan.Id, plan.IsOneTime, PlanDefinitionStatus.Draft,
                    plan.CreatedAtUtc, plan.UpdatedAtUtc, plan.Version);
                break;
            case "occurrence-plan":
                occurrence = PlanOccurrence.Rehydrate(occurrence.Id, "task266-other-plan",
                    occurrence.WindowStartUtc, occurrence.WindowEndUtc, occurrence.CreatedAtUtc,
                    occurrence.Status, occurrence.RunId, occurrence.TerminalReasonCode,
                    occurrence.UpdatedAtUtc, occurrence.Version);
                break;
            case "lease-status":
                lease = RehydrateLease(source.Lease, ConsentLeaseStatus.Pending);
                break;
            case "lease-digest":
                var authorization = new RecurringConsentLeaseAuthorizationRef(
                    lease.Id,
                    lease.ConfigurationRef,
                    lease.ValidFromUtc,
                    lease.ValidUntilUtc,
                    lease.AuthorizedPlanLatestEndUtc,
                    lease.PerRunDuration.Add(TimeSpan.FromMilliseconds(1)),
                    lease.MaxUses,
                    lease.MaxCumulativeDuration);
                lease = RecurringConsentLease.Rehydrate(
                    authorization.LeaseId,
                    authorization.ConfigurationRef,
                    authorization.ValidFromUtc,
                    authorization.ValidUntilUtc,
                    authorization.AuthorizedPlanLatestEndUtc,
                    authorization.PerRunDuration,
                    authorization.MaxUses,
                    authorization.MaxCumulativeDuration,
                    authorization.AuthorizationDigest,
                    source.Lease.Status,
                    source.Lease.CreatedAtUtc,
                    source.Lease.UpdatedAtUtc,
                    source.Lease.Version);
                break;
            case "approval-sid":
                var sid = "S-1-5-21-task266-tampered";
                approval = RehydrateApproval(source.Approval, currentUserSid: sid);
                break;
            case "run-status":
                run = RecordingRun.Rehydrate(run.Id, run.OccurrenceId, run.CreatedAtUtc,
                    RecordingRunStatus.Recording, hasCrossedStartCommit: true,
                    run.MediaArtifactId, run.BundleId, run.TerminalReasonCode,
                    run.UpdatedAtUtc, version: 3);
                break;
            case "run-media":
                run = RecordingRun.Rehydrate(run.Id, run.OccurrenceId, run.CreatedAtUtc,
                    run.Status, run.HasCrossedStartCommit, "task266-media", run.BundleId,
                    run.TerminalReasonCode, run.UpdatedAtUtc, run.Version);
                break;
            case "run-version":
                run = RecordingRun.Rehydrate(run.Id, run.OccurrenceId, run.CreatedAtUtc,
                    run.Status, run.HasCrossedStartCommit, run.MediaArtifactId, run.BundleId,
                    run.TerminalReasonCode, run.UpdatedAtUtc, version: 3);
                break;
            case "use-status":
                use = LeaseUse.Rehydrate(use.Id, use.LeaseId, use.OccurrenceId, use.RunId,
                    use.CreatedAtUtc, LeaseUseStatus.Consumed, use.ReservedUseCount,
                    use.ReservedDuration, use.ActualSettledDuration, use.UpdatedAtUtc, version: 2);
                break;
            case "use-duration":
                use = LeaseUse.Rehydrate(use.Id, use.LeaseId, use.OccurrenceId, use.RunId,
                    use.CreatedAtUtc, use.Status, use.ReservedUseCount,
                    use.ReservedDuration.Add(TimeSpan.FromMilliseconds(1)),
                    use.ActualSettledDuration, use.UpdatedAtUtc, use.Version);
                break;
            case "timestamp":
                committedAt = source.CommittedAtUtc.AddTicks(1);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(tamper), tamper, null);
        }

        return CopyReceipt(source, plan, occurrence, lease, source.Specification, run, use, approval, committedAt);
    }

    private static RecurringConsentLease RehydrateLease(
        RecurringConsentLease source,
        ConsentLeaseStatus status) =>
        RecurringConsentLease.Rehydrate(
            source.Id,
            source.ConfigurationRef,
            source.ValidFromUtc,
            source.ValidUntilUtc,
            source.AuthorizedPlanLatestEndUtc,
            source.PerRunDuration,
            source.MaxUses,
            source.MaxCumulativeDuration,
            source.AuthorizationDigest,
            status,
            source.CreatedAtUtc,
            source.UpdatedAtUtc,
            source.Version);

    private static RecurringLeaseLocalApprovalEvidence RehydrateApproval(
        RecurringLeaseLocalApprovalEvidence source,
        string? currentUserSid = null,
        string? sessionBinding = null) =>
        RecurringLeaseLocalApprovalEvidence.Rehydrate(
            source.ApprovalId,
            source.LeaseId,
            source.PlanId,
            source.ConfigurationDigest,
            source.AuthorizationDigest,
            currentUserSid ?? source.CurrentUserSid,
            sessionBinding ?? source.SessionBinding,
            source.ApprovedAtUtc,
            source.ApprovalKind,
            source.ApprovalVersion,
            RecurringLeaseLocalApprovalDigest.Compute(
                source.ApprovalId,
                source.LeaseId,
                source.PlanId,
                source.ConfigurationDigest,
                source.AuthorizationDigest,
                currentUserSid ?? source.CurrentUserSid,
                sessionBinding ?? source.SessionBinding,
                source.ApprovedAtUtc,
                source.ApprovalKind,
                source.ApprovalVersion));

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

    private sealed class ThrowingEnvironmentProvider : IRecurringOccurrenceEnvironmentProvider
    {
        public StandingLeaseExecutionEnvironment Capture(RecurringOccurrenceEnvironmentCaptureRequest request) =>
            throw new InvalidOperationException("The environment provider must not be called.");
    }
}
