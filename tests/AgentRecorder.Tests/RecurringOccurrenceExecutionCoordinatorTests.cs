using System.Reflection;
using AgentRecorder.Capture;
using AgentRecorder.Core;
using AgentRecorder.Core.Automation;
using AgentRecorder.Infrastructure;
using AgentRecorder.Persistence;
using AgentRecorder.Windows;
using Microsoft.Data.Sqlite;
using Xunit;

namespace AgentRecorder.Tests;

[Collection("NonParallel-AgentRecorderDataDir")]
public sealed class RecurringOccurrenceExecutionCoordinatorTests
{
    [Fact]
    public async Task FreshCandidateRunsTheCompletePipelineAndReturnsTheExactLifecycleOwner()
    {
        using var context = RecurringOccurrenceReservationTests.ReservationContext.Create(
            targetStatus: "scheduled",
            countdownSeconds: 0,
            approvalUserSid: "S-1-5-21-task276",
            approvalSessionBinding: CaptureAuthorizationSessionBinding.Current);
        var now = context.Slot.ScheduledStartUtc!.Value;
        var backend = new CoordinatorBackend();
        var environmentProvider = new CountingEnvironmentProvider(
            new RecurringOccurrenceReservationTests.MatchingReservationEnvironmentProvider());
        var stages = new List<RecurringOccurrenceExecutionStage>();
        IRecurringLeaseCaptureLifecycleSession? exactSession = null;
        var occurrenceBefore = new SqlitePlanOccurrenceRepository(context.Fixture.Store)
            .Get(context.Slot.OccurrenceId!);
        Assert.Equal(PlanOccurrenceStatus.Scheduled, occurrenceBefore.Status);
        Assert.Equal(0L, Convert.ToInt64(context.Fixture.Scalar(
            "SELECT COUNT(*) FROM recurring_occurrence_execution_specs WHERE occurrence_identity = $identity;",
            ("$identity", context.Slot.OccurrenceIdentity))));
        Assert.Equal(0L, Convert.ToInt64(context.Fixture.Scalar("SELECT COUNT(*) FROM recording_runs;")));
        Assert.Equal(0L, Convert.ToInt64(context.Fixture.Scalar("SELECT COUNT(*) FROM recurring_lease_uses;")));
        var coordinator = CreateCoordinator(context, now, backend,
            environmentProvider: environmentProvider,
            stageHook: stage =>
            {
                stages.Add(stage);
                if (stage == RecurringOccurrenceExecutionStage.AfterDueProjection)
                {
                    Assert.Equal(PlanOccurrenceStatus.Due,
                        new SqlitePlanOccurrenceRepository(context.Fixture.Store)
                            .Get(context.Slot.OccurrenceId!).Status);
                }
            },
            lifecycleSessionFactory: (ticket, captureBackend) =>
            {
                exactSession = new RecurringLeaseCaptureExecutionSession(
                    context.Fixture.Store,
                    ticket,
                    captureBackend,
                    () => now);
                return exactSession;
            });

        var result = await coordinator.ExecuteAsync(CreateRequest(context));

        Assert.Equal(RecurringOccurrenceExecutionStatus.Started, result.Status);
        Assert.Same(exactSession, result.LifecycleSession);
        Assert.Equal(
            new[]
            {
                RecurringOccurrenceExecutionStage.AfterDueProjection,
                RecurringOccurrenceExecutionStage.AfterReservation,
                RecurringOccurrenceExecutionStage.AfterStartCommit,
                RecurringOccurrenceExecutionStage.AfterAuthorization,
                RecurringOccurrenceExecutionStage.AfterTicket,
                RecurringOccurrenceExecutionStage.BeforeBridge,
            },
            stages);
        Assert.Equal(4, environmentProvider.CaptureCount);
        Assert.Equal(1L, Convert.ToInt64(context.Fixture.Scalar(
            "SELECT COUNT(*) FROM recurring_occurrence_execution_specs WHERE occurrence_identity = $identity;",
            ("$identity", context.Slot.OccurrenceIdentity))));
        Assert.Equal(1, backend.StartCalls);
        Assert.Equal(context.Fixture.PlanId, result.PlanId);
        Assert.Equal(context.Lease.LeaseId, result.LeaseId);
        Assert.NotNull(result.RunId);
        Assert.NotNull(result.UseId);

        var run = new SqliteRecordingRunRepository(context.Fixture.Store).Get(result.RunId!);
        var use = new SqliteRecurringLeaseUseAccountingReader(context.Fixture.Store)
            .TryGetByOccurrence(context.Slot.OccurrenceIdentity);
        Assert.Equal(RecordingRunStatus.StartCommitted, run.Status);
        Assert.Equal(LeaseUseStatus.StartCommitted, use!.Status);
        Assert.Equal(PlanOccurrenceStatus.RunCreated,
            new SqlitePlanOccurrenceRepository(context.Fixture.Store).Get(context.Slot.OccurrenceId!).Status);
        Assert.Equal(1L, Convert.ToInt64(context.Fixture.Scalar("SELECT COUNT(*) FROM recording_runs;")));
        Assert.Equal(1L, Convert.ToInt64(context.Fixture.Scalar("SELECT COUNT(*) FROM recurring_lease_uses;")));

        result.LifecycleSession!.Dispose();

        Assert.Equal(RecordingRunStatus.StartedUnknown,
            new SqliteRecordingRunRepository(context.Fixture.Store).Get(result.RunId!).Status);
        Assert.Equal(LeaseUseStatus.StartedUnknown,
            new SqliteRecurringLeaseUseAccountingReader(context.Fixture.Store)
                .TryGetByOccurrence(context.Slot.OccurrenceIdentity)!.Status);
        Assert.Equal(PlanOccurrenceStatus.Blocked,
            new SqlitePlanOccurrenceRepository(context.Fixture.Store).Get(context.Slot.OccurrenceId!).Status);
    }

    [Fact]
    public async Task AlreadyDueOccurrenceContinuesFromDueAndStartsExactlyOnce()
    {
        using var context = RecurringOccurrenceReservationTests.ReservationContext.Create(
            targetStatus: "due",
            countdownSeconds: 0,
            approvalUserSid: "S-1-5-21-task276",
            approvalSessionBinding: CaptureAuthorizationSessionBinding.Current);
        var backend = new CoordinatorBackend();
        var stages = new List<RecurringOccurrenceExecutionStage>();

        var result = await CreateCoordinator(
            context,
            context.Slot.ScheduledStartUtc!.Value,
            backend,
            stageHook: stages.Add)
            .ExecuteAsync(CreateRequest(context));

        Assert.Equal(RecurringOccurrenceExecutionStatus.Started, result.Status);
        Assert.Equal(1, backend.StartCalls);
        Assert.Equal(1L, Convert.ToInt64(context.Fixture.Scalar("SELECT COUNT(*) FROM recording_runs;")));
        Assert.Equal(1L, Convert.ToInt64(context.Fixture.Scalar("SELECT COUNT(*) FROM recurring_lease_uses;")));
        Assert.Equal(PlanOccurrenceStatus.RunCreated,
            new SqlitePlanOccurrenceRepository(context.Fixture.Store).Get(context.Slot.OccurrenceId!).Status);
        Assert.Equal(RecurringOccurrenceExecutionStage.AfterDueProjection, stages[0]);
        result.LifecycleSession!.Dispose();
    }

    [Fact]
    public async Task AuthorizedOccurrenceWithSpecificationContinuesIdempotently()
    {
        using var context = RecurringOccurrenceReservationTests.ReservationContext.Create(
            countdownSeconds: 0,
            approvalUserSid: "S-1-5-21-task276",
            approvalSessionBinding: CaptureAuthorizationSessionBinding.Current);
        var backend = new CoordinatorBackend();

        var result = await CreateCoordinator(
            context,
            context.Slot.ScheduledStartUtc!.Value,
            backend)
            .ExecuteAsync(CreateRequest(context));

        Assert.Equal(RecurringOccurrenceExecutionStatus.Started, result.Status);
        Assert.Equal(1, backend.StartCalls);
        Assert.Equal(1L, Convert.ToInt64(context.Fixture.Scalar("SELECT COUNT(*) FROM recording_runs;")));
        Assert.Equal(1L, Convert.ToInt64(context.Fixture.Scalar("SELECT COUNT(*) FROM recurring_lease_uses;")));
        result.LifecycleSession!.Dispose();
    }

    [Fact]
    public async Task RunCreatedOccurrenceReentryReusesTheExistingRunAndUse()
    {
        using var context = RecurringOccurrenceReservationTests.ReservationContext.Create(
            countdownSeconds: 0,
            approvalUserSid: "S-1-5-21-task276",
            approvalSessionBinding: CaptureAuthorizationSessionBinding.Current);
        var reservation = new RecurringOccurrenceReservationService(
            context.Fixture.Store,
            () => context.Slot.ScheduledStartUtc!.Value,
            () => "task276r-run-created-run",
            () => "task276r-run-created-use")
            .Reserve(context.Lease.LeaseId, context.Slot.OccurrenceIdentity);
        Assert.Equal(RecurringOccurrenceReservationStatus.Reserved, reservation.Status);

        var backend = new CoordinatorBackend();
        var result = await CreateCoordinator(
            context,
            context.Slot.ScheduledStartUtc!.Value,
            backend)
            .ExecuteAsync(CreateRequest(context));

        Assert.Equal(RecurringOccurrenceExecutionStatus.Started, result.Status);
        Assert.Equal(reservation.RunId, result.RunId);
        Assert.Equal(reservation.UseId, result.UseId);
        Assert.Equal(1, backend.StartCalls);
        Assert.Equal(1L, Convert.ToInt64(context.Fixture.Scalar("SELECT COUNT(*) FROM recording_runs;")));
        Assert.Equal(1L, Convert.ToInt64(context.Fixture.Scalar("SELECT COUNT(*) FROM recurring_lease_uses;")));
        result.LifecycleSession!.Dispose();
    }

