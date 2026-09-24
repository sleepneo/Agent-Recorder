using System.Data;
using System.Collections.Concurrent;
using System.Reflection;
using System.Text.Json;
using AgentRecorder.Capture;
using AgentRecorder.Core;
using AgentRecorder.Core.Automation;
using AgentRecorder.Infrastructure;
using AgentRecorder.Logging;
using AgentRecorder.Persistence;
using AgentRecorder.Windows;
using Microsoft.Data.Sqlite;
using Xunit;

namespace AgentRecorder.Tests;

[Collection("NonParallel-AgentRecorderDataDir")]
public sealed class RecurringLeaseExecutionGateTests
{
    [Fact]
    public void ValidRecurringProofIsConsumedAndPublishedOnlyAfterCommit()
    {
        using var context = CreateCommittedContext();
        var proof = context.Proof;
        var provider = new CountingProvider();
        var loader = new SqliteRecurringLeaseExecutionSnapshotLoader(
            context.Context.Fixture.Store,
            snapshotLoadedBeforeEnvironmentForTest: null,
            beforeCommitForTest: null,
            environmentProviderForTest: provider,
            trustedUtcClockForTest: () => context.Now);

        var succeeded = loader.TryAuthorizeAndConsumeRecurringLeaseUse(
            proof,
            out var authorization,
            out var failureReason);

        Assert.True(succeeded, failureReason);
        Assert.NotNull(authorization);
        Assert.Equal("", failureReason);
        Assert.True(proof.IsConsumed);
        Assert.True(authorization!.IsProofConsumed);
        Assert.True(authorization.HasValidConsumedProofBinding);
        Assert.Equal(context.Result.FirstCommitReceipt!.Specification.SpecificationDigest,
            authorization.Specification.SpecificationDigest);
        Assert.Equal(context.Result.FirstCommitReceipt.Specification.Duration, authorization.Duration);
        Assert.Equal(context.Result.FirstCommitReceipt.Specification.FrozenOutputFilePath, authorization.OutputFilePath);
        Assert.Equal(AuthorizedCaptureBackend.FfmpegRegion, authorization.Backend);
        Assert.Equal(AuthorizedAudioMode.None, authorization.AudioMode);
        Assert.Equal(1, provider.CaptureCount);
        Assert.Empty(typeof(RecurringLeaseCaptureAuthorization)
            .GetConstructors(BindingFlags.Public | BindingFlags.Instance));
        var serialized = JsonSerializer.Serialize(authorization);
        Assert.DoesNotContain(proof.OneTimeNonce, serialized, StringComparison.Ordinal);
        Assert.DoesNotContain(proof.ProofId, serialized, StringComparison.Ordinal);
        Assert.DoesNotContain("nonce", serialized, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ReservationAndStartCommitTimesRemainDistinctAndProofBindsToCommitTime()
    {
        using var context = CreateCommittedContext(startCommitOffset: TimeSpan.FromSeconds(5));
        var reservationTime = context.Context.Fixture.CreatedAt;
        var commitTime = context.Now;
        var receipt = Assert.IsType<RecurringStartCommitReceipt>(context.Result.FirstCommitReceipt);

        Assert.Equal(reservationTime, receipt.Run.CreatedAtUtc);
        Assert.Equal(reservationTime, receipt.Use.CreatedAtUtc);
        Assert.Equal(reservationTime, receipt.Occurrence.UpdatedAtUtc);
        Assert.Equal(commitTime, receipt.CommittedAtUtc);
        Assert.Equal(commitTime, receipt.Run.UpdatedAtUtc);
        Assert.Equal(commitTime, receipt.Use.UpdatedAtUtc);
        Assert.NotEqual(reservationTime, receipt.CommittedAtUtc);
        Assert.Equal(commitTime, context.Proof.IssuedAtUtc);
        Assert.NotEqual(reservationTime, context.Proof.IssuedAtUtc);

        var provider = new CountingProvider();
        var succeeded = CreateLoader(context, provider).TryAuthorizeAndConsumeRecurringLeaseUse(
            context.Proof, out var authorization, out var failureReason);

        Assert.True(succeeded, failureReason);
        Assert.NotNull(authorization);
        Assert.True(context.Proof.IsConsumed);
    }

    [Fact]
    public void ReusingConsumedRecurringProofCannotRepublishAuthorization()
    {
        using var context = CreateCommittedContext();
        var provider = new CountingProvider();
        var first = CreateLoader(context, provider).TryAuthorizeAndConsumeRecurringLeaseUse(
            context.Proof, out var firstAuthorization, out var firstReason);
        var second = CreateLoader(context, provider).TryAuthorizeAndConsumeRecurringLeaseUse(
            context.Proof, out var secondAuthorization, out var secondReason);

        Assert.True(first, firstReason);
        Assert.NotNull(firstAuthorization);
        Assert.False(second);
        Assert.Null(secondAuthorization);
        Assert.Equal("proof_already_consumed", secondReason);
        Assert.Equal(1, provider.CaptureCount);
    }

    [Fact]
    public void WrongProofKindsAreRejectedBeforeRecurringTransactionAndRemainAvailable()
    {
        using var context = CreateCommittedContext();
        var now = context.Now;
        var interactive = new InteractiveConfirmationProof(
            "interactive-test-proof",
            "recording",
            "run",
            "confirmation",
            "plan",
            "scope",
            now,
            now.AddMinutes(1),
            CaptureAuthorizationSessionBinding.Current,
            null,
            null);
        var standing = new StandingLeaseUseProof(
            "standing-test-proof",
            "run",
            "standing-source",
            "standing-plan",
            "standing-scope",
            now,
            now.AddMinutes(1),
            "S-1-5-21-test",
            "standing-session",
            "standing-lease",
            "standing-use",
            "0123456789abcdef0123456789abcdef",
            TimeSpan.FromSeconds(1));
        var loader = CreateLoader(context, new CountingProvider());

        Assert.False(loader.TryAuthorizeAndConsumeRecurringLeaseUse(null, out var nullAuthorization, out var nullReason));
        Assert.Null(nullAuthorization);
        Assert.Equal("proof_missing", nullReason);
        Assert.False(loader.TryAuthorizeAndConsumeRecurringLeaseUse(interactive, out var interactiveAuthorization, out var interactiveReason));
        Assert.Null(interactiveAuthorization);
        Assert.Equal("proof_kind_not_allowed", interactiveReason);
        Assert.True(interactive.CheckAvailableAt(now, out _));
        Assert.False(loader.TryAuthorizeAndConsumeRecurringLeaseUse(standing, out var standingAuthorization, out var standingReason));
        Assert.Null(standingAuthorization);
        Assert.Equal("proof_kind_not_allowed", standingReason);
        Assert.True(standing.CheckAvailableAt(now, out _));
        Assert.True(context.Proof.CheckAvailableAt(now, out var recurringReason), recurringReason);
    }

    [Fact]
    public void EnvironmentTimeMismatchFailsClosedWithoutConsumingProof()
    {
        using var context = CreateCommittedContext();
        var provider = new CountingProvider(timeOffsetTicks: 1);
        var succeeded = CreateLoader(context, provider).TryAuthorizeAndConsumeRecurringLeaseUse(
            context.Proof, out var authorization, out var failureReason);

        Assert.False(succeeded);
        Assert.Null(authorization);
        Assert.Equal("execution_environment_time_mismatch", failureReason);
        Assert.True(context.Proof.CheckAvailableAt(context.Now, out var reason), reason);
        Assert.Equal(1, provider.CaptureCount);
    }

    [Fact]
    public void TrustedClockRollbackIsRejectedBeforeASecondDatabaseAttempt()
    {
        using var context = CreateCommittedContext();
        var samples = new Queue<DateTimeOffset>(new[]
        {
            context.Now,
            context.Now.AddTicks(-1),
        });
        var provider = new CountingProvider(timeOffsetTicks: 1);
        var loader = new SqliteRecurringLeaseExecutionSnapshotLoader(
            context.Context.Fixture.Store,
            snapshotLoadedBeforeEnvironmentForTest: null,
            beforeCommitForTest: null,
            environmentProviderForTest: provider,
            trustedUtcClockForTest: () => samples.Dequeue());

        Assert.False(loader.TryAuthorizeAndConsumeRecurringLeaseUse(
            context.Proof, out var firstAuthorization, out var firstReason));
        Assert.Null(firstAuthorization);
        Assert.Equal("execution_environment_time_mismatch", firstReason);
        Assert.True(context.Proof.CheckAvailableAt(context.Now, out var availableReason), availableReason);

        Assert.False(loader.TryAuthorizeAndConsumeRecurringLeaseUse(
            context.Proof, out var secondAuthorization, out var secondReason));
        Assert.Null(secondAuthorization);
        Assert.Equal("clock_moved_backwards", secondReason);
        Assert.Equal(1, provider.CaptureCount);
    }

    [Fact]
    public void ImmediateTransactionBlocksACompetingRecurringWriterUntilTheGateReleases()
    {
        using var context = CreateCommittedContext();
        var barrierEntered = new ManualResetEventSlim();
        var release = new ManualResetEventSlim();
        var writerStarted = new ManualResetEventSlim();
        var writerWasBlocked = false;
        Task<RecurringOccurrenceStartCommitResult>? writerTask = null;

        var loader = new SqliteRecurringLeaseExecutionSnapshotLoader(
            context.Context.Fixture.Store,
            snapshotLoadedBeforeEnvironmentForTest: () =>
            {
                writerTask = Task.Run(() =>
                {
                    writerStarted.Set();
                    var competingStore = new SqliteOperationalStore(context.Context.Fixture.Store.DatabasePath);
                    return new RecurringOccurrenceStartCommitService(
                        competingStore,
                        () => context.Now,
                        new RecurringOccurrenceReservationTests.MatchingReservationEnvironmentProvider())
                        .Commit(context.Context.Lease.LeaseId, context.Context.Slot.OccurrenceIdentity);
                });

                Assert.True(writerStarted.Wait(TimeSpan.FromSeconds(5)));
                writerWasBlocked = !writerTask.Wait(TimeSpan.FromMilliseconds(250));
                barrierEntered.Set();
                release.Wait(TimeSpan.FromSeconds(5));
            },
            beforeCommitForTest: null,
            environmentProviderForTest: new CountingProvider(),
            trustedUtcClockForTest: () => context.Now);

        var loadTask = Task.Run(() => loader.TryAuthorizeAndConsumeRecurringLeaseUse(
            context.Proof, out var authorization, out var reason)
            ? (Success: true, Authorization: authorization, Reason: reason)
            : (Success: false, Authorization: authorization, Reason: reason));

        Assert.True(barrierEntered.Wait(TimeSpan.FromSeconds(5)));
        Assert.True(writerWasBlocked);
        release.Set();

        var result = loadTask.GetAwaiter().GetResult();
        Assert.True(result.Success, result.Reason);
        Assert.NotNull(result.Authorization);
        Assert.NotNull(writerTask);
        var writerResult = writerTask!.GetAwaiter().GetResult();
        Assert.Equal(RecurringOccurrenceStartCommitStatus.AlreadyCommitted, writerResult.Status);
    }

    [Fact]
    public void ClockIsSampledAfterImmediateLockAndExpiredTimeFailsBeforeEnvironment()
    {
        using var context = CreateCommittedContext(startCommitOffset: TimeSpan.FromSeconds(5));
        using var competingConnection = context.Context.Fixture.Store.OpenConnection();
        using var competingTransaction = competingConnection.BeginTransaction(
            IsolationLevel.Serializable, deferred: false);
        var loaderReachedBegin = new ManualResetEventSlim();
        var clockSampled = new ManualResetEventSlim();
        var provider = new CountingProvider();
        var expiredAt = context.Result.FirstCommitReceipt!.Specification.PlannedEndUtc.AddTicks(1);
        var loader = new SqliteRecurringLeaseExecutionSnapshotLoader(
            context.Context.Fixture.Store,
            snapshotLoadedBeforeEnvironmentForTest: null,
            beforeCommitForTest: null,
            environmentProviderForTest: provider,
            trustedUtcClockForTest: () =>
            {
                clockSampled.Set();
                return expiredAt;
            },
            beforeBeginWriteTransactionForTest: loaderReachedBegin.Set);

        var task = Task.Run(() => loader.TryAuthorizeAndConsumeRecurringLeaseUse(
            context.Proof, out var authorization, out var reason)
            ? (Success: true, Authorization: authorization, Reason: reason)
            : (Success: false, Authorization: authorization, Reason: reason));

        Assert.True(loaderReachedBegin.Wait(TimeSpan.FromSeconds(5)));
        Assert.False(clockSampled.IsSet);
        competingTransaction.Commit();

        var result = task.GetAwaiter().GetResult();
        Assert.False(result.Success);
        Assert.Null(result.Authorization);
        Assert.Equal("proof_expired", result.Reason);
        Assert.True(clockSampled.IsSet);
        Assert.Equal(0, provider.CaptureCount);
        Assert.True(context.Proof.CheckAvailableAt(context.Now, out var reason), reason);
    }

    [Fact]
    public void ReenableAfterProofBlocksAnActiveLeaseUsingTheOldApproval()
    {
        using var context = CreateCommittedContext(maxUses: 10, startCommitOffset: TimeSpan.FromSeconds(5));
        DisableAndReenableAfterProof(context, "task267r-active");

        var provider = new CountingProvider();
        var result = CreateLoader(context, provider).TryAuthorizeAndConsumeRecurringLeaseUse(
            context.Proof, out var authorization, out var failureReason);

        Assert.False(result);
        Assert.Null(authorization);
        Assert.Equal("unattended_reenable_requires_new_authorization", failureReason);
        Assert.Equal(0, provider.CaptureCount);
        Assert.True(context.Proof.CheckAvailableAt(context.Now, out var reason), reason);
    }

    [Fact]
    public void ReenableAfterProofBlocksAnExhaustedLeaseUsingTheOldApproval()
    {
        using var context = CreateCommittedContext(maxUses: 1, startCommitOffset: TimeSpan.FromSeconds(5));
        Assert.Equal(ConsentLeaseStatus.Exhausted, context.Result.FirstCommitReceipt!.Lease.Status);
        DisableAndReenableAfterProof(context, "task267r-exhausted");

        var provider = new CountingProvider();
        var result = CreateLoader(context, provider).TryAuthorizeAndConsumeRecurringLeaseUse(
            context.Proof, out var authorization, out var failureReason);

        Assert.False(result);
        Assert.Null(authorization);
        Assert.Equal("unattended_reenable_requires_new_authorization", failureReason);
        Assert.Equal(0, provider.CaptureCount);
        Assert.True(context.Proof.CheckAvailableAt(context.Now, out var reason), reason);
    }

    [Fact]
    public void ProofTimeMustMatchTheCommittedRunAndExactScopeExpiry()
    {
        using var context = CreateCommittedContext(startCommitOffset: TimeSpan.FromSeconds(5));
        var proof = new RecurringLeaseUseProof(
            "recurring_task267_time_tamper",
            context.Proof.RunId,
            context.Proof.LeaseId,
            context.Proof.LeaseUseId,
            context.Proof.OccurrenceIdentity,
            context.Proof.SpecificationDigest,
            context.Proof.AuthorizationSourceId,
            context.Proof.CapturePlanDigest,
            context.Proof.ScopeDigest,
            context.Context.Fixture.CreatedAt,
            context.Proof.ExpiresAtUtc,
            context.Proof.CurrentUserSid,
            context.Proof.SessionBinding,
            "0123456789abcdef0123456789abcdef",
            context.Proof.MaxDuration);

        var succeeded = CreateLoader(context, new CountingProvider()).TryAuthorizeAndConsumeRecurringLeaseUse(
            proof, out var authorization, out var failureReason);

        Assert.False(succeeded);
        Assert.Null(authorization);
        Assert.Equal("proof_time_binding_mismatch", failureReason);
        Assert.True(proof.CheckAvailableAt(context.Now, out var reason), reason);
    }

    [Fact]
    public void CommitFailureLeavesProofConsumedAndCannotBeRetried()
    {
        using var context = CreateCommittedContext();
        var provider = new CountingProvider();
        var failingLoader = new SqliteRecurringLeaseExecutionSnapshotLoader(
            context.Context.Fixture.Store,
            snapshotLoadedBeforeEnvironmentForTest: null,
            beforeCommitForTest: _ => throw new InvalidOperationException("test commit failure"),
            environmentProviderForTest: provider,
            trustedUtcClockForTest: () => context.Now);

        var failed = failingLoader.TryAuthorizeAndConsumeRecurringLeaseUse(
            context.Proof, out var failedAuthorization, out var failedReason);

        Assert.False(failed);
        Assert.Null(failedAuthorization);
        Assert.Equal("execution_transaction_commit_failed", failedReason);
        Assert.True(context.Proof.IsConsumed);

        var retry = CreateLoader(context, provider).TryAuthorizeAndConsumeRecurringLeaseUse(
            context.Proof, out var retryAuthorization, out var retryReason);
        Assert.False(retry);
        Assert.Null(retryAuthorization);
        Assert.Equal("proof_already_consumed", retryReason);
    }

    [Fact]
    public void ConcurrentLoadersProduceAtMostOneAuthorizationForOneProof()
    {
        using var context = CreateCommittedContext();
        var outcomes = new ConcurrentBag<(bool Success, RecurringLeaseCaptureAuthorization? Authorization, string Reason)>();

        Parallel.For(0, 64, _ =>
        {
            var loader = CreateLoader(context, new CountingProvider());
            var success = loader.TryAuthorizeAndConsumeRecurringLeaseUse(
                context.Proof, out var authorization, out var reason);
            outcomes.Add((success, authorization, reason));
        });

        Assert.Equal(1, outcomes.Count(item => item.Success));
        Assert.Single(outcomes.Where(item => item.Success && item.Authorization is not null));
        Assert.Equal(63, outcomes.Count(item => !item.Success));
        Assert.True(context.Proof.IsConsumed);
    }

    [Fact]
    public void RecurringProofStillCannotCrossInteractiveOrStandingGate()
    {
        using var context = CreateCommittedContext();
        var currentPlan = new CapturePlan(
            "wgc-continuous",
            "wgc-continuous",
            new CaptureBackendSelectionEvidence("wgc-continuous", "wgc-continuous", "test", "cached", null, false),
            "display_surface",
            "display",
            null,
            nint.Zero,
            null);
        var recording = new Recording("task267-cross-gate-recording");

        Assert.False(CaptureAuthorizationGate.TryConsumeInteractive(
            context.Proof,
            recording,
            currentPlan,
            confirmation: null,
            allowSyntheticTestAuthorization: true,
            nowUtc: context.Now,
            out var interactiveReason));
        Assert.Equal("proof_kind_not_allowed", interactiveReason);
        Assert.False(StandingLeaseExecutionGate.TryAuthorizeAndConsumeStandingLeaseUseInTransaction(
            context.Proof,
            snapshot: null,
            environment: null,
            out var standingReason));
        Assert.Equal("proof_kind_not_allowed", standingReason);
        Assert.True(context.Proof.CheckAvailableAt(context.Now, out var reason), reason);
    }

    [Fact]
    public void RecurringLeaseCaptureExecutionTicket_ProjectsOnlyExactAuthorizationAndClaimsOnce()
    {
        using var context = CreateCommittedContext(startCommitOffset: TimeSpan.FromSeconds(5));
        var provider = new CountingProvider();
        Assert.True(CreateLoader(context, provider).TryAuthorizeAndConsumeRecurringLeaseUse(
            context.Proof, out var authorization, out var authorizationReason), authorizationReason);
        Assert.NotNull(authorization);

        var succeeded = RecurringLeaseCaptureExecutionTicket.TryCreate(
            authorization,
            out var ticket,
            out var ticketReason);

        Assert.True(succeeded, ticketReason);
        Assert.NotNull(ticket);
        var receipt = Assert.IsType<RecurringStartCommitReceipt>(context.Result.FirstCommitReceipt);
        var profile = context.Context.Fixture.Setup.ExactProfile;
        Assert.Equal(receipt.Plan.Id, ticket!.PlanId);
        Assert.Equal(receipt.Plan.IsOneTime, ticket.PlanIsOneTime);
        Assert.Equal(receipt.Plan.IsPeriodic, ticket.PlanIsPeriodic);
        Assert.Equal(receipt.Plan.Status, ticket.PlanStatus);
        Assert.Equal(receipt.Plan.CreatedAtUtc, ticket.PlanCreatedAtUtc);
        Assert.Equal(receipt.Plan.UpdatedAtUtc, ticket.PlanUpdatedAtUtc);
        Assert.Equal(receipt.Plan.Version, ticket.PlanVersion);
        Assert.Equal(receipt.Occurrence.Id, ticket.OccurrenceId);
        Assert.Equal(receipt.Occurrence.PlanId, ticket.OccurrencePlanId);
        Assert.Equal(receipt.Occurrence.Status, ticket.OccurrenceStatus);
        Assert.Equal(receipt.Occurrence.RunId, ticket.OccurrenceRunId);
        Assert.Equal(receipt.Occurrence.TerminalReasonCode, ticket.OccurrenceTerminalReasonCode);
        Assert.Equal(receipt.Occurrence.WindowStartUtc, ticket.OccurrenceWindowStartUtc);
        Assert.Equal(receipt.Occurrence.WindowEndUtc, ticket.OccurrenceWindowEndUtc);
        Assert.Equal(receipt.Occurrence.CreatedAtUtc, ticket.OccurrenceCreatedAtUtc);
        Assert.Equal(receipt.Occurrence.UpdatedAtUtc, ticket.OccurrenceUpdatedAtUtc);
        Assert.Equal(receipt.Occurrence.Version, ticket.OccurrenceVersion);
        Assert.Equal(receipt.Lease.LeaseId, ticket.LeaseId);
        Assert.Equal(receipt.Lease.PlanId, ticket.LeasePlanId);
        Assert.Equal(receipt.Specification.OccurrenceId, ticket.LeaseOccurrenceId);
        Assert.Equal(receipt.Lease.Status, ticket.LeaseStatus);
        Assert.Equal(receipt.Lease.ValidFromUtc, ticket.LeaseValidFromUtc);
        Assert.Equal(receipt.Lease.ValidUntilUtc, ticket.LeaseValidUntilUtc);
        Assert.Equal(receipt.Lease.AuthorizedPlanLatestEndUtc, ticket.LeaseAuthorizedPlanLatestEndUtc);
        Assert.Equal(receipt.Lease.PerRunDuration, ticket.LeasePerRunDuration);
        Assert.Equal(receipt.Lease.MaxUses, ticket.LeaseMaxUses);
        Assert.Equal(receipt.Lease.MaxCumulativeDuration, ticket.LeaseMaxCumulativeDuration);
        Assert.Equal(receipt.Lease.AuthorizationDigest, ticket.LeaseAuthorizationDigest);
        Assert.Equal(receipt.Lease.Status, authorization!.LeaseStatus);
        Assert.Equal(receipt.Use.Id, ticket.UseId);
        Assert.Equal(receipt.Use.LeaseId, ticket.UseLeaseId);
        Assert.Equal(receipt.Use.OccurrenceId, ticket.UseOccurrenceId);
        Assert.Equal(receipt.Use.RunId, ticket.UseRunId);
        Assert.Equal(receipt.Use.Status, ticket.UseStatus);
        Assert.Equal(receipt.Use.ReservedUseCount, ticket.UseReservedUseCount);
        Assert.Equal(receipt.Use.ReservedDuration, ticket.UseReservedDuration);
        Assert.Equal(receipt.Use.ActualSettledDuration, ticket.UseActualSettledDuration);
        Assert.Equal(receipt.Use.CreatedAtUtc, ticket.UseCreatedAtUtc);
        Assert.Equal(receipt.Use.UpdatedAtUtc, ticket.UseUpdatedAtUtc);
        Assert.Equal(receipt.Use.Version, ticket.UseVersion);
        Assert.Equal(receipt.Use.IsQuotaConsumed, ticket.UseIsQuotaConsumed);
        Assert.Equal(receipt.Run.Id, ticket.RunId);
        Assert.Equal(receipt.Run.OccurrenceId, ticket.RunOccurrenceId);
        Assert.Equal(receipt.Run.Status, ticket.RunStatus);
        Assert.Equal(receipt.Run.HasCrossedStartCommit, ticket.RunHasCrossedStartCommit);
        Assert.Equal(receipt.Run.CreatedAtUtc, ticket.RunCreatedAtUtc);
        Assert.Equal(receipt.Run.UpdatedAtUtc, ticket.RunUpdatedAtUtc);
        Assert.Equal(receipt.Run.Version, ticket.RunVersion);
        Assert.Same(authorization.Specification, ticket.Specification);
        Assert.Equal(receipt.Specification.OccurrenceIdentity, ticket.OccurrenceIdentity);
        Assert.Equal(profile.ProfileId, ticket.ProfileId);
        Assert.Equal(profile.ProfileVersion, ticket.ProfileVersion);
        Assert.Equal(profile.ProfileDigest, ticket.ProfileDigest);
        Assert.Equal(profile.TargetType, ticket.TargetType);
        Assert.Equal(profile.RebindPolicy, ticket.RebindPolicy);
        Assert.Equal(profile.CaptureSemantics, ticket.CaptureSemantics);
        Assert.Equal(profile.CoordinateSpace, ticket.CoordinateSpace);
        Assert.Equal(profile.DisplayIdentityStatus, ticket.DisplayIdentityStatus);
        Assert.Equal(profile.StableDisplayFingerprint, ticket.StableDisplayFingerprint);
        Assert.Equal(profile.VirtualScreenRegion, ticket.VirtualScreenRegion);
        Assert.Equal(profile.DisplayBounds, ticket.DisplayBounds);
        Assert.Equal(profile.RegionWithinDisplay, ticket.RegionWithinDisplay);
        Assert.Equal(profile.DpiX, ticket.DpiX);
        Assert.Equal(profile.DpiY, ticket.DpiY);
        Assert.Equal(profile.PhysicalWidth, ticket.PhysicalWidth);
        Assert.Equal(profile.PhysicalHeight, ticket.PhysicalHeight);
        Assert.Equal(profile.Orientation, ticket.Orientation);
        Assert.Equal(profile.TopologyDigest, ticket.TopologyDigest);
        Assert.Equal(profile.Duration, ticket.Duration);
        Assert.Equal(profile.CountdownSeconds, ticket.CountdownSeconds);
        Assert.Equal(profile.OutputDirectory, ticket.NormalizedOutputDirectory);
        Assert.Equal(receipt.Specification.FrozenOutputFileName, ticket.FrozenOutputFileName);
        Assert.Equal(receipt.Specification.FrozenOutputFilePath, ticket.OutputFilePath);
        Assert.Equal(AuthorizedCaptureBackend.FfmpegRegion, ticket.Backend);
        Assert.Equal(AuthorizedAudioMode.None, ticket.AudioMode);
        Assert.Equal(AuthorizedOutputConflictPolicy.FailIfExists, ticket.OutputConflictPolicy);
        Assert.Equal(AuthorizedWakePolicy.NaturalWakeOnly, ticket.WakePolicy);
        Assert.Equal(AuthorizedDesktopRequirement.InteractiveDesktopRequired, ticket.DesktopRequirement);
        Assert.True(ticket.IsProofConsumed);
        Assert.False(ticket.IsClaimed);
        Assert.Equal(1, provider.CaptureCount);

        var serialized = JsonSerializer.Serialize(ticket);
        Assert.DoesNotContain(context.Proof.OneTimeNonce, serialized, StringComparison.Ordinal);
        Assert.DoesNotContain(context.Proof.ProofId, serialized, StringComparison.Ordinal);
        Assert.DoesNotContain("nonce", serialized, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(typeof(RecurringLeaseCaptureExecutionTicket)
            .GetConstructors(BindingFlags.Public | BindingFlags.Instance));
        Assert.Empty(typeof(RecurringLeaseCaptureExecutionTicket)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance));
        var forbiddenTypes = new[]
        {
            typeof(RecurringLeaseCaptureAuthorization),
            typeof(RecurringLeaseExecutionSnapshot),
            typeof(PlanDefinition),
            typeof(PlanOccurrence),
            typeof(RecurringConsentLease),
            typeof(LeaseUse),
            typeof(RecordingRun),
        };
        Assert.DoesNotContain(
            typeof(RecurringLeaseCaptureExecutionTicket)
                .GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                .Select(field => Nullable.GetUnderlyingType(field.FieldType) ?? field.FieldType),
            type => forbiddenTypes.Contains(type));
        Assert.DoesNotContain(
            typeof(RecurringLeaseCaptureExecutionTicket)
                .GetProperties(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                .Select(property => Nullable.GetUnderlyingType(property.PropertyType) ?? property.PropertyType),
            type => forbiddenTypes.Contains(type));
        Assert.DoesNotContain(
            typeof(RecurringLeaseCaptureAuthorization)
                .GetProperties(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic),
            property => property.Name == "SnapshotForTests" || forbiddenTypes.Contains(property.PropertyType));

        Assert.Throws<InvalidOperationException>(() => ticket.GetConsumedProofForBackendStart());
        Assert.True(ticket.TryClaim(out var claimReason), claimReason);
        Assert.Same(context.Proof, ticket.GetConsumedProofForBackendStart());
        Assert.False(ticket.TryClaim(out var retryReason));
        Assert.Equal("recurring_execution_already_claimed", retryReason);
        Assert.True(ticket.IsClaimed);
    }

    [Fact]
    public void RecurringLeaseCaptureExecutionTicket_SerialAuthorizationHandoffIsAtMostOnce()
    {
        using var context = CreateCommittedContext();
        Assert.True(CreateLoader(context, new CountingProvider()).TryAuthorizeAndConsumeRecurringLeaseUse(
            context.Proof, out var authorization, out var authorizationReason), authorizationReason);

        Assert.True(RecurringLeaseCaptureExecutionTicket.TryCreate(
            authorization, out var first, out var firstReason), firstReason);
        Assert.NotNull(first);
        Assert.False(RecurringLeaseCaptureExecutionTicket.TryCreate(
            authorization, out var second, out var secondReason));
        Assert.Null(second);
        Assert.Equal("recurring_execution_authorization_already_handed_off", secondReason);
    }

    [Fact]
    public void RecurringLeaseCaptureExecutionTicket_ConcurrentHandoffIsAtMostOnce()
    {
        using var context = CreateCommittedContext();
        Assert.True(CreateLoader(context, new CountingProvider()).TryAuthorizeAndConsumeRecurringLeaseUse(
            context.Proof, out var authorization, out var authorizationReason), authorizationReason);

        var outcomes = new ConcurrentBag<(bool Success, RecurringLeaseCaptureExecutionTicket? Ticket, string Reason)>();
        Parallel.For(0, 64, _ =>
        {
            var success = RecurringLeaseCaptureExecutionTicket.TryCreate(
                authorization, out var ticket, out var reason);
            outcomes.Add((success, ticket, reason));
        });

        Assert.Equal(1, outcomes.Count(item => item.Success));
        Assert.Single(outcomes.Where(item => item.Success && item.Ticket is not null));
        Assert.Equal(63, outcomes.Count(item => !item.Success));
        Assert.All(
            outcomes.Where(item => !item.Success),
            item => Assert.Equal("recurring_execution_authorization_already_handed_off", item.Reason));
    }

    [Fact]
    public void RecurringLeaseCaptureExecutionTicket_ConcurrentClaimIsAtMostOnce()
    {
        using var context = CreateCommittedContext();
        Assert.True(CreateLoader(context, new CountingProvider()).TryAuthorizeAndConsumeRecurringLeaseUse(
            context.Proof, out var authorization, out var authorizationReason), authorizationReason);
        Assert.True(RecurringLeaseCaptureExecutionTicket.TryCreate(
            authorization, out var ticket, out var ticketReason), ticketReason);

        var outcomes = new ConcurrentBag<(bool Success, string Reason)>();
        Parallel.For(0, 64, _ =>
        {
            var success = ticket!.TryClaim(out var reason);
            outcomes.Add((success, reason));
        });

        Assert.Equal(1, outcomes.Count(item => item.Success));
        Assert.Equal(63, outcomes.Count(item => !item.Success));
        Assert.All(
            outcomes.Where(item => !item.Success),
            item => Assert.Equal("recurring_execution_already_claimed", item.Reason));
    }

    [Fact]
    public void RecurringLeaseCaptureExecutionTicket_InvalidAuthorizationDoesNotBurnValidHandoff()
    {
        using var context = CreateCommittedContext();
        Assert.True(CreateLoader(context, new CountingProvider()).TryAuthorizeAndConsumeRecurringLeaseUse(
            context.Proof, out var validAuthorization, out var authorizationReason), authorizationReason);
        Assert.NotNull(validAuthorization);

        var unconsumedProof = new RecurringLeaseUseProof(
            "recurring_task268_unconsumed",
            context.Proof.RunId,
            context.Proof.LeaseId,
            context.Proof.LeaseUseId,
            context.Proof.OccurrenceIdentity,
            context.Proof.SpecificationDigest,
            context.Proof.AuthorizationSourceId,
            context.Proof.CapturePlanDigest,
            context.Proof.ScopeDigest,
            context.Proof.IssuedAtUtc,
            context.Proof.ExpiresAtUtc,
            context.Proof.CurrentUserSid,
            context.Proof.SessionBinding,
            "0123456789abcdef0123456789abcdef",
            context.Proof.MaxDuration);
        Assert.Throws<InvalidOperationException>(() => new RecurringLeaseCaptureAuthorization(
            unconsumedProof,
            BuildSnapshot(context),
            validAuthorization.ValidatedAtUtc));
        Assert.True(RecurringLeaseCaptureExecutionTicket.TryCreate(
            validAuthorization, out var validTicket, out var validReason), validReason);
        Assert.NotNull(validTicket);
    }

    [Fact]
    public void RecurringLeaseCaptureExecutionTicket_ProofBindingMismatchIsRejectedBeforeHandoffClaim()
    {
        using var context = CreateCommittedContext();
        Assert.True(CreateLoader(context, new CountingProvider()).TryAuthorizeAndConsumeRecurringLeaseUse(
            context.Proof, out var validAuthorization, out var authorizationReason), authorizationReason);
        Assert.NotNull(validAuthorization);

        var tamperedProof = new RecurringLeaseUseProof(
            "recurring_task268_tampered",
            context.Proof.RunId,
            context.Proof.LeaseId,
            context.Proof.LeaseUseId,
            context.Proof.OccurrenceIdentity,
            context.Proof.SpecificationDigest,
            context.Proof.AuthorizationSourceId,
            context.Proof.CapturePlanDigest + "-tampered",
            context.Proof.ScopeDigest,
            context.Proof.IssuedAtUtc,
            context.Proof.ExpiresAtUtc,
            context.Proof.CurrentUserSid,
            context.Proof.SessionBinding,
            "0123456789abcdef0123456789abcde1",
            context.Proof.MaxDuration);
        tamperedProof.TryConsume(context.Now, out _);
        var tamperedAuthorization = new RecurringLeaseCaptureAuthorization(
            tamperedProof,
            BuildSnapshot(context),
            validAuthorization.ValidatedAtUtc);

        Assert.False(RecurringLeaseCaptureExecutionTicket.TryCreate(
            tamperedAuthorization, out var tamperedTicket, out var tamperedReason));
        Assert.Null(tamperedTicket);
        Assert.Equal("recurring_execution_authorization_invalid", tamperedReason);
        Assert.True(RecurringLeaseCaptureExecutionTicket.TryCreate(
            validAuthorization, out var validTicket, out var validReason), validReason);
        Assert.NotNull(validTicket);
    }

    [Fact]
    public void RecurringLeaseCaptureExecutionTicket_ParentChainMismatchIsRejected()
    {
        using var context = CreateCommittedContext();
        Assert.True(CreateLoader(context, new CountingProvider()).TryAuthorizeAndConsumeRecurringLeaseUse(
            context.Proof, out var validAuthorization, out var authorizationReason), authorizationReason);
        Assert.NotNull(validAuthorization);
        var receipt = Assert.IsType<RecurringStartCommitReceipt>(context.Result.FirstCommitReceipt);
        var source = BuildSnapshot(context);
        var oneShotPlan = PlanDefinition.Rehydrate(
            receipt.Plan.Id,
            isOneTime: true,
            PlanDefinitionStatus.Enabled,
            receipt.Plan.CreatedAtUtc,
            receipt.Plan.UpdatedAtUtc,
            receipt.Plan.Version);
        var mismatchedSnapshot = new RecurringLeaseExecutionSnapshot(
            oneShotPlan,
            receipt.Lease,
            context.Context.Fixture.Setup.ExactProfile,
            source.Approval,
            source.Safety,
            receipt.Occurrence,
            receipt.Specification,
            receipt.Run,
            receipt.Use,
            receipt.LeaseEntries,
            receipt.Quota);
        var mismatchedAuthorization = new RecurringLeaseCaptureAuthorization(
            context.Proof,
            mismatchedSnapshot,
            validAuthorization.ValidatedAtUtc);

        Assert.False(RecurringLeaseCaptureExecutionTicket.TryCreate(
            mismatchedAuthorization, out var ticket, out var reason));
        Assert.Null(ticket);
        Assert.Equal("recurring_execution_authorization_invalid", reason);
    }

    [Fact]
    public void RecurringLeaseCaptureExecutionTicket_AllowsActiveAndLegallyExhaustedLease()
    {
        using var activeContext = CreateCommittedContext(maxUses: 10);
        Assert.True(CreateLoader(activeContext, new CountingProvider()).TryAuthorizeAndConsumeRecurringLeaseUse(
            activeContext.Proof, out var activeAuthorization, out var activeReason), activeReason);
        Assert.Equal(ConsentLeaseStatus.Active, activeAuthorization!.LeaseStatus);
        Assert.True(RecurringLeaseCaptureExecutionTicket.TryCreate(
            activeAuthorization, out var activeTicket, out var activeTicketReason), activeTicketReason);
        Assert.NotNull(activeTicket);

        using var exhaustedContext = CreateCommittedContext(maxUses: 1);
        Assert.True(CreateLoader(exhaustedContext, new CountingProvider()).TryAuthorizeAndConsumeRecurringLeaseUse(
            exhaustedContext.Proof, out var exhaustedAuthorization, out var exhaustedReason), exhaustedReason);
        Assert.Equal(ConsentLeaseStatus.Exhausted, exhaustedAuthorization!.LeaseStatus);
        Assert.True(RecurringLeaseCaptureExecutionTicket.TryCreate(
            exhaustedAuthorization, out var exhaustedTicket, out var exhaustedTicketReason), exhaustedTicketReason);
        Assert.NotNull(exhaustedTicket);
    }

    [Fact]
    public void RecurringLeaseCaptureExecutionTicket_FreezesEvidenceAfterOriginalAggregatesChange()
    {
        using var context = CreateCommittedContext();
        Assert.True(context.Proof.TryConsume(context.Now, out var proofReason), proofReason);
        var snapshot = BuildSnapshot(context);
        var authorization = new RecurringLeaseCaptureAuthorization(
            context.Proof,
            snapshot,
            context.Now);
        Assert.True(RecurringLeaseCaptureExecutionTicket.TryCreate(
            authorization, out var ticket, out var ticketReason), ticketReason);
        Assert.NotNull(ticket);

        var before = new
        {
            ticket!.PlanId,
            ticket.PlanStatus,
            ticket.PlanUpdatedAtUtc,
            ticket.PlanVersion,
            ticket.OccurrenceStatus,
            ticket.OccurrenceUpdatedAtUtc,
            ticket.OccurrenceVersion,
            ticket.LeaseStatus,
            ticket.LeaseUpdatedAtUtc,
            ticket.LeaseVersion,
            ticket.UseStatus,
            ticket.UseUpdatedAtUtc,
            ticket.UseVersion,
            ticket.RunStatus,
            ticket.RunUpdatedAtUtc,
            ticket.RunVersion,
            ticket.Specification,
            ticket.VirtualScreenRegion,
            ticket.DpiX,
            ticket.DpiY,
            ticket.Duration,
            ticket.CountdownSeconds,
            ticket.OutputFilePath,
            ticket.TargetType,
            ticket.CaptureSemantics,
            ticket.CoordinateSpace,
            ticket.DisplayIdentityStatus,
            ticket.RebindPolicy,
            ticket.WakePolicy,
            ticket.DesktopRequirement,
        };
        var receipt = Assert.IsType<RecurringStartCommitReceipt>(context.Result.FirstCommitReceipt);
        var changedAt = context.Now.AddSeconds(1);

        Assert.True(receipt.Plan.TryMarkUpdated(changedAt).Succeeded);
        Assert.True(receipt.Occurrence.TryTransition(PlanOccurrenceStatus.Completed, changedAt).Succeeded);
        Assert.True(receipt.Lease.TryTransition(ConsentLeaseStatus.Revoked, changedAt).Succeeded);
        Assert.True(receipt.Use.TryTransition(LeaseUseStatus.Consumed, changedAt).Succeeded);
        Assert.True(receipt.Run.TryTransition(RecordingRunStatus.Recording, changedAt).Succeeded);

        Assert.Equal(before.PlanId, ticket.PlanId);
        Assert.Equal(before.PlanStatus, ticket.PlanStatus);
        Assert.Equal(before.PlanUpdatedAtUtc, ticket.PlanUpdatedAtUtc);
        Assert.Equal(before.PlanVersion, ticket.PlanVersion);
        Assert.Equal(before.OccurrenceStatus, ticket.OccurrenceStatus);
        Assert.Equal(before.OccurrenceUpdatedAtUtc, ticket.OccurrenceUpdatedAtUtc);
        Assert.Equal(before.OccurrenceVersion, ticket.OccurrenceVersion);
        Assert.Equal(before.LeaseStatus, ticket.LeaseStatus);
        Assert.Equal(before.LeaseUpdatedAtUtc, ticket.LeaseUpdatedAtUtc);
        Assert.Equal(before.LeaseVersion, ticket.LeaseVersion);
        Assert.Equal(before.UseStatus, ticket.UseStatus);
        Assert.Equal(before.UseUpdatedAtUtc, ticket.UseUpdatedAtUtc);
        Assert.Equal(before.UseVersion, ticket.UseVersion);
        Assert.Equal(before.RunStatus, ticket.RunStatus);
        Assert.Equal(before.RunUpdatedAtUtc, ticket.RunUpdatedAtUtc);
        Assert.Equal(before.RunVersion, ticket.RunVersion);
        Assert.Same(before.Specification, ticket.Specification);
        Assert.Equal(before.VirtualScreenRegion, ticket.VirtualScreenRegion);
        Assert.Equal(before.DpiX, ticket.DpiX);
        Assert.Equal(before.DpiY, ticket.DpiY);
        Assert.Equal(before.Duration, ticket.Duration);
        Assert.Equal(before.CountdownSeconds, ticket.CountdownSeconds);
        Assert.Equal(before.OutputFilePath, ticket.OutputFilePath);
        Assert.Equal(before.TargetType, ticket.TargetType);
        Assert.Equal(before.CaptureSemantics, ticket.CaptureSemantics);
        Assert.Equal(before.CoordinateSpace, ticket.CoordinateSpace);
        Assert.Equal(before.DisplayIdentityStatus, ticket.DisplayIdentityStatus);
        Assert.Equal(before.RebindPolicy, ticket.RebindPolicy);
        Assert.Equal(before.WakePolicy, ticket.WakePolicy);
        Assert.Equal(before.DesktopRequirement, ticket.DesktopRequirement);
        Assert.True(ticket.TryClaim(out var claimReason), claimReason);
        Assert.True(ticket.IsClaimed);
    }

    [Fact]
    public async Task RecurringExecutionBridge_StartsOnceWithExactConfigAndConsumedProof()
    {
        using var context = CreateCommittedContext();
        var ticket = CreateExecutionTicket(context, out var provider);
        var backend = new BridgeBackend();

        var bridge = new RecurringLeaseCaptureExecutionBridge(
            provider,
            _ => backend,
            delayForTest: (duration, _) =>
            {
                Assert.Equal(TimeSpan.FromSeconds(ticket.CountdownSeconds), duration);
                return Task.CompletedTask;
            },
            utcNowForTest: () => context.Now,
            startSafetyInterlockForTest: new StandingLeaseStartSafetyInterlock(),
            currentSafetyValidatorForTest: (_, _) => AllowedSafetyDecision(),
            lifecycleSessionFactoryForTest: (_, _) => new TestLifecycleSession());

        var result = await bridge.ExecuteAsync(ticket);

        Assert.Equal(RecurringLeaseCaptureExecutionStatus.Started, result.Status);
        Assert.Same(backend, result.Backend);
        Assert.Same(context.Proof, backend.Proof);
        Assert.Equal(1, backend.StartCalls);
        Assert.Equal(0, backend.DisposeCalls);
        var config = Assert.IsType<CaptureConfig>(backend.Configuration);
        var specification = ticket.Specification;
        Assert.Equal("region", config.SourceKind);
        Assert.Equal("video", config.Mode);
        Assert.Equal(
            (specification.VirtualScreenRegion.X, specification.VirtualScreenRegion.Y,
                specification.VirtualScreenRegion.Width, specification.VirtualScreenRegion.Height),
            config.Bounds);
        Assert.Equal(
            (specification.DisplayBounds.X, specification.DisplayBounds.Y,
                specification.DisplayBounds.Width, specification.DisplayBounds.Height),
            config.DisplayBounds);
        Assert.Null(config.DisplayId);
        Assert.Equal(specification.StableDisplayFingerprint, config.DisplayStableIdentity);
        Assert.Equal(DisplayIdentityResolutionStatus.Resolved, config.DisplayIdentityStatus);
        Assert.Equal(specification.FrozenOutputFilePath, config.OutputPath);
        Assert.Equal("fail_if_exists", config.OutputConflictPolicy);
        Assert.Equal(AudioCaptureSourceKind.None, config.AudioSourceKind);
        Assert.False(config.Microphone);
        Assert.Null(config.MicDevice);
        Assert.Null(config.MicDeviceName);
        Assert.Null(config.SystemLoopbackEndpoint);
        Assert.Null(config.SystemLoopbackEndpointName);
        Assert.Null(config.SystemLoopbackEndpointIsDefault);
        Assert.Null(config.WindowTitle);
        Assert.Equal(nint.Zero, config.WindowHandle);
        Assert.Null(config.ScreenshotSeries);
        Assert.Equal(30, config.Fps);
        Assert.Equal("medium", config.Quality);
        Assert.Equal((int)specification.Duration.TotalSeconds, config.DurationSeconds);
        Assert.Equal(specification.CountdownSeconds, config.CountdownSeconds);
        Assert.Equal("", config.CommandArgs);
        Assert.Null(config.RegionNormalizedBounds);
        Assert.False(config.DeferCaptureStart);
        Assert.Equal(context.Now, provider.LastRequest!.TrustedNowUtc);
        Assert.Equal(specification.DpiX, provider.LastRequest.Requirements.DpiX);
        Assert.Equal(specification.DpiY, provider.LastRequest.Requirements.DpiY);
    }

    [Fact]
    public async Task RecurringLeaseExecutionBridge_MissingLifecycleFactoryDisposesBackendWithoutStart()
    {
        using var context = CreateCommittedContext();
        var ticket = CreateExecutionTicket(context, out var provider);
        var backend = new BridgeBackend();
        var bridge = new RecurringLeaseCaptureExecutionBridge(
            provider,
            _ => backend,
            delayForTest: (_, _) => Task.CompletedTask,
            utcNowForTest: () => context.Now,
            startSafetyInterlockForTest: new StandingLeaseStartSafetyInterlock(),
            currentSafetyValidatorForTest: (_, _) => AllowedSafetyDecision());

        var result = await bridge.ExecuteAsync(ticket);

        Assert.Equal(RecurringLeaseCaptureExecutionStatus.Failed, result.Status);
        Assert.Equal("recurring_execution_lifecycle_session_unavailable", result.Reason);
        Assert.Null(result.Backend);
        Assert.Null(result.LifecycleSession);
        Assert.Equal(0, backend.StartCalls);
        Assert.Equal(1, backend.DisposeCalls);
    }

    [Fact]
    public async Task RecurringLeaseExecutionBridge_LifecycleFactoryFailuresAreStableAndDisposeBackendOnce()
    {
        using var throwingContext = CreateCommittedContext();
        var throwingTicket = CreateExecutionTicket(throwingContext, out var throwingProvider);
        var throwingBackend = new BridgeBackend();
        var throwingBridge = new RecurringLeaseCaptureExecutionBridge(
            throwingProvider,
            _ => throwingBackend,
            delayForTest: (_, _) => Task.CompletedTask,
            utcNowForTest: () => throwingContext.Now,
            startSafetyInterlockForTest: new StandingLeaseStartSafetyInterlock(),
            currentSafetyValidatorForTest: (_, _) => AllowedSafetyDecision(),
            lifecycleSessionFactoryForTest: (_, _) =>
                throw new InvalidOperationException("lifecycle detail"));

        var throwing = await throwingBridge.ExecuteAsync(throwingTicket);

        Assert.Equal(RecurringLeaseCaptureExecutionStatus.Failed, throwing.Status);
        Assert.Equal("recurring_execution_lifecycle_session_unavailable", throwing.Reason);
        Assert.DoesNotContain("lifecycle detail", throwing.Reason, StringComparison.Ordinal);
        Assert.Equal(0, throwingBackend.StartCalls);
        Assert.Equal(1, throwingBackend.DisposeCalls);
        Assert.Null(throwing.Backend);
        Assert.Null(throwing.LifecycleSession);

        using var nullContext = CreateCommittedContext();
        var nullTicket = CreateExecutionTicket(nullContext, out var nullProvider);
        var nullBackend = new BridgeBackend();
        var nullBridge = new RecurringLeaseCaptureExecutionBridge(
            nullProvider,
            _ => nullBackend,
            delayForTest: (_, _) => Task.CompletedTask,
            utcNowForTest: () => nullContext.Now,
            startSafetyInterlockForTest: new StandingLeaseStartSafetyInterlock(),
            currentSafetyValidatorForTest: (_, _) => AllowedSafetyDecision(),
            lifecycleSessionFactoryForTest: (_, _) => null);

        var nullResult = await nullBridge.ExecuteAsync(nullTicket);

        Assert.Equal(RecurringLeaseCaptureExecutionStatus.Failed, nullResult.Status);
        Assert.Equal("recurring_execution_lifecycle_session_unavailable", nullResult.Reason);
        Assert.Equal(0, nullBackend.StartCalls);
        Assert.Equal(1, nullBackend.DisposeCalls);
        Assert.Null(nullResult.Backend);
        Assert.Null(nullResult.LifecycleSession);
    }

    [Fact]
    public async Task RecurringLeaseExecutionBridge_AttachesExactClaimedTicketAndBackendAndReturnsBothOnSuccess()
    {
        using var context = CreateCommittedContext();
        var ticket = CreateExecutionTicket(context, out var provider);
        var backend = new BridgeBackend();
        RecurringLeaseCaptureExecutionTicket? observedTicket = null;
        ICaptureBackend? observedBackend = null;
        TestLifecycleSession? session = null;
        var bridge = new RecurringLeaseCaptureExecutionBridge(
            provider,
            _ => backend,
            delayForTest: (_, _) => Task.CompletedTask,
            utcNowForTest: () => context.Now,
            startSafetyInterlockForTest: new StandingLeaseStartSafetyInterlock(),
            currentSafetyValidatorForTest: (_, _) => AllowedSafetyDecision(),
            lifecycleSessionFactoryForTest: (claimedTicket, exactBackend) =>
            {
                observedTicket = claimedTicket;
                observedBackend = exactBackend;
                session = new TestLifecycleSession();
                return session;
            });

        var result = await bridge.ExecuteAsync(ticket);

        Assert.Equal(RecurringLeaseCaptureExecutionStatus.Started, result.Status);
        Assert.Same(ticket, observedTicket);
        Assert.Same(backend, observedBackend);
        Assert.Same(backend, result.Backend);
        Assert.Same(session, result.LifecycleSession);
        Assert.Same(backend, session!.AttachedBackend);
        Assert.Equal(1, session.AttachCalls);
        Assert.Equal(1, backend.StartCalls);
        Assert.Equal(0, backend.DisposeCalls);
    }

    [Fact]
    public async Task RecurringLeaseExecutionBridge_AttachFailureKeepsBridgeOwnershipAndDisposesBothOwnersOnce()
    {
        using var context = CreateCommittedContext();
        var ticket = CreateExecutionTicket(context, out var provider);
        var backend = new BridgeBackend();
        var session = new TestLifecycleSession { AttachSucceeds = false };
        var bridge = new RecurringLeaseCaptureExecutionBridge(
            provider,
            _ => backend,
            delayForTest: (_, _) => Task.CompletedTask,
            utcNowForTest: () => context.Now,
            startSafetyInterlockForTest: new StandingLeaseStartSafetyInterlock(),
            currentSafetyValidatorForTest: (_, _) => AllowedSafetyDecision(),
            lifecycleSessionFactoryForTest: (_, _) => session);

        var result = await bridge.ExecuteAsync(ticket);

        Assert.Equal(RecurringLeaseCaptureExecutionStatus.Failed, result.Status);
        Assert.Equal("recurring_execution_lifecycle_session_attach_failed", result.Reason);
        Assert.Null(result.Backend);
        Assert.Null(result.LifecycleSession);
        Assert.Equal(0, backend.StartCalls);
        Assert.Equal(1, backend.DisposeCalls);
        Assert.Equal(1, session.DisposeCalls);
        Assert.Equal(0, session.StartFailedCalls);
    }

    [Fact]
    public async Task RecurringLeaseExecutionBridge_StartFailureUsesSessionTerminalizationWithoutDirectBackendCleanup()
    {
        using var context = CreateCommittedContext();
        var ticket = CreateExecutionTicket(context, out var provider);
        var backend = new BridgeObservableBackend
        {
            RaiseFirstFrameDuringStart = true,
            ThrowOnStart = true,
        };
        var bridge = new RecurringLeaseCaptureExecutionBridge(
            provider,
            _ => backend,
            delayForTest: (_, _) => Task.CompletedTask,
            utcNowForTest: () => context.Now,
            startSafetyInterlockForTest: new StandingLeaseStartSafetyInterlock(),
            currentSafetyValidatorForTest: (_, _) => AllowedSafetyDecision(),
            lifecycleSessionFactoryForTest: (claimedTicket, exactBackend) =>
                new RecurringLeaseCaptureExecutionSession(
                    context.Context.Fixture.Store,
                    claimedTicket,
                    exactBackend,
                    () => context.Now));

        var result = await bridge.ExecuteAsync(ticket);
        var run = new SqliteRecordingRunRepository(context.Context.Fixture.Store).Get(ticket.RunId);
        var use = new SqliteRecurringLeaseUseAccountingReader(context.Context.Fixture.Store)
            .TryGetByOccurrence(ticket.OccurrenceIdentity);
        var occurrence = new SqlitePlanOccurrenceRepository(context.Context.Fixture.Store)
            .Get(ticket.OccurrenceId);

        Assert.Equal(RecurringLeaseCaptureExecutionStatus.Failed, result.Status);
        Assert.Equal("recurring_execution_backend_start_failed", result.Reason);
        Assert.Null(result.Backend);
        Assert.Null(result.LifecycleSession);
        Assert.Equal(1, backend.StartCalls);
        Assert.Equal(0, backend.StopCalls);
        Assert.Equal(1, backend.DisposeCalls);
        Assert.Equal(RecordingRunStatus.Failed, run.Status);
        Assert.Equal(LeaseUseStatus.StartedUnknown, use!.Status);
        Assert.Equal(PlanOccurrenceStatus.Blocked, occurrence.Status);
    }

    [Fact]
    public async Task RecurringLeaseExecutionBridge_SynchronousNaturalExitCannotReturnReleasedStartedBackend()
    {
        using var context = CreateCommittedContext();
        var ticket = CreateExecutionTicket(context, out var provider);
        var backend = new BridgeObservableBackend { RaiseNaturalExitDuringStart = true };
        var bridge = new RecurringLeaseCaptureExecutionBridge(
            provider,
            _ => backend,
            delayForTest: (_, _) => Task.CompletedTask,
            utcNowForTest: () => context.Now,
            startSafetyInterlockForTest: new StandingLeaseStartSafetyInterlock(),
            currentSafetyValidatorForTest: (_, _) => AllowedSafetyDecision(),
            lifecycleSessionFactoryForTest: (claimedTicket, exactBackend) =>
                new RecurringLeaseCaptureExecutionSession(
                    context.Context.Fixture.Store,
                    claimedTicket,
                    exactBackend,
                    () => context.Now));

        var result = await bridge.ExecuteAsync(ticket);
        var run = new SqliteRecordingRunRepository(context.Context.Fixture.Store).Get(ticket.RunId);
        var use = new SqliteRecurringLeaseUseAccountingReader(context.Context.Fixture.Store)
            .TryGetByOccurrence(ticket.OccurrenceIdentity);
        var occurrence = new SqlitePlanOccurrenceRepository(context.Context.Fixture.Store)
            .Get(ticket.OccurrenceId);

        Assert.Equal(RecurringLeaseCaptureExecutionStatus.Failed, result.Status);
        Assert.Equal("recurring_execution_abnormal_terminal_during_start", result.Reason);
        Assert.Null(result.Backend);
        Assert.Null(result.LifecycleSession);
        Assert.Equal(1, backend.StartCalls);
        Assert.Equal(0, backend.StopCalls);
        Assert.Equal(1, backend.DisposeCalls);
        Assert.Equal(RecordingRunStatus.StartedUnknown, run.Status);
        Assert.Equal("natural_exit_before_first_frame", run.TerminalReasonCode);
        Assert.Equal(LeaseUseStatus.StartedUnknown, use!.Status);
        Assert.Equal(PlanOccurrenceStatus.Blocked, occurrence.Status);
    }

    [Fact]
    public async Task RecurringLeaseExecutionBridge_SynchronousFirstFrameThenNaturalExitReturnsOnlyDurableMediaSettledCompletion()
    {
        using var context = CreateCommittedContext();
        var ticket = CreateExecutionTicket(context, out var provider);
        var backend = new BridgeObservableBackend
        {
            RaiseFirstFrameDuringStart = true,
            RaiseNaturalExitDuringStart = true,
        };
        var bridge = new RecurringLeaseCaptureExecutionBridge(
            provider,
            _ => backend,
            delayForTest: (_, _) => Task.CompletedTask,
            utcNowForTest: () => context.Now,
            startSafetyInterlockForTest: new StandingLeaseStartSafetyInterlock(),
            currentSafetyValidatorForTest: (_, _) => AllowedSafetyDecision(),
            lifecycleSessionFactoryForTest: (claimedTicket, exactBackend) =>
                new RecurringLeaseCaptureExecutionSession(
                    context.Context.Fixture.Store,
                    claimedTicket,
                    exactBackend,
                    () => context.Now));

        var result = await bridge.ExecuteAsync(ticket);
        var run = new SqliteRecordingRunRepository(context.Context.Fixture.Store).Get(ticket.RunId);
        var use = new SqliteRecurringLeaseUseAccountingReader(context.Context.Fixture.Store)
            .TryGetByOccurrence(ticket.OccurrenceIdentity);
        var occurrence = new SqlitePlanOccurrenceRepository(context.Context.Fixture.Store)
            .Get(ticket.OccurrenceId);

        Assert.Equal(RecurringLeaseCaptureExecutionStatus.CompletedDuringStart, result.Status);
        Assert.Equal("recurring_execution_completed_during_start", result.Reason);
        Assert.Null(result.Backend);
        Assert.Null(result.LifecycleSession);
        Assert.Equal(1, backend.StartCalls);
        Assert.Equal(0, backend.StopCalls);
        Assert.Equal(1, backend.DisposeCalls);
        Assert.Equal(RecordingRunStatus.Settled, run.Status);
        Assert.Equal(LeaseUseStatus.Settled, use!.Status);
        Assert.Equal(PlanOccurrenceStatus.Completed, occurrence.Status);
    }

    [Fact]
    public async Task RecurringLeaseExecutionBridge_SynchronousFirstFramePersistenceFailureFailsClosedWithoutSuccess()
    {
        using var context = CreateCommittedContext();
        var ticket = CreateExecutionTicket(context, out var provider);
        var backend = new BridgeObservableBackend { RaiseFirstFrameDuringStart = true };
        var hookCalls = 0;
        var bridge = new RecurringLeaseCaptureExecutionBridge(
            provider,
            _ => backend,
            delayForTest: (_, _) => Task.CompletedTask,
            utcNowForTest: () => context.Now,
            startSafetyInterlockForTest: new StandingLeaseStartSafetyInterlock(),
            currentSafetyValidatorForTest: (_, _) => AllowedSafetyDecision(),
            lifecycleSessionFactoryForTest: (claimedTicket, exactBackend) =>
                new RecurringLeaseCaptureExecutionSession(
                    context.Context.Fixture.Store,
                    claimedTicket,
                    exactBackend,
                    () => context.Now,
                    failureHookForTest: point =>
                    {
                        if (point == RecurringLeaseLifecycleFailurePoint.AfterRunUpdate &&
                            Interlocked.Increment(ref hookCalls) == 1)
                            throw new InvalidOperationException("injected bridge callback persistence failure");
                    }));

        var result = await bridge.ExecuteAsync(ticket);
        var run = new SqliteRecordingRunRepository(context.Context.Fixture.Store).Get(ticket.RunId);
        var use = new SqliteRecurringLeaseUseAccountingReader(context.Context.Fixture.Store)
            .TryGetByOccurrence(ticket.OccurrenceIdentity);
        var occurrence = new SqlitePlanOccurrenceRepository(context.Context.Fixture.Store)
            .Get(ticket.OccurrenceId);

        Assert.Equal(RecurringLeaseCaptureExecutionStatus.Failed, result.Status);
        Assert.Equal("recurring_execution_lifecycle_failure", result.Reason);
        Assert.Null(result.Backend);
        Assert.Null(result.LifecycleSession);
        Assert.Equal(1, backend.StartCalls);
        Assert.InRange(backend.StopCalls, 0, 1);
        Assert.Equal(1, backend.DisposeCalls);
        Assert.NotEqual(RecordingRunStatus.Settled, run.Status);
        Assert.NotEqual(LeaseUseStatus.Settled, use!.Status);
        Assert.NotEqual(PlanOccurrenceStatus.Completed, occurrence.Status);
    }

    [Fact]
    public async Task RecurringLeaseExecutionBridge_SynchronousFirstFrameReturnsActiveSessionAndLaterSettlementOwnsCleanup()
    {
        using var context = CreateCommittedContext();
        var ticket = CreateExecutionTicket(context, out var provider);
        var backend = new BridgeObservableBackend { RaiseFirstFrameDuringStart = true };
        var bridge = new RecurringLeaseCaptureExecutionBridge(
            provider,
            _ => backend,
            delayForTest: (_, _) => Task.CompletedTask,
            utcNowForTest: () => context.Now,
            startSafetyInterlockForTest: new StandingLeaseStartSafetyInterlock(),
            currentSafetyValidatorForTest: (_, _) => AllowedSafetyDecision(),
            lifecycleSessionFactoryForTest: (claimedTicket, exactBackend) =>
                new RecurringLeaseCaptureExecutionSession(
                    context.Context.Fixture.Store,
                    claimedTicket,
                    exactBackend,
                    () => context.Now));

        var result = await bridge.ExecuteAsync(ticket);

        Assert.Equal(RecurringLeaseCaptureExecutionStatus.Started, result.Status);
        Assert.Same(backend, result.Backend);
        Assert.NotNull(result.LifecycleSession);
        Assert.Equal(0, backend.DisposeCalls);
        Assert.Equal(
            RecurringLeaseCaptureLifecycleHandoffStatus.Active,
            result.LifecycleSession!.CompleteStartHandoff().Status);

        backend.RaiseNaturalExit(0, ValidMetaForBridge(ticket.OutputFilePath));

        Assert.Equal(1, backend.DisposeCalls);
        Assert.Equal(RecordingRunStatus.Settled,
            new SqliteRecordingRunRepository(context.Context.Fixture.Store).Get(ticket.RunId).Status);
    }

    [Fact]
    public async Task RecurringLeaseExecutionBridge_CancellationAfterAttachTerminalizesSessionWithoutStop()
    {
        using var context = CreateCommittedContext();
        var ticket = CreateExecutionTicket(context, out var provider);
        var backend = new BridgeObservableBackend();
        using var cancellation = new CancellationTokenSource();
        var bridge = new RecurringLeaseCaptureExecutionBridge(
            provider,
            _ => backend,
            delayForTest: (_, _) => Task.CompletedTask,
            utcNowForTest: () => context.Now,
            startSafetyInterlockForTest: new StandingLeaseStartSafetyInterlock(),
            currentSafetyValidatorForTest: (_, _) => AllowedSafetyDecision(),
            lifecycleSessionFactoryForTest: (claimedTicket, exactBackend) =>
            {
                var inner = new RecurringLeaseCaptureExecutionSession(
                    context.Context.Fixture.Store,
                    claimedTicket,
                    exactBackend,
                    () => context.Now);
                return new CancelAfterAttachSession(inner, cancellation);
            });

        var result = await bridge.ExecuteAsync(ticket, cancellation.Token);
        var run = new SqliteRecordingRunRepository(context.Context.Fixture.Store).Get(ticket.RunId);
        var use = new SqliteRecurringLeaseUseAccountingReader(context.Context.Fixture.Store)
            .TryGetByOccurrence(ticket.OccurrenceIdentity);
        var occurrence = new SqlitePlanOccurrenceRepository(context.Context.Fixture.Store)
            .Get(ticket.OccurrenceId);

        Assert.Equal(RecurringLeaseCaptureExecutionStatus.Cancelled, result.Status);
        Assert.Null(result.Backend);
        Assert.Null(result.LifecycleSession);
        Assert.Equal(0, backend.StartCalls);
        Assert.Equal(0, backend.StopCalls);
        Assert.Equal(1, backend.DisposeCalls);
        Assert.Equal(RecordingRunStatus.StartedUnknown, run.Status);
        Assert.Equal("session_interrupted_before_first_frame", run.TerminalReasonCode);
        Assert.Equal(LeaseUseStatus.StartedUnknown, use!.Status);
        Assert.Equal(PlanOccurrenceStatus.Blocked, occurrence.Status);
    }

    [Fact]
    public async Task RecurringExecutionBridge_ClaimIsPermanentAndRepeatedExecutionCannotStart()
    {
        using var context = CreateCommittedContext();
        var ticket = CreateExecutionTicket(context, out var provider);
        var backend = new BridgeBackend();
        var factoryCalls = 0;
        var bridge = new RecurringLeaseCaptureExecutionBridge(
            provider,
            _ =>
            {
                Interlocked.Increment(ref factoryCalls);
                return backend;
            },
            delayForTest: (_, _) => Task.CompletedTask,
            utcNowForTest: () => context.Now,
            startSafetyInterlockForTest: new StandingLeaseStartSafetyInterlock(),
            currentSafetyValidatorForTest: (_, _) => AllowedSafetyDecision(),
            lifecycleSessionFactoryForTest: (_, _) => new TestLifecycleSession());

        var missing = await bridge.ExecuteAsync(null);
        var first = await bridge.ExecuteAsync(ticket);
        var second = await bridge.ExecuteAsync(ticket);

        Assert.Equal("recurring_execution_ticket_missing", missing.Reason);
        Assert.Equal(RecurringLeaseCaptureExecutionStatus.Started, first.Status);
        Assert.Equal(RecurringLeaseCaptureExecutionStatus.Rejected, second.Status);
        Assert.Equal("recurring_execution_already_claimed", second.Reason);
        Assert.Equal(1, factoryCalls);
        Assert.Equal(2, provider.CaptureCount);
        Assert.Equal(1, backend.StartCalls);
    }

    [Fact]
    public async Task RecurringExecutionBridge_ZeroCountdownSkipsDelayButStillUsesFreshClockAndEnvironment()
    {
        using var context = CreateCommittedContext(countdownSeconds: 0);
        var ticket = CreateExecutionTicket(context, out var provider);
        var backend = new BridgeBackend();
        var delayCalls = 0;
        var clockCalls = 0;
        var bridge = new RecurringLeaseCaptureExecutionBridge(
            provider,
            _ => backend,
            delayForTest: (_, _) =>
            {
                Interlocked.Increment(ref delayCalls);
                return Task.CompletedTask;
            },
            utcNowForTest: () =>
            {
                Interlocked.Increment(ref clockCalls);
                return context.Now;
            },
            startSafetyInterlockForTest: new StandingLeaseStartSafetyInterlock(),
            currentSafetyValidatorForTest: (_, _) => AllowedSafetyDecision(),
            lifecycleSessionFactoryForTest: (_, _) => new TestLifecycleSession());

        var result = await bridge.ExecuteAsync(ticket);

        Assert.Equal(RecurringLeaseCaptureExecutionStatus.Started, result.Status);
        Assert.Equal(0, delayCalls);
        Assert.Equal(2, clockCalls);
        Assert.Equal(2, provider.CaptureCount);
        Assert.Equal(1, backend.StartCalls);
        Assert.Equal(0, backend.Configuration!.CountdownSeconds);
    }

    [Fact]
    public async Task RecurringExecutionBridge_64ConcurrentCallsHaveOneContinuation()
    {
        using var context = CreateCommittedContext();
        var ticket = CreateExecutionTicket(context, out var provider);
        var backend = new BridgeBackend();
        var factoryCalls = 0;
        var bridge = new RecurringLeaseCaptureExecutionBridge(
            provider,
            _ =>
            {
                Interlocked.Increment(ref factoryCalls);
                return backend;
            },
            delayForTest: (_, _) => Task.CompletedTask,
            utcNowForTest: () => context.Now,
            startSafetyInterlockForTest: new StandingLeaseStartSafetyInterlock(),
            currentSafetyValidatorForTest: (_, _) => AllowedSafetyDecision(),
            lifecycleSessionFactoryForTest: (_, _) => new TestLifecycleSession());

        var tasks = Enumerable.Range(0, 64)
            .Select(_ => Task.Run(() => bridge.ExecuteAsync(ticket)))
            .ToArray();
        var results = await Task.WhenAll(tasks);

        Assert.Single(results, result => result.Status == RecurringLeaseCaptureExecutionStatus.Started);
        Assert.Equal(63, results.Count(result => result.Reason == "recurring_execution_already_claimed"));
        Assert.Equal(1, factoryCalls);
        Assert.Equal(2, provider.CaptureCount);
        Assert.Equal(1, backend.StartCalls);
        Assert.Equal(0, backend.DisposeCalls);
    }

    [Fact]
    public async Task RecurringExecutionBridge_CancellationAndDelayFailureBurnClaimWithoutEnvironment()
    {
        using var cancellationContext = CreateCommittedContext();
        var cancellationTicket = CreateExecutionTicket(cancellationContext, out var cancellationProvider);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var cancelledFactoryCalls = 0;
        var cancelledBridge = new RecurringLeaseCaptureExecutionBridge(
            cancellationProvider,
            _ =>
            {
                Interlocked.Increment(ref cancelledFactoryCalls);
                return new BridgeBackend();
            },
            delayForTest: (_, token) => Task.FromCanceled(token),
            utcNowForTest: () => cancellationContext.Now,
            startSafetyInterlockForTest: new StandingLeaseStartSafetyInterlock(),
            currentSafetyValidatorForTest: (_, _) => AllowedSafetyDecision());

        var cancelled = await cancelledBridge.ExecuteAsync(cancellationTicket, cancellation.Token);
        var cancelledRetry = await cancelledBridge.ExecuteAsync(cancellationTicket);

        Assert.Equal(RecurringLeaseCaptureExecutionStatus.Cancelled, cancelled.Status);
        Assert.Equal("recurring_execution_cancelled", cancelled.Reason);
        Assert.Equal("recurring_execution_already_claimed", cancelledRetry.Reason);
        Assert.Equal(1, cancellationProvider.CaptureCount);
        Assert.Equal(0, cancelledFactoryCalls);

        using var failureContext = CreateCommittedContext();
        var failureTicket = CreateExecutionTicket(failureContext, out var failureProvider);
        var failedBridge = new RecurringLeaseCaptureExecutionBridge(
            failureProvider,
            _ => throw new InvalidOperationException("test factory detail"),
            delayForTest: (_, _) => Task.FromException(new InvalidOperationException("test delay detail")),
            utcNowForTest: () => failureContext.Now,
            startSafetyInterlockForTest: new StandingLeaseStartSafetyInterlock(),
            currentSafetyValidatorForTest: (_, _) => AllowedSafetyDecision());

        var failed = await failedBridge.ExecuteAsync(failureTicket);
        Assert.Equal(RecurringLeaseCaptureExecutionStatus.Failed, failed.Status);
        Assert.Equal("recurring_execution_countdown_failed", failed.Reason);
        Assert.Equal(1, failureProvider.CaptureCount);
    }

    [Fact]
    public async Task RecurringExecutionBridge_FinalTimeRejectionDisposesConstructedBackend()
    {
        using var context = CreateCommittedContext(startCommitOffset: TimeSpan.FromSeconds(5));
        var ticket = CreateExecutionTicket(context, out var provider);
        var backend = new BridgeBackend();
        var clock = new Queue<DateTimeOffset>(new[]
        {
            context.Now,
            ticket.Specification.LatestStartUtc.AddTicks(1),
        });
        var bridge = new RecurringLeaseCaptureExecutionBridge(
            provider,
            _ => backend,
            delayForTest: (_, _) => Task.CompletedTask,
            utcNowForTest: () => clock.Dequeue(),
            startSafetyInterlockForTest: new StandingLeaseStartSafetyInterlock(),
            currentSafetyValidatorForTest: (_, _) => AllowedSafetyDecision());

        var result = await bridge.ExecuteAsync(ticket);

        Assert.Equal(RecurringLeaseCaptureExecutionStatus.Rejected, result.Status);
        Assert.Equal("recurring_execution_start_window_expired", result.Reason);
        Assert.Equal(0, backend.StartCalls);
        Assert.Equal(1, backend.DisposeCalls);
    }

    [Fact]
    public async Task RecurringExecutionBridge_SafetyFailureDisposesBackendAndNeverLeaksValidatorText()
    {
        using var context = CreateCommittedContext();
        var ticket = CreateExecutionTicket(context, out var provider);
        var backend = new BridgeBackend();
        var bridge = new RecurringLeaseCaptureExecutionBridge(
            provider,
            _ => backend,
            delayForTest: (_, _) => Task.CompletedTask,
            utcNowForTest: () => context.Now,
            startSafetyInterlockForTest: new StandingLeaseStartSafetyInterlock(),
            currentSafetyValidatorForTest: (_, _) =>
                new RecurringLeaseCurrentSafetyDecision(RecurringLeaseCurrentSafetyStatus.LeaseRevoked));

        var result = await bridge.ExecuteAsync(ticket);

        Assert.Equal(RecurringLeaseCaptureExecutionStatus.Rejected, result.Status);
        Assert.Equal("recurring_execution_lease_revoked", result.Reason);
        Assert.DoesNotContain(context.Proof.ProofId, result.Reason, StringComparison.Ordinal);
        Assert.DoesNotContain(context.Proof.OneTimeNonce, result.Reason, StringComparison.Ordinal);
        Assert.Equal(0, backend.StartCalls);
        Assert.Equal(1, backend.DisposeCalls);
    }

    [Fact]
    public async Task RecurringExecutionBridge_EnvironmentTimeMismatchAndBackendFailuresAreStable()
    {
        using var mismatchContext = CreateCommittedContext();
        var mismatchTicket = CreateExecutionTicket(mismatchContext, out _);
        var mismatchProvider = new CountingProvider(timeOffsetTicks: 1);
        var mismatchFactoryCalls = 0;
        var mismatchBridge = new RecurringLeaseCaptureExecutionBridge(
            mismatchProvider,
            _ =>
            {
                Interlocked.Increment(ref mismatchFactoryCalls);
                return new BridgeBackend();
            },
            delayForTest: (_, _) => Task.CompletedTask,
            utcNowForTest: () => mismatchContext.Now,
            startSafetyInterlockForTest: new StandingLeaseStartSafetyInterlock(),
            currentSafetyValidatorForTest: (_, _) => AllowedSafetyDecision());

        var mismatch = await mismatchBridge.ExecuteAsync(mismatchTicket);
        Assert.Equal("recurring_execution_environment_time_mismatch", mismatch.Reason);
        Assert.Equal(0, mismatchFactoryCalls);

        using var nullFactoryContext = CreateCommittedContext();
        var nullFactoryTicket = CreateExecutionTicket(nullFactoryContext, out var nullFactoryProvider);
        var nullFactoryBridge = new RecurringLeaseCaptureExecutionBridge(
            nullFactoryProvider,
            _ => null,
            delayForTest: (_, _) => Task.CompletedTask,
            utcNowForTest: () => nullFactoryContext.Now,
            startSafetyInterlockForTest: new StandingLeaseStartSafetyInterlock(),
            currentSafetyValidatorForTest: (_, _) => AllowedSafetyDecision());
        var nullFactory = await nullFactoryBridge.ExecuteAsync(nullFactoryTicket);
        Assert.Equal(RecurringLeaseCaptureExecutionStatus.Failed, nullFactory.Status);
        Assert.Equal("recurring_execution_backend_unavailable", nullFactory.Reason);
        Assert.Null(nullFactory.Backend);

        using var startFailureContext = CreateCommittedContext();
        var startFailureTicket = CreateExecutionTicket(startFailureContext, out var startFailureProvider);
        var startFailureBackend = new BridgeBackend { ThrowOnStart = true };
        var startFailureSession = new TestLifecycleSession();
        var startFailureBridge = new RecurringLeaseCaptureExecutionBridge(
            startFailureProvider,
            _ => startFailureBackend,
            delayForTest: (_, _) => Task.CompletedTask,
            utcNowForTest: () => startFailureContext.Now,
            startSafetyInterlockForTest: new StandingLeaseStartSafetyInterlock(),
            currentSafetyValidatorForTest: (_, _) => AllowedSafetyDecision(),
            lifecycleSessionFactoryForTest: (_, _) => startFailureSession);
        var startFailure = await startFailureBridge.ExecuteAsync(startFailureTicket);
        Assert.Equal("recurring_execution_backend_start_failed", startFailure.Reason);
        Assert.Equal(1, startFailureBackend.StartCalls);
        Assert.Equal(0, startFailureBackend.DisposeCalls);
        Assert.Equal(1, startFailureSession.StartFailedCalls);
        Assert.Equal(1, startFailureSession.DisposeCalls);
    }

    [Fact]
    public async Task RecurringExecutionBridge_UsesSharedInterlockForSafetyFirstAndStartFirstOrders()
    {
        using var safetyFirstContext = CreateCommittedContext();
        var safetyFirstTicket = CreateExecutionTicket(safetyFirstContext, out var safetyFirstProvider);
        var safetyFirstBackend = new BridgeBackend();
        var safetyFirstInterlock = new StandingLeaseStartSafetyInterlock();
        var factoryReady = new ManualResetEventSlim();
        var startObserved = new ManualResetEventSlim();
        var safetyFirstStatus = RecurringLeaseCurrentSafetyStatus.Allowed;
        Task<RecurringLeaseCaptureExecutionResult>? safetyFirstTask = null;
        var safetyFirstBridge = new RecurringLeaseCaptureExecutionBridge(
            safetyFirstProvider,
            _ =>
            {
                factoryReady.Set();
                return safetyFirstBackend;
            },
            delayForTest: (_, _) => Task.CompletedTask,
            utcNowForTest: () => safetyFirstContext.Now,
            startSafetyInterlockForTest: safetyFirstInterlock,
            currentSafetyValidatorForTest: (_, _) =>
            {
                startObserved.Set();
                return new RecurringLeaseCurrentSafetyDecision(safetyFirstStatus);
            });

        safetyFirstInterlock.Execute("safety-control-first", () =>
        {
            safetyFirstStatus = RecurringLeaseCurrentSafetyStatus.LeaseRevoked;
            safetyFirstTask = Task.Run(() => safetyFirstBridge.ExecuteAsync(safetyFirstTicket));
            Assert.True(factoryReady.Wait(TimeSpan.FromSeconds(5)));
            Assert.False(startObserved.IsSet);
        });
        var safetyFirstResult = await safetyFirstTask!;
        Assert.Equal(RecurringLeaseCaptureExecutionStatus.Rejected, safetyFirstResult.Status);
        Assert.Equal("recurring_execution_lease_revoked", safetyFirstResult.Reason);
        Assert.True(startObserved.IsSet);
        Assert.Equal(0, safetyFirstBackend.StartCalls);
        Assert.Equal(1, safetyFirstBackend.DisposeCalls);

        using var startFirstContext = CreateCommittedContext();
        var startFirstTicket = CreateExecutionTicket(startFirstContext, out var startFirstProvider);
        var startFirstInterlock = new StandingLeaseStartSafetyInterlock();
        var safetyControlCompleted = new ManualResetEventSlim();
        var startFirstBackend = new BridgeBackend();
        startFirstBackend.DuringStart = () =>
        {
            startFirstBackend.SafetyTask = Task.Run(() => startFirstInterlock.Execute(
                "safety-control-after-start-linearization",
                safetyControlCompleted.Set));
            startFirstBackend.SafetyWasBlockedDuringStart = !safetyControlCompleted.Wait(100);
        };
        var startFirstBridge = new RecurringLeaseCaptureExecutionBridge(
            startFirstProvider,
            _ => startFirstBackend,
            delayForTest: (_, _) => Task.CompletedTask,
            utcNowForTest: () => startFirstContext.Now,
            startSafetyInterlockForTest: startFirstInterlock,
            currentSafetyValidatorForTest: (_, _) => AllowedSafetyDecision(),
            lifecycleSessionFactoryForTest: (_, _) => new TestLifecycleSession());

        var startFirstResult = await startFirstBridge.ExecuteAsync(startFirstTicket);
        Assert.Equal(RecurringLeaseCaptureExecutionStatus.Started, startFirstResult.Status);
        Assert.True(startFirstBackend.SafetyWasBlockedDuringStart);
        Assert.True(startFirstBackend.SafetyTask!.Wait(TimeSpan.FromSeconds(5)));
        Assert.True(safetyControlCompleted.IsSet);
    }

    [Fact]
    public async Task RecurringLeaseSafetyControl_RealServiceSafetyFirstCommitsBeforeBridgeValidator()
    {
        using var context = CreateCommittedContext(maxUses: 10);
        using var factoryReady = new ManualResetEventSlim();
        using var validatorObserved = new ManualResetEventSlim();
        var interlock = new StandingLeaseStartSafetyInterlock();
        var stopper = new CountingSafetyStopper();
        var validator = new SqliteRecurringLeaseCurrentSafetyValidator(context.Context.Fixture.Store);
        var backend = new BridgeBackend();
        var ticket = CreateExecutionTicket(context, out _);
        var bridge = new RecurringLeaseCaptureExecutionBridge(
            new CountingProvider(),
            _ =>
            {
                factoryReady.Set();
                return backend;
            },
            delayForTest: (_, _) => Task.CompletedTask,
            utcNowForTest: () => context.Now,
            startSafetyInterlockForTest: interlock,
            currentSafetyValidatorForTest: (ticket, nowUtc) =>
            {
                validatorObserved.Set();
                return validator.Validate(ticket, nowUtc);
            },
            lifecycleSessionFactoryForTest: (_, _) => new TestLifecycleSession());
        var service = new StandingLeaseSafetyControlService(
            context.Context.Fixture.Store,
            () => context.Now,
            activeRunStopper: stopper,
            startSafetyInterlock: interlock);
        Task<RecurringLeaseCaptureExecutionResult>? bridgeTask = null;

        interlock.Execute("task271R-safety-first", () =>
        {
            var revoke = service.RevokeRecurringLease(
                context.Context.Lease.LeaseId,
                "task271R-safety-first-revoke",
                "task271R-test");
            Assert.Equal(StandingLeaseSafetyControlResultStatus.Changed, revoke.Status);
            Assert.True(revoke.DurableOperationCommitted);
            Assert.True(revoke.DurableStateChanged);

            bridgeTask = Task.Run(() => bridge.ExecuteAsync(ticket));
            Assert.True(factoryReady.Wait(TimeSpan.FromSeconds(5)));
            Assert.False(validatorObserved.IsSet);
        });

        var result = await bridgeTask!.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(RecurringLeaseCaptureExecutionStatus.Rejected, result.Status);
        Assert.Equal("recurring_execution_lease_revoked", result.Reason);
        Assert.True(validatorObserved.IsSet);
        Assert.Equal(0, backend.StartCalls);
        Assert.Equal(1, backend.DisposeCalls);
        Assert.Equal(1, stopper.Count);
    }

    [Fact]
    public async Task RecurringLeaseSafetyControl_RealServiceStartFirstCommitsAfterBackendStart()
    {
        using var context = CreateCommittedContext(maxUses: 10);
        using var serviceClockEntered = new ManualResetEventSlim();
        using var validatorObserved = new ManualResetEventSlim();
        var interlock = new StandingLeaseStartSafetyInterlock();
        var stopper = new CountingSafetyStopper();
        var validator = new SqliteRecurringLeaseCurrentSafetyValidator(context.Context.Fixture.Store);
        var backend = new BridgeBackend();
        StandingLeaseSafetyControlService? service = null;
        Task<StandingLeaseSafetyControlResult>? revokeTask = null;
        backend.DuringStart = () =>
        {
            revokeTask = Task.Run(() => service!.RevokeRecurringLease(
                context.Context.Lease.LeaseId,
                "task271R-start-first-revoke",
                "task271R-test"));
            Assert.True(serviceClockEntered.Wait(TimeSpan.FromSeconds(5)));
            Assert.False(revokeTask.IsCompleted);
        };
        var bridge = new RecurringLeaseCaptureExecutionBridge(
            new CountingProvider(),
            _ => backend,
            delayForTest: (_, _) => Task.CompletedTask,
            utcNowForTest: () => context.Now,
            startSafetyInterlockForTest: interlock,
            currentSafetyValidatorForTest: (ticket, nowUtc) =>
            {
                validatorObserved.Set();
                return validator.Validate(ticket, nowUtc);
            },
            lifecycleSessionFactoryForTest: (_, _) => new TestLifecycleSession());
        service = new StandingLeaseSafetyControlService(
            context.Context.Fixture.Store,
            () =>
            {
                serviceClockEntered.Set();
                return context.Now;
            },
            activeRunStopper: stopper,
            startSafetyInterlock: interlock);

        var result = await bridge.ExecuteAsync(CreateExecutionTicket(context, out _));
        var revoke = await revokeTask!.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(RecurringLeaseCaptureExecutionStatus.Started, result.Status);
        Assert.True(validatorObserved.IsSet);
        Assert.Equal(1, backend.StartCalls);
        Assert.Equal(0, backend.DisposeCalls);
        Assert.Equal(StandingLeaseSafetyControlResultStatus.Changed, revoke.Status);
        Assert.True(revoke.DurableOperationCommitted);
        Assert.True(revoke.DurableStateChanged);
        Assert.Equal(ConsentLeaseStatus.Revoked,
            new SqliteRecurringConsentLeaseRepository(context.Context.Fixture.Store)
                .Get(context.Context.Lease.LeaseId).Status);
        Assert.Equal(1, stopper.Count);
    }

    [Fact]
    public async Task RecurringExecutionBridge_RejectsOldTicketAfterDurableRecurringLeaseRevoke()
    {
        using var context = CreateCommittedContext(maxUses: 10);
        var ticket = CreateExecutionTicket(context, out var provider);
        var revoked = new StandingLeaseSafetyControlService(
            context.Context.Fixture.Store,
            () => context.Now)
            .RevokeRecurringLease(
                context.Context.Lease.LeaseId,
                "task271-old-ticket-revoke",
                "task271-test");
        Assert.Equal(StandingLeaseSafetyControlResultStatus.Changed, revoked.Status);

        var backend = new BridgeBackend();
        var bridge = new RecurringLeaseCaptureExecutionBridge(
            provider,
            _ => backend,
            delayForTest: (_, _) => Task.CompletedTask,
            utcNowForTest: () => context.Now,
            startSafetyInterlockForTest: new StandingLeaseStartSafetyInterlock(),
            currentSafetyValidatorForTest: new SqliteRecurringLeaseCurrentSafetyValidator(context.Context.Fixture.Store).Validate);

        var result = await bridge.ExecuteAsync(ticket);

        Assert.Equal(RecurringLeaseCaptureExecutionStatus.Rejected, result.Status);
        Assert.Equal("recurring_execution_lease_revoked", result.Reason);
        Assert.Equal(0, backend.StartCalls);
        Assert.Equal(1, backend.DisposeCalls);
    }

    [Fact]
    public async Task RecurringExecutionBridge_CancellationFromProviderStopsBeforeFactoryAndBurnsTicket()
    {
        using var context = CreateCommittedContext();
        var ticketProvider = new CountingProvider();
        var ticket = CreateExecutionTicket(context, ticketProvider);
        using var cancellation = new CancellationTokenSource();
        var provider = new CancellingProvider(ticketProvider, cancellation);
        var factoryCalls = 0;
        var bridge = new RecurringLeaseCaptureExecutionBridge(
            provider,
            _ =>
            {
                Interlocked.Increment(ref factoryCalls);
                return new BridgeBackend();
            },
            delayForTest: (_, _) => Task.CompletedTask,
            utcNowForTest: () => context.Now,
            startSafetyInterlockForTest: new StandingLeaseStartSafetyInterlock(),
            currentSafetyValidatorForTest: (_, _) => AllowedSafetyDecision());

        var result = await bridge.ExecuteAsync(ticket, cancellation.Token);
        var retry = await bridge.ExecuteAsync(ticket);

        Assert.Equal(RecurringLeaseCaptureExecutionStatus.Cancelled, result.Status);
        Assert.Equal("recurring_execution_cancelled", result.Reason);
        Assert.Equal("recurring_execution_already_claimed", retry.Reason);
        Assert.Equal(0, factoryCalls);
        Assert.Equal(2, ticketProvider.CaptureCount);
    }

    [Fact]
    public async Task RecurringExecutionBridge_CancellationFromFactoryDisposesWithoutStartAndBurnsTicket()
    {
        using var context = CreateCommittedContext();
        var ticket = CreateExecutionTicket(context, out var provider);
        using var cancellation = new CancellationTokenSource();
        var backend = new BridgeBackend();
        var factoryCalls = 0;
        var bridge = new RecurringLeaseCaptureExecutionBridge(
            provider,
            _ =>
            {
                Interlocked.Increment(ref factoryCalls);
                cancellation.Cancel();
                return backend;
            },
            delayForTest: (_, _) => Task.CompletedTask,
            utcNowForTest: () => context.Now,
            startSafetyInterlockForTest: new StandingLeaseStartSafetyInterlock(),
            currentSafetyValidatorForTest: (_, _) => AllowedSafetyDecision());

        var result = await bridge.ExecuteAsync(ticket, cancellation.Token);
        var retry = await bridge.ExecuteAsync(ticket);

        Assert.Equal(RecurringLeaseCaptureExecutionStatus.Cancelled, result.Status);
        Assert.Equal("recurring_execution_cancelled", result.Reason);
        Assert.Equal("recurring_execution_already_claimed", retry.Reason);
        Assert.Equal(1, factoryCalls);
        Assert.Equal(0, backend.StartCalls);
        Assert.Equal(1, backend.DisposeCalls);
    }

    [Fact]
    public async Task RecurringExecutionBridge_CancellationFromSafetyValidatorDisposesWithoutStartAndBurnsTicket()
    {
        using var context = CreateCommittedContext();
        var ticket = CreateExecutionTicket(context, out var provider);
        using var cancellation = new CancellationTokenSource();
        var backend = new BridgeBackend();
        var validatorCalls = 0;
        var bridge = new RecurringLeaseCaptureExecutionBridge(
            provider,
            _ => backend,
            delayForTest: (_, _) => Task.CompletedTask,
            utcNowForTest: () => context.Now,
            startSafetyInterlockForTest: new StandingLeaseStartSafetyInterlock(),
            currentSafetyValidatorForTest: (_, _) =>
            {
                Interlocked.Increment(ref validatorCalls);
                cancellation.Cancel();
                return AllowedSafetyDecision();
            });

        var result = await bridge.ExecuteAsync(ticket, cancellation.Token);
        var retry = await bridge.ExecuteAsync(ticket);

        Assert.Equal(RecurringLeaseCaptureExecutionStatus.Cancelled, result.Status);
        Assert.Equal("recurring_execution_cancelled", result.Reason);
        Assert.Equal("recurring_execution_already_claimed", retry.Reason);
        Assert.Equal(1, validatorCalls);
        Assert.Equal(0, backend.StartCalls);
        Assert.Equal(1, backend.DisposeCalls);
    }

    [Fact]
    public async Task RecurringExecutionBridge_MapsEveryTypedSafetyStatusToFixedReasonWithoutSensitiveMaterial()
    {
        var cases = new[]
        {
            (RecurringLeaseCurrentSafetyStatus.UnattendedDisabled,
                "recurring_execution_unattended_disabled"),
            (RecurringLeaseCurrentSafetyStatus.StopAllActive,
                "recurring_execution_stop_all_active"),
            (RecurringLeaseCurrentSafetyStatus.LeaseRevoked,
                "recurring_execution_lease_revoked"),
            (RecurringLeaseCurrentSafetyStatus.ReenableRequiresNewAuthorization,
                "recurring_execution_reenable_requires_new_authorization"),
            (RecurringLeaseCurrentSafetyStatus.StateInvalid,
                "recurring_execution_safety_state_invalid"),
            ((RecurringLeaseCurrentSafetyStatus)999,
                "recurring_execution_safety_state_invalid"),
        };

        foreach (var (status, expectedReason) in cases)
        {
            using var context = CreateCommittedContext();
            var ticket = CreateExecutionTicket(context, out var provider);
            var backend = new BridgeBackend();
            var bridge = new RecurringLeaseCaptureExecutionBridge(
                provider,
                _ => backend,
                delayForTest: (_, _) => Task.CompletedTask,
                utcNowForTest: () => context.Now,
                startSafetyInterlockForTest: new StandingLeaseStartSafetyInterlock(),
                currentSafetyValidatorForTest: (_, _) =>
                    new RecurringLeaseCurrentSafetyDecision(status));

            var result = await bridge.ExecuteAsync(ticket);

            Assert.Equal(RecurringLeaseCaptureExecutionStatus.Rejected, result.Status);
            Assert.Equal(expectedReason, result.Reason);
            Assert.DoesNotContain(context.Proof.ProofId, result.Reason, StringComparison.Ordinal);
            Assert.DoesNotContain(context.Proof.OneTimeNonce, result.Reason, StringComparison.Ordinal);
            Assert.DoesNotContain(context.Proof.SpecificationDigest, result.Reason, StringComparison.Ordinal);
            Assert.DoesNotContain(
                context.Result.FirstCommitReceipt!.Specification.FrozenOutputFilePath,
                result.Reason,
                StringComparison.Ordinal);
            Assert.Equal(0, backend.StartCalls);
            Assert.Equal(1, backend.DisposeCalls);
        }
    }

    [Fact]
    public async Task RecurringExecutionBridge_MissingOrThrowingSafetyValidatorUsesFixedFailureReason()
    {
        using var missingContext = CreateCommittedContext();
        var missingTicket = CreateExecutionTicket(missingContext, out var missingProvider);
        var missingBackend = new BridgeBackend();
        var missingBridge = new RecurringLeaseCaptureExecutionBridge(
            missingProvider,
            _ => missingBackend,
            delayForTest: (_, _) => Task.CompletedTask,
            utcNowForTest: () => missingContext.Now,
            startSafetyInterlockForTest: new StandingLeaseStartSafetyInterlock());

        var missing = await missingBridge.ExecuteAsync(missingTicket);

        Assert.Equal(RecurringLeaseCaptureExecutionStatus.Rejected, missing.Status);
        Assert.Equal("recurring_execution_safety_validator_unavailable", missing.Reason);
        Assert.DoesNotContain("detail", missing.Reason, StringComparison.Ordinal);
        Assert.Equal(0, missingBackend.StartCalls);
        Assert.Equal(1, missingBackend.DisposeCalls);

        using var throwingContext = CreateCommittedContext();
        var throwingTicket = CreateExecutionTicket(throwingContext, out var throwingProvider);
        var throwingBackend = new BridgeBackend();
        var throwingBridge = new RecurringLeaseCaptureExecutionBridge(
            throwingProvider,
            _ => throwingBackend,
            delayForTest: (_, _) => Task.CompletedTask,
            utcNowForTest: () => throwingContext.Now,
            startSafetyInterlockForTest: new StandingLeaseStartSafetyInterlock(),
            currentSafetyValidatorForTest: (_, _) =>
                throw new InvalidOperationException("validator detail"));

        var throwing = await throwingBridge.ExecuteAsync(throwingTicket);

        Assert.Equal(RecurringLeaseCaptureExecutionStatus.Rejected, throwing.Status);
        Assert.Equal("recurring_execution_safety_validator_unavailable", throwing.Reason);
        Assert.DoesNotContain("validator detail", throwing.Reason, StringComparison.Ordinal);
        Assert.Equal(0, throwingBackend.StartCalls);
        Assert.Equal(1, throwingBackend.DisposeCalls);
    }

    [Fact]
    public void RecurringLeaseCurrentSafety_ValidSnapshotReturnsAllowedAndIsReadOnly()
    {
        using var context = CreateCommittedContext();
        var ticket = CreateExecutionTicket(context, out _);
        var validator = new SqliteRecurringLeaseCurrentSafetyValidator(context.Context.Fixture.Store);
        var leaseVersion = ReadLong(context, "SELECT version FROM recurring_consent_leases WHERE lease_id = $id;", ("$id", ticket.LeaseId));
        var safetyVersion = ReadLong(context, "SELECT version FROM unattended_safety_state WHERE state_id = 'global';");

        var first = validator.Validate(ticket, context.Now);
        var second = validator.Validate(ticket, context.Now);

        Assert.Equal(RecurringLeaseCurrentSafetyStatus.Allowed, first.Status);
        Assert.Equal(RecurringLeaseCurrentSafetyStatus.Allowed, second.Status);
        Assert.True(context.Proof.IsConsumed);
        Assert.Equal(leaseVersion, ReadLong(context, "SELECT version FROM recurring_consent_leases WHERE lease_id = $id;", ("$id", ticket.LeaseId)));
        Assert.Equal(safetyVersion, ReadLong(context, "SELECT version FROM unattended_safety_state WHERE state_id = 'global';"));
    }

    [Fact]
    public void RecurringLeaseCurrentSafety_DisabledReturnsUnattendedDisabled()
    {
        using var context = CreateCommittedContext();
        var ticket = CreateExecutionTicket(context, out _);
        SetGlobalSafetyState(
            context,
            modeCode: "disabled",
            modeChangedAtUtc: context.Now.AddSeconds(1),
            unattendedEnabledAtUtc: null,
            stopAllApplied: false,
            stopAllAtUtc: null);

        var decision = new SqliteRecurringLeaseCurrentSafetyValidator(context.Context.Fixture.Store)
            .Validate(ticket, context.Now);

        Assert.Equal(RecurringLeaseCurrentSafetyStatus.UnattendedDisabled, decision.Status);
    }

    [Fact]
    public void RecurringLeaseCurrentSafety_StopAllAfterApprovalReturnsStopAllActive()
    {
        using var context = CreateCommittedContext();
        var ticket = CreateExecutionTicket(context, out _);
        var approvedAt = context.Result.FirstCommitReceipt!.Approval.ApprovedAtUtc;
        SetGlobalSafetyState(
            context,
            modeCode: "enabled",
            modeChangedAtUtc: approvedAt,
            unattendedEnabledAtUtc: approvedAt,
            stopAllApplied: true,
            stopAllAtUtc: approvedAt.AddTicks(1));

        var decision = new SqliteRecurringLeaseCurrentSafetyValidator(context.Context.Fixture.Store)
            .Validate(ticket, context.Now);

        Assert.Equal(RecurringLeaseCurrentSafetyStatus.StopAllActive, decision.Status);
    }

    [Fact]
    public void RecurringLeaseCurrentSafety_StopAllBeforeApprovalDoesNotBlockNewApproval()
    {
        using var context = CreateCommittedContext();
        var ticket = CreateExecutionTicket(context, out _);
        var approvedAt = context.Result.FirstCommitReceipt!.Approval.ApprovedAtUtc;
        SetGlobalSafetyState(
            context,
            modeCode: "enabled",
            modeChangedAtUtc: approvedAt,
            unattendedEnabledAtUtc: approvedAt,
            stopAllApplied: true,
            stopAllAtUtc: approvedAt.AddTicks(-1));

        var decision = new SqliteRecurringLeaseCurrentSafetyValidator(context.Context.Fixture.Store)
            .Validate(ticket, context.Now);

        Assert.Equal(RecurringLeaseCurrentSafetyStatus.Allowed, decision.Status);
    }

    [Fact]
    public void RecurringLeaseCurrentSafety_ReenableAfterApprovalReturnsRequiresNewAuthorization()
    {
        using var context = CreateCommittedContext();
        var ticket = CreateExecutionTicket(context, out _);
        var approvedAt = context.Result.FirstCommitReceipt!.Approval.ApprovedAtUtc;
        SetGlobalSafetyState(
            context,
            modeCode: "enabled",
            modeChangedAtUtc: approvedAt.AddTicks(1),
            unattendedEnabledAtUtc: approvedAt.AddTicks(1),
            stopAllApplied: false,
            stopAllAtUtc: null);

        var decision = new SqliteRecurringLeaseCurrentSafetyValidator(context.Context.Fixture.Store)
            .Validate(ticket, context.Now);

        Assert.Equal(RecurringLeaseCurrentSafetyStatus.ReenableRequiresNewAuthorization, decision.Status);
    }

    [Fact]
    public void RecurringLeaseCurrentSafety_ExactLeaseRevocationReturnsLeaseRevoked()
    {
        using var context = CreateCommittedContext();
        var ticket = CreateExecutionTicket(context, out _);
        ExecuteSql(
            context,
            "UPDATE recurring_consent_leases SET status_code = 'revoked', updated_at_utc = $updated, version = version + 1 WHERE lease_id = $lease_id;",
            ("$updated", context.Now.UtcDateTime.Ticks),
            ("$lease_id", ticket.LeaseId));

        var decision = new SqliteRecurringLeaseCurrentSafetyValidator(context.Context.Fixture.Store)
            .Validate(ticket, context.Now);

        Assert.Equal(RecurringLeaseCurrentSafetyStatus.LeaseRevoked, decision.Status);
    }

    [Fact]
    public void RecurringLeaseCurrentSafety_NonUtcMissingGlobalAndPersistedDriftFailClosed()
    {
        using var nonUtcContext = CreateCommittedContext();
        var nonUtcTicket = CreateExecutionTicket(nonUtcContext, out _);
        var nonUtc = new SqliteRecurringLeaseCurrentSafetyValidator(nonUtcContext.Context.Fixture.Store)
            .Validate(nonUtcTicket, nonUtcContext.Now.ToOffset(TimeSpan.FromHours(8)));
        Assert.Equal(RecurringLeaseCurrentSafetyStatus.StateInvalid, nonUtc.Status);

        using var missingGlobalContext = CreateCommittedContext();
        var missingGlobalTicket = CreateExecutionTicket(missingGlobalContext, out _);
        ExecuteSql(missingGlobalContext, "DELETE FROM unattended_safety_state WHERE state_id = 'global';");
        var missingGlobal = new SqliteRecurringLeaseCurrentSafetyValidator(missingGlobalContext.Context.Fixture.Store)
            .Validate(missingGlobalTicket, missingGlobalContext.Now);
        Assert.Equal(RecurringLeaseCurrentSafetyStatus.StateInvalid, missingGlobal.Status);

        using var driftContext = CreateCommittedContext();
        var driftTicket = CreateExecutionTicket(driftContext, out _);
        ExecuteSql(
            driftContext,
            "UPDATE plans SET version = version + 1 WHERE id = $plan_id;",
            ("$plan_id", driftTicket.PlanId));
        var drift = new SqliteRecurringLeaseCurrentSafetyValidator(driftContext.Context.Fixture.Store)
            .Validate(driftTicket, driftContext.Now);
        Assert.Equal(RecurringLeaseCurrentSafetyStatus.StateInvalid, drift.Status);
    }

    [Theory]
    [InlineData("enabled_missing_enabled_at")]
    [InlineData("disabled_with_enabled_at")]
    [InlineData("enabled_timestamp_mismatch")]
    [InlineData("stop_all_requested_after_applied")]
    [InlineData("version_exhausted")]
    public void RecurringLeaseCurrentSafety_GlobalSafetyIntegrityContradictionsFailClosed(string mutation)
    {
        using var context = CreateCommittedContext();
        var ticket = CreateExecutionTicket(context, out _);
        var nowTicks = context.Now.UtcDateTime.Ticks;

        switch (mutation)
        {
            case "enabled_missing_enabled_at":
                ExecuteSql(
                    context,
                    "UPDATE unattended_safety_state SET unattended_mode_code = 'enabled', mode_changed_at_utc = $at, unattended_enabled_at_utc = NULL, stop_all_applied = 0, stop_all_operation_id = NULL, stop_all_reason_code = NULL, stop_all_requested_at_utc = NULL, stop_all_applied_at_utc = NULL, version = version + 1 WHERE state_id = 'global';",
                    ("$at", nowTicks));
                break;
            case "disabled_with_enabled_at":
                ExecuteSql(
                    context,
                    "UPDATE unattended_safety_state SET unattended_mode_code = 'disabled', mode_changed_at_utc = $at, unattended_enabled_at_utc = $at, stop_all_applied = 0, stop_all_operation_id = NULL, stop_all_reason_code = NULL, stop_all_requested_at_utc = NULL, stop_all_applied_at_utc = NULL, version = version + 1 WHERE state_id = 'global';",
                    ("$at", nowTicks));
                break;
            case "enabled_timestamp_mismatch":
                ExecuteSql(
                    context,
                    "UPDATE unattended_safety_state SET unattended_mode_code = 'enabled', mode_changed_at_utc = $mode_changed, unattended_enabled_at_utc = $enabled_at, stop_all_applied = 0, stop_all_operation_id = NULL, stop_all_reason_code = NULL, stop_all_requested_at_utc = NULL, stop_all_applied_at_utc = NULL, version = version + 1 WHERE state_id = 'global';",
                    ("$mode_changed", nowTicks),
                    ("$enabled_at", checked(nowTicks + 1)));
                break;
            case "stop_all_requested_after_applied":
                ExecuteSql(
                    context,
                    "UPDATE unattended_safety_state SET stop_all_applied = 1, stop_all_operation_id = 'task270r-stop', stop_all_reason_code = 'task270r-test', stop_all_requested_at_utc = $requested, stop_all_applied_at_utc = $applied, version = version + 1 WHERE state_id = 'global';",
                    ("$requested", checked(nowTicks + TimeSpan.TicksPerSecond)),
                    ("$applied", nowTicks));
                break;
            case "version_exhausted":
                ExecuteSql(
                    context,
                    "UPDATE unattended_safety_state SET version = $version WHERE state_id = 'global';",
                    ("$version", long.MaxValue));
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(mutation), mutation, null);
        }

        var decision = new SqliteRecurringLeaseCurrentSafetyValidator(context.Context.Fixture.Store)
            .Validate(ticket, context.Now);

        Assert.Equal(RecurringLeaseCurrentSafetyStatus.StateInvalid, decision.Status);
    }

    [Fact]
    public void RecurringLeaseCurrentSafety_RevokedWithoutSingleStepVersionIsInvalid()
    {
        using var context = CreateCommittedContext();
        var ticket = CreateExecutionTicket(context, out _);
        ExecuteSql(
            context,
            "DROP TRIGGER trg_recurring_consent_leases_lifecycle_update; UPDATE recurring_consent_leases SET status_code = 'revoked', updated_at_utc = $updated, version = $version WHERE lease_id = $lease_id;",
            ("$updated", context.Now.UtcDateTime.Ticks),
            ("$version", ticket.LeaseVersion),
            ("$lease_id", ticket.LeaseId));

        var decision = new SqliteRecurringLeaseCurrentSafetyValidator(context.Context.Fixture.Store)
            .Validate(ticket, context.Now);

        Assert.Equal(RecurringLeaseCurrentSafetyStatus.StateInvalid, decision.Status);
    }

    [Theory]
    [InlineData("version_skipped")]
    [InlineData("version_exhausted")]
    [InlineData("updated_before_ticket")]
    [InlineData("updated_after_now")]
    [InlineData("authorization_field_changed")]
    public void RecurringLeaseCurrentSafety_RevokedForgeryIsInvalid(string mutation)
    {
        using var context = CreateCommittedContext();
        var ticket = CreateExecutionTicket(context, out _);
        var revokeVersion = checked(ticket.LeaseVersion + 1);
        var revokeUpdatedAt = context.Now.UtcDateTime.Ticks;
        var sql = mutation == "authorization_field_changed"
            ? "DROP TRIGGER trg_recurring_consent_leases_lifecycle_update; UPDATE recurring_consent_leases SET status_code = 'revoked', valid_until_utc = valid_until_utc + 1, updated_at_utc = $updated, version = $version WHERE lease_id = $lease_id;"
            : "DROP TRIGGER trg_recurring_consent_leases_lifecycle_update; UPDATE recurring_consent_leases SET status_code = 'revoked', updated_at_utc = $updated, version = $version WHERE lease_id = $lease_id;";
        var version = mutation switch
        {
            "version_skipped" => checked(ticket.LeaseVersion + 2),
            "version_exhausted" => long.MaxValue,
            _ => revokeVersion,
        };
        var updated = mutation switch
        {
            "updated_before_ticket" => checked(ticket.LeaseUpdatedAtUtc.UtcDateTime.Ticks - 1),
            "updated_after_now" => checked(revokeUpdatedAt + 1),
            _ => revokeUpdatedAt,
        };
        ExecuteSql(
            context,
            sql,
            ("$updated", updated),
            ("$version", version),
            ("$lease_id", ticket.LeaseId));

        var decision = new SqliteRecurringLeaseCurrentSafetyValidator(context.Context.Fixture.Store)
            .Validate(ticket, context.Now);

        Assert.Equal(RecurringLeaseCurrentSafetyStatus.StateInvalid, decision.Status);
    }

    [Theory]
    [InlineData("plan")]
    [InlineData("occurrence")]
    [InlineData("slot")]
    [InlineData("lease")]
    [InlineData("schedule")]
    [InlineData("binding")]
    [InlineData("profile")]
    [InlineData("approval")]
    [InlineData("specification")]
    [InlineData("run")]
    [InlineData("use_accounting")]
    public void RecurringLeaseCurrentSafety_TrustRootDriftIsInvalid(string mutation)
    {
        using var context = CreateCommittedContext();
        var ticket = CreateExecutionTicket(context, out _);

        switch (mutation)
        {
            case "plan":
                ExecuteSql(context, "UPDATE plans SET version = version + 1 WHERE id = $id;", ("$id", ticket.PlanId));
                break;
            case "occurrence":
                ExecuteSql(context, "UPDATE plan_occurrences SET updated_at_utc = updated_at_utc + 1, version = version + 1 WHERE id = $id;", ("$id", ticket.OccurrenceId));
                break;
            case "slot":
                ExecuteSql(context, "UPDATE recurring_occurrence_slots SET local_wall_clock_seconds = local_wall_clock_seconds + 1 WHERE occurrence_identity = $identity;", ("$identity", ticket.OccurrenceIdentity));
                break;
            case "lease":
                ExecuteSql(context, "UPDATE recurring_consent_leases SET status_code = 'exhausted', updated_at_utc = updated_at_utc + 1, version = version + 1 WHERE lease_id = $id;", ("$id", ticket.LeaseId));
                break;
            case "schedule":
                ExecuteSql(context, "UPDATE recurring_schedule_versions SET local_wall_clock_seconds = local_wall_clock_seconds + 1 WHERE plan_id = $plan_id AND schedule_revision = $revision;", ("$plan_id", ticket.PlanId), ("$revision", ticket.Specification.ScheduleRevision));
                break;
            case "binding":
                ExecuteSql(context, "DROP TRIGGER trg_recurring_plan_profile_bindings_immutable_update; UPDATE recurring_plan_profile_bindings SET bound_at_utc = bound_at_utc + 1 WHERE plan_id = $plan_id;", ("$plan_id", ticket.PlanId));
                break;
            case "profile":
                ExecuteSql(context, "DROP TRIGGER trg_recurring_fixed_region_profile_versions_immutable_update; UPDATE recurring_fixed_region_profile_versions SET display_bounds_width = display_bounds_width + 1, physical_width = physical_width + 1 WHERE profile_id = $profile_id AND profile_version = $version;", ("$profile_id", ticket.ProfileId), ("$version", ticket.ProfileVersion));
                break;
            case "approval":
                ExecuteSql(context, "DROP TRIGGER trg_recurring_lease_local_approvals_immutable_update; UPDATE recurring_lease_local_approvals SET approved_at_utc = approved_at_utc + 1 WHERE approval_id = $approval_id;", ("$approval_id", ticket.Specification.LocalApprovalId));
                break;
            case "specification":
                ExecuteSql(context, "DROP TRIGGER trg_recurring_occurrence_execution_specs_immutable_update; UPDATE recurring_occurrence_execution_specs SET evaluated_at_utc = evaluated_at_utc + 1 WHERE occurrence_identity = $identity;", ("$identity", ticket.OccurrenceIdentity));
                break;
            case "run":
                ExecuteSql(context, "UPDATE recording_runs SET updated_at_utc = updated_at_utc + 1, version = version + 1 WHERE id = $id;", ("$id", ticket.RunId));
                break;
            case "use_accounting":
                ExecuteSql(context, "UPDATE recurring_lease_uses SET status_code = 'consumed', updated_at_utc = updated_at_utc + 1, version = version + 1 WHERE use_id = $id;", ("$id", ticket.UseId));
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(mutation), mutation, null);
        }

        var decision = new SqliteRecurringLeaseCurrentSafetyValidator(context.Context.Fixture.Store)
            .Validate(ticket, context.Now);

        Assert.Equal(RecurringLeaseCurrentSafetyStatus.StateInvalid, decision.Status);
    }

    [Fact]
    public void RecurringLeaseCurrentSafety_UninitializedStoreAndMissingClaimFailClosed()
    {
        using var context = CreateCommittedContext();
        var ticket = CreateExecutionTicket(context, out _);
        using var temp = new TempDirectory();
        var uninitializedStore = new SqliteOperationalStore(Path.Combine(temp.Path, "uninitialized.db"));

        var uninitialized = new SqliteRecurringLeaseCurrentSafetyValidator(uninitializedStore)
            .Validate(ticket, context.Now);
        Assert.Equal(RecurringLeaseCurrentSafetyStatus.StateInvalid, uninitialized.Status);

        ExecuteSql(
            context,
            "DROP TRIGGER trg_recurring_lease_uses_immutable_delete; DELETE FROM recurring_lease_uses WHERE use_id = $id;",
            ("$id", ticket.UseId));
        var missingUse = new SqliteRecurringLeaseCurrentSafetyValidator(context.Context.Fixture.Store)
            .Validate(ticket, context.Now);
        Assert.Equal(RecurringLeaseCurrentSafetyStatus.StateInvalid, missingUse.Status);
    }

    [Fact]
    public void RecurringLeaseCurrentSafety_WindowAndDurationBoundariesAreFailClosed()
    {
        using var context = CreateCommittedContext();
        var ticket = CreateExecutionTicket(context, out _);
        var validator = new SqliteRecurringLeaseCurrentSafetyValidator(context.Context.Fixture.Store);

        var exactDurationEndStart = ticket.Specification.PlannedEndUtc - ticket.Duration;
        Assert.Equal(
            RecurringLeaseCurrentSafetyStatus.Allowed,
            validator.Validate(ticket, exactDurationEndStart).Status);
        Assert.Equal(
            RecurringLeaseCurrentSafetyStatus.StateInvalid,
            validator.Validate(ticket, ticket.Specification.LatestStartUtc.AddTicks(1)).Status);
        Assert.Equal(
            RecurringLeaseCurrentSafetyStatus.StateInvalid,
            validator.Validate(ticket, ticket.OccurrenceWindowEndUtc).Status);
        Assert.Equal(
            RecurringLeaseCurrentSafetyStatus.StateInvalid,
            validator.Validate(ticket, ticket.LeaseValidUntilUtc).Status);
    }

    [Fact]
    public async Task RecurringLeaseCurrentSafety_SafetyFirstUsesCommittedSqliteStateAndSharedInterlock()
    {
        using var context = CreateCommittedContext();
        var ticket = CreateExecutionTicket(context, out var provider);
        var backend = new BridgeBackend();
        var factoryReady = new ManualResetEventSlim();
        var interlock = new StandingLeaseStartSafetyInterlock();
        var validator = new SqliteRecurringLeaseCurrentSafetyValidator(context.Context.Fixture.Store);
        Task<RecurringLeaseCaptureExecutionResult>? bridgeTask = null;

        var bridge = new RecurringLeaseCaptureExecutionBridge(
            provider,
            _ =>
            {
                factoryReady.Set();
                return backend;
            },
            delayForTest: (_, _) => Task.CompletedTask,
            utcNowForTest: () => context.Now,
            startSafetyInterlockForTest: interlock,
            currentSafetyValidatorForTest: validator.Validate,
            lifecycleSessionFactoryForTest: (_, _) => new TestLifecycleSession());

        interlock.Execute("task270-safety-first", () =>
        {
            var safety = new SqliteStandingLeaseSafetyControlTransaction(context.Context.Fixture.Store);
            var changed = safety.SetUnattendedMode(
                "task270-disable",
                enabled: false,
                "task270-test",
                context.Now.AddSeconds(1));
            Assert.Equal(StandingLeaseSafetyControlResultStatus.Changed, changed.Status);

            bridgeTask = Task.Run(() => bridge.ExecuteAsync(ticket));
            Assert.True(factoryReady.Wait(TimeSpan.FromSeconds(5)));
        });

        var result = await bridgeTask!;

        Assert.Equal(RecurringLeaseCaptureExecutionStatus.Rejected, result.Status);
        Assert.Equal("recurring_execution_unattended_disabled", result.Reason);
        Assert.Equal(0, backend.StartCalls);
        Assert.Equal(1, backend.DisposeCalls);
    }

    [Fact]
    public async Task RecurringLeaseCurrentSafety_StartFirstUsesSameInterlockForLaterSafetyControl()
    {
        using var context = CreateCommittedContext();
        var ticket = CreateExecutionTicket(context, out var provider);
        var backend = new BridgeBackend();
        var interlock = new StandingLeaseStartSafetyInterlock();
        var validator = new SqliteRecurringLeaseCurrentSafetyValidator(context.Context.Fixture.Store);
        var safety = new StandingLeaseSafetyControlService(
            context.Context.Fixture.Store,
            utcNowForTest: () => context.Now.AddSeconds(1),
            auditForTest: (_, _) => { },
            startSafetyInterlock: interlock);
        var safetyCompleted = new ManualResetEventSlim();
        backend.DuringStart = () =>
        {
            backend.SafetyTask = Task.Run(() =>
            {
                safety.DisableUnattended(
                    "task270-later-disable",
                    "task270-test");
                safetyCompleted.Set();
            });
            backend.SafetyWasBlockedDuringStart = !safetyCompleted.Wait(100);
        };

        var bridge = new RecurringLeaseCaptureExecutionBridge(
            provider,
            _ => backend,
            delayForTest: (_, _) => Task.CompletedTask,
            utcNowForTest: () => context.Now,
            startSafetyInterlockForTest: interlock,
            currentSafetyValidatorForTest: validator.Validate,
            lifecycleSessionFactoryForTest: (_, _) => new TestLifecycleSession());

        var result = await bridge.ExecuteAsync(ticket);

        Assert.Equal(RecurringLeaseCaptureExecutionStatus.Started, result.Status);
        Assert.Equal(1, backend.StartCalls);
        Assert.True(backend.SafetyWasBlockedDuringStart);
        Assert.True(backend.SafetyTask!.Wait(TimeSpan.FromSeconds(5)));
        Assert.True(safetyCompleted.IsSet);
    }

    [Fact]
    public void RecurringExecutionBridge_ResultAndMapperAreNotPublicConstructionOrSurface()
    {
        Assert.Empty(typeof(RecurringLeaseCaptureExecutionResult)
            .GetConstructors(BindingFlags.Public | BindingFlags.Instance));
        Assert.Empty(typeof(RecurringLeaseCaptureExecutionResult)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance));
        Assert.Empty(typeof(RecurringLeaseCaptureExecutionBridge)
            .GetConstructors(BindingFlags.Public | BindingFlags.Instance));
        Assert.Empty(typeof(RecurringLeaseCaptureExecutionBridge)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance));
        Assert.False(typeof(RecurringLeaseCaptureExecutionBridge).IsPublic);
        Assert.False(typeof(RecurringLeaseCaptureExecutionResult).IsPublic);

