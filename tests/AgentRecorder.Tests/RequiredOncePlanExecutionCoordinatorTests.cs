using AgentRecorder.Capture;
using AgentRecorder.App;
using AgentRecorder.Core;
using AgentRecorder.Core.Automation;
using AgentRecorder.Infrastructure;
using AgentRecorder.Logging;
using AgentRecorder.Persistence;
using AgentRecorder.Windows;
using Microsoft.Data.Sqlite;
using Xunit;

namespace AgentRecorder.Tests;

public sealed partial class RequiredOncePlanSetupCoordinatorTests
{
    [Fact]
    public async Task RequiredOnceWaitsForDistinctExecutionApprovalThenStartsAndSettlesOneFakeCapture()
    {
        using var database = new TemporaryDatabase();
        var clock = new MutableClock(DateTimeOffset.UtcNow);
        var setup = CreateFixture(database, clock, new FakeUi());
        using var setupCoordinator = setup.Coordinator;
        var created = setupCoordinator.CreateOrGet(setup.ApiRequest("required-execution-success"));
        await setupCoordinator.WaitForIdleAsync().WaitAsync(TimeSpan.FromSeconds(5));
        var state = setupCoordinator.Get(created.SetupIntentId!)!;
        clock.Set(setup.Request.ScheduledStartUtc);

        var executionUi = new FakeExecutionUi { HoldApproval = true };
        var backend = new FakeRequiredOnceBackend();
        using var engine = CreateRequiredOnceEngine(setup, backend);
        using var execution = CreateExecutionCoordinator(database, setup, engine, executionUi);
        var dispatch = execution.ProcessDueOnceAsync();
        await executionUi.ApprovalRequested.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(0, backend.StartCalls);
        Assert.Equal(0, Count(database.Store, "recording_runs"));
        Assert.Equal("pending_confirmation", ScalarText(database.Store,
            "SELECT status_code FROM required_once_execution_authorizations LIMIT 1;"));
        Assert.Equal("pending_confirmation",
            new PlanExecutionStatusQueryService(database.Store).Get(state.PlanId!, Sid, Session)!
                .LatestOccurrence!.Status);
        Assert.Equal("DISPLAY-1", executionUi.LastDetails!.DisplayName);
        Assert.Equal("stable-display-fingerprint-1", executionUi.LastDetails.StableDisplayFingerprint);
        Assert.Equal(new AuthorizedPhysicalRectangle(120, 140, 640, 360), executionUi.LastDetails.RegionWithinDisplay);
        Assert.Equal(TimeSpan.FromSeconds(60), executionUi.LastDetails.Duration);
        Assert.Contains("640 × 360", executionUi.LastSummaryText);
        Assert.Contains("Not captured", executionUi.LastSummaryText);
        Assert.Contains("required-once-test.mp4", executionUi.LastSummaryText);

        executionUi.Complete(RequiredOnceExecutionApprovalResult.Approved);
        await dispatch.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(1, backend.StartCalls);
        Assert.Equal(CaptureAuthorizationProofKind.RequiredOnceExecution, backend.LastProof!.Kind);
        Assert.True(backend.LastProof.IsConsumed);
        var persistedExecutionStatus = ScalarText(database.Store,
            "SELECT status_code FROM required_once_execution_authorizations LIMIT 1;");
        var persistedExecutionReason = ScalarText(database.Store,
            "SELECT COALESCE(reason_code, '<null>') FROM required_once_execution_authorizations LIMIT 1;");
        var engineSnapshot = engine._recs.Values.Single();
        Assert.True(persistedExecutionStatus == "recording",
            $"status={persistedExecutionStatus}; reason={persistedExecutionReason}; state={engineSnapshot.State}; error={engineSnapshot.Error}; stop={engineSnapshot.StopReason}");
        var running = new PlanExecutionStatusQueryService(database.Store).Get(state.PlanId!, Sid, Session)!;
        Assert.Equal("recording", running.LatestOccurrence!.ExecutionStatusCode);
        Assert.Equal(RecordingRunStatus.Recording.ToString().ToLowerInvariant(), running.LatestOccurrence.Run!.Status);

        _ = engine.Stop(running.LatestOccurrence.RunId!, "user_requested");
        var completed = new PlanExecutionStatusQueryService(database.Store).Get(state.PlanId!, Sid, Session)!;
        Assert.True(completed.LatestOccurrence!.Status == "completed",
            $"occurrence={completed.LatestOccurrence.Status}; execution={completed.LatestOccurrence.ExecutionStatusCode}; " +
            $"occurrenceReason={completed.LatestOccurrence.TerminalReasonCode}; run={completed.LatestOccurrence.Run?.Status}; " +
            $"runReason={completed.LatestOccurrence.Run?.TerminalReasonCode}; engine={engine._recs.Values.Single().State}/{engine._recs.Values.Single().Error}");
        Assert.Equal("settled", completed.LatestOccurrence.ExecutionStatusCode);
        Assert.Equal("settled", completed.LatestOccurrence.Run!.Status);
        Assert.True(completed.LatestOccurrence.OutputPathRecorded);
        Assert.True(completed.LatestOccurrence.OutputFileExists);
        Assert.Equal(Path.GetFullPath(Path.Combine(setup.OutputPath, "required-once-test.mp4")),
            completed.LatestOccurrence.OutputPath);
        Assert.Equal(0, Count(database.Store, "lease_uses"));
        Assert.Equal(0, Count(database.Store, "consent_leases"));
        Assert.Equal(1, Count(database.Store, "recording_runs"));
    }

    [Fact]
    public async Task FailedAuthorizationUpdateRollsBackRunAndNeverStartsBackend()
    {
        using var database = new TemporaryDatabase();
        var clock = new MutableClock(DateTimeOffset.UtcNow);
        var setup = CreateFixture(database, clock, new FakeUi());
        using var setupCoordinator = setup.Coordinator;
        var created = setupCoordinator.CreateOrGet(setup.ApiRequest("required-execution-auth-update-ignored"));
        await setupCoordinator.WaitForIdleAsync().WaitAsync(TimeSpan.FromSeconds(5));
        var planId = setupCoordinator.Get(created.SetupIntentId!)!.PlanId!;
        clock.Set(setup.Request.ScheduledStartUtc);

        using (var connection = database.Store.OpenConnection())
        using (var trigger = connection.CreateCommand())
        {
            trigger.CommandText = """
                CREATE TRIGGER ignore_required_once_start_commit
                BEFORE UPDATE OF status_code ON required_once_execution_authorizations
                WHEN NEW.status_code = 'start_committed'
                BEGIN
                    SELECT RAISE(IGNORE);
                END;
                """;
            trigger.ExecuteNonQuery();
        }

        var backend = new FakeRequiredOnceBackend();
        using var engine = CreateRequiredOnceEngine(setup, backend);
        using var execution = CreateExecutionCoordinator(database, setup, engine, new FakeExecutionUi());
        await execution.ProcessDueOnceAsync().WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(0, backend.StartCalls);
        Assert.Equal(0, Count(database.Store, "recording_runs"));
        Assert.Null(ScalarNullableText(database.Store,
            "SELECT run_id FROM plan_occurrences WHERE plan_id = " +
            "(SELECT plan_id FROM required_once_execution_authorizations LIMIT 1) LIMIT 1;"));
        Assert.Equal("blocked", ScalarText(database.Store,
            "SELECT status_code FROM required_once_execution_authorizations LIMIT 1;"));
        Assert.Equal("execution_start_commit_rejected", ScalarText(database.Store,
            "SELECT reason_code FROM required_once_execution_authorizations LIMIT 1;"));
        Assert.Equal("blocked", new PlanExecutionStatusQueryService(database.Store)
            .Get(planId, Sid, Session)!.LatestOccurrence!.Status);
        Assert.Empty(new SqliteRequiredOnceExecutionRepository(database.Store)
            .ListDue(Sid, Session, clock.Now.AddMinutes(1)));
    }