    [Fact]
    public async Task StartCommittedOccurrenceReentryDoesNotProofBridgeOrRecoverAgain()
    {
        using var context = RecurringOccurrenceReservationTests.ReservationContext.Create(
            countdownSeconds: 0,
            approvalUserSid: "S-1-5-21-task276",
            approvalSessionBinding: CaptureAuthorizationSessionBinding.Current);
        var reservation = new RecurringOccurrenceReservationService(
            context.Fixture.Store,
            () => context.Slot.ScheduledStartUtc!.Value,
            () => "task276r-start-committed-run",
            () => "task276r-start-committed-use")
            .Reserve(context.Lease.LeaseId, context.Slot.OccurrenceIdentity);
        Assert.Equal(RecurringOccurrenceReservationStatus.Reserved, reservation.Status);
        var committed = new RecurringOccurrenceStartCommitService(
            context.Fixture.Store,
            () => context.Slot.ScheduledStartUtc!.Value,
            new RecurringOccurrenceReservationTests.MatchingReservationEnvironmentProvider())
            .Commit(context.Lease.LeaseId, context.Slot.OccurrenceIdentity);
        Assert.Equal(RecurringOccurrenceStartCommitStatus.Committed, committed.Status);

        var backend = new CoordinatorBackend();
        var result = await CreateCoordinator(
            context,
            context.Slot.ScheduledStartUtc!.Value,
            backend)
            .ExecuteAsync(CreateRequest(context));

        Assert.Contains(
            result.Status,
            new[]
            {
                RecurringOccurrenceExecutionStatus.AlreadyClaimed,
                RecurringOccurrenceExecutionStatus.AlreadyAdvanced,
            });
        Assert.Equal(reservation.RunId, result.RunId);
        Assert.Equal(reservation.UseId, result.UseId);
        Assert.Equal(0, backend.StartCalls);
        Assert.Equal(RecordingRunStatus.StartCommitted,
            new SqliteRecordingRunRepository(context.Fixture.Store).Get(reservation.RunId!).Status);
        Assert.Equal("start_committed",
            context.Fixture.ReadAccountingRow(reservation.UseId!).StatusCode);
    }

    [Fact]
    public async Task PausedPlanStopsAtDueProjectionWithoutReservationOrBackend()
    {
        using var context = RecurringOccurrenceReservationTests.ReservationContext.Create(
            countdownSeconds: 0,
            approvalUserSid: "S-1-5-21-task276",
            approvalSessionBinding: CaptureAuthorizationSessionBinding.Current);
        var planRepository = new SqlitePlanDefinitionRepository(context.Fixture.Store);
        var pausedPlan = planRepository.Get(context.Fixture.PlanId);
        Assert.True(pausedPlan.TryTransition(PlanDefinitionStatus.Paused, context.Slot.ScheduledStartUtc!.Value).Succeeded);
        planRepository.Update(pausedPlan, pausedPlan.Version - 1);
        var backend = new CoordinatorBackend();
        var coordinator = CreateCoordinator(
            context,
            context.Slot.ScheduledStartUtc!.Value,
            backend);

        var result = await coordinator.ExecuteAsync(CreateRequest(context));

        Assert.True(result.Status == RecurringOccurrenceExecutionStatus.Paused,
            $"status={result.Status}; reason={result.Reason}");
        Assert.Equal(0, backend.StartCalls);
        Assert.Equal(0L, Convert.ToInt64(context.Fixture.Scalar("SELECT COUNT(*) FROM recording_runs;")));
        Assert.Equal(0L, Convert.ToInt64(context.Fixture.Scalar("SELECT COUNT(*) FROM recurring_lease_uses;")));
    }

    [Theory]
    [InlineData("before_window")]
    [InlineData("missed")]
    [InlineData("cancelled")]
    public async Task DueBoundariesStopAtTheCorrectDurableStage(string boundary)
    {
        using var context = RecurringOccurrenceReservationTests.ReservationContext.Create(
            targetStatus: "scheduled",
            countdownSeconds: 0,
            approvalUserSid: "S-1-5-21-task276",
            approvalSessionBinding: CaptureAuthorizationSessionBinding.Current);
        if (boundary == "before_window")
        {
            context.Fixture.Execute(
                "UPDATE plan_occurrences SET created_at_utc = $created, updated_at_utc = $created WHERE id = $id;",
                ("$created", context.Slot.ScheduledStartUtc!.Value.AddTicks(-2).UtcDateTime.Ticks),
                ("$id", context.Slot.OccurrenceId!));
        }
        var planRepository = new SqlitePlanDefinitionRepository(context.Fixture.Store);
        if (boundary == "cancelled")
        {
            var plan = planRepository.Get(context.Fixture.PlanId);
            Assert.True(plan.TryTransition(
                PlanDefinitionStatus.Cancelled,
                context.Slot.ScheduledStartUtc!.Value).Succeeded);
            planRepository.Update(plan, plan.Version - 1);
        }

        var now = boundary switch
        {
            "before_window" => context.Slot.ScheduledStartUtc!.Value.AddTicks(-1),
            "missed" => context.Slot.LatestStartUtc!.Value.AddTicks(1),
            _ => context.Slot.ScheduledStartUtc!.Value,
        };
        var backend = new CoordinatorBackend();
        var result = await CreateCoordinator(context, now, backend)
            .ExecuteAsync(CreateRequest(context));

        var expected = boundary switch
        {
            "before_window" => RecurringOccurrenceExecutionStatus.BeforeWindow,
            "missed" => RecurringOccurrenceExecutionStatus.Missed,
            _ => RecurringOccurrenceExecutionStatus.Cancelled,
        };
        Assert.Equal(expected, result.Status);
        Assert.Equal(0, backend.StartCalls);
        Assert.Equal(0L, Convert.ToInt64(context.Fixture.Scalar("SELECT COUNT(*) FROM recording_runs;")));
        Assert.Equal(0L, Convert.ToInt64(context.Fixture.Scalar("SELECT COUNT(*) FROM recurring_lease_uses;")));
    }

    [Fact]
    public async Task SupersededScheduleRevisionTerminalizesBeforeEnvironmentOrReservation()
    {
        using var context = RecurringOccurrenceReservationTests.ReservationContext.Create(
            targetStatus: "scheduled",
            countdownSeconds: 0,
            approvalUserSid: "S-1-5-21-task276",
            approvalSessionBinding: CaptureAuthorizationSessionBinding.Current);
        var supersedingSchedule = RecurringPlanSchedule.CreateDaily(
            TimeZoneInfo.Utc.Id,
            new DateOnly(2026, 9, 11),
            new DateOnly(2026, 9, 11),
            new TimeOnly(11, 0),
            maximumOccurrences: 1,
            recordingDuration: TimeSpan.FromMinutes(2),
            latestStartGrace: TimeSpan.Zero);
        new SqliteRecurringScheduleVersionRepository(context.Fixture.Store)
            .CreateOrGet(
                context.Fixture.PlanId,
                2,
                supersedingSchedule,
                context.Fixture.CreatedAt.AddMinutes(1));
        var environmentProvider = new CountingEnvironmentProvider(
            new RecurringOccurrenceReservationTests.MatchingReservationEnvironmentProvider());
        var backend = new CoordinatorBackend();

        var result = await CreateCoordinator(
            context,
            context.Slot.ScheduledStartUtc!.Value,
            backend,
            environmentProvider: environmentProvider)
            .ExecuteAsync(CreateRequest(context));

        Assert.Equal(RecurringOccurrenceExecutionStatus.Superseded, result.Status);
        Assert.Equal(0, environmentProvider.CaptureCount);
        Assert.Equal(0, backend.StartCalls);
        Assert.Equal(PlanOccurrenceStatus.Cancelled,
            new SqlitePlanOccurrenceRepository(context.Fixture.Store).Get(context.Slot.OccurrenceId!).Status);
        Assert.Equal(0L, Convert.ToInt64(context.Fixture.Scalar("SELECT COUNT(*) FROM recurring_occurrence_execution_specs;")));
        Assert.Equal(0L, Convert.ToInt64(context.Fixture.Scalar("SELECT COUNT(*) FROM recording_runs;")));
        Assert.Equal(0L, Convert.ToInt64(context.Fixture.Scalar("SELECT COUNT(*) FROM recurring_lease_uses;")));
    }

