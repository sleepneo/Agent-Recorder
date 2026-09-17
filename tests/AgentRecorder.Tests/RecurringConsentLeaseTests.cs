using AgentRecorder.Core.Automation;
using Xunit;

namespace AgentRecorder.Tests;

[Collection("NonParallel-AgentRecorderDataDir")]
public sealed class RecurringConsentLeaseTests
{
    private static readonly DateTimeOffset T0 = new(2026, 9, 10, 10, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan PerRun = TimeSpan.FromMinutes(2);

    [Fact]
    public void DailyAndWeeklyConfigurationCreatesPendingLeaseWithDeterministicSensitiveAuthorizationDigest()
    {
        var daily = Configuration("daily-plan", 1, "a");
        var weekly = Configuration("weekly-plan", 2, "b");
        var first = RecurringConsentLease.CreatePending("lease-daily", daily, T0, T0.AddHours(1), T0.AddMinutes(50), PerRun, 3, TimeSpan.FromMinutes(6), T0);
        var same = RecurringConsentLease.CreatePending("lease-daily", daily, T0, T0.AddHours(1), T0.AddMinutes(50), PerRun, 3, TimeSpan.FromMinutes(6), T0);
        var differentCreatedAt = RecurringConsentLease.CreatePending("lease-daily", daily, T0, T0.AddHours(1), T0.AddMinutes(50), PerRun, 3, TimeSpan.FromMinutes(6), T0.AddMinutes(1));
        var variants = new[]
        {
            RecurringConsentLease.CreatePending("lease-other", daily, T0, T0.AddHours(1), T0.AddMinutes(50), PerRun, 3, TimeSpan.FromMinutes(6), T0),
            RecurringConsentLease.CreatePending("lease-daily", weekly, T0, T0.AddHours(1), T0.AddMinutes(50), PerRun, 3, TimeSpan.FromMinutes(6), T0),
            RecurringConsentLease.CreatePending("lease-daily", daily, T0.AddTicks(1), T0.AddHours(1), T0.AddMinutes(50), PerRun, 3, TimeSpan.FromMinutes(6), T0),
            RecurringConsentLease.CreatePending("lease-daily", daily, T0, T0.AddHours(1), T0.AddMinutes(50), TimeSpan.FromTicks(PerRun.Ticks + 1), 3, TimeSpan.FromTicks(TimeSpan.FromMinutes(6).Ticks + 1), T0),
            RecurringConsentLease.CreatePending("lease-daily", daily, T0, T0.AddHours(1), T0.AddMinutes(50), PerRun, 4, TimeSpan.FromMinutes(8), T0),
        };

        Assert.Equal(first.AuthorizationDigest, same.AuthorizationDigest);
        Assert.Equal(first.AuthorizationDigest, differentCreatedAt.AuthorizationDigest);
        Assert.StartsWith(RecurringConsentLeaseAuthorizationRef.DigestPrefix, first.AuthorizationDigest, StringComparison.Ordinal);
        Assert.All(variants, variant => Assert.NotEqual(first.AuthorizationDigest, variant.AuthorizationDigest));
        Assert.Equal(ConsentLeaseStatus.Pending, first.Status);
        Assert.Equal(0, first.Version);
        Assert.Equal(first.AuthorizationDigest, first.Authorization.AuthorizationDigest);
    }

    [Fact]
    public void AuthorizationCreationAndRehydrateRejectInvalidRelationsAndDigestTampering()
    {
        var configuration = Configuration();
        AssertReason(RecurringConsentLeaseReasonCodes.NonUtcTime, () =>
            RecurringConsentLease.CreatePending("lease", configuration, T0, T0.AddHours(1).ToOffset(TimeSpan.FromHours(1)), T0.AddMinutes(50), PerRun, 3, TimeSpan.FromMinutes(6), T0));
        AssertReason(RecurringConsentLeaseReasonCodes.WindowInvalid, () =>
            RecurringConsentLease.CreatePending("lease", configuration, T0.AddHours(1), T0, T0.AddMinutes(50), PerRun, 3, TimeSpan.FromMinutes(6), T0));
        AssertReason(RecurringConsentLeaseReasonCodes.LatestEndInvalid, () =>
            RecurringConsentLease.CreatePending("lease", configuration, T0, T0.AddHours(1), T0.AddHours(1), PerRun, 3, TimeSpan.FromMinutes(6), T0));
        AssertReason(RecurringConsentLeaseReasonCodes.PerRunDurationInvalid, () =>
            RecurringConsentLease.CreatePending("lease", configuration, T0, T0.AddHours(1), T0.AddMinutes(50), TimeSpan.Zero, 3, TimeSpan.FromMinutes(6), T0));
        AssertReason(RecurringConsentLeaseReasonCodes.MaxUsesInvalid, () =>
            RecurringConsentLease.CreatePending("lease", configuration, T0, T0.AddHours(1), T0.AddMinutes(50), PerRun, 0, TimeSpan.FromMinutes(6), T0));
        AssertReason(RecurringConsentLeaseReasonCodes.CumulativeDurationInvalid, () =>
            RecurringConsentLease.CreatePending("lease", configuration, T0, T0.AddHours(1), T0.AddMinutes(50), PerRun, 3, TimeSpan.FromTicks(PerRun.Ticks - 1), T0));
        AssertReason(RecurringConsentLeaseReasonCodes.AuthorizationOverflow, () =>
            RecurringConsentLease.CreatePending("lease", configuration, T0, T0.AddHours(1), T0.AddMinutes(50), TimeSpan.FromTicks(2), long.MaxValue, TimeSpan.FromTicks(2), T0));
        AssertReason(RecurringConsentLeaseReasonCodes.AuthorizationDigestInvalid, () =>
            RecurringConsentLeaseAuthorizationRef.Rehydrate("lease", configuration, T0, T0.AddHours(1), T0.AddMinutes(50), PerRun, 3, TimeSpan.FromMinutes(6), "bad"));

        var lease = CreateLease();
        var mismatch = lease.AuthorizationDigest[..^1] + (lease.AuthorizationDigest[^1] == '0' ? '1' : '0');
        AssertReason(RecurringConsentLeaseReasonCodes.AuthorizationDigestMismatch, () =>
            RecurringConsentLease.Rehydrate("lease", configuration, lease.ValidFromUtc, lease.ValidUntilUtc, lease.AuthorizedPlanLatestEndUtc, lease.PerRunDuration, lease.MaxUses, lease.MaxCumulativeDuration, mismatch, ConsentLeaseStatus.Pending, T0, T0, 0));
    }

    [Fact]
    public void LifecycleIsPendingOnlyForPublicCreationAndDigestExcludesMutableState()
    {
        var lease = CreateLease();
        var digest = lease.AuthorizationDigest;
        Assert.True(lease.TryTransition(ConsentLeaseStatus.Active, T0).Succeeded);
        Assert.Equal(digest, lease.AuthorizationDigest);
        Assert.Equal(1, lease.Version);
        Assert.Equal(ConsentLeaseStatus.Active, lease.Status);
        Assert.Equal(RecurringConsentLeaseReasonCodes.ExhaustionRequiresQuotaDecision, lease.TryTransition(ConsentLeaseStatus.Exhausted, T0.AddMinutes(1)).ReasonCode);
        Assert.True(lease.TryTransition(ConsentLeaseStatus.Revoked, T0.AddMinutes(1)).Succeeded);
        Assert.False(lease.TryTransition(ConsentLeaseStatus.Active, T0.AddMinutes(2)).Succeeded);
    }

    [Fact]
    public void OccurrenceRequestEnforcesExactWindowAndLeaseConfigurationAtReservation()
    {
        var lease = Activate(CreateLease());
        var valid = RecurringLeaseOccurrenceRequest.CreateFor(lease, "occ-1", T0, T0.AddMinutes(1), T0.AddMinutes(3), PerRun);
        var allowed = RecurringLeaseQuotaCalculator.EvaluateReservation(lease, Array.Empty<RecurringLeaseUseAccountingEntry>(), valid, T0);
        Assert.True(allowed.IsAllowed);
        Assert.Equal(1, allowed.ReservedUseCount);
        Assert.Equal(PerRun, allowed.ReservedDuration);

        var tickMismatch = RecurringLeaseOccurrenceRequest.CreateFor(lease, "occ-tick", T0, T0.AddMinutes(1), T0.AddMinutes(3).AddTicks(1), TimeSpan.FromTicks(PerRun.Ticks + 1));
        var millisecondMismatch = RecurringLeaseOccurrenceRequest.CreateFor(lease, "occ-ms", T0, T0.AddMinutes(1), T0.AddMinutes(3).AddTicks(TimeSpan.TicksPerMillisecond), TimeSpan.FromTicks(PerRun.Ticks + TimeSpan.TicksPerMillisecond));
        Assert.Equal(RecurringLeaseQuotaReasonCodes.DurationMismatch, RecurringLeaseQuotaCalculator.EvaluateReservation(lease, Array.Empty<RecurringLeaseUseAccountingEntry>(), tickMismatch, T0).ReasonCode);
        Assert.Equal(RecurringLeaseQuotaReasonCodes.DurationMismatch, RecurringLeaseQuotaCalculator.EvaluateReservation(lease, Array.Empty<RecurringLeaseUseAccountingEntry>(), millisecondMismatch, T0).ReasonCode);

        var afterUntilLatest = lease.ValidUntilUtc.Subtract(PerRun);
        var afterUntil = RecurringLeaseOccurrenceRequest.CreateFor(lease, "occ-after-until", T0, afterUntilLatest, lease.ValidUntilUtc, PerRun);
        Assert.Equal(RecurringLeaseQuotaReasonCodes.OccurrenceAfterValidity, RecurringLeaseQuotaCalculator.EvaluateReservation(lease, Array.Empty<RecurringLeaseUseAccountingEntry>(), afterUntil, T0).ReasonCode);
        Assert.Equal(RecurringLeaseQuotaReasonCodes.Expired, RecurringLeaseQuotaCalculator.EvaluateReservation(lease, Array.Empty<RecurringLeaseUseAccountingEntry>(), valid, lease.ValidUntilUtc).ReasonCode);
        Assert.Equal(RecurringLeaseQuotaReasonCodes.BeforeValidity, RecurringLeaseQuotaCalculator.EvaluateReservation(lease, Array.Empty<RecurringLeaseUseAccountingEntry>(), valid, lease.ValidFromUtc.AddTicks(-1)).ReasonCode);

        var before = new RecurringLeaseOccurrenceRequest("occ-before", lease.ConfigurationRef, T0.AddTicks(-1), T0, T0.AddMinutes(2), PerRun);
        Assert.Equal(RecurringLeaseQuotaReasonCodes.OccurrenceBeforeValidity, RecurringLeaseQuotaCalculator.EvaluateReservation(lease, Array.Empty<RecurringLeaseUseAccountingEntry>(), before, T0).ReasonCode);
        var afterLatestStart = lease.AuthorizedPlanLatestEndUtc.Subtract(PerRun).AddTicks(1);
        var afterLatest = new RecurringLeaseOccurrenceRequest("occ-latest", lease.ConfigurationRef, T0, afterLatestStart, lease.AuthorizedPlanLatestEndUtc.AddTicks(1), PerRun);
        Assert.Equal(RecurringLeaseQuotaReasonCodes.OccurrenceAfterAuthorizedLatestEnd, RecurringLeaseQuotaCalculator.EvaluateReservation(lease, Array.Empty<RecurringLeaseUseAccountingEntry>(), afterLatest, T0).ReasonCode);

        var otherConfiguration = Configuration("other-plan");
        var drifted = new RecurringLeaseOccurrenceRequest("occ-drift", otherConfiguration, T0, T0.AddMinutes(1), T0.AddMinutes(3), PerRun);
        Assert.Equal(RecurringLeaseQuotaReasonCodes.ConfigurationMismatch, RecurringLeaseQuotaCalculator.EvaluateReservation(lease, Array.Empty<RecurringLeaseUseAccountingEntry>(), drifted, T0).ReasonCode);
    }

    [Fact]
    public void StatusDecisionsRespectPendingRevokedRejectedExpiredAndExhausted()
    {
        var request = RecurringLeaseOccurrenceRequest.CreateFor(CreateLease(), "occ", T0, T0.AddMinutes(1), T0.AddMinutes(3), PerRun);
        var pending = CreateLease();
        Assert.Equal(RecurringLeaseQuotaReasonCodes.Pending, Evaluate(pending, request).ReasonCode);

        var rejected = CreateLease();
        Assert.True(rejected.TryTransition(ConsentLeaseStatus.Rejected, T0).Succeeded);
        Assert.Equal(RecurringLeaseQuotaReasonCodes.Rejected, Evaluate(rejected, request).ReasonCode);

        var revoked = CreateLease();
        Assert.True(revoked.TryTransition(ConsentLeaseStatus.Revoked, T0).Succeeded);
        Assert.Equal(RecurringLeaseQuotaReasonCodes.Revoked, Evaluate(revoked, request).ReasonCode);

        var expired = CreateLease();
        Assert.True(expired.TryTransition(ConsentLeaseStatus.Expired, T0.AddHours(1)).Succeeded);
        Assert.Equal(RecurringLeaseQuotaReasonCodes.Expired, Evaluate(expired, request).ReasonCode);

        var exhausted = RecurringConsentLease.Rehydrate(
            "lease",
            request.ConfigurationRef,
            T0,
            T0.AddHours(1),
            T0.AddMinutes(50),
            PerRun,
            3,
            TimeSpan.FromMinutes(6),
            CreateLease().AuthorizationDigest,
            ConsentLeaseStatus.Exhausted,
            T0,
            T0,
            1);
        Assert.Equal(RecurringLeaseQuotaReasonCodes.Exhausted, Evaluate(exhausted, request).ReasonCode);
    }

    [Fact]
    public void SixLeaseUseStatesProduceTheRequiredAccountingMatrix()
    {
        var lease = Activate(CreateLease(maxUses: 4, maxCumulativeDuration: TimeSpan.FromMinutes(8)));
        var statuses = new[]
        {
            LeaseUseStatus.Available,
            LeaseUseStatus.Reserved,
            LeaseUseStatus.StartCommitted,
            LeaseUseStatus.Consumed,
            LeaseUseStatus.Settled,
            LeaseUseStatus.StartedUnknown,
        };

        foreach (var status in statuses)
        {
            TimeSpan? actual = status == LeaseUseStatus.Settled ? TimeSpan.Zero : null;
            var entry = RecurringLeaseUseAccountingEntry.CreateFor(lease, "use-" + status, "occ-" + status, "run-" + status, status,
                status == LeaseUseStatus.Available ? 0 : 1,
                status == LeaseUseStatus.Available ? TimeSpan.Zero : PerRun,
                actual);
            var snapshot = RecurringLeaseQuotaCalculator.Calculate(lease, new[] { entry });
            Assert.Equal(status == LeaseUseStatus.Available ? 0 : 1, snapshot.AdmissionUsedUses);
            Assert.Equal(status is LeaseUseStatus.Available or LeaseUseStatus.Settled ? actual ?? TimeSpan.Zero : status == LeaseUseStatus.Reserved ? PerRun : PerRun, snapshot.AdmissionUsedDuration);
            Assert.Equal(status is LeaseUseStatus.StartCommitted or LeaseUseStatus.Consumed or LeaseUseStatus.Settled or LeaseUseStatus.StartedUnknown ? 1 : 0, snapshot.PermanentlyConsumedUses);
            Assert.Equal(status is LeaseUseStatus.StartCommitted or LeaseUseStatus.Consumed or LeaseUseStatus.StartedUnknown ? PerRun : status == LeaseUseStatus.Settled ? TimeSpan.Zero : TimeSpan.Zero, snapshot.ConservativelyChargedDuration);
            Assert.Equal(status is LeaseUseStatus.Reserved or LeaseUseStatus.StartCommitted or LeaseUseStatus.Consumed ? 1 : 0, snapshot.InFlightUseCount);
        }
    }

    [Fact]
    public void SettledActualDurationReleasesOnlyDurationAndReservationReleaseRestoresCapacity()
    {
        var lease = Activate(CreateLease(maxUses: 2, maxCumulativeDuration: TimeSpan.FromMinutes(4)));
        var reserved = RecurringLeaseUseAccountingEntry.CreateFor(lease, "reserved", "occ-reserved", "run-reserved", LeaseUseStatus.Reserved, 1, PerRun, null);
        var temporary = RecurringLeaseQuotaCalculator.Calculate(lease, new[] { reserved });
        Assert.True(temporary.IsTemporarilyUnavailable);
        Assert.False(temporary.IsTerminallyExhausted);

        var empty = RecurringLeaseQuotaCalculator.Calculate(lease, Array.Empty<RecurringLeaseUseAccountingEntry>());
        Assert.Equal(2, empty.RemainingReservableUses);
        Assert.Equal(TimeSpan.FromMinutes(4), empty.RemainingReservableDuration);

        var settledZero = RecurringLeaseUseAccountingEntry.CreateFor(lease, "settled", "occ-settled", "run-settled", LeaseUseStatus.Settled, 1, PerRun, TimeSpan.Zero);
        var settledSnapshot = RecurringLeaseQuotaCalculator.Calculate(lease, new[] { settledZero });
        Assert.Equal(1, settledSnapshot.PermanentlyConsumedUses);
        Assert.Equal(TimeSpan.Zero, settledSnapshot.ConservativelyChargedDuration);
        Assert.Equal(1, settledSnapshot.RemainingReservableUses);
        Assert.Equal(TimeSpan.FromMinutes(4), settledSnapshot.RemainingReservableDuration);
    }

    [Fact]
    public void InFlightUseBlocksAnotherOccurrenceButTerminalExhaustionIsDistinguished()
    {
        var lease = Activate(CreateLease(maxUses: 2, maxCumulativeDuration: TimeSpan.FromMinutes(4)));
        var request = RecurringLeaseOccurrenceRequest.CreateFor(lease, "new-occ", T0, T0.AddMinutes(1), T0.AddMinutes(3), PerRun);
        var inFlight = RecurringLeaseUseAccountingEntry.CreateFor(lease, "use-1", "old-occ", "run-1", LeaseUseStatus.Consumed, 1, PerRun, null);
        var blocked = RecurringLeaseQuotaCalculator.EvaluateReservation(lease, new[] { inFlight }, request, T0);
        Assert.False(blocked.IsAllowed);
        Assert.Equal(RecurringLeaseQuotaReasonCodes.RunInFlight, blocked.ReasonCode);
        Assert.True(blocked.Quota!.IsTemporarilyUnavailable);
        Assert.False(blocked.Quota.IsTerminallyExhausted);

        var terminalLease = Activate(CreateLease(maxUses: 1, maxCumulativeDuration: PerRun));
        var unknown = RecurringLeaseUseAccountingEntry.CreateFor(terminalLease, "use-terminal", "old-terminal", "run-terminal", LeaseUseStatus.StartedUnknown, 1, PerRun, null);
        var terminal = RecurringLeaseQuotaCalculator.EvaluateReservation(
            terminalLease,
            new[] { unknown },
            RecurringLeaseOccurrenceRequest.CreateFor(terminalLease, "new-terminal", T0, T0.AddMinutes(1), T0.AddMinutes(3), PerRun),
            T0);
        Assert.False(terminal.IsAllowed);
        Assert.Equal(RecurringLeaseQuotaReasonCodes.QuotaExhausted, terminal.ReasonCode);
        Assert.True(terminal.Quota!.IsTerminallyExhausted);
        Assert.False(terminal.Quota.IsTemporarilyUnavailable);
    }

    [Fact]
    public void AnyExistingOccurrenceIsAtMostOnceAndDuplicateEvidenceFailsClosed()
    {
        var lease = Activate(CreateLease(maxUses: 4, maxCumulativeDuration: TimeSpan.FromMinutes(8)));
        var request = RecurringLeaseOccurrenceRequest.CreateFor(lease, "claimed", T0, T0.AddMinutes(1), T0.AddMinutes(3), PerRun);
        foreach (var status in new[] { LeaseUseStatus.Available, LeaseUseStatus.Settled, LeaseUseStatus.StartedUnknown })
        {
            var entry = RecurringLeaseUseAccountingEntry.CreateFor(lease,
                "use-" + status,
                "claimed",
                "run-" + status,
                status,
                status == LeaseUseStatus.Available ? 0 : 1,
                status == LeaseUseStatus.Available ? TimeSpan.Zero : PerRun,
                status == LeaseUseStatus.Settled ? TimeSpan.Zero : null);
            Assert.Equal(RecurringLeaseQuotaReasonCodes.OccurrenceAlreadyClaimed, RecurringLeaseQuotaCalculator.EvaluateReservation(lease, new[] { entry }, request, T0).ReasonCode);
        }

        var duplicateUse = new[]
        {
            RecurringLeaseUseAccountingEntry.CreateFor(lease, "same-use", "occ-a", "run-a", LeaseUseStatus.Available, 0, TimeSpan.Zero, null),
            RecurringLeaseUseAccountingEntry.CreateFor(lease, "same-use", "occ-b", "run-b", LeaseUseStatus.Available, 0, TimeSpan.Zero, null),
        };
        var duplicateRun = new[]
        {
            RecurringLeaseUseAccountingEntry.CreateFor(lease, "use-a", "occ-a", "same-run", LeaseUseStatus.Available, 0, TimeSpan.Zero, null),
            RecurringLeaseUseAccountingEntry.CreateFor(lease, "use-b", "occ-b", "same-run", LeaseUseStatus.Available, 0, TimeSpan.Zero, null),
        };
        var duplicateOccurrence = new[]
        {
            RecurringLeaseUseAccountingEntry.CreateFor(lease, "use-a", "same-occ", "run-a", LeaseUseStatus.Available, 0, TimeSpan.Zero, null),
            RecurringLeaseUseAccountingEntry.CreateFor(lease, "use-b", "same-occ", "run-b", LeaseUseStatus.Available, 0, TimeSpan.Zero, null),
        };
        Assert.Equal(RecurringLeaseQuotaReasonCodes.DuplicateUseId, RecurringLeaseQuotaCalculator.EvaluateReservation(lease, duplicateUse, request, T0).ReasonCode);
        Assert.Equal(RecurringLeaseQuotaReasonCodes.DuplicateRunId, RecurringLeaseQuotaCalculator.EvaluateReservation(lease, duplicateRun, request, T0).ReasonCode);
        Assert.Equal(RecurringLeaseQuotaReasonCodes.DuplicateOccurrence, RecurringLeaseQuotaCalculator.EvaluateReservation(lease, duplicateOccurrence, request, T0).ReasonCode);
    }

    [Fact]
    public void InvalidAccountingEvidenceSettledBoundsAndCheckedSummationFailClosed()
    {
        var lease = Activate(CreateLease(maxUses: 2, maxCumulativeDuration: TimeSpan.FromMinutes(4)));
        AssertReason(RecurringLeaseQuotaReasonCodes.AccountingInvalid, () =>
            RecurringLeaseUseAccountingEntry.CreateFor(lease, "use", "occ", "run", LeaseUseStatus.Available, 1, TimeSpan.Zero, null));
        AssertReason(RecurringLeaseQuotaReasonCodes.AccountingInvalid, () =>
            RecurringLeaseUseAccountingEntry.CreateFor(lease, "use", "occ", "run", LeaseUseStatus.Reserved, 1, PerRun, TimeSpan.Zero));
        AssertReason(RecurringLeaseQuotaReasonCodes.SettledDurationInvalid, () =>
            RecurringLeaseUseAccountingEntry.CreateFor(lease, "use", "occ", "run", LeaseUseStatus.Settled, 1, PerRun, TimeSpan.FromTicks(PerRun.Ticks + 1)));
        AssertReason(RecurringLeaseQuotaReasonCodes.ReservedDurationInvalid, () =>
            RecurringLeaseUseAccountingEntry.CreateFor(lease, "use", "occ", "run", LeaseUseStatus.Reserved, 1, TimeSpan.FromTicks(PerRun.Ticks + 1), null));

        var overflowLease = Activate(RecurringConsentLease.CreatePending(
            "overflow-lease",
            Configuration(),
            T0,
            T0.AddHours(1),
            T0.AddMinutes(50),
            TimeSpan.MaxValue,
            1,
            TimeSpan.MaxValue,
            T0));
        var entries = new[]
        {
            RecurringLeaseUseAccountingEntry.CreateFor(overflowLease, "use-a", "occ-a", "run-a", LeaseUseStatus.Settled, 1, TimeSpan.MaxValue, TimeSpan.MaxValue),
            RecurringLeaseUseAccountingEntry.CreateFor(overflowLease, "use-b", "occ-b", "run-b", LeaseUseStatus.Settled, 1, TimeSpan.MaxValue, TimeSpan.MaxValue),
        };
        AssertReason(RecurringLeaseQuotaReasonCodes.AccountingOverflow, () => RecurringLeaseQuotaCalculator.Calculate(overflowLease, entries));
    }

    [Fact]
    public void CalculatorIsDeterministicOrderIndependentAndDoesNotMutateInputs()
    {
        var lease = Activate(CreateLease(maxUses: 4, maxCumulativeDuration: TimeSpan.FromMinutes(8)));
        var entries = new[]
        {
            RecurringLeaseUseAccountingEntry.CreateFor(lease, "use-a", "occ-a", "run-a", LeaseUseStatus.Settled, 1, PerRun, TimeSpan.FromSeconds(30)),
            RecurringLeaseUseAccountingEntry.CreateFor(lease, "use-b", "occ-b", "run-b", LeaseUseStatus.Available, 0, TimeSpan.Zero, null),
        };
        var request = RecurringLeaseOccurrenceRequest.CreateFor(lease, "occ-new", T0, T0.AddMinutes(1), T0.AddMinutes(3), PerRun);
        var beforeStatus = lease.Status;
        var beforeVersion = lease.Version;
        var first = RecurringLeaseQuotaCalculator.EvaluateReservation(lease, entries, request, T0);
        var second = RecurringLeaseQuotaCalculator.EvaluateReservation(lease, entries.Reverse(), request, T0);
        Assert.Equal(first.IsAllowed, second.IsAllowed);
        Assert.Equal(first.ReasonCode, second.ReasonCode);
        Assert.Equal(first.Quota!.AdmissionUsedDuration, second.Quota!.AdmissionUsedDuration);
        Assert.Equal(beforeStatus, lease.Status);
        Assert.Equal(beforeVersion, lease.Version);
        Assert.Equal(RecurringLeaseQuotaReasonCodes.Available, first.ReasonCode);
    }

    [Fact]
    public void CreatedAtIsIndependentFromValidFromAndFutureLeaseCanActivateAtCreationTime()
    {
        var validFrom = T0.AddHours(1);
        var validUntil = T0.AddHours(2);
        var latestEnd = validFrom.AddMinutes(50);
        var lease = RecurringConsentLease.CreatePending(
            "future-lease",
            Configuration("future-plan"),
            validFrom,
            validUntil,
            latestEnd,
            PerRun,
            3,
            TimeSpan.FromMinutes(6),
            T0);

        Assert.Equal(T0, lease.CreatedAtUtc);
        Assert.Equal(T0, lease.UpdatedAtUtc);
        Assert.Equal(0, lease.Version);
        Assert.True(lease.TryTransition(ConsentLeaseStatus.Active, T0).Succeeded);
        Assert.Equal(T0, lease.UpdatedAtUtc);
        Assert.Equal(1, lease.Version);

        AssertReason(RecurringConsentLeaseReasonCodes.CreatedAtInvalid, () =>
            RecurringConsentLease.CreatePending(
                "invalid-created",
                Configuration("invalid-created-plan"),
                validFrom,
                validUntil,
                latestEnd,
                PerRun,
                3,
                TimeSpan.FromMinutes(6),
                validUntil));
        AssertReason(RecurringConsentLeaseReasonCodes.NonUtcTime, () =>
            RecurringConsentLease.CreatePending(
                "non-utc-created",
                Configuration("non-utc-created-plan"),
                validFrom,
                validUntil,
                latestEnd,
                PerRun,
                3,
                TimeSpan.FromMinutes(6),
                T0.ToOffset(TimeSpan.FromHours(1))));
        AssertReason(RecurringConsentLeaseReasonCodes.NonMonotonicTime, () =>
            RecurringConsentLease.Rehydrate(
                lease.LeaseId,
                lease.ConfigurationRef,
                lease.ValidFromUtc,
                lease.ValidUntilUtc,
                lease.AuthorizedPlanLatestEndUtc,
                lease.PerRunDuration,
                lease.MaxUses,
                lease.MaxCumulativeDuration,
                lease.AuthorizationDigest,
                ConsentLeaseStatus.Pending,
                T0.AddMinutes(1),
                T0,
                0));
    }

    [Fact]
    public void LifecycleTransitionTimeBoundariesAreFailClosedAndSameStateIsIdempotent()
    {
        var pending = CreateLease();
        var beforeCreated = pending.TryTransition(ConsentLeaseStatus.Active, T0.AddTicks(-1));
        Assert.False(beforeCreated.Succeeded);
        Assert.Equal(RecurringConsentLeaseReasonCodes.TransitionBeforeCreation, beforeCreated.ReasonCode);

        var afterValidity = pending.TryTransition(ConsentLeaseStatus.Active, pending.ValidUntilUtc);
        Assert.False(afterValidity.Succeeded);
        Assert.Equal(RecurringConsentLeaseReasonCodes.ActiveAfterValidity, afterValidity.ReasonCode);

        var notYetExpired = pending.TryTransition(ConsentLeaseStatus.Expired, pending.ValidUntilUtc.AddTicks(-1));
        Assert.False(notYetExpired.Succeeded);
        Assert.Equal(RecurringConsentLeaseReasonCodes.ExpirationBeforeValidity, notYetExpired.ReasonCode);

        var expired = pending.TryTransition(ConsentLeaseStatus.Expired, pending.ValidUntilUtc);
        Assert.True(expired.Succeeded);
        Assert.Equal(ConsentLeaseStatus.Expired, pending.Status);
        Assert.Equal(pending.ValidUntilUtc, pending.UpdatedAtUtc);
        Assert.Equal(1, pending.Version);

        var noOp = pending.TryTransition(ConsentLeaseStatus.Expired, pending.ValidUntilUtc.AddHours(1));
        Assert.True(noOp.Succeeded);
        Assert.False(noOp.Changed);
        Assert.Equal(pending.ValidUntilUtc, pending.UpdatedAtUtc);
        Assert.Equal(1, pending.Version);

        var nonUtc = pending.TryTransition(ConsentLeaseStatus.Expired, pending.ValidUntilUtc.ToOffset(TimeSpan.FromHours(1)));
        Assert.False(nonUtc.Succeeded);
        Assert.Equal(RecurringConsentLeaseReasonCodes.NonUtcTime, nonUtc.ReasonCode);
        Assert.Equal(1, pending.Version);
    }

    [Fact]
    public void AccountingEvidenceAlwaysCarriesExactLeaseRelation()
    {
        var lease = Activate(CreateLease("bound-lease"));
        var otherLease = Activate(CreateLease("other-lease"));
        var entry = RecurringLeaseUseAccountingEntry.CreateFor(
            lease,
            "bound-use",
            "bound-occurrence",
            "bound-run",
            LeaseUseStatus.Reserved,
            1,
            PerRun,
            null);

        Assert.Equal(lease.LeaseId, entry.LeaseId);
        var otherEntry = RecurringLeaseUseAccountingEntry.Rehydrate(
            otherLease.LeaseId,
            "other-use",
            "other-occurrence",
            "other-run",
            LeaseUseStatus.Reserved,
            1,
            PerRun,
            null);
        AssertReason(RecurringLeaseQuotaReasonCodes.LeaseRelationInvalid, () =>
            RecurringLeaseQuotaCalculator.Calculate(lease, new[] { otherEntry }));
    }

    [Fact]
    public void TerminalQuotaSnapshotBindsLeaseIdentityAndSupportsOnlyQuotaAwareExhaustion()
    {
        var lease = Activate(CreateLease("exhaustion-lease", maxUses: 1, maxCumulativeDuration: PerRun));
        var evidence = RecurringLeaseUseAccountingEntry.CreateFor(
            lease,
            "terminal-use",
            "terminal-occurrence",
            "terminal-run",
            LeaseUseStatus.StartedUnknown,
            1,
            PerRun,
            null);
        var snapshot = RecurringLeaseQuotaCalculator.Calculate(lease, new[] { evidence });

        Assert.Equal(lease.LeaseId, snapshot.LeaseId);
        Assert.Equal(lease.AuthorizationDigest, snapshot.LeaseAuthorizationDigest);
        Assert.Equal(lease.Version, snapshot.LeaseVersion);
        Assert.True(snapshot.IsTerminallyExhausted);
        var marked = lease.TryMarkExhausted(snapshot, T0);
        Assert.True(marked.Succeeded);
        Assert.Equal(ConsentLeaseStatus.Exhausted, lease.Status);
        Assert.Equal(2, lease.Version);

        var currentSnapshot = RecurringLeaseQuotaCalculator.Calculate(lease, new[] { evidence });
        var noOp = lease.TryMarkExhausted(currentSnapshot, T0.AddMinutes(1));
        Assert.True(noOp.Succeeded);
        Assert.False(noOp.Changed);
        Assert.Equal(2, lease.Version);
        Assert.Equal(lease.AuthorizationDigest, currentSnapshot.LeaseAuthorizationDigest);

        var otherLease = Activate(CreateLease("different-exhaustion-lease", maxUses: 1, maxCumulativeDuration: PerRun));
        var otherEvidence = RecurringLeaseUseAccountingEntry.CreateFor(
            otherLease,
            "other-terminal-use",
            "other-terminal-occurrence",
            "other-terminal-run",
            LeaseUseStatus.StartedUnknown,
            1,
            PerRun,
            null);
        var otherSnapshot = RecurringLeaseQuotaCalculator.Calculate(otherLease, new[] { otherEvidence });
        var mismatch = Activate(CreateLease("mismatch-lease", maxUses: 1, maxCumulativeDuration: PerRun));
        Assert.Equal(RecurringConsentLeaseReasonCodes.ExhaustionSnapshotLeaseMismatch, mismatch.TryMarkExhausted(otherSnapshot, T0).ReasonCode);
        Assert.Equal(RecurringConsentLeaseReasonCodes.ExhaustionSnapshotVersionMismatch,
            RecurringConsentLease.Rehydrate(
                lease.LeaseId,
                lease.ConfigurationRef,
                lease.ValidFromUtc,
                lease.ValidUntilUtc,
                lease.AuthorizedPlanLatestEndUtc,
                lease.PerRunDuration,
                lease.MaxUses,
                lease.MaxCumulativeDuration,
                lease.AuthorizationDigest,
                ConsentLeaseStatus.Active,
                T0,
                T0,
                lease.Version + 1)
                .TryMarkExhausted(snapshot, T0)
                .ReasonCode);

        var digestLease = Activate(CreateLease("digest-mismatch-lease", maxUses: 1, maxCumulativeDuration: PerRun));
        var digestSnapshot = RecurringLeaseQuotaCalculator.Calculate(digestLease, new[]
        {
            RecurringLeaseUseAccountingEntry.CreateFor(
                digestLease,
                "digest-use",
                "digest-occurrence",
                "digest-run",
                LeaseUseStatus.StartedUnknown,
                1,
                PerRun,
                null),
        });
        var digestTarget = Activate(RecurringConsentLease.CreatePending(
            digestLease.LeaseId,
            Configuration("digest-target-plan"),
            T0,
            T0.AddHours(1),
            T0.AddMinutes(50),
            PerRun,
            1,
            PerRun,
            T0));
        Assert.Equal(RecurringConsentLeaseReasonCodes.ExhaustionSnapshotDigestMismatch,
            digestTarget.TryMarkExhausted(digestSnapshot, T0).ReasonCode);
    }

    [Fact]
    public void NonTerminalSnapshotCannotMarkExhaustedAndOrdinaryTransitionStillRequiresEvidence()
    {
        var lease = Activate(CreateLease("non-terminal-lease", maxUses: 2, maxCumulativeDuration: TimeSpan.FromMinutes(4)));
        var inFlight = RecurringLeaseUseAccountingEntry.CreateFor(
            lease,
            "inflight-use",
            "inflight-occurrence",
            "inflight-run",
            LeaseUseStatus.Reserved,
            1,
            PerRun,
            null);
        var snapshot = RecurringLeaseQuotaCalculator.Calculate(lease, new[] { inFlight });
        Assert.True(snapshot.IsTemporarilyUnavailable);
        Assert.False(snapshot.IsTerminallyExhausted);
        Assert.Equal(RecurringConsentLeaseReasonCodes.ExhaustionSnapshotNotTerminal, lease.TryMarkExhausted(snapshot, T0).ReasonCode);
        Assert.Equal(RecurringConsentLeaseReasonCodes.ExhaustionRequiresQuotaDecision,
            lease.TryTransition(ConsentLeaseStatus.Exhausted, T0).ReasonCode);
        Assert.Equal(ConsentLeaseStatus.Active, lease.Status);
    }

    [Fact]
    public void OccurrencePlannedEndMustBeExactIncludingTickMillisecondAndOverflowCases()
    {
        var configuration = Configuration("planned-end-plan");
        var valid = new RecurringLeaseOccurrenceRequest(
            "planned-end-valid",
            configuration,
            T0,
            T0.AddMinutes(1),
            T0.AddMinutes(3),
            PerRun);
        Assert.Equal(T0.AddMinutes(3), valid.PlannedEndUtc);
        AssertReason(RecurringLeaseQuotaReasonCodes.OccurrencePlannedEndMismatch, () =>
            new RecurringLeaseOccurrenceRequest("planned-end-tick", configuration, T0, T0.AddMinutes(1), T0.AddMinutes(3).AddTicks(1), PerRun));
        AssertReason(RecurringLeaseQuotaReasonCodes.OccurrencePlannedEndMismatch, () =>
            new RecurringLeaseOccurrenceRequest("planned-end-ms", configuration, T0, T0.AddMinutes(1), T0.AddMinutes(3).AddTicks(TimeSpan.TicksPerMillisecond), PerRun));
        AssertReason(RecurringLeaseQuotaReasonCodes.OccurrencePlannedEndOverflow, () =>
            new RecurringLeaseOccurrenceRequest(
                "planned-end-overflow",
                configuration,
                DateTimeOffset.MaxValue.Subtract(TimeSpan.FromTicks(1)),
                DateTimeOffset.MaxValue,
                DateTimeOffset.MaxValue,
                TimeSpan.FromTicks(1)));
    }

    [Fact]
    public void LifecycleAndValidityReasonsPrecedeExistingOccurrenceClaim()
    {
        var revoked = CreateLease("precedence-revoked");
        Assert.True(revoked.TryTransition(ConsentLeaseStatus.Revoked, T0).Succeeded);
        var revokedEntry = RecurringLeaseUseAccountingEntry.CreateFor(revoked, "revoked-use", "same-occurrence", "revoked-run", LeaseUseStatus.Available, 0, TimeSpan.Zero, null);
        var revokedRequest = RecurringLeaseOccurrenceRequest.CreateFor(revoked, "same-occurrence", T0, T0.AddMinutes(1), T0.AddMinutes(3), PerRun);
        Assert.Equal(RecurringLeaseQuotaReasonCodes.Revoked, EvaluateWithEntries(revoked, new[] { revokedEntry }, revokedRequest, T0).ReasonCode);

        var expired = CreateLease("precedence-expired");
        Assert.True(expired.TryTransition(ConsentLeaseStatus.Expired, expired.ValidUntilUtc).Succeeded);
        var expiredEntry = RecurringLeaseUseAccountingEntry.CreateFor(expired, "expired-use", "same-expired", "expired-run", LeaseUseStatus.Available, 0, TimeSpan.Zero, null);
        var expiredRequest = RecurringLeaseOccurrenceRequest.CreateFor(expired, "same-expired", T0, T0.AddMinutes(1), T0.AddMinutes(3), PerRun);
        Assert.Equal(RecurringLeaseQuotaReasonCodes.Expired, EvaluateWithEntries(expired, new[] { expiredEntry }, expiredRequest, T0).ReasonCode);

        var exhausted = RecurringConsentLease.Rehydrate(
            "precedence-exhausted",
            Configuration("precedence-exhausted"),
            T0,
            T0.AddHours(1),
            T0.AddMinutes(50),
            PerRun,
            1,
            PerRun,
            RecurringConsentLease.CreatePending("precedence-exhausted", Configuration("precedence-exhausted"), T0, T0.AddHours(1), T0.AddMinutes(50), PerRun, 1, PerRun, T0).AuthorizationDigest,
            ConsentLeaseStatus.Exhausted,
            T0,
            T0,
            2);
        var exhaustedEntry = RecurringLeaseUseAccountingEntry.CreateFor(exhausted, "exhausted-use", "same-exhausted", "exhausted-run", LeaseUseStatus.StartedUnknown, 1, PerRun, null);
        var exhaustedRequest = RecurringLeaseOccurrenceRequest.CreateFor(exhausted, "same-exhausted", T0, T0.AddMinutes(1), T0.AddMinutes(3), PerRun);
        Assert.Equal(RecurringLeaseQuotaReasonCodes.Exhausted, EvaluateWithEntries(exhausted, new[] { exhaustedEntry }, exhaustedRequest, T0).ReasonCode);

        var active = Activate(CreateLease("precedence-active"));
        var activeEntry = RecurringLeaseUseAccountingEntry.CreateFor(active, "active-use", "same-active", "active-run", LeaseUseStatus.Available, 0, TimeSpan.Zero, null);
        var activeRequest = RecurringLeaseOccurrenceRequest.CreateFor(active, "same-active", T0, T0.AddMinutes(1), T0.AddMinutes(3), PerRun);
        Assert.Equal(RecurringLeaseQuotaReasonCodes.OccurrenceAlreadyClaimed, EvaluateWithEntries(active, new[] { activeEntry }, activeRequest, T0).ReasonCode);
    }

    [Fact]
    public void RequestValidationPrecedesExistingOccurrenceClaimForConfigurationDurationAndWindow()
    {
        var lease = Activate(CreateLease("request-precedence-lease"));
        const string occurrenceIdentity = "competing-occurrence";
        var existing = RecurringLeaseUseAccountingEntry.CreateFor(
            lease,
            "competing-use",
            occurrenceIdentity,
            "competing-run",
            LeaseUseStatus.Available,
            0,
            TimeSpan.Zero,
            null);

        var wrongConfiguration = new RecurringLeaseOccurrenceRequest(
            occurrenceIdentity,
            Configuration("different-configuration"),
            T0,
            T0.AddMinutes(1),
            T0.AddMinutes(3),
            PerRun);
        Assert.Equal(
            RecurringLeaseQuotaReasonCodes.ConfigurationMismatch,
            EvaluateWithEntries(lease, new[] { existing }, wrongConfiguration, T0).ReasonCode);

        var wrongDuration = new RecurringLeaseOccurrenceRequest(
            occurrenceIdentity,
            lease.ConfigurationRef,
            T0,
            T0.AddMinutes(1),
            T0.AddMinutes(3).AddTicks(1),
            TimeSpan.FromTicks(PerRun.Ticks + 1));
        Assert.Equal(
            RecurringLeaseQuotaReasonCodes.DurationMismatch,
            EvaluateWithEntries(lease, new[] { existing }, wrongDuration, T0).ReasonCode);

        var beforeValidity = new RecurringLeaseOccurrenceRequest(
            occurrenceIdentity,
            lease.ConfigurationRef,
            lease.ValidFromUtc.AddTicks(-1),
            lease.ValidFromUtc,
            lease.ValidFromUtc.Add(PerRun),
            PerRun);
        Assert.Equal(
            RecurringLeaseQuotaReasonCodes.OccurrenceBeforeValidity,
            EvaluateWithEntries(lease, new[] { existing }, beforeValidity, T0).ReasonCode);

        var afterValidityLatest = lease.ValidUntilUtc.Subtract(PerRun);
        var afterValidity = new RecurringLeaseOccurrenceRequest(
            occurrenceIdentity,
            lease.ConfigurationRef,
            T0,
            afterValidityLatest,
            lease.ValidUntilUtc,
            PerRun);
        Assert.Equal(
            RecurringLeaseQuotaReasonCodes.OccurrenceAfterValidity,
            EvaluateWithEntries(lease, new[] { existing }, afterValidity, T0).ReasonCode);

        var beyondAuthorizedLatestStart = lease.AuthorizedPlanLatestEndUtc.Subtract(PerRun).AddTicks(1);
        var beyondAuthorizedLatest = new RecurringLeaseOccurrenceRequest(
            occurrenceIdentity,
            lease.ConfigurationRef,
            T0,
            beyondAuthorizedLatestStart,
            lease.AuthorizedPlanLatestEndUtc.AddTicks(1),
            PerRun);
        Assert.Equal(
            RecurringLeaseQuotaReasonCodes.OccurrenceAfterAuthorizedLatestEnd,
            EvaluateWithEntries(lease, new[] { existing }, beyondAuthorizedLatest, T0).ReasonCode);

        var matching = RecurringLeaseOccurrenceRequest.CreateFor(
            lease,
            occurrenceIdentity,
            T0,
            T0.AddMinutes(1),
            T0.AddMinutes(3),
            PerRun);
        Assert.Equal(
            RecurringLeaseQuotaReasonCodes.OccurrenceAlreadyClaimed,
            EvaluateWithEntries(lease, new[] { existing }, matching, T0).ReasonCode);
    }

    [Fact]
    public void InvalidAccountingEvidenceStillPrecedesLifecycleAndRequestReasons()
    {
        var revoked = CreateLease("invalid-accounting-precedence");
        Assert.True(revoked.TryTransition(ConsentLeaseStatus.Revoked, T0).Succeeded);
        var request = RecurringLeaseOccurrenceRequest.CreateFor(
            revoked,
            "invalid-accounting-occurrence",
            T0,
            T0.AddMinutes(1),
            T0.AddMinutes(3),
            PerRun);

        var invalidEntries = new RecurringLeaseUseAccountingEntry?[] { null };
        var decision = EvaluateWithEntries(revoked, invalidEntries!, request, T0);
        Assert.Equal(RecurringLeaseQuotaReasonCodes.AccountingInvalid, decision.ReasonCode);
        Assert.False(decision.IsAllowed);
    }

    private static RecurringLeaseQuotaDecision Evaluate(RecurringConsentLease lease, RecurringLeaseOccurrenceRequest request) =>
        RecurringLeaseQuotaCalculator.EvaluateReservation(lease, Array.Empty<RecurringLeaseUseAccountingEntry>(), request, T0);

    private static RecurringLeaseQuotaDecision EvaluateWithEntries(
        RecurringConsentLease lease,
        IEnumerable<RecurringLeaseUseAccountingEntry> entries,
        RecurringLeaseOccurrenceRequest request,
        DateTimeOffset nowUtc) =>
        RecurringLeaseQuotaCalculator.EvaluateReservation(lease, entries, request, nowUtc);

    private static RecurringConsentLease CreateLease(string leaseId = "lease", long maxUses = 3, TimeSpan? maxCumulativeDuration = null)
    {
        var perRun = PerRun;
        return RecurringConsentLease.CreatePending(
            leaseId,
            Configuration(),
            T0,
            T0.AddHours(1),
            T0.AddMinutes(50),
            perRun,
            maxUses,
            maxCumulativeDuration ?? TimeSpan.FromTicks(checked(perRun.Ticks * maxUses)),
            T0);
    }

    private static RecurringConsentLease Activate(RecurringConsentLease lease)
    {
        Assert.True(lease.TryTransition(ConsentLeaseStatus.Active, T0).Succeeded);
        return lease;
    }

    private static RecurringPlanConfigurationRef Configuration(string planId = "plan", long revision = 1, string scheduleSuffix = "a") =>
        new(
            planId,
            revision,
            RecurringPlanScheduleDigest(scheduleSuffix),
            RecurringTimeZoneRulesDigest.Prefix + "b".PadRight(64, 'b'),
            new ProfileRef("profile", 1, RecurringFixedRegionProfileVersion.DigestPrefix + "c".PadRight(64, 'c')));

    private static string RecurringPlanScheduleDigest(string suffix) =>
        "recurring-schedule/v1:" + suffix.PadRight(64, suffix[0]);

    private static void AssertReason(string expected, Action action)
    {
        var exception = Assert.Throws<Phase3DomainException>(action);
        Assert.Equal(expected, exception.ReasonCode);
    }
}