    [Fact]
    public async Task FfmpegRegionZeroCountdownStartsOnFirstFrameAndSettlesOnce()
    {
        using var database = new TemporaryDatabase();
        var clock = new MutableClock(DateTimeOffset.UtcNow);
        var setup = CreateFixture(database, clock, new FakeUi());
        using var setupCoordinator = setup.Coordinator;
        var created = setupCoordinator.CreateOrGet(setup.ApiRequest("required-execution-ffmpeg-success"));
        await setupCoordinator.WaitForIdleAsync().WaitAsync(TimeSpan.FromSeconds(5));
        var planId = setupCoordinator.Get(created.SetupIntentId!)!.PlanId!;
        clock.Set(setup.Request.ScheduledStartUtc);
        var backend = new FakeRequiredOnceBackend();
        using var engine = CreateRequiredOnceEngine(setup, backend, "ffmpeg-region");
        var executionUi = new FakeExecutionUi { HoldApproval = true };
        using var execution = CreateExecutionCoordinator(database, setup, engine, executionUi);

        var dispatch = execution.ProcessDueOnceAsync();
        await executionUi.ApprovalRequested.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(0, backend.StartCalls);
        Assert.Equal(0, Count(database.Store, "recording_runs"));
        executionUi.Complete(RequiredOnceExecutionApprovalResult.Approved);
        await dispatch.WaitAsync(TimeSpan.FromSeconds(5));
        await WaitUntilAsync(() => backend.StartCalls == 1 &&
            engine._recs.Values.SingleOrDefault()?.State == RecState.recording &&
            ScalarText(database.Store, "SELECT status_code FROM required_once_execution_authorizations LIMIT 1;") == "recording");

        var running = new PlanExecutionStatusQueryService(database.Store).Get(planId, Sid, Session)!;
        Assert.Equal("recording", running.LatestOccurrence!.ExecutionStatusCode);
        Assert.Equal("recording", running.LatestOccurrence.Run!.Status);
        Assert.Equal(1, backend.StartCalls);
        Assert.Equal("ffmpeg-region", engine._recs.Values.Single().BackendType);
        _ = engine.Stop(running.LatestOccurrence.RunId!, "user_requested");
        var settled = new PlanExecutionStatusQueryService(database.Store).Get(planId, Sid, Session)!;
        Assert.Equal("settled", settled.LatestOccurrence!.ExecutionStatusCode);
        Assert.Equal("completed", settled.LatestOccurrence.Status);
        Assert.True(settled.LatestOccurrence.OutputPathRecorded);
        Assert.True(settled.LatestOccurrence.OutputFileExists);
        Assert.Equal(0, Count(database.Store, "recording_run_output_evidence"));

        await execution.ProcessDueOnceAsync().WaitAsync(TimeSpan.FromSeconds(5));
        var recovered = new SqliteRequiredOnceExecutionRepository(database.Store)
            .RecoverOutstanding(32, clock.Now.AddMinutes(1));
        Assert.Equal(0, recovered.RecoveredCount);
        Assert.Equal(1, Count(database.Store, "recording_runs"));
        Assert.Equal(1, backend.StartCalls);
    }

    [Fact]
    public async Task FfmpegRegionZeroCountdownFailureBeforeBackendCallSettlesFailedWithoutReplay()
    {
        using var database = new TemporaryDatabase();
        var clock = new MutableClock(DateTimeOffset.UtcNow);
        var setup = CreateFixture(database, clock, new FakeUi());
        using var setupCoordinator = setup.Coordinator;
        var created = setupCoordinator.CreateOrGet(setup.ApiRequest("required-execution-ffmpeg-prestart-failure"));
        await setupCoordinator.WaitForIdleAsync().WaitAsync(TimeSpan.FromSeconds(5));
        var planId = setupCoordinator.Get(created.SetupIntentId!)!.PlanId!;
        clock.Set(setup.Request.ScheduledStartUtc);
        var backend = new FakeRequiredOnceBackend();
        using var engine = CreateRequiredOnceEngine(setup, backend, "ffmpeg-region");
        engine.BeforeStartActionForTests = (_, stage) =>
        {
            if (stage == "backend.start")
                throw new InvalidOperationException("injected pre-start failure");
        };
        using var execution = CreateExecutionCoordinator(database, setup, engine, new FakeExecutionUi());

        await execution.ProcessDueOnceAsync().WaitAsync(TimeSpan.FromSeconds(5));
        await WaitUntilAsync(() => ScalarText(database.Store,
            "SELECT status_code FROM required_once_execution_authorizations LIMIT 1;") == "failed");

        var status = new PlanExecutionStatusQueryService(database.Store).Get(planId, Sid, Session)!;
        Assert.Equal(0, backend.StartCalls);
        Assert.Equal(0, backend.StopCalls);
        Assert.Equal(1, backend.DisposeCalls);
        Assert.Equal("failed", status.LatestOccurrence!.ExecutionStatusCode);
        Assert.Equal("blocked", status.LatestOccurrence.Status);
        Assert.Equal("failed", status.LatestOccurrence.Run!.Status);
        Assert.Equal("required_once_backend_start_failed", status.LatestOccurrence.TerminalReasonCode);
        Assert.Equal(status.LatestOccurrence.TerminalReasonCode, status.LatestOccurrence.Run.TerminalReasonCode);
        Assert.Equal("required_once_backend_start_failed", ScalarText(database.Store,
            "SELECT reason_code FROM required_once_execution_authorizations LIMIT 1;"));
        Assert.Equal(RecState.failed, engine._recs.Values.Single().State);

        await execution.ProcessDueOnceAsync().WaitAsync(TimeSpan.FromSeconds(5));
        var recovered = new SqliteRequiredOnceExecutionRepository(database.Store)
            .RecoverOutstanding(32, clock.Now.AddMinutes(1));
        Assert.Equal(0, recovered.RecoveredCount);
        Assert.Equal(1, Count(database.Store, "recording_runs"));
        Assert.Equal(0, backend.StartCalls);
    }