    [Fact]
    public async Task RecheckBlockedRejectedAndConflictStopBeforeReservation()
    {
        using (var blocked = RecurringOccurrenceReservationTests.ReservationContext.Create(
                   targetStatus: "scheduled",
                   disableSafetyBeforeReservation: true,
                   countdownSeconds: 0,
                   approvalUserSid: "S-1-5-21-task276",
                   approvalSessionBinding: CaptureAuthorizationSessionBinding.Current))
        {
            var backend = new CoordinatorBackend();
            var result = await CreateCoordinator(
                blocked,
                blocked.Slot.ScheduledStartUtc!.Value,
                backend)
                .ExecuteAsync(CreateRequest(blocked));

            Assert.Equal(RecurringOccurrenceExecutionStatus.Blocked, result.Status);
            Assert.Equal(PlanOccurrenceStatus.Blocked,
                new SqlitePlanOccurrenceRepository(blocked.Fixture.Store).Get(blocked.Slot.OccurrenceId!).Status);
            Assert.Equal(0, backend.StartCalls);
            Assert.Equal(0L, Convert.ToInt64(blocked.Fixture.Scalar("SELECT COUNT(*) FROM recording_runs;")));
            Assert.Equal(0L, Convert.ToInt64(blocked.Fixture.Scalar("SELECT COUNT(*) FROM recurring_lease_uses;")));
        }

        using (var rejected = RecurringOccurrenceReservationTests.ReservationContext.Create(
                   targetStatus: "scheduled",
                   countdownSeconds: 0,
                   approvalUserSid: "S-1-5-21-task276",
                   approvalSessionBinding: CaptureAuthorizationSessionBinding.Current))
        {
            var backend = new CoordinatorBackend();
            var result = await CreateCoordinator(
                rejected,
                rejected.Slot.ScheduledStartUtc!.Value,
                backend,
                recheckFailureHook: point =>
                {
                    if (point == RecurringOccurrenceEnvironmentRecheckFailurePoint.AfterFirstRecheckingCas)
                        throw new InvalidOperationException("recheck rejection seam");
                })
                .ExecuteAsync(CreateRequest(rejected));

            Assert.Equal(RecurringOccurrenceExecutionStatus.Rejected, result.Status);
            Assert.Equal(PlanOccurrenceStatus.Due,
                new SqlitePlanOccurrenceRepository(rejected.Fixture.Store).Get(rejected.Slot.OccurrenceId!).Status);
            Assert.Equal(0, backend.StartCalls);
            Assert.Equal(0L, Convert.ToInt64(rejected.Fixture.Scalar("SELECT COUNT(*) FROM recurring_occurrence_execution_specs;")));
            Assert.Equal(0L, Convert.ToInt64(rejected.Fixture.Scalar("SELECT COUNT(*) FROM recording_runs;")));
            Assert.Equal(0L, Convert.ToInt64(rejected.Fixture.Scalar("SELECT COUNT(*) FROM recurring_lease_uses;")));
        }

        using var conflict = RecurringOccurrenceReservationTests.ReservationContext.Create(
            targetStatus: "scheduled",
            countdownSeconds: 0,
            approvalUserSid: "S-1-5-21-task276",
            approvalSessionBinding: CaptureAuthorizationSessionBinding.Current);
        var conflictBackend = new CoordinatorBackend();
        var conflictResult = await CreateCoordinator(
            conflict,
            conflict.Slot.ScheduledStartUtc!.Value,
            conflictBackend,
            recheckAfterFirstRecheckingCas: (connection, transaction) =>
            {
                using var command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText = "UPDATE plan_occurrences SET version = version + 1 WHERE id = $id;";
                command.Parameters.AddWithValue("$id", conflict.Slot.OccurrenceId!);
                Assert.Equal(1, command.ExecuteNonQuery());
            })
            .ExecuteAsync(CreateRequest(conflict));

        Assert.Equal(RecurringOccurrenceExecutionStatus.Conflict, conflictResult.Status);
        Assert.Equal(PlanOccurrenceStatus.Due,
            new SqlitePlanOccurrenceRepository(conflict.Fixture.Store).Get(conflict.Slot.OccurrenceId!).Status);
        Assert.Equal(0, conflictBackend.StartCalls);
        Assert.Equal(0L, Convert.ToInt64(conflict.Fixture.Scalar("SELECT COUNT(*) FROM recurring_occurrence_execution_specs;")));
        Assert.Equal(0L, Convert.ToInt64(conflict.Fixture.Scalar("SELECT COUNT(*) FROM recording_runs;")));
        Assert.Equal(0L, Convert.ToInt64(conflict.Fixture.Scalar("SELECT COUNT(*) FROM recurring_lease_uses;")));
    }

    [Fact]
    public async Task ReservationRejectedAndConflictRollbackRunUseAndOccurrence()
    {
        using (var rejected = RecurringOccurrenceReservationTests.ReservationContext.Create(
                   countdownSeconds: 0,
                   approvalUserSid: "S-1-5-21-task276",
                   approvalSessionBinding: CaptureAuthorizationSessionBinding.Current))
        {
            var backend = new CoordinatorBackend();
            var result = await CreateCoordinator(
                rejected,
                rejected.Slot.ScheduledStartUtc!.Value,
                backend,
                reservationFailureHook: point =>
                {
                    if (point == RecurringOccurrenceReservationFailurePoint.AfterRunInsert)
                        throw new InvalidOperationException("reservation rejection seam");
                })
                .ExecuteAsync(CreateRequest(rejected));

            Assert.Equal(RecurringOccurrenceExecutionStatus.Rejected, result.Status);
            Assert.Equal(PlanOccurrenceStatus.Authorized,
                new SqlitePlanOccurrenceRepository(rejected.Fixture.Store).Get(rejected.Slot.OccurrenceId!).Status);
            Assert.Equal(0, backend.StartCalls);
            Assert.Equal(0L, Convert.ToInt64(rejected.Fixture.Scalar("SELECT COUNT(*) FROM recording_runs;")));
            Assert.Equal(0L, Convert.ToInt64(rejected.Fixture.Scalar("SELECT COUNT(*) FROM recurring_lease_uses;")));
        }

        using var conflict = RecurringOccurrenceReservationTests.ReservationContext.Create(
            countdownSeconds: 0,
            approvalUserSid: "S-1-5-21-task276",
            approvalSessionBinding: CaptureAuthorizationSessionBinding.Current);
        var conflictBackend = new CoordinatorBackend();
        var conflictResult = await CreateCoordinator(
            conflict,
            conflict.Slot.ScheduledStartUtc!.Value,
            conflictBackend,
            reservationFailureHook: point =>
            {
                if (point == RecurringOccurrenceReservationFailurePoint.AfterOccurrenceCasUpdate)
                {
                    throw new Phase3PersistenceException(
                        RecurringOccurrenceReservationReasonCodes.Conflict,
                        "reservation conflict seam");
                }
            })
            .ExecuteAsync(CreateRequest(conflict));

        Assert.Equal(RecurringOccurrenceExecutionStatus.Conflict, conflictResult.Status);
        Assert.Equal(PlanOccurrenceStatus.Authorized,
            new SqlitePlanOccurrenceRepository(conflict.Fixture.Store).Get(conflict.Slot.OccurrenceId!).Status);
        Assert.Equal(0, conflictBackend.StartCalls);
        Assert.Equal(0L, Convert.ToInt64(conflict.Fixture.Scalar("SELECT COUNT(*) FROM recording_runs;")));
        Assert.Equal(0L, Convert.ToInt64(conflict.Fixture.Scalar("SELECT COUNT(*) FROM recurring_lease_uses;")));
    }

    [Fact]
    public async Task AlreadyTerminalDueProjectionIsAReadOnlyNoOp()
    {
        using var context = RecurringOccurrenceReservationTests.ReservationContext.Create(
            targetStatus: "scheduled",
            countdownSeconds: 0,
            approvalUserSid: "S-1-5-21-task276",
            approvalSessionBinding: CaptureAuthorizationSessionBinding.Current);
        var now = context.Slot.LatestStartUtc!.Value.AddTicks(1);
        var coordinator = CreateCoordinator(context, now, new CoordinatorBackend());
        var first = await coordinator.ExecuteAsync(CreateRequest(context));
        Assert.Equal(RecurringOccurrenceExecutionStatus.Missed, first.Status);
        var version = new SqlitePlanOccurrenceRepository(context.Fixture.Store)
            .Get(context.Slot.OccurrenceId!).Version;

        var second = await coordinator.ExecuteAsync(CreateRequest(context));

        Assert.Equal(RecurringOccurrenceExecutionStatus.AlreadyTerminal, second.Status);
        Assert.Equal(version, new SqlitePlanOccurrenceRepository(context.Fixture.Store)
            .Get(context.Slot.OccurrenceId!).Version);
        Assert.Equal(0L, Convert.ToInt64(context.Fixture.Scalar("SELECT COUNT(*) FROM recording_runs;")));
    }

    [Fact]
    public async Task ProofIssuerFailureUsesReceiptBoundExactRecoveryAndCannotBeRetried()
    {
        using var context = RecurringOccurrenceReservationTests.ReservationContext.Create(
            countdownSeconds: 0,
            approvalUserSid: "S-1-5-21-task276",
            approvalSessionBinding: CaptureAuthorizationSessionBinding.Current);
        var backend = new CoordinatorBackend();
        var coordinator = CreateCoordinator(
            context,
            context.Slot.ScheduledStartUtc!.Value,
            backend,
            recurringProofIssuer: _ => throw new InvalidOperationException("proof issuer seam"));

        var result = await coordinator.ExecuteAsync(CreateRequest(context));

        Assert.Equal(RecurringOccurrenceExecutionStatus.CommittedNotStarted, result.Status);
        Assert.StartsWith("committed_not_started:recovered:", result.Reason, StringComparison.Ordinal);
        Assert.Equal(0, backend.StartCalls);
        Assert.Equal(RecordingRunStatus.StartedUnknown,
            new SqliteRecordingRunRepository(context.Fixture.Store).Get(result.RunId!).Status);
        Assert.Equal(LeaseUseStatus.StartedUnknown,
            new SqliteRecurringLeaseUseAccountingReader(context.Fixture.Store)
                .TryGetByOccurrence(context.Slot.OccurrenceIdentity)!.Status);
        Assert.Equal(PlanOccurrenceStatus.Blocked,
            new SqlitePlanOccurrenceRepository(context.Fixture.Store).Get(context.Slot.OccurrenceId!).Status);

        var retry = await coordinator.ExecuteAsync(CreateRequest(context));
        Assert.Contains(
            retry.Status,
            new[]
            {
                RecurringOccurrenceExecutionStatus.AlreadyClaimed,
                RecurringOccurrenceExecutionStatus.AlreadyAdvanced,
                RecurringOccurrenceExecutionStatus.AlreadyTerminal,
            });
        Assert.Equal(0, backend.StartCalls);
    }

    [Fact]
    public async Task BridgeFailureRecoversTheTicketChainExactlyOnce()
    {
        using var context = RecurringOccurrenceReservationTests.ReservationContext.Create(
            countdownSeconds: 0,
            approvalUserSid: "S-1-5-21-task276",
            approvalSessionBinding: CaptureAuthorizationSessionBinding.Current);
        var factoryCalls = 0;
        var coordinator = CreateCoordinator(
            context,
            context.Slot.ScheduledStartUtc!.Value,
            backend: null,
            backendFactory: _ =>
            {
                factoryCalls++;
                return null;
            });

        var result = await coordinator.ExecuteAsync(CreateRequest(context));

        Assert.Equal(RecurringOccurrenceExecutionStatus.CommittedNotStarted, result.Status);
        Assert.StartsWith("committed_not_started:recovered:", result.Reason, StringComparison.Ordinal);
        Assert.Equal(1, factoryCalls);
        Assert.Equal(RecordingRunStatus.StartedUnknown,
            new SqliteRecordingRunRepository(context.Fixture.Store).Get(result.RunId!).Status);
    }