        var stringValidatorType = typeof(Func<
            RecurringLeaseCaptureExecutionTicket,
            DateTimeOffset,
            string>);
        var constructorParameterTypes = typeof(RecurringLeaseCaptureExecutionBridge)
            .GetConstructors(BindingFlags.NonPublic | BindingFlags.Instance)
            .SelectMany(constructor => constructor.GetParameters())
            .Select(parameter => parameter.ParameterType)
            .ToArray();
        Assert.DoesNotContain(stringValidatorType, constructorParameterTypes);
        Assert.DoesNotContain(
            typeof(RecurringLeaseCaptureExecutionBridge)
                .GetMethods(BindingFlags.NonPublic | BindingFlags.Static)
                .Select(method => method.Name),
            name => name == "NormalizeSafetyFailureReason");
        Assert.DoesNotContain(
            typeof(RecurringLeaseCurrentSafetyDecision)
                .GetProperties(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                .Select(property => property.PropertyType),
            propertyType => propertyType == typeof(string));
    }

    [Fact]
    public async Task RecurringProductionBridgeTransfersOwnershipToEngineOnceAndUsesOneCountdown()
    {
        using var context = CreateCommittedContext();
        var provider = new CountingProvider();
        var ticket = CreateExecutionTicket(context, provider);
        var backend = new EngineOwnedRecurringBackend();
        var interlock = new StandingLeaseStartSafetyInterlock();
        var tray = new ProductionBridgeTray();
        var starterCalls = 0;
        var factoryCalls = 0;
        var countdownCalls = 0;
        using var audit = new TemporaryAuditLogger();

        using var engine = new RecordingEngine(
            audit.Logger,
            tracer: null,
            bundleGenerator: null,
            microphoneProvider: null,
            microphoneStatusProvider: null,
            displayTopologyProvider: null,
            systemAudioEndpointProvider: null,
            recurringStartSafetyInterlock: interlock,
            recurringStartSafetyValidator: (_, _) => AllowedSafetyDecision(),
            recurringEnvironmentProvider: provider);
        engine.UtcNowForTests = () => context.Now.UtcDateTime;
        engine.BackendFactory = _ =>
        {
            factoryCalls++;
            return (backend, "ffmpeg-region");
        };

        var bridge = new RecurringLeaseCaptureExecutionBridge(
            provider,
            backendFactoryForTest: null,
            delayForTest: (duration, _) =>
            {
                countdownCalls++;
                Assert.Equal(TimeSpan.FromSeconds(ticket.CountdownSeconds), duration);
                return Task.CompletedTask;
            },
            utcNowForTest: () => context.Now,
            startSafetyInterlockForTest: interlock,
            currentSafetyValidatorForTest: (_, _) => AllowedSafetyDecision(),
            executionStarterForProduction: (claimedTicket, claimedInterlock, cancellationToken) =>
            {
                starterCalls++;
                return Task.FromResult(engine.StartRecurringCapture(
                    claimedTicket,
                    tray,
                    claimedInterlock,
                    exactBackend => new RecurringLeaseCaptureExecutionSession(
                        context.Context.Fixture.Store,
                        claimedTicket,
                        exactBackend,
                        () => context.Now,
                        attachBackendCallbacks: false),
                    cancellationToken));
            });

        var result = await bridge.ExecuteAsync(ticket);

        Assert.True(
            result.Status == RecurringLeaseCaptureExecutionStatus.Started,
            $"reason={result.Reason}; tray_error={tray.LastError}");
        Assert.Same(backend, result.Backend);
        Assert.NotNull(result.LifecycleSession);
        Assert.Equal(1, starterCalls);
        Assert.Equal(1, factoryCalls);
        Assert.Equal(1, backend.StartCalls);
        Assert.Equal(1, countdownCalls);
        Assert.Equal(0, engine.ActiveCountdownOperationCountForTests);
        Assert.Equal(0, tray.CountdownCalls);
        Assert.Equal(1, backend.FirstFrameAddCalls);
        Assert.Equal(1, backend.CaptureEndedAddCalls);
        Assert.Equal(1, backend.NaturalExitRegistrationCalls);
        Assert.Same(context.Proof, backend.Proof);

        backend.RaiseFirstFrame();
        engine.Stop(ticket.RunId, "production_test_stop");

        Assert.Equal(1, backend.StopCalls);
        Assert.Equal(1, backend.DisposeCalls);
    }

