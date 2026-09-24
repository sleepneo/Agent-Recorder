using System.Reflection;
using AgentRecorder.App;
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
public sealed class StandingLeasePreparedIntentNaturalWakeTests
{
    [Fact]
    public async Task ActivatedIntentBeforeWindowDoesNotCallCoordinatorOrCreateClaims()
    {
        using var database = CreateActivatedDatabase();
        var before = ReadEvidence(database.Store);
        var coordinatorCalls = 0;
        var dispatcher = CreateDispatcher(
            database,
            At(5),
            (request, _) =>
            {
                coordinatorCalls++;
                return Task.FromResult(StandingLeaseOneShotExecutionResult.Rejected("must_not_start"));
            });

        var result = await dispatcher.DispatchAsync("intent-1");

        Assert.Equal(StandingLeaseNaturalWakeDispatchStatus.BeforeWindow, result.Status);
        Assert.Equal("before_occurrence_window", result.Reason);
        Assert.Equal(0, coordinatorCalls);
        Assert.Equal(before, ReadEvidence(database.Store));
    }

    [Fact]
    public async Task ActivatedIntentAfterWindowExpiresWithoutCallingCoordinator()
    {
        using var database = CreateActivatedDatabase();
        var coordinatorCalls = 0;
        var dispatcher = CreateDispatcher(
            database,
            At(60),
            (request, _) =>
            {
                coordinatorCalls++;
                return Task.FromResult(StandingLeaseOneShotExecutionResult.Rejected("must_not_start"));
            });

        var result = await dispatcher.DispatchAsync("intent-1");

        Assert.Equal(StandingLeaseNaturalWakeDispatchStatus.Expired, result.Status);
        Assert.Equal("capture_duration_no_longer_fits", result.Reason);
        Assert.Equal(0, coordinatorCalls);
        Assert.Equal(PlanOccurrenceStatus.Expired, new SqlitePlanOccurrenceRepository(database.Store).Get("occurrence-1").Status);
        Assert.Equal(0L, Scalar(database.Store, "SELECT COUNT(*) FROM recording_runs;"));
        Assert.Equal(0L, Scalar(database.Store, "SELECT COUNT(*) FROM lease_uses;"));
    }

    [Fact]
    public async Task ActivatedIntentEligibleHandoffUsesDurableScopeAndVersionsBeforeCoordinator()
    {
        using var database = CreateActivatedDatabase();
        StandingLeaseOneShotExecutionRequest? capturedRequest = null;
        var coordinatorCalls = 0;
        var digest = ReadText(database.Store, "SELECT scope_digest FROM authorized_capture_scopes;");
        var dispatcher = CreateDispatcher(
            database,
            At(15),
            (request, _) =>
            {
                coordinatorCalls++;
                capturedRequest = request;
                Assert.Equal("plan-1", request.PlanId);
                Assert.Equal("occurrence-1", request.OccurrenceId);
                Assert.Equal("lease-1", request.LeaseId);
                Assert.Equal("scope-1", request.ScopeId);
                Assert.Equal(digest, request.ScopeDigest);
                Assert.Equal(1L, request.ExpectedPlanVersion);
                Assert.Equal(1L, request.ExpectedOccurrenceVersion);
                Assert.Equal(1L, request.ExpectedLeaseVersion);
                Assert.Equal(0L, Scalar(database.Store, "SELECT COUNT(*) FROM recording_runs;") + Scalar(database.Store, "SELECT COUNT(*) FROM lease_uses;"));
                return Task.FromResult(StandingLeaseOneShotExecutionResult.AlreadyCommitted(request.RunId, request.LeaseUseId));
            });

        var result = await dispatcher.DispatchAsync("intent-1");

        Assert.Equal(StandingLeaseNaturalWakeDispatchStatus.AlreadyCommitted, result.Status);
        Assert.Equal("already_committed", result.Reason);
        Assert.Equal(1, coordinatorCalls);
        Assert.NotNull(capturedRequest);
        Assert.Equal("scope-1", result.ScopeId);
        Assert.Equal(digest, result.ScopeDigest);
        Assert.Equal(0L, Scalar(database.Store, "SELECT COUNT(*) FROM recording_runs;") + Scalar(database.Store, "SELECT COUNT(*) FROM lease_uses;"));
    }

    [Fact]
    public async Task UnactivatedMissingOrCorruptIntentBindingFailsClosedBeforeCoordinator()
    {
        using (var regionPending = CreatePreparedDatabase(prepare: false))
        {
            await AssertRejectedWithoutCoordinator(regionPending, "natural_wake_intent_not_activated");
        }

        using (var leaseApprovalPending = CreatePreparedDatabase())
        {
            await AssertRejectedWithoutCoordinator(leaseApprovalPending, "natural_wake_intent_not_activated");
        }

        foreach (var terminalStatus in new[] { "rejected", "expired" })
        {
            using var terminal = CreatePreparedDatabase();
            Execute(terminal.Store, "UPDATE setup_intents SET status_code = $status, terminal_reason_code = 'test_terminal' WHERE intent_id = 'intent-1';", ("$status", terminalStatus));
            await AssertRejectedWithoutCoordinator(terminal, "natural_wake_intent_not_activated");
        }

        using (var missingIntent = CreateActivatedDatabase())
        {
            await AssertRejectedWithoutCoordinator(missingIntent, "natural_wake_intent_not_found", "other-intent");
        }

        using (var missingAssociation = CreateActivatedDatabase())
        {
            Execute(missingAssociation.Store, "UPDATE setup_intents SET scope_id = NULL WHERE intent_id = 'intent-1';");
            await AssertRejectedWithoutCoordinator(missingAssociation, "natural_wake_intent_conflict");
        }

        using (var missingScope = CreateActivatedDatabase())
        {
            Execute(missingScope.Store, "PRAGMA foreign_keys = OFF; DELETE FROM authorized_capture_scopes WHERE scope_id = 'scope-1';");
            await AssertRejectedWithoutCoordinator(missingScope, "natural_wake_intent_conflict");
        }

        using (var relationMismatch = CreateActivatedDatabase())
        {
            Execute(relationMismatch.Store, "PRAGMA foreign_keys = OFF; UPDATE authorized_capture_scopes SET plan_id = 'other-plan' WHERE scope_id = 'scope-1';");
            await AssertRejectedWithoutCoordinator(relationMismatch, "natural_wake_handoff_snapshot_invalid");
        }

        using (var sessionMismatch = CreateActivatedDatabase())
        {
            Execute(sessionMismatch.Store, "UPDATE authorized_capture_scopes SET session_binding = 'other-session' WHERE scope_id = 'scope-1';");
            await AssertRejectedWithoutCoordinator(sessionMismatch, "natural_wake_handoff_snapshot_invalid");
        }

        using (var corruptMetadata = CreateActivatedDatabase())
        {
            Execute(corruptMetadata.Store, "UPDATE setup_intents SET request_digest = $digest WHERE intent_id = 'intent-1';", ("$digest", new string('b', 64)));
            await AssertRejectedWithoutCoordinator(corruptMetadata, "natural_wake_handoff_snapshot_invalid");
        }
    }

    [Fact]
    public async Task HandoffRevalidatesIntentAndScopeBeforeEligibilityCanReachCoordinator()
    {
        using (var statusChanged = CreateActivatedDatabase())
        {
            var coordinatorCalls = 0;
            var dispatcher = CreateDispatcher(
                statusChanged,
                At(15),
                (request, _) =>
                {
                    coordinatorCalls++;
                    return Task.FromResult(StandingLeaseOneShotExecutionResult.Rejected("must_not_start"));
                },
                afterHandoffBeforeEligibilityForTest: () => Execute(
                    statusChanged.Store,
                    "UPDATE setup_intents SET status_code = 'lease_approval_pending' WHERE intent_id = 'intent-1';"));

            var result = await dispatcher.DispatchAsync("intent-1");

            Assert.Equal(StandingLeaseNaturalWakeDispatchStatus.Rejected, result.Status);
            Assert.Equal("eligibility_intent_not_activated", result.Reason);
            Assert.Equal(0, coordinatorCalls);
        }

        using (var scopeChanged = CreateActivatedDatabase())
        {
            var coordinatorCalls = 0;
            var dispatcher = CreateDispatcher(
                scopeChanged,
                At(15),
                (request, _) =>
                {
                    coordinatorCalls++;
                    return Task.FromResult(StandingLeaseOneShotExecutionResult.Rejected("must_not_start"));
                },
                afterHandoffBeforeEligibilityForTest: () => Execute(
                    scopeChanged.Store,
                    "PRAGMA foreign_keys = OFF; UPDATE authorized_capture_scopes SET plan_id = 'other-plan' WHERE scope_id = 'scope-1';"));

            var result = await dispatcher.DispatchAsync("intent-1");

            Assert.Equal(StandingLeaseNaturalWakeDispatchStatus.Rejected, result.Status);
            Assert.Equal("eligibility_snapshot_invalid", result.Reason);
            Assert.Equal(0, coordinatorCalls);
        }
    }

    [Fact]
    public async Task ExistingClaimIsForwardedAsAlreadyClaimedWithoutSecondCoordinatorCall()
    {
        using var database = CreateActivatedDatabase();
        var scope = new SqliteAuthorizedCaptureScopeRepository(database.Store).GetById("scope-1");
        var commit = new SqlitePhase3StartGateTransaction(database.Store).Commit(
            new Phase3StartGateRequest(
                scope.PlanId,
                scope.OccurrenceId,
                scope.LeaseId,
                scope.ScopeId,
                scope.ScopeDigest,
                "run-existing",
                "use-existing",
                1,
                1,
                1,
                scope.ReservedDuration,
                At(15)));
        Assert.Equal(Phase3StartGateCommitStatus.Committed, commit.Status);

        var coordinatorCalls = 0;
        var dispatcher = CreateDispatcher(
            database,
            At(15),
            (request, _) =>
            {
                coordinatorCalls++;
                return Task.FromResult(StandingLeaseOneShotExecutionResult.Rejected("must_not_start"));
            });

        var result = await dispatcher.DispatchAsync("intent-1");

        Assert.Equal(StandingLeaseNaturalWakeDispatchStatus.AlreadyClaimed, result.Status);
        Assert.Equal("occurrence_already_claimed", result.Reason);
        Assert.Equal(0, coordinatorCalls);
        Assert.Equal(1L, Scalar(database.Store, "SELECT COUNT(*) FROM recording_runs;"));
        Assert.Equal(1L, Scalar(database.Store, "SELECT COUNT(*) FROM lease_uses;"));
    }

    [Fact]
    public async Task ConcurrentIntentBoundHandoffsRemainAtMostOnceThroughExistingStartGate()
    {
        using var database = CreateActivatedDatabase();
        var coordinatorCalls = 0;
        using var coordinatorBarrier = new Barrier(2);
        var startGateSync = new object();
        var coordinator = (StandingLeaseOneShotExecutionRequest request, CancellationToken _) =>
        {
            Interlocked.Increment(ref coordinatorCalls);
            coordinatorBarrier.SignalAndWait();
            lock (startGateSync)
            {
                var scope = new SqliteAuthorizedCaptureScopeRepository(database.Store).GetById(request.ScopeId!);
                try
                {
                    var commit = new SqlitePhase3StartGateTransaction(database.Store).Commit(
                        new Phase3StartGateRequest(
                            request.PlanId,
                            request.OccurrenceId,
                            request.LeaseId,
                            request.ScopeId!,
                            request.ScopeDigest!,
                            request.RunId,
                            request.LeaseUseId,
                            request.ExpectedPlanVersion,
                            request.ExpectedOccurrenceVersion,
                            request.ExpectedLeaseVersion,
                            scope.ReservedDuration,
                            At(15)));
                    return Task.FromResult(
                        commit.Status == Phase3StartGateCommitStatus.Committed
                            ? StandingLeaseOneShotExecutionResult.CommittedNotStarted("test_committed_not_started", request.RunId, request.LeaseUseId)
                            : StandingLeaseOneShotExecutionResult.AlreadyCommitted(request.RunId, request.LeaseUseId));
                }
                catch (Phase3PersistenceException exception) when (
                    exception.Code is "start_gate_conflict" or "occurrence_already_claimed")
                {
                    // The double models the production coordinator's stable
                    // rejected result instead of leaking its persistence exception.
                    return Task.FromResult(StandingLeaseOneShotExecutionResult.Rejected("start_gate_conflict"));
                }
            }
        };

        var first = CreateDispatcher(database, At(15), coordinator, () => new StandingLeaseGeneratedExecutionIds("run-first", "use-first"));
        var second = CreateDispatcher(database, At(15), coordinator, () => new StandingLeaseGeneratedExecutionIds("run-second", "use-second"));
        var results = await Task.WhenAll(
            Task.Run(() => first.DispatchAsync("intent-1")),
            Task.Run(() => second.DispatchAsync("intent-1")));

        Assert.Equal(1, results.Count(result => result.Status == StandingLeaseNaturalWakeDispatchStatus.CommittedNotStarted));
        Assert.True(
            results.Count(result =>
                result.Status == StandingLeaseNaturalWakeDispatchStatus.AlreadyClaimed) == 1,
            string.Join(" | ", results.Select(result => $"{result.Status}:{result.Reason}")));
        var claimed = results.Single(result => result.Status == StandingLeaseNaturalWakeDispatchStatus.AlreadyClaimed);
        Assert.Equal("occurrence_already_claimed", claimed.Reason);
        Assert.Equal(2, coordinatorCalls);
        Assert.Equal(1L, Scalar(database.Store, "SELECT COUNT(*) FROM recording_runs;"));
        Assert.Equal(1L, Scalar(database.Store, "SELECT COUNT(*) FROM lease_uses;"));
    }