    [Fact]
    public async Task ConcurrentCoordinatorsHaveOneFirstProofAndLoserNeverRecoversWinner()
    {
        using var context = RecurringOccurrenceReservationTests.ReservationContext.Create(
            countdownSeconds: 0,
            approvalUserSid: "S-1-5-21-task276",
            approvalSessionBinding: CaptureAuthorizationSessionBinding.Current);
        var path = context.Fixture.Store.DatabasePath;
        var firstBackend = new CoordinatorBackend();
        var secondBackend = new CoordinatorBackend();
        var now = context.Slot.ScheduledStartUtc!.Value;
        var sharedInterlock = new StandingLeaseStartSafetyInterlock();

        var first = CreateCoordinator(
            context,
            now,
            firstBackend,
            store: new SqliteOperationalStore(path),
            startSafetyInterlock: sharedInterlock);
        var second = CreateCoordinator(
            context,
            now,
            secondBackend,
            store: new SqliteOperationalStore(path),
            startSafetyInterlock: sharedInterlock);

        var results = await Task.WhenAll(
            first.ExecuteAsync(CreateRequest(context)),
            second.ExecuteAsync(CreateRequest(context)));

        Assert.Single(results, result => result.Status == RecurringOccurrenceExecutionStatus.Started);
        Assert.Single(results, result => result.Status == RecurringOccurrenceExecutionStatus.AlreadyClaimed);
        Assert.Equal(1, firstBackend.StartCalls + secondBackend.StartCalls);
        Assert.Equal(1L, Convert.ToInt64(context.Fixture.Scalar("SELECT COUNT(*) FROM recording_runs;")));
        Assert.Equal(1L, Convert.ToInt64(context.Fixture.Scalar("SELECT COUNT(*) FROM recurring_lease_uses;")));

        foreach (var result in results.Where(result => result.Status == RecurringOccurrenceExecutionStatus.Started))
            result.LifecycleSession!.Dispose();
    }

    [Fact]
    public async Task SynchronousSettledBridgeMapsToCompletedWithoutRecovery()
    {
        using var context = RecurringOccurrenceReservationTests.ReservationContext.Create(
            countdownSeconds: 0,
            approvalUserSid: "S-1-5-21-task276",
            approvalSessionBinding: CaptureAuthorizationSessionBinding.Current);
        var backend = new CoordinatorBackend { CompleteNaturallyDuringStart = true };
        var coordinator = CreateCoordinator(
            context,
            context.Slot.ScheduledStartUtc!.Value,
            backend);

        var result = await coordinator.ExecuteAsync(CreateRequest(context));

        Assert.True(
            result.Status == RecurringOccurrenceExecutionStatus.CompletedDuringStart,
            $"status={result.Status}; reason={result.Reason}");
        Assert.Null(result.LifecycleSession);
        Assert.Equal(1, backend.StartCalls);
        Assert.Equal(PlanOccurrenceStatus.Completed,
            new SqlitePlanOccurrenceRepository(context.Fixture.Store).Get(context.Slot.OccurrenceId!).Status);
        Assert.Equal(RecordingRunStatus.Settled,
            new SqliteRecordingRunRepository(context.Fixture.Store).Get(result.RunId!).Status);
    }

    [Theory]
    [InlineData("plan")]
    [InlineData("schedule_revision")]
    [InlineData("schedule_digest")]
    [InlineData("scheduled_start")]
    [InlineData("latest_start")]
    [InlineData("planned_end")]
    public async Task CandidateImmutableMismatchStopsBeforeAnyMutation(string field)
    {
        using var context = RecurringOccurrenceReservationTests.ReservationContext.Create(
            countdownSeconds: 0,
            approvalUserSid: "S-1-5-21-task276",
            approvalSessionBinding: CaptureAuthorizationSessionBinding.Current);
        var candidate = CreateRequest(context).Candidate!;
        candidate = field switch
        {
            "plan" => candidate with { PlanId = candidate.PlanId + "-tampered" },
            "schedule_revision" => candidate with { ScheduleRevision = candidate.ScheduleRevision + 1 },
            "schedule_digest" => candidate with { ScheduleDigest = "tampered-schedule-digest" },
            "scheduled_start" => candidate with { ScheduledStartUtc = candidate.ScheduledStartUtc.AddTicks(-1) },
            "latest_start" => candidate with { LatestStartUtc = candidate.LatestStartUtc.AddTicks(1) },
            "planned_end" => candidate with { PlannedEndUtc = candidate.PlannedEndUtc.AddTicks(1) },
            _ => throw new ArgumentOutOfRangeException(nameof(field)),
        };

        var before = new SqlitePlanOccurrenceRepository(context.Fixture.Store).Get(context.Slot.OccurrenceId!);
        var result = await CreateCoordinator(
            context,
            context.Slot.ScheduledStartUtc!.Value,
            new CoordinatorBackend())
            .ExecuteAsync(new RecurringOccurrenceExecutionRequest(
                context.Lease.LeaseId,
                candidate));

        Assert.Equal(RecurringOccurrenceExecutionStatus.Conflict, result.Status);
        Assert.Null(result.PlanId);
        var after = new SqlitePlanOccurrenceRepository(context.Fixture.Store).Get(context.Slot.OccurrenceId!);
        Assert.Equal(before.Status, after.Status);
        Assert.Equal(before.Version, after.Version);
        Assert.Equal(0L, Convert.ToInt64(context.Fixture.Scalar("SELECT COUNT(*) FROM recording_runs;")));
        Assert.Equal(0L, Convert.ToInt64(context.Fixture.Scalar("SELECT COUNT(*) FROM recurring_lease_uses;")));
    }

    [Fact]
    public async Task FutureCandidateVersionIsAConflictWithoutMutation()
    {
        using var context = RecurringOccurrenceReservationTests.ReservationContext.Create(
            countdownSeconds: 0,
            approvalUserSid: "S-1-5-21-task276",
            approvalSessionBinding: CaptureAuthorizationSessionBinding.Current);
        var currentVersion = new SqlitePlanOccurrenceRepository(context.Fixture.Store)
            .Get(context.Slot.OccurrenceId!).Version;
        var candidate = CreateRequest(context).Candidate! with
        {
            OccurrenceVersion = currentVersion + 1,
        };
        var result = await CreateCoordinator(
            context,
            context.Slot.ScheduledStartUtc!.Value,
            new CoordinatorBackend())
            .ExecuteAsync(new RecurringOccurrenceExecutionRequest(context.Lease.LeaseId, candidate));

        Assert.Equal(RecurringOccurrenceExecutionStatus.Conflict, result.Status);
        Assert.Equal(0L, Convert.ToInt64(context.Fixture.Scalar("SELECT COUNT(*) FROM recording_runs;")));
        Assert.Equal(0L, Convert.ToInt64(context.Fixture.Scalar("SELECT COUNT(*) FROM recurring_lease_uses;")));
    }

    [Fact]
    public async Task CancellationBeforeProjectionDoesNotCreateDurableRows()
    {
        using var context = RecurringOccurrenceReservationTests.ReservationContext.Create(
            countdownSeconds: 0,
            approvalUserSid: "S-1-5-21-task276",
            approvalSessionBinding: CaptureAuthorizationSessionBinding.Current);
        var result = await CreateCoordinator(
            context,
            context.Slot.ScheduledStartUtc!.Value,
            new CoordinatorBackend())
            .ExecuteAsync(CreateRequest(context), new CancellationToken(canceled: true));

        Assert.Equal(RecurringOccurrenceExecutionStatus.Cancelled, result.Status);
        Assert.Equal(0L, Convert.ToInt64(context.Fixture.Scalar("SELECT COUNT(*) FROM recording_runs;")));
        Assert.Equal(0L, Convert.ToInt64(context.Fixture.Scalar("SELECT COUNT(*) FROM recurring_lease_uses;")));
        Assert.Equal(PlanOccurrenceStatus.Authorized,
            new SqlitePlanOccurrenceRepository(context.Fixture.Store).Get(context.Slot.OccurrenceId!).Status);
    }

    [Fact]
    public async Task CancellationAfterReservationLeavesOneReusableClaim()
    {
        using var context = RecurringOccurrenceReservationTests.ReservationContext.Create(
            countdownSeconds: 0,
            approvalUserSid: "S-1-5-21-task276",
            approvalSessionBinding: CaptureAuthorizationSessionBinding.Current);
        using var cancellation = new CancellationTokenSource();
        var first = await CreateCoordinator(
            context,
            context.Slot.ScheduledStartUtc!.Value,
            new CoordinatorBackend(),
            stageHook: stage =>
            {
                if (stage == RecurringOccurrenceExecutionStage.AfterReservation)
                    cancellation.Cancel();
            })
            .ExecuteAsync(CreateRequest(context), cancellation.Token);

        Assert.Equal(RecurringOccurrenceExecutionStatus.Cancelled, first.Status);
        Assert.Equal(1L, Convert.ToInt64(context.Fixture.Scalar("SELECT COUNT(*) FROM recording_runs;")));
        Assert.Equal(1L, Convert.ToInt64(context.Fixture.Scalar("SELECT COUNT(*) FROM recurring_lease_uses;")));
        Assert.Equal(PlanOccurrenceStatus.RunCreated,
            new SqlitePlanOccurrenceRepository(context.Fixture.Store).Get(context.Slot.OccurrenceId!).Status);

        var backend = new CoordinatorBackend();
        var retry = await CreateCoordinator(
            context,
            context.Slot.ScheduledStartUtc!.Value,
            backend)
            .ExecuteAsync(CreateRequest(context));

        Assert.Equal(RecurringOccurrenceExecutionStatus.Started, retry.Status);
        Assert.Equal(1, backend.StartCalls);
        Assert.Equal(1L, Convert.ToInt64(context.Fixture.Scalar("SELECT COUNT(*) FROM recording_runs;")));
        Assert.Equal(1L, Convert.ToInt64(context.Fixture.Scalar("SELECT COUNT(*) FROM recurring_lease_uses;")));
        retry.LifecycleSession!.Dispose();
    }