    [Fact]
    public async Task RecurringProductionBridgeRoutesEngineCallbacksAndPublishesRegistryViews()
    {
        using var context = CreateCommittedContext();
        var provider = new CountingProvider();
        var ticket = CreateExecutionTicket(context, provider);
        var backend = new EngineOwnedRecurringBackend();
        var interlock = new StandingLeaseStartSafetyInterlock();
        var tray = new ProductionBridgeTray();
        var displayTopology = new MatchingDisplayTopologyProvider(ticket);
        using var audit = new TemporaryAuditLogger();

        using var engine = new RecordingEngine(
            audit.Logger,
            tracer: null,
            bundleGenerator: null,
            microphoneProvider: null,
            microphoneStatusProvider: null,
            displayTopologyProvider: displayTopology,
            systemAudioEndpointProvider: null,
            recurringStartSafetyInterlock: interlock,
            recurringStartSafetyValidator: (_, _) => AllowedSafetyDecision(),
            recurringEnvironmentProvider: provider);
        engine.UtcNowForTests = () => context.Now.UtcDateTime;
        engine.DisableDeadlineWatchdogForTests = true;
        engine.BackendFactory = _ => (backend, "ffmpeg-region");

        var bridge = new RecurringLeaseCaptureExecutionBridge(
            provider,
            backendFactoryForTest: null,
            delayForTest: (_, _) => Task.CompletedTask,
            utcNowForTest: () => context.Now,
            startSafetyInterlockForTest: interlock,
            currentSafetyValidatorForTest: (_, _) => AllowedSafetyDecision(),
            executionStarterForProduction: (claimedTicket, claimedInterlock, cancellationToken) =>
                Task.FromResult(engine.StartRecurringCapture(
                    claimedTicket,
                    tray,
                    claimedInterlock,
                    exactBackend => new RecurringLeaseCaptureExecutionSession(
                        context.Context.Fixture.Store,
                        claimedTicket,
                        exactBackend,
                        () => context.Now,
                        attachBackendCallbacks: false),
                    cancellationToken)));

        var result = await bridge.ExecuteAsync(ticket);

        Assert.Equal(RecurringLeaseCaptureExecutionStatus.Started, result.Status);
        Assert.Single(engine.List());
        using (var status = JsonDocument.Parse(JsonSerializer.Serialize(engine.GetStatus(ticket.RunId))))
        {
            Assert.Equal(ticket.RunId, status.RootElement.GetProperty("recording_id").GetString());
            Assert.Equal("ffmpeg-region", status.RootElement.GetProperty("backend").GetString());
            Assert.Equal(
                ticket.OutputFilePath,
                status.RootElement.GetProperty("output").GetProperty("path").GetString());
        }

        backend.RaiseFirstFrame();
        backend.RaiseCaptureEnded();
        backend.RaiseNaturalExit();

        using (var output = JsonDocument.Parse(JsonSerializer.Serialize(engine.GetOutput(ticket.RunId))))
        {
            Assert.Equal(ticket.RunId, output.RootElement.GetProperty("recording_id").GetString());
            Assert.Equal(
                ticket.OutputFilePath,
                output.RootElement.GetProperty("output").GetProperty("path").GetString());
        }

        var run = new SqliteRecordingRunRepository(context.Context.Fixture.Store).Get(ticket.RunId);
        var use = new SqliteRecurringLeaseUseAccountingReader(context.Context.Fixture.Store)
            .TryGetByOccurrence(ticket.OccurrenceIdentity);
        var occurrence = new SqlitePlanOccurrenceRepository(context.Context.Fixture.Store)
            .Get(ticket.OccurrenceId);

        Assert.Equal(RecordingRunStatus.Settled, run.Status);
        Assert.Equal(LeaseUseStatus.Settled, use!.Status);
        Assert.Equal(PlanOccurrenceStatus.Completed, occurrence.Status);
        Assert.Equal(1, backend.DisposeCalls);
    }