    [Fact]
    public async Task CancellationAndCoordinatorFailureAreStableAndDoNotExposeExecutionSurface()
    {
        using (var cancelled = CreateActivatedDatabase())
        {
            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();
            var coordinatorCalls = 0;
            var dispatcher = CreateDispatcher(
                cancelled,
                At(15),
                (request, _) =>
                {
                    coordinatorCalls++;
                    return Task.FromResult(StandingLeaseOneShotExecutionResult.Rejected("must_not_start"));
                });

            var result = await dispatcher.DispatchAsync("intent-1", cancellation.Token);

            Assert.Equal(StandingLeaseNaturalWakeDispatchStatus.Rejected, result.Status);
            Assert.Equal("natural_wake_handoff_cancelled", result.Reason);
            Assert.Equal(0, coordinatorCalls);
        }

        using (var failed = CreateActivatedDatabase())
        {
            var dispatcher = CreateDispatcher(
                failed,
                At(15),
                (request, _) => throw new InvalidOperationException("coordinator failure"));

            var result = await dispatcher.DispatchAsync("intent-1");

            Assert.Equal(StandingLeaseNaturalWakeDispatchStatus.Rejected, result.Status);
            Assert.Equal("natural_wake_coordinator_failed", result.Reason);
            Assert.Equal(0L, Scalar(failed.Store, "SELECT COUNT(*) FROM recording_runs;"));
            Assert.Equal(0L, Scalar(failed.Store, "SELECT COUNT(*) FROM lease_uses;"));
        }

        using (var sqliteFailure = CreateActivatedDatabase())
        {
            var dispatcher = CreateDispatcher(
                sqliteFailure,
                At(15),
                (request, _) => Task.FromResult(StandingLeaseOneShotExecutionResult.Rejected("sqlite_failure")));

            var result = await dispatcher.DispatchAsync("intent-1");

            Assert.Equal(StandingLeaseNaturalWakeDispatchStatus.Rejected, result.Status);
            Assert.Equal("sqlite_failure", result.Reason);
            Assert.Equal(0L, Scalar(sqliteFailure.Store, "SELECT COUNT(*) FROM recording_runs;"));
            Assert.Equal(0L, Scalar(sqliteFailure.Store, "SELECT COUNT(*) FROM lease_uses;"));
        }
    }