    [Fact]
    public async Task CancellationDuringCountdownClaimsTicketOnceAndRecoversWithoutBackendStart()
    {
        using var context = RecurringOccurrenceReservationTests.ReservationContext.Create(
            countdownSeconds: 3,
            approvalUserSid: "S-1-5-21-task276",
            approvalSessionBinding: CaptureAuthorizationSessionBinding.Current);
        using var cancellation = new CancellationTokenSource();
        var delayCalls = 0;
        var backend = new CoordinatorBackend();
        var result = await CreateCoordinator(
            context,
            context.Slot.ScheduledStartUtc!.Value,
            backend,
            delayForTest: (_, token) =>
            {
                delayCalls++;
                cancellation.Cancel();
                return Task.FromCanceled(token);
            })
            .ExecuteAsync(CreateRequest(context), cancellation.Token);

        Assert.Equal(RecurringOccurrenceExecutionStatus.CommittedNotStarted, result.Status);
        Assert.StartsWith("committed_not_started:recovered:recurring_execution_cancelled", result.Reason, StringComparison.Ordinal);
        Assert.Equal(1, delayCalls);
        Assert.Equal(0, backend.StartCalls);
        Assert.Equal(RecordingRunStatus.StartedUnknown,
            new SqliteRecordingRunRepository(context.Fixture.Store).Get(result.RunId!).Status);
        Assert.Equal("started_unknown",
            context.Fixture.ReadAccountingRow(result.UseId!).StatusCode);
        Assert.Equal(PlanOccurrenceStatus.Blocked,
            new SqlitePlanOccurrenceRepository(context.Fixture.Store).Get(context.Slot.OccurrenceId!).Status);
    }

    [Theory]
    [InlineData("receipt")]
    [InlineData("proof")]
    [InlineData("authorization")]
    [InlineData("ticket")]
    public void ImmediateRecoveryAcceptsEachExactSourceType(string sourceType)
    {
        using var context = RecurringOccurrenceReservationTests.ReservationContext.Create(
            countdownSeconds: 0,
            approvalUserSid: "S-1-5-21-task276",
            approvalSessionBinding: CaptureAuthorizationSessionBinding.Current);
        var sources = CreateRecoverySources(context, createAuthorizationAndTicket: true);
        if (sourceType == "ticket")
            Assert.True(sources.Ticket!.TryClaim(out var ticketReason), ticketReason);

        var before = CaptureRecoveryVersions(context);
        var result = RecoverSource(context, sources, sourceType);

        Assert.Equal(RecurringLeaseRestartRecoveryStatus.Recovered, result.Status);
        Assert.Equal(RecordingRunStatus.StartedUnknown,
            new SqliteRecordingRunRepository(context.Fixture.Store).Get(sources.Receipt.Run.Id).Status);
        Assert.Equal("started_unknown",
            context.Fixture.ReadAccountingRow(sources.Receipt.Use.Id).StatusCode);
        Assert.Equal(PlanOccurrenceStatus.Blocked,
            new SqlitePlanOccurrenceRepository(context.Fixture.Store).Get(sources.Receipt.Occurrence.Id).Status);
        Assert.True(CaptureRecoveryVersions(context).Run > before.Run);
    }

    [Theory]
    [InlineData("specification")]
    [InlineData("profile")]
    [InlineData("configuration")]
    [InlineData("authorization")]
    [InlineData("approval")]
    public void ReceiptRecoveryEvidenceMismatchIsRejectedWithoutMutation(string mismatch)
    {
        using var context = RecurringOccurrenceReservationTests.ReservationContext.Create(
            countdownSeconds: 0,
            approvalUserSid: "S-1-5-21-task276",
            approvalSessionBinding: CaptureAuthorizationSessionBinding.Current);
        var sources = CreateRecoverySources(context, createAuthorizationAndTicket: false);
        TamperRecoveryEvidence(context, mismatch);
        var before = CaptureRecoveryVersions(context);

        var result = RecoverSource(context, sources, "receipt");

        Assert.Equal(RecurringLeaseRestartRecoveryStatus.Rejected, result.Status);
        Assert.Equal(before, CaptureRecoveryVersions(context));
        Assert.Equal(RecordingRunStatus.StartCommitted,
            new SqliteRecordingRunRepository(context.Fixture.Store).Get(sources.Receipt.Run.Id).Status);
        Assert.Equal("start_committed",
            context.Fixture.ReadAccountingRow(sources.Receipt.Use.Id).StatusCode);
    }

    [Theory]
    [InlineData("specification")]
    [InlineData("capture_plan")]
    [InlineData("scope")]
    [InlineData("sid")]
    [InlineData("session")]
    public void ProofRecoveryDigestMismatchIsRejectedWithoutMutation(string mismatch)
    {
        using var context = RecurringOccurrenceReservationTests.ReservationContext.Create(
            countdownSeconds: 0,
            approvalUserSid: "S-1-5-21-task276",
            approvalSessionBinding: CaptureAuthorizationSessionBinding.Current);
        var sources = CreateRecoverySources(context, createAuthorizationAndTicket: false);
        var proof = sources.Proof;
        var tamperedProof = new RecurringLeaseUseProof(
            "task276rr-proof-tampered-" + mismatch,
            proof.RunId,
            proof.LeaseId,
            proof.LeaseUseId,
            proof.OccurrenceIdentity,
            mismatch == "specification" ? proof.SpecificationDigest + "-tampered" : proof.SpecificationDigest,
            proof.AuthorizationSourceId,
            mismatch == "capture_plan" ? proof.CapturePlanDigest + "-tampered" : proof.CapturePlanDigest,
            mismatch == "scope" ? proof.ScopeDigest + "-tampered" : proof.ScopeDigest,
            proof.IssuedAtUtc,
            proof.ExpiresAtUtc,
            mismatch == "sid" ? "S-1-5-21-tampered" : proof.CurrentUserSid,
            mismatch == "session" ? "session-tampered" : proof.SessionBinding,
            "0123456789abcdef0123456789abcde1",
            proof.MaxDuration);
        var before = CaptureRecoveryVersions(context);

        var result = new RecurringLeaseImmediateRecoveryService(
            context.Fixture.Store,
            () => "S-1-5-21-task276",
            () => CaptureAuthorizationSessionBinding.Current)
            .Recover(tamperedProof, context.Slot.ScheduledStartUtc!.Value);

        Assert.Equal(RecurringLeaseRestartRecoveryStatus.Rejected, result.Status);
        Assert.Equal(before, CaptureRecoveryVersions(context));
    }

    [Fact]
    public void AuthorizationRecoveryFrozenSpecificationMismatchIsRejectedWithoutMutation()
    {
        using var context = RecurringOccurrenceReservationTests.ReservationContext.Create(
            countdownSeconds: 0,
            approvalUserSid: "S-1-5-21-task276",
            approvalSessionBinding: CaptureAuthorizationSessionBinding.Current);
        var sources = CreateRecoverySources(context, createAuthorizationAndTicket: true);
        TamperRecoveryEvidence(context, "specification");
        var before = CaptureRecoveryVersions(context);

        var result = RecoverSource(context, sources, "authorization");

        Assert.Equal(RecurringLeaseRestartRecoveryStatus.Rejected, result.Status);
        Assert.Equal(before, CaptureRecoveryVersions(context));
    }

    [Fact]
    public void TicketRecoveryFrozenSpecificationMismatchIsRejectedAfterTicketClaim()
    {
        using var context = RecurringOccurrenceReservationTests.ReservationContext.Create(
            countdownSeconds: 0,
            approvalUserSid: "S-1-5-21-task276",
            approvalSessionBinding: CaptureAuthorizationSessionBinding.Current);
        var sources = CreateRecoverySources(context, createAuthorizationAndTicket: true);
        Assert.True(sources.Ticket!.TryClaim(out var ticketReason), ticketReason);
        TamperRecoveryEvidence(context, "specification");
        var before = CaptureRecoveryVersions(context);

        var result = RecoverSource(context, sources, "ticket");

        Assert.Equal(RecurringLeaseRestartRecoveryStatus.Rejected, result.Status);
        Assert.Equal(before, CaptureRecoveryVersions(context));
    }

    [Fact]
    public void CurrentSidAndSessionMismatchAreRejectedIndependentlyWithoutMutation()
    {
        using (var sidMismatch = RecurringOccurrenceReservationTests.ReservationContext.Create(
                   countdownSeconds: 0,
                   approvalUserSid: "S-1-5-21-task276",
                   approvalSessionBinding: CaptureAuthorizationSessionBinding.Current))
        {
            var sources = CreateRecoverySources(sidMismatch, createAuthorizationAndTicket: false);
            var before = CaptureRecoveryVersions(sidMismatch);
            var result = new RecurringLeaseImmediateRecoveryService(
                sidMismatch.Fixture.Store,
                () => "S-1-5-21-other",
                () => CaptureAuthorizationSessionBinding.Current)
                .Recover(sources.Receipt, sidMismatch.Slot.ScheduledStartUtc!.Value);
            Assert.Equal(RecurringLeaseRestartRecoveryStatus.Rejected, result.Status);
            Assert.Equal(before, CaptureRecoveryVersions(sidMismatch));
        }

        using var sessionMismatch = RecurringOccurrenceReservationTests.ReservationContext.Create(
            countdownSeconds: 0,
            approvalUserSid: "S-1-5-21-task276",
            approvalSessionBinding: CaptureAuthorizationSessionBinding.Current);
        var sessionSources = CreateRecoverySources(sessionMismatch, createAuthorizationAndTicket: false);
        var sessionBefore = CaptureRecoveryVersions(sessionMismatch);
        var sessionResult = new RecurringLeaseImmediateRecoveryService(
            sessionMismatch.Fixture.Store,
            () => "S-1-5-21-task276",
            () => "session-task276-mismatch")
            .Recover(sessionSources.Receipt, sessionMismatch.Slot.ScheduledStartUtc!.Value);
        Assert.Equal(RecurringLeaseRestartRecoveryStatus.Rejected, sessionResult.Status);
        Assert.Equal(sessionBefore, CaptureRecoveryVersions(sessionMismatch));
    }