    [Fact]
    public async Task RecurringProductionBridgeSettlesFfmpegQuantizedTailFromProbeEvidence()
    {
        using var context = CreateCommittedContext(
            countdownSeconds: 0,
            recordingDuration: TimeSpan.FromSeconds(10));
        var provider = new CountingProvider();
        var ticket = CreateExecutionTicket(context, provider);
        var backend = new EngineOwnedRecurringBackend();
        var interlock = new StandingLeaseStartSafetyInterlock();
        var tray = new ProductionBridgeTray();
        var displayTopology = new MatchingDisplayTopologyProvider(ticket);
        using var audit = new TemporaryAuditLogger();

        using var engine = new RecordingEngine(
            audit.Logger,
            tracer: null,
            bundleGenerator: null,
            microphoneProvider: null,
            microphoneStatusProvider: null,
            displayTopologyProvider: displayTopology,
            systemAudioEndpointProvider: null,
            recurringStartSafetyInterlock: interlock,
            recurringStartSafetyValidator: (_, _) => AllowedSafetyDecision(),
            recurringEnvironmentProvider: provider);
        engine.UtcNowForTests = () => context.Now.UtcDateTime;
        engine.DisableDeadlineWatchdogForTests = true;
        engine.BackendFactory = _ => (backend, "ffmpeg-region");

        var bridge = new RecurringLeaseCaptureExecutionBridge(
            provider,
            backendFactoryForTest: null,
            delayForTest: (_, _) => Task.CompletedTask,
            utcNowForTest: () => context.Now,
            startSafetyInterlockForTest: interlock,
            currentSafetyValidatorForTest: (_, _) => AllowedSafetyDecision(),
            executionStarterForProduction: (claimedTicket, claimedInterlock, cancellationToken) =>
                Task.FromResult(engine.StartRecurringCapture(
                    claimedTicket,
                    tray,
                    claimedInterlock,
                    exactBackend => new RecurringLeaseCaptureExecutionSession(
                        context.Context.Fixture.Store,
                        claimedTicket,
                        exactBackend,
                        () => context.Now,
                        attachBackendCallbacks: false),
                    cancellationToken)));

        var start = await bridge.ExecuteAsync(ticket);
        Assert.Equal(RecurringLeaseCaptureExecutionStatus.Started, start.Status);
        var configuration = Assert.IsType<CaptureConfig>(backend.Configuration);
        Assert.Equal(10, configuration.DurationSeconds);
        Assert.Equal(ticket.Specification.FrozenOutputFilePath, configuration.OutputPath);

        QuantizedTailTestMedia.Generate(configuration.OutputPath, 299, "179/6");
        var probed = FfmpegCaptureBackend.ProbeAuthorizedFixedRateCapture(configuration.OutputPath, configuration);
        Assert.True(probed.DurationSeconds > 10, $"probe duration was {probed.DurationSeconds:R}s");
        Assert.True(probed.ProbeStreams.Single().StartTimeSeconds < 10);
        Assert.NotNull(probed.QuantizedTailEvidence);

        backend.RaiseFirstFrame();
        backend.RaiseNaturalExit(probed);

        using (var completed = JsonDocument.Parse(JsonSerializer.Serialize(engine.GetStatus(ticket.RunId))))
        {
            Assert.Equal("completed", completed.RootElement.GetProperty("status").GetString());
            Assert.Equal(10, completed.RootElement.GetProperty("config").GetProperty("duration_seconds").GetInt32());
            Assert.True(completed.RootElement.GetProperty("output").GetProperty("duration_seconds").GetDouble() > 10);
        }

        var run = new SqliteRecordingRunRepository(context.Context.Fixture.Store).Get(ticket.RunId);
        var use = new SqliteRecurringLeaseUseAccountingReader(context.Context.Fixture.Store)
            .TryGetByOccurrence(ticket.OccurrenceIdentity);
        var occurrence = new SqlitePlanOccurrenceRepository(context.Context.Fixture.Store)
            .Get(ticket.OccurrenceId);
        Assert.Equal(RecordingRunStatus.Settled, run.Status);
        Assert.Equal(LeaseUseStatus.Settled, use!.Status);
        Assert.Equal(PlanOccurrenceStatus.Completed, occurrence.Status);
        Assert.Equal(TimeSpan.FromSeconds(10), use.ActualSettledDuration);
        Assert.Equal(1, backend.StartCalls);
        Assert.Equal(1, backend.DisposeCalls);
        Assert.Equal(1L, ReadLong(context, "SELECT COUNT(*) FROM plan_occurrences WHERE id = $id;", ("$id", ticket.OccurrenceId)));
        Assert.Equal(1L, ReadLong(context, "SELECT COUNT(*) FROM recording_runs WHERE id = $id;", ("$id", ticket.RunId)));
        Assert.Equal(1L, ReadLong(context, "SELECT COUNT(*) FROM recurring_lease_uses WHERE occurrence_id = $id;", ("$id", ticket.OccurrenceId)));
    }