    [Fact]
    public async Task FfmpegRegionZeroCountdownFailureAfterStartAttemptStopsAndNeverCompletes()
    {
        using var database = new TemporaryDatabase();
        var clock = new MutableClock(DateTimeOffset.UtcNow);
        var setup = CreateFixture(database, clock, new FakeUi());
        using var setupCoordinator = setup.Coordinator;
        var created = setupCoordinator.CreateOrGet(setup.ApiRequest("required-execution-ffmpeg-poststart-failure"));
        await setupCoordinator.WaitForIdleAsync().WaitAsync(TimeSpan.FromSeconds(5));
        var planId = setupCoordinator.Get(created.SetupIntentId!)!.PlanId!;
        clock.Set(setup.Request.ScheduledStartUtc);
        var backend = new FakeRequiredOnceBackend { FailStartAfterFirstFrame = true };
        using var engine = CreateRequiredOnceEngine(setup, backend, "ffmpeg-region");
        using var execution = CreateExecutionCoordinator(database, setup, engine, new FakeExecutionUi());

        await execution.ProcessDueOnceAsync().WaitAsync(TimeSpan.FromSeconds(5));
        await WaitUntilAsync(() => ScalarText(database.Store,
            "SELECT status_code FROM required_once_execution_authorizations LIMIT 1;") == "failed");

        var status = new PlanExecutionStatusQueryService(database.Store).Get(planId, Sid, Session)!;
        Assert.Equal(1, backend.StartCalls);
        Assert.Equal(1, backend.StopCalls);
        Assert.Equal(1, backend.DisposeCalls);
        Assert.Equal("failed", status.LatestOccurrence!.ExecutionStatusCode);
        Assert.Equal("blocked", status.LatestOccurrence.Status);
        Assert.Equal("failed", status.LatestOccurrence.Run!.Status);
        Assert.Equal("required_once_start_outcome_unknown", status.LatestOccurrence.TerminalReasonCode);
        Assert.Equal(status.LatestOccurrence.TerminalReasonCode, status.LatestOccurrence.Run.TerminalReasonCode);
        Assert.Equal("required_once_start_outcome_unknown", ScalarText(database.Store,
            "SELECT reason_code FROM required_once_execution_authorizations LIMIT 1;"));
        Assert.False(status.LatestOccurrence.OutputPathRecorded);
        Assert.Equal(0, Count(database.Store, "recording_run_output_evidence"));
        Assert.Equal(RecState.failed, engine._recs.Values.Single().State);

        await execution.ProcessDueOnceAsync().WaitAsync(TimeSpan.FromSeconds(5));
        var recovered = new SqliteRequiredOnceExecutionRepository(database.Store)
            .RecoverOutstanding(32, clock.Now.AddMinutes(1));
        Assert.Equal(0, recovered.RecoveredCount);
        Assert.Equal(1, Count(database.Store, "recording_runs"));
        Assert.Equal(1, backend.StartCalls);
    }

    [Theory]
    [InlineData("Rejected", "rejected", "cancelled", "execution_confirmation_rejected_by_user")]
    [InlineData("TimedOut", "expired", "expired", "execution_confirmation_timed_out")]
    [InlineData("HostShutdown", "rejected", "cancelled", "execution_confirmation_host_shutdown")]
    [InlineData("Unavailable", "blocked", "blocked", "interactive_desktop_unavailable")]
    public async Task RejectedOrTimedOutExecutionApprovalNeverCreatesRun(
        string decisionCode,
        string executionStatus,
        string occurrenceStatus,
        string expectedReason)
    {
        var decision = Enum.Parse<RequiredOnceExecutionApprovalResult>(decisionCode);
        using var database = new TemporaryDatabase();
        var clock = new MutableClock(DateTimeOffset.UtcNow);
        var setup = CreateFixture(database, clock, new FakeUi());
        using var setupCoordinator = setup.Coordinator;
        var created = setupCoordinator.CreateOrGet(setup.ApiRequest("required-execution-" + executionStatus));
        await setupCoordinator.WaitForIdleAsync().WaitAsync(TimeSpan.FromSeconds(5));
        var planId = setupCoordinator.Get(created.SetupIntentId!)!.PlanId!;
        clock.Set(setup.Request.ScheduledStartUtc);

        var executionUi = new FakeExecutionUi { Result = decision };
        var backend = new FakeRequiredOnceBackend();
        using var engine = CreateRequiredOnceEngine(setup, backend);
        using var execution = CreateExecutionCoordinator(database, setup, engine, executionUi);
        await execution.ProcessDueOnceAsync().WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(0, backend.StartCalls);
        Assert.Equal(0, Count(database.Store, "recording_runs"));
        Assert.Equal(executionStatus, ScalarText(database.Store,
            "SELECT status_code FROM required_once_execution_authorizations LIMIT 1;"));
        Assert.Equal(expectedReason, ScalarText(database.Store,
            "SELECT reason_code FROM required_once_execution_authorizations LIMIT 1;"));
        var status = new PlanExecutionStatusQueryService(database.Store).Get(planId, Sid, Session)!;
        Assert.Equal(occurrenceStatus, status.LatestOccurrence!.Status);
        Assert.Equal(executionStatus, status.LatestOccurrence.ExecutionStatusCode);
    }

    [Fact]
    public async Task DisplayDriftAfterApprovalButInsideStartCommitIsTerminalWithoutRun()
    {
        using var database = new TemporaryDatabase();
        var clock = new MutableClock(DateTimeOffset.UtcNow);
        var setup = CreateFixture(database, clock, new FakeUi());
        using var setupCoordinator = setup.Coordinator;
        var created = setupCoordinator.CreateOrGet(setup.ApiRequest("required-execution-atomic-drift"));
        await setupCoordinator.WaitForIdleAsync().WaitAsync(TimeSpan.FromSeconds(5));
        var planId = setupCoordinator.Get(created.SetupIntentId!)!.PlanId!;
        clock.Set(setup.Request.ScheduledStartUtc);

        var executionUi = new FakeExecutionUi();
        var backend = new FakeRequiredOnceBackend();
        using var engine = CreateRequiredOnceEngine(setup, backend);
        using var execution = new RequiredOncePlanExecutionCoordinator(
            database.Store, engine, new TestTray(), new NoopAuditLogger(), executionUi,
            currentDisplaysForTest: () => setup.Displays.Current,
            principalForTest: () => new RequiredOncePrincipal(Sid, Session),
            utcNowForTest: () => clock.Now,
            outputReadinessForTest: setup.OutputReadiness,
            beforeAtomicEnvironmentRecheckForTest: () => setup.OutputReadiness.ReasonCode = "execution_output_unavailable");

        await execution.ProcessDueOnceAsync().WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(1, executionUi.ShowCount);
        Assert.Equal(0, backend.StartCalls);
        Assert.Equal(0, Count(database.Store, "recording_runs"));
        Assert.Equal("blocked", ScalarText(database.Store,
            "SELECT status_code FROM required_once_execution_authorizations LIMIT 1;"));
        Assert.Equal("execution_output_unavailable", ScalarText(database.Store,
            "SELECT reason_code FROM required_once_execution_authorizations LIMIT 1;"));
        var status = new PlanExecutionStatusQueryService(database.Store).Get(planId, Sid, Session)!;
        Assert.Equal("blocked", status.LatestOccurrence!.Status);
        Assert.Equal("blocked", status.LatestOccurrence.ExecutionStatusCode);
    }