    [Theory]
    [InlineData("AfterStartCommit")]
    [InlineData("AfterAuthorization")]
    [InlineData("AfterTicket")]
    public async Task CancellationAfterEachCommitHandoffUsesExactEvidenceRecovery(
        string cancellationStage)
    {
        using var context = RecurringOccurrenceReservationTests.ReservationContext.Create(
            countdownSeconds: 0,
            approvalUserSid: "S-1-5-21-task276",
            approvalSessionBinding: CaptureAuthorizationSessionBinding.Current);
        using var cancellation = new CancellationTokenSource();
        var selectedStage = Enum.Parse<RecurringOccurrenceExecutionStage>(cancellationStage);
        var backend = new CoordinatorBackend();
        var result = await CreateCoordinator(
            context,
            context.Slot.ScheduledStartUtc!.Value,
            backend,
            stageHook: stage =>
            {
                if (stage == selectedStage)
                    cancellation.Cancel();
            })
            .ExecuteAsync(CreateRequest(context), cancellation.Token);

        Assert.Equal(RecurringOccurrenceExecutionStatus.CommittedNotStarted, result.Status);
        Assert.StartsWith(
            "committed_not_started:recovered:recurring_execution_cancelled",
            result.Reason,
            StringComparison.Ordinal);
        Assert.Equal(0, backend.StartCalls);
        Assert.Null(result.LifecycleSession);
        Assert.Equal(RecordingRunStatus.StartedUnknown,
            new SqliteRecordingRunRepository(context.Fixture.Store).Get(result.RunId!).Status);
        Assert.Equal(LeaseUseStatus.StartedUnknown,
            new SqliteRecurringLeaseUseAccountingReader(context.Fixture.Store)
                .TryGetByOccurrence(context.Slot.OccurrenceIdentity)!.Status);
        Assert.Equal(PlanOccurrenceStatus.Blocked,
            new SqlitePlanOccurrenceRepository(context.Fixture.Store).Get(context.Slot.OccurrenceId!).Status);
    }

    [Fact]
    public async Task ProofRecoveryDigestMismatchDoesNotMutateCommittedChain()
    {
        using var context = RecurringOccurrenceReservationTests.ReservationContext.Create(
            countdownSeconds: 0,
            approvalUserSid: "S-1-5-21-task276",
            approvalSessionBinding: CaptureAuthorizationSessionBinding.Current);
        using var cancellation = new CancellationTokenSource();
        var result = await CreateCoordinator(
            context,
            context.Slot.ScheduledStartUtc!.Value,
            new CoordinatorBackend(),
            stageHook: stage =>
            {
                if (stage != RecurringOccurrenceExecutionStage.AfterStartCommit)
                    return;
                context.Fixture.Execute(
                    "PRAGMA foreign_keys = OFF; DROP TRIGGER trg_recurring_occurrence_execution_specs_immutable_update; UPDATE recurring_occurrence_execution_specs SET specification_digest = $digest WHERE occurrence_identity = $identity;",
                    ("$digest", "recurring-occurrence-execution-spec/v1:" + new string('a', 64)),
                    ("$identity", context.Slot.OccurrenceIdentity));
                cancellation.Cancel();
            })
            .ExecuteAsync(CreateRequest(context), cancellation.Token);

        Assert.Equal(RecurringOccurrenceExecutionStatus.CommittedNotStarted, result.Status);
        Assert.Contains("recovery_failed:", result.Reason, StringComparison.Ordinal);
        Assert.Equal(RecordingRunStatus.StartCommitted,
            new SqliteRecordingRunRepository(context.Fixture.Store).Get(result.RunId!).Status);
        Assert.Equal(LeaseUseStatus.StartCommitted,
            new SqliteRecurringLeaseUseAccountingReader(context.Fixture.Store)
                .TryGetByOccurrence(context.Slot.OccurrenceIdentity)!.Status);
        Assert.Equal(PlanOccurrenceStatus.RunCreated,
            new SqlitePlanOccurrenceRepository(context.Fixture.Store).Get(context.Slot.OccurrenceId!).Status);
    }

    [Fact]
    public async Task ExactReadRaceReturnsConflictWithoutRecoveryMutation()
    {
        using var context = RecurringOccurrenceReservationTests.ReservationContext.Create(
            countdownSeconds: 0,
            approvalUserSid: "S-1-5-21-task276",
            approvalSessionBinding: CaptureAuthorizationSessionBinding.Current);
        using var cancellation = new CancellationTokenSource();
        var result = await CreateCoordinator(
            context,
            context.Slot.ScheduledStartUtc!.Value,
            new CoordinatorBackend(),
            stageHook: stage =>
            {
                if (stage == RecurringOccurrenceExecutionStage.AfterStartCommit)
                    cancellation.Cancel();
            },
            recoveryAfterExactRead: (connection, transaction) =>
            {
                using var command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText = "UPDATE recording_runs SET version = version + 1 WHERE occurrence_id = $occurrence_id;";
                command.Parameters.AddWithValue("$occurrence_id", context.Slot.OccurrenceId!);
                command.ExecuteNonQuery();
            })
            .ExecuteAsync(CreateRequest(context), cancellation.Token);

        Assert.Equal(RecurringOccurrenceExecutionStatus.CommittedNotStarted, result.Status);
        Assert.Contains("recovery_failed:recovery_concurrency_conflict", result.Reason, StringComparison.Ordinal);
        Assert.Equal(RecordingRunStatus.StartCommitted,
            new SqliteRecordingRunRepository(context.Fixture.Store).Get(result.RunId!).Status);
        Assert.Equal(PlanOccurrenceStatus.RunCreated,
            new SqlitePlanOccurrenceRepository(context.Fixture.Store).Get(context.Slot.OccurrenceId!).Status);
    }

    [Fact]
    public async Task RecoveryFailureAfterRunUpdateRollsBackImmediateRecovery()
    {
        using var context = RecurringOccurrenceReservationTests.ReservationContext.Create(
            countdownSeconds: 0,
            approvalUserSid: "S-1-5-21-task276",
            approvalSessionBinding: CaptureAuthorizationSessionBinding.Current);
        using var cancellation = new CancellationTokenSource();
        var result = await CreateCoordinator(
            context,
            context.Slot.ScheduledStartUtc!.Value,
            new CoordinatorBackend(),
            stageHook: stage =>
            {
                if (stage == RecurringOccurrenceExecutionStage.AfterStartCommit)
                    cancellation.Cancel();
            },
            recoveryFailureHook: point =>
            {
                if (point == RecurringLeaseRestartRecoveryFailurePoint.AfterRunUpdate)
                    throw new InvalidOperationException("test rollback");
            })
            .ExecuteAsync(CreateRequest(context), cancellation.Token);

        Assert.Equal(RecurringOccurrenceExecutionStatus.CommittedNotStarted, result.Status);
        Assert.Contains("recovery_failed:recovery_sqlite_failure", result.Reason, StringComparison.Ordinal);
        Assert.Equal(RecordingRunStatus.StartCommitted,
            new SqliteRecordingRunRepository(context.Fixture.Store).Get(result.RunId!).Status);
        Assert.Equal(LeaseUseStatus.StartCommitted,
            new SqliteRecurringLeaseUseAccountingReader(context.Fixture.Store)
                .TryGetByOccurrence(context.Slot.OccurrenceIdentity)!.Status);
    }

    [Fact]
    public async Task ProofRecoveryTerminalReentryIsAlreadyReconciledWithoutVersionAdvance()
    {
        using var context = RecurringOccurrenceReservationTests.ReservationContext.Create(
            countdownSeconds: 0,
            approvalUserSid: "S-1-5-21-task276",
            approvalSessionBinding: CaptureAuthorizationSessionBinding.Current);
        using var cancellation = new CancellationTokenSource();
        RecurringLeaseUseProof? proof = null;
        var result = await CreateCoordinator(
            context,
            context.Slot.ScheduledStartUtc!.Value,
            new CoordinatorBackend(),
            recurringProofIssuer: receipt =>
            {
                proof = CaptureAuthorizationProofIssuer.IssueRecurringLeaseUse(receipt);
                return proof;
            },
            stageHook: stage =>
            {
                if (stage == RecurringOccurrenceExecutionStage.AfterStartCommit)
                    cancellation.Cancel();
            })
            .ExecuteAsync(CreateRequest(context), cancellation.Token);

        Assert.Equal(RecurringOccurrenceExecutionStatus.CommittedNotStarted, result.Status);
        Assert.NotNull(proof);
        var before = new
        {
            Run = new SqliteRecordingRunRepository(context.Fixture.Store).Get(result.RunId!).Version,
            Use = Convert.ToInt64(context.Fixture.Scalar(
                "SELECT version FROM recurring_lease_uses WHERE occurrence_identity = $identity;",
                ("$identity", context.Slot.OccurrenceIdentity))),
            Occurrence = new SqlitePlanOccurrenceRepository(context.Fixture.Store)
                .Get(context.Slot.OccurrenceId!).Version,
        };
        var second = new RecurringLeaseImmediateRecoveryService(
            context.Fixture.Store,
            () => "S-1-5-21-task276",
            () => CaptureAuthorizationSessionBinding.Current)
            .Recover(proof, context.Slot.ScheduledStartUtc!.Value);

        Assert.Equal(RecurringLeaseRestartRecoveryStatus.AlreadyReconciled, second.Status);
        Assert.Equal(before.Run, new SqliteRecordingRunRepository(context.Fixture.Store).Get(result.RunId!).Version);
        Assert.Equal(before.Use, Convert.ToInt64(context.Fixture.Scalar(
            "SELECT version FROM recurring_lease_uses WHERE occurrence_identity = $identity;",
            ("$identity", context.Slot.OccurrenceIdentity))));
        Assert.Equal(before.Occurrence, new SqlitePlanOccurrenceRepository(context.Fixture.Store)
            .Get(context.Slot.OccurrenceId!).Version);
    }