    [Fact]
    public async Task RecurringProductionBridgeUsesSameInterlockAndFailsClosedOnFinalSafetyRejection()
    {
        using var context = CreateCommittedContext();
        var provider = new CountingProvider();
        var ticket = CreateExecutionTicket(context, provider);
        var backend = new EngineOwnedRecurringBackend();
        var interlock = new StandingLeaseStartSafetyInterlock();
        var tray = new ProductionBridgeTray();
        var safetyDecision = new RecurringLeaseCurrentSafetyDecision(
            RecurringLeaseCurrentSafetyStatus.StopAllActive);
        using var audit = new TemporaryAuditLogger();

        using var engine = new RecordingEngine(
            audit.Logger,
            tracer: null,
            bundleGenerator: null,
            microphoneProvider: null,
            microphoneStatusProvider: null,
            displayTopologyProvider: null,
            systemAudioEndpointProvider: null,
            recurringStartSafetyInterlock: interlock,
            recurringStartSafetyValidator: (_, _) => safetyDecision,
            recurringEnvironmentProvider: provider);
        engine.UtcNowForTests = () => context.Now.UtcDateTime;
        engine.BackendFactory = _ => (backend, "ffmpeg-region");

        var bridge = new RecurringLeaseCaptureExecutionBridge(
            provider,
            backendFactoryForTest: null,
            delayForTest: (_, _) => Task.CompletedTask,
            utcNowForTest: () => context.Now,
            startSafetyInterlockForTest: interlock,
            currentSafetyValidatorForTest: (_, _) => AllowedSafetyDecision(),
            executionStarterForProduction: (claimedTicket, claimedInterlock, cancellationToken) =>
                Task.FromResult(engine.StartRecurringCapture(
                    claimedTicket,
                    tray,
                    claimedInterlock,
                    exactBackend => new RecurringLeaseCaptureExecutionSession(
                        context.Context.Fixture.Store,
                        claimedTicket,
                        exactBackend,
                        () => context.Now,
                        attachBackendCallbacks: false),
                    cancellationToken)));

        var result = await bridge.ExecuteAsync(ticket);

        Assert.Equal(RecurringLeaseCaptureExecutionStatus.Failed, result.Status);
        Assert.Equal("recurring_execution_stop_all_active", result.Reason);
        Assert.Equal(0, backend.StartCalls);
        Assert.Equal(1, backend.DisposeCalls);
        Assert.Equal("recurring_execution_stop_all_active", tray.LastError);
    }