    [Fact]
    public async Task SessionDriftDuringPreDialogRecheckDurablyBlocksWithoutPrompt()
    {
        using var database = new TemporaryDatabase();
        var clock = new MutableClock(DateTimeOffset.UtcNow);
        var setup = CreateFixture(database, clock, new FakeUi());
        using var setupCoordinator = setup.Coordinator;
        var created = setupCoordinator.CreateOrGet(setup.ApiRequest("required-execution-session-preflight"));
        await setupCoordinator.WaitForIdleAsync().WaitAsync(TimeSpan.FromSeconds(5));
        var planId = setupCoordinator.Get(created.SetupIntentId!)!.PlanId!;
        clock.Set(setup.Request.ScheduledStartUtc);
        var principalReads = 0;
        var executionUi = new FakeExecutionUi();
        var backend = new FakeRequiredOnceBackend();
        using var engine = CreateRequiredOnceEngine(setup, backend);
        using var execution = CreateExecutionCoordinator(database, setup, engine, executionUi,
            principalForTest: () => Interlocked.Increment(ref principalReads) <= 2
                ? new RequiredOncePrincipal(Sid, Session)
                : new RequiredOncePrincipal(Sid, "changed-session"));

        await execution.ProcessDueOnceAsync().WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(0, executionUi.ShowCount);
        Assert.Equal(0, backend.StartCalls);
        Assert.Equal(0, Count(database.Store, "recording_runs"));
        Assert.Equal(0, Count(database.Store, "required_once_execution_authorizations"));
        var status = new PlanExecutionStatusQueryService(database.Store).Get(planId, Sid, Session)!;
        Assert.Equal("blocked", status.LatestOccurrence!.Status);
        Assert.Equal("execution_identity_changed", status.LatestOccurrence.TerminalReasonCode);
    }

    [Fact]
    public async Task SessionDriftInsideAtomicStartGateBlocksApprovedRunWithoutStarting()
    {
        using var database = new TemporaryDatabase();
        var clock = new MutableClock(DateTimeOffset.UtcNow);
        var setup = CreateFixture(database, clock, new FakeUi());
        using var setupCoordinator = setup.Coordinator;
        var created = setupCoordinator.CreateOrGet(setup.ApiRequest("required-execution-session-gate"));
        await setupCoordinator.WaitForIdleAsync().WaitAsync(TimeSpan.FromSeconds(5));
        var planId = setupCoordinator.Get(created.SetupIntentId!)!.PlanId!;
        clock.Set(setup.Request.ScheduledStartUtc);
        var currentPrincipal = new RequiredOncePrincipal(Sid, Session);
        var executionUi = new FakeExecutionUi();
        var backend = new FakeRequiredOnceBackend();
        using var engine = CreateRequiredOnceEngine(setup, backend);
        using var execution = CreateExecutionCoordinator(database, setup, engine, executionUi,
            principalForTest: () => currentPrincipal,
            beforeAtomicEnvironmentRecheckForTest: () => currentPrincipal = new RequiredOncePrincipal(Sid, "changed-session"));

        await execution.ProcessDueOnceAsync().WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(1, executionUi.ShowCount);
        Assert.Equal(0, backend.StartCalls);
        Assert.Equal(0, Count(database.Store, "recording_runs"));
        Assert.Equal("blocked", ScalarText(database.Store,
            "SELECT status_code FROM required_once_execution_authorizations LIMIT 1;"));
        Assert.Equal("execution_identity_changed", ScalarText(database.Store,
            "SELECT reason_code FROM required_once_execution_authorizations LIMIT 1;"));
        var status = new PlanExecutionStatusQueryService(database.Store).Get(planId, Sid, Session)!;
        Assert.Equal("blocked", status.LatestOccurrence!.Status);
        Assert.Null(status.LatestOccurrence.RunId);
    }

    [Fact]
    public async Task LatestStartDriftInsideAtomicGateExpiresApprovedClaimWithoutRun()
    {
        using var database = new TemporaryDatabase();
        var clock = new MutableClock(DateTimeOffset.UtcNow);
        var setup = CreateFixture(database, clock, new FakeUi());
        using var setupCoordinator = setup.Coordinator;
        var created = setupCoordinator.CreateOrGet(setup.ApiRequest("required-execution-latest-start-gate"));
        await setupCoordinator.WaitForIdleAsync().WaitAsync(TimeSpan.FromSeconds(5));
        var planId = setupCoordinator.Get(created.SetupIntentId!)!.PlanId!;
        clock.Set(setup.Request.ScheduledStartUtc);
        var executionUi = new FakeExecutionUi();
        var backend = new FakeRequiredOnceBackend();
        using var engine = CreateRequiredOnceEngine(setup, backend);
        using var execution = CreateExecutionCoordinator(database, setup, engine, executionUi,
            beforeAtomicEnvironmentRecheckForTest: () => clock.Set(setup.Request.LatestStartUtc));

        await execution.ProcessDueOnceAsync().WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(1, executionUi.ShowCount);
        Assert.Equal(0, backend.StartCalls);
        Assert.Equal(0, Count(database.Store, "recording_runs"));
        Assert.Equal("expired", ScalarText(database.Store,
            "SELECT status_code FROM required_once_execution_authorizations LIMIT 1;"));
        Assert.Equal("latest_start_window_missed", ScalarText(database.Store,
            "SELECT reason_code FROM required_once_execution_authorizations LIMIT 1;"));
        Assert.Equal("expired", new PlanExecutionStatusQueryService(database.Store)
            .Get(planId, Sid, Session)!.LatestOccurrence!.Status);
    }

