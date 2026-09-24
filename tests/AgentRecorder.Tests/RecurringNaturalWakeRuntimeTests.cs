using AgentRecorder.App;
using AgentRecorder.Capture;
using AgentRecorder.Core;
using AgentRecorder.Core.Automation;
using AgentRecorder.Infrastructure;
using AgentRecorder.Logging;
using AgentRecorder.Persistence;
using Microsoft.Win32;
using System.Text;
using Xunit;

namespace AgentRecorder.Tests;

[Collection("NonParallel-AgentRecorderDataDir")]
public sealed class RecurringNaturalWakeRuntimeTests
{
    [Fact]
    public async Task SchedulerRunsAdvancementBeforeReadyCandidateQueryAndDispatch()
    {
        using var database = new TestDatabase();
        var candidate = DueCandidate("advance-before-query");
        var order = new System.Collections.Concurrent.ConcurrentQueue<string>();
        var dispatched = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var scheduler = new RecurringLeaseNaturalWakeScheduler(
            database.Store,
            RejectedDispatcher(),
            () => false,
            () => "S-1",
            () => "session-1",
            waitForTest: (_, token) => Task.Delay(Timeout.InfiniteTimeSpan, token),
            candidateSourceForTest: (_, _, _, _) =>
            {
                order.Enqueue("query");
                return new RecurringOccurrenceNaturalWakeCandidatePage(new[] { candidate }, false);
            },
            dispatchForTest: (_, _) =>
            {
                order.Enqueue("dispatch");
                dispatched.TrySetResult(null);
                return Task.FromResult(RecurringOccurrenceNaturalWakeDispatchResult.Rejected(candidate, "test_rejected"));
            },
            advanceBeforeQuery: (_, _, _, _) =>
            {
                order.Enqueue("advance");
                return Task.FromResult(true);
            });

        Assert.True(scheduler.Start());
        await dispatched.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(new[] { "advance", "query", "dispatch" }, order.ToArray());
    }

    [Fact]
    public async Task SchedulerDispatchesStartedLifecycleExactlyOnceWithoutDisposingTheOwner()
    {
        using var database = new TestDatabase();
        var candidate = DueCandidate("scheduler-started");
        var lifecycle = new TestLifecycleSession();
        var dispatchSeen = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var remaining = 1;

        var dispatcher = RejectedDispatcher();
        using var scheduler = new RecurringLeaseNaturalWakeScheduler(
            database.Store,
            dispatcher,
            () => false,
            () => "S-1",
            () => "session-1",
            utcNow: () => DateTimeOffset.UtcNow,
            waitForTest: (_, token) => Task.Delay(Timeout.InfiniteTimeSpan, token),
            candidateSourceForTest: (_, _, _, _) =>
                Interlocked.Exchange(ref remaining, 0) == 1
                    ? new RecurringOccurrenceNaturalWakeCandidatePage(new[] { candidate }, false)
                    : new RecurringOccurrenceNaturalWakeCandidatePage(
                        Array.Empty<RecurringOccurrenceNaturalWakeCandidate>(), false),
            dispatchForTest: (value, _) =>
            {
                dispatchSeen.TrySetResult(null);
                var execution = RecurringOccurrenceExecutionResult
                    .ForRequest(value.LeaseId, value.Slot.OccurrenceIdentity)
                    .WithStatus(
                        RecurringOccurrenceExecutionStatus.Started,
                        "recurring_execution_started",
                        lifecycleSession: lifecycle);
                return Task.FromResult(
                    RecurringOccurrenceNaturalWakeDispatchResult.FromCoordinator(value, execution));
            });

        Assert.True(scheduler.Start());
        await dispatchSeen.Task.WaitAsync(TimeSpan.FromSeconds(2));
        scheduler.Dispose();

        Assert.Equal(0, lifecycle.DisposeCalls);
        Assert.False(scheduler.IsStarted);
    }

    [Fact]
    public async Task SchedulerWaitsForAHeldDispatchGateBeforeDispatching()
    {
        using var database = new TestDatabase();
        var candidate = DueCandidate("gate-wait");
        var candidateSeen = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var dispatchSeen = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);

        using var scheduler = new RecurringLeaseNaturalWakeScheduler(
            database.Store,
            RejectedDispatcher(),
            () => false,
            () => "S-1",
            () => "session-1",
            waitForTest: (_, token) => Task.Delay(Timeout.InfiniteTimeSpan, token),
            candidateSourceForTest: (_, _, _, _) =>
            {
                candidateSeen.TrySetResult(null);
                return new RecurringOccurrenceNaturalWakeCandidatePage(new[] { candidate }, false);
            },
            dispatchForTest: (_, _) =>
            {
                dispatchSeen.TrySetResult(null);
                return Task.FromResult(RecurringOccurrenceNaturalWakeDispatchResult.Rejected(
                    candidate,
                    "gate_test_rejected"));
            });

        await scheduler.HoldDispatchGateForTestsAsync();
        Assert.True(scheduler.Start());
        await candidateSeen.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await Task.Delay(50);
        Assert.False(dispatchSeen.Task.IsCompleted);