    [Fact]
    public void IntentBoundNaturalWakeBoundaryIsInternalAndDoesNotExposeSecrets()
    {
        Assert.True(typeof(StandingLeasePreparedIntentNaturalWakeDispatcher).IsNotPublic);
        Assert.Empty(typeof(StandingLeasePreparedIntentNaturalWakeDispatcher).GetConstructors(BindingFlags.Public | BindingFlags.Instance));
        Assert.True(typeof(SqliteStandingLeasePreparedIntentNaturalWakeHandoffTransaction).IsNotPublic);

        var requestProperties = typeof(StandingLeaseOneShotExecutionRequest)
            .GetProperties(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            .Select(property => property.Name)
            .ToArray();
        Assert.DoesNotContain(requestProperties, name => name.Contains("Proof", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(requestProperties, name => name.Contains("Capture", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(requestProperties, name => name.Contains("Environment", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(requestProperties, name => name.Contains("Native", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task GlobalDisablePersistsAcrossRestartBlocksActivationAndWakeAndReenableNeedsNewAuthorization()
    {
        using var database = CreateActivatedDatabase();
        var now = At(10);
        var auditEvents = new List<string>();
        var control = new StandingLeaseSafetyControlService(
            database.Store,
            () => now,
            (eventName, _) => auditEvents.Add(eventName));

        var initial = control.QueryIntent("intent-1");
        Assert.Equal(StandingLeaseSafetyQueryStatus.Available, initial.Status);
        Assert.Equal(UnattendedModeStatus.Enabled, initial.State!.UnattendedMode);
        Assert.Null(initial.State.BlockedReason);

        var disabled = control.DisableUnattended("disable-1");
        Assert.Equal(StandingLeaseSafetyControlResultStatus.Changed, disabled.Status);
        Assert.Equal(StandingLeaseSafetyReasonCodes.UnattendedDisabled, disabled.Reason);

        var reopenedStore = new SqliteOperationalStore(database.Store.DatabasePath);
        reopenedStore.Initialize();
        var persisted = new StandingLeaseSafetyControlService(reopenedStore, () => now)
            .QueryIntent("intent-1");
        Assert.Equal(UnattendedModeStatus.Disabled, persisted.State!.UnattendedMode);
        Assert.Equal(StandingLeaseSafetyReasonCodes.UnattendedDisabled, persisted.State.BlockedReason);

        var coordinatorCalls = 0;
        var dispatcher = new StandingLeasePreparedIntentNaturalWakeDispatcher(
            reopenedStore,
            () => At(15),
            (request, _) =>
            {
                coordinatorCalls++;
                return Task.FromResult(StandingLeaseOneShotExecutionResult.Rejected("must_not_start"));
            },
            () => new StandingLeaseGeneratedExecutionIds("run-disabled", "use-disabled"),
            auditForTest: (eventName, _) => auditEvents.Add(eventName));
        var disabledWake = await dispatcher.DispatchAsync("intent-1");
        Assert.Equal(StandingLeaseNaturalWakeDispatchStatus.Rejected, disabledWake.Status);
        Assert.Equal(StandingLeaseSafetyReasonCodes.UnattendedDisabled, disabledWake.Reason);
        Assert.Equal(0, coordinatorCalls);
        Assert.Equal(0L, Scalar(reopenedStore, "SELECT COUNT(*) FROM recording_runs;"));
        Assert.Contains("standing_lease.wake_blocked", auditEvents);

        now = At(20);
        var enabled = new StandingLeaseSafetyControlService(reopenedStore, () => now)
            .EnableUnattended("enable-1");
        Assert.Equal(StandingLeaseSafetyControlResultStatus.Changed, enabled.Status);

        var afterReenable = new StandingLeaseSafetyControlService(reopenedStore, () => now)
            .QueryIntent("intent-1");
        Assert.Equal(UnattendedModeStatus.Enabled, afterReenable.State!.UnattendedMode);
        Assert.Equal(
            StandingLeaseSafetyReasonCodes.ReenableRequiresNewAuthorization,
            afterReenable.State.BlockedReason);

        var reenableWake = await dispatcher.DispatchAsync("intent-1");
        Assert.Equal(StandingLeaseSafetyReasonCodes.ReenableRequiresNewAuthorization, reenableWake.Reason);
        Assert.Equal(0, coordinatorCalls);

        var duplicateEnable = new StandingLeaseSafetyControlService(reopenedStore, () => now)
            .EnableUnattended("enable-1");
        Assert.Equal(StandingLeaseSafetyControlResultStatus.AlreadyApplied, duplicateEnable.Status);
        Assert.Equal(2L, Scalar(reopenedStore, "SELECT COUNT(*) FROM standing_lease_safety_operations;"));
    }

    [Fact]
    public void RevokePendingLeaseBlocksOccurrenceRetainsScopeAndIsIdempotent()
    {
        using var database = CreatePreparedDatabase();
        var auditEvents = new List<string>();
        var control = new StandingLeaseSafetyControlService(
            database.Store,
            () => At(4),
            (eventName, _) => auditEvents.Add(eventName));

        var result = control.RevokeLease("intent-1", "revoke-1");
        Assert.Equal(StandingLeaseSafetyControlResultStatus.Changed, result.Status);
        Assert.Equal(StandingLeaseSafetyReasonCodes.LeaseRevoked, result.Reason);
        Assert.Equal(ConsentLeaseStatus.Revoked, new SqliteConsentLeaseRepository(database.Store).Get("lease-1").Status);
        Assert.Equal(PlanOccurrenceStatus.Blocked, new SqlitePlanOccurrenceRepository(database.Store).Get("occurrence-1").Status);
        Assert.Equal(1L, Scalar(database.Store, "SELECT COUNT(*) FROM authorized_capture_scopes;"));
        Assert.Equal(1L, Scalar(database.Store, "SELECT COUNT(*) FROM setup_intents;"));
        Assert.Contains("standing_lease.safety_control", auditEvents);

        var repeated = control.RevokeLease("intent-1", "revoke-1");
        Assert.Equal(StandingLeaseSafetyControlResultStatus.AlreadyApplied, repeated.Status);
        Assert.False(repeated.Changed);
        Assert.Equal(1L, Scalar(database.Store, "SELECT COUNT(*) FROM standing_lease_safety_operations;"));
    }

    [Fact]
    public void RevokeTerminalLeaseReturnsStableReasonWithoutPretendingToChangeIt()
    {
        foreach (var (status, expectedReason) in new[]
        {
            ("expired", StandingLeaseSafetyReasonCodes.LeaseExpired),
            ("exhausted", StandingLeaseSafetyReasonCodes.LeaseExhausted),
            ("rejected", StandingLeaseSafetyReasonCodes.LeaseRejected),
        })
        {
            using var database = CreatePreparedDatabase();
            Execute(
                database.Store,
                "UPDATE consent_leases SET status_code = $status WHERE id = 'lease-1';",
                ("$status", status));

            var result = new StandingLeaseSafetyControlService(database.Store, () => At(4))
                .RevokeLease("intent-1", "revoke-terminal");

            Assert.Equal(StandingLeaseSafetyControlResultStatus.Rejected, result.Status);
            Assert.Equal(expectedReason, result.Reason);
            Assert.Equal(status, ReadText(database.Store, "SELECT status_code FROM consent_leases WHERE id = 'lease-1';"));
            Assert.Equal(1L, Scalar(database.Store, "SELECT COUNT(*) FROM standing_lease_safety_operations;"));
        }
    }

    [Fact]
    public async Task StopAllIsAtomicPersistentAndIdempotentButDoesNotBecomeFutureGlobalGate()
    {
        using var database = CreateActivatedDatabase();
        var stopperCalls = 0;
        var control = new StandingLeaseSafetyControlService(
            database.Store,
            () => At(25),
            activeRunStopper: new TestActiveRunStopper(() => stopperCalls++));

        var result = control.StopAllAndRevokeAll("stop-all-1", "operator_stop");
        Assert.Equal(StandingLeaseSafetyControlResultStatus.Changed, result.Status);
        Assert.Equal("stop_all_applied", result.Reason);
        Assert.Equal(ConsentLeaseStatus.Revoked, new SqliteConsentLeaseRepository(database.Store).Get("lease-1").Status);
        Assert.Equal(PlanOccurrenceStatus.Blocked, new SqlitePlanOccurrenceRepository(database.Store).Get("occurrence-1").Status);
        Assert.Equal(1L, Scalar(database.Store, "SELECT COUNT(*) FROM setup_intents;"));
        Assert.Equal(1L, Scalar(database.Store, "SELECT COUNT(*) FROM authorized_capture_scopes;"));
        Assert.Equal(1L, Scalar(database.Store, "SELECT COUNT(*) FROM standing_lease_safety_operations;"));
        Assert.Equal(1, stopperCalls);

        var repeated = control.StopAllAndRevokeAll("stop-all-1", "operator_stop");
        Assert.Equal(StandingLeaseSafetyControlResultStatus.AlreadyApplied, repeated.Status);
        Assert.Equal(1L, Scalar(database.Store, "SELECT COUNT(*) FROM standing_lease_safety_operations;"));
        Assert.Equal(2, stopperCalls);

        var coordinatorCalls = 0;
        var dispatcher = CreateDispatcher(
            database,
            At(30),
            (request, _) =>
            {
                coordinatorCalls++;
                return Task.FromResult(StandingLeaseOneShotExecutionResult.Rejected("must_not_start"));
            });
        var wake = await dispatcher.DispatchAsync("intent-1");
        Assert.Equal(StandingLeaseSafetyReasonCodes.LeaseRevoked, wake.Reason);
        Assert.Equal(0, coordinatorCalls);
        Assert.Equal(0L, Scalar(database.Store, "SELECT COUNT(*) FROM recording_runs;"));
    }

    [Fact]
    public void StopAllWithActiveUnattendedRunDispatchesStopperExactlyOnce()
    {
        using var database = CreateActivatedDatabase();
        var scope = new SqliteAuthorizedCaptureScopeRepository(database.Store).GetById("scope-1");
        var plan = new SqlitePlanDefinitionRepository(database.Store).Get("plan-1");
        var occurrence = new SqlitePlanOccurrenceRepository(database.Store).Get("occurrence-1");
        var lease = new SqliteConsentLeaseRepository(database.Store).Get("lease-1");
        var start = new SqlitePhase3StartGateTransaction(database.Store).Commit(
            new Phase3StartGateRequest(
                plan.Id,
                occurrence.Id,
                lease.Id,
                scope.ScopeId,
                scope.ScopeDigest,
                "run-stop-all",
                "use-stop-all",
                plan.Version,
                occurrence.Version,
                lease.Version,
                scope.ReservedDuration,
                At(15)));
        Assert.Equal(Phase3StartGateCommitStatus.Committed, start.Status);

        var stopperCalls = 0;
        var control = new StandingLeaseSafetyControlService(
            database.Store,
            () => At(25),
            activeRunStopper: new TestActiveRunStopper(() => stopperCalls++));

        var result = control.StopAllAndRevokeAll("stop-all-active-run", "operator_stop");

        Assert.Equal(StandingLeaseSafetyControlResultStatus.Changed, result.Status);
        Assert.Equal(1, stopperCalls);
        Assert.Equal(ConsentLeaseStatus.Revoked, new SqliteConsentLeaseRepository(database.Store).Get("lease-1").Status);
    }

    [Fact]
    public async Task RevokeBeforeFinalBackendGateBlocksStartAndLeavesRunNonRetryable()
    {
        using var database = CreateActivatedDatabase(CaptureAuthorizationSessionBinding.Current);
        using var attempt = BeginBlockedStandingStart(database);
        await attempt.EnvironmentEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));

        var safety = await Task.Run(() => attempt.Safety.RevokeLease("intent-1", "revoke-before-final-gate"));
        Assert.Equal(StandingLeaseSafetyControlResultStatus.Changed, safety.Status);
        Assert.Equal(ConsentLeaseStatus.Revoked, new SqliteConsentLeaseRepository(database.Store).Get("lease-1").Status);
        Assert.Equal(UnattendedModeStatus.Enabled, ReadSafetyMode(database.Store));

        attempt.ReleaseFinalGate();
        var started = await attempt.StartTask.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.True(started.Status != StandingLeaseCaptureExecutionStatus.Started, started.Reason);
        Assert.Equal(0, attempt.Backend.StartCalls);
        Assert.Equal(0, attempt.Backend.StopCalls);
        var firstState = attempt.Engine._recs.GetValueOrDefault("run-1")?.State;
        Assert.True(attempt.Backend.DisposeCalls == 1, $"dispose={attempt.Backend.DisposeCalls}, recs={attempt.Engine._recs.Count}, state={firstState}, result={started.Status}/{started.Reason}");
        AssertStandingStartBlocked(database, attempt.Engine);
    }

    [Fact]
    public async Task StopAllBeforeFinalBackendGateBlocksStartAndPersistsGlobalStop()
    {
        using var database = CreateActivatedDatabase(CaptureAuthorizationSessionBinding.Current);
        using var attempt = BeginBlockedStandingStart(database);
        await attempt.EnvironmentEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));

        var safety = await Task.Run(() => attempt.Safety.StopAllAndRevokeAll("stop-all-before-final-gate"));
        Assert.Equal(StandingLeaseSafetyControlResultStatus.Changed, safety.Status);
        Assert.Equal(ConsentLeaseStatus.Revoked, new SqliteConsentLeaseRepository(database.Store).Get("lease-1").Status);
        Assert.True(ReadSafetyStopAll(database.Store));

        attempt.ReleaseFinalGate();
        var started = await attempt.StartTask.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.NotEqual(StandingLeaseCaptureExecutionStatus.Started, started.Status);
        Assert.Equal(0, attempt.Backend.StartCalls);
        Assert.Equal(0, attempt.Backend.StopCalls);
        var stopAllState = attempt.Engine._recs.GetValueOrDefault("run-1")?.State;
        Assert.True(attempt.Backend.DisposeCalls == 1, $"dispose={attempt.Backend.DisposeCalls}, recs={attempt.Engine._recs.Count}, state={stopAllState}");
        AssertStandingStartBlocked(database, attempt.Engine);
    }

    [Fact]
    public async Task DisableBeforeFinalBackendGateBlocksStartAndPersistsDisabledMode()
    {
        using var database = CreateActivatedDatabase(CaptureAuthorizationSessionBinding.Current);
        using var attempt = BeginBlockedStandingStart(database);
        await attempt.EnvironmentEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));

        var safety = await Task.Run(() => attempt.Safety.DisableUnattended("disable-before-final-gate"));
        Assert.Equal(StandingLeaseSafetyControlResultStatus.Changed, safety.Status);
        Assert.Equal(UnattendedModeStatus.Disabled, ReadSafetyMode(database.Store));
        Assert.Equal(ConsentLeaseStatus.Exhausted, new SqliteConsentLeaseRepository(database.Store).Get("lease-1").Status);

        attempt.ReleaseFinalGate();
        var started = await attempt.StartTask.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.NotEqual(StandingLeaseCaptureExecutionStatus.Started, started.Status);
        Assert.Equal(0, attempt.Backend.StartCalls);
        Assert.Equal(0, attempt.Backend.StopCalls);
        var disableState = attempt.Engine._recs.GetValueOrDefault("run-1")?.State;
        Assert.True(attempt.Backend.DisposeCalls == 1, $"dispose={attempt.Backend.DisposeCalls}, recs={attempt.Engine._recs.Count}, state={disableState}");
        AssertStandingStartBlocked(database, attempt.Engine);
    }

    [Fact]
    public void AlreadyAppliedDisableRetriesPhysicalStopUntilItSucceedsAndStopsAfterTerminality()
    {
        using var database = CreateActivatedDatabase();
        CommitActiveRun(database, "run-disable-retry", "use-disable-retry");
        var stopperCalls = 0;
        var control = new StandingLeaseSafetyControlService(
            database.Store,
            () => At(25),
            activeRunStopper: new TestActiveRunStopper(() =>
            {
                stopperCalls++;
                if (stopperCalls == 1)
                    throw new InvalidOperationException("first physical stop fails");
            }));

        var first = control.DisableUnattended("disable-retry");
        Assert.Equal(StandingLeaseSafetyControlResultStatus.Rejected, first.Status);
        Assert.Equal(StandingLeaseSafetyReasonCodes.ActiveRunStopFailed, first.Reason);
        Assert.Equal(UnattendedModeStatus.Disabled, ReadSafetyMode(database.Store));

        var sameOperationRetry = control.DisableUnattended("disable-retry");
        Assert.Equal(StandingLeaseSafetyControlResultStatus.AlreadyApplied, sameOperationRetry.Status);
        Assert.Equal(2, stopperCalls);

        Execute(database.Store, "UPDATE recording_runs SET status_code = 'settled' WHERE id = 'run-disable-retry';");
        Execute(database.Store, "UPDATE lease_uses SET status_code = 'settled' WHERE id = 'use-disable-retry';");
        Execute(database.Store, "UPDATE plan_occurrences SET status_code = 'completed' WHERE id = 'occurrence-1';");

        var sameOperationAfterTerminal = control.DisableUnattended("disable-retry");
        var newOperationAfterTerminal = control.DisableUnattended("disable-retry-new");
        Assert.Equal(StandingLeaseSafetyControlResultStatus.AlreadyApplied, sameOperationAfterTerminal.Status);
        Assert.Equal(StandingLeaseSafetyControlResultStatus.AlreadyApplied, newOperationAfterTerminal.Status);
        Assert.Equal(2, stopperCalls);
    }

    [Fact]
    public void PhysicalStopRunsOutsideStartSafetyInterlock()
    {
        using var database = CreateActivatedDatabase();
        CommitActiveRun(database, "run-stop-outside-interlock", "use-stop-outside-interlock");
        var interlock = new StandingLeaseStartSafetyInterlock();
        var probeCalls = 0;
        var control = new StandingLeaseSafetyControlService(
            database.Store,
            () => At(25),
            activeRunStopper: new TestActiveRunStopper(() =>
            {
                var probe = Task.Run(() => interlock.Execute("stopper_probe", () => Interlocked.Increment(ref probeCalls)));
                Assert.True(probe.Wait(TimeSpan.FromSeconds(2)), "physical stop was invoked while the start interlock was held");
                Assert.True(probe.IsCompletedSuccessfully);
            }),
            startSafetyInterlock: interlock);

        var result = control.DisableUnattended("disable-outside-interlock");

        Assert.Equal(StandingLeaseSafetyControlResultStatus.Changed, result.Status);
        Assert.Equal(1, probeCalls);
    }

    [Fact]
    public async Task SharedStartSafetyInterlockSerializesBackendAndSafetyLinearization()
    {
        for (var iteration = 0; iteration < 20; iteration++)
        {
            var interlock = new StandingLeaseStartSafetyInterlock();
            var entered = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
            using var release = new ManualResetEventSlim(false);
            var order = new List<string>();

            var startTask = Task.Run(() => interlock.Execute("backend_start", () =>
            {
                order.Add("backend_start");
                entered.SetResult(null);
                release.Wait();
            }));
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));

            var safetyTask = Task.Run(() => interlock.Execute("safety_control", () => order.Add("safety_control")));
            Assert.False(safetyTask.IsCompleted);
            release.Set();
            await startTask.WaitAsync(TimeSpan.FromSeconds(2));
            await safetyTask.WaitAsync(TimeSpan.FromSeconds(2));

            Assert.Equal(new[] { "backend_start", "safety_control" }, order);
        }
    }

    [Fact]
    public async Task BackendStartWinsSafetyRevokeThenStopsExactlyOnceAfterTheInterlock()
    {
        using var database = CreateActivatedDatabase(CaptureAuthorizationSessionBinding.Current);
        using var attempt = BeginStartHeldAtBackendBoundary(database);
        var entered = await Task.WhenAny(attempt.Backend.StartEntered.Task, attempt.StartTask);
        Assert.True(
            ReferenceEquals(attempt.Backend.StartEntered.Task, entered),
            attempt.StartTask.IsCompleted
                ? (await attempt.StartTask).Reason
                : "backend_start_not_entered");

        var revokeTask = Task.Run(() => attempt.Safety.RevokeLease("intent-1", "revoke-after-start-boundary"));
        Assert.False(revokeTask.IsCompleted);
        attempt.ReleaseBackendStart();
        var started = await attempt.StartTask.WaitAsync(TimeSpan.FromSeconds(2));
        var safety = await revokeTask.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(StandingLeaseCaptureExecutionStatus.Started, started.Status);
        Assert.Equal(StandingLeaseSafetyControlResultStatus.Changed, safety.Status);
        Assert.Equal(1, attempt.Backend.StartCalls);
        Assert.Equal(1, attempt.Backend.StopCalls);
        var recording = attempt.Engine._recs["run-1"];
        var persistedRun = new SqliteRecordingRunRepository(database.Store).Get("run-1");
        var persistedUse = new SqliteLeaseUseRepository(database.Store).Get("use-1");
        var persistedOccurrence = new SqlitePlanOccurrenceRepository(database.Store).Get("occurrence-1");
        var sessionAttached = recording.StandingLifecycleSession is not null;
        Assert.True(
            attempt.Backend.DisposeCalls == 1,
            $"dispose={attempt.Backend.DisposeCalls}; rec={recording.State}; finalized={recording.IsFinalized}; session={sessionAttached}; stop_reason={recording.StopReason}; error={recording.Error}; run={persistedRun.Status}; use={persistedUse.Status}; occurrence={persistedOccurrence.Status}");
        AssertStandingStartInterrupted(database, attempt.Engine);
    }

    [Fact]
    public async Task RealNaturalWakeAndSafetyRaceCompletesTwentyControlledRoundsInBothOrders()
    {
        for (var round = 0; round < 20; round++)
        {
            using var database = CreateActivatedDatabase(
                CaptureAuthorizationSessionBinding.Current,
                System.Security.Principal.WindowsIdentity.GetCurrent().User!.Value);
            using var race = CreateProductionRace(database, safetyWins: round % 2 == 0);

            Assert.True(race.Scheduler.Start());
            if (race.SafetyWins)
            {
                await race.FinalGateEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));
                var safetyTask = Task.Run(() => race.Safety.DisableUnattended("race-disable-" + round));
                var safety = await safetyTask.WaitAsync(TimeSpan.FromSeconds(2));
                Assert.Equal(StandingLeaseSafetyControlResultStatus.Changed, safety.Status);
                race.ReleaseFinalGate();
                await race.StartCompleted.Task.WaitAsync(TimeSpan.FromSeconds(2));
                Assert.Equal(0, race.Backend.StartCalls);
            }
            else
            {
                await race.Backend.StartEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));
                var safetyTask = Task.Run(() => race.Safety.DisableUnattended("race-disable-" + round));
                Assert.False(safetyTask.IsCompleted);
                race.ReleaseBackendStart();
                await race.StartCompleted.Task.WaitAsync(TimeSpan.FromSeconds(2));
                var safety = await safetyTask.WaitAsync(TimeSpan.FromSeconds(2));
                Assert.Equal(StandingLeaseSafetyControlResultStatus.Changed, safety.Status);
                Assert.Equal(1, race.Backend.StartCalls);
                Assert.Equal(1, race.Backend.StopCalls);
            }