    [Fact]
    public void RecurringEngineRejectsDifferentBridgeInterlockBeforeBackendConstruction()
    {
        using var context = CreateCommittedContext();
        var provider = new CountingProvider();
        var ticket = CreateExecutionTicket(context, provider);
        var backend = new EngineOwnedRecurringBackend();
        var engineInterlock = new StandingLeaseStartSafetyInterlock();
        var bridgeInterlock = new StandingLeaseStartSafetyInterlock();
        var factoryCalls = 0;
        var tray = new ProductionBridgeTray();
        using var audit = new TemporaryAuditLogger();

        using var engine = new RecordingEngine(
            audit.Logger,
            tracer: null,
            bundleGenerator: null,
            microphoneProvider: null,
            microphoneStatusProvider: null,
            displayTopologyProvider: null,
            systemAudioEndpointProvider: null,
            recurringStartSafetyInterlock: engineInterlock,
            recurringStartSafetyValidator: (_, _) => AllowedSafetyDecision(),
            recurringEnvironmentProvider: provider);
        engine.UtcNowForTests = () => context.Now.UtcDateTime;
        engine.BackendFactory = _ =>
        {
            factoryCalls++;
            return (backend, "ffmpeg-region");
        };

        var bridge = new RecurringLeaseCaptureExecutionBridge(
            provider,
            backendFactoryForTest: null,
            delayForTest: (_, _) => Task.CompletedTask,
            utcNowForTest: () => context.Now,
            startSafetyInterlockForTest: bridgeInterlock,
            currentSafetyValidatorForTest: (_, _) => AllowedSafetyDecision(),
            executionStarterForProduction: (claimedTicket, claimedInterlock, cancellationToken) =>
                Task.FromResult(engine.StartRecurringCapture(
                    claimedTicket,
                    tray,
                    claimedInterlock,
                    exactBackend => new RecurringLeaseCaptureExecutionSession(
                        context.Context.Fixture.Store,
                        claimedTicket,
                        exactBackend,
                        () => context.Now,
                        attachBackendCallbacks: false),
                    cancellationToken)));

        var result = bridge.ExecuteAsync(ticket).GetAwaiter().GetResult();

        Assert.Equal(RecurringLeaseCaptureExecutionStatus.Failed, result.Status);
        Assert.Equal("recurring_execution_safety_interlock_mismatch", result.Reason);
        Assert.Equal(0, factoryCalls);
        Assert.Equal(0, backend.StartCalls);
        Assert.Equal(0, backend.DisposeCalls);
    }