    [Fact]
    public async Task CorruptApprovedSpecificationIsDurablyBlockedWithoutPromptOrRun()
    {
        using var database = new TemporaryDatabase();
        var clock = new MutableClock(DateTimeOffset.UtcNow);
        var setup = CreateFixture(database, clock, new FakeUi());
        using var setupCoordinator = setup.Coordinator;
        var created = setupCoordinator.CreateOrGet(setup.ApiRequest("required-execution-corrupt-spec"));
        await setupCoordinator.WaitForIdleAsync().WaitAsync(TimeSpan.FromSeconds(5));
        var state = setupCoordinator.Get(created.SetupIntentId!)!;
        clock.Set(setup.Request.ScheduledStartUtc);
        using (var connection = database.Store.OpenConnection())
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "DROP TRIGGER trg_required_once_authorized_specs_immutable_update; UPDATE required_once_authorized_specs SET region_width = region_width + 1 WHERE occurrence_id = $occurrence;";
            command.Parameters.AddWithValue("$occurrence", state.OccurrenceId!);
            Assert.Equal(1, command.ExecuteNonQuery());
        }

        var executionUi = new FakeExecutionUi();
        var backend = new FakeRequiredOnceBackend();
        using var engine = CreateRequiredOnceEngine(setup, backend);
        using var execution = CreateExecutionCoordinator(database, setup, engine, executionUi);