            Assert.Equal(1L, Scalar(database.Store, "SELECT COUNT(*) FROM recording_runs;"));
            Assert.Equal(1L, Scalar(database.Store, "SELECT COUNT(*) FROM lease_uses;"));
            Assert.NotEqual("settled", ReadText(database.Store, "SELECT status_code FROM recording_runs;"));
        }
    }

    [Fact]
    public async Task ProductionNaturalWakeChainSettlesAndProjectsCompletedSetupState()
    {
        var currentUserSid = System.Security.Principal.WindowsIdentity.GetCurrent().User?.Value;
        Assert.False(string.IsNullOrWhiteSpace(currentUserSid));

        using var database = CreateActivatedDatabase(
            CaptureAuthorizationSessionBinding.Current,
            currentUserSid!);
        var scope = new SqliteAuthorizedCaptureScopeRepository(database.Store).GetById("scope-1");
        var interlock = new StandingLeaseStartSafetyInterlock();
        StandingLeaseSafetyControlService? safety = null;
        var backend = new ProductionChainCaptureBackend();
        using var engine = new RecordingEngine(
            new TestAuditLogger(),
            tracer: null,
            bundleGenerator: null,
            microphoneProvider: null,
            microphoneStatusProvider: null,
            displayTopologyProvider: new ProductionChainDisplayTopologyProvider(scope),
            systemAudioEndpointProvider: null,
            standingStartSafetyInterlock: interlock,
            standingStartSafetyValidator: (ticket, nowUtc) => safety is null
                ? "standing_safety_service_unavailable"
                : safety.ValidateStandingStart(ticket, nowUtc));
        engine.DisableDeadlineWatchdogForTests = true;
        engine.UtcNowForTests = () => At(15).UtcDateTime;
        engine.StandingExecutionEnvironmentProviderForTests = scope => CompleteEnvironment(scope, At(15));
        engine.BackendSelectionFactoryForTests = _ => new CaptureBackendSelection(
            backend,
            StandingLeaseCaptureSpecification.BackendCode,
            new CaptureBackendSelectionEvidence(
                StandingLeaseCaptureSpecification.BackendCode,
                StandingLeaseCaptureSpecification.BackendCode,
                "standing_production_chain_test",
                "test",
                null,
                false));
        engine.SetTray(new StandingEngineTestTray());
        safety = new StandingLeaseSafetyControlService(
            database.Store,
            () => At(15),
            activeRunStopper: new RecordingEngineStandingLeaseActiveRunStopper(engine),
            startSafetyInterlock: interlock);

        var coordinator = new StandingLeaseOneShotExecutionCoordinator(
            database.Store,
            environmentProviderForTest: new FixedStandingExecutionEnvironmentProvider(
                scope => CompleteEnvironment(scope, At(15))),
            backendFactoryForTest: null,
            delayForTest: null,
            utcNowForTest: () => At(15),
            executionStarterForProduction: (ticket, cancellationToken) => Task.FromResult(
                engine.StartStandingCapture(
                    ticket,
                    new StandingEngineTestTray(),
                    candidate => new StandingLeaseOneShotExecutionSession(
                        database.Store,
                        ticket.Specification,
                        candidate,
                        () => At(15),
                        attachBackendCallbacks: false),
                    cancellationToken)));
        var preparedDispatcher = new StandingLeasePreparedIntentNaturalWakeDispatcher(
            database.Store,
            utcNowForTest: () => At(15),
            coordinatorForTest: coordinator.ExecuteAsync,
            auditForTest: (_, _) => { });
        using var scheduler = new StandingLeaseNaturalWakeScheduler(
            database.Store,
            preparedDispatcher,
            () => currentUserSid,
            () => CaptureAuthorizationSessionBinding.Current,
            utcNow: () => At(15),
            pollInterval: TimeSpan.FromSeconds(1),
            audit: (_, _) => { });

        Assert.True(scheduler.Start());
        await backend.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await WaitForAsync(() =>
            ReadText(database.Store, "SELECT status_code FROM recording_runs WHERE id = (SELECT run_id FROM plan_occurrences WHERE id = 'occurrence-1');") == "recording");
        Assert.Equal(1, backend.StartCalls);
        Assert.Equal("recording", ReadText(database.Store, "SELECT status_code FROM recording_runs WHERE id = (SELECT run_id FROM plan_occurrences WHERE id = 'occurrence-1');"));
        Assert.Equal("consumed", ReadText(database.Store, "SELECT status_code FROM lease_uses WHERE occurrence_id = 'occurrence-1';"));

        var configuration = Assert.IsType<CaptureConfig>(backend.Configuration);
        Assert.Equal(30, configuration.DurationSeconds);
        QuantizedTailTestMedia.Generate(configuration.OutputPath, 899, "599/20");
        var probed = FfmpegCaptureBackend.ProbeAuthorizedFixedRateCapture(configuration.OutputPath, configuration);
        Assert.True(probed.DurationSeconds > 30, $"probe duration was {probed.DurationSeconds:R}s");
        Assert.NotNull(probed.QuantizedTailEvidence);
        backend.RaiseNaturalExit(0, probed);

        await WaitForAsync(() =>
            ReadText(database.Store, "SELECT status_code FROM recording_runs WHERE id = (SELECT run_id FROM plan_occurrences WHERE id = 'occurrence-1');") == "settled");
        Assert.Equal("settled", ReadText(database.Store, "SELECT status_code FROM lease_uses WHERE occurrence_id = 'occurrence-1';"));
        Assert.Equal(30000L,
            Scalar(database.Store, "SELECT actual_settled_duration_ms FROM lease_uses WHERE occurrence_id = 'occurrence-1';"));
        Assert.Equal("completed", ReadText(database.Store, "SELECT status_code FROM plan_occurrences WHERE id = 'occurrence-1';"));
        Assert.Equal(0, backend.StopCalls);
        Assert.Equal(1, backend.DisposeCalls);

        using var setup = new StandingPlanSetupCoordinator(
            database.Store,
            new TestAuditLogger(),
            new NoOpStandingPlanSetupUi(),
            safety,
            executionSupportedForTest: () => true,
            recordingExistsForTest: engine.HasRecording);
        var projected = setup.Get("intent-1");
        var settledRunId = ReadText(
            database.Store,
            "SELECT run_id FROM plan_occurrences WHERE id = 'occurrence-1';");

        Assert.NotNull(projected);
        Assert.Equal("completed", projected!.StatusCode);
        Assert.Equal(settledRunId, projected.RunId);
        Assert.Equal("/api/v1/recordings/" + Uri.EscapeDataString(projected.RunId!), projected.RecordingStatusUrl);
        Assert.NotNull(projected.StartedAtUtc);
        Assert.NotNull(projected.CompletedAtUtc);
        Assert.True(projected.CompletedAtUtc >= projected.StartedAtUtc);
        Assert.True(projected.StatusVersion > 0);
    }

    [Fact]
    public void StopAllPhysicalStopFailureReturnsStableFailureAfterPersistence()
    {
        using var database = CreateActivatedDatabase();
        var auditEvents = new List<string>();
        var stopperCalls = 0;
        var control = new StandingLeaseSafetyControlService(
            database.Store,
            () => At(25),
            (eventName, _) => auditEvents.Add(eventName),
            activeRunStopper: new TestActiveRunStopper(() =>
            {
                stopperCalls++;
                throw new InvalidOperationException("physical stop failed");
            }));

        var result = control.StopAllAndRevokeAll("stop-all-stop-failure", "operator_stop");

        Assert.Equal(StandingLeaseSafetyControlResultStatus.Rejected, result.Status);
        Assert.Equal(StandingLeaseSafetyReasonCodes.ActiveRunStopFailed, result.Reason);
        Assert.Equal(1, stopperCalls);
        Assert.Equal(ConsentLeaseStatus.Revoked, new SqliteConsentLeaseRepository(database.Store).Get("lease-1").Status);
        Assert.Equal(1L, Scalar(database.Store, "SELECT stop_all_applied FROM unattended_safety_state WHERE state_id = 'global';"));
        Assert.Equal(1L, Scalar(database.Store, "SELECT COUNT(*) FROM standing_lease_safety_operations;"));
        Assert.Contains("standing_lease.active_run_stop_failed", auditEvents);

        var replay = control.StopAllAndRevokeAll("stop-all-stop-failure", "operator_stop");
        Assert.Equal(StandingLeaseSafetyControlResultStatus.Rejected, replay.Status);
        Assert.Equal(StandingLeaseSafetyReasonCodes.ActiveRunStopFailed, replay.Reason);
        Assert.Equal(2, stopperCalls);
        Assert.Equal(1L, Scalar(database.Store, "SELECT COUNT(*) FROM standing_lease_safety_operations;"));
    }

    [Fact]
    public async Task StopAllRevokesCurrentChainsButAllowsFreshApprovalAndNaturalWakeAfterDisableEnable()
    {
        using var database = CreateActivatedDatabase();
        var stopAll = new StandingLeaseSafetyControlService(database.Store, () => At(25));

        var firstStop = stopAll.StopAllAndRevokeAll("stop-all-1", "operator_stop");
        Assert.Equal(StandingLeaseSafetyControlResultStatus.Changed, firstStop.Status);
        Assert.Equal(ConsentLeaseStatus.Revoked, new SqliteConsentLeaseRepository(database.Store).Get("lease-1").Status);
        Assert.Equal(PlanOccurrenceStatus.Blocked, new SqlitePlanOccurrenceRepository(database.Store).Get("occurrence-1").Status);

        var oldCoordinatorCalls = 0;
        var oldWake = await CreateDispatcher(
            database,
            At(30),
            (request, _) =>
            {
                oldCoordinatorCalls++;
                return Task.FromResult(StandingLeaseOneShotExecutionResult.Rejected("must_not_start"));
            }).DispatchAsync("intent-1");
        Assert.Equal(StandingLeaseSafetyReasonCodes.LeaseRevoked, oldWake.Reason);
        Assert.Equal(0, oldCoordinatorCalls);

        CreateAndActivateAdditionalChain(
            database,
            suffix: "2",
            createdAtSeconds: 30,
            selectedAtSeconds: 31,
            approvedAtSeconds: 32);

        var freshCoordinatorCalls = 0;
        var freshWake = await CreateDispatcher(
            database,
            At(45),
            (request, _) =>
            {
                freshCoordinatorCalls++;
                return Task.FromResult(
                    StandingLeaseOneShotExecutionResult.CommittedNotStarted(
                        "test_committed_not_started",
                        request.RunId,
                        request.LeaseUseId));
            },
            () => new StandingLeaseGeneratedExecutionIds("run-fresh", "use-fresh"))
            .DispatchAsync("intent-2");
        Assert.Equal("test_committed_not_started", freshWake.Reason);
        Assert.Equal(1, freshCoordinatorCalls);
        Assert.Equal(0L, Scalar(database.Store, "SELECT COUNT(*) FROM recording_runs;"));
        Assert.Equal(0L, Scalar(database.Store, "SELECT COUNT(*) FROM lease_uses;"));

        var disabled = new StandingLeaseSafetyControlService(database.Store, () => At(50))
            .DisableUnattended("disable-after-stop-all");
        Assert.Equal(StandingLeaseSafetyControlResultStatus.Changed, disabled.Status);

        var enabled = new StandingLeaseSafetyControlService(database.Store, () => At(60))
            .EnableUnattended("enable-after-stop-all");
        Assert.Equal(StandingLeaseSafetyControlResultStatus.Changed, enabled.Status);

        var oldAuthorizationAfterReenable = new StandingLeaseSafetyControlService(database.Store, () => At(65))
            .QueryIntent("intent-2");
        Assert.Equal(StandingLeaseSafetyQueryStatus.Available, oldAuthorizationAfterReenable.Status);
        Assert.Equal(
            StandingLeaseSafetyReasonCodes.ReenableRequiresNewAuthorization,
            oldAuthorizationAfterReenable.State!.BlockedReason);

        var reenabledOldCoordinatorCalls = 0;
        var reenabledOldWake = await CreateDispatcher(
            database,
            At(65),
            (request, _) =>
            {
                reenabledOldCoordinatorCalls++;
                return Task.FromResult(StandingLeaseOneShotExecutionResult.Rejected("must_not_start"));
            }).DispatchAsync("intent-2");
        Assert.Equal(StandingLeaseSafetyReasonCodes.ReenableRequiresNewAuthorization, reenabledOldWake.Reason);
        Assert.Equal(0, reenabledOldCoordinatorCalls);

        CreateAndActivateAdditionalChain(
            database,
            suffix: "3",
            createdAtSeconds: 70,
            selectedAtSeconds: 71,
            approvedAtSeconds: 72);

        var afterReenableCoordinatorCalls = 0;
        var afterReenableWake = await CreateDispatcher(
            database,
            At(85),
            (request, _) =>
            {
                afterReenableCoordinatorCalls++;
                return Task.FromResult(
                    StandingLeaseOneShotExecutionResult.CommittedNotStarted(
                        "test_committed_after_reenable",
                        request.RunId,
                        request.LeaseUseId));
            },
            () => new StandingLeaseGeneratedExecutionIds("run-after-reenable", "use-after-reenable"))
            .DispatchAsync("intent-3");
        Assert.Equal("test_committed_after_reenable", afterReenableWake.Reason);
        Assert.Equal(1, afterReenableCoordinatorCalls);
        Assert.Equal(0L, Scalar(database.Store, "SELECT COUNT(*) FROM recording_runs;"));
        Assert.Equal(0L, Scalar(database.Store, "SELECT COUNT(*) FROM lease_uses;"));

        var secondStop = new StandingLeaseSafetyControlService(database.Store, () => At(90))
            .StopAllAndRevokeAll("stop-all-2", "operator_stop_again");
        Assert.Equal(StandingLeaseSafetyControlResultStatus.Changed, secondStop.Status);
        Assert.Equal(ConsentLeaseStatus.Revoked, new SqliteConsentLeaseRepository(database.Store).Get("lease-2").Status);
        Assert.Equal(ConsentLeaseStatus.Revoked, new SqliteConsentLeaseRepository(database.Store).Get("lease-3").Status);
        Assert.Equal(PlanOccurrenceStatus.Blocked, new SqlitePlanOccurrenceRepository(database.Store).Get("occurrence-2").Status);
        Assert.Equal(PlanOccurrenceStatus.Blocked, new SqlitePlanOccurrenceRepository(database.Store).Get("occurrence-3").Status);
        Assert.Equal(4L, Scalar(database.Store, "SELECT COUNT(*) FROM standing_lease_safety_operations;"));
        Assert.Equal("stop-all-2", ReadText(database.Store, "SELECT stop_all_operation_id FROM unattended_safety_state WHERE state_id = 'global';"));

        var repeatedSecondStop = new StandingLeaseSafetyControlService(database.Store, () => At(91))
            .StopAllAndRevokeAll("stop-all-2", "operator_stop_again");
        Assert.Equal(StandingLeaseSafetyControlResultStatus.AlreadyApplied, repeatedSecondStop.Status);
        Assert.Equal(4L, Scalar(database.Store, "SELECT COUNT(*) FROM standing_lease_safety_operations;"));
    }

    [Fact]
    public async Task ApprovalBeforeStopAllLinearizesAsActivatedThenRevokedWithoutHalfState()
    {
        using var database = CreatePreparedDatabase();
        var digest = ReadText(database.Store, "SELECT scope_digest FROM authorized_capture_scopes;");
        var receipt = StandingLeaseLocalApprovalReceipt.CreateForTest(
            "approval-linearized", "plan-1", "occurrence-1", "lease-1", "scope-1", digest,
            "S-1-5-21-test", "session-1", At(3));

        using var activationEntered = new ManualResetEventSlim();
        using var releaseActivation = new ManualResetEventSlim();
        var activation = new StandingLeasePreparedIntentApprovalActivationService(
            database.Store,
            () => At(3),
            beforeCommitForTest: (_, _) =>
            {
                activationEntered.Set();
                Assert.True(releaseActivation.Wait(TimeSpan.FromSeconds(5)));
            });
        var approvalTask = Task.Run(() => activation.Activate("intent-1", receipt));
        Assert.True(activationEntered.Wait(TimeSpan.FromSeconds(5)));

        var stopAll = new StandingLeaseSafetyControlService(database.Store, () => At(4));
        var stopAllTask = Task.Run(() => stopAll.StopAllAndRevokeAll("stop-all-linearized", "operator_stop"));
        releaseActivation.Set();

        var approvalResult = await approvalTask;
        var stopAllResult = await stopAllTask;
        Assert.Equal(StandingLeaseAuthorizationActivationStatus.Activated, approvalResult.Status);
        Assert.Equal(StandingLeaseSafetyControlResultStatus.Changed, stopAllResult.Status);
        Assert.Equal(ConsentLeaseStatus.Revoked, new SqliteConsentLeaseRepository(database.Store).Get("lease-1").Status);
        Assert.Equal(PlanOccurrenceStatus.Blocked, new SqlitePlanOccurrenceRepository(database.Store).Get("occurrence-1").Status);
        Assert.Equal(StandingSetupIntentStatus.Activated, ReadIntentStatus(database.Store, "intent-1"));
        Assert.Equal(1L, Scalar(database.Store, "SELECT COUNT(*) FROM standing_lease_safety_operations;"));
    }

    [Fact]
    public async Task DisabledApprovalAndRevokedWakeFailClosedWithoutCoordinator()
    {
        using (var disabled = CreatePreparedDatabase())
        {
            var control = new StandingLeaseSafetyControlService(disabled.Store, () => At(2));
            Assert.Equal(StandingLeaseSafetyControlResultStatus.Changed, control.DisableUnattended("disable-approval").Status);
            var digest = ReadText(disabled.Store, "SELECT scope_digest FROM authorized_capture_scopes;");
            var receipt = StandingLeaseLocalApprovalReceipt.CreateForTest(
                "approval-disabled", "plan-1", "occurrence-1", "lease-1", "scope-1", digest,
                "S-1-5-21-test", "session-1", At(2));
            var activation = new StandingLeasePreparedIntentApprovalActivationService(disabled.Store, () => At(3))
                .Activate("intent-1", receipt);
            Assert.Equal(StandingLeaseAuthorizationActivationStatus.Rejected, activation.Status);
            Assert.Equal(StandingLeaseSafetyReasonCodes.UnattendedDisabled, activation.Reason);
            Assert.Equal("pending", ReadText(disabled.Store, "SELECT status_code FROM consent_leases WHERE id = 'lease-1';"));
        }

        using var revoked = CreateActivatedDatabase();
        var controlForRevoke = new StandingLeaseSafetyControlService(revoked.Store, () => At(4));
        Assert.Equal(StandingLeaseSafetyControlResultStatus.Changed, controlForRevoke.RevokeLease("intent-1", "revoke-wake").Status);
        var coordinatorCalls = 0;
        var dispatcher = CreateDispatcher(
            revoked,
            At(15),
            (request, _) =>
            {
                coordinatorCalls++;
                return Task.FromResult(StandingLeaseOneShotExecutionResult.Rejected("must_not_start"));
            });
        var wake = await dispatcher.DispatchAsync("intent-1");
        Assert.Equal(StandingLeaseSafetyReasonCodes.LeaseRevoked, wake.Reason);
        Assert.Equal(0, coordinatorCalls);
        Assert.Equal(0L, Scalar(revoked.Store, "SELECT COUNT(*) FROM recording_runs;"));
        Assert.Equal(0L, Scalar(revoked.Store, "SELECT COUNT(*) FROM lease_uses;"));
    }

    [Fact]
    public async Task RevokeAndNaturalWakeRaceLinearizesAtTheLeaseTransactionBoundary()
    {
        using var database = CreateActivatedDatabase();
        var control = new StandingLeaseSafetyControlService(database.Store, () => At(15));
        var coordinatorCalls = 0;
        var coordinator = async (StandingLeaseOneShotExecutionRequest request, CancellationToken _) =>
        {
            Interlocked.Increment(ref coordinatorCalls);
            var scope = new SqliteAuthorizedCaptureScopeRepository(database.Store).GetById(request.ScopeId!);
            try
            {
                var commit = new SqlitePhase3StartGateTransaction(database.Store).Commit(
                    new Phase3StartGateRequest(
                        request.PlanId,
                        request.OccurrenceId,
                        request.LeaseId,
                        request.ScopeId!,
                        request.ScopeDigest!,
                        request.RunId,
                        request.LeaseUseId,
                        request.ExpectedPlanVersion,
                        request.ExpectedOccurrenceVersion,
                        request.ExpectedLeaseVersion,
                        scope.ReservedDuration,
                        At(15)));
                return await Task.FromResult(
                    commit.Status == Phase3StartGateCommitStatus.Committed
                        ? StandingLeaseOneShotExecutionResult.CommittedNotStarted("test_committed_not_started", request.RunId, request.LeaseUseId)
                        : StandingLeaseOneShotExecutionResult.AlreadyCommitted(request.RunId, request.LeaseUseId));
            }
            catch (Phase3PersistenceException exception)
            {
                return await Task.FromResult(StandingLeaseOneShotExecutionResult.Rejected(exception.Code));
            }
        };

        var dispatcher = CreateDispatcher(database, At(15), coordinator);
        var wakeTask = Task.Run(() => dispatcher.DispatchAsync("intent-1"));
        var revokeTask = Task.Run(() => control.RevokeLease("intent-1", "revoke-race"));
        await Task.WhenAll(wakeTask, revokeTask);
        var wakeResult = await wakeTask;

        Assert.InRange(coordinatorCalls, 0, 1);
        Assert.InRange(Scalar(database.Store, "SELECT COUNT(*) FROM recording_runs;"), 0, 1);
        Assert.InRange(Scalar(database.Store, "SELECT COUNT(*) FROM lease_uses;"), 0, 1);
        Assert.True(
            wakeResult.Reason is StandingLeaseSafetyReasonCodes.LeaseRevoked or "lease_not_active" or "test_committed_not_started" or "already_committed",
            wakeResult.Reason);
    }

    [Fact]
    public async Task RevokeWinsBeforeStartGateAndConcurrencyIsReclassifiedAsLeaseRevoked()
    {
        using var database = CreateActivatedDatabase();
        var control = new StandingLeaseSafetyControlService(database.Store, () => At(15));
        var coordinatorCalls = 0;
        var dispatcher = CreateDispatcher(
            database,
            At(15),
            (request, _) =>
            {
                Interlocked.Increment(ref coordinatorCalls);
                var revoke = control.RevokeLease("intent-1", "revoke-before-start-gate");
                Assert.Equal(StandingLeaseSafetyControlResultStatus.Changed, revoke.Status);

                var scope = new SqliteAuthorizedCaptureScopeRepository(database.Store).GetById(request.ScopeId!);
                try
                {
                    var ignoredCommit = new SqlitePhase3StartGateTransaction(database.Store).Commit(
                        new Phase3StartGateRequest(
                            request.PlanId,
                            request.OccurrenceId,
                            request.LeaseId,
                            scope.ScopeId,
                            scope.ScopeDigest,
                            request.RunId,
                            request.LeaseUseId,
                            request.ExpectedPlanVersion,
                            request.ExpectedOccurrenceVersion,
                            request.ExpectedLeaseVersion,
                            scope.ReservedDuration,
                            At(15)));
                    return Task.FromResult(
                        StandingLeaseOneShotExecutionResult.Rejected("test_expected_start_gate_conflict"));
                }
                catch (Phase3PersistenceException exception)
                {
                    return Task.FromResult(StandingLeaseOneShotExecutionResult.Rejected(exception.Code));
                }
            });

        var result = await dispatcher.DispatchAsync("intent-1");

        Assert.Equal(StandingLeaseNaturalWakeDispatchStatus.Rejected, result.Status);
        Assert.Equal(StandingLeaseSafetyReasonCodes.LeaseRevoked, result.Reason);
        Assert.Equal(1, coordinatorCalls);
        Assert.Equal(ConsentLeaseStatus.Revoked, new SqliteConsentLeaseRepository(database.Store).Get("lease-1").Status);
        Assert.Equal(PlanOccurrenceStatus.Blocked, new SqlitePlanOccurrenceRepository(database.Store).Get("occurrence-1").Status);
        Assert.Equal(0L, Scalar(database.Store, "SELECT COUNT(*) FROM recording_runs;"));
        Assert.Equal(0L, Scalar(database.Store, "SELECT COUNT(*) FROM lease_uses;"));
    }

    [Fact]
    public async Task StartGateWinsBeforeRevokeReturnsCommittedAndKeepsOneClaim()
    {
        using var database = CreateActivatedDatabase();
        var control = new StandingLeaseSafetyControlService(database.Store, () => At(15));
        var coordinatorCalls = 0;
        var dispatcher = CreateDispatcher(
            database,
            At(15),
            (request, _) =>
            {
                Interlocked.Increment(ref coordinatorCalls);
                var scope = new SqliteAuthorizedCaptureScopeRepository(database.Store).GetById(request.ScopeId!);
                var commit = new SqlitePhase3StartGateTransaction(database.Store).Commit(
                    new Phase3StartGateRequest(
                        request.PlanId,
                        request.OccurrenceId,
                        request.LeaseId,
                        scope.ScopeId,
                        scope.ScopeDigest,
                        request.RunId,
                        request.LeaseUseId,
                        request.ExpectedPlanVersion,
                        request.ExpectedOccurrenceVersion,
                        request.ExpectedLeaseVersion,
                        scope.ReservedDuration,
                        At(15)));
                Assert.Equal(Phase3StartGateCommitStatus.Committed, commit.Status);

                var revoke = control.RevokeLease("intent-1", "revoke-after-start-gate");
                Assert.Equal(StandingLeaseSafetyControlResultStatus.Changed, revoke.Status);
                return Task.FromResult(
                    StandingLeaseOneShotExecutionResult.CommittedNotStarted(
                        "test_committed_not_started",
                        request.RunId,
                        request.LeaseUseId));
            });

        var result = await dispatcher.DispatchAsync("intent-1");

        Assert.Equal(StandingLeaseNaturalWakeDispatchStatus.CommittedNotStarted, result.Status);
        Assert.Equal("test_committed_not_started", result.Reason);
        Assert.Equal(1, coordinatorCalls);
        Assert.Equal(ConsentLeaseStatus.Revoked, new SqliteConsentLeaseRepository(database.Store).Get("lease-1").Status);
        Assert.Equal(1L, Scalar(database.Store, "SELECT COUNT(*) FROM recording_runs;"));
        Assert.Equal(1L, Scalar(database.Store, "SELECT COUNT(*) FROM lease_uses;"));
    }

    [Fact]
    public async Task UnprovenConcurrencyConflictRemainsFailClosed()
    {
        using var database = CreateActivatedDatabase();
        var coordinatorCalls = 0;
        var dispatcher = CreateDispatcher(
            database,
            At(15),
            (request, _) =>
            {
                Interlocked.Increment(ref coordinatorCalls);
                return Task.FromResult(StandingLeaseOneShotExecutionResult.Rejected("concurrency_conflict"));
            });

        var result = await dispatcher.DispatchAsync("intent-1");

        Assert.Equal(StandingLeaseNaturalWakeDispatchStatus.Rejected, result.Status);
        Assert.Equal("concurrency_conflict", result.Reason);
        Assert.Equal(1, coordinatorCalls);
        Assert.Equal(ConsentLeaseStatus.Active, new SqliteConsentLeaseRepository(database.Store).Get("lease-1").Status);
        Assert.Equal(0L, Scalar(database.Store, "SELECT COUNT(*) FROM recording_runs;"));
        Assert.Equal(0L, Scalar(database.Store, "SELECT COUNT(*) FROM lease_uses;"));
    }

    [Fact]
    public async Task RevalidationSnapshotTamperDoesNotBecomeLeaseRevoked()
    {
        using var database = CreateActivatedDatabase();
        var dispatcher = CreateDispatcher(
            database,
            At(15),
            (request, _) =>
            {
                Execute(
                    database.Store,
                    "PRAGMA foreign_keys = OFF; UPDATE authorized_capture_scopes SET plan_id = 'other-plan' WHERE scope_id = 'scope-1';");
                return Task.FromResult(StandingLeaseOneShotExecutionResult.Rejected("concurrency_conflict"));
            });

        var result = await dispatcher.DispatchAsync("intent-1");

        Assert.Equal(StandingLeaseNaturalWakeDispatchStatus.Rejected, result.Status);
        Assert.Equal("natural_wake_concurrency_revalidation_snapshot_invalid", result.Reason);
        Assert.Equal(ConsentLeaseStatus.Active, new SqliteConsentLeaseRepository(database.Store).Get("lease-1").Status);
        Assert.Equal(0L, Scalar(database.Store, "SELECT COUNT(*) FROM recording_runs;"));
        Assert.Equal(0L, Scalar(database.Store, "SELECT COUNT(*) FROM lease_uses;"));
    }

    [Fact]
    public async Task MissingGlobalSafetyStateFailsClosedForQueryAndNaturalWake()
    {
        using var database = CreateActivatedDatabase();
        Execute(database.Store, "DELETE FROM unattended_safety_state WHERE state_id = 'global';");

        var query = new StandingLeaseSafetyControlService(database.Store, () => At(15))
            .QueryIntent("intent-1");
        Assert.Equal(StandingLeaseSafetyQueryStatus.Rejected, query.Status);
        Assert.Equal("safety_snapshot_invalid", query.Reason);

        var coordinatorCalls = 0;
        var dispatcher = CreateDispatcher(
            database,
            At(15),
            (request, _) =>
            {
                coordinatorCalls++;
                return Task.FromResult(StandingLeaseOneShotExecutionResult.Rejected("must_not_start"));
            });
        var wake = await dispatcher.DispatchAsync("intent-1");
        Assert.Equal(StandingLeaseNaturalWakeDispatchStatus.Rejected, wake.Status);
        Assert.Equal("natural_wake_handoff_snapshot_invalid", wake.Reason);
        Assert.Equal(0, coordinatorCalls);
        Assert.Equal(0L, Scalar(database.Store, "SELECT COUNT(*) FROM recording_runs;"));
    }

    [Fact]
    public void StopAllRollbackLeavesEverySafetyWriteUncommitted()
    {
        using var database = CreateActivatedDatabase();
        var failing = new StandingLeaseSafetyControlService(
            database.Store,
            () => At(25),
            beforeCommitForTest: (_, _) => throw new InvalidOperationException("rollback"));

        var failed = failing.StopAllAndRevokeAll("stop-all-rollback", "operator_stop");
        Assert.Equal(StandingLeaseSafetyControlResultStatus.Rejected, failed.Status);
        Assert.Equal("safety_sqlite_failure", failed.Reason);
        Assert.Equal("active", ReadText(database.Store, "SELECT status_code FROM consent_leases WHERE id = 'lease-1';"));
        Assert.Equal("authorized", ReadText(database.Store, "SELECT status_code FROM plan_occurrences WHERE id = 'occurrence-1';"));
        Assert.Equal(0L, Scalar(database.Store, "SELECT stop_all_applied FROM unattended_safety_state WHERE state_id = 'global';"));
        Assert.Equal(0L, Scalar(database.Store, "SELECT COUNT(*) FROM standing_lease_safety_operations;"));
    }

    private static async Task AssertRejectedWithoutCoordinator(
        PreparedDatabase database,
        string expectedReason,
        string intentId = "intent-1")
    {
        var coordinatorCalls = 0;
        var dispatcher = CreateDispatcher(
            database,
            At(15),
            (request, _) =>
            {
                coordinatorCalls++;
                return Task.FromResult(StandingLeaseOneShotExecutionResult.Rejected("must_not_start"));
            });

        var result = await dispatcher.DispatchAsync(intentId);

        Assert.Equal(StandingLeaseNaturalWakeDispatchStatus.Rejected, result.Status);
        Assert.Equal(expectedReason, result.Reason);
        Assert.Equal(0, coordinatorCalls);
        Assert.Equal(0L, Scalar(database.Store, "SELECT COUNT(*) FROM recording_runs;"));
        Assert.Equal(0L, Scalar(database.Store, "SELECT COUNT(*) FROM lease_uses;"));
    }

    private static StandingLeasePreparedIntentNaturalWakeDispatcher CreateDispatcher(
        PreparedDatabase database,
        DateTimeOffset nowUtc,
        Func<StandingLeaseOneShotExecutionRequest, CancellationToken, Task<StandingLeaseOneShotExecutionResult>> coordinator,
        Func<StandingLeaseGeneratedExecutionIds>? idGenerator = null,
        Action? afterHandoffBeforeEligibilityForTest = null) =>
        new(
            database.Store,
            () => nowUtc,
            coordinator,
            idGenerator ?? (() => new StandingLeaseGeneratedExecutionIds("run-generated", "use-generated")),
            afterHandoffBeforeEligibilityForTest);

    private static PreparedDatabase CreateActivatedDatabase(
        string? sessionBinding = null,
        string currentUserSid = "S-1-5-21-test")
    {
        sessionBinding ??= "session-1";
        var database = CreatePreparedDatabase(
            sessionBinding: sessionBinding,
            currentUserSid: currentUserSid);
        var digest = ReadText(database.Store, "SELECT scope_digest FROM authorized_capture_scopes;");
        var receipt = StandingLeaseLocalApprovalReceipt.CreateForTest(
            "approval-1", "plan-1", "occurrence-1", "lease-1", "scope-1", digest,
            currentUserSid, sessionBinding, At(3));
        var result = new StandingLeasePreparedIntentApprovalActivationService(database.Store, () => At(3))
            .Activate("intent-1", receipt);
        Assert.Equal(StandingLeaseAuthorizationActivationStatus.Activated, result.Status);
        return database;
    }

    private static PreparedDatabase CreatePreparedDatabase(
        bool prepare = true,
        string sessionBinding = "session-1",
        string currentUserSid = "S-1-5-21-test")
    {
        var database = new PreparedDatabase();
        var intent = StandingSetupIntentSnapshot.CreateForTrustedSetupAdapter(
            "intent-1", "request-1", currentUserSid, sessionBinding, At(0), At(300),
            At(10), At(20), At(80), TimeSpan.FromSeconds(30), At(3600),
            Path.Combine(database.RootPath, "captures"), "capture.mp4");
        Assert.Equal(StandingSetupIntentResultStatus.Created, new StandingSetupIntentService(database.Store, () => At(1)).CreateOrGet(intent).Result);
        if (prepare)
        {
            var selection = StandingFixedRegionSelectionSnapshot.CreateForTrustedLocalSelectionAdapter(
                "intent-1", currentUserSid, sessionBinding, At(2), "display-a",
                new AuthorizedPhysicalRectangle(0, 0, 1920, 1080),
                new AuthorizedPhysicalRectangle(10, 20, 640, 480),
                96, 96, 1920, 1080, AuthorizedDisplayOrientation.Landscape, new string('a', 64));
            var prepared = new StandingLeasePreparationService(database.Store, () => At(2), new FixedIds()).Prepare(selection);
            Assert.Equal(StandingLeasePreparationResultStatus.Prepared, prepared.Status);
        }

        return database;
    }

    private static void CreateAndActivateAdditionalChain(
        PreparedDatabase database,
        string suffix,
        int createdAtSeconds,
        int selectedAtSeconds,
        int approvedAtSeconds)
    {
        var intentId = "intent-" + suffix;
        var intent = StandingSetupIntentSnapshot.CreateForTrustedSetupAdapter(
            intentId,
            "request-" + suffix,
            "S-1-5-21-test",
            "session-1",
            At(createdAtSeconds - 1),
            At(300),
            At(createdAtSeconds + 10),
            At(createdAtSeconds + 20),
            At(createdAtSeconds + 80),
            TimeSpan.FromSeconds(30),
            At(3600),
            Path.Combine(Path.GetTempPath(), "AgentRecorderNaturalWake", "captures"),
            "capture-" + suffix + ".mp4");
        Assert.Equal(
            StandingSetupIntentResultStatus.Created,
            new StandingSetupIntentService(database.Store, () => At(createdAtSeconds)).CreateOrGet(intent).Result);

        var selection = StandingFixedRegionSelectionSnapshot.CreateForTrustedLocalSelectionAdapter(
            intentId,
            "S-1-5-21-test",
            "session-1",
            At(selectedAtSeconds),
            "display-" + suffix,
            new AuthorizedPhysicalRectangle(0, 0, 1920, 1080),
            new AuthorizedPhysicalRectangle(20, 30, 640, 480),
            96,
            96,
            1920,
            1080,
            AuthorizedDisplayOrientation.Landscape,
            new string(suffix[0], 64));
        var prepared = new StandingLeasePreparationService(
            database.Store,
            () => At(selectedAtSeconds),
            new AdditionalIds(suffix)).Prepare(selection);
        Assert.Equal(StandingLeasePreparationResultStatus.Prepared, prepared.Status);

        var planId = "plan-" + suffix;
        var occurrenceId = "occurrence-" + suffix;
        var leaseId = "lease-" + suffix;
        var scopeId = "scope-" + suffix;
        var digest = ReadText(
            database.Store,
            $"SELECT scope_digest FROM authorized_capture_scopes WHERE scope_id = '{scopeId}';");
        var receipt = StandingLeaseLocalApprovalReceipt.CreateForTest(
            "approval-" + suffix,
            planId,
            occurrenceId,
            leaseId,
            scopeId,
            digest,
            "S-1-5-21-test",
            "session-1",
            At(approvedAtSeconds));
        var activation = new StandingLeasePreparedIntentApprovalActivationService(
            database.Store,
            () => At(approvedAtSeconds)).Activate(intentId, receipt);
        Assert.Equal(StandingLeaseAuthorizationActivationStatus.Activated, activation.Status);
    }

    private static (long RunCount, long UseCount, string OccurrenceStatus) ReadEvidence(SqliteOperationalStore store) =>
        (Scalar(store, "SELECT COUNT(*) FROM recording_runs;"),
         Scalar(store, "SELECT COUNT(*) FROM lease_uses;"),
         ReadText(store, "SELECT status_code FROM plan_occurrences;"));

    private static long Scalar(SqliteOperationalStore store, string sql)
    {
        using var connection = store.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt64(command.ExecuteScalar());
    }

    private static string ReadText(SqliteOperationalStore store, string sql)
    {
        using var connection = store.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToString(command.ExecuteScalar())!;
    }

    private static void Execute(SqliteOperationalStore store, string sql, params (string Name, object Value)[] parameters)
    {
        using var connection = store.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        command.ExecuteNonQuery();
    }

    private static void CommitActiveRun(
        PreparedDatabase database,
        string runId,
        string useId)
    {
        var scope = new SqliteAuthorizedCaptureScopeRepository(database.Store).GetById("scope-1");
        var plan = new SqlitePlanDefinitionRepository(database.Store).Get("plan-1");
        var occurrence = new SqlitePlanOccurrenceRepository(database.Store).Get("occurrence-1");
        var lease = new SqliteConsentLeaseRepository(database.Store).Get("lease-1");
        var result = new SqlitePhase3StartGateTransaction(database.Store).Commit(
            new Phase3StartGateRequest(
                plan.Id,
                occurrence.Id,
                lease.Id,
                scope.ScopeId,
                scope.ScopeDigest,
                runId,
                useId,
                plan.Version,
                occurrence.Version,
                lease.Version,
                scope.ReservedDuration,
                At(15)));
        Assert.Equal(Phase3StartGateCommitStatus.Committed, result.Status);
    }

    private static DateTimeOffset At(int seconds) =>
        new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero).AddSeconds(seconds);

    private static async Task WaitForAsync(Func<bool> condition)
    {
        for (var attempt = 0; attempt < 200; attempt++)
        {
            if (condition())
                return;
            await Task.Delay(10);
        }

        Assert.True(condition(), "Timed out waiting for the durable natural-wake terminal state.");
    }

    private static StandingSetupIntentStatus ReadIntentStatus(SqliteOperationalStore store, string intentId) =>
        StandingSetupIntentStatusCodes.Parse(
            ReadText(store, $"SELECT status_code FROM setup_intents WHERE intent_id = '{intentId}';"));

    private sealed class FixedIds : IStandingLeasePreparationIdProvider
    {
        public string CreatePlanId() => "plan-1";
        public string CreateOccurrenceId() => "occurrence-1";
        public string CreateLeaseId() => "lease-1";
        public string CreateScopeId() => "scope-1";
    }

    private sealed class AdditionalIds : IStandingLeasePreparationIdProvider
    {
        private readonly string _suffix;

        internal AdditionalIds(string suffix) => _suffix = suffix;

        public string CreatePlanId() => "plan-" + _suffix;
        public string CreateOccurrenceId() => "occurrence-" + _suffix;
        public string CreateLeaseId() => "lease-" + _suffix;
        public string CreateScopeId() => "scope-" + _suffix;
    }

    private sealed class TestActiveRunStopper : IStandingLeaseActiveRunStopper
    {
        private readonly Action _stop;

        internal TestActiveRunStopper(Action stop) => _stop = stop;

        public void StopAll(string reason) => _stop();
    }

    private static (StandingLeaseCaptureSpecification Specification,
        StandingLeaseCaptureExecutionTicket Ticket,
        AuthorizedFixedRegionScope Scope) CreateStandingTicket(PreparedDatabase database)
    {
        var plans = new SqlitePlanDefinitionRepository(database.Store);
        var occurrences = new SqlitePlanOccurrenceRepository(database.Store);
        var leases = new SqliteConsentLeaseRepository(database.Store);
        var scopes = new SqliteAuthorizedCaptureScopeRepository(database.Store);
        var scope = scopes.GetById("scope-1");
        var commit = new SqlitePhase3StartGateTransaction(database.Store).Commit(
            new Phase3StartGateRequest(
                scope.PlanId,
                scope.OccurrenceId,
                scope.LeaseId,
                scope.ScopeId,
                scope.ScopeDigest,
                "run-1",
                "use-1",
                plans.Get(scope.PlanId).Version,
                occurrences.Get(scope.OccurrenceId).Version,
                leases.Get(scope.LeaseId).Version,
                scope.ReservedDuration,
                At(12)));
        var proof = commit.FirstCommitProof;
        Assert.NotNull(proof);

        var loader = new SqliteStandingLeaseExecutionSnapshotLoader(database.Store);
        Assert.True(loader.TryAuthorizeAndConsumeStandingLeaseUseForTest(
            proof,
            CompleteEnvironment(scope, At(12)),
            out var authorization,
            out var authorizationFailure), authorizationFailure);
        Assert.NotNull(authorization);
        Assert.True(StandingLeaseCaptureSpecification.TryCreate(
            authorization!, out var specification, out var specificationFailure), specificationFailure);
        Assert.NotNull(specification);
        Assert.True(StandingLeaseCaptureExecutionTicket.TryCreate(
            authorization!, specification!, out var ticket, out var ticketFailure), ticketFailure);
        Assert.NotNull(ticket);
        Assert.True(ticket!.TryClaim(out var claimFailure), claimFailure);
        return (specification!, ticket, scope);
    }

    private static StandingLeaseExecutionEnvironment CompleteEnvironment(
        AuthorizedFixedRegionScope scope,
        DateTimeOffset nowUtc) =>
        new(
            nowUtc,
            scope.CurrentUserSid,
            scope.SessionBinding,
            true,
            new[]
            {
                new StandingLeaseDisplayMetadata(
                    "display-1",
                    scope.StableDisplayFingerprint,
                    DisplayIdentityResolutionStatus.Resolved,
                    scope.DisplayBounds,
                    scope.DpiX,
                    scope.DpiY,
                    scope.PhysicalWidth,
                    scope.PhysicalHeight,
                    scope.Orientation),
            },
            scope.TopologyDigest,
            new StandingLeaseOutputFileSystemSnapshot(
                StandingLeaseOutputPath.NormalizeDirectory(scope.OutputDirectory),
                Path.GetFullPath(scope.OutputFilePath),
                directoryExists: true,
                frozenFileExists: false,
                directoryWritable: true,
                freeSpaceAvailable: true,
                availableFreeBytes: RecordingPreflightChecker.RequiredFreeSpaceBytes(scope.ReservedDuration),
                requiredFreeBytes: RecordingPreflightChecker.RequiredFreeSpaceBytes(scope.ReservedDuration)));

    private static void AssertStandingStartBlocked(
        PreparedDatabase database,
        RecordingEngine engine)
    {
        var run = new SqliteRecordingRunRepository(database.Store).Get("run-1");
        var use = new SqliteLeaseUseRepository(database.Store).Get("use-1");
        var occurrence = new SqlitePlanOccurrenceRepository(database.Store).Get("occurrence-1");
        Assert.True(run.IsNonRetryable, "A committed run must not be retried after a safety decision.");
        Assert.NotEqual(RecordingRunStatus.Settled, run.Status);
        Assert.NotEqual(LeaseUseStatus.Settled, use.Status);
        Assert.Equal(PlanOccurrenceStatus.Blocked, occurrence.Status);
        Assert.NotEqual(RecState.completed, engine._recs["run-1"].State);
        var recording = engine._recs["run-1"];
        Assert.NotEqual(RecState.completed, recording.State);

        var execution = new StandingPlanSetupQueryService(database.Store)
            .GetExecution("intent-1", "S-1-5-21-test", CaptureAuthorizationSessionBinding.Current);
        Assert.NotNull(execution);
        Assert.Equal("run-1", execution!.RunId);
        Assert.Equal("blocked", execution.OccurrenceStatusCode);
    }

    private static void AssertStandingStartInterrupted(PreparedDatabase database, RecordingEngine engine)
    {
        var run = new SqliteRecordingRunRepository(database.Store).Get("run-1");
        var use = new SqliteLeaseUseRepository(database.Store).Get("use-1");
        var occurrence = new SqlitePlanOccurrenceRepository(database.Store).Get("occurrence-1");
        var recordingState = engine._recs["run-1"].State;
        Assert.True(run.IsNonRetryable);
        Assert.NotEqual(RecordingRunStatus.Settled, run.Status);
        var stateMessage =
            $"run={run.Status}/{run.TerminalReasonCode}; use={use.Status}; occurrence={occurrence.Status}/{occurrence.TerminalReasonCode}; rec={recordingState}";
        Assert.True(use.Status == LeaseUseStatus.StartedUnknown, stateMessage);
        Assert.True(occurrence.Status == PlanOccurrenceStatus.Blocked, stateMessage);
        Assert.NotEqual(RecState.completed, engine._recs["run-1"].State);
    }

    private static UnattendedModeStatus ReadSafetyMode(SqliteOperationalStore store) =>
        UnattendedModeStatusCodes.Parse(ReadText(store, "SELECT unattended_mode_code FROM unattended_safety_state WHERE state_id = 'global';"));

    private static bool ReadSafetyStopAll(SqliteOperationalStore store) =>
        Scalar(store, "SELECT stop_all_applied FROM unattended_safety_state WHERE state_id = 'global';") == 1;

    private sealed class BlockedStandingStart : IDisposable
    {
        private readonly ManualResetEventSlim? _releaseFinalGate;

        internal BlockedStandingStart(
            RecordingEngine engine,
            StandingLeaseSafetyControlService safety,
            LinearizationCaptureBackend backend,
            TaskCompletionSource<object?>? environmentEntered,
            ManualResetEventSlim? releaseFinalGate,
            Task<StandingLeaseCaptureExecutionResult> startTask)
        {
            Engine = engine;
            Safety = safety;
            Backend = backend;
            EnvironmentEntered = environmentEntered ??
                new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
            _releaseFinalGate = releaseFinalGate;
            StartTask = startTask;
        }

        internal RecordingEngine Engine { get; }
        internal StandingLeaseSafetyControlService Safety { get; }
        internal LinearizationCaptureBackend Backend { get; }
        internal TaskCompletionSource<object?> EnvironmentEntered { get; }
        internal Task<StandingLeaseCaptureExecutionResult> StartTask { get; }

        internal void ReleaseFinalGate() => _releaseFinalGate?.Set();

        internal void ReleaseBackendStart() => Backend.ReleaseStart();

        public void Dispose()
        {
            _releaseFinalGate?.Set();
            Backend.ReleaseStart();
            try { if (!StartTask.IsCompleted) StartTask.Wait(TimeSpan.FromSeconds(2)); } catch { }
            Engine.Dispose();
            _releaseFinalGate?.Dispose();
            Backend.DisposeGate();
        }
    }

    private sealed class LinearizationCaptureBackend : ICaptureBackend, IFirstFrameObservableCaptureBackend
    {
        private readonly ManualResetEventSlim _releaseStart = new(false);
        private Action<int, OutputMeta>? _naturalExit;
        private int _startCalls;
        private int _stopCalls;
        private int _disposeCalls;

        internal TaskCompletionSource<object?> StartEntered { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public event Action<FirstFrameObservation>? FirstFrameObserved;

        internal Action? BeforeStart { get; set; }
        internal bool WaitInsideStart { get; set; }
        internal int StartCalls => Volatile.Read(ref _startCalls);
        internal int StopCalls => Volatile.Read(ref _stopCalls);
        internal int DisposeCalls => Volatile.Read(ref _disposeCalls);

        public void Start(CaptureConfig cfg, CaptureAuthorizationProof authorizationProof)
        {
            Interlocked.Increment(ref _startCalls);
            BeforeStart?.Invoke();
            if (WaitInsideStart)
                _releaseStart.Wait();
            FirstFrameObserved?.Invoke(new FirstFrameObservation
            {
                FrameNumber = 1,
                TotalSizeBytes = 1,
                OutTimeUs = 1
            });
        }

        public OutputMeta Stop()
        {
            Interlocked.Increment(ref _stopCalls);
            return new OutputMeta();
        }

        public void OnNaturalExit(Action<int, OutputMeta> callback) => _naturalExit = callback;

        public void Dispose() => Interlocked.Increment(ref _disposeCalls);

        internal void ReleaseStart() => _releaseStart.Set();

        internal void DisposeGate() => _releaseStart.Dispose();
    }

    private sealed class ProductionChainCaptureBackend : ICaptureBackend, IFirstFrameObservableCaptureBackend
    {
        private Action<int, OutputMeta>? _naturalExit;
        private int _startCalls;
        private int _stopCalls;
        private int _disposeCalls;
        private readonly TaskCompletionSource<object?> _releaseStart =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal TaskCompletionSource<object?> Started { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource<object?> StartEntered { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal bool WaitInsideStart { get; set; }
        internal CaptureConfig? Configuration { get; private set; }

        public event Action<FirstFrameObservation>? FirstFrameObserved;

        internal int StartCalls => Volatile.Read(ref _startCalls);
        internal int StopCalls => Volatile.Read(ref _stopCalls);
        internal int DisposeCalls => Volatile.Read(ref _disposeCalls);

        public void Start(CaptureConfig cfg, CaptureAuthorizationProof authorizationProof)
        {
            Interlocked.Increment(ref _startCalls);
            Configuration = cfg;
            Started.TrySetResult(null);
            StartEntered.TrySetResult(null);
            if (WaitInsideStart)
                _releaseStart.Task.GetAwaiter().GetResult();
            FirstFrameObserved?.Invoke(new FirstFrameObservation
            {
                FrameNumber = 1,
                TotalSizeBytes = 4096,
                OutTimeUs = 1,
            });
        }

        public OutputMeta Stop()
        {
            Interlocked.Increment(ref _stopCalls);
            return new OutputMeta
            {
                OutputFileExists = true,
                SizeBytes = 4096,
                DurationSeconds = 30,
            };
        }

        public void OnNaturalExit(Action<int, OutputMeta> callback) => _naturalExit = callback;

        public void Dispose() => Interlocked.Increment(ref _disposeCalls);

        internal void RaiseNaturalExit(int exitCode, OutputMeta meta) => _naturalExit?.Invoke(exitCode, meta);
        internal void ReleaseStart() => _releaseStart.TrySetResult(null);
    }

    private sealed class ProductionRace : IDisposable
    {
        private readonly TaskCompletionSource<object?> _releaseFinalGate;
        private readonly StandingLeaseOneShotExecutionCoordinator _coordinator;
        private readonly RecordingEngine _engine;

        internal ProductionRace(
            bool safetyWins,
            StandingLeaseSafetyControlService safety,
            ProductionChainCaptureBackend backend,
            StandingLeaseNaturalWakeScheduler scheduler,
            StandingLeaseOneShotExecutionCoordinator coordinator,
            RecordingEngine engine,
            TaskCompletionSource<object?> finalGateEntered,
            TaskCompletionSource<object?> startCompleted,
            TaskCompletionSource<object?> releaseFinalGate)
        {
            SafetyWins = safetyWins;
            Safety = safety;
            Backend = backend;
            Scheduler = scheduler;
            _coordinator = coordinator;
            _engine = engine;
            FinalGateEntered = finalGateEntered;
            StartCompleted = startCompleted;
            _releaseFinalGate = releaseFinalGate;
        }

        internal bool SafetyWins { get; }
        internal StandingLeaseSafetyControlService Safety { get; }
        internal ProductionChainCaptureBackend Backend { get; }
        internal StandingLeaseNaturalWakeScheduler Scheduler { get; }
        internal TaskCompletionSource<object?> FinalGateEntered { get; }
        internal TaskCompletionSource<object?> StartCompleted { get; }

        internal void ReleaseFinalGate() => _releaseFinalGate.TrySetResult(null);

        internal void ReleaseBackendStart() => Backend.ReleaseStart();

        public void Dispose()
        {
            ReleaseFinalGate();
            ReleaseBackendStart();
            Scheduler.Dispose();
            _engine.Dispose();
        }
    }

    private static ProductionRace CreateProductionRace(PreparedDatabase database, bool safetyWins)
    {
        var currentUserSid = System.Security.Principal.WindowsIdentity.GetCurrent().User?.Value;
        Assert.False(string.IsNullOrWhiteSpace(currentUserSid));
        var scope = new SqliteAuthorizedCaptureScopeRepository(database.Store).GetById("scope-1");
        var interlock = new StandingLeaseStartSafetyInterlock();
        var backend = new ProductionChainCaptureBackend { WaitInsideStart = !safetyWins };
        var finalGateEntered = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFinalGate = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var startCompleted = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
        StandingLeaseSafetyControlService? safety = null;

        var engine = new RecordingEngine(
            new TestAuditLogger(),
            tracer: null,
            bundleGenerator: null,
            microphoneProvider: null,
            microphoneStatusProvider: null,
            displayTopologyProvider: new ProductionChainDisplayTopologyProvider(scope),
            systemAudioEndpointProvider: null,
            standingStartSafetyInterlock: interlock,
            standingStartSafetyValidator: (ticket, nowUtc) => safety is null
                ? "standing_safety_service_unavailable"
                : safety.ValidateStandingStart(ticket, nowUtc));
        engine.DisableDeadlineWatchdogForTests = true;
        engine.UtcNowForTests = () => At(15).UtcDateTime;
        engine.StandingExecutionEnvironmentProviderForTests = currentScope => CompleteEnvironment(currentScope, At(15));
        engine.BackendSelectionFactoryForTests = _ => new CaptureBackendSelection(
            backend,
            StandingLeaseCaptureSpecification.BackendCode,
            new CaptureBackendSelectionEvidence(
                StandingLeaseCaptureSpecification.BackendCode,
                StandingLeaseCaptureSpecification.BackendCode,
                "standing_production_race_test",
                "test",
                null,
                false));
        engine.SetTray(new StandingEngineTestTray());
        if (safetyWins)
        {
            engine.BeforeStandingBackendFinalGateForTests = _ =>
            {
                finalGateEntered.TrySetResult(null);
                releaseFinalGate.Task.GetAwaiter().GetResult();
            };
        }

        safety = new StandingLeaseSafetyControlService(
            database.Store,
            () => At(15),
            activeRunStopper: new RecordingEngineStandingLeaseActiveRunStopper(engine),
            startSafetyInterlock: interlock);

        var coordinator = new StandingLeaseOneShotExecutionCoordinator(
            database.Store,
            environmentProviderForTest: new FixedStandingExecutionEnvironmentProvider(
                currentScope => CompleteEnvironment(currentScope, At(15))),
            backendFactoryForTest: null,
            delayForTest: null,
            utcNowForTest: () => At(15),
            executionStarterForProduction: (ticket, cancellationToken) =>
            {
                var result = engine.StartStandingCapture(
                    ticket,
                    new StandingEngineTestTray(),
                    candidate => new StandingLeaseOneShotExecutionSession(
                        database.Store,
                        ticket.Specification,
                        candidate,
                        () => At(15),
                        attachBackendCallbacks: false),
                    cancellationToken);
                startCompleted.TrySetResult(null);
                return Task.FromResult(result);
            });
        var dispatcher = new StandingLeasePreparedIntentNaturalWakeDispatcher(
            database.Store,
            utcNowForTest: () => At(15),
            coordinatorForTest: coordinator.ExecuteAsync,
            auditForTest: (_, _) => { });
        var scheduler = new StandingLeaseNaturalWakeScheduler(
            database.Store,
            dispatcher,
            () => currentUserSid,
            () => CaptureAuthorizationSessionBinding.Current,
            utcNow: () => At(15),
            waitForTest: (_, cancellationToken) => Task.CompletedTask,
            pollInterval: TimeSpan.FromSeconds(1),
            audit: (_, _) => { });

        return new ProductionRace(
            safetyWins,
            safety,
            backend,
            scheduler,
            coordinator,
            engine,
            finalGateEntered,
            startCompleted,
            releaseFinalGate);
    }

    private sealed class FixedStandingExecutionEnvironmentProvider : IStandingLeaseExecutionEnvironmentProvider
    {
        private readonly Func<AuthorizedFixedRegionScope, StandingLeaseExecutionEnvironment> _capture;

        internal FixedStandingExecutionEnvironmentProvider(
            Func<AuthorizedFixedRegionScope, StandingLeaseExecutionEnvironment> capture) =>
            _capture = capture;

        public StandingLeaseExecutionEnvironment Capture(AuthorizedFixedRegionScope scope) => _capture(scope);
    }

    private sealed class ProductionChainDisplayTopologyProvider : IDisplayTopologyProvider
    {
        private readonly AuthorizedFixedRegionScope _scope;

        internal ProductionChainDisplayTopologyProvider(AuthorizedFixedRegionScope scope) => _scope = scope;

        public IReadOnlyList<DisplayTopologySnapshot> GetCurrentDisplays() => new[]
        {
            new DisplayTopologySnapshot(
                "display-1",
                _scope.StableDisplayFingerprint,
                DisplayIdentityResolutionStatus.Resolved,
                new CapturePlanBounds(
                    _scope.DisplayBounds.X,
                    _scope.DisplayBounds.Y,
                    _scope.DisplayBounds.Width,
                    _scope.DisplayBounds.Height)),
        };
    }

    private sealed class NoOpStandingPlanSetupUi : IStandingPlanSetupUi
    {
        public bool IsInteractiveDesktopAvailable => true;

        public Task<StandingPlanSetupSelection> SelectFreshRegionAsync(CancellationToken cancellationToken) =>
            Task.FromResult(new StandingPlanSetupSelection(
                "selected", 10, 20, 640, 480, "display-a", "virtual_screen"));

        public Task<bool> ShowApprovalAsync(StandingLeaseApprovalDetails details, CancellationToken cancellationToken) =>
            Task.FromResult(false);
    }

    private sealed class StandingEngineTestTray : ITrayContext
    {
        public string HostMode => "headless";
        public bool SupportsRegionSelectionUi => false;
        public void RequestConfirmation(RecordingConfirmationPresentation presentation, Action<ConfirmationDecision> callback) { }
        public void RequestRegionSelection(int timeoutSeconds, Action<string, int, int, int, int, string, string> callback) { }
        public void SetRecording(RecordingUiPresentation presentation) { }
        public void SetIdle(RecordingUiPresentation presentation) { }
        public void SetAllIdle() { }
        public void ShowError(string text) { }
    }

    private sealed class TestAuditLogger : AuditLogger
    {
        public override void Log(string evt, object payload) { }
    }

    private static BlockedStandingStart BeginBlockedStandingStart(PreparedDatabase database)
    {
        var (specification, ticket, scope) = CreateStandingTicket(database);
        var backend = new LinearizationCaptureBackend();
        var entered = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new ManualResetEventSlim(false);
        var interlock = new StandingLeaseStartSafetyInterlock();
        var composition = CreateStandingEngine(database, backend, interlock, ticket, specification, scope,
            currentEnvironment: currentScope => CompleteEnvironment(currentScope, At(20)),
            safetyFactory: createdEngine => new StandingLeaseSafetyControlService(
                database.Store,
                () => At(20),
                activeRunStopper: new RecordingEngineStandingLeaseActiveRunStopper(createdEngine),
                startSafetyInterlock: interlock));
        composition.Engine.BeforeStandingBackendFinalGateForTests = _ =>
        {
            entered.TrySetResult(null);
            release.Wait();
        };

        var startTask = Task.Run(() => composition.Engine.StartStandingCapture(
            ticket,
            new StandingEngineTestTray(),
            candidate => new StandingLeaseOneShotExecutionSession(
                database.Store,
                specification,
                candidate,
                () => At(20),
                attachBackendCallbacks: true)));

        return new BlockedStandingStart(
            composition.Engine,
            composition.Safety,
            backend,
            entered,
            release,
            startTask);
    }

    private static BlockedStandingStart BeginStartHeldAtBackendBoundary(PreparedDatabase database)
    {
        var (specification, ticket, scope) = CreateStandingTicket(database);
        var backend = new LinearizationCaptureBackend();
        var interlock = new StandingLeaseStartSafetyInterlock();
        var composition = CreateStandingEngine(database, backend, interlock, ticket, specification, scope,
            currentEnvironment: currentScope => CompleteEnvironment(currentScope, At(20)),
            safetyFactory: createdEngine => new StandingLeaseSafetyControlService(
                database.Store,
                () => At(20),
                activeRunStopper: new RecordingEngineStandingLeaseActiveRunStopper(createdEngine),
                startSafetyInterlock: interlock),
            validateStandingSafety: false);
        backend.BeforeStart = () => backend.StartEntered.TrySetResult(null);
        backend.WaitInsideStart = true;

        var startTask = Task.Run(() => composition.Engine.StartStandingCapture(
            ticket,
            new StandingEngineTestTray(),
            candidate => new StandingLeaseOneShotExecutionSession(
                database.Store,
                specification,
                candidate,
                () => At(20),
                attachBackendCallbacks: true)));

        return new BlockedStandingStart(
            composition.Engine,
            composition.Safety,
            backend,
            environmentEntered: null,
            releaseFinalGate: null,
            startTask);
    }

    private static (RecordingEngine Engine, StandingLeaseSafetyControlService Safety) CreateStandingEngine(
        PreparedDatabase database,
        LinearizationCaptureBackend backend,
        StandingLeaseStartSafetyInterlock interlock,
        StandingLeaseCaptureExecutionTicket ticket,
        StandingLeaseCaptureSpecification specification,
        AuthorizedFixedRegionScope scope,
        Func<AuthorizedFixedRegionScope, StandingLeaseExecutionEnvironment> currentEnvironment,
        Func<RecordingEngine, StandingLeaseSafetyControlService> safetyFactory,
        bool validateStandingSafety = true)
    {
        StandingLeaseSafetyControlService? safety = null;
        var engine = new RecordingEngine(
            new TestAuditLogger(),
            tracer: null,
            bundleGenerator: null,
            microphoneProvider: null,
            microphoneStatusProvider: null,
            displayTopologyProvider: new ProductionChainDisplayTopologyProvider(scope),
            systemAudioEndpointProvider: null,
            standingStartSafetyInterlock: interlock,
            standingStartSafetyValidator: validateStandingSafety
                ? (candidate, nowUtc) => safety is null
                    ? "standing_safety_service_unavailable"
                    : safety.ValidateStandingStart(candidate, nowUtc)
                : (_, _) => null);
        engine.DisableDeadlineWatchdogForTests = true;
        engine.UtcNowForTests = () => At(20).UtcDateTime;
        engine.StandingExecutionEnvironmentProviderForTests = currentEnvironment;
        engine.BackendSelectionFactoryForTests = _ => new CaptureBackendSelection(
            backend,
            StandingLeaseCaptureSpecification.BackendCode,
            new CaptureBackendSelectionEvidence(
                StandingLeaseCaptureSpecification.BackendCode,
                StandingLeaseCaptureSpecification.BackendCode,
                "standing_test",
                "test",
                null,
                false));
        engine.SetTray(new StandingEngineTestTray());

        // The factory is deliberately invoked after Engine creation so the
        // validator closure and active-run stopper share the same Engine and
        // interlock, exactly as in Program.cs.
        safety = safetyFactory(engine);
        return (engine, safety);
    }

    private sealed class PreparedDatabase : IDisposable
    {
        private readonly string _directory = Path.Combine(Path.GetTempPath(), "AgentRecorderPreparedNaturalWake_" + Guid.NewGuid().ToString("N"));

        internal PreparedDatabase()
        {
            Directory.CreateDirectory(_directory);
            Store = new SqliteOperationalStore(Path.Combine(_directory, "state", "agent-recorder.db"));
            Store.Initialize();
            Execute(Store, "UPDATE unattended_safety_state SET unattended_mode_code = 'enabled' WHERE state_id = 'global';");
        }

        internal SqliteOperationalStore Store { get; }
        internal string RootPath => _directory;

        public void Dispose()
        {
            for (var attempt = 0; attempt < 20; attempt++)
            {
                try
                {
                    Directory.Delete(_directory, recursive: true);
                    return;
                }
                catch (DirectoryNotFoundException)
                {
                    return;
                }
                catch (IOException) when (attempt < 19)
                {
                    GC.Collect();
                    GC.WaitForPendingFinalizers();
                    Thread.Sleep(25);
                }
            }
        }
    }
}