    [Fact]
    public void RecurringEngineLifecycleConstructionFailuresAreStableAndCleanedExactlyOnce()
    {
        using var throwingContext = CreateCommittedContext();
        var throwingProvider = new CountingProvider();
        var throwingTicket = CreateExecutionTicket(throwingContext, throwingProvider);
        Assert.True(throwingTicket.TryClaim(out var throwingClaimReason), throwingClaimReason);
        var throwingBackend = new EngineOwnedRecurringBackend();
        var throwingInterlock = new StandingLeaseStartSafetyInterlock();
        var throwingTray = new ProductionBridgeTray();
        using var throwingAudit = new TemporaryAuditLogger();
        using var throwingEngine = new RecordingEngine(
            throwingAudit.Logger,
            tracer: null,
            bundleGenerator: null,
            microphoneProvider: null,
            microphoneStatusProvider: null,
            displayTopologyProvider: null,
            systemAudioEndpointProvider: null,
            recurringStartSafetyInterlock: throwingInterlock,
            recurringStartSafetyValidator: (_, _) => AllowedSafetyDecision(),
            recurringEnvironmentProvider: throwingProvider);
        throwingEngine.UtcNowForTests = () => throwingContext.Now.UtcDateTime;
        throwingEngine.BackendFactory = _ => (throwingBackend, "ffmpeg-region");

        var throwingResult = throwingEngine.StartRecurringCapture(
            throwingTicket,
            throwingTray,
            throwingInterlock,
            _ => throw new InvalidOperationException("lifecycle detail"));

        Assert.Equal(
            "recurring_execution_lifecycle_session_unavailable",
            throwingResult.Reason);
        Assert.Equal(0, throwingBackend.StartCalls);
        Assert.Equal(1, throwingBackend.DisposeCalls);
        Assert.Empty(throwingEngine.List());

        using var attachContext = CreateCommittedContext();
        var attachProvider = new CountingProvider();
        var attachTicket = CreateExecutionTicket(attachContext, attachProvider);
        Assert.True(attachTicket.TryClaim(out var attachClaimReason), attachClaimReason);
        var attachBackend = new EngineOwnedRecurringBackend();
        var attachSession = new TestLifecycleSession { ThrowOnAttach = true };
        var attachInterlock = new StandingLeaseStartSafetyInterlock();
        var attachTray = new ProductionBridgeTray();
        using var attachAudit = new TemporaryAuditLogger();
        using var attachEngine = new RecordingEngine(
            attachAudit.Logger,
            tracer: null,
            bundleGenerator: null,
            microphoneProvider: null,
            microphoneStatusProvider: null,
            displayTopologyProvider: null,
            systemAudioEndpointProvider: null,
            recurringStartSafetyInterlock: attachInterlock,
            recurringStartSafetyValidator: (_, _) => AllowedSafetyDecision(),
            recurringEnvironmentProvider: attachProvider);
        attachEngine.UtcNowForTests = () => attachContext.Now.UtcDateTime;
        attachEngine.BackendFactory = _ => (attachBackend, "ffmpeg-region");

        var attachResult = attachEngine.StartRecurringCapture(
            attachTicket,
            attachTray,
            attachInterlock,
            _ => attachSession);

        Assert.Equal(
            "recurring_execution_lifecycle_session_attach_failed",
            attachResult.Reason);
        Assert.Equal(0, attachBackend.StartCalls);
        Assert.Equal(1, attachBackend.DisposeCalls);
        Assert.Equal(1, attachSession.DisposeCalls);
        Assert.Empty(attachEngine.List());
    }

    [Fact]
    public void RecurringEngineBackendStartFailureIsStableDurablyConvergedAndCleanedOnce()
    {
        using var context = CreateCommittedContext();
        var provider = new CountingProvider();
        var ticket = CreateExecutionTicket(context, provider);
        Assert.True(ticket.TryClaim(out var claimReason), claimReason);
        var backend = new EngineOwnedRecurringBackend { ThrowOnStart = true };
        var interlock = new StandingLeaseStartSafetyInterlock();
        var tray = new ProductionBridgeTray();
        using var audit = new TemporaryAuditLogger();

        using var engine = new RecordingEngine(
            audit.Logger,
            tracer: null,
            bundleGenerator: null,
            microphoneProvider: null,
            microphoneStatusProvider: null,
            displayTopologyProvider: null,
            systemAudioEndpointProvider: null,
            recurringStartSafetyInterlock: interlock,
            recurringStartSafetyValidator: (_, _) => AllowedSafetyDecision(),
            recurringEnvironmentProvider: provider);
        engine.UtcNowForTests = () => context.Now.UtcDateTime;
        engine.BackendFactory = _ => (backend, "ffmpeg-region");

        var result = engine.StartRecurringCapture(
            ticket,
            tray,
            interlock,
            exactBackend => new RecurringLeaseCaptureExecutionSession(
                context.Context.Fixture.Store,
                ticket,
                exactBackend,
                () => context.Now,
                attachBackendCallbacks: false));

        var run = new SqliteRecordingRunRepository(context.Context.Fixture.Store).Get(ticket.RunId);
        var use = new SqliteRecurringLeaseUseAccountingReader(context.Context.Fixture.Store)
            .TryGetByOccurrence(ticket.OccurrenceIdentity);
        var occurrence = new SqlitePlanOccurrenceRepository(context.Context.Fixture.Store)
            .Get(ticket.OccurrenceId);

        Assert.Equal(RecurringLeaseCaptureExecutionStatus.Failed, result.Status);
        Assert.Equal("recurring_execution_backend_start_failed", result.Reason);
        Assert.Equal(1, backend.StartCalls);
        Assert.Equal(1, backend.DisposeCalls);
        Assert.Equal(RecordingRunStatus.StartedUnknown, run.Status);
        Assert.Equal(LeaseUseStatus.StartedUnknown, use!.Status);
        Assert.Equal(PlanOccurrenceStatus.Blocked, occurrence.Status);
        Assert.Empty(engine.List());
    }

    [Fact]
    public void RecurringEngineFinalOutputCollisionPreservesSentinelAndNeverStartsBackend()
    {
        using var context = CreateCommittedContext();
        var authorizationProvider = new CountingProvider();
        var ticket = CreateExecutionTicket(context, authorizationProvider);
        var provider = new CountingProvider
        {
            FrozenFileExistsForCapture = captureNumber => captureNumber >= 2,
        };
        Assert.True(ticket.TryClaim(out var claimReason), claimReason);
        var backend = new EngineOwnedRecurringBackend();
        var interlock = new StandingLeaseStartSafetyInterlock();
        var tray = new ProductionBridgeTray();
        using var audit = new TemporaryAuditLogger();
        var outputDirectory = Path.GetDirectoryName(ticket.OutputFilePath)
            ?? throw new InvalidOperationException("Recurring output directory missing.");
        Directory.CreateDirectory(outputDirectory);
        File.WriteAllText(ticket.OutputFilePath, "task-278r-sentinel");

        try
        {
            using var engine = new RecordingEngine(
                audit.Logger,
                tracer: null,
                bundleGenerator: null,
                microphoneProvider: null,
                microphoneStatusProvider: null,
                displayTopologyProvider: null,
                systemAudioEndpointProvider: null,
                recurringStartSafetyInterlock: interlock,
                recurringStartSafetyValidator: (_, _) => AllowedSafetyDecision(),
                recurringEnvironmentProvider: provider);
            engine.UtcNowForTests = () => context.Now.UtcDateTime;
            engine.BackendFactory = _ => (backend, "ffmpeg-region");

            var result = engine.StartRecurringCapture(
                ticket,
                tray,
                interlock,
                exactBackend => new RecurringLeaseCaptureExecutionSession(
                    context.Context.Fixture.Store,
                    ticket,
                    exactBackend,
                    () => context.Now,
                    attachBackendCallbacks: false));

            Assert.Equal(RecurringLeaseCaptureExecutionStatus.Failed, result.Status);
            Assert.Equal("execution_output_file_exists", result.Reason);
            Assert.Equal(2, provider.CaptureCount);
            Assert.Equal(0, backend.StartCalls);
            Assert.Equal(1, backend.DisposeCalls);
            Assert.Equal("task-278r-sentinel", File.ReadAllText(ticket.OutputFilePath));
        }
        finally
        {
            try
            {
                if (File.Exists(ticket.OutputFilePath))
                    File.Delete(ticket.OutputFilePath);
            }
            catch
            {
            }
        }
    }

    [Fact]
    public async Task RecurringProductionBridgeDoesNotCreateSecondBackendWhenEngineRegistryIsBusy()
    {
        using var firstContext = CreateCommittedContext();
        using var secondContext = CreateCommittedContext();
        var firstProvider = new CountingProvider();
        var secondProvider = new CountingProvider();
        var firstTicket = CreateExecutionTicket(firstContext, firstProvider);
        var secondTicket = CreateExecutionTicket(secondContext, secondProvider);
        var firstBackend = new EngineOwnedRecurringBackend();
        var secondBackend = new EngineOwnedRecurringBackend();
        var interlock = new StandingLeaseStartSafetyInterlock();
        var factoryCalls = 0;
        var backends = new Queue<EngineOwnedRecurringBackend>(new[] { firstBackend, secondBackend });
        var tray = new ProductionBridgeTray();
        using var audit = new TemporaryAuditLogger();

        using var engine = new RecordingEngine(
            audit.Logger,
            tracer: null,
            bundleGenerator: null,
            microphoneProvider: null,
            microphoneStatusProvider: null,
            displayTopologyProvider: null,
            systemAudioEndpointProvider: null,
            recurringStartSafetyInterlock: interlock,
            recurringStartSafetyValidator: (_, _) => AllowedSafetyDecision(),
            recurringEnvironmentProvider: firstProvider);
        engine.UtcNowForTests = () => firstContext.Now.UtcDateTime;
        engine.BackendFactory = _ =>
        {
            factoryCalls++;
            return (backends.Dequeue(), "ffmpeg-region");
        };

        Task<RecurringLeaseCaptureExecutionResult> StartWithEngine(
            RecurringLeaseCaptureExecutionTicket claimedTicket,
            StandingLeaseStartSafetyInterlock claimedInterlock,
            CancellationToken cancellationToken) =>
            Task.FromResult(engine.StartRecurringCapture(
                claimedTicket,
                tray,
                claimedInterlock,
                exactBackend => new RecurringLeaseCaptureExecutionSession(
                    firstContext.Context.Fixture.Store,
                    claimedTicket,
                    exactBackend,
                    () => firstContext.Now,
                    attachBackendCallbacks: false),
                cancellationToken));

        var firstBridge = new RecurringLeaseCaptureExecutionBridge(
            firstProvider,
            backendFactoryForTest: null,
            delayForTest: (_, _) => Task.CompletedTask,
            utcNowForTest: () => firstContext.Now,
            startSafetyInterlockForTest: interlock,
            currentSafetyValidatorForTest: (_, _) => AllowedSafetyDecision(),
            executionStarterForProduction: StartWithEngine);
        var firstResult = await firstBridge.ExecuteAsync(firstTicket);

        var secondBridge = new RecurringLeaseCaptureExecutionBridge(
            secondProvider,
            backendFactoryForTest: null,
            delayForTest: (_, _) => Task.CompletedTask,
            utcNowForTest: () => secondContext.Now,
            startSafetyInterlockForTest: interlock,
            currentSafetyValidatorForTest: (_, _) => AllowedSafetyDecision(),
            executionStarterForProduction: StartWithEngine);
        var secondResult = await secondBridge.ExecuteAsync(secondTicket);

        Assert.True(
            firstResult.Status == RecurringLeaseCaptureExecutionStatus.Started,
            $"reason={firstResult.Reason}; tray_error={tray.LastError}");
        Assert.Equal(RecurringLeaseCaptureExecutionStatus.Rejected, secondResult.Status);
        Assert.Equal("recording_conflict", secondResult.Reason);
        Assert.Equal(1, factoryCalls);
        Assert.Equal(1, firstBackend.StartCalls);
        Assert.Equal(0, secondBackend.StartCalls);

        engine.Stop(firstTicket.RunId, "production_test_stop");
    }

    [Fact]
    public async Task RecurringProductionBridgeCancellationAtFinalGateDoesNotStartBackend()
    {
        using var context = CreateCommittedContext();
        var provider = new CountingProvider();
        var ticket = CreateExecutionTicket(context, provider);
        var backend = new EngineOwnedRecurringBackend();
        var interlock = new StandingLeaseStartSafetyInterlock();
        var cancellation = new CancellationTokenSource();
        var tray = new ProductionBridgeTray();
        using var audit = new TemporaryAuditLogger();

        using var engine = new RecordingEngine(
            audit.Logger,
            tracer: null,
            bundleGenerator: null,
            microphoneProvider: null,
            microphoneStatusProvider: null,
            displayTopologyProvider: null,
            systemAudioEndpointProvider: null,
            recurringStartSafetyInterlock: interlock,
            recurringStartSafetyValidator: (_, _) => AllowedSafetyDecision(),
            recurringEnvironmentProvider: provider);
        engine.UtcNowForTests = () => context.Now.UtcDateTime;
        engine.BackendFactory = _ => (backend, "ffmpeg-region");
        engine.BeforeRecurringBackendFinalGateForTests = _ => cancellation.Cancel();

        var bridge = new RecurringLeaseCaptureExecutionBridge(
            provider,
            backendFactoryForTest: null,
            delayForTest: (_, _) => Task.CompletedTask,
            utcNowForTest: () => context.Now,
            startSafetyInterlockForTest: interlock,
            currentSafetyValidatorForTest: (_, _) => AllowedSafetyDecision(),
            executionStarterForProduction: (claimedTicket, claimedInterlock, cancellationToken) =>
                Task.FromResult(engine.StartRecurringCapture(
                    claimedTicket,
                    tray,
                    claimedInterlock,
                    exactBackend => new RecurringLeaseCaptureExecutionSession(
                        context.Context.Fixture.Store,
                        claimedTicket,
                        exactBackend,
                        () => context.Now,
                        attachBackendCallbacks: false),
                    cancellationToken)));

        var result = await bridge.ExecuteAsync(ticket, cancellation.Token);

        Assert.Equal(RecurringLeaseCaptureExecutionStatus.Cancelled, result.Status);
        Assert.Equal("recurring_execution_cancelled", result.Reason);
        Assert.Equal(0, backend.StartCalls);
        Assert.Equal(1, backend.DisposeCalls);
    }

    [Fact]
    public async Task RecurringProductionBridgeStopAllUsesLifecycleOwnerForPhysicalStop()
    {
        using var context = CreateCommittedContext();
        var provider = new CountingProvider();
        var ticket = CreateExecutionTicket(context, provider);
        var backend = new EngineOwnedRecurringBackend();
        var interlock = new StandingLeaseStartSafetyInterlock();
        var tray = new ProductionBridgeTray();
        using var audit = new TemporaryAuditLogger();

        using var engine = new RecordingEngine(
            audit.Logger,
            tracer: null,
            bundleGenerator: null,
            microphoneProvider: null,
            microphoneStatusProvider: null,
            displayTopologyProvider: null,
            systemAudioEndpointProvider: null,
            recurringStartSafetyInterlock: interlock,
            recurringStartSafetyValidator: (_, _) => AllowedSafetyDecision(),
            recurringEnvironmentProvider: provider);
        engine.UtcNowForTests = () => context.Now.UtcDateTime;
        engine.BackendFactory = _ => (backend, "ffmpeg-region");

        var bridge = new RecurringLeaseCaptureExecutionBridge(
            provider,
            backendFactoryForTest: null,
            delayForTest: (_, _) => Task.CompletedTask,
            utcNowForTest: () => context.Now,
            startSafetyInterlockForTest: interlock,
            currentSafetyValidatorForTest: (_, _) => AllowedSafetyDecision(),
            executionStarterForProduction: (claimedTicket, claimedInterlock, cancellationToken) =>
                Task.FromResult(engine.StartRecurringCapture(
                    claimedTicket,
                    tray,
                    claimedInterlock,
                    exactBackend => new RecurringLeaseCaptureExecutionSession(
                        context.Context.Fixture.Store,
                        claimedTicket,
                        exactBackend,
                        () => context.Now,
                        attachBackendCallbacks: false),
                    cancellationToken)));

        var result = await bridge.ExecuteAsync(ticket);
        Assert.Equal(RecurringLeaseCaptureExecutionStatus.Started, result.Status);

        engine.StopAllSync("recurring_lease_safety_control");

        Assert.Equal(1, backend.StartCalls);
        Assert.Equal(1, backend.StopCalls);
        Assert.Equal(1, backend.DisposeCalls);
    }

    [Fact]
    public async Task RecurringProductionBridgeWithoutStarterRemainsFailClosed()
    {
        using var context = CreateCommittedContext();
        var provider = new CountingProvider();
        var ticket = CreateExecutionTicket(context, provider);
        var bridge = new RecurringLeaseCaptureExecutionBridge(
            provider,
            backendFactoryForTest: null,
            delayForTest: (_, _) => Task.CompletedTask,
            utcNowForTest: () => context.Now,
            startSafetyInterlockForTest: new StandingLeaseStartSafetyInterlock(),
            currentSafetyValidatorForTest: (_, _) => AllowedSafetyDecision());

        var result = await bridge.ExecuteAsync(ticket);

        Assert.Equal(RecurringLeaseCaptureExecutionStatus.Failed, result.Status);
        Assert.Equal("recurring_execution_backend_unavailable", result.Reason);
        Assert.Null(result.Backend);
    }