    [Fact]
    public async Task SharedSafetyInterlockRejectsBeforeBackendStartAndRecoversOnce()
    {
        using var context = RecurringOccurrenceReservationTests.ReservationContext.Create(
            countdownSeconds: 0,
            approvalUserSid: "S-1-5-21-task276",
            approvalSessionBinding: CaptureAuthorizationSessionBinding.Current);
        var backend = new CoordinatorBackend();
        var interlock = new StandingLeaseStartSafetyInterlock();
        var result = await CreateCoordinator(
            context,
            context.Slot.ScheduledStartUtc!.Value,
            backend,
            startSafetyInterlock: interlock,
            currentSafetyValidator: (_, _) => new(RecurringLeaseCurrentSafetyStatus.StopAllActive))
            .ExecuteAsync(CreateRequest(context));

        Assert.Equal(RecurringOccurrenceExecutionStatus.CommittedNotStarted, result.Status);
        Assert.Contains(
            "recovered:recurring_execution_stop_all_active",
            result.Reason,
            StringComparison.Ordinal);
        Assert.Equal(0, backend.StartCalls);
        Assert.Equal(RecordingRunStatus.StartedUnknown,
            new SqliteRecordingRunRepository(context.Fixture.Store).Get(result.RunId!).Status);
    }

    [Fact]
    public async Task StartedOwnershipSurvivesOuterCancellationUntilExplicitSessionDispose()
    {
        using var context = RecurringOccurrenceReservationTests.ReservationContext.Create(
            countdownSeconds: 0,
            approvalUserSid: "S-1-5-21-task276",
            approvalSessionBinding: CaptureAuthorizationSessionBinding.Current);
        using var cancellation = new CancellationTokenSource();
        var backend = new CoordinatorBackend();
        var result = await CreateCoordinator(
            context,
            context.Slot.ScheduledStartUtc!.Value,
            backend)
            .ExecuteAsync(CreateRequest(context), cancellation.Token);

        Assert.Equal(RecurringOccurrenceExecutionStatus.Started, result.Status);
        cancellation.Cancel();
        Assert.Equal(0, backend.StopCalls);
        Assert.Equal(0, backend.DisposeCalls);
        result.LifecycleSession!.Dispose();
    }