        await execution.ProcessDueOnceAsync().WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(0, executionUi.ShowCount);
        Assert.Equal(0, backend.StartCalls);
        Assert.Equal(0, Count(database.Store, "recording_runs"));
        Assert.Equal(0, Count(database.Store, "required_once_execution_authorizations"));
        using var verification = database.Store.OpenConnection();
        using var occurrenceRead = verification.CreateCommand();
        occurrenceRead.CommandText = "SELECT status_code, terminal_reason_code FROM plan_occurrences WHERE id = $occurrence;";
        occurrenceRead.Parameters.AddWithValue("$occurrence", state.OccurrenceId!);
        using var occurrenceReader = occurrenceRead.ExecuteReader();
        Assert.True(occurrenceReader.Read());
        Assert.Equal("blocked", occurrenceReader.GetString(0));
        Assert.Equal("approved_specification_corrupt", occurrenceReader.GetString(1));
    }

    [Fact]
    public async Task UnavailableDesktopOrPreDialogDisplayDriftDurablyBlocksWithoutShowingPrompt()
    {
        using var database = new TemporaryDatabase();
        var clock = new MutableClock(DateTimeOffset.UtcNow);
        var setup = CreateFixture(database, clock, new FakeUi());
        using var setupCoordinator = setup.Coordinator;
        var created = setupCoordinator.CreateOrGet(setup.ApiRequest("required-execution-preflight"));
        await setupCoordinator.WaitForIdleAsync().WaitAsync(TimeSpan.FromSeconds(5));
        var planId = setupCoordinator.Get(created.SetupIntentId!)!.PlanId!;
        clock.Set(setup.Request.ScheduledStartUtc);
        setup.Displays.Set(new[]
        {
            new StandingLeaseDisplayMetadata("DISPLAY-1", "stable-display-fingerprint-1",
                DisplayIdentityResolutionStatus.Resolved,
                new AuthorizedPhysicalRectangle(0, 0, 1920, 1080),
                120, 120, 1920, 1080, AuthorizedDisplayOrientation.Landscape),
        });
        var executionUi = new FakeExecutionUi();
        var backend = new FakeRequiredOnceBackend();
        using var engine = CreateRequiredOnceEngine(setup, backend);
        using var execution = CreateExecutionCoordinator(database, setup, engine, executionUi);

        await execution.ProcessDueOnceAsync().WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(0, executionUi.ShowCount);
        Assert.Equal(0, backend.StartCalls);
        Assert.Equal(0, Count(database.Store, "required_once_execution_authorizations"));
        var status = new PlanExecutionStatusQueryService(database.Store).Get(planId, Sid, Session)!;
        Assert.Equal("blocked", status.LatestOccurrence!.Status);
        Assert.Equal("execution_display_topology_changed", status.LatestOccurrence.TerminalReasonCode);
    }

    [Fact]
    public async Task OutputDriftBeforeDialogDurablyBlocksWithoutPromptOrRun()
    {
        using var database = new TemporaryDatabase();
        var clock = new MutableClock(DateTimeOffset.UtcNow);
        var setup = CreateFixture(database, clock, new FakeUi());
        using var setupCoordinator = setup.Coordinator;
        var created = setupCoordinator.CreateOrGet(setup.ApiRequest("required-execution-output-drift"));
        await setupCoordinator.WaitForIdleAsync().WaitAsync(TimeSpan.FromSeconds(5));
        var planId = setupCoordinator.Get(created.SetupIntentId!)!.PlanId!;
        clock.Set(setup.Request.ScheduledStartUtc);
        setup.OutputReadiness.ReasonCode = "execution_output_unavailable";
        var executionUi = new FakeExecutionUi();
        var backend = new FakeRequiredOnceBackend();
        using var engine = CreateRequiredOnceEngine(setup, backend);
        using var execution = CreateExecutionCoordinator(database, setup, engine, executionUi);

        await execution.ProcessDueOnceAsync().WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(0, executionUi.ShowCount);
        Assert.Equal(0, backend.StartCalls);
        Assert.Equal(0, Count(database.Store, "recording_runs"));
        Assert.Equal("execution_output_unavailable", ScalarText(database.Store,
            "SELECT terminal_reason_code FROM plan_occurrences LIMIT 1;"));
        Assert.Equal("blocked", new PlanExecutionStatusQueryService(database.Store)
            .Get(planId, Sid, Session)!.LatestOccurrence!.Status);
    }

    [Fact]
    public async Task MissedWindowIsDurableAndNeverReplay()
    {
        using var database = new TemporaryDatabase();
        var clock = new MutableClock(DateTimeOffset.UtcNow);
        var setup = CreateFixture(database, clock, new FakeUi());
        using var setupCoordinator = setup.Coordinator;
        var missed = setupCoordinator.CreateOrGet(setup.ApiRequest("required-execution-missed"));
        await setupCoordinator.WaitForIdleAsync().WaitAsync(TimeSpan.FromSeconds(5));
        var missedPlan = setupCoordinator.Get(missed.SetupIntentId!)!.PlanId!;
        clock.Set(setup.Request.LatestStartUtc);
        var missedUi = new FakeExecutionUi();
        var missedBackend = new FakeRequiredOnceBackend();
        using (var missedEngine = CreateRequiredOnceEngine(setup, missedBackend))
        using (var missedExecution = CreateExecutionCoordinator(database, setup, missedEngine, missedUi))
        {
            await missedExecution.ProcessDueOnceAsync().WaitAsync(TimeSpan.FromSeconds(5));
            await missedExecution.ProcessDueOnceAsync().WaitAsync(TimeSpan.FromSeconds(5));
        }
        Assert.Equal(0, missedUi.ShowCount);
        Assert.Equal(0, missedBackend.StartCalls);
        Assert.Equal("missed", new PlanExecutionStatusQueryService(database.Store)
            .Get(missedPlan, Sid, Session)!.LatestOccurrence!.Status);
    }

    [Fact]
    public async Task UnavailableDesktopBlocksWithoutPromptOrRun()
    {
        using var database = new TemporaryDatabase();
        var clock = new MutableClock(DateTimeOffset.UtcNow);
        var setup = CreateFixture(database, clock, new FakeUi());
        using var setupCoordinator = setup.Coordinator;
        var unavailable = setupCoordinator.CreateOrGet(setup.ApiRequest("required-execution-desktop-unavailable"));
        await setupCoordinator.WaitForIdleAsync().WaitAsync(TimeSpan.FromSeconds(5));
        var unavailablePlan = setupCoordinator.Get(unavailable.SetupIntentId!)!.PlanId!;
        clock.Set(setup.Request.ScheduledStartUtc);
        var unavailableUi = new FakeExecutionUi { IsAvailable = false };
        var unavailableBackend = new FakeRequiredOnceBackend();
        using var engine = CreateRequiredOnceEngine(setup, unavailableBackend);
        using var execution = CreateExecutionCoordinator(database, setup, engine, unavailableUi);
        await execution.ProcessDueOnceAsync().WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(0, unavailableUi.ShowCount);
        Assert.Equal(0, unavailableBackend.StartCalls);
        Assert.Equal("interactive_desktop_unavailable", ScalarText(database.Store,
            "SELECT terminal_reason_code FROM plan_occurrences WHERE plan_id = '" + unavailablePlan + "';"));
        Assert.Equal("blocked", new PlanExecutionStatusQueryService(database.Store)
            .Get(unavailablePlan, Sid, Session)!.LatestOccurrence!.Status);
    }

    [Theory]
    [InlineData("Approved")]
    [InlineData("Unavailable")]
    public async Task LateApprovalOrUnavailableDialogAfterLatestStartExpiresWithoutRun(string resultCode)
    {
        using var database = new TemporaryDatabase();
        var clock = new MutableClock(DateTimeOffset.UtcNow);
        var setup = CreateFixture(database, clock, new FakeUi());
        using var setupCoordinator = setup.Coordinator;
        var created = setupCoordinator.CreateOrGet(setup.ApiRequest("required-execution-late-approval"));
        await setupCoordinator.WaitForIdleAsync().WaitAsync(TimeSpan.FromSeconds(5));
        var planId = setupCoordinator.Get(created.SetupIntentId!)!.PlanId!;
        clock.Set(setup.Request.ScheduledStartUtc);
        var executionUi = new FakeExecutionUi { HoldApproval = true };
        var backend = new FakeRequiredOnceBackend();
        using var engine = CreateRequiredOnceEngine(setup, backend);
        using var execution = CreateExecutionCoordinator(database, setup, engine, executionUi);
        var dispatch = execution.ProcessDueOnceAsync();
        await executionUi.ApprovalRequested.Task.WaitAsync(TimeSpan.FromSeconds(5));

        clock.Set(setup.Request.LatestStartUtc);
        executionUi.Complete(Enum.Parse<RequiredOnceExecutionApprovalResult>(resultCode));
        await dispatch.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(0, backend.StartCalls);
        Assert.Equal(0, Count(database.Store, "recording_runs"));
        Assert.Equal("expired", ScalarText(database.Store,
            "SELECT status_code FROM required_once_execution_authorizations LIMIT 1;"));
        Assert.Equal("latest_start_window_missed", ScalarText(database.Store,
            "SELECT reason_code FROM required_once_execution_authorizations LIMIT 1;"));
        Assert.Equal("expired", new PlanExecutionStatusQueryService(database.Store)
            .Get(planId, Sid, Session)!.LatestOccurrence!.Status);
    }

    [Fact]
    public async Task BackendStartFailureIsDurableAndDoesNotReplay()
    {
        using var database = new TemporaryDatabase();
        var clock = new MutableClock(DateTimeOffset.UtcNow);
        var setup = CreateFixture(database, clock, new FakeUi());
        using var setupCoordinator = setup.Coordinator;
        var created = setupCoordinator.CreateOrGet(setup.ApiRequest("required-execution-start-failure"));
        await setupCoordinator.WaitForIdleAsync().WaitAsync(TimeSpan.FromSeconds(5));
        var planId = setupCoordinator.Get(created.SetupIntentId!)!.PlanId!;
        clock.Set(setup.Request.ScheduledStartUtc);
        var backend = new FakeRequiredOnceBackend { FailStart = true };
        using var engine = CreateRequiredOnceEngine(setup, backend);
        using var execution = CreateExecutionCoordinator(database, setup, engine, new FakeExecutionUi());

        await execution.ProcessDueOnceAsync().WaitAsync(TimeSpan.FromSeconds(5));
        await execution.ProcessDueOnceAsync().WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(1, backend.StartCalls);
        Assert.Equal(1, Count(database.Store, "recording_runs"));
        Assert.Equal("failed", ScalarText(database.Store,
            "SELECT status_code FROM required_once_execution_authorizations LIMIT 1;"));
        Assert.Equal("failed", new PlanExecutionStatusQueryService(database.Store)
            .Get(planId, Sid, Session)!.LatestOccurrence!.Run!.Status);
        Assert.Empty(new SqliteRequiredOnceExecutionRepository(database.Store)
            .ListDue(Sid, Session, clock.Now.AddMinutes(1)));
    }

    [Fact]
    public async Task InvalidFakeOutputSettlesFailedWithoutVerifiedOutputEvidence()
    {
        using var database = new TemporaryDatabase();
        var clock = new MutableClock(DateTimeOffset.UtcNow);
        var setup = CreateFixture(database, clock, new FakeUi());
        using var setupCoordinator = setup.Coordinator;
        var created = setupCoordinator.CreateOrGet(setup.ApiRequest("required-execution-output-failure"));
        await setupCoordinator.WaitForIdleAsync().WaitAsync(TimeSpan.FromSeconds(5));
        var planId = setupCoordinator.Get(created.SetupIntentId!)!.PlanId!;
        clock.Set(setup.Request.ScheduledStartUtc);
        var backend = new FakeRequiredOnceBackend { FailOutput = true };
        using var engine = CreateRequiredOnceEngine(setup, backend);
        using var execution = CreateExecutionCoordinator(database, setup, engine, new FakeExecutionUi());

        await execution.ProcessDueOnceAsync().WaitAsync(TimeSpan.FromSeconds(5));
        var running = new PlanExecutionStatusQueryService(database.Store).Get(planId, Sid, Session)!;
        _ = engine.Stop(running.LatestOccurrence!.RunId!, "user_requested");
        var failed = new PlanExecutionStatusQueryService(database.Store).Get(planId, Sid, Session)!;

        Assert.Equal("failed", failed.LatestOccurrence!.ExecutionStatusCode);
        Assert.Equal("blocked", failed.LatestOccurrence.Status);
        Assert.False(failed.LatestOccurrence.OutputPathRecorded);
        Assert.Equal(0, Count(database.Store, "recording_run_output_evidence"));
    }

    [Fact]
    public async Task DuplicateWakeAndRestartRecoveryNeverReplayAnOccurrence()
    {
        using var database = new TemporaryDatabase();
        var clock = new MutableClock(DateTimeOffset.UtcNow);
        var setup = CreateFixture(database, clock, new FakeUi());
        using var setupCoordinator = setup.Coordinator;
        var created = setupCoordinator.CreateOrGet(setup.ApiRequest("required-execution-duplicate"));
        await setupCoordinator.WaitForIdleAsync().WaitAsync(TimeSpan.FromSeconds(5));
        var planId = setupCoordinator.Get(created.SetupIntentId!)!.PlanId!;
        clock.Set(setup.Request.ScheduledStartUtc);

        var executionUi = new FakeExecutionUi { HoldApproval = true };
        var backend = new FakeRequiredOnceBackend();
        using var engine = CreateRequiredOnceEngine(setup, backend);
        using var first = CreateExecutionCoordinator(database, setup, engine, executionUi);
        using var second = CreateExecutionCoordinator(database, setup, engine, executionUi);
        var firstWake = first.ProcessDueOnceAsync();
        await executionUi.ApprovalRequested.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var duplicateWake = second.ProcessDueOnceAsync();
        executionUi.Complete(RequiredOnceExecutionApprovalResult.Approved);
        await Task.WhenAll(firstWake, duplicateWake).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(1, executionUi.ShowCount);
        Assert.Equal(1, backend.StartCalls);
        Assert.Equal(1, Count(database.Store, "recording_runs"));

        var running = new PlanExecutionStatusQueryService(database.Store).Get(planId, Sid, Session)!;
        Assert.Equal("recording", running.LatestOccurrence!.ExecutionStatusCode);
        Assert.Empty(new SqliteRequiredOnceExecutionRepository(database.Store)
            .ListDue(Sid, Session, clock.Now.AddSeconds(2)));
        Assert.Equal(1, Count(database.Store, "recording_runs"));
    }

    [Fact]
    public async Task RestartRecoveryMarksUnobservedCommittedRunUnknownAndNeverReplays()
    {
        using var database = new TemporaryDatabase();
        var clock = new MutableClock(DateTimeOffset.UtcNow);
        var setup = CreateFixture(database, clock, new FakeUi());
        using var setupCoordinator = setup.Coordinator;
        var created = setupCoordinator.CreateOrGet(setup.ApiRequest("required-execution-restart-recovery"));
        await setupCoordinator.WaitForIdleAsync().WaitAsync(TimeSpan.FromSeconds(5));
        var planId = setupCoordinator.Get(created.SetupIntentId!)!.PlanId!;
        clock.Set(setup.Request.ScheduledStartUtc);
        var repository = new SqliteRequiredOnceExecutionRepository(database.Store);
        var candidate = Assert.Single(repository.ListDue(Sid, Session, clock.Now));
        const string executionId = "restart-recovery-execution";
        Assert.True(repository.ClaimForConfirmation(candidate, executionId, clock.Now).Succeeded);
        var committed = repository.CommitStart(executionId, candidate.Specification,
            "restart-recovery-approval", "restart-recovery-proof", new string('b', 32),
            clock.Now, clock.Now, Sid, Session);
        Assert.NotNull(committed);

        var recovered = repository.RecoverOutstanding(32, clock.Now.AddSeconds(1));

        Assert.Equal(1, recovered.RecoveredCount);
        Assert.False(recovered.HasMore);
        Assert.Equal("started_unknown", ScalarText(database.Store,
            "SELECT status_code FROM required_once_execution_authorizations WHERE execution_id = 'restart-recovery-execution';"));
        Assert.Equal("started_unknown", new PlanExecutionStatusQueryService(database.Store)
            .Get(planId, Sid, Session)!.LatestOccurrence!.Run!.Status);
        Assert.Equal(1, Count(database.Store, "recording_runs"));
        Assert.Empty(repository.ListDue(Sid, Session, clock.Now.AddMinutes(1)));
    }

    [Fact]
    public async Task V18ExecutionAuthorizationRejectsBindingMutationAndIllegalStatusJump()
    {
        using var database = new TemporaryDatabase();
        var clock = new MutableClock(DateTimeOffset.UtcNow);
        var setup = CreateFixture(database, clock, new FakeUi());
        using var setupCoordinator = setup.Coordinator;
        var created = setupCoordinator.CreateOrGet(setup.ApiRequest("required-execution-schema-guard"));
        await setupCoordinator.WaitForIdleAsync().WaitAsync(TimeSpan.FromSeconds(5));
        clock.Set(setup.Request.ScheduledStartUtc);
        var repository = new SqliteRequiredOnceExecutionRepository(database.Store);
        var candidate = Assert.Single(repository.ListDue(Sid, Session, clock.Now));
        Assert.True(repository.ClaimForConfirmation(candidate, "schema-guard-execution", clock.Now).Succeeded);

        using var connection = database.Store.OpenConnection();
        using var bindingMutation = connection.CreateCommand();
        bindingMutation.CommandText = "UPDATE required_once_execution_authorizations SET specification_digest = $digest;";
        bindingMutation.Parameters.AddWithValue("$digest", new string('0', 64));
        var immutableFailure = Assert.Throws<SqliteException>(() => bindingMutation.ExecuteNonQuery());
        Assert.Contains("required_once_execution_immutable_binding", immutableFailure.Message, StringComparison.Ordinal);

        using var illegalTransition = connection.CreateCommand();
        illegalTransition.CommandText = "UPDATE required_once_execution_authorizations SET status_code = 'recording', version = version + 1, updated_at_utc = updated_at_utc + 1;";
        var transitionFailure = Assert.Throws<SqliteException>(() => illegalTransition.ExecuteNonQuery());
        Assert.Contains("required_once_execution_invalid_transition", transitionFailure.Message, StringComparison.Ordinal);
        Assert.Equal("pending_confirmation", ScalarText(database.Store,
            "SELECT status_code FROM required_once_execution_authorizations LIMIT 1;"));
        Assert.Equal(0, Count(database.Store, "recording_runs"));
    }

    private static RequiredOncePlanExecutionCoordinator CreateExecutionCoordinator(
        TemporaryDatabase database,
        Fixture setup,
        RecordingEngine engine,
        FakeExecutionUi ui,
        Func<RequiredOncePrincipal?>? principalForTest = null,
        Action? beforeAtomicEnvironmentRecheckForTest = null) => new(
            database.Store, engine, new TestTray(), new NoopAuditLogger(), ui,
            currentDisplaysForTest: () => setup.Displays.Current,
            principalForTest: principalForTest ?? (() => new RequiredOncePrincipal(Sid, Session)),
            utcNowForTest: () => setup.Clock.Now,
            outputReadinessForTest: setup.OutputReadiness,
            pollIntervalForTest: TimeSpan.FromMilliseconds(10),
            beforeAtomicEnvironmentRecheckForTest: beforeAtomicEnvironmentRecheckForTest);

    private static RecordingEngine CreateRequiredOnceEngine(
        Fixture setup,
        FakeRequiredOnceBackend backend,
        string backendType = "fake-required-once")
    {
        var engine = new RecordingEngine(new NoopAuditLogger(), displayTopologyProvider: new FixedDisplayTopologyProvider(new[]
        {
            new DisplayTopologySnapshot("DISPLAY-1", "stable-display-fingerprint-1",
                DisplayIdentityResolutionStatus.Resolved, new CapturePlanBounds(0, 0, 1920, 1080)),
        }))
        {
            UtcNowForTests = () => setup.Request.ScheduledStartUtc.UtcDateTime,
            CountdownSteps = 0,
            CountdownInterval = TimeSpan.Zero,
            FirstFrameTimeout = TimeSpan.FromSeconds(1),
            DisableDeadlineWatchdogForTests = true,
        };
        engine.SetTray(new TestTray());
        engine.RequiredOnceIdentityProviderForTests = () => (Sid, Session);
        engine.RequiredOnceDisplaysForTests = () => setup.Displays.Current;
        engine.RequiredOnceOutputReadinessForTests = setup.OutputReadiness;
        engine.BackendFactory = _ => (backend, backendType);
        return engine;
    }

    private static string? ScalarNullableText(SqliteOperationalStore store, string sql)
    {
        using var connection = store.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        var value = command.ExecuteScalar();
        return value is DBNull or null ? null : (string)value;
    }

    private static async Task WaitUntilAsync(Func<bool> predicate)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!predicate())
            await Task.Delay(10, timeout.Token);
    }

    private static string ScalarText(SqliteOperationalStore store, string sql)
    {
        using var connection = store.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return (string)command.ExecuteScalar()!;
    }

    private sealed class FixedDisplayTopologyProvider(IReadOnlyList<DisplayTopologySnapshot> displays) : IDisplayTopologyProvider
    {
        public IReadOnlyList<DisplayTopologySnapshot> GetCurrentDisplays() => displays;
    }

    private sealed class NoopAuditLogger : AuditLogger
    {
        public override void Log(string evt, object payload) { }
    }

    private sealed class FakeExecutionUi : IRequiredOncePlanExecutionUi
    {
        private readonly TaskCompletionSource<RequiredOnceExecutionApprovalResult> _approval =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal bool IsAvailable { get; set; } = true;
        internal bool HoldApproval { get; set; }
        internal RequiredOnceExecutionApprovalResult Result { get; set; } = RequiredOnceExecutionApprovalResult.Approved;
        internal RequiredOnceExecutionApprovalDetails? LastDetails { get; private set; }
        internal string LastSummaryText => LastDetails is null ? string.Empty :
            $"{LastDetails.DisplayName} {LastDetails.RegionWithinDisplay.Width} × {LastDetails.RegionWithinDisplay.Height} " +
            $"Not captured {LastDetails.FrozenOutputFilePath}";
        internal int ShowCount;
        internal TaskCompletionSource<object?> ApprovalRequested { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool IsInteractiveDesktopAvailable => IsAvailable;

        public Task<RequiredOnceExecutionApprovalResult> ShowExecutionApprovalAsync(
            RequiredOnceExecutionApprovalDetails details,
            CancellationToken cancellationToken)
        {
            LastDetails = details;
            Interlocked.Increment(ref ShowCount);
            ApprovalRequested.TrySetResult(null);
            return HoldApproval ? _approval.Task : Task.FromResult(Result);
        }

        internal void Complete(RequiredOnceExecutionApprovalResult result) => _approval.TrySetResult(result);
    }

    private sealed class FakeRequiredOnceBackend : ICaptureBackend, IFirstFrameObservableCaptureBackend, ICaptureEndedObservableBackend
    {
        private string? _outputPath;
        private int _exitCode;
        internal int StartCalls;
        internal int StopCalls;
        internal bool FailStart;
        internal bool FailStartAfterFirstFrame;
        internal bool FailOutput;
        internal int DisposeCalls;
        internal CaptureAuthorizationProof? LastProof;
        public event Action<FirstFrameObservation>? FirstFrameObserved;
        public event Action<CaptureEndedObservation>? CaptureEnded;

        public void Start(CaptureConfig cfg, CaptureAuthorizationProof authorizationProof)
        {
            Interlocked.Increment(ref StartCalls);
            _outputPath = cfg.OutputPath;
            LastProof = authorizationProof;
            _exitCode = FailOutput ? 1 : 0;
            if (FailStart) throw new InvalidOperationException("injected fake start failure");
            FirstFrameObserved?.Invoke(new FirstFrameObservation { FrameNumber = 1, TotalSizeBytes = 1024, OutTimeUs = 1000 });
            if (FailStartAfterFirstFrame)
                throw new InvalidOperationException("injected failure after first frame");
        }

        public OutputMeta Stop()
        {
            Interlocked.Increment(ref StopCalls);
            var bytes = FailOutput ? new byte[64] : Enumerable.Range(0, 2048).Select(i => (byte)(i % 251)).ToArray();
            if (_outputPath is not null)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_outputPath)!);
                File.WriteAllBytes(_outputPath, bytes);
            }
            CaptureEnded?.Invoke(new CaptureEndedObservation { ExitCode = _exitCode, Reason = "manual" });
            return new OutputMeta
            {
                OutputPath = _outputPath,
                SizeBytes = bytes.LongLength,
                DurationSeconds = 1,
                Width = 640,
                Height = 360,
                Fps = 30,
                Container = "mp4",
                Codec = "h264",
                OutputFileExists = _outputPath is not null,
                AudioSourceKind = "none",
                AudioStatus = "not_requested",
                HasAudioStream = false,
            };
        }

        public int ExitCode => _exitCode;
        public void OnNaturalExit(Action<int, OutputMeta> callback) { }
        public void Dispose() => Interlocked.Increment(ref DisposeCalls);
    }

    private sealed class TestTray : ITrayContext
    {
        public string HostMode => "tray";
        public bool SupportsRegionSelectionUi => true;
        public void RequestConfirmation(RecordingConfirmationPresentation presentation, Action<ConfirmationDecision> callback) { }
        public void RequestRegionSelection(int timeoutSeconds, Action<string, int, int, int, int, string, string> callback) { }
        public void SetRecording(RecordingUiPresentation presentation) { }
        public void SetIdle(RecordingUiPresentation presentation) { }
        public void SetAllIdle() { }
        public void ShowError(string text) { }
    }
}