    private static RecurringLeaseCurrentSafetyDecision AllowedSafetyDecision() =>
        new(RecurringLeaseCurrentSafetyStatus.Allowed);

    private static RecurringLeaseCaptureExecutionTicket CreateExecutionTicket(
        CommittedContext context,
        out CountingProvider provider)
    {
        provider = new CountingProvider();
        return CreateExecutionTicket(context, provider);
    }

    private static RecurringLeaseCaptureExecutionTicket CreateExecutionTicket(
        CommittedContext context,
        CountingProvider provider)
    {
        var succeeded = CreateLoader(context, provider).TryAuthorizeAndConsumeRecurringLeaseUse(
            context.Proof,
            out var authorization,
            out var authorizationReason);
        Assert.True(succeeded, authorizationReason);
        Assert.NotNull(authorization);
        Assert.True(RecurringLeaseCaptureExecutionTicket.TryCreate(
            authorization,
            out var ticket,
            out var ticketReason), ticketReason);
        return Assert.IsType<RecurringLeaseCaptureExecutionTicket>(ticket);
    }

    private static RecurringLeaseExecutionSnapshot BuildSnapshot(CommittedContext context)
    {
        var receipt = Assert.IsType<RecurringStartCommitReceipt>(context.Result.FirstCommitReceipt);
        return new RecurringLeaseExecutionSnapshot(
            receipt.Plan,
            receipt.Lease,
            context.Context.Fixture.Setup.ExactProfile,
            receipt.Approval,
            new RecurringLeaseExecutionSafetyEvidence(
                UnattendedModeStatus.Enabled,
                StopAllApplied: false,
                StopAllAppliedAtUtc: null,
                UnattendedEnabledAtUtc: context.Context.Fixture.CreatedAt.AddMinutes(-1),
                Version: 1),
            receipt.Occurrence,
            receipt.Specification,
            receipt.Run,
            receipt.Use,
            receipt.LeaseEntries,
            receipt.Quota);
    }

    private static CommittedContext CreateCommittedContext(
        int maxUses = 10,
        TimeSpan? startCommitOffset = null,
        int countdownSeconds = 3,
        TimeSpan? recordingDuration = null)
    {
        var commitOffset = startCommitOffset ?? TimeSpan.Zero;
        var context = RecurringOccurrenceReservationTests.ReservationContext.Create(
            maxUses: maxUses,
            approvalUserSid: "S-1-5-21-task267",
            approvalSessionBinding: CaptureAuthorizationSessionBinding.Current,
            latestStartGrace: commitOffset > TimeSpan.Zero ? TimeSpan.FromSeconds(10) : null,
            countdownSeconds: countdownSeconds,
            recordingDuration: recordingDuration);
        var startCommitAt = context.Fixture.CreatedAt + commitOffset;
        var reservation = new RecurringOccurrenceReservationService(
                context.Fixture.Store,
                () => context.Fixture.CreatedAt,
                () => "task267-run",
                () => "task267-use")
            .Reserve(context.Lease.LeaseId, context.Slot.OccurrenceIdentity);
        Assert.Equal(RecurringOccurrenceReservationStatus.Reserved, reservation.Status);
        var result = new RecurringOccurrenceStartCommitService(
                context.Fixture.Store,
                () => startCommitAt,
                new RecurringOccurrenceReservationTests.MatchingReservationEnvironmentProvider())
            .Commit(context.Lease.LeaseId, context.Slot.OccurrenceIdentity);
        Assert.True(result.FirstCommitProof is not null,
            $"start-commit status={result.Status}; reason={result.ReasonCode}; occurrence={result.OccurrenceIdentity}");
        var proof = Assert.IsType<RecurringLeaseUseProof>(result.FirstCommitProof);
        return new CommittedContext(context, result, proof, startCommitAt);
    }

    private static void DisableAndReenableAfterProof(CommittedContext context, string prefix)
    {
        var safety = new SqliteStandingLeaseSafetyControlTransaction(context.Context.Fixture.Store);
        safety.SetUnattendedMode(
            prefix + "-disable",
            false,
            "task267r-test",
            context.Now.AddSeconds(1));
        safety.SetUnattendedMode(
            prefix + "-reenable",
            true,
            "task267r-test",
            context.Now.AddSeconds(2));
    }

    private static void SetGlobalSafetyState(
        CommittedContext context,
        string modeCode,
        DateTimeOffset modeChangedAtUtc,
        DateTimeOffset? unattendedEnabledAtUtc,
        bool stopAllApplied,
        DateTimeOffset? stopAllAtUtc)
    {
        ExecuteSql(
            context,
            """
            UPDATE unattended_safety_state
            SET unattended_mode_code = $mode,
                mode_changed_at_utc = $mode_changed,
                unattended_enabled_at_utc = $enabled_at,
                stop_all_applied = $stop_all_applied,
                stop_all_operation_id = $stop_operation_id,
                stop_all_reason_code = $stop_reason_code,
                stop_all_requested_at_utc = $stop_requested_at,
                stop_all_applied_at_utc = $stop_applied_at,
                version = version + 1
            WHERE state_id = 'global';
            """,
            ("$mode", modeCode),
            ("$mode_changed", modeChangedAtUtc.UtcDateTime.Ticks),
            ("$enabled_at", unattendedEnabledAtUtc?.UtcDateTime.Ticks),
            ("$stop_all_applied", stopAllApplied ? 1L : 0L),
            ("$stop_operation_id", stopAllApplied ? "task270-stop" : null),
            ("$stop_reason_code", stopAllApplied ? "task270-test" : null),
            ("$stop_requested_at", stopAllApplied ? stopAllAtUtc?.UtcDateTime.Ticks : null),
            ("$stop_applied_at", stopAllApplied ? stopAllAtUtc?.UtcDateTime.Ticks : null));
    }

    private static long ReadLong(
        CommittedContext context,
        string sql,
        params (string Name, object? Value)[] parameters)
    {
        using var connection = context.Context.Fixture.Store.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
            command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        return Convert.ToInt64(command.ExecuteScalar());
    }

    private static void ExecuteSql(
        CommittedContext context,
        string sql,
        params (string Name, object? Value)[] parameters)
    {
        using var connection = context.Context.Fixture.Store.OpenConnection();
        using var transaction = connection.BeginTransaction();
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
            command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        command.ExecuteNonQuery();
        transaction.Commit();
    }

    private static SqliteRecurringLeaseExecutionSnapshotLoader CreateLoader(
        CommittedContext context,
        IRecurringOccurrenceEnvironmentProvider provider) =>
        new(
            context.Context.Fixture.Store,
            snapshotLoadedBeforeEnvironmentForTest: null,
            beforeCommitForTest: null,
            environmentProviderForTest: provider,
            trustedUtcClockForTest: () => context.Now);

    private sealed record CommittedContext(
        RecurringOccurrenceReservationTests.ReservationContext Context,
        RecurringOccurrenceStartCommitResult Result,
        RecurringLeaseUseProof Proof,
        DateTimeOffset Now) : IDisposable
    {
        public void Dispose() => Context.Dispose();
    }

    private sealed class CountingProvider : IRecurringOccurrenceEnvironmentProvider
    {
        private readonly long _timeOffsetTicks;
        private int _captureCount;

        internal CountingProvider(long timeOffsetTicks = 0)
        {
            _timeOffsetTicks = timeOffsetTicks;
        }

        internal int CaptureCount => _captureCount;

        internal Func<int, bool>? FrozenFileExistsForCapture { get; set; }

        internal RecurringOccurrenceEnvironmentCaptureRequest? LastRequest { get; private set; }

        public StandingLeaseExecutionEnvironment Capture(RecurringOccurrenceEnvironmentCaptureRequest request)
        {
            var captureNumber = Interlocked.Increment(ref _captureCount);
            LastRequest = request;
            var requirements = request.Requirements;
            var requiredFreeBytes = RecordingPreflightChecker.RequiredFreeSpaceBytes(requirements.ReservedDuration);
            return new StandingLeaseExecutionEnvironment(
                request.TrustedNowUtc.AddTicks(_timeOffsetTicks),
                requirements.CurrentUserSid,
                requirements.SessionBinding,
                isInteractiveDesktop: true,
                displays: new[]
                {
                    new StandingLeaseDisplayMetadata(
                        "task267-display",
                        requirements.StableDisplayFingerprint,
                        DisplayIdentityResolutionStatus.Resolved,
                        requirements.DisplayBounds,
                        requirements.DpiX,
                        requirements.DpiY,
                        requirements.PhysicalWidth,
                        requirements.PhysicalHeight,
                        requirements.Orientation),
                },
                requirements.TopologyDigest,
                new StandingLeaseOutputFileSystemSnapshot(
                    requirements.NormalizedOutputDirectory,
                    requirements.FrozenOutputFilePath,
                    directoryExists: true,
                    frozenFileExists: FrozenFileExistsForCapture?.Invoke(captureNumber) ?? false,
                    directoryWritable: true,
                    freeSpaceAvailable: true,
                    availableFreeBytes: requiredFreeBytes,
                    requiredFreeBytes));
        }
    }

    private sealed class CancellingProvider : IRecurringOccurrenceEnvironmentProvider
    {
        private readonly CountingProvider _inner;
        private readonly CancellationTokenSource _cancellation;

        internal CancellingProvider(CountingProvider inner, CancellationTokenSource cancellation)
        {
            _inner = inner;
            _cancellation = cancellation;
        }

        public StandingLeaseExecutionEnvironment Capture(RecurringOccurrenceEnvironmentCaptureRequest request)
        {
            var environment = _inner.Capture(request);
            _cancellation.Cancel();
            return environment;
        }
    }

    private sealed class TestLifecycleSession :
        IRecurringLeaseCaptureLifecycleSession,
        IRecurringLeaseCaptureLifecycleDriver
    {
        internal bool AttachSucceeds { get; set; } = true;
        internal bool ThrowOnAttach { get; set; }
        internal bool ThrowOnStartFailed { get; set; }
        internal bool ThrowOnDispose { get; set; }
        internal RecurringLeaseCaptureLifecycleHandoffStatus HandoffStatus { get; set; } =
            RecurringLeaseCaptureLifecycleHandoffStatus.Active;
        internal ICaptureBackend? AttachedBackend { get; private set; }
        internal int AttachCalls { get; private set; }
        internal int StartFailedCalls { get; private set; }
        internal int FailBeforeStartCalls { get; private set; }
        internal int DisposeCalls { get; private set; }
        internal string? LastBeforeStartReason { get; private set; }

        public bool TryAttach(ICaptureBackend backend, out string failureReason)
        {
            AttachCalls++;
            if (ThrowOnAttach)
                throw new InvalidOperationException("attach detail");
            if (!AttachSucceeds)
            {
                failureReason = "attach detail";
                return false;
            }

            AttachedBackend = backend;
            failureReason = "";
            return true;
        }

        public RecurringLeaseCaptureLifecycleHandoffResult CompleteStartHandoff() =>
            new(HandoffStatus);

        public RecurringLeaseLifecycleActionResult StartFailed()
        {
            StartFailedCalls++;
            if (ThrowOnStartFailed)
                throw new InvalidOperationException("start-failed detail");
            return RecurringLeaseLifecycleActionResult.Applied(
                "backend_start_failed_before_first_frame",
                terminal: true);
        }

        public RecurringLeaseLifecycleActionResult Stop() =>
            RecurringLeaseLifecycleActionResult.Applied("session_interrupted", terminal: true);

        public RecurringLeaseLifecycleActionResult ObserveFirstFrame(FirstFrameObservation observation) =>
            RecurringLeaseLifecycleActionResult.Applied("first_frame_observed");

        public RecurringLeaseLifecycleActionResult ObserveCaptureEnded(CaptureEndedObservation observation) =>
            RecurringLeaseLifecycleActionResult.Applied("capture_ended");

        public RecurringLeaseLifecycleActionResult ObserveNaturalExit(int exitCode, OutputMeta meta) =>
            RecurringLeaseLifecycleActionResult.Applied("media_settled", terminal: true);

        public RecurringLeaseLifecycleActionResult FailBeforeStart(string reason)
        {
            FailBeforeStartCalls++;
            LastBeforeStartReason = reason;
            return RecurringLeaseLifecycleActionResult.Applied(
                "session_interrupted_before_first_frame",
                terminal: true);
        }

        public RecurringLeaseCaptureStopResult StopForEngine(string? reason = null) =>
            new(Stop(), null, -1);

        public void Dispose()
        {
            DisposeCalls++;
            if (ThrowOnDispose)
                throw new InvalidOperationException("dispose detail");
        }
    }

    private sealed class BridgeObservableBackend :
        ICaptureBackend,
        IFirstFrameObservableCaptureBackend,
        ICaptureEndedObservableBackend
    {
        private Action<FirstFrameObservation>? _firstFrameObserved;
        private Action<CaptureEndedObservation>? _captureEnded;
        private Action<int, OutputMeta>? _naturalExit;

        internal bool RaiseFirstFrameDuringStart { get; set; }
        internal bool RaiseNaturalExitDuringStart { get; set; }
        internal bool ThrowOnStart { get; set; }
        internal int StartCalls { get; private set; }
        internal int StopCalls { get; private set; }
        internal int DisposeCalls { get; private set; }

        public event Action<FirstFrameObservation>? FirstFrameObserved
        {
            add => _firstFrameObserved += value;
            remove => _firstFrameObserved -= value;
        }

        public event Action<CaptureEndedObservation>? CaptureEnded
        {
            add => _captureEnded += value;
            remove => _captureEnded -= value;
        }

        public void Start(CaptureConfig cfg, CaptureAuthorizationProof authorizationProof)
        {
            StartCalls++;
            authorizationProof.RequireConsumed();
            if (RaiseFirstFrameDuringStart)
            {
                _firstFrameObserved?.Invoke(new FirstFrameObservation
                {
                    FrameNumber = 1,
                    TotalSizeBytes = 1024,
                    OutTimeUs = 1,
                });
            }

            if (RaiseNaturalExitDuringStart)
                _naturalExit?.Invoke(0, ValidMetaForBridge(cfg.OutputPath));

            if (ThrowOnStart)
                throw new InvalidOperationException("backend start detail");
        }

        public OutputMeta Stop()
        {
            StopCalls++;
            return ValidMetaForBridge(null);
        }

        public void OnNaturalExit(Action<int, OutputMeta> callback) => _naturalExit += callback;

        internal void RaiseNaturalExit(int exitCode, OutputMeta meta) => _naturalExit?.Invoke(exitCode, meta);

        public void Dispose() => DisposeCalls++;
    }

    private sealed class EngineOwnedRecurringBackend :
        ICaptureBackend,
        IFirstFrameObservableCaptureBackend,
        ICaptureEndedObservableBackend
    {
        private Action<FirstFrameObservation>? _firstFrameObserved;
        private Action<CaptureEndedObservation>? _captureEnded;
        private Action<int, OutputMeta>? _naturalExit;

        internal CaptureConfig? Configuration { get; private set; }
        internal CaptureAuthorizationProof? Proof { get; private set; }
        internal bool ThrowOnStart { get; set; }
        internal int StartCalls { get; private set; }
        internal int StopCalls { get; private set; }
        internal int DisposeCalls { get; private set; }
        internal int FirstFrameAddCalls { get; private set; }
        internal int CaptureEndedAddCalls { get; private set; }
        internal int NaturalExitRegistrationCalls { get; private set; }

        public event Action<FirstFrameObservation>? FirstFrameObserved
        {
            add
            {
                FirstFrameAddCalls++;
                _firstFrameObserved += value;
            }
            remove => _firstFrameObserved -= value;
        }

        public event Action<CaptureEndedObservation>? CaptureEnded
        {
            add
            {
                CaptureEndedAddCalls++;
                _captureEnded += value;
            }
            remove => _captureEnded -= value;
        }

        public void Start(CaptureConfig cfg, CaptureAuthorizationProof authorizationProof)
        {
            StartCalls++;
            Configuration = cfg;
            Proof = authorizationProof;
            authorizationProof.RequireConsumed();
            if (ThrowOnStart)
                throw new InvalidOperationException("backend start detail");
        }

        public OutputMeta Stop()
        {
            StopCalls++;
            return ValidMetaForBridge(Configuration?.OutputPath);
        }

        public void OnNaturalExit(Action<int, OutputMeta> callback)
        {
            NaturalExitRegistrationCalls++;
            _naturalExit += callback;
        }

        public void Dispose() => DisposeCalls++;

        internal void RaiseFirstFrame() => _firstFrameObserved?.Invoke(new FirstFrameObservation
        {
            FrameNumber = 1,
            TotalSizeBytes = 1024,
            OutTimeUs = 1,
        });

        internal void RaiseCaptureEnded() => _captureEnded?.Invoke(new CaptureEndedObservation
        {
            ExitCode = 0,
            Reason = "manual",
        });

        internal void RaiseNaturalExit() => RaiseNaturalExit(ValidMetaForBridge(Configuration?.OutputPath));

        internal void RaiseNaturalExit(OutputMeta meta) => _naturalExit?.Invoke(0, meta);
    }

    private sealed class ProductionBridgeTray : ITrayContext
    {
        internal int CountdownCalls { get; private set; }
        internal string? LastError { get; private set; }

        public string HostMode => "test";
        public bool SupportsRegionSelectionUi => false;

        public void RequestConfirmation(
            RecordingConfirmationPresentation presentation,
            Action<ConfirmationDecision> callback) =>
            throw new InvalidOperationException("Recurring production bridge must not request confirmation.");

        public void RequestRegionSelection(
            int timeoutSeconds,
            Action<string, int, int, int, int, string, string> callback) =>
            throw new InvalidOperationException("Recurring production bridge must not request region selection.");

        public void SetRecording(RecordingUiPresentation presentation) { }
        public void SetIdle(RecordingUiPresentation presentation) { }
        public void SetAllIdle() { }
        public void ShowError(string text) => LastError = text;
        public void SetCountdown(RecordingUiPresentation presentation) => CountdownCalls++;
    }

    private sealed class MatchingDisplayTopologyProvider : IDisplayTopologyProvider
    {
        private readonly IReadOnlyList<DisplayTopologySnapshot> _displays;

        internal MatchingDisplayTopologyProvider(RecurringLeaseCaptureExecutionTicket ticket)
        {
            var bounds = ticket.Specification.DisplayBounds;
            _displays = new[]
            {
                new DisplayTopologySnapshot(
                    "task278-display",
                    ticket.Specification.StableDisplayFingerprint,
                    DisplayIdentityResolutionStatus.Resolved,
                    new CapturePlanBounds(bounds.X, bounds.Y, bounds.Width, bounds.Height))
            };
        }

        public IReadOnlyList<DisplayTopologySnapshot> GetCurrentDisplays() => _displays;
    }

    private sealed class TemporaryAuditLogger : IDisposable
    {
        private readonly string _directory = Path.Combine(
            Path.GetTempPath(),
            "agent-recorder-task278-audit-" + Guid.NewGuid().ToString("N"));

        internal TemporaryAuditLogger()
        {
            Logger = new AuditLogger(Path.Combine(_directory, "audit.jsonl"));
        }

        internal AuditLogger Logger { get; }

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(_directory))
                    Directory.Delete(_directory, recursive: true);
            }
            catch
            {
            }
        }
    }

    private sealed class CancelAfterAttachSession :
        IRecurringLeaseCaptureLifecycleSession,
        IRecurringLeaseCaptureLifecycleDriver
    {
        private readonly IRecurringLeaseCaptureLifecycleSession _session;
        private readonly IRecurringLeaseCaptureLifecycleDriver _driver;
        private readonly CancellationTokenSource _cancellation;

        internal CancelAfterAttachSession(
            IRecurringLeaseCaptureLifecycleSession session,
            CancellationTokenSource cancellation)
        {
            _session = session;
            _driver = (IRecurringLeaseCaptureLifecycleDriver)session;
            _cancellation = cancellation;
        }

        public bool TryAttach(ICaptureBackend backend, out string failureReason)
        {
            var attached = _session.TryAttach(backend, out failureReason);
            if (attached)
                _cancellation.Cancel();
            return attached;
        }

        public RecurringLeaseCaptureLifecycleHandoffResult CompleteStartHandoff() =>
            _session.CompleteStartHandoff();

        public RecurringLeaseLifecycleActionResult StartFailed() => _session.StartFailed();

        public RecurringLeaseLifecycleActionResult Stop() => _session.Stop();

        public RecurringLeaseLifecycleActionResult ObserveFirstFrame(FirstFrameObservation observation) =>
            _driver.ObserveFirstFrame(observation);

        public RecurringLeaseLifecycleActionResult ObserveCaptureEnded(CaptureEndedObservation observation) =>
            _driver.ObserveCaptureEnded(observation);

        public RecurringLeaseLifecycleActionResult ObserveNaturalExit(int exitCode, OutputMeta meta) =>
            _driver.ObserveNaturalExit(exitCode, meta);

        public RecurringLeaseLifecycleActionResult FailBeforeStart(string reason) =>
            _driver.FailBeforeStart(reason);

        public RecurringLeaseCaptureStopResult StopForEngine(string? reason = null) =>
            _driver.StopForEngine(reason);

        public void Dispose() => _session.Dispose();
    }

    private static OutputMeta ValidMetaForBridge(string? outputPath) => new()
    {
        SizeBytes = 1024,
        OutputFileExists = true,
        DurationSeconds = 1,
        OutputPath = outputPath,
        StopReason = "natural",
    };

    private sealed class BridgeBackend : ICaptureBackend
    {
        internal int StartCalls;
        internal int DisposeCalls;
        internal bool ThrowOnStart;
        internal CaptureAuthorizationProof? Proof;
        internal CaptureConfig? Configuration;
        internal Action? DuringStart;
        internal Task? SafetyTask;
        internal bool SafetyWasBlockedDuringStart;

        public void Start(CaptureConfig cfg, CaptureAuthorizationProof authorizationProof)
        {
            StartCalls++;
            Configuration = cfg;
            Proof = authorizationProof;
            authorizationProof.RequireConsumed();
            DuringStart?.Invoke();
            if (ThrowOnStart)
                throw new InvalidOperationException("backend start detail");
        }

        public OutputMeta Stop() => new();

        public void OnNaturalExit(Action<int, OutputMeta> callback)
        {
        }

        public void Dispose() => DisposeCalls++;
    }

    private sealed class CountingSafetyStopper : IStandingLeaseActiveRunStopper
    {
        private int _count;

        internal int Count => _count;

        public void StopAll(string reason) => Interlocked.Increment(ref _count);
    }
}