        scheduler.ReleaseDispatchGateForTests();
        await dispatchSeen.Task.WaitAsync(TimeSpan.FromSeconds(2));
    }

    [Fact]
    public async Task SchedulerDisposeReturnsBoundedAndReleasesResourcesAfterSlowDispatchExactlyOnce()
    {
        using var database = new TestDatabase();
        var candidate = DueCandidate("slow-dispatch-dispose");
        var dispatchSeen = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseDispatch = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);

        using var scheduler = new RecurringLeaseNaturalWakeScheduler(
            database.Store,
            RejectedDispatcher(),
            () => false,
            () => "S-1",
            () => "session-1",
            waitForTest: (_, token) => Task.Delay(Timeout.InfiniteTimeSpan, token),
            candidateSourceForTest: (_, _, _, _) =>
                new RecurringOccurrenceNaturalWakeCandidatePage(new[] { candidate }, false),
            dispatchForTest: async (_, _) =>
            {
                dispatchSeen.TrySetResult(null);
                await releaseDispatch.Task.ConfigureAwait(false);
                return RecurringOccurrenceNaturalWakeDispatchResult.Rejected(
                    candidate,
                    "slow_dispatch_rejected");
            },
            disposeWaitForTest: TimeSpan.FromMilliseconds(50));

        Assert.True(scheduler.Start());
        await dispatchSeen.Task.WaitAsync(TimeSpan.FromSeconds(2));

        scheduler.Dispose();
        Assert.False(scheduler.SynchronizationResourcesDisposedForTests);

        releaseDispatch.TrySetResult(null);
        await scheduler.LoopTaskForTests!.WaitAsync(TimeSpan.FromSeconds(2));
        if (scheduler.ResourceCleanupTaskForTests is { } cleanup)
            await cleanup.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.True(scheduler.SynchronizationResourcesDisposedForTests);
        Assert.Equal(1, scheduler.SynchronizationResourceDisposeCountForTests);
        scheduler.Dispose();
        Assert.Equal(1, scheduler.SynchronizationResourceDisposeCountForTests);
    }

    [Fact]
    public async Task SchedulerDefersWhileEngineIsBusyAndSignalWakesTheNextPoll()
    {
        using var database = new TestDatabase();
        var candidate = DueCandidate("busy-deferred");
        var active = 1;
        var queryCount = 0;
        var dispatchSeen = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var busySeen = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);

        using var scheduler = new RecurringLeaseNaturalWakeScheduler(
            database.Store,
            RejectedDispatcher(),
            () => Volatile.Read(ref active) != 0,
            () => "S-1",
            () => "session-1",
            utcNow: () => DateTimeOffset.UtcNow,
            pollInterval: TimeSpan.FromMinutes(1),
            audit: (eventName, _) =>
            {
                if (eventName == "recurring_lease.scheduler_busy_deferred")
                    busySeen.TrySetResult(null);
            },
            candidateSourceForTest: (_, _, _, _) =>
            {
                Interlocked.Increment(ref queryCount);
                return new RecurringOccurrenceNaturalWakeCandidatePage(new[] { candidate }, false);
            },
            dispatchForTest: (_, _) =>
            {
                dispatchSeen.TrySetResult(null);
                return Task.FromResult(RecurringOccurrenceNaturalWakeDispatchResult.Rejected(
                    candidate,
                    "test_rejected"));
            });

        Assert.True(scheduler.Start());
        await busySeen.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(0, Volatile.Read(ref queryCount));

        Volatile.Write(ref active, 0);
        scheduler.Signal();
        await dispatchSeen.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(1, Volatile.Read(ref queryCount));
    }

    [Fact]
    public async Task SchedulerBacksOffAfterARejectedDispatchAndDoesNotHotRetry()
    {
        using var database = new TestDatabase();
        var candidate = DueCandidate("rejected-backoff");
        var dispatchCount = 0;
        var waitCount = 0;
        var backoffSeen = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);

        using var scheduler = new RecurringLeaseNaturalWakeScheduler(
            database.Store,
            RejectedDispatcher(),
            () => false,
            () => "S-1",
            () => "session-1",
            utcNow: () => DateTimeOffset.UtcNow,
            waitForTest: (_, token) =>
            {
                if (Interlocked.Increment(ref waitCount) == 1)
                    backoffSeen.TrySetResult(null);
                return Task.Delay(Timeout.InfiniteTimeSpan, token);
            },
            candidateSourceForTest: (_, _, _, _) =>
                new RecurringOccurrenceNaturalWakeCandidatePage(new[] { candidate }, false),
            dispatchForTest: (_, _) =>
            {
                Interlocked.Increment(ref dispatchCount);
                return Task.FromResult(RecurringOccurrenceNaturalWakeDispatchResult.Rejected(
                    candidate,
                    "test_rejected"));
            });

        Assert.True(scheduler.Start());
        await backoffSeen.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await Task.Delay(50);

        Assert.Equal(1, Volatile.Read(ref dispatchCount));
        Assert.Equal(1, Volatile.Read(ref waitCount));
    }

    [Fact]
    public async Task SchedulerConvertsQueryExceptionsToStableAuditAndContinuesBoundedly()
    {
        using var database = new TestDatabase();
        var waitSeen = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var audited = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var queryCount = 0;

        using var scheduler = new RecurringLeaseNaturalWakeScheduler(
            database.Store,
            RejectedDispatcher(),
            () => false,
            () => "S-1",
            () => "session-1",
            utcNow: () => DateTimeOffset.UtcNow,
            waitForTest: (_, token) =>
            {
                waitSeen.TrySetResult(null);
                return Task.Delay(Timeout.InfiniteTimeSpan, token);
            },
            audit: (eventName, payload) =>
            {
                if (eventName != "recurring_lease.scheduler_query_failed")
                    return;
                var reason = payload.GetType().GetProperty("reason_code")?.GetValue(payload)?.ToString();
                audited.TrySetResult(reason ?? "missing");
            },
            candidateSourceForTest: (_, _, _, _) =>
            {
                Interlocked.Increment(ref queryCount);
                throw new InvalidOperationException("test query failure");
            });

        Assert.True(scheduler.Start());
        Assert.Equal("recurring_natural_wake_tick_failed",
            await audited.Task.WaitAsync(TimeSpan.FromSeconds(2)));
        await waitSeen.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(1, Volatile.Read(ref queryCount));
    }

    [Fact]
    public void StartupRecoveryMustCompleteBeforeSchedulerStarts()
    {
        using var fixture = new RuntimeFixture();
        var recoveryCompleted = false;
        var schedulerObservedRecovery = false;
        var scheduler = fixture.CreateScheduler(
            candidateSourceForTest: (_, _, _, _) =>
            {
                schedulerObservedRecovery = recoveryCompleted;
                return new RecurringOccurrenceNaturalWakeCandidatePage(
                    Array.Empty<RecurringOccurrenceNaturalWakeCandidate>(), false);
            });

        using var runtime = fixture.CreateRuntime(
            scheduler,
            recoveryBatchForTest: (_, _, _) =>
            {
                Assert.False(scheduler.IsStarted);
                recoveryCompleted = true;
                return EmptyRecoveryBatch();
            });

        Assert.True(runtime.Start());
        Assert.True(
            runtime.ExecutionSupported,
            $"state={runtime.StartStateForTests}; events={runtime.SystemEventsRegisteredForTests}; scheduler={runtime.SchedulerStartedForTests}");
        SpinWait.SpinUntil(() => schedulerObservedRecovery, TimeSpan.FromSeconds(2));
        Assert.True(schedulerObservedRecovery);
        runtime.Dispose();
    }

    [Fact]
    public async Task RuntimeDisposeRacingStartupSerializesWithTheSingleStartAttempt()
    {
        using var fixture = new RuntimeFixture();
        var recoveryEntered = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseRecovery = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var scheduler = fixture.CreateScheduler();
        using var runtime = fixture.CreateRuntime(
            scheduler,
            recoveryBatchForTest: (_, _, _) =>
            {
                recoveryEntered.TrySetResult(null);
                releaseRecovery.Task.GetAwaiter().GetResult();
                return EmptyRecoveryBatch();
            });

        var startTask = Task.Run(runtime.Start);
        await recoveryEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var disposeTask = Task.Run(runtime.Dispose);
        await Task.Delay(50);
        Assert.False(disposeTask.IsCompleted);

        releaseRecovery.TrySetResult(null);
        Assert.True(await startTask);
        await disposeTask;
        Assert.False(runtime.ExecutionSupported);
        Assert.False(scheduler.IsStarted);
        Assert.True(scheduler.SynchronizationResourcesDisposedForTests);
    }

    [Fact]
    public async Task ProductionPathUsesPersistedCandidateThroughRuntimeAndSettlesOnceOnSharedLifecycleSignal()
    {
        const string sid = "S-1-5-21-task279r";
        var session = CaptureAuthorizationSessionBinding.Current;
        using var context = RecurringOccurrenceReservationTests.ReservationContext.Create(
            countdownSeconds: 0,
            approvalUserSid: sid,
            approvalSessionBinding: session);
        var now = context.Slot.ScheduledStartUtc!.Value;
        var interlock = new StandingLeaseStartSafetyInterlock();
        var environment = new RecurringOccurrenceReservationTests.MatchingReservationEnvironmentProvider();
        var auditPath = Path.Combine(context.Fixture.RootPath, "task279r-runtime-audit.jsonl");
        var audit = new AuditLogger(auditPath);
        var tray = new NoOpTray();
        var backend = new ProductionPathBackend();
        var validator = new SqliteRecurringLeaseCurrentSafetyValidator(context.Fixture.Store);
        using var engine = new RecordingEngine(
            audit,
            tracer: null,
            bundleGenerator: null,
            microphoneProvider: null,
            microphoneStatusProvider: null,
            displayTopologyProvider: null,
            systemAudioEndpointProvider: null,
            recurringStartSafetyInterlock: interlock,
            recurringStartSafetyValidator: validator.Validate,
            recurringEnvironmentProvider: environment);
        engine.SetTray(tray);
        engine.UtcNowForTests = () => now.UtcDateTime;
        engine.BackendFactory = _ => (backend, "test-recurring-backend");

        var before = CaptureDurableCounts(context);
        var candidateQuery = new RecurringOccurrenceNaturalWakeCandidateQuery(context.Fixture.Store);
        Assert.Single(candidateQuery.ListReady(now, sid, session, 4).Candidates);
        using var runtime = new RecurringLeaseNaturalWakeRuntime(
            context.Fixture.Store,
            engine,
            tray,
            audit,
            interlock,
            utcNowForTest: () => now,
            currentUserSidForTest: () => sid,
            sessionBindingForTest: () => session,
            recoveryBatchForTest: (_, _, _) => EmptyRecoveryBatch(),
            registerSystemLifecycleSignalsForTest: () => true,
            environmentProviderForTest: environment,
            pollIntervalForTest: TimeSpan.FromMilliseconds(10));
        using var standingRuntime = new StandingLeaseNaturalWakeRuntime(
            context.Fixture.Store,
            engine,
            tray,
            audit);

        Assert.True(runtime.Start());
        Assert.True(runtime.ExecutionSupported);
        var startCompleted = await Task.WhenAny(backend.Started, Task.Delay(TimeSpan.FromSeconds(5)));
        Assert.True(
            ReferenceEquals(backend.Started, startCompleted),
            $"backend did not start within 5 seconds. Audit diagnostic: {ReadAuditDiagnostic(auditPath)}");
        await Task.Delay(50);

        var afterStart = CaptureDurableCounts(context);
        Assert.Equal((1L, 0L, 0L), before);
        Assert.Equal((1L, 1L, 1L), afterStart);
        Assert.Equal(1, backend.StartCalls);
        Assert.Single(engine.List());
        Assert.Empty(new RecurringOccurrenceNaturalWakeCandidateQuery(context.Fixture.Store)
            .ListReady(now, sid, session, 4).Candidates);

        await Task.WhenAll(
            Task.Run(() => standingRuntime.HandleSessionSwitchForTests(SessionSwitchReason.SessionLock)),
            Task.Run(() => runtime.HandleSessionSwitchForTests(SessionSwitchReason.SessionLock)),
            Task.Run(() => standingRuntime.HandlePowerModeChangedForTests(PowerModes.Suspend)),
            Task.Run(() => runtime.HandlePowerModeChangedForTests(PowerModes.Suspend)),
            Task.Run(() => standingRuntime.HandleSessionSwitchForTests(SessionSwitchReason.ConsoleDisconnect)),
            Task.Run(() => runtime.HandleSessionSwitchForTests(SessionSwitchReason.ConsoleDisconnect)));
        await backend.Disposed.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(1, backend.StopCalls);
        Assert.Equal(0, backend.CancelCalls);
        Assert.Equal(1, backend.DisposeCalls);
        Assert.Equal(RecordingRunStatus.SessionInterrupted,
            new SqliteRecordingRunRepository(context.Fixture.Store).Get(
                ReadText(context.Fixture, "SELECT id FROM recording_runs LIMIT 1;")).Status);
        Assert.Equal(PlanOccurrenceStatus.Blocked,
            new SqlitePlanOccurrenceRepository(context.Fixture.Store).Get(context.Slot.OccurrenceId!).Status);

        standingRuntime.HandlePowerModeChangedForTests(PowerModes.Suspend);
        runtime.HandlePowerModeChangedForTests(PowerModes.Suspend);
        standingRuntime.HandleSessionSwitchForTests(SessionSwitchReason.ConsoleDisconnect);
            runtime.HandleSessionSwitchForTests(SessionSwitchReason.ConsoleDisconnect);
        Assert.Equal(1, backend.StopCalls);
        Assert.Equal(1, backend.DisposeCalls);
        Assert.Equal(afterStart, CaptureDurableCounts(context));
    }

    [Fact]
    public async Task NoPreseededOccurrenceSetupIsAdvancedAndStartedOnceByProductionRuntime()
    {
        using var setup = new RecurringPlanSetupTestFixture();
        const string intentId = "task286-no-preseeded-occurrence";
        var session = CaptureAuthorizationSessionBinding.Current;
        setup.Create(intentId, prepared: true,
            sid: RecurringPlanSetupTestFixture.Sid, session: session);
        setup.Activate(intentId, RecurringPlanSetupTestFixture.Sid, session);
        Assert.Equal(RecurringSetupIntentStatus.Activated, setup.Query.Get(intentId,
            RecurringPlanSetupTestFixture.Sid, session)!.Status);
        Assert.Equal(0, setup.Scalar("SELECT COUNT(*) FROM plan_occurrences;"));
        Assert.Equal(0, setup.Scalar("SELECT COUNT(*) FROM recurring_occurrence_slots;"));
        Assert.Equal(0, setup.Scalar("SELECT COUNT(*) FROM recurring_advancement_operations;"));

        var now = RecurringPlanSetupTestFixture.At(5_399); // 01:29:59Z, one second before 09:30 China time.
        var interlock = new StandingLeaseStartSafetyInterlock();
        var audit = new AuditLogger(Path.Combine(setup.Root, "task286-runtime-audit.jsonl"));
        var tray = new NoOpTray();
        var backend = new ProductionPathBackend();
        var environment = new RecurringOccurrenceReservationTests.MatchingReservationEnvironmentProvider();
        var validator = new SqliteRecurringLeaseCurrentSafetyValidator(setup.Store);
        using var engine = new RecordingEngine(
            audit,
            tracer: null,
            bundleGenerator: null,
            microphoneProvider: null,
            microphoneStatusProvider: null,
            displayTopologyProvider: null,
            systemAudioEndpointProvider: null,
            recurringStartSafetyInterlock: interlock,
            recurringStartSafetyValidator: validator.Validate,
            recurringEnvironmentProvider: environment);
        engine.SetTray(tray);
        engine.UtcNowForTests = () => now.UtcDateTime;
        engine.BackendFactory = _ => (backend, "task286-no-preseeded-occurrence-backend");

        using var runtime = new RecurringLeaseNaturalWakeRuntime(
            setup.Store,
            engine,
            tray,
            audit,
            interlock,
            utcNowForTest: () => now,
            currentUserSidForTest: () => RecurringPlanSetupTestFixture.Sid,
            sessionBindingForTest: () => session,
            recoveryBatchForTest: (_, _, _) => EmptyRecoveryBatch(),
            registerSystemLifecycleSignalsForTest: () => true,
            environmentProviderForTest: environment,
            pollIntervalForTest: TimeSpan.FromMilliseconds(10));

        Assert.True(runtime.Start(), "no preseeded occurrence: production runtime must start after bounded advancement");
        Assert.True(runtime.ExecutionSupported, "no preseeded occurrence: capability requires healthy advancement and natural wake");
        Assert.True(runtime.AdvancementHealthyForTests, "no preseeded occurrence: initial advancement pass must be healthy");
        Assert.Equal(1, setup.Scalar("SELECT COUNT(*) FROM recurring_occurrence_slots;"));
        Assert.Equal(1, setup.Scalar("SELECT COUNT(*) FROM recurring_advancement_operations;"));
        Assert.Equal(1L, long.Parse(setup.Text("SELECT version FROM recurring_schedule_cursors LIMIT 1;"), System.Globalization.CultureInfo.InvariantCulture));
        Assert.Equal(0, backend.StartCalls);
        var occurrenceId = setup.Text("SELECT occurrence_id FROM recurring_occurrence_slots LIMIT 1;");
        var occurrence = new SqlitePlanOccurrenceRepository(setup.Store).Get(occurrenceId);
        Assert.Equal(PlanOccurrenceStatus.Scheduled, occurrence.Status);
        Assert.Equal(0, new RecurringOccurrenceNaturalWakeCandidateQuery(setup.Store)
            .ListReady(now, RecurringPlanSetupTestFixture.Sid, session, 4).Candidates.Count);

        runtime.Dispose();
        using var restartedRuntime = new RecurringLeaseNaturalWakeRuntime(
            setup.Store,
            engine,
            tray,
            audit,
            interlock,
            utcNowForTest: () => now,
            currentUserSidForTest: () => RecurringPlanSetupTestFixture.Sid,
            sessionBindingForTest: () => session,
            recoveryBatchForTest: (_, _, _) => EmptyRecoveryBatch(),
            registerSystemLifecycleSignalsForTest: () => true,
            environmentProviderForTest: environment,
            pollIntervalForTest: TimeSpan.FromMilliseconds(10));
        Assert.True(restartedRuntime.Start(), "no preseeded occurrence: restart must recover the existing cursor idempotently");
        Assert.Equal(1, setup.Scalar("SELECT COUNT(*) FROM recurring_occurrence_slots;"));
        Assert.Equal(1, setup.Scalar("SELECT COUNT(*) FROM recurring_advancement_operations;"));
        Assert.Equal(1L, long.Parse(setup.Text("SELECT version FROM recurring_schedule_cursors LIMIT 1;"), System.Globalization.CultureInfo.InvariantCulture));

        now = RecurringPlanSetupTestFixture.At(5_400);
        var startCompleted = await Task.WhenAny(backend.Started, Task.Delay(TimeSpan.FromSeconds(5)));
        Assert.True(ReferenceEquals(backend.Started, startCompleted),
            $"no preseeded occurrence: materialized occurrence was not dispatched. Audit: {ReadAuditDiagnostic(Path.Combine(setup.Root, "task286-runtime-audit.jsonl"))}");
        Assert.Equal(1, backend.StartCalls);
        Assert.Equal(1, setup.Scalar("SELECT COUNT(*) FROM recording_runs;"));
        Assert.Equal(1, setup.Scalar("SELECT COUNT(*) FROM recurring_lease_uses;"));
        Assert.Equal(1, setup.Scalar("SELECT COUNT(*) FROM recurring_occurrence_execution_specs;"));

        var recordingId = setup.Text("SELECT id FROM recording_runs LIMIT 1;");
        _ = engine.Stop(recordingId, "user_requested");
        await backend.Disposed.WaitAsync(TimeSpan.FromSeconds(5));
        var materializationDeadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (setup.Scalar("SELECT COUNT(*) FROM recurring_occurrence_slots;") < 2 &&
               DateTime.UtcNow < materializationDeadline)
            await Task.Delay(10);
        Assert.Equal(2, setup.Scalar("SELECT COUNT(*) FROM recurring_occurrence_slots;"));
        var countsAfterSettlement = (
            setup.Scalar("SELECT COUNT(*) FROM recording_runs;"),
            setup.Scalar("SELECT COUNT(*) FROM recurring_lease_uses;"),
            setup.Scalar("SELECT COUNT(*) FROM recurring_occurrence_execution_specs;"));
        Assert.Equal((1L, 1L, 1L), countsAfterSettlement);
        Assert.Equal(1, backend.StopCalls);
        Assert.Equal(1, backend.DisposeCalls);
        Assert.Equal(RecordingRunStatus.SessionInterrupted,
            new SqliteRecordingRunRepository(setup.Store).Get(
                setup.Text("SELECT id FROM recording_runs LIMIT 1;")).Status);
        Assert.Equal(PlanOccurrenceStatus.Blocked,
            new SqlitePlanOccurrenceRepository(setup.Store).Get(occurrenceId).Status);
        Assert.Equal(2L, long.Parse(setup.Text("SELECT version FROM recurring_schedule_cursors LIMIT 1;"), System.Globalization.CultureInfo.InvariantCulture));
        Assert.Equal("scheduled", setup.Text("SELECT slot_status_code FROM recurring_occurrence_slots ORDER BY local_date DESC LIMIT 1;"));
        _ = engine.Stop(recordingId, "duplicate_user_requested");
        Assert.Equal(countsAfterSettlement, (
            setup.Scalar("SELECT COUNT(*) FROM recording_runs;"),
            setup.Scalar("SELECT COUNT(*) FROM recurring_lease_uses;"),
            setup.Scalar("SELECT COUNT(*) FROM recurring_occurrence_execution_specs;")));
        Assert.Equal(1, backend.StopCalls);
        Assert.Equal(1, backend.DisposeCalls);
    }

    [Fact]
    public async Task ApprovalAfterRuntimeStartIsDiscoveredWithoutAnExplicitSignal()
    {
        using var setup = new RecurringPlanSetupTestFixture();
        var session = CaptureAuthorizationSessionBinding.Current;
        var now = RecurringPlanSetupTestFixture.At(5_399);
        var interlock = new StandingLeaseStartSafetyInterlock();
        var audit = new AuditLogger(Path.Combine(setup.Root, "task286-late-approval-audit.jsonl"));
        var tray = new NoOpTray();
        var backend = new ProductionPathBackend();
        var environment = new RecurringOccurrenceReservationTests.MatchingReservationEnvironmentProvider();
        var validator = new SqliteRecurringLeaseCurrentSafetyValidator(setup.Store);
        using var engine = new RecordingEngine(
            audit,
            tracer: null,
            bundleGenerator: null,
            microphoneProvider: null,
            microphoneStatusProvider: null,
            displayTopologyProvider: null,
            systemAudioEndpointProvider: null,
            recurringStartSafetyInterlock: interlock,
            recurringStartSafetyValidator: validator.Validate,
            recurringEnvironmentProvider: environment);
        engine.SetTray(tray);
        engine.UtcNowForTests = () => now.UtcDateTime;
        engine.BackendFactory = _ => (backend, "task286-late-approval-backend");
        using var runtime = new RecurringLeaseNaturalWakeRuntime(
            setup.Store,
            engine,
            tray,
            audit,
            interlock,
            utcNowForTest: () => now,
            currentUserSidForTest: () => RecurringPlanSetupTestFixture.Sid,
            sessionBindingForTest: () => session,
            recoveryBatchForTest: (_, _, _) => EmptyRecoveryBatch(),
            registerSystemLifecycleSignalsForTest: () => true,
            environmentProviderForTest: environment,
            pollIntervalForTest: TimeSpan.FromMilliseconds(25));

        Assert.True(runtime.Start());
        const string intentId = "task286-approved-after-runtime-start";
        setup.Create(intentId, prepared: true,
            sid: RecurringPlanSetupTestFixture.Sid, session: session);
        setup.Activate(intentId, RecurringPlanSetupTestFixture.Sid, session);
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(1);
        while (setup.Scalar("SELECT COUNT(*) FROM recurring_occurrence_slots;") == 0 && DateTime.UtcNow < deadline)
            await Task.Delay(5);

        Assert.Equal(1, setup.Scalar("SELECT COUNT(*) FROM recurring_occurrence_slots;"));
        Assert.Equal(1, setup.Scalar("SELECT COUNT(*) FROM recurring_advancement_operations;"));
        Assert.Equal(0, backend.StartCalls);
    }

    [Fact]
    public async Task LateNoPreseededOccurrenceIsMissedBeforeTheNextSlotAndNeverStartsBackend()
    {
        using var setup = new RecurringPlanSetupTestFixture();
        const string intentId = "task286-late-no-preseeded-occurrence";
        var session = CaptureAuthorizationSessionBinding.Current;
        setup.Create(intentId, prepared: true,
            sid: RecurringPlanSetupTestFixture.Sid, session: session);
        setup.Activate(intentId, RecurringPlanSetupTestFixture.Sid, session);

        var now = RecurringPlanSetupTestFixture.At(5_701); // One second beyond the first day's five-minute grace window.
        var interlock = new StandingLeaseStartSafetyInterlock();
        var auditPath = Path.Combine(setup.Root, "task286-late-runtime-audit.jsonl");
        var audit = new AuditLogger(auditPath);
        var tray = new NoOpTray();
        var backend = new ProductionPathBackend();
        var environment = new RecurringOccurrenceReservationTests.MatchingReservationEnvironmentProvider();
        var validator = new SqliteRecurringLeaseCurrentSafetyValidator(setup.Store);
        using var engine = new RecordingEngine(
            audit,
            tracer: null,
            bundleGenerator: null,
            microphoneProvider: null,
            microphoneStatusProvider: null,
            displayTopologyProvider: null,
            systemAudioEndpointProvider: null,
            recurringStartSafetyInterlock: interlock,
            recurringStartSafetyValidator: validator.Validate,
            recurringEnvironmentProvider: environment);
        engine.SetTray(tray);
        engine.UtcNowForTests = () => now.UtcDateTime;
        engine.BackendFactory = _ => (backend, "task286-late-no-preseeded-occurrence-backend");

        using var runtime = new RecurringLeaseNaturalWakeRuntime(
            setup.Store,
            engine,
            tray,
            audit,
            interlock,
            utcNowForTest: () => now,
            currentUserSidForTest: () => RecurringPlanSetupTestFixture.Sid,
            sessionBindingForTest: () => session,
            recoveryBatchForTest: (_, _, _) => EmptyRecoveryBatch(),
            registerSystemLifecycleSignalsForTest: () => true,
            environmentProviderForTest: environment,
            pollIntervalForTest: TimeSpan.FromMilliseconds(10));

        Assert.True(runtime.Start(), "late no-preseeded occurrence: startup must materialize the durable first slot");
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while ((setup.Scalar("SELECT COUNT(*) FROM recurring_occurrence_slots;") < 2 ||
                setup.Scalar("SELECT COUNT(*) FROM plan_occurrences WHERE status_code = 'missed';") != 1) &&
               DateTime.UtcNow < deadline)
            await Task.Delay(10);

        Assert.Equal(2, setup.Scalar("SELECT COUNT(*) FROM recurring_occurrence_slots;"));
        Assert.Equal(1, setup.Scalar("SELECT COUNT(*) FROM plan_occurrences WHERE status_code = 'missed';"));
        Assert.Equal(1, setup.Scalar("SELECT COUNT(*) FROM plan_occurrences WHERE status_code = 'scheduled';"));
        Assert.Equal(2L, long.Parse(setup.Text("SELECT version FROM recurring_schedule_cursors LIMIT 1;"), System.Globalization.CultureInfo.InvariantCulture));
        Assert.Equal(0, backend.StartCalls);
        Assert.Equal(0, setup.Scalar("SELECT COUNT(*) FROM recording_runs;"));
        Assert.Contains("missed", ReadAuditDiagnostic(auditPath), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void AdvancementCandidateRequiresCurrentIdentitySafetyLeaseAndEnabledPlan()
    {
        using var setup = new RecurringPlanSetupTestFixture();
        const string intentId = "task286-candidate-eligibility";
        var session = CaptureAuthorizationSessionBinding.Current;
        setup.Create(intentId, prepared: true,
            sid: RecurringPlanSetupTestFixture.Sid, session: session);
        setup.Activate(intentId, RecurringPlanSetupTestFixture.Sid, session);
        var query = new RecurringAdvancementCandidateQuery(setup.Store);
        var now = RecurringPlanSetupTestFixture.At(11);

        var eligible = query.ListPage(now, RecurringPlanSetupTestFixture.Sid, session, null, null, 8);
        var candidate = Assert.Single(eligible.Candidates);
        Assert.Equal(RecurringPlanSetupTestFixture.At(10), candidate.InitialAfterUtc);
        Assert.Equal(0, candidate.ExpectedCursorVersion);
        Assert.Empty(query.ListPage(now, "S-1-5-21-other", session, null, null, 8).Candidates);
        Assert.Empty(query.ListPage(now, RecurringPlanSetupTestFixture.Sid, "other-session", null, null, 8).Candidates);

        setup.Execute("UPDATE unattended_safety_state SET unattended_mode_code = 'disabled';");
        Assert.Empty(query.ListPage(now, RecurringPlanSetupTestFixture.Sid, session, null, null, 8).Candidates);
        setup.Execute("UPDATE unattended_safety_state SET unattended_mode_code = 'enabled';");
        Assert.Empty(query.ListPage(RecurringPlanSetupTestFixture.At(172_800),
            RecurringPlanSetupTestFixture.Sid, session, null, null, 8).Candidates);

        var setupRecord = setup.Query.Get(intentId, RecurringPlanSetupTestFixture.Sid, session)!;
        var plans = new SqlitePlanDefinitionRepository(setup.Store);
        var plan = plans.Get(setupRecord.PlanId!);
        Assert.True(plan.TryTransition(PlanDefinitionStatus.Paused, now.AddSeconds(1)).Succeeded);
        plans.Update(plan, plan.Version - 1);
        Assert.Empty(query.ListPage(now.AddSeconds(2), RecurringPlanSetupTestFixture.Sid, session, null, null, 8).Candidates);

        const string revokedIntentId = "task286-candidate-revoked-lease";
        setup.Create(revokedIntentId, prepared: true,
            sid: RecurringPlanSetupTestFixture.Sid, session: session);
        setup.Activate(revokedIntentId, RecurringPlanSetupTestFixture.Sid, session);
        Assert.Single(query.ListPage(now.AddSeconds(3), RecurringPlanSetupTestFixture.Sid, session, null, null, 8).Candidates);
        var revokedLeaseId = setup.Query.Get(revokedIntentId, RecurringPlanSetupTestFixture.Sid, session)!.LeaseId!;
        var revoke = new StandingLeaseSafetyControlService(
            setup.Store, () => now.AddSeconds(4)).RevokeRecurringLease(
                revokedLeaseId, "task286-revoke-lease", "task286_test");
        Assert.Equal(StandingLeaseSafetyControlResultStatus.Changed, revoke.Status);
        Assert.Empty(query.ListPage(now.AddSeconds(5), RecurringPlanSetupTestFixture.Sid, session, null, null, 8).Candidates);
    }

    [Fact]
    public async Task ConcurrentAdvancementRuntimesMaterializeOnlyOneOccurrenceAndCursorTransition()
    {
        using var setup = new RecurringPlanSetupTestFixture();
        const string intentId = "task286-concurrent-advancement";
        var session = CaptureAuthorizationSessionBinding.Current;
        setup.Create(intentId, prepared: true,
            sid: RecurringPlanSetupTestFixture.Sid, session: session);
        setup.Activate(intentId, RecurringPlanSetupTestFixture.Sid, session);
        var first = new RecurringScheduleAdvancementRuntime(setup.Store, (_, _) => { });
        var second = new RecurringScheduleAdvancementRuntime(setup.Store, (_, _) => { });
        var now = RecurringPlanSetupTestFixture.At(20);

        var outcomes = await Task.WhenAll(
            Task.Run(() => first.RunStartupPass(now, RecurringPlanSetupTestFixture.Sid, session)),
            Task.Run(() => second.RunStartupPass(now, RecurringPlanSetupTestFixture.Sid, session)));

        Assert.All(outcomes, outcome => Assert.True(outcome));
        Assert.Equal(1, setup.Scalar("SELECT COUNT(*) FROM recurring_occurrence_slots;"));
        Assert.Equal(1, setup.Scalar("SELECT COUNT(*) FROM recurring_advancement_operations;"));
        Assert.Equal(1L, long.Parse(setup.Text("SELECT version FROM recurring_schedule_cursors LIMIT 1;"), System.Globalization.CultureInfo.InvariantCulture));
    }

    [Fact]
    public void CorruptActivatedPreparationBlocksRuntimeCapabilityWithStableAudit()
    {
        using var setup = new RecurringPlanSetupTestFixture();
        const string intentId = "task286-corrupt-activated-chain";
        var session = CaptureAuthorizationSessionBinding.Current;
        setup.Create(intentId, prepared: true,
            sid: RecurringPlanSetupTestFixture.Sid, session: session);
        setup.Activate(intentId, RecurringPlanSetupTestFixture.Sid, session);
        setup.Corrupt("UPDATE recurring_setup_preparations SET lease_id = 'missing-task286-lease' WHERE intent_id = 'task286-corrupt-activated-chain';");

        var interlock = new StandingLeaseStartSafetyInterlock();
        var tray = new NoOpTray();
        using var engine = new RecordingEngine(setup.Audit);
        engine.SetTray(tray);
        using var runtime = new RecurringLeaseNaturalWakeRuntime(
            setup.Store,
            engine,
            tray,
            setup.Audit,
            interlock,
            utcNowForTest: () => RecurringPlanSetupTestFixture.At(11),
            currentUserSidForTest: () => RecurringPlanSetupTestFixture.Sid,
            sessionBindingForTest: () => session,
            recoveryBatchForTest: (_, _, _) => EmptyRecoveryBatch(),
            registerSystemLifecycleSignalsForTest: () => true);

        Assert.False(runtime.Start());
        Assert.False(runtime.ExecutionSupported);
        Assert.False(runtime.SchedulerStartedForTests);
        Assert.Contains(setup.Audit.Events, value => value.Contains("recurring_lease.advancement_query_failed", StringComparison.Ordinal));
        Assert.Contains(setup.Audit.Events, value => value.Contains("recurring_advancement_candidate_query_failed", StringComparison.Ordinal));
    }

    [Fact]
    public async Task NaturalWakeQueryFailureWithdrawsRecurringExecutionCapability()
    {
        using var database = new TestDatabase();
        var audit = new AuditLogger(Path.Combine(database.Root, "task286-health-audit.jsonl"));
        var queryFailed = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var scheduler = new RecurringLeaseNaturalWakeScheduler(
            database.Store,
            RejectedDispatcher(),
            () => false,
            () => "S-1-5-21-task286-health",
            () => "task286-health-session",
            utcNow: () => DateTimeOffset.UtcNow,
            waitForTest: (_, token) => Task.Delay(Timeout.InfiniteTimeSpan, token),
            audit: (eventName, _) =>
            {
                if (eventName == "recurring_lease.scheduler_query_failed")
                    queryFailed.TrySetResult(null);
            },
            candidateSourceForTest: (_, _, _, _) => throw new InvalidOperationException("injected natural-wake query failure"));
        var tray = new NoOpTray();
        using var engine = new RecordingEngine(audit);
        engine.SetTray(tray);
        using var runtime = new RecurringLeaseNaturalWakeRuntime(
            database.Store,
            engine,
            tray,
            audit,
            new StandingLeaseStartSafetyInterlock(),
            utcNowForTest: () => DateTimeOffset.UtcNow,
            currentUserSidForTest: () => "S-1-5-21-task286-health",
            sessionBindingForTest: () => "task286-health-session",
            recoveryBatchForTest: (_, _, _) => EmptyRecoveryBatch(),
            schedulerForTest: scheduler,
            registerSystemLifecycleSignalsForTest: () => true);

        Assert.True(runtime.Start());
        await queryFailed.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.False(runtime.ExecutionSupported);
        Assert.False(scheduler.IsHealthy);
    }

    [Fact]
    public void AuditDiagnosticReadsWhileWriterOwnsFileAndMissingFileRemainsNonFatal()
    {
        var path = Path.Combine(Path.GetTempPath(), $"AgentRecorderAuditDiagnostic_{Guid.NewGuid():N}.jsonl");
        var missingPath = path + ".missing";
        try
        {
            using (var writer = new FileStream(path, FileMode.CreateNew, FileAccess.Write,
                FileShare.Read))
            {
                var partialRecord = Encoding.UTF8.GetBytes("{\"event\":\"writer-active\"");
                writer.Write(partialRecord);
                writer.Flush();
                Assert.Contains("writer-active", ReadAuditDiagnostic(path), StringComparison.Ordinal);
            }

            Assert.Contains("unavailable", ReadAuditDiagnostic(missingPath), StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            try { if (File.Exists(path)) File.Delete(path); } catch { }
        }
    }

    private static string ReadAuditDiagnostic(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            var contents = reader.ReadToEnd();
            return string.IsNullOrWhiteSpace(contents) ? "audit file is empty or incomplete" : contents;
        }
        catch (Exception exception)
        {
            return $"audit diagnostic unavailable ({exception.GetType().Name}: {exception.Message})";
        }
    }

    [Fact]
    public void RecoveryFailureFailsClosedAndNeverStartsTheScheduler()
    {
        using var fixture = new RuntimeFixture();
        var queryCount = 0;
        var scheduler = fixture.CreateScheduler(
            candidateSourceForTest: (_, _, _, _) =>
            {
                Interlocked.Increment(ref queryCount);
                return new RecurringOccurrenceNaturalWakeCandidatePage(
                    Array.Empty<RecurringOccurrenceNaturalWakeCandidate>(), false);
            });

        using var runtime = fixture.CreateRuntime(
            scheduler,
            recoveryBatchForTest: (_, _, _) =>
                RecurringLeaseStartupRecoveryBatchResult.QueryFailed(
                    "recovery_candidate_query_failed"));

        Assert.False(runtime.Start());
        Assert.False(runtime.ExecutionSupported);
        Assert.False(scheduler.IsStarted);
        Assert.Equal(0, Volatile.Read(ref queryCount));
        Assert.False(runtime.Start());
        Assert.Equal(0, Volatile.Read(ref queryCount));
        runtime.Dispose();
    }

    [Fact]
    public void RecoveryBacklogBeyondTheBoundedCapFailsClosed()
    {
        using var fixture = new RuntimeFixture();
        var recoveryCalls = 0;
        var scheduler = fixture.CreateScheduler();

        using var runtime = fixture.CreateRuntime(
            scheduler,
            recoveryBatchForTest: (_, _, _) =>
            {
                Interlocked.Increment(ref recoveryCalls);
                return new RecurringLeaseStartupRecoveryBatchResult(
                    scanned: 1,
                    recovered: 1,
                    alreadyReconciled: 0,
                    failed: 0,
                    hasMore: true,
                    failureReason: null,
                    outcomes: new[] { RecurringLeaseRestartRecoveryResult.Rejected("test_outcome") });
            });

        Assert.False(runtime.Start());
        Assert.False(runtime.ExecutionSupported);
        Assert.Equal(RecurringLeaseNaturalWakeRuntime.MaxStartupRecoveryBatches,
            Volatile.Read(ref recoveryCalls));
        Assert.False(scheduler.IsStarted);
        Assert.False(runtime.Start());
        Assert.Equal(RecurringLeaseNaturalWakeRuntime.MaxStartupRecoveryBatches,
            Volatile.Read(ref recoveryCalls));
        runtime.Dispose();
    }

    [Fact]
    public void InvalidStartupIdentityFailsClosedBeforeRecoveryQuery()
    {
        using var fixture = new RuntimeFixture();
        var recoveryCalls = 0;
        var scheduler = fixture.CreateScheduler();

        using var runtime = fixture.CreateRuntime(
            scheduler,
            currentUserSid: " ",
            recoveryBatchForTest: (_, _, _) =>
            {
                Interlocked.Increment(ref recoveryCalls);
                return EmptyRecoveryBatch();
            });

        Assert.False(runtime.Start());
        Assert.False(runtime.ExecutionSupported);
        Assert.Equal(0, Volatile.Read(ref recoveryCalls));
        Assert.False(scheduler.IsStarted);
        runtime.Dispose();
    }

    private static RecurringOccurrenceNaturalWakeDispatcher RejectedDispatcher() =>
        new(
            (_, _) => Task.FromResult(RecurringOccurrenceExecutionResult.Rejected("test_dispatcher_unused")),
            auditForTest: (_, _) => { });

    private static RecurringLeaseStartupRecoveryBatchResult EmptyRecoveryBatch() =>
        new(
            scanned: 0,
            recovered: 0,
            alreadyReconciled: 0,
            failed: 0,
            hasMore: false,
            failureReason: null,
            outcomes: Array.Empty<RecurringLeaseRestartRecoveryResult>());

    private static RecurringOccurrenceNaturalWakeCandidate DueCandidate(string identity) =>
        new(
            "lease-1",
            new PeriodicOccurrenceDueCandidate(
                "plan-1",
                1,
                "digest-1",
                identity,
                DateTimeOffset.UtcNow.AddSeconds(-1),
                DateTimeOffset.UtcNow.AddMinutes(1),
                DateTimeOffset.UtcNow.AddMinutes(2),
            1),
            "due");

    private static (long SpecCount, long RunCount, long UseCount) CaptureDurableCounts(
        RecurringOccurrenceReservationTests.ReservationContext context) =>
        (
            Count(context.Fixture, "SELECT COUNT(*) FROM recurring_occurrence_execution_specs;"),
            Count(context.Fixture, "SELECT COUNT(*) FROM recording_runs;"),
            Count(context.Fixture, "SELECT COUNT(*) FROM recurring_lease_uses;"));

    private static long Count(RecurringLeaseFixture fixture, string sql) =>
        Convert.ToInt64(fixture.Scalar(sql));

    private static string ReadText(RecurringLeaseFixture fixture, string sql) =>
        Convert.ToString(fixture.Scalar(sql))!;

    private sealed class RuntimeFixture : IDisposable
    {
        private readonly string _root = Path.Combine(
            Path.GetTempPath(),
            "agent-recorder-task279-" + Guid.NewGuid().ToString("N"));

        internal RuntimeFixture()
        {
            Directory.CreateDirectory(_root);
            Store = new SqliteOperationalStore(Path.Combine(
                _root,
                SqliteOperationalStore.DatabaseFileName));
            Store.Initialize();
            Audit = new AuditLogger(Path.Combine(_root, "audit.jsonl"));
            Engine = new RecordingEngine(Audit);
            Tray = new NoOpTray();
            Engine.SetTray(Tray);
        }

        internal SqliteOperationalStore Store { get; }
        internal AuditLogger Audit { get; }
        internal RecordingEngine Engine { get; }
        internal NoOpTray Tray { get; }

        internal RecurringLeaseNaturalWakeScheduler CreateScheduler(
            Func<
                DateTimeOffset,
                string?,
                string?,
                int,
                RecurringOccurrenceNaturalWakeCandidatePage>? candidateSourceForTest = null) =>
            new(
                Store,
                RejectedDispatcher(),
                () => false,
                () => "S-1",
                () => "session-1",
                utcNow: () => DateTimeOffset.UtcNow,
                waitForTest: (_, token) => Task.Delay(Timeout.InfiniteTimeSpan, token),
                candidateSourceForTest: candidateSourceForTest ??
                    ((_, _, _, _) => new RecurringOccurrenceNaturalWakeCandidatePage(
                        Array.Empty<RecurringOccurrenceNaturalWakeCandidate>(), false)));

        internal RecurringLeaseNaturalWakeRuntime CreateRuntime(
            RecurringLeaseNaturalWakeScheduler scheduler,
            string currentUserSid = "S-1",
            Func<string?, string?, int, RecurringLeaseStartupRecoveryBatchResult>? recoveryBatchForTest = null) =>
            new(
                Store,
                Engine,
                Tray,
                Audit,
                new StandingLeaseStartSafetyInterlock(),
                utcNowForTest: () => DateTimeOffset.UtcNow,
                currentUserSidForTest: () => currentUserSid,
                sessionBindingForTest: () => "session-1",
                recoveryBatchForTest: recoveryBatchForTest,
                schedulerForTest: scheduler,
                registerSystemLifecycleSignalsForTest: () => true);

        public void Dispose()
        {
            try { Engine.Dispose(); } catch { }
            try { Directory.Delete(_root, recursive: true); } catch { }
        }
    }

    private sealed class TestLifecycleSession : IRecurringLeaseCaptureLifecycleSession
    {
        private int _disposeCalls;

        internal int DisposeCalls => Volatile.Read(ref _disposeCalls);

        public bool TryAttach(AgentRecorder.Capture.ICaptureBackend backend, out string failureReason)
        {
            failureReason = "";
            return true;
        }

        public RecurringLeaseCaptureLifecycleHandoffResult CompleteStartHandoff() =>
            new(RecurringLeaseCaptureLifecycleHandoffStatus.Active);

        public RecurringLeaseLifecycleActionResult StartFailed() =>
            RecurringLeaseLifecycleActionResult.Applied("test_start_failed", terminal: true);

        public RecurringLeaseLifecycleActionResult Stop() =>
            RecurringLeaseLifecycleActionResult.Applied("test_stop", terminal: true);

        public void Dispose() => Interlocked.Increment(ref _disposeCalls);
    }

    private sealed class ProductionPathBackend : ICaptureBackend, IFirstFrameObservableCaptureBackend
    {
        private string _outputPath = "task279r-output.mp4";
        private int _startCalls;
        private int _stopCalls;
        private int _cancelCalls;
        private int _disposeCalls;
        public event Action<FirstFrameObservation>? FirstFrameObserved;
        private readonly TaskCompletionSource<object?> _started =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<object?> _disposed =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal int StartCalls => Volatile.Read(ref _startCalls);
        internal int StopCalls => Volatile.Read(ref _stopCalls);
        internal int CancelCalls => Volatile.Read(ref _cancelCalls);
        internal int DisposeCalls => Volatile.Read(ref _disposeCalls);
        internal Task Started => _started.Task;
        internal Task Disposed => _disposed.Task;

        public void Start(CaptureConfig config, CaptureAuthorizationProof authorizationProof)
        {
            authorizationProof.RequireConsumed();
            _outputPath = config.OutputPath;
            Interlocked.Increment(ref _startCalls);
            FirstFrameObserved?.Invoke(new FirstFrameObservation
            {
                EvidenceKind = "ffmpeg_progress_frame_and_output_bytes",
                FrameNumber = 1,
                TotalSizeBytes = 1024,
                OutTimeUs = 1_000_000,
            });
            _started.TrySetResult(null);
        }

        public OutputMeta Stop()
        {
            Interlocked.Increment(ref _stopCalls);
            return new OutputMeta
            {
                SizeBytes = 1024,
                OutputFileExists = true,
                DurationSeconds = 1,
                OutputPath = _outputPath,
                StopReason = "stop",
            };
        }

        public void Cancel() => Interlocked.Increment(ref _cancelCalls);

        public void Dispose()
        {
            Interlocked.Increment(ref _disposeCalls);
            _disposed.TrySetResult(null);
        }
    }

    private sealed class NoOpTray : ITrayContext
    {
        public string HostMode => "headless";
        public bool SupportsRegionSelectionUi => false;

        public void RequestConfirmation(
            RecordingConfirmationPresentation presentation,
            Action<ConfirmationDecision> callback)
        {
        }

        public void RequestRegionSelection(
            int timeoutSeconds,
            Action<string, int, int, int, int, string, string> callback)
        {
        }

        public void SetRecording(RecordingUiPresentation presentation) { }
        public void SetIdle(RecordingUiPresentation presentation) { }
        public void SetAllIdle() { }
        public void ShowError(string text) { }
    }

    private sealed class TestDatabase : IDisposable
    {
        private readonly string _root = Path.Combine(
            Path.GetTempPath(),
            "agent-recorder-task279-scheduler-" + Guid.NewGuid().ToString("N"));

        internal TestDatabase()
        {
            Directory.CreateDirectory(_root);
            Store = new SqliteOperationalStore(Path.Combine(
                _root,
                SqliteOperationalStore.DatabaseFileName));
            Store.Initialize();
        }

        internal SqliteOperationalStore Store { get; }
        internal string Root => _root;

        public void Dispose()
        {
            try { Directory.Delete(_root, recursive: true); } catch { }
        }
    }
}