    [Fact]
    public void RequestAndResultSurfacesAreInternalAndDoNotExposeExecutionSecrets()
    {
        Assert.True(typeof(RecurringOccurrenceExecutionCoordinator).IsNotPublic);
        Assert.True(typeof(RecurringOccurrenceExecutionRequest).IsNotPublic);
        Assert.True(typeof(RecurringOccurrenceExecutionResult).IsNotPublic);
        Assert.True(typeof(RecurringLeaseImmediateRecoveryEvidence).IsNotPublic);

        var requestNames = typeof(RecurringOccurrenceExecutionRequest)
            .GetProperties(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            .Select(property => property.Name)
            .ToArray();
        Assert.Equal(new[] { "LeaseId", "Candidate" }, requestNames);

        var resultNames = typeof(RecurringOccurrenceExecutionResult)
            .GetProperties(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            .Select(property => property.Name)
            .ToArray();
        Assert.DoesNotContain(resultNames, name => name.Contains("Proof", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(resultNames, name => name.Contains("Ticket", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(resultNames, name => name.Contains("Config", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(resultNames, name => name.Contains("Backend", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(resultNames, name => name.Contains("Path", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(resultNames, name => name.Contains("Bounds", StringComparison.OrdinalIgnoreCase));

        var evidenceNames = typeof(RecurringLeaseImmediateRecoveryEvidence)
            .GetProperties(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            .Select(property => property.Name)
            .ToArray();
        Assert.DoesNotContain(evidenceNames, name => name.Contains("Nonce", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(evidenceNames, name => name.Contains("Backend", StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [InlineData("settled")]
    [InlineData("blocked")]
    public void ImmediateRecoveryLegalSettledAndBlockedTerminalsAreReadOnly(string terminal)
    {
        using var context = RecurringOccurrenceReservationTests.ReservationContext.Create(
            countdownSeconds: 0,
            approvalUserSid: "S-1-5-21-task276",
            approvalSessionBinding: CaptureAuthorizationSessionBinding.Current);
        var sources = CreateRecoverySources(context, createAuthorizationAndTicket: false);
        var now = context.Slot.ScheduledStartUtc!.Value;
        var recovery = new RecurringLeaseImmediateRecoveryService(
            context.Fixture.Store,
            () => "S-1-5-21-task276",
            () => CaptureAuthorizationSessionBinding.Current);
        if (terminal == "settled")
        {
            var lifecycle = new SqliteRecurringLeaseLifecycleTransaction(
                context.Fixture.Store,
                sources.Receipt);
            Assert.True(lifecycle.ObserveFirstFrame(
                new FirstFrameObservation { FrameNumber = 1, TotalSizeBytes = 1024 },
                now.AddMinutes(1)).Succeeded);
            Assert.True(lifecycle.CompleteTermination(
                RecurringLeaseLifecycleTerminationKind.NaturalExit,
                0,
                new OutputMeta
                {
                    OutputFileExists = true,
                    SizeBytes = 1024,
                    DurationSeconds = 1,
                    OutputPath = sources.Receipt.Specification.FrozenOutputFilePath,
                    StopReason = "natural",
                },
                now.AddMinutes(2)).Succeeded);
        }
        else
        {
            var first = recovery.Recover(sources.Receipt, now);
            Assert.Equal(RecurringLeaseRestartRecoveryStatus.Recovered, first.Status);
        }

        var before = CaptureRecoveryVersions(context);
        var second = recovery.Recover(sources.Receipt, now.AddMinutes(3));

        Assert.Equal(RecurringLeaseRestartRecoveryStatus.AlreadyReconciled, second.Status);
        Assert.Equal(before, CaptureRecoveryVersions(context));
    }

    [Fact]
    public void ImmediateRecoveryTargetsOneChainWhenMoreThanOneHundredHistoricalTerminalsExist()
    {
        const int historicalTerminalCount = 101;
        using var context = RecurringOccurrenceReservationTests.ReservationContext.Create(
            slotCount: historicalTerminalCount + 1,
            maxUses: 200,
            maxCumulativeDuration: TimeSpan.FromMinutes(400),
            countdownSeconds: 0,
            approvalUserSid: "S-1-5-21-task276",
            approvalSessionBinding: CaptureAuthorizationSessionBinding.Current);
        for (var index = 0; index < historicalTerminalCount; index++)
        {
            SeedAbnormalTerminal(
                context,
                context.Slots[index],
                "task276rr-history-run-" + index,
                "task276rr-history-use-" + index);
        }

        var target = context.Slots[historicalTerminalCount];
        var sources = CreateRecoverySources(context, createAuthorizationAndTicket: false, target);
        var historyBefore = CaptureRecoveryVersions(context, context.Slots[0]);
        var recovery = new RecurringLeaseImmediateRecoveryService(
            context.Fixture.Store,
            () => "S-1-5-21-task276",
            () => CaptureAuthorizationSessionBinding.Current);

        var result = recovery.Recover(sources.Receipt, target.ScheduledStartUtc!.Value);

        Assert.Equal(RecurringLeaseRestartRecoveryStatus.Recovered, result.Status);
        Assert.Equal(historyBefore, CaptureRecoveryVersions(context, context.Slots[0]));
        Assert.Equal("started_unknown", Convert.ToString(context.Fixture.Scalar(
            "SELECT status_code FROM recording_runs WHERE id = $id;",
            ("$id", sources.Receipt.Run.Id))));
        Assert.Equal(1L, Convert.ToInt64(context.Fixture.Scalar(
            "SELECT COUNT(*) FROM recording_runs WHERE status_code = 'started_unknown' AND id = $id;",
            ("$id", sources.Receipt.Run.Id))));
        Assert.Equal(historicalTerminalCount + 1L,
            Convert.ToInt64(context.Fixture.Scalar("SELECT COUNT(*) FROM recurring_lease_uses;")));
    }

    private static RecoverySources CreateRecoverySources(
        RecurringOccurrenceReservationTests.ReservationContext context,
        bool createAuthorizationAndTicket,
        RecurringOccurrenceSlotSnapshot? selectedSlot = null)
    {
        var slot = selectedSlot ?? context.Slot;
        var now = slot.ScheduledStartUtc!.Value;
        var reservation = new RecurringOccurrenceReservationService(
            context.Fixture.Store,
            () => now,
            () => "task276rr-recovery-run",
            () => "task276rr-recovery-use")
            .Reserve(context.Lease.LeaseId, slot.OccurrenceIdentity);
        Assert.Equal(RecurringOccurrenceReservationStatus.Reserved, reservation.Status);
        var committed = new RecurringOccurrenceStartCommitService(
            context.Fixture.Store,
            () => now,
            new RecurringOccurrenceReservationTests.MatchingReservationEnvironmentProvider())
            .Commit(context.Lease.LeaseId, slot.OccurrenceIdentity);
        Assert.Equal(RecurringOccurrenceStartCommitStatus.Committed, committed.Status);
        var receipt = Assert.IsType<RecurringStartCommitReceipt>(committed.FirstCommitReceipt);
        var proof = Assert.IsType<RecurringLeaseUseProof>(committed.FirstCommitProof);
        if (!createAuthorizationAndTicket)
            return new RecoverySources(receipt, proof, null, null);

        var loader = new SqliteRecurringLeaseExecutionSnapshotLoader(
            context.Fixture.Store,
            snapshotLoadedBeforeEnvironmentForTest: null,
            beforeCommitForTest: null,
            new RecurringOccurrenceReservationTests.MatchingReservationEnvironmentProvider(),
            () => now);
        Assert.True(
            loader.TryAuthorizeAndConsumeRecurringLeaseUse(
                proof,
                out var authorization,
                out var authorizationReason),
            authorizationReason);
        Assert.NotNull(authorization);
        Assert.True(
            RecurringLeaseCaptureExecutionTicket.TryCreate(
                authorization,
                out var ticket,
                out var ticketReason),
            ticketReason);
        Assert.NotNull(ticket);
        return new RecoverySources(receipt, proof, authorization, ticket);
    }

    private static RecurringLeaseRestartRecoveryResult RecoverSource(
        RecurringOccurrenceReservationTests.ReservationContext context,
        RecoverySources sources,
        string sourceType)
    {
        var recovery = new RecurringLeaseImmediateRecoveryService(
            context.Fixture.Store,
            () => "S-1-5-21-task276",
            () => CaptureAuthorizationSessionBinding.Current);
        var now = sources.Receipt.Specification.ScheduledStartUtc;
        return sourceType switch
        {
            "receipt" => recovery.Recover(sources.Receipt, now),
            "proof" => recovery.Recover(sources.Proof, now),
            "authorization" => recovery.Recover(sources.Authorization, now),
            "ticket" => recovery.Recover(sources.Ticket, now),
            _ => throw new ArgumentOutOfRangeException(nameof(sourceType)),
        };
    }

    private static void TamperRecoveryEvidence(
        RecurringOccurrenceReservationTests.ReservationContext context,
        string mismatch)
    {
        switch (mismatch)
        {
            case "specification":
                context.Fixture.Execute(
                    "DROP TRIGGER trg_recurring_occurrence_execution_specs_immutable_update; UPDATE recurring_occurrence_execution_specs SET specification_digest = $digest WHERE occurrence_identity = $identity;",
                    ("$digest", "recurring-occurrence-execution-spec/v1:" + new string('a', 64)),
                    ("$identity", context.Slot.OccurrenceIdentity));
                break;
            case "profile":
                context.Fixture.Execute(
                    "DROP TRIGGER trg_recurring_fixed_region_profile_versions_immutable_update; UPDATE recurring_fixed_region_profile_versions SET duration_ms = duration_ms + 1 WHERE profile_id = $profile AND profile_version = $version;",
                    ("$profile", context.Fixture.Setup.ExactProfile.Reference.ProfileId),
                    ("$version", context.Fixture.Setup.ExactProfile.Reference.ProfileVersion));
                break;
            case "configuration":
                context.Fixture.Execute(
                    "PRAGMA foreign_keys = OFF; DROP TRIGGER trg_recurring_occurrence_execution_specs_immutable_update; UPDATE recurring_occurrence_execution_specs SET configuration_digest = $digest WHERE occurrence_identity = $identity;",
                    ("$digest", "recurring-plan-configuration/v1:" + new string('b', 64)),
                    ("$identity", context.Slot.OccurrenceIdentity));
                break;
            case "authorization":
                context.Fixture.Execute(
                    "PRAGMA foreign_keys = OFF; DROP TRIGGER trg_recurring_consent_leases_lifecycle_update; UPDATE recurring_consent_leases SET authorization_digest = $digest WHERE lease_id = $lease;",
                    ("$digest", "recurring-consent-lease-authorization/v1:" + new string('c', 64)),
                    ("$lease", context.Lease.LeaseId));
                break;
            case "approval":
                context.Fixture.Execute(
                    "PRAGMA foreign_keys = OFF; DROP TRIGGER trg_recurring_lease_local_approvals_immutable_update; UPDATE recurring_lease_local_approvals SET current_user_sid = 'S-1-5-21-tampered' WHERE lease_id = $lease;",
                    ("$lease", context.Lease.LeaseId));
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(mismatch));
        }
    }

    private static RecoveryVersions CaptureRecoveryVersions(
        RecurringOccurrenceReservationTests.ReservationContext context,
        RecurringOccurrenceSlotSnapshot? selectedSlot = null)
    {
        var slot = selectedSlot ?? context.Slot;
        return new(
            Convert.ToInt64(context.Fixture.Scalar(
                "SELECT version FROM recording_runs WHERE occurrence_id = $id;",
                ("$id", slot.OccurrenceId!))),
            Convert.ToInt64(context.Fixture.Scalar(
                "SELECT version FROM recurring_lease_uses WHERE occurrence_identity = $identity;",
                ("$identity", slot.OccurrenceIdentity))),
            new SqlitePlanOccurrenceRepository(context.Fixture.Store).Get(slot.OccurrenceId!).Version,
            Convert.ToInt64(context.Fixture.Scalar(
                "SELECT version FROM recurring_consent_leases WHERE lease_id = $id;",
                ("$id", context.Lease.LeaseId))),
            Convert.ToInt64(context.Fixture.Scalar(
                "SELECT version FROM plans WHERE id = $id;",
                ("$id", context.Fixture.PlanId))));
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

    private sealed record RecoverySources(
        RecurringStartCommitReceipt Receipt,
        RecurringLeaseUseProof Proof,
        RecurringLeaseCaptureAuthorization? Authorization,
        RecurringLeaseCaptureExecutionTicket? Ticket);

    private sealed record RecoveryVersions(
        long Run,
        long Use,
        long Occurrence,
        long Lease,
        long Plan);

    private static RecurringOccurrenceExecutionCoordinator CreateCoordinator(
        RecurringOccurrenceReservationTests.ReservationContext context,
        DateTimeOffset now,
        CoordinatorBackend? backend,
        SqliteOperationalStore? store = null,
        IRecurringOccurrenceEnvironmentProvider? environmentProvider = null,
        Func<TimeSpan, CancellationToken, Task>? delayForTest = null,
        Func<RecurringOccurrenceExecutionSpecification, ICaptureBackend?>? backendFactory = null,
        Func<RecurringStartCommitReceipt, RecurringLeaseUseProof>? recurringProofIssuer = null,
        Func<RecurringLeaseCaptureExecutionTicket, ICaptureBackend, IRecurringLeaseCaptureLifecycleSession?>? lifecycleSessionFactory = null,
        StandingLeaseStartSafetyInterlock? startSafetyInterlock = null,
        Func<RecurringLeaseCaptureExecutionTicket, DateTimeOffset, RecurringLeaseCurrentSafetyDecision>? currentSafetyValidator = null,
        Action<RecurringOccurrenceExecutionStage>? stageHook = null,
        Action<RecurringOccurrenceEnvironmentRecheckFailurePoint>? recheckFailureHook = null,
        Action<SqliteConnection, SqliteTransaction>? recheckBeforeFinalReadback = null,
        Action<SqliteConnection, SqliteTransaction>? recheckAfterFirstRecheckingCas = null,
        Action<RecurringOccurrenceReservationFailurePoint>? reservationFailureHook = null,
        Action<SqliteConnection, SqliteTransaction>? reservationBeforeFinalReadback = null,
        Action<RecurringLeaseRestartRecoveryFailurePoint>? recoveryFailureHook = null,
        Action<SqliteConnection, SqliteTransaction>? recoveryAfterExactRead = null) =>
        new(
            store ?? context.Fixture.Store,
            environmentProvider ?? new RecurringOccurrenceReservationTests.MatchingReservationEnvironmentProvider(),
            backendFactory ?? (_ => backend),
            delayForTest,
            utcNowForTest: () => now,
            startSafetyInterlockForTest: startSafetyInterlock,
            currentSafetyValidatorForTest: currentSafetyValidator,
            currentUserSidForTest: () => "S-1-5-21-task276",
            sessionBindingForTest: () => CaptureAuthorizationSessionBinding.Current,
            recurringProofIssuerForTest: recurringProofIssuer,
            lifecycleSessionFactoryForTest: lifecycleSessionFactory,
            recheckFailureHookForTest: recheckFailureHook,
            recheckBeforeFinalReadbackForTest: recheckBeforeFinalReadback,
            recheckAfterFirstRecheckingCasForTest: recheckAfterFirstRecheckingCas,
            reservationFailureHookForTest: reservationFailureHook,
            reservationBeforeFinalReadbackForTest: reservationBeforeFinalReadback,
            recoveryFailureHookForTest: recoveryFailureHook,
            recoveryAfterExactReadForTest: recoveryAfterExactRead,
            stageHookForTest: stageHook);

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

    private static RecurringOccurrenceExecutionRequest CreateRequest(
        RecurringOccurrenceReservationTests.ReservationContext context) =>
        new(
            context.Lease.LeaseId,
            new PeriodicOccurrenceDueCandidate(
                context.Fixture.PlanId,
                context.Slot.ScheduleRevision,
                context.Slot.ScheduleDigest,
                context.Slot.OccurrenceIdentity,
                context.Slot.ScheduledStartUtc!.Value,
                context.Slot.LatestStartUtc!.Value,
                context.Slot.PlannedEndUtc!.Value,
                0));

    private sealed class CoordinatorBackend : ICaptureBackend, IFirstFrameObservableCaptureBackend
    {
        private Action<FirstFrameObservation>? _firstFrameObserved;
        private Action<int, OutputMeta>? _naturalExit;

        internal int StartCalls { get; private set; }
        internal int StopCalls { get; private set; }
        internal int DisposeCalls { get; private set; }
        internal bool CompleteNaturallyDuringStart { get; init; }

        public void Start(CaptureConfig config, CaptureAuthorizationProof authorizationProof)
        {
            StartCalls++;
            authorizationProof.RequireConsumed();
            if (CompleteNaturallyDuringStart)
            {
                _firstFrameObserved?.Invoke(new FirstFrameObservation
                {
                    FrameNumber = 1,
                    TotalSizeBytes = 1024,
                    OutTimeUs = 1,
                });
                _naturalExit?.Invoke(0, new OutputMeta
                {
                    SizeBytes = 1024,
                    OutputFileExists = true,
                    DurationSeconds = 1,
                    OutputPath = config.OutputPath,
                    StopReason = "natural",
                });
            }
        }

        public OutputMeta Stop()
        {
            StopCalls++;
            return new()
            {
                SizeBytes = 1024,
                OutputFileExists = true,
                DurationSeconds = 1,
                StopReason = "stop",
            };
        }

        public void OnNaturalExit(Action<int, OutputMeta> callback) => _naturalExit = callback;

        public event Action<FirstFrameObservation>? FirstFrameObserved
        {
            add => _firstFrameObserved += value;
            remove => _firstFrameObserved -= value;
        }

        public void Dispose()
        {
            DisposeCalls++;
        }
    }
}
