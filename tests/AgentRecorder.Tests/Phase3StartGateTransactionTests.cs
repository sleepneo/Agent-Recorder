using AgentRecorder.Core.Automation;
using AgentRecorder.Core;
using AgentRecorder.Capture;
using AgentRecorder.Infrastructure;
using AgentRecorder.Persistence;
using AgentRecorder.Logging;
using AgentRecorder.Windows;
using Microsoft.Data.Sqlite;
using System.Text.Json;
using System.Threading;
using Xunit;

namespace AgentRecorder.Tests;

[Collection("NonParallel-AgentRecorderDataDir")]
public sealed class Phase3StartGateTransactionTests
{
    [Fact]
    public void HappyPathCommitsAllFourPiecesOfEvidenceAtomically()
    {
        using var database = new TemporaryDatabase();
        var request = database.CreateRequest();

        var result = database.Gate.Commit(request);

        Assert.Equal("committed", result.Code);
        Assert.Equal(Phase3StartGateCommitStatus.Committed, result.Status);
        Assert.Equal(request.RunId, result.RunId);
        Assert.Equal(request.LeaseUseId, result.LeaseUseId);

        var plan = database.Plans.Get(request.PlanId);
        var occurrence = database.Occurrences.Get(request.OccurrenceId);
        var run = database.Runs.Get(request.RunId);
        var lease = database.Leases.Get(request.LeaseId);
        var use = database.Uses.Get(request.LeaseUseId);

        Assert.Equal(PlanDefinitionStatus.Enabled, plan.Status);
        Assert.Equal(0L, plan.Version);
        Assert.Equal(PlanOccurrenceStatus.RunCreated, occurrence.Status);
        Assert.Equal(request.RunId, occurrence.RunId);
        Assert.Equal(1L, occurrence.Version);
        Assert.Equal(request.CommitAtUtc, occurrence.UpdatedAtUtc);
        Assert.Equal(RecordingRunStatus.StartCommitted, run.Status);
        Assert.True(run.HasCrossedStartCommit);
        Assert.Equal(2L, run.Version);
        Assert.Equal(request.CommitAtUtc, run.CreatedAtUtc);
        Assert.Equal(ConsentLeaseStatus.Exhausted, lease.Status);
        Assert.Equal(1L, lease.Version);
        Assert.Equal(request.CommitAtUtc, lease.UpdatedAtUtc);
        Assert.Equal(LeaseUseStatus.StartCommitted, use.Status);
        Assert.Equal(1, use.ReservedUseCount);
        Assert.Equal(request.ReservedDuration, use.ReservedDuration);
        Assert.Null(use.ActualSettledDuration);
        Assert.Equal(2L, use.Version);
    }

    [Fact]
    public void IdenticalRetryReturnsAlreadyCommittedWithoutWritingAgain()
    {
        using var database = new TemporaryDatabase();
        var request = database.CreateRequest();
        database.Gate.Commit(request);
        var firstState = ReadEvidenceState(database, request);

        var result = database.Gate.Commit(request);
        var secondState = ReadEvidenceState(database, request);

        Assert.Equal("already_committed", result.Code);
        Assert.Equal(Phase3StartGateCommitStatus.AlreadyCommitted, result.Status);
        Assert.Equal(firstState, secondState);
    }

    [Fact]
    public void RetryAfterLaterRunProgressAndLeaseRevocationStillReturnsAlreadyCommitted()
    {
        using var database = new TemporaryDatabase();
        var request = database.CreateRequest();
        database.Gate.Commit(request);

        var run = database.Runs.Get(request.RunId);
        Assert.True(run.TryTransition(RecordingRunStatus.Recording, At(2)).Succeeded);
        database.Runs.Update(run, 2);
        var use = database.Uses.Get(request.LeaseUseId);
        Assert.True(use.TryTransition(LeaseUseStatus.Consumed, At(2)).Succeeded);
        database.Uses.Update(use, 2);

        RawExecute(database.Store.DatabasePath, "UPDATE consent_leases SET status_code = 'revoked', version = 2 WHERE id = $id;", ("$id", request.LeaseId));

        var result = database.Gate.Commit(request);

        Assert.Equal("already_committed", result.Code);
        Assert.Equal(RecordingRunStatus.Recording, database.Runs.Get(request.RunId).Status);
        Assert.Equal(LeaseUseStatus.Consumed, database.Uses.Get(request.LeaseUseId).Status);
        Assert.Equal(ConsentLeaseStatus.Revoked, database.Leases.Get(request.LeaseId).Status);
    }

    [Fact]
    public void RetryAfterLeaseValidityIsShortenedBelowReservedEndFailsClosed()
    {
        using var database = new TemporaryDatabase();
        var request = database.CreateRequest();
        database.Gate.Commit(request);
        RawExecute(database.Store.DatabasePath, "UPDATE consent_leases SET valid_until_utc = $valid_until WHERE id = $id;",
            ("$valid_until", At(5).Ticks), ("$id", request.LeaseId));

        var exception = Assert.Throws<Phase3PersistenceException>(() => database.Gate.Commit(request));

        Assert.Equal("persisted_snapshot_invalid", exception.Code);
    }

    [Fact]
    public void RetryAfterLeaseMaxDurationIsShortenedBelowReservationFailsClosed()
    {
        using var database = new TemporaryDatabase();
        var request = database.CreateRequest();
        database.Gate.Commit(request);
        RawExecute(database.Store.DatabasePath, "UPDATE consent_leases SET max_duration_ms = $max_duration WHERE id = $id;",
            ("$max_duration", 4000L), ("$id", request.LeaseId));

        var exception = Assert.Throws<Phase3PersistenceException>(() => database.Gate.Commit(request));

        Assert.Equal("persisted_snapshot_invalid", exception.Code);
    }

    [Fact]
    public void RetryAfterOccurrenceWindowIsShortenedBelowReservedEndFailsClosed()
    {
        using var database = new TemporaryDatabase();
        var request = database.CreateRequest();
        database.Gate.Commit(request);
        RawExecute(database.Store.DatabasePath, "UPDATE plan_occurrences SET window_end_utc = $window_end WHERE id = $id;",
            ("$window_end", At(5).Ticks), ("$id", request.OccurrenceId));

        var exception = Assert.Throws<Phase3PersistenceException>(() => database.Gate.Commit(request));

        Assert.Equal("persisted_snapshot_invalid", exception.Code);
    }

    [Fact]
    public void RetryAfterCrossAggregateOccurrencePlanScopeCorruptionFailsClosed()
    {
        using var database = new TemporaryDatabase();
        var request = database.CreateRequest();
        database.Gate.Commit(request);
        RawExecute(database.Store.DatabasePath, "PRAGMA foreign_keys = OFF; UPDATE plan_occurrences SET plan_id = $plan_id WHERE id = $id;",
            ("$plan_id", "other-plan"), ("$id", request.OccurrenceId));

        var exception = Assert.Throws<Phase3PersistenceException>(() => database.Gate.Commit(request));

        Assert.Equal("persisted_snapshot_invalid", exception.Code);
    }

    [Fact]
    public void RetryAfterRunOrUseCommitTimesDisagreeFailsClosed()
    {
        using (var runDatabase = new TemporaryDatabase())
        {
            var request = runDatabase.CreateRequest();
            runDatabase.Gate.Commit(request);
            RawExecute(runDatabase.Store.DatabasePath,
                "UPDATE recording_runs SET created_at_utc = $time, updated_at_utc = $time WHERE id = $id;",
                ("$time", At(2).Ticks), ("$id", request.RunId));

            var exception = Assert.Throws<Phase3PersistenceException>(() => runDatabase.Gate.Commit(request));

            Assert.Equal("persisted_snapshot_invalid", exception.Code);
        }

        using (var useDatabase = new TemporaryDatabase())
        {
            var request = useDatabase.CreateRequest();
            useDatabase.Gate.Commit(request);
            RawExecute(useDatabase.Store.DatabasePath,
                "UPDATE lease_uses SET created_at_utc = $time, updated_at_utc = $time WHERE id = $id;",
                ("$time", At(2).Ticks), ("$id", request.LeaseUseId));

            var exception = Assert.Throws<Phase3PersistenceException>(() => useDatabase.Gate.Commit(request));

            Assert.Equal("persisted_snapshot_invalid", exception.Code);
        }
    }

    [Fact]
    public void RetryAfterCompleteEvidenceLeaseIsReturnedToActiveFailsClosed()
    {
        using var database = new TemporaryDatabase();
        var request = database.CreateRequest();
        database.Gate.Commit(request);
        RawExecute(database.Store.DatabasePath, "UPDATE consent_leases SET status_code = 'active' WHERE id = $id;", ("$id", request.LeaseId));

        var exception = Assert.Throws<Phase3PersistenceException>(() => database.Gate.Commit(request));

        Assert.Equal("persisted_snapshot_invalid", exception.Code);
    }

    [Fact]
    public void IdempotentEvidenceAcceptsExactEndBoundaryButRejectsOneTickBeyondIt()
    {
        using (var exact = new TemporaryDatabase(occurrenceEndSeconds: 6, leaseEndSeconds: 6))
        {
            var request = exact.CreateRequest();
            exact.Gate.Commit(request);

            var result = exact.Gate.Commit(request);

            Assert.Equal("already_committed", result.Code);
        }

        using (var beyond = new TemporaryDatabase(occurrenceEndSeconds: 6, leaseEndSeconds: 6))
        {
            var request = beyond.CreateRequest();
            beyond.Gate.Commit(request);
            RawExecute(beyond.Store.DatabasePath,
                "UPDATE plan_occurrences SET window_end_utc = $window_end WHERE id = $id;",
                ("$window_end", At(6).AddTicks(-1).Ticks), ("$id", request.OccurrenceId));

            var exception = Assert.Throws<Phase3PersistenceException>(() => beyond.Gate.Commit(request));

            Assert.Equal("persisted_snapshot_invalid", exception.Code);
        }
    }

    [Fact]
    public async Task SameRequestOnTwoIndependentCoordinatorsHasOneCommitAndOneIdempotentResult()
    {
        using var database = new TemporaryDatabase();
        var request = database.CreateRequest();
        var first = new SqlitePhase3StartGateTransaction(database.Store);
        var second = new SqlitePhase3StartGateTransaction(database.Store);

        var results = await Task.WhenAll(
            Task.Run(() => CaptureGateCode(() => first.Commit(request))),
            Task.Run(() => CaptureGateCode(() => second.Commit(request))));

        Assert.Equal(1, results.Count(code => code == "committed"));
        Assert.Equal(1, results.Count(code => code == "already_committed"));
        Assert.Equal(1L, RawScalar(database.Store.DatabasePath, "SELECT COUNT(*) FROM recording_runs;"));
        Assert.Equal(1L, RawScalar(database.Store.DatabasePath, "SELECT COUNT(*) FROM lease_uses;"));
    }

    [Fact]
    public async Task DifferentRunAndUseIdsCompetingForOneOccurrenceHaveOneWinner()
    {
        using var database = new TemporaryDatabase();
        var firstRequest = database.CreateRequest("run-1", "use-1");
        var secondRequest = database.CreateRequest("run-2", "use-2");
        var first = new SqlitePhase3StartGateTransaction(database.Store);
        var second = new SqlitePhase3StartGateTransaction(database.Store);

        var results = await Task.WhenAll(
            Task.Run(() => CaptureGateCode(() => first.Commit(firstRequest))),
            Task.Run(() => CaptureGateCode(() => second.Commit(secondRequest))));

        Assert.Equal(1, results.Count(code => code == "committed"));
        Assert.Equal(1, results.Count(code => code == "occurrence_already_claimed"));
        Assert.Equal(1L, RawScalar(database.Store.DatabasePath, "SELECT COUNT(*) FROM recording_runs;"));
        Assert.Equal(1L, RawScalar(database.Store.DatabasePath, "SELECT COUNT(*) FROM lease_uses;"));
    }

    [Fact]
    public async Task StartGateAndIndependentLeaseRevokeHaveOnlySafeLinearizations()
    {
        using var database = new TemporaryDatabase();
        var request = database.CreateRequest();
        var lease = database.Leases.Get(request.LeaseId);
        Assert.True(lease.TryTransition(ConsentLeaseStatus.Revoked, At(1)).Succeeded);
        var gate = new SqlitePhase3StartGateTransaction(database.Store);
        var revoker = new SqliteConsentLeaseRepository(database.Store);

        var results = await Task.WhenAll(
            Task.Run(() => CaptureGateCode(() => gate.Commit(request))),
            Task.Run(() => CaptureActionCode(() => revoker.Update(lease, 0))));

        Assert.True(
            (results.Contains("committed") && results.Contains("concurrency_conflict")) ||
            (results.Contains("concurrency_conflict") && results.Contains("success")) ||
            (results.Contains("lease_not_active") && results.Contains("success")),
            string.Join(",", results));
        if (results.Contains("committed"))
        {
            Assert.Equal(ConsentLeaseStatus.Exhausted, database.Leases.Get(request.LeaseId).Status);
            Assert.Equal(1L, RawScalar(database.Store.DatabasePath, "SELECT COUNT(*) FROM recording_runs;"));
        }
        else
        {
            Assert.Equal(ConsentLeaseStatus.Revoked, database.Leases.Get(request.LeaseId).Status);
            Assert.Equal(0L, RawScalar(database.Store.DatabasePath, "SELECT COUNT(*) FROM recording_runs;"));
        }
    }

    [Fact]
    public void NonEnabledPlanIsRejectedWithoutPartialEvidence()
    {
        using var database = new TemporaryDatabase(planStatus: PlanDefinitionStatus.Draft);

        var exception = Assert.Throws<Phase3PersistenceException>(() => database.Gate.Commit(database.CreateRequest()));

        Assert.Equal("plan_not_enabled", exception.Code);
        AssertNoStartGateRows(database);
    }

    [Fact]
    public void NonAuthorizedOccurrenceAndNonActiveLeaseAreRejectedWithoutPartialEvidence()
    {
        using (var occurrenceDatabase = new TemporaryDatabase(occurrenceStatus: PlanOccurrenceStatus.PendingConfirmation))
        {
            var exception = Assert.Throws<Phase3PersistenceException>(() => occurrenceDatabase.Gate.Commit(occurrenceDatabase.CreateRequest()));
            Assert.Equal("occurrence_not_authorized", exception.Code);
            AssertNoStartGateRows(occurrenceDatabase);
        }

        using (var leaseDatabase = new TemporaryDatabase(leaseStatus: ConsentLeaseStatus.Revoked))
        {
            var exception = Assert.Throws<Phase3PersistenceException>(() => leaseDatabase.Gate.Commit(leaseDatabase.CreateRequest()));
            Assert.Equal("lease_not_active", exception.Code);
            AssertNoStartGateRows(leaseDatabase, expectActiveLease: false);
        }
    }

    [Fact]
    public void StaleVersionsAreRejectedBeforeAnyStartGateWrite()
    {
        foreach (var expectedVersions in new[] { (1L, 0L, 0L), (0L, 1L, 0L), (0L, 0L, 1L) })
        {
            using var database = new TemporaryDatabase();
            var request = database.CreateRequest() with
            {
                ExpectedPlanVersion = expectedVersions.Item1,
                ExpectedOccurrenceVersion = expectedVersions.Item2,
                ExpectedLeaseVersion = expectedVersions.Item3,
            };

            var exception = Assert.Throws<Phase3PersistenceException>(() => database.Gate.Commit(request));

            Assert.Equal("concurrency_conflict", exception.Code);
            AssertNoStartGateRows(database);
        }
    }

    [Fact]
    public void CommitWindowAndLeaseBoundariesAreExact()
    {
        using (var atStart = new TemporaryDatabase(scopeDuration: TimeSpan.FromSeconds(1)))
        {
            var result = atStart.Gate.Commit(atStart.CreateRequest(commitAt: At(0), duration: TimeSpan.FromSeconds(1)));
            Assert.Equal("committed", result.Code);
        }

        using (var atOccurrenceEnd = new TemporaryDatabase())
        {
            var exception = Assert.Throws<Phase3PersistenceException>(() => atOccurrenceEnd.Gate.Commit(atOccurrenceEnd.CreateRequest(commitAt: At(60))));
            Assert.Equal("occurrence_not_in_window", exception.Code);
        }

        using (var atLeaseEnd = new TemporaryDatabase(occurrenceEndSeconds: 60, leaseEndSeconds: 120))
        {
            var exception = Assert.Throws<Phase3PersistenceException>(() => atLeaseEnd.Gate.Commit(atLeaseEnd.CreateRequest(commitAt: At(120))));
            Assert.Equal("lease_not_valid_at_commit", exception.Code);
        }
    }

    [Fact]
    public void DurationUpperBoundIsAcceptedButOverflowIsRejectedWithoutShortening()
    {
        using (var equal = new TemporaryDatabase(scopeDuration: TimeSpan.FromSeconds(30)))
        {
            var result = equal.Gate.Commit(equal.CreateRequest(commitAt: At(0), duration: TimeSpan.FromSeconds(30)));
            Assert.Equal("committed", result.Code);
            Assert.Equal(TimeSpan.FromSeconds(30), equal.Uses.Get("use-1").ReservedDuration);
        }

        using (var overLease = new TemporaryDatabase(scopeDuration: TimeSpan.FromSeconds(30)))
        {
            var exception = Assert.Throws<Phase3PersistenceException>(() => overLease.Gate.Commit(overLease.CreateRequest(duration: TimeSpan.FromSeconds(30).Add(TimeSpan.FromMilliseconds(1)))));
            Assert.Equal("duration_out_of_scope", exception.Code);
            AssertNoStartGateRows(overLease);
        }

        using (var overWindow = new TemporaryDatabase(scopeDuration: TimeSpan.FromSeconds(11)))
        {
            var exception = Assert.Throws<Phase3PersistenceException>(() => overWindow.Gate.Commit(overWindow.CreateRequest(commitAt: At(50), duration: TimeSpan.FromSeconds(11))));
            Assert.Equal("duration_out_of_scope", exception.Code);
            AssertNoStartGateRows(overWindow);
        }
    }

    [Fact]
    public void InvalidDurationTimeAndIdsAreRejectedBeforeOpeningBusinessConnection()
    {
        foreach (var requestFactory in new Func<TemporaryDatabase, Phase3StartGateRequest>[]
        {
            database => database.CreateRequest(duration: TimeSpan.Zero),
            database => database.CreateRequest(duration: TimeSpan.FromTicks(-1)),
            database => database.CreateRequest(duration: TimeSpan.FromTicks(1)),
            database => database.CreateRequest(commitAt: new DateTimeOffset(2026, 1, 1, 0, 0, 1, TimeSpan.FromHours(1))),
            database => database.CreateRequest() with { PlanId = "  " },
            database => database.CreateRequest() with { ExpectedPlanVersion = -1 },
        })
        {
            using var database = new TemporaryDatabase();
            var exception = Assert.Throws<Phase3PersistenceException>(() => database.Gate.Commit(requestFactory(database)));
            Assert.Contains(exception.Code, new[] { "duration_out_of_scope", "duration_not_representable", "invalid_argument" });
            AssertNoStartGateRows(database);
        }
    }

    [Fact]
    public void ScopeAndExistingRunOrUseConflictsAreRejected()
    {
        using (var scopeDatabase = new TemporaryDatabase())
        {
            var otherOccurrence = PlanOccurrence.Rehydrate("occ-2", "plan-1", At(0), At(60), At(0), PlanOccurrenceStatus.Authorized, null, null, At(0), 0);
            scopeDatabase.Occurrences.Insert(otherOccurrence);
            var exception = Assert.Throws<Phase3PersistenceException>(() => scopeDatabase.Gate.Commit(scopeDatabase.CreateRequest() with { OccurrenceId = "occ-2" }));
            Assert.Equal("start_gate_conflict", exception.Code);
            AssertNoStartGateRows(scopeDatabase);
        }

        using (var existingRunDatabase = new TemporaryDatabase())
        {
            existingRunDatabase.Runs.Insert(new RecordingRun("run-1", "occ-1", At(0)));
            var exception = Assert.Throws<Phase3PersistenceException>(() => existingRunDatabase.Gate.Commit(existingRunDatabase.CreateRequest()));
            Assert.Equal("start_gate_conflict", exception.Code);
            Assert.Equal(1L, RawScalar(existingRunDatabase.Store.DatabasePath, "SELECT COUNT(*) FROM recording_runs;"));
            Assert.Equal(0L, RawScalar(existingRunDatabase.Store.DatabasePath, "SELECT COUNT(*) FROM lease_uses;"));
        }
    }

    [Fact]
    public void PartialOrCorruptEvidenceFailsClosedAndDoesNotGetCompleted()
    {
        using (var partial = new TemporaryDatabase())
        {
            var run = new RecordingRun("run-1", "occ-1", At(1));
            Assert.True(run.TryTransition(RecordingRunStatus.Preparing, At(1)).Succeeded);
            partial.Runs.Insert(run);
            var exception = Assert.Throws<Phase3PersistenceException>(() => partial.Gate.Commit(partial.CreateRequest()));
            Assert.Equal("start_gate_conflict", exception.Code);
            Assert.Equal(PlanOccurrenceStatus.Authorized, partial.Occurrences.Get("occ-1").Status);
            Assert.Equal(ConsentLeaseStatus.Active, partial.Leases.Get("lease-1").Status);
            Assert.Equal(0L, RawScalar(partial.Store.DatabasePath, "SELECT COUNT(*) FROM lease_uses;"));
        }

        using (var corrupt = new TemporaryDatabase())
        {
            RawExecute(corrupt.Store.DatabasePath, "UPDATE plans SET status_code = 'unknown-state' WHERE id = $id;", ("$id", "plan-1"));
            var exception = Assert.Throws<Phase3PersistenceException>(() => corrupt.Gate.Commit(corrupt.CreateRequest()));
            Assert.Equal("persisted_snapshot_invalid", exception.Code);
            AssertNoStartGateRows(corrupt);
        }
    }

    [Fact]
    public void LastWriteAbortRollsBackRunOccurrenceAndLeaseChanges()
    {
        using var database = new TemporaryDatabase();
        RawExecute(database.Store.DatabasePath, "CREATE TRIGGER abort_start_gate_use BEFORE INSERT ON lease_uses BEGIN SELECT RAISE(ABORT, 'test-only abort'); END;");

        var exception = Assert.Throws<Phase3PersistenceException>(() => database.Gate.Commit(database.CreateRequest()));

        Assert.Equal("constraint_violation", exception.Code);
        Assert.Equal(PlanOccurrenceStatus.Authorized, database.Occurrences.Get("occ-1").Status);
        Assert.Equal(0L, database.Occurrences.Get("occ-1").Version);
        Assert.Equal(ConsentLeaseStatus.Active, database.Leases.Get("lease-1").Status);
        Assert.Equal(0L, database.Leases.Get("lease-1").Version);
        AssertNoStartGateRows(database);
    }

    [Fact]
    public void InjectionStyleIdsAreValuesAndDoNotAlterTheOperationalSchema()
    {
        const string planId = "plan' OR 1=1; DROP TABLE plans;--";
        using var database = new TemporaryDatabase(planId: planId);

        var result = database.Gate.Commit(database.CreateRequest());

        Assert.Equal("committed", result.Code);
        Assert.Equal(planId, database.Plans.Get(planId).Id);
        Assert.Equal(1L, RawScalar(database.Store.DatabasePath, "SELECT COUNT(*) FROM plans;"));
        Assert.Equal(1L, RawScalar(database.Store.DatabasePath, "SELECT COUNT(*) FROM recording_runs;"));
    }

    [Fact]
    public void StartGateCreatesNoMediaProofLogsOrCaptureArtifacts()
    {
        using var database = new TemporaryDatabase();
        database.Gate.Commit(database.CreateRequest());

        var files = Directory.GetFiles(database.RootPath, "*", SearchOption.AllDirectories);
        Assert.DoesNotContain(files, path => path.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(files, path => path.EndsWith(".jsonl", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(files, path => path.Contains("proof", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(files, path => path.Contains("capture", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void StandingLeaseUseProofIsIssuedOnlyAfterTheFirstDurableCommit()
    {
        using var database = new TemporaryDatabase();
        var request = database.CreateRequest();

        var result = database.Gate.Commit(request);
        var proof = result.FirstCommitProof;

        Assert.Equal("committed", result.Code);
        Assert.NotNull(proof);
        Assert.Equal(CaptureAuthorizationProofKind.StandingLeaseUse, proof!.Kind);
        Assert.Equal(request.RunId, proof.RunId);
        Assert.Equal(request.RunId, proof.RecordingId);
        Assert.Equal(request.LeaseId, proof.LeaseId);
        Assert.Equal(request.LeaseUseId, proof.LeaseUseId);
        Assert.Equal(database.Scope.ScopeDigest, proof.ScopeDigest);
        Assert.Equal(request.CommitAtUtc, proof.IssuedAtUtc);
        Assert.Equal(At(60), proof.ExpiresAtUtc);
        Assert.Equal(database.Scope.CurrentUserSid, proof.CurrentUserSid);
        Assert.Equal(database.Scope.SessionBinding, proof.SessionBinding);
        Assert.Equal(database.Scope.ReservedDuration, proof.MaxDuration);
        Assert.Equal(5000L, proof.MaxDurationMilliseconds);
        Assert.Equal("standing-lease-use:" + request.LeaseUseId, proof.AuthorizationSourceId);
        Assert.Matches("^[0-9a-f]{32}$", proof.OneTimeNonce);
        Assert.Equal(CaptureAuthorizationProofState.Available, proof.State);

        var publicResultJson = JsonSerializer.Serialize(result);
        Assert.DoesNotContain("FirstCommitProof", publicResultJson, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(proof.ProofId, publicResultJson, StringComparison.Ordinal);
        Assert.DoesNotContain(proof.OneTimeNonce, publicResultJson, StringComparison.Ordinal);
    }

    [Fact]
    public void AlreadyCommittedRetryDoesNotIssueAnotherStandingProof()
    {
        using var database = new TemporaryDatabase();
        var request = database.CreateRequest();

        var first = database.Gate.Commit(request);
        var second = database.Gate.Commit(request);

        Assert.NotNull(first.FirstCommitProof);
        Assert.Equal("already_committed", second.Code);
        Assert.Null(second.FirstCommitProof);
    }

    [Fact]
    public void StandingProofIsOneTimeAndExpiresAtTheTightestAuthorizationBoundary()
    {
        using var database = new TemporaryDatabase();
        var proof = database.Gate.Commit(database.CreateRequest()).FirstCommitProof!;

        Assert.True(proof.TryConsume(At(2), out var consumeFailure), consumeFailure);
        Assert.Equal(CaptureAuthorizationProofState.Consumed, proof.State);
        Assert.False(proof.TryConsume(At(3), out var secondFailure));
        Assert.Equal("proof_already_consumed", secondFailure);

        using var expiryDatabase = new TemporaryDatabase();
        var expiringProof = expiryDatabase.Gate.Commit(expiryDatabase.CreateRequest()).FirstCommitProof!;
        Assert.False(expiringProof.TryConsume(expiringProof.ExpiresAtUtc, out var expiryFailure));
        Assert.Equal("proof_expired", expiryFailure);
        Assert.Equal(CaptureAuthorizationProofState.Expired, expiringProof.State);
    }

    [Fact]
    public async Task ConcurrentCoordinatorsProduceAtMostOneFreshStandingProof()
    {
        using var database = new TemporaryDatabase();
        var request = database.CreateRequest();
        var first = new SqlitePhase3StartGateTransaction(database.Store);
        var second = new SqlitePhase3StartGateTransaction(database.Store);

        var results = await Task.WhenAll(
            Task.Run(() => first.Commit(request)),
            Task.Run(() => second.Commit(request)));

        Assert.Equal(1, results.Count(item => item.Code == "committed" && item.FirstCommitProof is not null));
        Assert.Equal(1, results.Count(item => item.Code == "already_committed" && item.FirstCommitProof is null));
        Assert.Equal(1L, RawScalar(database.Store.DatabasePath, "SELECT COUNT(*) FROM recording_runs;"));
        Assert.Equal(1L, RawScalar(database.Store.DatabasePath, "SELECT COUNT(*) FROM lease_uses;"));
    }

    [Fact]
    public void StandingProofCannotBeConstructedThroughA_PublicConstructorOrAcceptedByInteractiveGate()
    {
        var publicConstructors = typeof(StandingLeaseUseProof).GetConstructors();
        Assert.Empty(publicConstructors);
        Assert.Equal(
            new[] { typeof(StandingLeaseUseProofIssuanceReceipt) },
            typeof(CaptureAuthorizationProofIssuer).GetMethod(
                "IssueStandingLeaseUse",
                System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!.GetParameters()
                .Select(parameter => parameter.ParameterType));

        using var database = new TemporaryDatabase();
        var proof = database.Gate.Commit(database.CreateRequest()).FirstCommitProof!;
        Assert.Equal(CaptureAuthorizationProofKind.StandingLeaseUse, proof.Kind);
        var output = Path.Combine(database.RootPath, "proof-gate.mp4");
        var recording = new Recording
        {
            State = RecState.created,
            OutputPath = output,
            Config = new CaptureConfig
            {
                SourceKind = "display",
                Bounds = (0, 0, 32, 32),
                OutputPath = output,
                DurationSeconds = 5,
            },
        };
        var plan = new CapturePlan(
            "fake",
            "fake",
            new CaptureBackendSelectionEvidence("fake", "fake", "test", "not_run", null, false),
            "display_surface",
            "display",
            null,
            nint.Zero,
            new CapturePlanBounds(0, 0, 32, 32),
            coordinateSpace: "virtual_screen");

        Assert.False(CaptureAuthorizationGate.TryConsumeInteractive(
            proof,
            recording,
            plan,
            confirmation: null,
            allowSyntheticTestAuthorization: true,
            nowUtc: At(2),
            out var ignored));
        Assert.Equal("proof_kind_not_allowed", ignored);
        Assert.Equal(CaptureAuthorizationProofState.Available, proof.State);
    }

    [Fact]
    public void ValidStandingProofAndOneTransactionSnapshotReturnAnImmutableExecutionContext()
    {
        using var database = new TemporaryDatabase();
        var proof = database.Gate.Commit(database.CreateRequest()).FirstCommitProof!;
        var consumed = database.ExecutionSnapshots.TryAuthorizeAndConsumeStandingLeaseUse(
            proof,
            EnvironmentAt(database.Scope, At(2)),
            out var authorization,
            out var reason);

        Assert.True(consumed, reason);
        Assert.NotNull(authorization);
        Assert.Equal(database.Scope.ScopeDigest, authorization!.Scope.ScopeDigest);
        Assert.Equal(database.Scope.OutputFilePath, authorization.OutputFilePath);
        Assert.Equal(database.Scope.VirtualScreenRegion, authorization.VirtualScreenRegion);
        Assert.Equal(AuthorizedCaptureBackend.FfmpegRegion, authorization.Backend);
        Assert.Equal(AuthorizedAudioMode.None, authorization.AudioMode);
        Assert.Equal(AuthorizedWakePolicy.NaturalWakeOnly, authorization.WakePolicy);
        Assert.Equal(AuthorizedDesktopRequirement.InteractiveDesktopRequired, authorization.DesktopRequirement);
        Assert.Equal(database.Scope.ReservedDuration, authorization.ReservedDuration);
        Assert.Equal(At(2), authorization.ConsumedAtUtc);
        Assert.True(authorization.IsProofConsumed);
        Assert.DoesNotContain(
            "nonce",
            JsonSerializer.Serialize(authorization),
            StringComparison.OrdinalIgnoreCase);
        Assert.Empty(typeof(StandingLeaseCaptureAuthorization).GetConstructors());
    }

    [Fact]
    public void ConsumedStandingAuthorizationProjectsEveryApprovedFieldWithoutLosingMilliseconds()
    {
        using var database = new TemporaryDatabase(scopeDuration: TimeSpan.FromMilliseconds(5001));
        var proof = database.Gate.Commit(
            database.CreateRequest(duration: TimeSpan.FromMilliseconds(5001))).FirstCommitProof!;

        Assert.True(database.ExecutionSnapshots.TryAuthorizeAndConsumeStandingLeaseUse(
            proof,
            EnvironmentAt(
                database.Scope,
                At(2),
                currentUserSid: database.Scope.CurrentUserSid,
                sessionBinding: database.Scope.SessionBinding),
            out var authorization,
            out var authorizationReason), authorizationReason);
        Assert.NotNull(authorization);

        Assert.True(StandingLeaseCaptureSpecification.TryCreate(
            authorization,
            out var specification,
            out var specificationReason), specificationReason);
        Assert.NotNull(specification);

        Assert.Equal("run-1", specification!.RunId);
        Assert.Equal("lease-1", specification.LeaseId);
        Assert.Equal("use-1", specification.LeaseUseId);
        Assert.Equal(database.Scope.ScopeDigest, specification.ScopeDigest);
        Assert.Equal(AuthorizedScopeTargetType.FixedRegion, specification.TargetType);
        Assert.Equal(AuthorizedCaptureSemantics.DesktopRegion, specification.CaptureSemantics);
        Assert.Equal(AuthorizedCoordinateSpace.PhysicalVirtualScreen, specification.CoordinateSpace);
        Assert.Equal(database.Scope.VirtualScreenRegion, specification.VirtualScreenRegion);
        Assert.Equal(database.Scope.StableDisplayFingerprint, specification.StableDisplayFingerprint);
        Assert.Equal(database.Scope.DisplayBounds, specification.DisplayBounds);
        Assert.Equal(database.Scope.DpiX, specification.DpiX);
        Assert.Equal(database.Scope.DpiY, specification.DpiY);
        Assert.Equal(database.Scope.PhysicalWidth, specification.PhysicalWidth);
        Assert.Equal(database.Scope.PhysicalHeight, specification.PhysicalHeight);
        Assert.Equal(database.Scope.Orientation, specification.Orientation);
        Assert.Equal(database.Scope.TopologyDigest, specification.TopologyDigest);
        Assert.Equal(AuthorizedCaptureBackend.FfmpegRegion, specification.Backend);
        Assert.Equal("ffmpeg-region", specification.BackendCodeValue);
        Assert.Equal(AuthorizedAudioMode.None, specification.AudioMode);
        Assert.Equal("none", specification.AudioCodeValue);
        Assert.Equal(TimeSpan.FromMilliseconds(5001), specification.MaximumDuration);
        Assert.Equal(StandingLeaseCountdownStrategy.FixedSeconds, specification.CountdownStrategy);
        Assert.Equal("fixed_seconds", specification.CountdownStrategyCodeValue);
        Assert.Equal(database.Scope.CountdownSeconds, specification.CountdownSeconds);
        Assert.Equal(database.Scope.OutputDirectory, specification.OutputDirectory);
        Assert.Equal(database.Scope.FrozenFileName, specification.FrozenFileName);
        Assert.Equal(database.Scope.OutputFilePath, specification.OutputFilePath);
        Assert.Equal(AuthorizedOutputConflictPolicy.FailIfExists, specification.OutputConflictPolicy);
        Assert.Equal(AuthorizedWakePolicy.NaturalWakeOnly, specification.WakePolicy);
        Assert.Equal("natural_wake_only", specification.WakePolicyCodeValue);
        Assert.Equal(AuthorizedDesktopRequirement.InteractiveDesktopRequired, specification.DesktopRequirement);
        Assert.Equal("interactive_desktop_required", specification.DesktopRequirementCodeValue);
    }

    [Fact]
    public void SpecificationRejectsMissingUnconsumedAndProofMismatchedAuthorizations()
    {
        Assert.False(StandingLeaseCaptureSpecification.TryCreate(
            null,
            out var missing,
            out var missingReason));
        Assert.Null(missing);
        Assert.Equal("standing_specification_authorization_missing", missingReason);

        using var database = new TemporaryDatabase();
        var proof = database.Gate.Commit(database.CreateRequest()).FirstCommitProof!;
        var availableSnapshot = new StandingLeaseExecutionSnapshot(
            database.Plans.Get("plan-1"),
            database.Occurrences.Get("occ-1"),
            database.Leases.Get("lease-1"),
            database.Scope,
            database.Runs.Get("run-1"),
            database.Uses.Get("use-1"));
        var availableAuthorization = new StandingLeaseCaptureAuthorization(
            proof,
            availableSnapshot,
            At(2));

        Assert.False(StandingLeaseCaptureSpecification.TryCreate(
            availableAuthorization,
            out var unconsumed,
            out var unconsumedReason));
        Assert.Null(unconsumed);
        Assert.Equal("standing_specification_proof_not_consumed", unconsumedReason);

        var mismatchedProof = CloneStandingProof(proof, runId: "other-run");
        Assert.True(mismatchedProof.TryConsume(At(2), out var consumeFailure), consumeFailure);
        var mismatchedAuthorization = new StandingLeaseCaptureAuthorization(
            mismatchedProof,
            availableSnapshot,
            At(2));

        Assert.False(StandingLeaseCaptureSpecification.TryCreate(
            mismatchedAuthorization,
            out var forged,
            out var forgedReason));
        Assert.Null(forged);
        Assert.Equal("standing_specification_authorization_incomplete", forgedReason);
    }

    [Fact]
    public void SpecificationHasNoMutableOrPublicConstructionOrCaptureConfigSurface()
    {
        Assert.Empty(typeof(StandingLeaseCaptureSpecification).GetConstructors());
        Assert.Empty(typeof(StandingLeaseCaptureSpecification).GetProperties(
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public));
        Assert.DoesNotContain(
            typeof(StandingLeaseCaptureSpecification).GetProperties(
                System.Reflection.BindingFlags.Instance |
                System.Reflection.BindingFlags.Public |
                System.Reflection.BindingFlags.NonPublic),
            property => property.SetMethod is not null);
        Assert.DoesNotContain(
            typeof(StandingLeaseCaptureSpecification).GetMembers(
                System.Reflection.BindingFlags.Instance |
                System.Reflection.BindingFlags.Static |
                System.Reflection.BindingFlags.Public |
                System.Reflection.BindingFlags.NonPublic),
            member => member is System.Reflection.MethodBase method &&
                method.GetParameters().Any(parameter => parameter.ParameterType == typeof(CaptureConfig)));
        Assert.DoesNotContain(
            typeof(StandingLeaseCaptureSpecification).GetProperties(
                System.Reflection.BindingFlags.Instance |
                System.Reflection.BindingFlags.Public |
                System.Reflection.BindingFlags.NonPublic),
            property => property.PropertyType == typeof(CaptureConfig) ||
                property.Name.Contains("Nonce", StringComparison.OrdinalIgnoreCase) ||
                property.Name.Contains("Secret", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void StandingSpecificationCannotBeAcceptedByTheInteractiveGate()
    {
        Assert.DoesNotContain(
            typeof(CaptureAuthorizationGate).GetMethods(
                System.Reflection.BindingFlags.Static |
                System.Reflection.BindingFlags.Public |
                System.Reflection.BindingFlags.NonPublic),
            method => method.GetParameters().Any(parameter =>
                parameter.ParameterType == typeof(StandingLeaseCaptureSpecification)));
    }

    [Fact]
    public async Task ValidStandingTicketMapsOnlyTheApprovedFixedProtocolAndStartsOnce()
    {
        using var database = new TemporaryDatabase();
        var (proof, _, specification, ticket) = CreateStandingAuthorizationAndSpecification(database);
        var backend = new CountingCaptureBackend();
        var factoryCalls = 0;
        var clockCalls = 0;
        var bridge = new StandingLeaseCaptureExecutionBridge(
            new FixedExecutionEnvironmentProvider(scope => EnvironmentAt(scope, At(2))),
            _ =>
            {
                Interlocked.Increment(ref factoryCalls);
                return backend;
            },
            (_, _) => Task.CompletedTask,
            () =>
            {
                Interlocked.Increment(ref clockCalls);
                return At(2);
            });

        var result = await bridge.ExecuteAsync(ticket);

        Assert.Equal(StandingLeaseCaptureExecutionStatus.Started, result.Status);
        Assert.Equal("", result.Reason);
        Assert.Same(backend, result.Backend);
        Assert.Equal(1, factoryCalls);
        Assert.Equal(2, clockCalls);
        Assert.Equal(1, backend.StartCalls);
        Assert.Same(proof, backend.AuthorizationProof);

        var config = Assert.IsType<CaptureConfig>(backend.Configuration);
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
        Assert.Equal(specification.StableDisplayFingerprint, config.DisplayStableIdentity);
        Assert.Equal(DisplayIdentityResolutionStatus.Resolved, config.DisplayIdentityStatus);
        Assert.Null(config.DisplayId);
        Assert.Equal(specification.OutputFilePath, config.OutputPath);
        Assert.Equal("fail_if_exists", config.OutputConflictPolicy);
        Assert.Equal(AudioCaptureSourceKind.None, config.AudioSourceKind);
        Assert.False(config.Microphone);
        Assert.Null(config.MicDevice);
        Assert.Null(config.MicDeviceName);
        Assert.Null(config.SystemLoopbackEndpoint);
        Assert.Null(config.SystemLoopbackEndpointName);
        Assert.Null(config.SystemLoopbackEndpointIsDefault);
        Assert.Equal(nint.Zero, config.WindowHandle);
        Assert.Null(config.WindowTitle);
        Assert.Null(config.ScreenshotSeries);
        Assert.Equal(30, config.Fps);
        Assert.Equal("medium", config.Quality);
        Assert.Equal((int)specification.MaximumDuration.TotalSeconds, config.DurationSeconds);
        Assert.Equal(specification.CountdownSeconds, config.CountdownSeconds);
        Assert.Null(config.RegionNormalizedBounds);
        Assert.False(config.DeferCaptureStart);
    }

    [Fact]
    public async Task MismatchedAuthorizationAndSpecificationAreRejectedBeforeBackendFactory()
    {
        using var firstDatabase = new TemporaryDatabase();
        var (_, firstAuthorization, _, _) = CreateStandingAuthorizationAndSpecification(firstDatabase);
        using var secondDatabase = new TemporaryDatabase(planId: "plan-2");
        var (_, _, secondSpecification, _) = CreateStandingAuthorizationAndSpecification(secondDatabase);

        Assert.False(StandingLeaseCaptureExecutionTicket.TryCreate(
            firstAuthorization,
            secondSpecification,
            out var ticket,
            out var reason));
        Assert.Null(ticket);
        Assert.Equal("standing_execution_ticket_mismatch", reason);

        var factoryCalls = 0;
        var bridge = new StandingLeaseCaptureExecutionBridge(
            new FixedExecutionEnvironmentProvider(scope => EnvironmentAt(scope, At(2))),
            _ =>
            {
                Interlocked.Increment(ref factoryCalls);
                return new CountingCaptureBackend();
            },
            (_, _) => Task.CompletedTask,
            () => At(2));
        var result = await bridge.ExecuteAsync(ticket);

        Assert.Equal(StandingLeaseCaptureExecutionStatus.Rejected, result.Status);
        Assert.Equal("standing_execution_ticket_missing", result.Reason);
        Assert.Equal(0, factoryCalls);
    }

    [Fact]
    public void TicketRejectsNullUnconsumedRevokedAndExpiredProofContexts()
    {
        Assert.False(StandingLeaseCaptureExecutionTicket.TryCreate(
            null,
            null,
            out var missing,
            out var missingReason));
        Assert.Null(missing);
        Assert.Equal("standing_execution_authorization_missing", missingReason);

        using var database = new TemporaryDatabase();
        var (proof, authorization, specification, _) = CreateStandingAuthorizationAndSpecification(database);
        var snapshot = CreateExecutionSnapshot(database);

        var availableProof = CloneStandingProof(proof);
        var availableAuthorization = new StandingLeaseCaptureAuthorization(
            availableProof,
            snapshot,
            At(2));
        Assert.False(StandingLeaseCaptureExecutionTicket.TryCreate(
            availableAuthorization,
            specification,
            out var availableTicket,
            out var availableReason));
        Assert.Null(availableTicket);
        Assert.Equal("standing_execution_proof_not_consumed", availableReason);

        var revokedProof = CloneStandingProof(proof);
        revokedProof.Revoke();
        var revokedAuthorization = new StandingLeaseCaptureAuthorization(
            revokedProof,
            snapshot,
            At(2));
        Assert.False(StandingLeaseCaptureExecutionTicket.TryCreate(
            revokedAuthorization,
            specification,
            out var revokedTicket,
            out var revokedReason));
        Assert.Null(revokedTicket);
        Assert.Equal("standing_execution_proof_not_consumed", revokedReason);

        var expiredProof = CloneStandingProof(proof);
        Assert.False(expiredProof.TryConsume(expiredProof.ExpiresAtUtc, out var expiryReason));
        Assert.Equal("proof_expired", expiryReason);
        var expiredAuthorization = new StandingLeaseCaptureAuthorization(
            expiredProof,
            snapshot,
            At(2));
        Assert.False(StandingLeaseCaptureExecutionTicket.TryCreate(
            expiredAuthorization,
            specification,
            out var expiredTicket,
            out var expiredReason));
        Assert.Null(expiredTicket);
        Assert.Equal("standing_execution_proof_not_consumed", expiredReason);
        Assert.True(authorization.IsProofConsumed);
    }

    [Fact]
    public async Task FinalEnvironmentMismatchIsRejectedWithoutConstructingOrStartingBackend()
    {
        var variants = new (Func<AuthorizedFixedRegionScope, StandingLeaseExecutionEnvironment> Environment, string Reason)[]
        {
            (scope => EnvironmentAt(scope, At(2), currentUserSid: "S-1-5-21-other"), "execution_environment_mismatch"),
            (scope => EnvironmentAt(scope, At(2), sessionBinding: "session-other"), "execution_environment_mismatch"),
            (scope => WithOutput(
                CompleteEnvironment(scope, EnvironmentAt(At(2))),
                CompleteEnvironment(scope, EnvironmentAt(At(2))).OutputFileSystem! with { DirectoryExists = false }),
                "execution_output_directory_unavailable"),
            (scope => WithOutput(
                CompleteEnvironment(scope, EnvironmentAt(At(2))),
                CompleteEnvironment(scope, EnvironmentAt(At(2))).OutputFileSystem! with { FrozenFileExists = true }),
                "execution_output_file_exists"),
            (scope => WithOutput(
                CompleteEnvironment(scope, EnvironmentAt(At(2))),
                CompleteEnvironment(scope, EnvironmentAt(At(2))).OutputFileSystem! with { FreeSpaceAvailable = false }),
                "execution_disk_space_unavailable"),
            (scope =>
            {
                var valid = CompleteEnvironment(scope, EnvironmentAt(At(2)));
                return new StandingLeaseExecutionEnvironment(
                    At(2), scope.CurrentUserSid, scope.SessionBinding, true,
                    Array.Empty<StandingLeaseDisplayMetadata>(), scope.TopologyDigest, valid.OutputFileSystem);
            }, "execution_display_identity_unavailable"),
            (scope =>
            {
                var valid = CompleteEnvironment(scope, EnvironmentAt(At(2)));
                return new StandingLeaseExecutionEnvironment(
                    At(2), scope.CurrentUserSid, scope.SessionBinding, true,
                    valid.Displays, new string('b', 64), valid.OutputFileSystem);
            }, "execution_topology_mismatch"),
        };

        foreach (var variant in variants)
        {
            using var database = new TemporaryDatabase();
            var (_, _, _, ticket) = CreateStandingAuthorizationAndSpecification(database);
            var factoryCalls = 0;
            var bridge = new StandingLeaseCaptureExecutionBridge(
                new FixedExecutionEnvironmentProvider(variant.Environment),
                _ =>
                {
                    Interlocked.Increment(ref factoryCalls);
                    return new CountingCaptureBackend();
                },
                (_, _) => Task.CompletedTask,
                () => At(2));

            var result = await bridge.ExecuteAsync(ticket);

            Assert.Equal(StandingLeaseCaptureExecutionStatus.Rejected, result.Status);
            Assert.Equal(variant.Reason, result.Reason);
            Assert.Null(result.Backend);
            Assert.Equal(0, factoryCalls);
        }
    }

    [Fact]
    public async Task NonIntegralMillisecondDurationIsRejectedBeforeBackendAndTicketCannotRetry()
    {
        using var database = new TemporaryDatabase(scopeDuration: TimeSpan.FromMilliseconds(5001));
        var (_, _, _, ticket) = CreateStandingAuthorizationAndSpecification(
            database,
            TimeSpan.FromMilliseconds(5001));
        var factoryCalls = 0;
        var bridge = new StandingLeaseCaptureExecutionBridge(
            new FixedExecutionEnvironmentProvider(scope => EnvironmentAt(scope, At(2))),
            _ =>
            {
                Interlocked.Increment(ref factoryCalls);
                return new CountingCaptureBackend();
            },
            (_, _) => Task.CompletedTask,
            () => At(2));

        var result = await bridge.ExecuteAsync(ticket);
        var retry = await bridge.ExecuteAsync(ticket);

        Assert.Equal(StandingLeaseCaptureExecutionStatus.Rejected, result.Status);
        Assert.Equal("standing_execution_duration_not_representable", result.Reason);
        Assert.Equal(StandingLeaseCaptureExecutionStatus.Rejected, retry.Status);
        Assert.Equal("standing_execution_already_claimed", retry.Reason);
        Assert.Equal(0, factoryCalls);
    }

    [Fact]
    public async Task CountdownCrossingProofExpiryIsRejectedBeforeBackendAndCannotRetry()
    {
        using var database = new TemporaryDatabase(
            occurrenceEndSeconds: 5,
            scopeDuration: TimeSpan.FromSeconds(2),
            scopeCountdownSeconds: 3);
        var (_, _, _, ticket) = CreateStandingAuthorizationAndSpecification(database);
        var factoryCalls = 0;
        var backend = new CountingCaptureBackend();
        var currentTime = At(2);
        var bridge = new StandingLeaseCaptureExecutionBridge(
            new FixedExecutionEnvironmentProvider(scope => EnvironmentAt(scope, At(2))),
            _ =>
            {
                Interlocked.Increment(ref factoryCalls);
                return backend;
            },
            (_, _) =>
            {
                currentTime = At(5);
                return Task.CompletedTask;
            },
            () => currentTime);

        var result = await bridge.ExecuteAsync(ticket);
        var retry = await bridge.ExecuteAsync(ticket);

        Assert.Equal(StandingLeaseCaptureExecutionStatus.Rejected, result.Status);
        Assert.Equal("standing_execution_window_expired", result.Reason);
        Assert.Equal(0, factoryCalls);
        Assert.Equal(0, backend.StartCalls);
        Assert.Equal("standing_execution_already_claimed", retry.Reason);
    }

    [Fact]
    public async Task RemainingWindowShorterThanMaximumDurationIsRejectedBeforeBackendAndCannotRetry()
    {
        using var database = new TemporaryDatabase();
        var (_, _, _, ticket) = CreateStandingAuthorizationAndSpecification(database);
        var factoryCalls = 0;
        var bridge = new StandingLeaseCaptureExecutionBridge(
            new FixedExecutionEnvironmentProvider(scope => EnvironmentAt(scope, At(57))),
            _ =>
            {
                Interlocked.Increment(ref factoryCalls);
                return new CountingCaptureBackend();
            },
            (_, _) => Task.CompletedTask,
            () => At(57));

        var result = await bridge.ExecuteAsync(ticket);
        var retry = await bridge.ExecuteAsync(ticket);

        Assert.Equal(StandingLeaseCaptureExecutionStatus.Rejected, result.Status);
        Assert.Equal("standing_execution_duration_exceeds_window", result.Reason);
        Assert.Equal(0, factoryCalls);
        Assert.Equal("standing_execution_already_claimed", retry.Reason);
    }

    [Fact]
    public async Task MaximumDurationEndingExactlyAtExpiryIsAllowedButStartingAtExpiryIsRejected()
    {
        using var database = new TemporaryDatabase();
        var (_, _, specification, ticket) = CreateStandingAuthorizationAndSpecification(database);
        var backend = new CountingCaptureBackend();
        var factoryCalls = 0;
        var bridge = new StandingLeaseCaptureExecutionBridge(
            new FixedExecutionEnvironmentProvider(scope => EnvironmentAt(scope, At(55))),
            _ =>
            {
                Interlocked.Increment(ref factoryCalls);
                return backend;
            },
            (_, _) => Task.CompletedTask,
            () => At(55));

        var result = await bridge.ExecuteAsync(ticket);

        Assert.Equal(StandingLeaseCaptureExecutionStatus.Started, result.Status);
        Assert.Equal(1, factoryCalls);
        Assert.Equal(1, backend.StartCalls);
        Assert.Equal((int)specification.MaximumDuration.TotalSeconds, backend.Configuration!.DurationSeconds);

        using var expiredDatabase = new TemporaryDatabase();
        var (_, _, _, expiredTicket) = CreateStandingAuthorizationAndSpecification(expiredDatabase);
        var expiredFactoryCalls = 0;
        var expiredBridge = new StandingLeaseCaptureExecutionBridge(
            new FixedExecutionEnvironmentProvider(scope => EnvironmentAt(scope, At(60))),
            _ =>
            {
                Interlocked.Increment(ref expiredFactoryCalls);
                return new CountingCaptureBackend();
            },
            (_, _) => Task.CompletedTask,
            () => At(60));

        var expiredResult = await expiredBridge.ExecuteAsync(expiredTicket);
        var expiredRetry = await expiredBridge.ExecuteAsync(expiredTicket);

        Assert.Equal(StandingLeaseCaptureExecutionStatus.Rejected, expiredResult.Status);
        Assert.Equal("standing_execution_window_expired", expiredResult.Reason);
        Assert.Equal(0, expiredFactoryCalls);
        Assert.Equal("standing_execution_already_claimed", expiredRetry.Reason);
    }

    [Fact]
    public async Task FinalTimeGateRejectsWhenEnvironmentReviewHasJustCrossedExpiry()
    {
        using var database = new TemporaryDatabase();
        var (_, _, _, ticket) = CreateStandingAuthorizationAndSpecification(database);
        var factoryCalls = 0;
        var bridge = new StandingLeaseCaptureExecutionBridge(
            new FixedExecutionEnvironmentProvider(scope => EnvironmentAt(scope, At(2))),
            _ =>
            {
                Interlocked.Increment(ref factoryCalls);
                return new CountingCaptureBackend();
            },
            (_, _) => Task.CompletedTask,
            SequenceClock(At(2), At(60)));

        var result = await bridge.ExecuteAsync(ticket);
        var retry = await bridge.ExecuteAsync(ticket);

        Assert.Equal(StandingLeaseCaptureExecutionStatus.Rejected, result.Status);
        Assert.Equal("standing_execution_window_expired", result.Reason);
        Assert.Equal(0, factoryCalls);
        Assert.Equal("standing_execution_already_claimed", retry.Reason);
    }

    [Fact]
    public async Task ExecutionBeforeProofIssuanceIsRejectedBeforeEnvironmentAndBackend()
    {
        using var database = new TemporaryDatabase();
        var (_, _, _, ticket) = CreateStandingAuthorizationAndSpecification(database);
        var factoryCalls = 0;
        var environmentCalls = 0;
        var bridge = new StandingLeaseCaptureExecutionBridge(
            new FixedExecutionEnvironmentProvider(scope =>
            {
                Interlocked.Increment(ref environmentCalls);
                return EnvironmentAt(scope, At(2));
            }),
            _ =>
            {
                Interlocked.Increment(ref factoryCalls);
                return new CountingCaptureBackend();
            },
            (_, _) => Task.CompletedTask,
            () => At(-1));

        var result = await bridge.ExecuteAsync(ticket);
        var retry = await bridge.ExecuteAsync(ticket);

        Assert.Equal(StandingLeaseCaptureExecutionStatus.Rejected, result.Status);
        Assert.Equal("standing_execution_not_yet_valid", result.Reason);
        Assert.Equal(0, environmentCalls);
        Assert.Equal(0, factoryCalls);
        Assert.Equal("standing_execution_already_claimed", retry.Reason);
    }

    [Fact]
    public async Task NonUtcAndUnavailableClockSamplesFailClosedBeforeBackendAndCannotRetry()
    {
        using var nonUtcDatabase = new TemporaryDatabase();
        var (_, _, _, nonUtcTicket) = CreateStandingAuthorizationAndSpecification(nonUtcDatabase);
        var nonUtcFactoryCalls = 0;
        var nonUtcBridge = new StandingLeaseCaptureExecutionBridge(
            new FixedExecutionEnvironmentProvider(scope => EnvironmentAt(scope, At(2))),
            _ =>
            {
                Interlocked.Increment(ref nonUtcFactoryCalls);
                return new CountingCaptureBackend();
            },
            (_, _) => Task.CompletedTask,
            () => At(2).ToOffset(TimeSpan.FromHours(8)));

        var nonUtcResult = await nonUtcBridge.ExecuteAsync(nonUtcTicket);
        var nonUtcRetry = await nonUtcBridge.ExecuteAsync(nonUtcTicket);

        Assert.Equal(StandingLeaseCaptureExecutionStatus.Rejected, nonUtcResult.Status);
        Assert.Equal("standing_execution_time_not_utc", nonUtcResult.Reason);
        Assert.Equal(0, nonUtcFactoryCalls);
        Assert.Equal("standing_execution_already_claimed", nonUtcRetry.Reason);

        using var unavailableDatabase = new TemporaryDatabase();
        var (_, _, _, unavailableTicket) = CreateStandingAuthorizationAndSpecification(unavailableDatabase);
        var unavailableFactoryCalls = 0;
        var unavailableBridge = new StandingLeaseCaptureExecutionBridge(
            new FixedExecutionEnvironmentProvider(scope => EnvironmentAt(scope, At(2))),
            _ =>
            {
                Interlocked.Increment(ref unavailableFactoryCalls);
                return new CountingCaptureBackend();
            },
            (_, _) => Task.CompletedTask,
            () => null);

        var unavailableResult = await unavailableBridge.ExecuteAsync(unavailableTicket);
        var unavailableRetry = await unavailableBridge.ExecuteAsync(unavailableTicket);

        Assert.Equal(StandingLeaseCaptureExecutionStatus.Rejected, unavailableResult.Status);
        Assert.Equal("standing_execution_clock_unavailable", unavailableResult.Reason);
        Assert.Equal(0, unavailableFactoryCalls);
        Assert.Equal("standing_execution_already_claimed", unavailableRetry.Reason);

        using var throwingDatabase = new TemporaryDatabase();
        var (_, _, _, throwingTicket) = CreateStandingAuthorizationAndSpecification(throwingDatabase);
        var throwingFactoryCalls = 0;
        var throwingBridge = new StandingLeaseCaptureExecutionBridge(
            new FixedExecutionEnvironmentProvider(scope => EnvironmentAt(scope, At(2))),
            _ =>
            {
                Interlocked.Increment(ref throwingFactoryCalls);
                return new CountingCaptureBackend();
            },
            (_, _) => Task.CompletedTask,
            () => throw new InvalidOperationException("clock unavailable"));

        var throwingResult = await throwingBridge.ExecuteAsync(throwingTicket);

        Assert.Equal(StandingLeaseCaptureExecutionStatus.Rejected, throwingResult.Status);
        Assert.Equal("standing_execution_clock_unavailable", throwingResult.Reason);
        Assert.Equal(0, throwingFactoryCalls);
    }

    [Fact]
    public async Task OneShotCoordinatorCompletesCommitProofBridgeChainAndStartsOnce()
    {
        using var database = new TemporaryDatabase();
        var backend = new CountingCaptureBackend();
        var factoryCalls = 0;
        var environmentCalls = 0;
        var providerSawCommittedEvidence = false;
        var provider = new FixedExecutionEnvironmentProvider(scope =>
        {
            Interlocked.Increment(ref environmentCalls);
            providerSawCommittedEvidence |=
                RawScalar(database.Store.DatabasePath, "SELECT COUNT(*) FROM recording_runs WHERE id = 'run-1' AND status_code = 'start_committed';") == 1;
            return EnvironmentAt(scope, At(2));
        });
        var coordinator = new StandingLeaseOneShotExecutionCoordinator(
            database.Store,
            provider,
            _ =>
            {
                Interlocked.Increment(ref factoryCalls);
                Assert.Equal(1L, RawScalar(database.Store.DatabasePath, "SELECT COUNT(*) FROM recording_runs WHERE id = 'run-1';"));
                return backend;
            },
            (_, _) => Task.CompletedTask,
            () => At(2));

        var result = await coordinator.ExecuteAsync(CreateOneShotRequest(database));

        Assert.Equal(StandingLeaseOneShotExecutionStatus.Started, result.Status);
        Assert.Equal("", result.Reason);
        Assert.NotNull(result.LifecycleSession);
        Assert.Equal(1, factoryCalls);
        Assert.Equal(1, backend.StartCalls);
        Assert.Equal(2, environmentCalls);
        Assert.True(providerSawCommittedEvidence);
        Assert.Equal(PlanOccurrenceStatus.RunCreated, database.Occurrences.Get("occ-1").Status);
        Assert.Equal("run-1", database.Occurrences.Get("occ-1").RunId);
        Assert.Equal(RecordingRunStatus.StartCommitted, database.Runs.Get("run-1").Status);
        Assert.True(database.Runs.Get("run-1").HasCrossedStartCommit);
        Assert.Equal(LeaseUseStatus.StartCommitted, database.Uses.Get("use-1").Status);
        Assert.Equal(1, database.Uses.Get("use-1").ReservedUseCount);
        Assert.Equal(database.Scope.ReservedDuration, database.Uses.Get("use-1").ReservedDuration);
        Assert.Equal(ConsentLeaseStatus.Exhausted, database.Leases.Get("lease-1").Status);
    }

    [Fact]
    public void RecordingEngineProductionStandingEntryUsesDurableFirstFrameBeforeRecordingUi()
    {
        using var database = new TemporaryDatabase(
            scopeDuration: TimeSpan.FromSeconds(20),
            scopeSessionBinding: CaptureAuthorizationSessionBinding.Current);
        var (_, _, specification, ticket) = CreateStandingAuthorizationAndSpecification(database);
        Assert.True(ticket.TryClaim(out var claimFailure), claimFailure);

        var backend = new LifecycleCaptureBackend
        {
            EmitFirstFrameOnStart = true,
            StopResult = ValidOutputMeta(1),
        };
        var tray = new StandingEngineTestTray();
        using var engine = new RecordingEngine(
            new TestAuditLogger(),
            displayTopologyProvider: new StandingDisplayTopologyProvider(database.Scope));
        engine.SetTray(tray);
        engine.DisableDeadlineWatchdogForTests = true;
        engine.UtcNowForTests = () => At(2).UtcDateTime;
        engine.StandingExecutionEnvironmentProviderForTests = scope =>
            CompleteEnvironment(
                scope,
                EnvironmentAt(
                    scope,
                    At(2),
                    currentUserSid: scope.CurrentUserSid,
                    sessionBinding: scope.SessionBinding));
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

        var started = engine.StartStandingCapture(
            ticket,
            tray,
            candidate => new StandingLeaseOneShotExecutionSession(
                database.Store,
                specification,
                candidate,
                () => At(2),
                attachBackendCallbacks: false));

        Assert.True(started.Status == StandingLeaseCaptureExecutionStatus.Started, started.Reason);
        Assert.Equal(1, backend.StartCalls);
        Assert.Equal(0, backend.StopCalls);
        Assert.Equal(RecordingRunStatus.Recording, database.Runs.Get("run-1").Status);
        Assert.Equal(LeaseUseStatus.Consumed, database.Uses.Get("use-1").Status);
        Assert.Equal(1, tray.RecordingCalls);

        _ = engine.Stop("run-1", "user_requested");

        Assert.Equal(RecordingRunStatus.Settled, database.Runs.Get("run-1").Status);
        Assert.Equal(LeaseUseStatus.Settled, database.Uses.Get("use-1").Status);
        Assert.Equal(PlanOccurrenceStatus.Completed, database.Occurrences.Get("occ-1").Status);
        Assert.Equal(1, backend.StopCalls);
        Assert.Equal(1, backend.DisposeCalls);
    }

    [Fact]
    public void RecordingEngineBackendStartFailureAfterFirstFrameCannotSettleValidStopMedia()
    {
        using var database = new TemporaryDatabase(
            scopeDuration: TimeSpan.FromSeconds(20),
            scopeSessionBinding: CaptureAuthorizationSessionBinding.Current);
        var (_, _, specification, ticket) = CreateStandingAuthorizationAndSpecification(database);
        Assert.True(ticket.TryClaim(out var claimFailure), claimFailure);

        var backend = new LifecycleCaptureBackend
        {
            EmitFirstFrameOnStart = true,
            ThrowAfterFirstFrameOnStart = true,
            StopResult = ValidOutputMeta(1),
        };
        var tray = new StandingEngineTestTray();
        using var engine = new RecordingEngine(
            new TestAuditLogger(),
            displayTopologyProvider: new StandingDisplayTopologyProvider(database.Scope));
        engine.SetTray(tray);
        engine.DisableDeadlineWatchdogForTests = true;
        engine.UtcNowForTests = () => At(2).UtcDateTime;
        engine.StandingExecutionEnvironmentProviderForTests = scope =>
            CompleteEnvironment(
                scope,
                EnvironmentAt(scope, At(2), scope.CurrentUserSid, scope.SessionBinding));
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

        var result = engine.StartStandingCapture(
            ticket,
            tray,
            candidate => new StandingLeaseOneShotExecutionSession(
                database.Store,
                specification,
                candidate,
                () => At(2),
                attachBackendCallbacks: false));

        Assert.Equal(StandingLeaseCaptureExecutionStatus.Failed, result.Status);
        Assert.Equal(1, backend.StartCalls);
        Assert.Equal(1, backend.StopCalls);
        Assert.Equal(1, backend.DisposeCalls);
        Assert.Equal(RecordingRunStatus.Failed, database.Runs.Get("run-1").Status);
        Assert.Equal("backend_start_failed_after_first_frame", database.Runs.Get("run-1").TerminalReasonCode);
        Assert.Equal(LeaseUseStatus.StartedUnknown, database.Uses.Get("use-1").Status);
        Assert.Equal(PlanOccurrenceStatus.Blocked, database.Occurrences.Get("occ-1").Status);
        Assert.NotEqual(PlanOccurrenceStatus.Completed, database.Occurrences.Get("occ-1").Status);
        Assert.NotEqual(RecState.completed, engine._recs["run-1"].State);
        Assert.Equal(RecState.failed, engine._recs["run-1"].State);
    }

    [Fact]
    public void RecordingEngineFirstFramePersistenceFailureStopsOnceAndPublishesNoRecording()
    {
        using var database = new TemporaryDatabase(
            scopeDuration: TimeSpan.FromSeconds(20),
            scopeSessionBinding: CaptureAuthorizationSessionBinding.Current);
        var (_, _, specification, ticket) = CreateStandingAuthorizationAndSpecification(database);
        Assert.True(ticket.TryClaim(out var claimFailure), claimFailure);

        var backend = new LifecycleCaptureBackend { EmitFirstFrameOnStart = true };
        var commits = 0;
        var tray = new StandingEngineTestTray();
        using var engine = new RecordingEngine(
            new TestAuditLogger(),
            displayTopologyProvider: new StandingDisplayTopologyProvider(database.Scope));
        engine.SetTray(tray);
        engine.DisableDeadlineWatchdogForTests = true;
        engine.UtcNowForTests = () => At(2).UtcDateTime;
        engine.StandingExecutionEnvironmentProviderForTests = scope =>
            CompleteEnvironment(scope, EnvironmentAt(scope, At(2), scope.CurrentUserSid, scope.SessionBinding));
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

        var result = engine.StartStandingCapture(
            ticket,
            tray,
            candidate => new StandingLeaseOneShotExecutionSession(
                database.Store,
                specification,
                candidate,
                () => At(2),
                (_, _) =>
                {
                    if (Interlocked.Increment(ref commits) >= 1)
                        throw new InvalidOperationException("first-frame durable commit failure");
                },
                attachBackendCallbacks: false));

        Assert.Equal(StandingLeaseCaptureExecutionStatus.Failed, result.Status);
        Assert.Equal(1, backend.StartCalls);
        Assert.Equal(1, backend.StopCalls);
        Assert.Equal(1, backend.DisposeCalls);
        Assert.Equal(RecordingRunStatus.StartedUnknown, database.Runs.Get("run-1").Status);
        Assert.Equal(LeaseUseStatus.StartedUnknown, database.Uses.Get("use-1").Status);
        Assert.Equal(PlanOccurrenceStatus.Blocked, database.Occurrences.Get("occ-1").Status);
        Assert.NotEqual(RecState.recording, engine._recs["run-1"].State);
        Assert.NotEqual(RecState.completed, engine._recs["run-1"].State);
    }

    [Fact]
    public void RecordingEngineNaturalExitPersistenceFailureUsesOnePhysicalStopAndFailsClosed()
    {
        using var database = new TemporaryDatabase(
            scopeDuration: TimeSpan.FromSeconds(20),
            scopeSessionBinding: CaptureAuthorizationSessionBinding.Current);
        var (_, _, specification, ticket) = CreateStandingAuthorizationAndSpecification(database);
        Assert.True(ticket.TryClaim(out var claimFailure), claimFailure);

        var backend = new LifecycleCaptureBackend { EmitFirstFrameOnStart = true };
        var commits = 0;
        var tray = new StandingEngineTestTray();
        using var engine = new RecordingEngine(
            new TestAuditLogger(),
            displayTopologyProvider: new StandingDisplayTopologyProvider(database.Scope));
        engine.SetTray(tray);
        engine.DisableDeadlineWatchdogForTests = true;
        engine.UtcNowForTests = () => At(2).UtcDateTime;
        engine.StandingExecutionEnvironmentProviderForTests = scope =>
            CompleteEnvironment(scope, EnvironmentAt(scope, At(2), scope.CurrentUserSid, scope.SessionBinding));
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

        var result = engine.StartStandingCapture(
            ticket,
            tray,
            candidate => new StandingLeaseOneShotExecutionSession(
                database.Store,
                specification,
                candidate,
                () => At(2),
                (_, _) =>
                {
                    if (Interlocked.Increment(ref commits) >= 2)
                        throw new InvalidOperationException("natural settlement durable commit failure");
                },
                attachBackendCallbacks: false));

        Assert.Equal(StandingLeaseCaptureExecutionStatus.Started, result.Status);
        backend.RaiseNaturalExit(0, ValidOutputMeta(1));

        Assert.Equal(1, backend.StartCalls);
        Assert.Equal(1, backend.StopCalls);
        Assert.Equal(1, backend.DisposeCalls);
        Assert.NotEqual(RecordingRunStatus.Settled, database.Runs.Get("run-1").Status);
        Assert.Equal(LeaseUseStatus.StartedUnknown, database.Uses.Get("use-1").Status);
        Assert.Equal(PlanOccurrenceStatus.Blocked, database.Occurrences.Get("occ-1").Status);
        Assert.NotEqual(RecState.completed, engine._recs["run-1"].State);
    }

    [Fact]
    public async Task OneShotCoordinatorStartGateFailureDoesNotEnterAuthorizationOrBackend()
    {
        using var database = new TemporaryDatabase(planStatus: PlanDefinitionStatus.Paused);
        var environmentCalls = 0;
        var factoryCalls = 0;
        var coordinator = new StandingLeaseOneShotExecutionCoordinator(
            database.Store,
            new FixedExecutionEnvironmentProvider(scope =>
            {
                Interlocked.Increment(ref environmentCalls);
                return EnvironmentAt(scope, At(2));
            }),
            _ =>
            {
                Interlocked.Increment(ref factoryCalls);
                return new CountingCaptureBackend();
            },
            (_, _) => Task.CompletedTask,
            () => At(2));

        var result = await coordinator.ExecuteAsync(CreateOneShotRequest(database));

        Assert.Equal(StandingLeaseOneShotExecutionStatus.Rejected, result.Status);
        Assert.Equal("plan_not_enabled", result.Reason);
        Assert.Equal(0, environmentCalls);
        Assert.Equal(0, factoryCalls);
        AssertNoStartGateRows(database);
    }

    [Fact]
    public async Task OneShotCoordinatorSecondCallIsAlreadyCommittedWithoutNewProofOrBackend()
    {
        using var database = new TemporaryDatabase();
        var backend = new CountingCaptureBackend();
        var firstFactoryCalls = 0;
        var secondFactoryCalls = 0;
        var environmentCalls = 0;
        var provider = new FixedExecutionEnvironmentProvider(scope =>
        {
            Interlocked.Increment(ref environmentCalls);
            return EnvironmentAt(scope, At(2));
        });
        var request = CreateOneShotRequest(database);
        var firstCoordinator = new StandingLeaseOneShotExecutionCoordinator(
            database.Store,
            provider,
            _ =>
            {
                Interlocked.Increment(ref firstFactoryCalls);
                return backend;
            },
            (_, _) => Task.CompletedTask,
            () => At(2));
        var secondCoordinator = new StandingLeaseOneShotExecutionCoordinator(
            database.Store,
            provider,
            _ =>
            {
                Interlocked.Increment(ref secondFactoryCalls);
                return new CountingCaptureBackend();
            },
            (_, _) => Task.CompletedTask,
            () => At(2));

        var first = await firstCoordinator.ExecuteAsync(request);
        var second = await secondCoordinator.ExecuteAsync(request);

        Assert.Equal(StandingLeaseOneShotExecutionStatus.Started, first.Status);
        Assert.Equal(StandingLeaseOneShotExecutionStatus.AlreadyCommitted, second.Status);
        Assert.Equal("already_committed", second.Reason);
        Assert.Equal(1, firstFactoryCalls);
        Assert.Equal(0, secondFactoryCalls);
        Assert.Equal(1, backend.StartCalls);
        Assert.Equal(2, environmentCalls);
        Assert.Equal(1L, RawScalar(database.Store.DatabasePath, "SELECT COUNT(*) FROM recording_runs;"));
        Assert.Equal(1L, RawScalar(database.Store.DatabasePath, "SELECT COUNT(*) FROM lease_uses;"));
    }

    [Fact]
    public async Task OneShotCoordinatorLoaderFailureIsCommittedButNotStartedAndCannotRetry()
    {
        using var database = new TemporaryDatabase();
        var factoryCalls = 0;
        var providerCalls = 0;
        var coordinator = new StandingLeaseOneShotExecutionCoordinator(
            database.Store,
            new FixedExecutionEnvironmentProvider(scope =>
            {
                Interlocked.Increment(ref providerCalls);
                return EnvironmentAt(scope, At(2), currentUserSid: "S-1-5-21-other");
            }),
            _ =>
            {
                Interlocked.Increment(ref factoryCalls);
                return new CountingCaptureBackend();
            },
            (_, _) => Task.CompletedTask,
            () => At(2));

        var request = CreateOneShotRequest(database);
        var result = await coordinator.ExecuteAsync(request);
        var retry = await coordinator.ExecuteAsync(request);

        Assert.Equal(StandingLeaseOneShotExecutionStatus.CommittedNotStarted, result.Status);
        Assert.Equal("execution_environment_mismatch", result.Reason);
        Assert.Equal(StandingLeaseOneShotExecutionStatus.AlreadyCommitted, retry.Status);
        Assert.Equal(0, factoryCalls);
        Assert.Equal(1, providerCalls);
        Assert.Equal(RecordingRunStatus.StartedUnknown, database.Runs.Get("run-1").Status);
        Assert.Equal(LeaseUseStatus.StartedUnknown, database.Uses.Get("use-1").Status);
        Assert.Equal(PlanOccurrenceStatus.Blocked, database.Occurrences.Get("occ-1").Status);
    }

    [Fact]
    public async Task OneShotCoordinatorBridgeTimeGateRejectsAfterCommitWithoutRetry()
    {
        using var database = new TemporaryDatabase();
        var factoryCalls = 0;
        var providerCalls = 0;
        var coordinator = new StandingLeaseOneShotExecutionCoordinator(
            database.Store,
            new FixedExecutionEnvironmentProvider(scope =>
            {
                Interlocked.Increment(ref providerCalls);
                return EnvironmentAt(scope, At(2));
            }),
            _ =>
            {
                Interlocked.Increment(ref factoryCalls);
                return new CountingCaptureBackend();
            },
            (_, _) => Task.CompletedTask,
            SequenceClock(At(2), At(57)));

        var request = CreateOneShotRequest(database);
        var result = await coordinator.ExecuteAsync(request);
        var retryCoordinator = new StandingLeaseOneShotExecutionCoordinator(
            database.Store,
            new FixedExecutionEnvironmentProvider(scope =>
            {
                Interlocked.Increment(ref providerCalls);
                return EnvironmentAt(scope, At(2));
            }),
            _ =>
            {
                Interlocked.Increment(ref factoryCalls);
                return new CountingCaptureBackend();
            },
            (_, _) => Task.CompletedTask,
            () => At(2));
        var retry = await retryCoordinator.ExecuteAsync(request);

        Assert.Equal(StandingLeaseOneShotExecutionStatus.CommittedNotStarted, result.Status);
        Assert.Equal("standing_execution_duration_exceeds_window", result.Reason);
        Assert.Equal(StandingLeaseOneShotExecutionStatus.AlreadyCommitted, retry.Status);
        Assert.Equal(0, factoryCalls);
        Assert.Equal(1, providerCalls);
    }

    [Fact]
    public async Task OneShotCoordinatorFinalBridgeClockFailureIsCommittedButNotStarted()
    {
        using var database = new TemporaryDatabase();
        var backend = new CountingCaptureBackend();
        var factoryCalls = 0;
        var providerCalls = 0;
        var coordinator = new StandingLeaseOneShotExecutionCoordinator(
            database.Store,
            new FixedExecutionEnvironmentProvider(scope =>
            {
                Interlocked.Increment(ref providerCalls);
                return EnvironmentAt(scope, At(2));
            }),
            _ =>
            {
                Interlocked.Increment(ref factoryCalls);
                return backend;
            },
            (_, _) => Task.CompletedTask,
            SequenceClock(At(2), At(2), At(60)));

        var result = await coordinator.ExecuteAsync(CreateOneShotRequest(database));

        Assert.Equal(StandingLeaseOneShotExecutionStatus.CommittedNotStarted, result.Status);
        Assert.Equal("standing_execution_window_expired", result.Reason);
        Assert.Equal(0, factoryCalls);
        Assert.Equal(0, backend.StartCalls);
        Assert.Equal(2, providerCalls);
    }

    [Fact]
    public async Task OneShotCoordinatorCancellationAndStartFailureRemainCommittedAndUnretryable()
    {
        using var cancellationDatabase = new TemporaryDatabase(scopeCountdownSeconds: 3);
        var cancellationBackend = new CountingCaptureBackend();
        var countdownEntered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cancellation = new CancellationTokenSource();
        var cancellationCoordinator = new StandingLeaseOneShotExecutionCoordinator(
            cancellationDatabase.Store,
            new FixedExecutionEnvironmentProvider(scope => EnvironmentAt(scope, At(2))),
            _ => cancellationBackend,
            async (_, token) =>
            {
                countdownEntered.TrySetResult(true);
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
            },
            () => At(2));

        var cancellationExecution = cancellationCoordinator.ExecuteAsync(
            CreateOneShotRequest(cancellationDatabase),
            cancellation.Token);
        Assert.True(await countdownEntered.Task);
        cancellation.Cancel();
        var cancelled = await cancellationExecution;
        var cancelledRetry = await cancellationCoordinator.ExecuteAsync(CreateOneShotRequest(cancellationDatabase));

        Assert.Equal(StandingLeaseOneShotExecutionStatus.CommittedNotStarted, cancelled.Status);
        Assert.Equal("standing_execution_cancelled", cancelled.Reason);
        Assert.Equal(0, cancellationBackend.StartCalls);
        Assert.Equal(StandingLeaseOneShotExecutionStatus.AlreadyCommitted, cancelledRetry.Status);
        Assert.Equal(RecordingRunStatus.StartedUnknown, cancellationDatabase.Runs.Get("run-1").Status);
        Assert.Equal(LeaseUseStatus.StartedUnknown, cancellationDatabase.Uses.Get("use-1").Status);
        Assert.Equal(PlanOccurrenceStatus.Blocked, cancellationDatabase.Occurrences.Get("occ-1").Status);

        using var failedDatabase = new TemporaryDatabase();
        var failedBackend = new CountingCaptureBackend { ThrowOnStart = true };
        var failedCoordinator = new StandingLeaseOneShotExecutionCoordinator(
            failedDatabase.Store,
            new FixedExecutionEnvironmentProvider(scope => EnvironmentAt(scope, At(2))),
            _ => failedBackend,
            (_, _) => Task.CompletedTask,
            () => At(2));

        var failed = await failedCoordinator.ExecuteAsync(CreateOneShotRequest(failedDatabase));
        var failedRetry = await failedCoordinator.ExecuteAsync(CreateOneShotRequest(failedDatabase));

        Assert.Equal(StandingLeaseOneShotExecutionStatus.CommittedNotStarted, failed.Status);
        Assert.Equal("standing_execution_backend_start_failed", failed.Reason);
        Assert.Equal(1, failedBackend.StartCalls);
        Assert.Equal(1, failedBackend.DisposeCalls);
        Assert.Equal(StandingLeaseOneShotExecutionStatus.AlreadyCommitted, failedRetry.Status);
        Assert.Equal(RecordingRunStatus.StartedUnknown, failedDatabase.Runs.Get("run-1").Status);
        Assert.Equal(LeaseUseStatus.StartedUnknown, failedDatabase.Uses.Get("use-1").Status);
        Assert.Equal(PlanOccurrenceStatus.Blocked, failedDatabase.Occurrences.Get("occ-1").Status);
    }

    [Fact]
    public async Task OneShotCoordinatorUsesTrustedClockAndApprovedScopeForTimeBoundaries()
    {
        using var startDatabase = new TemporaryDatabase();
        var startBackend = new CountingCaptureBackend();
        var startCoordinator = new StandingLeaseOneShotExecutionCoordinator(
            startDatabase.Store,
            new FixedExecutionEnvironmentProvider(scope => EnvironmentAt(scope, At(0))),
            _ => startBackend,
            (_, _) => Task.CompletedTask,
            () => At(0));
        var atWindowStart = await startCoordinator.ExecuteAsync(CreateOneShotRequest(startDatabase));

        Assert.Equal(StandingLeaseOneShotExecutionStatus.Started, atWindowStart.Status);
        Assert.Equal(1, startBackend.StartCalls);

        using var endDatabase = new TemporaryDatabase();
        var endFactoryCalls = 0;
        var endCoordinator = new StandingLeaseOneShotExecutionCoordinator(
            endDatabase.Store,
            new FixedExecutionEnvironmentProvider(scope => EnvironmentAt(scope, At(60))),
            _ =>
            {
                Interlocked.Increment(ref endFactoryCalls);
                return new CountingCaptureBackend();
            },
            (_, _) => Task.CompletedTask,
            () => At(60));
        var atWindowEnd = await endCoordinator.ExecuteAsync(CreateOneShotRequest(endDatabase));

        Assert.Equal(StandingLeaseOneShotExecutionStatus.Rejected, atWindowEnd.Status);
        Assert.Equal("occurrence_not_in_window", atWindowEnd.Reason);
        Assert.Equal(0, endFactoryCalls);

        using var exactDatabase = new TemporaryDatabase();
        var exactBackend = new CountingCaptureBackend();
        var exactCoordinator = new StandingLeaseOneShotExecutionCoordinator(
            exactDatabase.Store,
            new FixedExecutionEnvironmentProvider(scope => EnvironmentAt(scope, At(55))),
            _ => exactBackend,
            (_, _) => Task.CompletedTask,
            () => At(55));
        var exact = await exactCoordinator.ExecuteAsync(CreateOneShotRequest(exactDatabase));

        Assert.Equal(StandingLeaseOneShotExecutionStatus.Started, exact.Status);
        Assert.Equal(1, exactBackend.StartCalls);
    }

    [Fact]
    public async Task OneShotRequestAndResultDoNotExposeCallerCaptureOrProofInputs()
    {
        var requestProperties = typeof(StandingLeaseOneShotExecutionRequest)
            .GetProperties(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic);
        Assert.Equal(
            new[] { "PlanId", "OccurrenceId", "LeaseId", "RunId", "LeaseUseId", "ExpectedPlanVersion", "ExpectedOccurrenceVersion", "ExpectedLeaseVersion", "ScopeId", "ScopeDigest" }
                .OrderBy(name => name, StringComparer.Ordinal),
            requestProperties
                .Where(property => property.Name != "EqualityContract")
                .Select(property => property.Name)
                .OrderBy(name => name, StringComparer.Ordinal));
        Assert.DoesNotContain(requestProperties, property =>
            property.PropertyType == typeof(CaptureConfig) ||
            property.Name is "CommitAtUtc" or "ReservedDuration" or "OutputPath" or "Bounds" or "Backend" or "Audio" or "WindowHandle");
        Assert.True(typeof(StandingLeaseOneShotExecutionCoordinator).IsNotPublic);
        Assert.True(typeof(StandingLeaseOneShotExecutionRequest).IsNotPublic);
        Assert.True(typeof(StandingLeaseOneShotExecutionResult).IsNotPublic);
        Assert.DoesNotContain(
            typeof(StandingLeaseOneShotExecutionResult).GetProperties(
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic),
            property => property.PropertyType == typeof(CaptureConfig) ||
                typeof(ICaptureBackend).IsAssignableFrom(property.PropertyType) ||
                property.Name.Contains("Proof", StringComparison.OrdinalIgnoreCase) ||
                property.Name.Contains("Nonce", StringComparison.OrdinalIgnoreCase) ||
                property.Name.Contains("Environment", StringComparison.OrdinalIgnoreCase));

        using var database = new TemporaryDatabase();
        var backend = new CountingCaptureBackend();
        var factoryCalls = 0;
        var coordinator = new StandingLeaseOneShotExecutionCoordinator(
            database.Store,
            new FixedExecutionEnvironmentProvider(scope => EnvironmentAt(scope, At(2))),
            _ =>
            {
                Interlocked.Increment(ref factoryCalls);
                return backend;
            },
            (_, _) => Task.CompletedTask,
            () => At(2));
        var result = await coordinator.ExecuteAsync(CreateOneShotRequest(database));

        Assert.Equal(StandingLeaseOneShotExecutionStatus.Started, result.Status);
        Assert.Equal(1, factoryCalls);
        Assert.Equal(1, backend.StartCalls);
        Assert.NotNull(result.LifecycleSession);
    }

    [Fact]
    public async Task CountdownCancellationClaimsTicketWithoutCallingBackend()
    {
        using var database = new TemporaryDatabase(scopeCountdownSeconds: 3);
        var (_, _, _, ticket) = CreateStandingAuthorizationAndSpecification(database);
        var backend = new CountingCaptureBackend();
        var delayEntered = new TaskCompletionSource<TimeSpan>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cancellation = new CancellationTokenSource();
        var bridge = new StandingLeaseCaptureExecutionBridge(
            new FixedExecutionEnvironmentProvider(scope => EnvironmentAt(scope, At(2))),
            _ => backend,
            async (duration, token) =>
            {
                delayEntered.TrySetResult(duration);
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
            },
            () => At(2));

        var execution = bridge.ExecuteAsync(ticket, cancellation.Token);
        Assert.Equal(TimeSpan.FromSeconds(3), await delayEntered.Task);
        cancellation.Cancel();
        var result = await execution;
        var retry = await bridge.ExecuteAsync(ticket);

        Assert.Equal(StandingLeaseCaptureExecutionStatus.Cancelled, result.Status);
        Assert.Equal("standing_execution_cancelled", result.Reason);
        Assert.Equal(0, backend.StartCalls);
        Assert.Equal(StandingLeaseCaptureExecutionStatus.Rejected, retry.Status);
        Assert.Equal("standing_execution_already_claimed", retry.Reason);
    }

    [Fact]
    public async Task CountdownCompletionCallsBackendExactlyOnceWithApprovedIntegerDuration()
    {
        using var database = new TemporaryDatabase(scopeCountdownSeconds: 3);
        var (_, _, specification, ticket) = CreateStandingAuthorizationAndSpecification(database);
        var backend = new CountingCaptureBackend();
        var delays = new List<TimeSpan>();
        var bridge = new StandingLeaseCaptureExecutionBridge(
            new FixedExecutionEnvironmentProvider(scope => EnvironmentAt(scope, At(2))),
            _ => backend,
            (duration, _) =>
            {
                delays.Add(duration);
                return Task.CompletedTask;
            },
            () => At(2));

        var result = await bridge.ExecuteAsync(ticket);

        Assert.Equal(StandingLeaseCaptureExecutionStatus.Started, result.Status);
        Assert.Equal(new[] { TimeSpan.FromSeconds(3) }, delays);
        Assert.Equal(1, backend.StartCalls);
        Assert.Equal((int)specification.MaximumDuration.TotalSeconds, backend.Configuration!.DurationSeconds);
    }

    [Fact]
    public async Task ConcurrentExecutionHasOneClaimAndBackendStartAndStartFailureIsNotRetried()
    {
        using var database = new TemporaryDatabase();
        var (_, _, _, ticket) = CreateStandingAuthorizationAndSpecification(database);
        var backend = new CountingCaptureBackend();
        var bridge = new StandingLeaseCaptureExecutionBridge(
            new FixedExecutionEnvironmentProvider(scope => EnvironmentAt(scope, At(2))),
            _ => backend,
            (_, _) => Task.CompletedTask,
            () => At(2));

        var results = await Task.WhenAll(
            bridge.ExecuteAsync(ticket),
            bridge.ExecuteAsync(ticket));

        Assert.Equal(1, results.Count(result => result.Status == StandingLeaseCaptureExecutionStatus.Started));
        Assert.Equal(1, results.Count(result => result.Reason == "standing_execution_already_claimed"));
        Assert.Equal(1, backend.StartCalls);

        using var failedDatabase = new TemporaryDatabase();
        var (_, _, _, failedTicket) = CreateStandingAuthorizationAndSpecification(failedDatabase);
        var throwingBackend = new CountingCaptureBackend { ThrowOnStart = true };
        var failedBridge = new StandingLeaseCaptureExecutionBridge(
            new FixedExecutionEnvironmentProvider(scope => EnvironmentAt(scope, At(2))),
            _ => throwingBackend,
            (_, _) => Task.CompletedTask,
            () => At(2));

        var failed = await failedBridge.ExecuteAsync(failedTicket);
        var failedRetry = await failedBridge.ExecuteAsync(failedTicket);

        Assert.Equal(StandingLeaseCaptureExecutionStatus.Failed, failed.Status);
        Assert.Equal("standing_execution_backend_start_failed", failed.Reason);
        Assert.Equal(1, throwingBackend.StartCalls);
        Assert.Equal(1, throwingBackend.DisposeCalls);
        Assert.Equal("standing_execution_already_claimed", failedRetry.Reason);
    }

    [Fact]
    public void TicketBridgeAndResultHaveNoPublicConstructionOrSecretSurface()
    {
        Assert.Empty(typeof(StandingLeaseCaptureExecutionTicket).GetConstructors());
        Assert.Empty(typeof(StandingLeaseCaptureExecutionBridge).GetConstructors());
        Assert.Empty(typeof(StandingLeaseCaptureExecutionResult).GetConstructors());
        Assert.Empty(typeof(StandingLeaseCaptureExecutionTicket).GetProperties(
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public));
        Assert.Empty(typeof(StandingLeaseCaptureExecutionBridge).GetProperties(
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public));
        Assert.Empty(typeof(StandingLeaseCaptureExecutionResult).GetProperties(
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public));
        Assert.DoesNotContain(
            typeof(StandingLeaseCaptureExecutionTicket).GetFields(
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public),
            field => field.FieldType == typeof(CaptureAuthorizationProof) ||
                field.Name.Contains("Nonce", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(
            typeof(StandingLeaseCaptureExecutionBridge).GetMethods(
                System.Reflection.BindingFlags.Instance |
                System.Reflection.BindingFlags.Public),
            method => method.GetParameters().Any(parameter => parameter.ParameterType == typeof(CaptureConfig)));
    }

    [Fact]
    public async Task ExecutionContextIsPublishedOnlyAfterTheProtectedTransactionCommits()
    {
        using var commitReached = new ManualResetEventSlim(false);
        using var allowCommit = new ManualResetEventSlim(false);
        using var database = new TemporaryDatabase(
            beforeCommitForTest: _ =>
            {
                commitReached.Set();
                Assert.True(allowCommit.Wait(TimeSpan.FromSeconds(10)));
            });
        var proof = database.Gate.Commit(database.CreateRequest()).FirstCommitProof!;

        var authorizationTask = Task.Run(() =>
        {
            var succeeded = database.ExecutionSnapshots.TryAuthorizeAndConsumeStandingLeaseUse(
                proof,
                EnvironmentAt(database.Scope, At(2)),
                out var authorization,
                out var reason);
            return (Succeeded: succeeded, Authorization: authorization, Reason: reason);
        });

        try
        {
            Assert.True(commitReached.Wait(TimeSpan.FromSeconds(5)));
            Assert.Equal(CaptureAuthorizationProofState.Consumed, proof.State);
            Assert.False(authorizationTask.Wait(TimeSpan.FromMilliseconds(250)));
        }
        finally
        {
            allowCommit.Set();
        }

        var result = await authorizationTask;
        Assert.True(result.Succeeded, result.Reason);
        Assert.NotNull(result.Authorization);
    }

    [Fact]
    public async Task ImmediateAuthorizationTransactionBlocksAWriterAtTheLoadedSnapshotBarrier()
    {
        using var snapshotLoaded = new ManualResetEventSlim(false);
        using var allowCoreGate = new ManualResetEventSlim(false);
        using var writerStarted = new ManualResetEventSlim(false);
        using var database = new TemporaryDatabase(
            snapshotLoadedBeforeCoreGateForTest: () =>
            {
                snapshotLoaded.Set();
                Assert.True(allowCoreGate.Wait(TimeSpan.FromSeconds(10)));
            });
        var proof = database.Gate.Commit(database.CreateRequest()).FirstCommitProof!;

        var authorizationTask = Task.Run(() =>
        {
            var succeeded = database.ExecutionSnapshots.TryAuthorizeAndConsumeStandingLeaseUse(
                proof,
                EnvironmentAt(database.Scope, At(2)),
                out var authorization,
                out var reason);
            return (Succeeded: succeeded, Authorization: authorization, Reason: reason);
        });

        Task<(bool Succeeded, string Error)>? writerTask = null;
        try
        {
            Assert.True(snapshotLoaded.Wait(TimeSpan.FromSeconds(5)));
            Assert.Equal(CaptureAuthorizationProofState.Available, proof.State);

            writerTask = Task.Run(() =>
            {
                writerStarted.Set();
                try
                {
                    using var connection = database.Store.OpenConnection();
                    using var transaction = connection.BeginTransaction(
                        System.Data.IsolationLevel.Serializable,
                        deferred: false);
                    using var command = connection.CreateCommand();
                    command.Transaction = transaction;
                    command.CommandText = """
                        UPDATE recording_runs
                        SET status_code = 'recording', updated_at_utc = $updated_at_utc
                        WHERE id = $id;
                        """;
                    command.Parameters.AddWithValue("$updated_at_utc", At(3).Ticks);
                    command.Parameters.AddWithValue("$id", "run-1");
                    command.ExecuteNonQuery();
                    transaction.Commit();
                    return (Succeeded: true, Error: "");
                }
                catch (Exception exception)
                {
                    return (Succeeded: false, Error: exception.ToString());
                }
            });

            Assert.True(writerStarted.Wait(TimeSpan.FromSeconds(5)));
            Assert.False(writerTask.Wait(TimeSpan.FromMilliseconds(250)));
            allowCoreGate.Set();

            var authorization = await authorizationTask;
            Assert.True(authorization.Succeeded, authorization.Reason);
            Assert.NotNull(authorization.Authorization);

            Assert.True(writerTask.Wait(TimeSpan.FromSeconds(5)));
            var writer = await writerTask;
            Assert.True(writer.Succeeded, writer.Error);
        }
        finally
        {
            allowCoreGate.Set();
        }

        if (writerTask is not null)
        {
            Assert.True(writerTask.Wait(TimeSpan.FromSeconds(5)));
        }
    }

    [Fact]
    public void WriterThatCommitsBeforeAuthorizationIsObservedAndFailsClosed()
    {
        using var database = new TemporaryDatabase();
        var proof = database.Gate.Commit(database.CreateRequest()).FirstCommitProof!;

        RawExecute(
            database.Store.DatabasePath,
            "UPDATE recording_runs SET status_code = 'recording', updated_at_utc = $updated_at_utc WHERE id = $id;",
            ("$updated_at_utc", At(3).Ticks),
            ("$id", "run-1"));

        Assert.False(database.ExecutionSnapshots.TryAuthorizeAndConsumeStandingLeaseUse(
            proof,
            EnvironmentAt(database.Scope, At(2)),
            out var authorization,
            out var reason));
        Assert.Null(authorization);
        Assert.NotEqual("", reason);
        Assert.Equal(CaptureAuthorizationProofState.Available, proof.State);
    }

    [Fact]
    public void CommitFailureAfterProofConsumptionReturnsNoContextAndKeepsProofConsumed()
    {
        using var database = new TemporaryDatabase(
            beforeCommitForTest: connection => connection.Close());
        var proof = database.Gate.Commit(database.CreateRequest()).FirstCommitProof!;

        Assert.False(database.ExecutionSnapshots.TryAuthorizeAndConsumeStandingLeaseUse(
            proof,
            EnvironmentAt(database.Scope, At(2)),
            out var authorization,
            out var reason));

        Assert.Null(authorization);
        Assert.Equal("execution_transaction_commit_failed", reason);
        Assert.Equal(CaptureAuthorizationProofState.Consumed, proof.State);
        Assert.Equal(1L, RawScalar(database.Store.DatabasePath, "SELECT COUNT(*) FROM recording_runs;"));
        Assert.Equal(1L, RawScalar(database.Store.DatabasePath, "SELECT COUNT(*) FROM lease_uses;"));
    }

    [Fact]
    public void ProductionAuthorizationSurfaceHasNoSeparateLoadThenConsumeEntry()
    {
        Assert.DoesNotContain(
            typeof(SqliteStandingLeaseExecutionSnapshotLoader).GetMethods(
                System.Reflection.BindingFlags.Instance |
                System.Reflection.BindingFlags.NonPublic),
            method => method.Name.Contains("LoadStandingLeaseExecutionSnapshot", StringComparison.Ordinal));
        Assert.DoesNotContain(
            typeof(StandingLeaseExecutionGate).GetMethods(
                System.Reflection.BindingFlags.Static |
                System.Reflection.BindingFlags.NonPublic),
            method => method.GetParameters().Any(parameter =>
                parameter.ParameterType == typeof(StandingLeaseCaptureAuthorization)));
    }

    [Fact]
    public void StandingGateRejectsKindIdentityPlanDurationAndSessionMismatchesWithoutConsuming()
    {
        var variants = new Func<StandingLeaseUseProof, StandingLeaseUseProof>[]
        {
            proof => CloneStandingProof(proof, runId: "other-run"),
            proof => CloneStandingProof(proof, leaseId: "other-lease"),
            proof => CloneStandingProof(proof, leaseUseId: "other-use"),
            proof => CloneStandingProof(proof, scopeDigest: new string('f', 64)),
            proof => CloneStandingProof(proof, capturePlanDigest: new string('e', 64)),
            proof => CloneStandingProof(proof, currentUserSid: "S-1-5-21-other"),
            proof => CloneStandingProof(proof, sessionBinding: "other-session"),
            proof => CloneStandingProof(proof, authorizationSourceId: "standing-lease-use:other-use"),
            proof => CloneStandingProof(proof, maxDuration: TimeSpan.FromMilliseconds(5001)),
        };

        foreach (var variant in variants)
        {
            using var database = new TemporaryDatabase();
            var original = database.Gate.Commit(database.CreateRequest()).FirstCommitProof!;
            var candidate = variant(original);

            var consumed = database.ExecutionSnapshots.TryAuthorizeAndConsumeStandingLeaseUse(
                candidate,
                EnvironmentAt(database.Scope, At(2)),
                out var authorization,
                out var reason);

            Assert.False(consumed, reason);
            Assert.Null(authorization);
            Assert.NotEqual("", reason);
            Assert.Equal(CaptureAuthorizationProofState.Available, candidate.State);
        }
    }

    [Fact]
    public void StandingGateRejectsInteractiveProofBeforeAnyStandingSnapshotCheck()
    {
        using var database = new TemporaryDatabase();
        var standingProof = database.Gate.Commit(database.CreateRequest()).FirstCommitProof!;
        var interactiveProof = new InteractiveConfirmationProof(
            proofId: "interactive-for-standing-gate",
            recordingId: standingProof.RunId,
            runId: standingProof.RunId,
            authorizationSourceId: "confirmation",
            capturePlanDigest: standingProof.CapturePlanDigest,
            scopeDigest: standingProof.ScopeDigest,
            issuedAtUtc: At(1),
            expiresAtUtc: At(60),
            userSessionBinding: "S-1-5-21-1|session-1",
            maxDurationSeconds: null,
            maxFrameCount: null);

        Assert.False(database.ExecutionSnapshots.TryAuthorizeAndConsumeStandingLeaseUse(
            interactiveProof,
            EnvironmentAt(database.Scope, At(2)),
            out var authorization,
            out var reason));
        Assert.Null(authorization);
        Assert.Equal("proof_kind_not_allowed", reason);
        Assert.Equal(CaptureAuthorizationProofState.Available, interactiveProof.State);
        Assert.Equal(CaptureAuthorizationProofState.Available, standingProof.State);
    }

    [Fact]
    public void StandingProofUsesTheHalfOpenUtcIntervalAndExactMillisecondDuration()
    {
        using (var beforeIssue = new TemporaryDatabase())
        {
            var proof = beforeIssue.Gate.Commit(beforeIssue.CreateRequest()).FirstCommitProof!;
            Assert.False(beforeIssue.ExecutionSnapshots.TryAuthorizeAndConsumeStandingLeaseUse(
                proof,
                EnvironmentAt(beforeIssue.Scope, At(0)),
                out _,
                out var reason));
            Assert.Equal("proof_not_yet_valid", reason);
            Assert.Equal(CaptureAuthorizationProofState.Available, proof.State);
        }

        using (var atExpiry = new TemporaryDatabase())
        {
            var proof = atExpiry.Gate.Commit(atExpiry.CreateRequest()).FirstCommitProof!;
            Assert.False(atExpiry.ExecutionSnapshots.TryAuthorizeAndConsumeStandingLeaseUse(
                proof,
                EnvironmentAt(atExpiry.Scope, proof.ExpiresAtUtc),
                out _,
                out var reason));
            Assert.Equal("proof_expired", reason);
            Assert.Equal(CaptureAuthorizationProofState.Available, proof.State);
        }

        using var exactDuration = new TemporaryDatabase(scopeDuration: TimeSpan.FromMilliseconds(5001));
        var exactProof = exactDuration.Gate.Commit(
            exactDuration.CreateRequest(duration: TimeSpan.FromMilliseconds(5001))).FirstCommitProof!;
        Assert.True(exactDuration.ExecutionSnapshots.TryAuthorizeAndConsumeStandingLeaseUse(
            exactProof,
            EnvironmentAt(exactDuration.Scope, At(2)),
            out var authorization,
            out var reasonForSuccess), reasonForSuccess);
        Assert.Equal(5001L, exactProof.MaxDurationMilliseconds);
        Assert.Equal(TimeSpan.FromMilliseconds(5001), authorization!.ReservedDuration);
    }

    [Fact]
    public void CorruptOrMissingPersistedExecutionEvidenceFailsClosedWithoutConsumingProof()
    {
        var mutations = new Action<TemporaryDatabase>[]
        {
            database => RawExecute(database.Store.DatabasePath, "UPDATE authorized_capture_scopes SET region_x = 11 WHERE scope_id = $id;", ("$id", database.Scope.ScopeId)),
            database => RawExecute(database.Store.DatabasePath, "DELETE FROM authorized_capture_scopes WHERE scope_id = $id;", ("$id", database.Scope.ScopeId)),
            database => RawExecute(database.Store.DatabasePath, "PRAGMA foreign_keys = OFF; DELETE FROM recording_runs WHERE id = $id;", ("$id", "run-1")),
            database => RawExecute(database.Store.DatabasePath, "DELETE FROM lease_uses WHERE id = $id;", ("$id", "use-1")),
            database => RawExecute(database.Store.DatabasePath, "UPDATE recording_runs SET status_code = 'recording' WHERE id = $id;", ("$id", "run-1")),
            database => RawExecute(database.Store.DatabasePath, "UPDATE consent_leases SET valid_until_utc = $time WHERE id = $id;", ("$time", At(5).Ticks), ("$id", "lease-1")),
        };

        foreach (var mutation in mutations)
        {
            using var database = new TemporaryDatabase();
            var proof = database.Gate.Commit(database.CreateRequest()).FirstCommitProof!;
            mutation(database);

            Assert.False(database.ExecutionSnapshots.TryAuthorizeAndConsumeStandingLeaseUse(
                proof,
                EnvironmentAt(database.Scope, At(2)),
                out var authorization,
                out var reason));
            Assert.Null(authorization);
            Assert.NotEqual("", reason);
            Assert.Equal(CaptureAuthorizationProofState.Available, proof.State);
        }
    }

    [Fact]
    public void CurrentSidSessionAndInteractiveDesktopMustMatchThePersistedScope()
    {
        var environments = new[]
        {
            new StandingLeaseExecutionEnvironment(At(2), "S-1-5-21-other", "session-1", true),
            new StandingLeaseExecutionEnvironment(At(2), "S-1-5-21-1", "other-session", true),
            new StandingLeaseExecutionEnvironment(At(2), "S-1-5-21-1", "session-1", false),
        };

        foreach (var environment in environments)
        {
            using var database = new TemporaryDatabase();
            var proof = database.Gate.Commit(database.CreateRequest()).FirstCommitProof!;

            Assert.False(database.ExecutionSnapshots.TryAuthorizeAndConsumeStandingLeaseUse(
                proof,
                CompleteEnvironment(database.Scope, environment),
                out var authorization,
                out var reason));
            Assert.Null(authorization);
            Assert.Equal("execution_environment_mismatch", reason);
            Assert.Equal(CaptureAuthorizationProofState.Available, proof.State);
        }
    }

    [Fact]
    public void ProductionLoaderCapturesTheEnvironmentThroughTheInternalProviderSeam()
    {
        using var database = new TemporaryDatabase(
            environmentProviderForTest: new FixedExecutionEnvironmentProvider(
                scope => EnvironmentAt(scope, At(2))));
        var proof = database.Gate.Commit(database.CreateRequest()).FirstCommitProof!;

        Assert.True(database.ExecutionSnapshots.TryAuthorizeAndConsumeStandingLeaseUse(
            proof,
            out var authorization,
            out var reason), reason);
        Assert.NotNull(authorization);
        Assert.Equal(At(2), authorization!.ConsumedAtUtc);
    }

    [Fact]
    public void AnyDisplayMetadataOrTopologyChangeFailsClosedWithoutConsumingProof()
    {
        var mutations = new Func<AuthorizedFixedRegionScope, StandingLeaseDisplayMetadata>[]
        {
            scope => ValidDisplay(scope) with { StableDisplayFingerprint = "display-fingerprint-other" },
            scope => ValidDisplay(scope) with { PhysicalBounds = new AuthorizedPhysicalRectangle(100, -50, 1600, 900) },
            scope => ValidDisplay(scope) with { DpiX = scope.DpiX + 1 },
            scope => ValidDisplay(scope) with { DpiY = scope.DpiY + 1 },
            scope => ValidDisplay(scope) with { PhysicalWidth = scope.PhysicalWidth - 1 },
            scope => ValidDisplay(scope) with { PhysicalHeight = scope.PhysicalHeight - 1 },
            scope => ValidDisplay(scope) with { Orientation = AuthorizedDisplayOrientation.Portrait },
        };

        foreach (var mutation in mutations)
        {
            using var database = new TemporaryDatabase();
            var proof = database.Gate.Commit(database.CreateRequest()).FirstCommitProof!;
            var environment = CompleteEnvironment(
                database.Scope,
                EnvironmentAt(At(2)),
                displayOverride: mutation(database.Scope));

            Assert.False(database.ExecutionSnapshots.TryAuthorizeAndConsumeStandingLeaseUse(
                proof,
                environment,
                out var authorization,
                out var reason));
            Assert.Null(authorization);
            Assert.NotEqual("", reason);
            Assert.Equal(CaptureAuthorizationProofState.Available, proof.State);
        }

        using var topologyDatabase = new TemporaryDatabase();
        var topologyProof = topologyDatabase.Gate.Commit(topologyDatabase.CreateRequest()).FirstCommitProof!;
        var changedTopology = CompleteEnvironment(
            topologyDatabase.Scope,
            EnvironmentAt(At(2)),
            topologyDigestOverride: new string('b', 64));
        Assert.False(topologyDatabase.ExecutionSnapshots.TryAuthorizeAndConsumeStandingLeaseUse(
            topologyProof,
            changedTopology,
            out var topologyAuthorization,
            out var topologyReason));
        Assert.Null(topologyAuthorization);
        Assert.Equal("execution_topology_mismatch", topologyReason);
        Assert.Equal(CaptureAuthorizationProofState.Available, topologyProof.State);
    }

    [Fact]
    public void MissingUnresolvedAndIncompleteDisplayMetadataFailClosed()
    {
        var variants = new Func<TemporaryDatabase, StandingLeaseExecutionEnvironment>[]
        {
            database =>
            {
                var valid = CompleteEnvironment(database.Scope, EnvironmentAt(At(2)));
                return new StandingLeaseExecutionEnvironment(
                    valid.NowUtc, valid.CurrentUserSid, valid.SessionBinding, valid.IsInteractiveDesktop,
                    Array.Empty<StandingLeaseDisplayMetadata>(), valid.TopologyDigest, valid.OutputFileSystem);
            },
            database =>
            {
                var valid = CompleteEnvironment(database.Scope, EnvironmentAt(At(2)));
                return new StandingLeaseExecutionEnvironment(
                    valid.NowUtc, valid.CurrentUserSid, valid.SessionBinding, valid.IsInteractiveDesktop,
                    new StandingLeaseDisplayMetadata[] { valid.Displays.Single() with { StableDisplayFingerprint = null, IdentityStatus = DisplayIdentityResolutionStatus.Unresolved } },
                    valid.TopologyDigest, valid.OutputFileSystem);
            },
            database =>
            {
                var valid = CompleteEnvironment(database.Scope, EnvironmentAt(At(2)));
                return new StandingLeaseExecutionEnvironment(
                    valid.NowUtc, valid.CurrentUserSid, valid.SessionBinding, valid.IsInteractiveDesktop,
                    new StandingLeaseDisplayMetadata[] { valid.Displays.Single() with { DpiX = null } },
                    valid.TopologyDigest, valid.OutputFileSystem);
            },
        };

        foreach (var environmentFactory in variants)
        {
            using var database = new TemporaryDatabase();
            var environment = environmentFactory(database);
            var proof = database.Gate.Commit(database.CreateRequest()).FirstCommitProof!;
            Assert.False(database.ExecutionSnapshots.TryAuthorizeAndConsumeStandingLeaseUse(
                proof,
                environment,
                out var authorization,
                out var reason));
            Assert.Null(authorization);
            Assert.NotEqual("", reason);
            Assert.Equal(CaptureAuthorizationProofState.Available, proof.State);
        }
    }

    [Fact]
    public void FrozenRegionAndOutputFilesystemAreNeverSilentlyRewritten()
    {
        var variants = new Func<TemporaryDatabase, (StandingLeaseExecutionEnvironment Environment, string Reason)>[]
        {
            database =>
            {
                var valid = CompleteEnvironment(database.Scope, EnvironmentAt(At(2)));
                var output = valid.OutputFileSystem! with { NormalizedOutputDirectory = Path.Combine(database.RootPath, "other") + Path.DirectorySeparatorChar };
                return (WithOutput(valid, output), "execution_output_directory_mismatch");
            },
            database =>
            {
                var valid = CompleteEnvironment(database.Scope, EnvironmentAt(At(2)));
                var output = valid.OutputFileSystem! with { FrozenOutputFilePath = Path.Combine(database.RootPath, "other", "other.mp4") };
                return (WithOutput(valid, output), "execution_output_file_path_mismatch");
            },
            database =>
            {
                var valid = CompleteEnvironment(database.Scope, EnvironmentAt(At(2)));
                return (WithOutput(valid, valid.OutputFileSystem! with { DirectoryExists = false }), "execution_output_directory_unavailable");
            },
            database =>
            {
                var valid = CompleteEnvironment(database.Scope, EnvironmentAt(At(2)));
                return (WithOutput(valid, valid.OutputFileSystem! with { DirectoryWritable = false }), "execution_output_directory_unwritable");
            },
            database =>
            {
                var valid = CompleteEnvironment(database.Scope, EnvironmentAt(At(2)));
                return (WithOutput(valid, valid.OutputFileSystem! with { FrozenFileExists = true }), "execution_output_file_exists");
            },
            database =>
            {
                var valid = CompleteEnvironment(database.Scope, EnvironmentAt(At(2)));
                return (WithOutput(valid, valid.OutputFileSystem! with { FreeSpaceAvailable = false }), "execution_disk_space_unavailable");
            },
            database =>
            {
                var valid = CompleteEnvironment(database.Scope, EnvironmentAt(At(2)));
                var output = valid.OutputFileSystem! with { AvailableFreeBytes = valid.OutputFileSystem!.RequiredFreeBytes - 1 };
                return (WithOutput(valid, output), "execution_disk_space_insufficient");
            },
        };

        foreach (var variant in variants)
        {
            using var database = new TemporaryDatabase();
            var (environment, expectedReason) = variant(database);
            var proof = database.Gate.Commit(database.CreateRequest()).FirstCommitProof!;
            Assert.False(database.ExecutionSnapshots.TryAuthorizeAndConsumeStandingLeaseUse(
                proof,
                environment,
                out var authorization,
                out var reason));
            Assert.Null(authorization);
            Assert.Equal(expectedReason, reason);
            Assert.Equal(CaptureAuthorizationProofState.Available, proof.State);
        }
    }

    [Fact]
    public void EnvironmentSnapshotAndProviderHaveNoPublicConstructionSurface()
    {
        Assert.Empty(typeof(StandingLeaseExecutionEnvironment).GetConstructors());
        Assert.Empty(typeof(StandingLeaseDisplayMetadata).GetConstructors());
        Assert.Empty(typeof(StandingLeaseOutputFileSystemSnapshot).GetConstructors());
        Assert.Empty(typeof(SystemQueryStandingLeaseExecutionEnvironmentProvider).GetConstructors());
        Assert.DoesNotContain(
            typeof(StandingLeaseExecutionEnvironment).GetProperties(
                System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance),
            property => property.Name.Contains("nonce", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task ConcurrentStandingConsumptionHasAtMostOneSuccessAndOneContext()
    {
        using var database = new TemporaryDatabase();
        var proof = database.Gate.Commit(database.CreateRequest()).FirstCommitProof!;

        var attempts = Enumerable.Range(0, 64)
            .Select(_ => Task.Run(() =>
            {
                var succeeded = database.ExecutionSnapshots.TryAuthorizeAndConsumeStandingLeaseUse(
                    proof,
                    EnvironmentAt(database.Scope, At(2)),
                    out var authorization,
                    out var reason);
                return (Succeeded: succeeded, Authorization: authorization, Reason: reason);
            }))
            .ToArray();
        var results = await Task.WhenAll(attempts);

        Assert.Equal(1, results.Count(item => item.Succeeded));
        Assert.Equal(1, results.Count(item => item.Authorization is not null));
        Assert.Equal(63, results.Count(item => !item.Succeeded && item.Reason == "proof_already_consumed"));
        Assert.True(results.Single(item => item.Succeeded).Authorization!.IsProofConsumed);
    }

    [Fact]
    public void LoaderAndExecutionContextHaveNoPublicConstructionOrSerializationSurface()
    {
        Assert.Empty(typeof(StandingLeaseExecutionSnapshot).GetConstructors());
        Assert.Empty(typeof(SqliteStandingLeaseExecutionSnapshotLoader).GetConstructors());
        Assert.Empty(typeof(StandingLeaseCaptureAuthorization).GetConstructors());
        Assert.DoesNotContain(
            typeof(StandingLeaseCaptureAuthorization).GetProperties(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance),
            property => property.Name.Contains("nonce", StringComparison.OrdinalIgnoreCase));

        using var database = new TemporaryDatabase();
        var proof = database.Gate.Commit(database.CreateRequest()).FirstCommitProof!;
        var result = database.Gate.Commit(database.CreateRequest());
        Assert.Null(result.FirstCommitProof);
        Assert.DoesNotContain(proof.OneTimeNonce, JsonSerializer.Serialize(result), StringComparison.Ordinal);
    }

    [Fact]
    public void MissingScopeWithoutEvidenceFailsClosedWithoutWritingStartGateEvidence()
    {
        using var database = new TemporaryDatabase();
        RawExecute(database.Store.DatabasePath, "DELETE FROM authorized_capture_scopes WHERE scope_id = $id;", ("$id", database.Scope.ScopeId));

        var exception = Assert.Throws<Phase3PersistenceException>(() => database.Gate.Commit(database.CreateRequest()));

        Assert.Equal("scope_not_found", exception.Code);
        AssertNoStartGateRows(database);
    }

    [Fact]
    public void InvalidScopeRequestFieldsAreRejectedBeforeOpeningTheDatabaseTransaction()
    {
        foreach (var requestFactory in new Func<TemporaryDatabase, Phase3StartGateRequest>[]
        {
            database => database.CreateRequest() with { ScopeId = " scope-1" },
            database => database.CreateRequest() with { ScopeId = "" },
            database => database.CreateRequest() with { ScopeDigest = new string('A', 64) },
            database => database.CreateRequest() with { ScopeDigest = new string('f', 63) },
            database => database.CreateRequest() with { ScopeDigest = new string('g', 64) },
        })
        {
            using var database = new TemporaryDatabase();

            var exception = Assert.Throws<Phase3PersistenceException>(() => database.Gate.Commit(requestFactory(database)));

            Assert.Equal("invalid_argument", exception.Code);
            AssertNoStartGateRows(database);
        }
    }

    [Fact]
    public void ScopeBindingMismatchesFailBeforeAnyStartGateWrite()
    {
        var requestFactories = new Func<TemporaryDatabase, Phase3StartGateRequest>[]
        {
            database => database.CreateRequest() with { ScopeId = "scope-other" },
            database => database.CreateRequest() with { ScopeDigest = new string('f', 64) },
            database => database.CreateRequest() with { PlanId = "other-plan" },
            database => database.CreateRequest() with { OccurrenceId = "other-occurrence" },
            database => database.CreateRequest() with { LeaseId = "other-lease" },
            database => database.CreateRequest(duration: TimeSpan.FromSeconds(6)),
        };

        foreach (var requestFactory in requestFactories)
        {
            using var database = new TemporaryDatabase();

            var exception = Assert.Throws<Phase3PersistenceException>(() => database.Gate.Commit(requestFactory(database)));

            Assert.Contains(exception.Code, new[] { "start_gate_conflict", "duration_out_of_scope" });
            AssertNoStartGateRows(database);
        }
    }

    [Fact]
    public void ScopeDigestAndPersistedScopeFieldsAreValidatedByTheStartGate()
    {
        using (var fieldCorruption = new TemporaryDatabase())
        {
            RawExecute(fieldCorruption.Store.DatabasePath,
                "UPDATE authorized_capture_scopes SET region_x = 11 WHERE scope_id = $id;",
                ("$id", fieldCorruption.Scope.ScopeId));

            var exception = Assert.Throws<Phase3PersistenceException>(() => fieldCorruption.Gate.Commit(fieldCorruption.CreateRequest()));

            Assert.Equal("persisted_snapshot_invalid", exception.Code);
            AssertNoStartGateRows(fieldCorruption);
        }

        using (var forgedDigest = new TemporaryDatabase())
        {
            RawExecute(forgedDigest.Store.DatabasePath,
                "UPDATE authorized_capture_scopes SET region_x = 12, scope_digest = $digest WHERE scope_id = $id;",
                ("$digest", new string('f', 64)), ("$id", forgedDigest.Scope.ScopeId));

            var exception = Assert.Throws<Phase3PersistenceException>(() => forgedDigest.Gate.Commit(forgedDigest.CreateRequest()));

            Assert.Equal("persisted_snapshot_invalid", exception.Code);
            AssertNoStartGateRows(forgedDigest);
        }
    }

    [Fact]
    public void ScopeGeometryDisplayPathPolicyAndDurationCorruptionFailClosed()
    {
        var mutations = new Action<TemporaryDatabase>[]
        {
            database => RawExecute(database.Store.DatabasePath, "UPDATE authorized_capture_scopes SET region_width = 641 WHERE scope_id = $id;", ("$id", database.Scope.ScopeId)),
            database => RawExecute(database.Store.DatabasePath, "PRAGMA ignore_check_constraints = ON; UPDATE authorized_capture_scopes SET display_identity_status_code = 'unresolved' WHERE scope_id = $id;", ("$id", database.Scope.ScopeId)),
            database => RawExecute(database.Store.DatabasePath, "UPDATE authorized_capture_scopes SET output_directory = $path WHERE scope_id = $id;", ("$path", Path.Combine(database.RootPath, "different-output")), ("$id", database.Scope.ScopeId)),
            database => RawExecute(database.Store.DatabasePath, "PRAGMA ignore_check_constraints = ON; UPDATE authorized_capture_scopes SET backend_code = 'wgc' WHERE scope_id = $id;", ("$id", database.Scope.ScopeId)),
            database => RawExecute(database.Store.DatabasePath, "UPDATE authorized_capture_scopes SET reserved_duration_ms = 4000 WHERE scope_id = $id;", ("$id", database.Scope.ScopeId)),
            database => RawExecute(database.Store.DatabasePath, "PRAGMA foreign_keys = OFF; UPDATE authorized_capture_scopes SET lease_id = 'missing-lease' WHERE scope_id = $id;", ("$id", database.Scope.ScopeId)),
        };

        foreach (var mutation in mutations)
        {
            using var database = new TemporaryDatabase();
            mutation(database);

            var exception = Assert.Throws<Phase3PersistenceException>(() => database.Gate.Commit(database.CreateRequest()));

            Assert.Equal("persisted_snapshot_invalid", exception.Code);
            AssertNoStartGateRows(database);
        }
    }

    [Fact]
    public void PersistedEvidenceIsValidatedBeforeAnIdempotentScopeRequestConflict()
    {
        using var database = new TemporaryDatabase();
        var request = database.CreateRequest();
        database.Gate.Commit(request);
        RawExecute(database.Store.DatabasePath,
            "UPDATE recording_runs SET created_at_utc = $time, updated_at_utc = $time WHERE id = $id;",
            ("$time", At(2).Ticks), ("$id", request.RunId));

        var exception = Assert.Throws<Phase3PersistenceException>(() => database.Gate.Commit(request with { ScopeDigest = new string('f', 64) }));

        Assert.Equal("persisted_snapshot_invalid", exception.Code);
    }

    [Fact]
    public void MissingScopeAfterACommitInvalidatesIdempotentRecoveryInsteadOfReturningAlreadyCommitted()
    {
        using var database = new TemporaryDatabase();
        var request = database.CreateRequest();
        database.Gate.Commit(request);
        RawExecute(database.Store.DatabasePath, "DELETE FROM authorized_capture_scopes WHERE scope_id = $id;", ("$id", database.Scope.ScopeId));

        var exception = Assert.Throws<Phase3PersistenceException>(() => database.Gate.Commit(request));

        Assert.Equal("persisted_snapshot_invalid", exception.Code);
        Assert.Equal(1L, RawScalar(database.Store.DatabasePath, "SELECT COUNT(*) FROM recording_runs;"));
        Assert.Equal(1L, RawScalar(database.Store.DatabasePath, "SELECT COUNT(*) FROM lease_uses;"));
    }

    [Fact]
    public async Task StandingLeaseLifecycleCapturesSynchronousFirstFrameBeforeStartReturns()
    {
        using var database = new TemporaryDatabase();
        var backend = new LifecycleCaptureBackend { EmitFirstFrameOnStart = true };
        var coordinator = CreateLifecycleCoordinator(database, backend);

        var result = await coordinator.ExecuteAsync(CreateOneShotRequest(database));

        Assert.Equal(StandingLeaseOneShotExecutionStatus.Started, result.Status);
        Assert.NotNull(result.LifecycleSession);
        Assert.Equal(1, backend.StartCalls);
        Assert.Equal(RecordingRunStatus.Recording, database.Runs.Get("run-1").Status);
        Assert.Equal(LeaseUseStatus.Consumed, database.Uses.Get("use-1").Status);
        Assert.Equal(PlanOccurrenceStatus.RunCreated, database.Occurrences.Get("occ-1").Status);
        Assert.Equal(3L, database.Runs.Get("run-1").Version);
        Assert.Equal(3L, database.Uses.Get("use-1").Version);
        Assert.Equal(1L, database.Occurrences.Get("occ-1").Version);

        result.LifecycleSession!.Dispose();
        Assert.Equal(1, backend.DisposeCalls);
    }

    [Fact]
    public async Task StandingLeaseLifecycleCapturesSynchronousExitWithoutFirstFrameAsUnknown()
    {
        using var database = new TemporaryDatabase();
        var backend = new LifecycleCaptureBackend { EmitNaturalExitOnStart = true };
        var coordinator = CreateLifecycleCoordinator(database, backend);

        var result = await coordinator.ExecuteAsync(CreateOneShotRequest(database));

        Assert.Equal(StandingLeaseOneShotExecutionStatus.Started, result.Status);
        Assert.Equal(1, backend.StartCalls);
        Assert.Equal(1, backend.DisposeCalls);
        Assert.Equal(RecordingRunStatus.StartedUnknown, database.Runs.Get("run-1").Status);
        Assert.Equal("natural_exit_before_first_frame", database.Runs.Get("run-1").TerminalReasonCode);
        Assert.Equal(LeaseUseStatus.StartedUnknown, database.Uses.Get("use-1").Status);
        Assert.Null(database.Uses.Get("use-1").ActualSettledDuration);
        Assert.Equal(PlanOccurrenceStatus.Blocked, database.Occurrences.Get("occ-1").Status);
        Assert.Equal("natural_exit_before_first_frame", database.Occurrences.Get("occ-1").TerminalReasonCode);
        Assert.NotEqual(PlanOccurrenceStatus.Completed, database.Occurrences.Get("occ-1").Status);
    }

    [Fact]
    public async Task StandingLeaseLifecycleStartFailureAfterAFrameCannotLeaveRecording()
    {
        using var database = new TemporaryDatabase();
        var backend = new LifecycleCaptureBackend
        {
            EmitFirstFrameOnStart = true,
            ThrowAfterFirstFrameOnStart = true,
        };
        var coordinator = CreateLifecycleCoordinator(database, backend);

        var result = await coordinator.ExecuteAsync(CreateOneShotRequest(database));

        Assert.Equal(StandingLeaseOneShotExecutionStatus.CommittedNotStarted, result.Status);
        Assert.Equal("standing_execution_backend_start_failed", result.Reason);
        Assert.Equal(1, backend.StartCalls);
        Assert.Equal(1, backend.DisposeCalls);
        Assert.Equal(RecordingRunStatus.Failed, database.Runs.Get("run-1").Status);
        Assert.Equal("backend_start_failed_after_first_frame", database.Runs.Get("run-1").TerminalReasonCode);
        Assert.Equal(LeaseUseStatus.StartedUnknown, database.Uses.Get("use-1").Status);
        Assert.Equal(PlanOccurrenceStatus.Blocked, database.Occurrences.Get("occ-1").Status);
    }

    [Fact]
    public async Task StandingLeaseLifecycleSettlesAnAsynchronousFirstFrameAndNaturalExitExactlyOnce()
    {
        using var database = new TemporaryDatabase();
        var backend = new LifecycleCaptureBackend();
        var coordinator = CreateLifecycleCoordinator(database, backend);

        var result = await coordinator.ExecuteAsync(CreateOneShotRequest(database));
        backend.RaiseFirstFrame();
        backend.RaiseCaptureEnded();
        Assert.Equal(RecordingRunStatus.Finalizing, database.Runs.Get("run-1").Status);
        Assert.Equal(LeaseUseStatus.Consumed, database.Uses.Get("use-1").Status);
        backend.RaiseNaturalExit(0, ValidOutputMeta(1.5));
        backend.RaiseNaturalExit(0, ValidOutputMeta(1.5));
        backend.RaiseFirstFrame();

        var run = database.Runs.Get("run-1");
        var use = database.Uses.Get("use-1");
        var occurrence = database.Occurrences.Get("occ-1");
        Assert.Equal(RecordingRunStatus.Settled, run.Status);
        Assert.Equal(LeaseUseStatus.Settled, use.Status);
        Assert.Equal(TimeSpan.FromMilliseconds(1500), use.ActualSettledDuration);
        Assert.Equal(PlanOccurrenceStatus.Completed, occurrence.Status);
        Assert.Null(run.TerminalReasonCode);
        Assert.Equal(6L, run.Version);
        Assert.Equal(4L, use.Version);
        Assert.Equal(2L, occurrence.Version);
        Assert.Equal(1, backend.StartCalls);
        Assert.Equal(0, backend.StopCalls);
        Assert.Equal(1, backend.DisposeCalls);
        Assert.NotNull(result.LifecycleSession);
    }

    [Fact]
    public async Task StandingLeaseLifecycleConcurrentSessionsUseDatabaseVersionsForAtMostOnceSettlement()
    {
        using var database = new TemporaryDatabase();
        var (_, _, specification, _) = CreateStandingAuthorizationAndSpecification(database);
        var firstBackend = new LifecycleCaptureBackend();
        var secondBackend = new LifecycleCaptureBackend();
        using var first = new StandingLeaseOneShotExecutionSession(database.Store, specification, firstBackend, () => At(2));
        using var second = new StandingLeaseOneShotExecutionSession(database.Store, specification, secondBackend, () => At(2));
        var firstSession = (IStandingLeaseCaptureLifecycleSession)first;
        var secondSession = (IStandingLeaseCaptureLifecycleSession)second;
        Assert.True(firstSession.TryAttach(firstBackend, out var firstAttachFailure), firstAttachFailure);
        Assert.True(secondSession.TryAttach(secondBackend, out var secondAttachFailure), secondAttachFailure);

        await Task.WhenAll(
            Task.Run(firstBackend.RaiseFirstFrame),
            Task.Run(secondBackend.RaiseFirstFrame));
        await Task.WhenAll(
            Task.Run(() => firstBackend.RaiseNaturalExit(0, ValidOutputMeta(1.25))),
            Task.Run(() => secondBackend.RaiseNaturalExit(0, ValidOutputMeta(1.25))));

        Assert.Equal(RecordingRunStatus.Settled, database.Runs.Get("run-1").Status);
        Assert.Equal(LeaseUseStatus.Settled, database.Uses.Get("use-1").Status);
        Assert.Equal(TimeSpan.FromMilliseconds(1250), database.Uses.Get("use-1").ActualSettledDuration);
        Assert.Equal(PlanOccurrenceStatus.Completed, database.Occurrences.Get("occ-1").Status);
        Assert.Equal(6L, database.Runs.Get("run-1").Version);
        Assert.Equal(4L, database.Uses.Get("use-1").Version);
        Assert.Equal(2L, database.Occurrences.Get("occ-1").Version);
        Assert.Equal(1, firstBackend.DisposeCalls);
        Assert.Equal(1, secondBackend.DisposeCalls);
    }

    [Theory]
    [InlineData(-1.0)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(6.0)]
    public async Task StandingLeaseLifecycleRejectsInvalidOrOverlongOutputDuration(double durationSeconds)
    {
        using var database = new TemporaryDatabase();
        var backend = new LifecycleCaptureBackend();
        var coordinator = CreateLifecycleCoordinator(database, backend);

        await coordinator.ExecuteAsync(CreateOneShotRequest(database));
        backend.RaiseFirstFrame();
        backend.RaiseNaturalExit(0, ValidOutputMeta(durationSeconds));

        Assert.Equal(RecordingRunStatus.Failed, database.Runs.Get("run-1").Status);
        Assert.Equal(LeaseUseStatus.StartedUnknown, database.Uses.Get("use-1").Status);
        Assert.Null(database.Uses.Get("use-1").ActualSettledDuration);
        Assert.Equal(PlanOccurrenceStatus.Blocked, database.Occurrences.Get("occ-1").Status);
        Assert.NotEqual(PlanOccurrenceStatus.Completed, database.Occurrences.Get("occ-1").Status);
        Assert.Equal(1, backend.DisposeCalls);
    }

    [Fact]
    public async Task StandingLeaseLifecycleRejectsNonzeroExitAndKeepsOccurrenceBlocked()
    {
        using var database = new TemporaryDatabase();
        var backend = new LifecycleCaptureBackend();
        var coordinator = CreateLifecycleCoordinator(database, backend);

        await coordinator.ExecuteAsync(CreateOneShotRequest(database));
        backend.RaiseFirstFrame();
        backend.RaiseNaturalExit(7, ValidOutputMeta(1));

        Assert.Equal(RecordingRunStatus.Failed, database.Runs.Get("run-1").Status);
        Assert.Equal("capture_exit_nonzero", database.Runs.Get("run-1").TerminalReasonCode);
        Assert.Equal(LeaseUseStatus.StartedUnknown, database.Uses.Get("use-1").Status);
        Assert.Equal(PlanOccurrenceStatus.Blocked, database.Occurrences.Get("occ-1").Status);
        Assert.Equal("capture_exit_nonzero", database.Occurrences.Get("occ-1").TerminalReasonCode);
    }

    [Fact]
    public async Task StandingLeaseLifecycleRejectsMissingFinalArtifactAfterCaptureEnded()
    {
        using var database = new TemporaryDatabase();
        var backend = new LifecycleCaptureBackend();
        var coordinator = CreateLifecycleCoordinator(database, backend);

        await coordinator.ExecuteAsync(CreateOneShotRequest(database));
        backend.RaiseFirstFrame();
        backend.RaiseCaptureEnded();
        backend.RaiseNaturalExit(0, new OutputMeta
        {
            OutputPath = Path.Combine(database.RootPath, "output", "capture.mp4"),
            OutputFileExists = false,
            SizeBytes = 4096,
            DurationSeconds = 1,
        });

        Assert.Equal(RecordingRunStatus.Failed, database.Runs.Get("run-1").Status);
        Assert.Equal(LeaseUseStatus.StartedUnknown, database.Uses.Get("use-1").Status);
        Assert.Equal(PlanOccurrenceStatus.Blocked, database.Occurrences.Get("occ-1").Status);
        Assert.NotEqual(PlanOccurrenceStatus.Completed, database.Occurrences.Get("occ-1").Status);
        Assert.Equal(1, backend.DisposeCalls);
    }

    [Fact]
    public async Task StandingLeaseLifecycleUserStopUsesOneStopAndTheSameSettlementPath()
    {
        using var database = new TemporaryDatabase();
        var backend = new LifecycleCaptureBackend { StopResult = ValidOutputMeta(2) };
        var coordinator = CreateLifecycleCoordinator(database, backend);
        var result = await coordinator.ExecuteAsync(CreateOneShotRequest(database));
        backend.RaiseFirstFrame();

        var firstStop = result.LifecycleSession!.Stop();
        var secondStop = result.LifecycleSession.Stop();

        Assert.True(firstStop.Succeeded);
        Assert.True(firstStop.Terminal);
        Assert.False(secondStop.Changed);
        Assert.Equal(1, backend.StopCalls);
        Assert.Equal(1, backend.DisposeCalls);
        Assert.Equal(RecordingRunStatus.Settled, database.Runs.Get("run-1").Status);
        Assert.Equal(LeaseUseStatus.Settled, database.Uses.Get("use-1").Status);
        Assert.Equal(TimeSpan.FromSeconds(2), database.Uses.Get("use-1").ActualSettledDuration);
        Assert.Equal(PlanOccurrenceStatus.Completed, database.Occurrences.Get("occ-1").Status);
    }

    [Fact]
    public async Task StandingLeaseLifecycleInvalidUserStopBecomesSessionInterrupted()
    {
        using var database = new TemporaryDatabase();
        var backend = new LifecycleCaptureBackend { StopResult = new OutputMeta() };
        var coordinator = CreateLifecycleCoordinator(database, backend);
        var result = await coordinator.ExecuteAsync(CreateOneShotRequest(database));
        backend.RaiseFirstFrame();

        var stop = result.LifecycleSession!.Stop();

        Assert.True(stop.Succeeded);
        Assert.Equal(RecordingRunStatus.SessionInterrupted, database.Runs.Get("run-1").Status);
        Assert.Equal("user_stop_output_invalid", database.Runs.Get("run-1").TerminalReasonCode);
        Assert.Equal(LeaseUseStatus.StartedUnknown, database.Uses.Get("use-1").Status);
        Assert.Equal(PlanOccurrenceStatus.Blocked, database.Occurrences.Get("occ-1").Status);
        Assert.Equal(1, backend.StopCalls);
        Assert.Equal(1, backend.DisposeCalls);
    }

    [Fact]
    public async Task StandingLeaseLifecycleStopBeforeFirstFrameIsConservativeAndIdempotent()
    {
        using var database = new TemporaryDatabase();
        var backend = new LifecycleCaptureBackend { StopResult = ValidOutputMeta(1) };
        var coordinator = CreateLifecycleCoordinator(database, backend);
        var result = await coordinator.ExecuteAsync(CreateOneShotRequest(database));

        var firstStop = result.LifecycleSession!.Stop();
        var secondStop = result.LifecycleSession.Stop();

        Assert.True(firstStop.Succeeded);
        Assert.False(secondStop.Changed);
        Assert.Equal(1, backend.StopCalls);
        Assert.Equal(1, backend.DisposeCalls);
        Assert.Equal(RecordingRunStatus.StartedUnknown, database.Runs.Get("run-1").Status);
        Assert.Equal(LeaseUseStatus.StartedUnknown, database.Uses.Get("use-1").Status);
        Assert.Equal(PlanOccurrenceStatus.Blocked, database.Occurrences.Get("occ-1").Status);
    }

    [Fact]
    public void StandingLeaseLifecycleFailsClosedWhenCallbackClockIsUnavailableOrNonUtc()
    {
        foreach (Func<DateTimeOffset?> clock in new Func<DateTimeOffset?>[]
        {
            () => null,
            () => new DateTimeOffset(2035, 1, 1, 0, 0, 2, TimeSpan.FromHours(8)),
        })
        {
            using var database = new TemporaryDatabase();
            var (_, _, specification, _) = CreateStandingAuthorizationAndSpecification(database);
            var backend = new LifecycleCaptureBackend();
            using var session = new StandingLeaseOneShotExecutionSession(database.Store, specification, backend, clock);
            var lifecycleSession = (IStandingLeaseCaptureLifecycleSession)session;
            Assert.True(lifecycleSession.TryAttach(backend, out var attachFailure), attachFailure);

            backend.RaiseFirstFrame();

            Assert.Equal(RecordingRunStatus.StartCommitted, database.Runs.Get("run-1").Status);
            Assert.Equal(LeaseUseStatus.StartCommitted, database.Uses.Get("use-1").Status);
            Assert.Equal(PlanOccurrenceStatus.RunCreated, database.Occurrences.Get("occ-1").Status);
        }
    }

    [Fact]
    public void StandingLeaseLifecycleFailsClosedAndReconcilesWhenCommitFails()
    {
        using var database = new TemporaryDatabase();
        var (_, _, specification, _) = CreateStandingAuthorizationAndSpecification(database);
        var backend = new LifecycleCaptureBackend();
        using var session = new StandingLeaseOneShotExecutionSession(
            database.Store,
            specification,
            backend,
            () => At(2),
            (_, _) => throw new InvalidOperationException("injected lifecycle commit failure"));
        var lifecycleSession = (IStandingLeaseCaptureLifecycleSession)session;
        Assert.True(lifecycleSession.TryAttach(backend, out var attachFailure), attachFailure);

        backend.RaiseFirstFrame();

        Assert.Equal(RecordingRunStatus.StartedUnknown, database.Runs.Get("run-1").Status);
        Assert.Equal(LeaseUseStatus.StartedUnknown, database.Uses.Get("use-1").Status);
        Assert.Equal(PlanOccurrenceStatus.Blocked, database.Occurrences.Get("occ-1").Status);
        Assert.Equal(3L, database.Runs.Get("run-1").Version);
        Assert.Equal(3L, database.Uses.Get("use-1").Version);
        Assert.Equal(2L, database.Occurrences.Get("occ-1").Version);
    }

    [Fact]
    public void StandingLeaseWindowEligibilityReturnsBeforeWindowWithoutWriting()
    {
        using var database = new TemporaryDatabase();
        RawExecute(
            database.Store.DatabasePath,
            "UPDATE plan_occurrences SET window_start_utc = $window_start_utc WHERE id = $id;",
            ("$window_start_utc", At(10).UtcDateTime.Ticks),
            ("$id", "occ-1"));
        var before = ReadWindowState(database);

        var result = new StandingLeaseWindowEligibilityService(database.Store, () => At(5))
            .Evaluate(CreateWindowEligibilityRequest(database));

        Assert.Equal(StandingLeaseWindowEligibilityStatus.BeforeWindow, result.Status);
        Assert.Equal("before_occurrence_window", result.Reason);
        Assert.False(result.Changed);
        Assert.Equal(before, ReadWindowState(database));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(10)]
    [InlineData(55)]
    public void StandingLeaseWindowEligibilityReturnsEligibleAtStartAndExactCompletionBoundary(int nowSeconds)
    {
        using var database = new TemporaryDatabase();
        var before = ReadWindowState(database);

        var result = new StandingLeaseWindowEligibilityService(database.Store, () => At(nowSeconds))
            .Evaluate(CreateWindowEligibilityRequest(database));

        Assert.Equal(StandingLeaseWindowEligibilityStatus.Eligible, result.Status);
        Assert.Equal("execution_window_eligible", result.Reason);
        Assert.False(result.Changed);
        Assert.Equal(before, ReadWindowState(database));
    }

    [Fact]
    public void StandingLeaseWindowEligibilityExpiresAtOccurrenceEndWithNoExecutionClaims()
    {
        using var database = new TemporaryDatabase();

        var result = new StandingLeaseWindowEligibilityService(database.Store, () => At(60))
            .Evaluate(CreateWindowEligibilityRequest(database));

        Assert.Equal(StandingLeaseWindowEligibilityStatus.Expired, result.Status);
        Assert.Equal("occurrence_window_expired", result.Reason);
        Assert.True(result.Changed);
        AssertWindowState(
            database,
            PlanOccurrenceStatus.Expired,
            "occurrence_window_expired",
            runId: null,
            occurrenceVersion: 1,
            runCount: 0,
            useCount: 0);
    }

    [Fact]
    public void StandingLeaseWindowEligibilityExpiresWhenDurationNoLongerFitsOccurrence()
    {
        using var database = new TemporaryDatabase(occurrenceEndSeconds: 4);

        var result = new StandingLeaseWindowEligibilityService(database.Store, () => At(0))
            .Evaluate(CreateWindowEligibilityRequest(database));

        Assert.Equal(StandingLeaseWindowEligibilityStatus.Expired, result.Status);
        Assert.Equal("capture_duration_no_longer_fits", result.Reason);
        AssertWindowState(
            database,
            PlanOccurrenceStatus.Expired,
            "capture_duration_no_longer_fits",
            runId: null,
            occurrenceVersion: 1,
            runCount: 0,
            useCount: 0);
    }

    [Fact]
    public void StandingLeaseWindowEligibilityExpiresWhenLeaseEndNoLongerFits()
    {
        using var database = new TemporaryDatabase();
        RawExecute(
            database.Store.DatabasePath,
            "UPDATE consent_leases SET valid_until_utc = $valid_until_utc WHERE id = $id;",
            ("$valid_until_utc", At(4).UtcDateTime.Ticks),
            ("$id", "lease-1"));

        var result = new StandingLeaseWindowEligibilityService(database.Store, () => At(0))
            .Evaluate(CreateWindowEligibilityRequest(database));

        Assert.Equal(StandingLeaseWindowEligibilityStatus.Expired, result.Status);
        Assert.Equal("lease_window_expired", result.Reason);
        AssertWindowState(
            database,
            PlanOccurrenceStatus.Expired,
            "lease_window_expired",
            runId: null,
            occurrenceVersion: 1,
            runCount: 0,
            useCount: 0);
    }

    [Fact]
    public void StandingLeaseWindowEligibilityFailsClosedForDurableStateBoundariesWithoutWriting()
    {
        using var disabledPlanDatabase = new TemporaryDatabase(planStatus: PlanDefinitionStatus.Paused);
        var disabledPlanBefore = ReadWindowState(disabledPlanDatabase);
        var disabledPlanResult = new StandingLeaseWindowEligibilityService(disabledPlanDatabase.Store, () => At(0))
            .Evaluate(CreateWindowEligibilityRequest(disabledPlanDatabase));
        Assert.Equal(StandingLeaseWindowEligibilityStatus.Rejected, disabledPlanResult.Status);
        Assert.Equal("plan_not_enabled", disabledPlanResult.Reason);
        Assert.Equal(disabledPlanBefore, ReadWindowState(disabledPlanDatabase));

        using var unauthorizedOccurrenceDatabase = new TemporaryDatabase(occurrenceStatus: PlanOccurrenceStatus.Due);
        var unauthorizedOccurrenceBefore = ReadWindowState(unauthorizedOccurrenceDatabase);
        var unauthorizedOccurrenceResult = new StandingLeaseWindowEligibilityService(unauthorizedOccurrenceDatabase.Store, () => At(0))
            .Evaluate(CreateWindowEligibilityRequest(unauthorizedOccurrenceDatabase));
        Assert.Equal(StandingLeaseWindowEligibilityStatus.Rejected, unauthorizedOccurrenceResult.Status);
        Assert.Equal("occurrence_not_authorized", unauthorizedOccurrenceResult.Reason);
        Assert.Equal(unauthorizedOccurrenceBefore, ReadWindowState(unauthorizedOccurrenceDatabase));

        using var inactiveLeaseDatabase = new TemporaryDatabase(leaseStatus: ConsentLeaseStatus.Revoked);
        var inactiveLeaseBefore = ReadWindowState(inactiveLeaseDatabase);
        var inactiveLeaseResult = new StandingLeaseWindowEligibilityService(inactiveLeaseDatabase.Store, () => At(0))
            .Evaluate(CreateWindowEligibilityRequest(inactiveLeaseDatabase));
        Assert.Equal(StandingLeaseWindowEligibilityStatus.Rejected, inactiveLeaseResult.Status);
        Assert.Equal("lease_not_active", inactiveLeaseResult.Reason);
        Assert.Equal(inactiveLeaseBefore, ReadWindowState(inactiveLeaseDatabase));
    }

    [Fact]
    public void StandingLeaseWindowEligibilityReturnsAlreadyClaimedForCommittedStartEvidence()
    {
        using var database = new TemporaryDatabase();
        database.Gate.Commit(database.CreateRequest(commitAt: At(1)));
        var before = ReadWindowState(database);

        var result = new StandingLeaseWindowEligibilityService(database.Store, () => At(2))
            .Evaluate(CreateWindowEligibilityRequest(database));

        Assert.Equal(StandingLeaseWindowEligibilityStatus.AlreadyClaimed, result.Status);
        Assert.Equal("occurrence_already_claimed", result.Reason);
        Assert.False(result.Changed);
        Assert.Equal(before, ReadWindowState(database));
    }

    [Fact]
    public void StandingLeaseWindowEligibilityReturnsAlreadyTerminalWithoutRewritingExpiredOccurrence()
    {
        using var database = new TemporaryDatabase();
        var request = CreateWindowEligibilityRequest(database);
        var first = new StandingLeaseWindowEligibilityService(database.Store, () => At(60)).Evaluate(request);
        var before = ReadWindowState(database);

        var second = new StandingLeaseWindowEligibilityService(database.Store, () => At(61)).Evaluate(request);

        Assert.Equal(StandingLeaseWindowEligibilityStatus.Expired, first.Status);
        Assert.Equal(StandingLeaseWindowEligibilityStatus.AlreadyTerminal, second.Status);
        Assert.Equal("occurrence_already_terminal", second.Reason);
        Assert.False(second.Changed);
        Assert.Equal(before, ReadWindowState(database));
    }

    [Fact]
    public void StandingLeaseWindowEligibilityRejectsInvalidClockSamplesWithoutWriting()
    {
        foreach (var clock in new Func<DateTimeOffset?>[]
        {
            () => null,
            () => throw new InvalidOperationException("clock unavailable"),
            () => new DateTimeOffset(2035, 1, 1, 0, 0, 0, TimeSpan.FromHours(8)),
        })
        {
            using var database = new TemporaryDatabase();
            var before = ReadWindowState(database);

            var result = new StandingLeaseWindowEligibilityService(database.Store, clock)
                .Evaluate(CreateWindowEligibilityRequest(database));

            Assert.Equal(StandingLeaseWindowEligibilityStatus.Rejected, result.Status);
            Assert.Contains(result.Reason, new[]
            {
                "eligibility_clock_unavailable",
                "eligibility_time_not_utc",
            });
            Assert.Equal(before, ReadWindowState(database));
        }
    }

    [Fact]
    public void StandingLeaseWindowEligibilityRejectsClockRewindWithoutWriting()
    {
        using var database = new TemporaryDatabase();
        var before = ReadWindowState(database);
        var service = new StandingLeaseWindowEligibilityService(
            database.Store,
            SequenceClock(At(0), At(-1)));
        var request = CreateWindowEligibilityRequest(database);

        var eligible = service.Evaluate(request);
        var rewound = service.Evaluate(request);

        Assert.Equal(StandingLeaseWindowEligibilityStatus.Eligible, eligible.Status);
        Assert.Equal(StandingLeaseWindowEligibilityStatus.Rejected, rewound.Status);
        Assert.Equal("eligibility_time_non_monotonic", rewound.Reason);
        Assert.Equal(before, ReadWindowState(database));
    }

    [Fact]
    public void StandingLeaseWindowEligibilityUsesCheckedTimeArithmeticAndRejectsNonOneTimePlan()
    {
        using var database = new TemporaryDatabase();
        var nearMax = DateTimeOffset.MaxValue.AddSeconds(-30);
        var overflowPlan = PlanDefinition.Rehydrate("plan-1", true, PlanDefinitionStatus.Enabled, nearMax, nearMax, 0);
        var overflowOccurrence = PlanOccurrence.Rehydrate(
            "occ-1", "plan-1", nearMax.AddSeconds(10), nearMax.AddSeconds(29), nearMax, PlanOccurrenceStatus.Authorized,
            null, null, nearMax, 0);
        var overflowLease = ConsentLease.Rehydrate(
            "lease-1", "plan-1", "occ-1", nearMax, DateTimeOffset.MaxValue, 1, TimeSpan.FromSeconds(30),
            ConsentLeaseStatus.Active, nearMax, 0);
        var overflowDecision = StandingLeaseWindowEligibilityPolicy.Evaluate(
            overflowPlan, overflowOccurrence, overflowLease, database.Scope, nearMax.AddSeconds(28));

        Assert.Equal(StandingLeaseWindowEligibilityStatus.Rejected, overflowDecision.Status);
        Assert.Equal("eligibility_time_overflow", overflowDecision.Reason);

        var nonOneTimePlan = PlanDefinition.Rehydrate("plan-1", false, PlanDefinitionStatus.Enabled, At(0), At(0), 0);
        var nonOneTimeDecision = StandingLeaseWindowEligibilityPolicy.Evaluate(
            nonOneTimePlan,
            database.Occurrences.Get("occ-1"),
            database.Leases.Get("lease-1"),
            database.Scope,
            At(0));

        Assert.Equal(StandingLeaseWindowEligibilityStatus.Rejected, nonOneTimeDecision.Status);
        Assert.Equal("plan_not_one_time", nonOneTimeDecision.Reason);
        Assert.Equal(
            (PlanOccurrenceStatus.Authorized, (string?)null, (string?)null, 0L, 0L, 0L),
            ReadWindowState(database));
    }

    [Fact]
    public void StandingLeaseWindowEligibilityRejectsMissingDigestAndUnknownStatusWithoutWriting()
    {
        using var digestDatabase = new TemporaryDatabase();
        var digestBefore = ReadWindowState(digestDatabase);
        var digestResult = new StandingLeaseWindowEligibilityService(digestDatabase.Store, () => At(0))
            .Evaluate(CreateWindowEligibilityRequest(digestDatabase) with { ScopeDigest = new string('f', 64) });
        Assert.Equal(StandingLeaseWindowEligibilityStatus.Rejected, digestResult.Status);
        Assert.Equal("eligibility_scope_identity_mismatch", digestResult.Reason);
        Assert.Equal(digestBefore, ReadWindowState(digestDatabase));

        using var unknownStatusDatabase = new TemporaryDatabase();
        var unknownBefore = ReadWindowState(unknownStatusDatabase);
        RawExecute(
            unknownStatusDatabase.Store.DatabasePath,
            "PRAGMA ignore_check_constraints = ON; UPDATE plan_occurrences SET status_code = 'not_a_status' WHERE id = $id;",
            ("$id", "occ-1"));
        var unknownResult = new StandingLeaseWindowEligibilityService(unknownStatusDatabase.Store, () => At(0))
            .Evaluate(CreateWindowEligibilityRequest(unknownStatusDatabase));
        Assert.Equal(StandingLeaseWindowEligibilityStatus.Rejected, unknownResult.Status);
        Assert.Equal("eligibility_snapshot_invalid", unknownResult.Reason);
        Assert.Equal("not_a_status", RawText(unknownStatusDatabase.Store.DatabasePath, "SELECT status_code FROM plan_occurrences WHERE id = 'occ-1';"));
        Assert.Equal(unknownBefore.Version, RawScalar(unknownStatusDatabase.Store.DatabasePath, "SELECT version FROM plan_occurrences WHERE id = 'occ-1';"));
        Assert.Equal(0L, RawScalar(unknownStatusDatabase.Store.DatabasePath, "SELECT COUNT(*) FROM recording_runs;"));
        Assert.Equal(0L, RawScalar(unknownStatusDatabase.Store.DatabasePath, "SELECT COUNT(*) FROM lease_uses;"));
    }

    [Fact]
    public void StandingLeaseWindowEligibilityRollsBackAuthorizedToExpiredWhenCommitFails()
    {
        using var database = new TemporaryDatabase();
        var before = ReadWindowState(database);

        var result = new StandingLeaseWindowEligibilityService(
                database.Store,
                () => At(60),
                (_, _) => throw new InvalidOperationException("injected eligibility commit failure"))
            .Evaluate(CreateWindowEligibilityRequest(database));

        Assert.Equal(StandingLeaseWindowEligibilityStatus.Rejected, result.Status);
        Assert.Equal("eligibility_sqlite_failure", result.Reason);
        Assert.Equal(before, ReadWindowState(database));
    }

    [Fact]
    public async Task StandingLeaseWindowEligibilityConcurrentCallsExpireOnlyOnce()
    {
        using var database = new TemporaryDatabase();
        var request = CreateWindowEligibilityRequest(database);
        var first = new StandingLeaseWindowEligibilityService(database.Store, () => At(60));
        var second = new StandingLeaseWindowEligibilityService(database.Store, () => At(60));

        var results = await Task.WhenAll(
            Task.Run(() => first.Evaluate(request)),
            Task.Run(() => second.Evaluate(request)));

        Assert.Equal(1, results.Count(item => item.Status == StandingLeaseWindowEligibilityStatus.Expired));
        Assert.Equal(1, results.Count(item =>
            item.Status == StandingLeaseWindowEligibilityStatus.AlreadyTerminal ||
            (item.Status == StandingLeaseWindowEligibilityStatus.Rejected && item.Reason == "eligibility_concurrency_conflict")));
        AssertWindowState(
            database,
            PlanOccurrenceStatus.Expired,
            "occurrence_window_expired",
            runId: null,
            occurrenceVersion: 1,
            runCount: 0,
            useCount: 0);
    }

    [Fact]
    public void StandingLeaseWindowEligibilityHasNoCaptureSurfaceAndExpiredStateCannotPassStartGate()
    {
        using var database = new TemporaryDatabase();
        var result = new StandingLeaseWindowEligibilityService(database.Store, () => At(60))
            .Evaluate(CreateWindowEligibilityRequest(database));

        Assert.Equal(StandingLeaseWindowEligibilityStatus.Expired, result.Status);
        Assert.DoesNotContain(
            typeof(StandingLeaseWindowEligibilityService).GetFields(
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic),
            field => typeof(ICaptureBackend).IsAssignableFrom(field.FieldType));
        Assert.DoesNotContain(
            typeof(StandingLeaseWindowEligibilityService).GetMethods(
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic),
            method => method.GetParameters().Any(parameter => typeof(ICaptureBackend).IsAssignableFrom(parameter.ParameterType)));

        var startGateCode = CaptureGateCode(() => database.Gate.Commit(database.CreateRequest(commitAt: At(60))));
        Assert.NotEqual("committed", startGateCode);
        AssertWindowState(
            database,
            PlanOccurrenceStatus.Expired,
            "occurrence_window_expired",
            runId: null,
            occurrenceVersion: 1,
            runCount: 0,
            useCount: 0);
    }

    [Fact]
    public void StandingLeaseWindowEligibilityRequestContainsOnlyDurableIdentity()
    {
        var propertyNames = typeof(StandingLeaseWindowEligibilityRequest)
            .GetProperties(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic)
            .Select(property => property.Name)
            .Where(name => name != "EqualityContract")
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(
            new[] { "LeaseId", "OccurrenceId", "PlanId", "ScopeDigest", "ScopeId" },
            propertyNames);
    }

    [Fact]
    public async Task StandingLeaseNaturalWakeBeforeWindowDoesNotCallCoordinatorOrWrite()
    {
        using var database = new TemporaryDatabase();
        RawExecute(
            database.Store.DatabasePath,
            "UPDATE plan_occurrences SET window_start_utc = $window_start_utc WHERE id = $id;",
            ("$window_start_utc", At(10).UtcDateTime.Ticks),
            ("$id", "occ-1"));
        var before = ReadWindowState(database);
        var coordinatorCalls = 0;

        var dispatcher = new StandingLeaseNaturalWakeDispatcher(
            new StandingLeaseWindowEligibilityService(database.Store, () => At(5)),
            (_, _) =>
            {
                coordinatorCalls++;
                return Task.FromResult(StandingLeaseOneShotExecutionResult.Rejected("must_not_start"));
            },
            () => new StandingLeaseGeneratedExecutionIds("run_generated", "use_generated"));

        var result = await dispatcher.DispatchAsync(CreateWindowEligibilityRequest(database));

        Assert.Equal(StandingLeaseNaturalWakeDispatchStatus.BeforeWindow, result.Status);
        Assert.Equal("before_occurrence_window", result.Reason);
        Assert.Equal(0, coordinatorCalls);
        Assert.Equal(before, ReadWindowState(database));
    }

    [Fact]
    public async Task StandingLeaseNaturalWakeExpiredDoesNotCallCoordinatorOrCreateClaims()
    {
        using var database = new TemporaryDatabase();
        var coordinatorCalls = 0;
        var dispatcher = new StandingLeaseNaturalWakeDispatcher(
            new StandingLeaseWindowEligibilityService(database.Store, () => At(60)),
            (_, _) =>
            {
                coordinatorCalls++;
                return Task.FromResult(StandingLeaseOneShotExecutionResult.Rejected("must_not_start"));
            },
            () => new StandingLeaseGeneratedExecutionIds("run_generated", "use_generated"));

        var result = await dispatcher.DispatchAsync(CreateWindowEligibilityRequest(database));

        Assert.Equal(StandingLeaseNaturalWakeDispatchStatus.Expired, result.Status);
        Assert.Equal("occurrence_window_expired", result.Reason);
        Assert.Equal(0, coordinatorCalls);
        AssertWindowState(
            database,
            PlanOccurrenceStatus.Expired,
            "occurrence_window_expired",
            runId: null,
            occurrenceVersion: 1,
            runCount: 0,
            useCount: 0);
    }

    [Fact]
    public async Task StandingLeaseNaturalWakeRejectedClaimedTerminalAndInvalidIdentityDoNotCallCoordinator()
    {
        using var claimedDatabase = new TemporaryDatabase();
        claimedDatabase.Gate.Commit(claimedDatabase.CreateRequest(commitAt: At(1)));
        var claimedCalls = 0;
        var claimedDispatcher = new StandingLeaseNaturalWakeDispatcher(
            new StandingLeaseWindowEligibilityService(claimedDatabase.Store, () => At(2)),
            (_, _) =>
            {
                claimedCalls++;
                return Task.FromResult(StandingLeaseOneShotExecutionResult.Rejected("must_not_start"));
            },
            () => new StandingLeaseGeneratedExecutionIds("run_generated", "use_generated"));
        var claimedResult = await claimedDispatcher.DispatchAsync(CreateWindowEligibilityRequest(claimedDatabase));
        Assert.Equal(StandingLeaseNaturalWakeDispatchStatus.AlreadyClaimed, claimedResult.Status);
        Assert.Equal(0, claimedCalls);

        using var terminalDatabase = new TemporaryDatabase();
        var terminalService = new StandingLeaseWindowEligibilityService(terminalDatabase.Store, () => At(60));
        var terminalDispatcher = new StandingLeaseNaturalWakeDispatcher(
            terminalService,
            (_, _) => Task.FromResult(StandingLeaseOneShotExecutionResult.Rejected("must_not_start")),
            () => new StandingLeaseGeneratedExecutionIds("run_generated", "use_generated"));
        _ = await terminalDispatcher.DispatchAsync(CreateWindowEligibilityRequest(terminalDatabase));
        var terminalCalls = 0;
        var terminalRetryDispatcher = new StandingLeaseNaturalWakeDispatcher(
            new StandingLeaseWindowEligibilityService(terminalDatabase.Store, () => At(61)),
            (_, _) =>
            {
                terminalCalls++;
                return Task.FromResult(StandingLeaseOneShotExecutionResult.Rejected("must_not_start"));
            },
            () => new StandingLeaseGeneratedExecutionIds("run_generated", "use_generated"));
        var terminalResult = await terminalRetryDispatcher.DispatchAsync(CreateWindowEligibilityRequest(terminalDatabase));
        Assert.Equal(StandingLeaseNaturalWakeDispatchStatus.AlreadyTerminal, terminalResult.Status);
        Assert.Equal(0, terminalCalls);

        using var invalidDatabase = new TemporaryDatabase();
        var invalidBefore = ReadWindowState(invalidDatabase);
        var invalidCalls = 0;
        var invalidDispatcher = new StandingLeaseNaturalWakeDispatcher(
            new StandingLeaseWindowEligibilityService(invalidDatabase.Store, () => At(0)),
            (_, _) =>
            {
                invalidCalls++;
                return Task.FromResult(StandingLeaseOneShotExecutionResult.Rejected("must_not_start"));
            },
            () => new StandingLeaseGeneratedExecutionIds("run_generated", "use_generated"));
        var invalidResult = await invalidDispatcher.DispatchAsync(
            CreateWindowEligibilityRequest(invalidDatabase) with { ScopeDigest = new string('f', 64) });
        Assert.Equal(StandingLeaseNaturalWakeDispatchStatus.Rejected, invalidResult.Status);
        Assert.Equal("eligibility_scope_identity_mismatch", invalidResult.Reason);
        Assert.Equal(0, invalidCalls);
        Assert.Equal(invalidBefore, ReadWindowState(invalidDatabase));
    }

    [Fact]
    public async Task StandingLeaseNaturalWakeClockSnapshotAndIdFailuresDoNotStartOrRetry()
    {
        using var clockDatabase = new TemporaryDatabase();
        var clockCalls = 0;
        var clockDispatcher = new StandingLeaseNaturalWakeDispatcher(
            new StandingLeaseWindowEligibilityService(clockDatabase.Store, () => null),
            (_, _) =>
            {
                clockCalls++;
                return Task.FromResult(StandingLeaseOneShotExecutionResult.Rejected("must_not_start"));
            },
            () => new StandingLeaseGeneratedExecutionIds("run_generated", "use_generated"));
        var clockResult = await clockDispatcher.DispatchAsync(CreateWindowEligibilityRequest(clockDatabase));
        Assert.Equal(StandingLeaseNaturalWakeDispatchStatus.Rejected, clockResult.Status);
        Assert.Equal("eligibility_clock_unavailable", clockResult.Reason);
        Assert.Equal(0, clockCalls);

        using var snapshotDatabase = new TemporaryDatabase();
        var snapshotCalls = 0;
        var snapshotDispatcher = new StandingLeaseNaturalWakeDispatcher(
            new StandingLeaseWindowEligibilityService(snapshotDatabase.Store, () => At(0)),
            (_, _) =>
            {
                snapshotCalls++;
                return Task.FromResult(StandingLeaseOneShotExecutionResult.Rejected("must_not_start"));
            },
            () => new StandingLeaseGeneratedExecutionIds("run_generated", "use_generated"));
        var snapshotResult = await snapshotDispatcher.DispatchAsync(
            CreateWindowEligibilityRequest(snapshotDatabase) with { ScopeId = "scope_missing" });
        Assert.Equal(StandingLeaseNaturalWakeDispatchStatus.Rejected, snapshotResult.Status);
        Assert.Equal("eligibility_snapshot_invalid", snapshotResult.Reason);
        Assert.Equal(0, snapshotCalls);

        using var idDatabase = new TemporaryDatabase();
        var idBefore = ReadWindowState(idDatabase);
        var idCalls = 0;
        var idDispatcher = new StandingLeaseNaturalWakeDispatcher(
            new StandingLeaseWindowEligibilityService(idDatabase.Store, () => At(0)),
            (_, _) =>
            {
                idCalls++;
                return Task.FromResult(StandingLeaseOneShotExecutionResult.Rejected("must_not_start"));
            },
            () => throw new InvalidOperationException("id generator failed"));
        var idResult = await idDispatcher.DispatchAsync(CreateWindowEligibilityRequest(idDatabase));
        Assert.Equal(StandingLeaseNaturalWakeDispatchStatus.Rejected, idResult.Status);
        Assert.Equal("natural_wake_id_generation_failed", idResult.Reason);
        Assert.Equal(0, idCalls);
        Assert.Equal(idBefore, ReadWindowState(idDatabase));

        using var coordinatorDatabase = new TemporaryDatabase();
        var coordinatorBefore = ReadWindowState(coordinatorDatabase);
        var coordinatorCallCount = 0;
        var coordinatorDispatcher = new StandingLeaseNaturalWakeDispatcher(
            new StandingLeaseWindowEligibilityService(coordinatorDatabase.Store, () => At(0)),
            (_, _) =>
            {
                coordinatorCallCount++;
                throw new InvalidOperationException("coordinator failed");
            },
            () => new StandingLeaseGeneratedExecutionIds("run_generated", "use_generated"));
        var coordinatorResult = await coordinatorDispatcher.DispatchAsync(CreateWindowEligibilityRequest(coordinatorDatabase));
        Assert.Equal(StandingLeaseNaturalWakeDispatchStatus.Rejected, coordinatorResult.Status);
        Assert.Equal("natural_wake_coordinator_failed", coordinatorResult.Reason);
        Assert.Equal(1, coordinatorCallCount);
        Assert.Equal(coordinatorBefore, ReadWindowState(coordinatorDatabase));
    }

    [Fact]
    public async Task StandingLeaseNaturalWakeEligibleGeneratesIdsAndPassesEligibilityVersionsOnce()
    {
        using var database = new TemporaryDatabase();
        var before = ReadWindowState(database);
        StandingLeaseOneShotExecutionRequest? capturedRequest = null;
        var coordinatorCalls = 0;
        var generatorCalls = 0;
        var dispatcher = new StandingLeaseNaturalWakeDispatcher(
            new StandingLeaseWindowEligibilityService(database.Store, () => At(0)),
            (request, _) =>
            {
                coordinatorCalls++;
                capturedRequest = request;
                Assert.Equal(before, ReadWindowState(database));
                return Task.FromResult(StandingLeaseOneShotExecutionResult.AlreadyCommitted(
                    request.RunId,
                    request.LeaseUseId));
            },
            () =>
            {
                generatorCalls++;
                return new StandingLeaseGeneratedExecutionIds("run_generated", "use_generated");
            });

        var result = await dispatcher.DispatchAsync(CreateWindowEligibilityRequest(database));

        Assert.Equal(StandingLeaseNaturalWakeDispatchStatus.AlreadyCommitted, result.Status);
        Assert.Equal("already_committed", result.Reason);
        Assert.Equal(1, coordinatorCalls);
        Assert.Equal(1, generatorCalls);
        Assert.NotNull(capturedRequest);
        Assert.Equal("run_generated", capturedRequest!.RunId);
        Assert.Equal("use_generated", capturedRequest.LeaseUseId);
        Assert.Equal(0L, capturedRequest.ExpectedPlanVersion);
        Assert.Equal(0L, capturedRequest.ExpectedOccurrenceVersion);
        Assert.Equal(0L, capturedRequest.ExpectedLeaseVersion);
        Assert.Equal(before, ReadWindowState(database));
    }

    [Theory]
    [InlineData("already-committed")]
    [InlineData("committed-not-started")]
    public async Task StandingLeaseNaturalWakeForwardsCoordinatorTerminalBoundaryWithoutRetry(string coordinatorOutcome)
    {
        using var database = new TemporaryDatabase();
        var coordinatorCalls = 0;
        var generatorCalls = 0;
        var dispatcher = new StandingLeaseNaturalWakeDispatcher(
            new StandingLeaseWindowEligibilityService(database.Store, () => At(0)),
            (request, _) =>
            {
                coordinatorCalls++;
                var coordinatorResult = coordinatorOutcome == "already-committed"
                    ? StandingLeaseOneShotExecutionResult.AlreadyCommitted(request.RunId, request.LeaseUseId)
                    : StandingLeaseOneShotExecutionResult.CommittedNotStarted(
                        "fake_committed_not_started", request.RunId, request.LeaseUseId);
                return Task.FromResult(coordinatorResult);
            },
            () =>
            {
                generatorCalls++;
                return new StandingLeaseGeneratedExecutionIds("run_generated", "use_generated");
            });

        var result = await dispatcher.DispatchAsync(CreateWindowEligibilityRequest(database));

        Assert.Equal(
            coordinatorOutcome == "already-committed"
                ? StandingLeaseNaturalWakeDispatchStatus.AlreadyCommitted
                : StandingLeaseNaturalWakeDispatchStatus.CommittedNotStarted,
            result.Status);
        Assert.Equal(1, coordinatorCalls);
        Assert.Equal(1, generatorCalls);
        Assert.Equal(0L, RawScalar(database.Store.DatabasePath, "SELECT COUNT(*) FROM recording_runs;"));
        Assert.Equal(0L, RawScalar(database.Store.DatabasePath, "SELECT COUNT(*) FROM lease_uses;"));
    }

    [Fact]
    public async Task StandingLeaseNaturalWakeStartedUsesExistingCoordinatorLifecycleBoundaryWithoutBackendSurface()
    {
        using var database = new TemporaryDatabase();
        var backend = new LifecycleCaptureBackend();
        var coordinator = CreateLifecycleCoordinator(database, backend);
        var dispatcher = new StandingLeaseNaturalWakeDispatcher(
            new StandingLeaseWindowEligibilityService(database.Store, () => At(0)),
            coordinator.ExecuteAsync,
            () => new StandingLeaseGeneratedExecutionIds("run_generated", "use_generated"));

        var result = await dispatcher.DispatchAsync(CreateWindowEligibilityRequest(database));

        Assert.Equal(StandingLeaseNaturalWakeDispatchStatus.Started, result.Status);
        Assert.Equal("started", result.Reason);
        Assert.NotNull(result.LifecycleSession);
        Assert.Equal(1, backend.StartCalls);
        Assert.Equal(1L, RawScalar(database.Store.DatabasePath, "SELECT COUNT(*) FROM recording_runs;"));
        Assert.Equal(1L, RawScalar(database.Store.DatabasePath, "SELECT COUNT(*) FROM lease_uses;"));
        Assert.Equal(RecordingRunStatus.StartCommitted, database.Runs.Get("run_generated").Status);
        Assert.Equal(LeaseUseStatus.StartCommitted, database.Uses.Get("use_generated").Status);
        Assert.Equal(PlanOccurrenceStatus.RunCreated, database.Occurrences.Get("occ-1").Status);

        result.LifecycleSession!.Dispose();
        Assert.Equal(1, backend.DisposeCalls);
    }

    [Fact]
    public async Task StandingLeaseNaturalWakeStartGateRevalidationRejectsVersionChangeWithoutRetry()
    {
        using var database = new TemporaryDatabase();
        var backend = new LifecycleCaptureBackend();
        var coordinator = CreateLifecycleCoordinator(database, backend);
        var coordinatorCalls = 0;
        var dispatcher = new StandingLeaseNaturalWakeDispatcher(
            new StandingLeaseWindowEligibilityService(database.Store, () => At(0)),
            (request, cancellationToken) =>
            {
                coordinatorCalls++;
                RawExecute(
                    database.Store.DatabasePath,
                    "UPDATE plans SET version = 1 WHERE id = $id;",
                    ("$id", "plan-1"));
                return coordinator.ExecuteAsync(request, cancellationToken);
            },
            () => new StandingLeaseGeneratedExecutionIds("run_generated", "use_generated"));

        var result = await dispatcher.DispatchAsync(CreateWindowEligibilityRequest(database));

        Assert.Equal(StandingLeaseNaturalWakeDispatchStatus.Rejected, result.Status);
        Assert.Equal(1, coordinatorCalls);
        Assert.Equal(0, backend.StartCalls);
        Assert.Equal(0L, RawScalar(database.Store.DatabasePath, "SELECT COUNT(*) FROM recording_runs;"));
        Assert.Equal(0L, RawScalar(database.Store.DatabasePath, "SELECT COUNT(*) FROM lease_uses;"));
        Assert.Equal(PlanOccurrenceStatus.Authorized, database.Occurrences.Get("occ-1").Status);
    }

    [Fact]
    public async Task StandingLeaseNaturalWakeConcurrentCallsProduceOneRunOneUseAndOneStart()
    {
        using var database = new TemporaryDatabase();
        var firstBackend = new LifecycleCaptureBackend();
        var secondBackend = new LifecycleCaptureBackend();
        var firstCoordinator = CreateLifecycleCoordinator(database, firstBackend);
        var secondCoordinator = CreateLifecycleCoordinator(database, secondBackend);
        var first = new StandingLeaseNaturalWakeDispatcher(
            new StandingLeaseWindowEligibilityService(database.Store, () => At(0)),
            firstCoordinator.ExecuteAsync,
            () => new StandingLeaseGeneratedExecutionIds("run_first", "use_first"));
        var second = new StandingLeaseNaturalWakeDispatcher(
            new StandingLeaseWindowEligibilityService(database.Store, () => At(0)),
            secondCoordinator.ExecuteAsync,
            () => new StandingLeaseGeneratedExecutionIds("run_second", "use_second"));
        var request = CreateWindowEligibilityRequest(database);

        var results = await Task.WhenAll(
            Task.Run(() => first.DispatchAsync(request)),
            Task.Run(() => second.DispatchAsync(request)));

        Assert.Equal(1, results.Count(item => item.Status == StandingLeaseNaturalWakeDispatchStatus.Started));
        var competingResultCount = results.Count(item =>
            item.Status == StandingLeaseNaturalWakeDispatchStatus.AlreadyClaimed ||
            item.Status == StandingLeaseNaturalWakeDispatchStatus.AlreadyCommitted ||
            item.Status == StandingLeaseNaturalWakeDispatchStatus.Rejected);
        Assert.True(
            competingResultCount == 1,
            string.Join(" | ", results.Select(item => $"{item.Status}:{item.Reason}")));
        Assert.Equal(1L, RawScalar(database.Store.DatabasePath, "SELECT COUNT(*) FROM recording_runs;"));
        Assert.Equal(1L, RawScalar(database.Store.DatabasePath, "SELECT COUNT(*) FROM lease_uses;"));
        Assert.Equal(1, firstBackend.StartCalls + secondBackend.StartCalls);
        foreach (var result in results.Where(item => item.LifecycleSession is not null))
        {
            result.LifecycleSession!.Dispose();
        }
    }

    [Fact]
    public void StandingLeaseNaturalWakeRequestAndResultDoNotExposeCallerExecutionOrCaptureFields()
    {
        var requestNames = typeof(StandingLeaseWindowEligibilityRequest)
            .GetProperties(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic)
            .Select(property => property.Name)
            .Where(name => name != "EqualityContract")
            .ToArray();
        var resultNames = typeof(StandingLeaseNaturalWakeDispatchResult)
            .GetProperties(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic)
            .Select(property => property.Name)
            .ToArray();
        var forbiddenNames = new[]
        {
            "RunId", "LeaseUseId", "Duration", "CaptureConfig", "Target", "Bounds", "OutputPath",
            "Backend", "Proof", "Nonce", "Environment", "NativeHandle",
        };

        Assert.DoesNotContain(requestNames, name => forbiddenNames.Contains(name, StringComparer.OrdinalIgnoreCase));
        Assert.DoesNotContain(resultNames, name => forbiddenNames.Contains(name, StringComparer.OrdinalIgnoreCase));
        Assert.DoesNotContain(
            typeof(StandingLeaseNaturalWakeDispatcher).GetMethods(
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic),
            method => method.GetParameters().Any(parameter => typeof(ICaptureBackend).IsAssignableFrom(parameter.ParameterType)));
    }

    [Fact]
    public void StandingLeaseRecoveryReconcilesCommittedStartConservatively()
    {
        using var database = new TemporaryDatabase();
        _ = CreateStandingAuthorizationAndSpecification(database);
        var before = ReadRecoveryState(database);

        var result = new StandingLeaseRestartRecoveryService(database.Store, () => At(3))
            .Recover(CreateRecoveryRequest(database));

        Assert.Equal(StandingLeaseRecoveryStatus.Recovered, result.Status);
        Assert.Equal("recovery_after_start_commit", result.Reason);
        AssertRecoveryState(
            database,
            RecordingRunStatus.StartedUnknown,
            LeaseUseStatus.StartedUnknown,
            PlanOccurrenceStatus.Blocked,
            "recovery_after_start_commit",
            expectedActualDuration: null,
            expectedRunVersion: before.RunVersion + 1,
            expectedUseVersion: before.UseVersion + 1,
            expectedOccurrenceVersion: before.OccurrenceVersion + 1);
        Assert.Equal(ConsentLeaseStatus.Exhausted, database.Leases.Get("lease-1").Status);
        Assert.Equal(before.LeaseVersion, database.Leases.Get("lease-1").Version);
    }

    [Theory]
    [InlineData(RecordingRunStatus.Recording, "recovery_after_recording_interrupted")]
    [InlineData(RecordingRunStatus.Finalizing, "recovery_during_finalization")]
    [InlineData(RecordingRunStatus.MediaReady, "recovery_before_settlement")]
    public void StandingLeaseRecoveryReconcilesActiveDurableTailWithoutMediaInference(
        RecordingRunStatus runStatus,
        string expectedReason)
    {
        using var database = new TemporaryDatabase();
        var (_, _, specification, _) = CreateStandingAuthorizationAndSpecification(database);
        PrepareRecoveryActiveState(database, specification, runStatus);
        var before = ReadRecoveryState(database);

        var result = new StandingLeaseRestartRecoveryService(database.Store, () => At(6))
            .Recover(CreateRecoveryRequest(database));

        Assert.Equal(StandingLeaseRecoveryStatus.Recovered, result.Status);
        Assert.Equal(expectedReason, result.Reason);
        AssertRecoveryState(
            database,
            RecordingRunStatus.SessionInterrupted,
            LeaseUseStatus.StartedUnknown,
            PlanOccurrenceStatus.Blocked,
            expectedReason,
            expectedActualDuration: null,
            expectedRunVersion: before.RunVersion + 1,
            expectedUseVersion: before.UseVersion + 1,
            expectedOccurrenceVersion: before.OccurrenceVersion + 1);
    }

    [Theory]
    [InlineData("settled")]
    [InlineData("started_unknown")]
    [InlineData("session_interrupted")]
    [InlineData("failed")]
    public void StandingLeaseRecoveryIsIdempotentForEveryConsistentTerminalChain(string terminalKind)
    {
        using var database = new TemporaryDatabase();
        var (_, _, specification, _) = CreateStandingAuthorizationAndSpecification(database);
        PrepareRecoveryTerminalState(database, specification, terminalKind);
        var before = ReadRecoveryState(database);

        var result = new StandingLeaseRestartRecoveryService(database.Store, () => At(6))
            .Recover(CreateRecoveryRequest(database));

        Assert.Equal(StandingLeaseRecoveryStatus.AlreadyReconciled, result.Status);
        Assert.Equal("recovery_idempotent_noop", result.Reason);
        var after = ReadRecoveryState(database);
        Assert.Equal(before, after);
        Assert.Equal(before.RunReason, after.RunReason);
        Assert.Equal(before.OccurrenceReason, after.OccurrenceReason);
        Assert.Equal(before.ActualDuration, after.ActualDuration);
    }

    [Fact]
    public void StandingLeaseRecoveryRejectsMissingRowsAndRelationMismatchWithoutWrites()
    {
        using var missingRunDatabase = new TemporaryDatabase();
        _ = CreateStandingAuthorizationAndSpecification(missingRunDatabase);
        var missingBefore = ReadRecoveryState(missingRunDatabase);
        RawExecute(
            missingRunDatabase.Store.DatabasePath,
            "PRAGMA foreign_keys = OFF; DELETE FROM recording_runs WHERE id = $id;",
            ("$id", "run-1"));

        var missingResult = new StandingLeaseRestartRecoveryService(missingRunDatabase.Store, () => At(3))
            .Recover(CreateRecoveryRequest(missingRunDatabase));

        Assert.Equal(StandingLeaseRecoveryStatus.Rejected, missingResult.Status);
        Assert.Equal("recovery_snapshot_invalid", missingResult.Reason);
        Assert.Equal(missingBefore.OccurrenceVersion, RawScalar(missingRunDatabase.Store.DatabasePath, "SELECT version FROM plan_occurrences WHERE id = 'occ-1';"));
        Assert.Equal(missingBefore.LeaseVersion, missingRunDatabase.Leases.Get("lease-1").Version);
        Assert.Equal(0L, RawScalar(missingRunDatabase.Store.DatabasePath, "SELECT COUNT(*) FROM recording_runs;"));

        using var mismatchDatabase = new TemporaryDatabase();
        _ = CreateStandingAuthorizationAndSpecification(mismatchDatabase);
        var mismatchBefore = ReadRecoveryState(mismatchDatabase);
        RawExecute(
            mismatchDatabase.Store.DatabasePath,
            "PRAGMA foreign_keys = OFF; UPDATE lease_uses SET occurrence_id = $occurrence_id WHERE id = $id;",
            ("$occurrence_id", "other-occurrence"),
            ("$id", "use-1"));

        var mismatchResult = new StandingLeaseRestartRecoveryService(mismatchDatabase.Store, () => At(3))
            .Recover(CreateRecoveryRequest(mismatchDatabase));

        Assert.Equal(StandingLeaseRecoveryStatus.Rejected, mismatchResult.Status);
        Assert.Equal("recovery_snapshot_invalid", mismatchResult.Reason);
        Assert.Equal(mismatchBefore.RunVersion, mismatchDatabase.Runs.Get("run-1").Version);
        Assert.Equal(mismatchBefore.OccurrenceVersion, mismatchDatabase.Occurrences.Get("occ-1").Version);
    }

    [Fact]
    public void StandingLeaseRecoveryRejectsScopeDigestUnknownStatusAndIllegalReasonWithoutWrites()
    {
        using var digestDatabase = new TemporaryDatabase();
        _ = CreateStandingAuthorizationAndSpecification(digestDatabase);
        var digestBefore = ReadRecoveryState(digestDatabase);
        var digestResult = new StandingLeaseRestartRecoveryService(digestDatabase.Store, () => At(3))
            .Recover(CreateRecoveryRequest(digestDatabase) with { ScopeDigest = new string('f', 64) });
        Assert.Equal(StandingLeaseRecoveryStatus.Rejected, digestResult.Status);
        Assert.Equal(digestBefore, ReadRecoveryState(digestDatabase));

        using var unknownStatusDatabase = new TemporaryDatabase();
        _ = CreateStandingAuthorizationAndSpecification(unknownStatusDatabase);
        var unknownBefore = ReadRecoveryState(unknownStatusDatabase);
        RawExecute(
            unknownStatusDatabase.Store.DatabasePath,
            "PRAGMA ignore_check_constraints = ON; UPDATE recording_runs SET status_code = 'not_a_status' WHERE id = $id;",
            ("$id", "run-1"));
        var unknownResult = new StandingLeaseRestartRecoveryService(unknownStatusDatabase.Store, () => At(3))
            .Recover(CreateRecoveryRequest(unknownStatusDatabase));
        Assert.Equal(StandingLeaseRecoveryStatus.Rejected, unknownResult.Status);
        Assert.Equal("recovery_snapshot_invalid", unknownResult.Reason);
        Assert.Equal(unknownBefore.OccurrenceVersion, unknownStatusDatabase.Occurrences.Get("occ-1").Version);
        Assert.Equal(unknownBefore.LeaseVersion, unknownStatusDatabase.Leases.Get("lease-1").Version);

        using var reasonDatabase = new TemporaryDatabase();
        var (_, _, reasonSpecification, _) = CreateStandingAuthorizationAndSpecification(reasonDatabase);
        PrepareRecoveryTerminalState(reasonDatabase, reasonSpecification, "started_unknown");
        var reasonBefore = ReadRecoveryState(reasonDatabase);
        RawExecute(
            reasonDatabase.Store.DatabasePath,
            "UPDATE plan_occurrences SET terminal_reason_code = $reason WHERE id = $id;",
            ("$reason", "not_a_recovery_reason"),
            ("$id", "occ-1"));
        var reasonResult = new StandingLeaseRestartRecoveryService(reasonDatabase.Store, () => At(6))
            .Recover(CreateRecoveryRequest(reasonDatabase));
        Assert.Equal(StandingLeaseRecoveryStatus.Rejected, reasonResult.Status);
        Assert.Equal("recovery_snapshot_invalid", reasonResult.Reason);
        Assert.Equal(reasonBefore.RunVersion, reasonDatabase.Runs.Get("run-1").Version);
        Assert.Equal(reasonBefore.UseVersion, reasonDatabase.Uses.Get("use-1").Version);
    }

    [Fact]
    public void StandingLeaseRecoveryRejectsAnUnadvanceablePersistedVersionWithoutWrites()
    {
        using var database = new TemporaryDatabase();
        _ = CreateStandingAuthorizationAndSpecification(database);
        RawExecute(
            database.Store.DatabasePath,
            "PRAGMA ignore_check_constraints = ON; UPDATE recording_runs SET version = $version WHERE id = $id;",
            ("$version", long.MaxValue),
            ("$id", "run-1"));

        var result = new StandingLeaseRestartRecoveryService(database.Store, () => At(3))
            .Recover(CreateRecoveryRequest(database));

        Assert.Equal(StandingLeaseRecoveryStatus.Rejected, result.Status);
        Assert.Equal("recovery_snapshot_invalid", result.Reason);
        Assert.Equal(long.MaxValue, RawScalar(database.Store.DatabasePath, "SELECT version FROM recording_runs WHERE id = 'run-1';"));
        Assert.Equal(2L, database.Uses.Get("use-1").Version);
        Assert.Equal(1L, database.Occurrences.Get("occ-1").Version);
    }

    [Theory]
    [InlineData("clock-null")]
    [InlineData("clock-non-utc")]
    [InlineData("clock-rewound")]
    public void StandingLeaseRecoveryFailsClosedForUnavailableNonUtcOrRewoundClock(string clockKind)
    {
        using var database = new TemporaryDatabase();
        _ = CreateStandingAuthorizationAndSpecification(database);
        var before = ReadRecoveryState(database);
        Func<DateTimeOffset?> clock = clockKind switch
        {
            "clock-null" => () => null,
            "clock-non-utc" => () => new DateTimeOffset(2035, 1, 1, 0, 0, 3, TimeSpan.FromHours(8)),
            _ => () => At(0),
        };

        var result = new StandingLeaseRestartRecoveryService(database.Store, clock)
            .Recover(CreateRecoveryRequest(database));

        Assert.Equal(StandingLeaseRecoveryStatus.Rejected, result.Status);
        Assert.Contains(result.Reason, new[] { "recovery_clock_unavailable", "recovery_time_not_utc", "recovery_time_non_monotonic" });
        Assert.Equal(before, ReadRecoveryState(database));
    }

    [Fact]
    public void StandingLeaseRecoveryRollsBackAllAggregateWritesWhenCommitFails()
    {
        using var database = new TemporaryDatabase();
        _ = CreateStandingAuthorizationAndSpecification(database);
        var before = ReadRecoveryState(database);
        var result = new StandingLeaseRestartRecoveryService(
                database.Store,
                () => At(3),
                (_, _) => throw new InvalidOperationException("injected recovery commit failure"))
            .Recover(CreateRecoveryRequest(database));

        Assert.Equal(StandingLeaseRecoveryStatus.Rejected, result.Status);
        Assert.Equal("recovery_sqlite_failure", result.Reason);
        Assert.Equal(before, ReadRecoveryState(database));
    }

    [Fact]
    public async Task StandingLeaseRecoveryConcurrentCallsAdvanceTheChainOnlyOnce()
    {
        using var database = new TemporaryDatabase();
        _ = CreateStandingAuthorizationAndSpecification(database);
        var request = CreateRecoveryRequest(database);
        var first = new StandingLeaseRestartRecoveryService(database.Store, () => At(3));
        var second = new StandingLeaseRestartRecoveryService(database.Store, () => At(3));

        var results = await Task.WhenAll(
            Task.Run(() => first.Recover(request)),
            Task.Run(() => second.Recover(request)));

        Assert.Equal(1, results.Count(item => item.Status == StandingLeaseRecoveryStatus.Recovered));
        Assert.Equal(1, results.Count(item => item.Status == StandingLeaseRecoveryStatus.AlreadyReconciled));
        AssertRecoveryState(
            database,
            RecordingRunStatus.StartedUnknown,
            LeaseUseStatus.StartedUnknown,
            PlanOccurrenceStatus.Blocked,
            "recovery_after_start_commit",
            expectedActualDuration: null,
            expectedRunVersion: 3,
            expectedUseVersion: 3,
            expectedOccurrenceVersion: 2);
    }

    [Fact]
    public void StandingLeaseRecoveryHasNoBackendSurfaceAndDoesNotCreateExecutionClaims()
    {
        using var database = new TemporaryDatabase();
        _ = CreateStandingAuthorizationAndSpecification(database);
        var result = new StandingLeaseRestartRecoveryService(database.Store, () => At(3))
            .Recover(CreateRecoveryRequest(database));

        Assert.Equal(StandingLeaseRecoveryStatus.Recovered, result.Status);
        Assert.DoesNotContain(
            typeof(StandingLeaseRestartRecoveryService).GetFields(
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic),
            field => typeof(ICaptureBackend).IsAssignableFrom(field.FieldType));
        Assert.DoesNotContain(
            typeof(StandingLeaseRestartRecoveryService).GetMethods(
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic),
            method => method.GetParameters().Any(parameter => typeof(ICaptureBackend).IsAssignableFrom(parameter.ParameterType)));
        Assert.Equal(1L, RawScalar(database.Store.DatabasePath, "SELECT COUNT(*) FROM recording_runs;"));
        Assert.Equal(1L, RawScalar(database.Store.DatabasePath, "SELECT COUNT(*) FROM lease_uses;"));
        Assert.Equal(ConsentLeaseStatus.Exhausted, database.Leases.Get("lease-1").Status);
    }

    [Fact]
    public void StandingLeaseRecoveryLeavesTheExistingStartGateNonExecutable()
    {
        using var database = new TemporaryDatabase();
        _ = CreateStandingAuthorizationAndSpecification(database);
        var result = new StandingLeaseRestartRecoveryService(database.Store, () => At(3))
            .Recover(CreateRecoveryRequest(database));
        Assert.Equal(StandingLeaseRecoveryStatus.Recovered, result.Status);

        var retry = database.Gate.Commit(database.CreateRequest());

        Assert.Equal(Phase3StartGateCommitStatus.AlreadyCommitted, retry.Status);
        Assert.Equal("already_committed", retry.Code);
        Assert.Equal(1L, RawScalar(database.Store.DatabasePath, "SELECT COUNT(*) FROM recording_runs;"));
        Assert.Equal(1L, RawScalar(database.Store.DatabasePath, "SELECT COUNT(*) FROM lease_uses;"));
    }

    [Fact]
    public void ActiveStandingSessionDisposeUsesConservativeRecoveryBeforeReleasingBackend()
    {
        using var database = new TemporaryDatabase();
        var (_, _, specification, _) = CreateStandingAuthorizationAndSpecification(database);
        var backend = new LifecycleCaptureBackend();
        var session = new StandingLeaseOneShotExecutionSession(database.Store, specification, backend, () => At(3));
        var lifecycleSession = (IStandingLeaseCaptureLifecycleSession)session;
        Assert.True(lifecycleSession.TryAttach(backend, out var attachFailure), attachFailure);

        ((IDisposable)session).Dispose();

        Assert.Equal(RecordingRunStatus.StartedUnknown, database.Runs.Get("run-1").Status);
        Assert.Equal(LeaseUseStatus.StartedUnknown, database.Uses.Get("use-1").Status);
        Assert.Equal(PlanOccurrenceStatus.Blocked, database.Occurrences.Get("occ-1").Status);
        Assert.Equal("recovery_after_start_commit", database.Runs.Get("run-1").TerminalReasonCode);
        Assert.Equal(1, backend.DisposeCalls);
    }

    [Fact]
    public void ActiveRecordingSessionDisposeUsesSessionInterruptedRecoveryBeforeReleasingBackend()
    {
        using var database = new TemporaryDatabase();
        var (_, _, specification, _) = CreateStandingAuthorizationAndSpecification(database);
        var backend = new LifecycleCaptureBackend();
        var session = new StandingLeaseOneShotExecutionSession(database.Store, specification, backend, () => At(3));
        var lifecycleSession = (IStandingLeaseCaptureLifecycleSession)session;
        Assert.True(lifecycleSession.TryAttach(backend, out var attachFailure), attachFailure);
        backend.RaiseFirstFrame();

        ((IDisposable)session).Dispose();

        Assert.Equal(RecordingRunStatus.SessionInterrupted, database.Runs.Get("run-1").Status);
        Assert.Equal(LeaseUseStatus.StartedUnknown, database.Uses.Get("use-1").Status);
        Assert.Equal(PlanOccurrenceStatus.Blocked, database.Occurrences.Get("occ-1").Status);
        Assert.Equal("recovery_after_recording_interrupted", database.Runs.Get("run-1").TerminalReasonCode);
        Assert.Equal(1, backend.DisposeCalls);
    }

    private static StandingLeaseRecoveryRequest CreateRecoveryRequest(TemporaryDatabase database) =>
        new(
            database.Scope.PlanId,
            database.Scope.OccurrenceId,
            database.Scope.LeaseId,
            "run-1",
            "use-1",
            database.Scope.ScopeId,
            database.Scope.ScopeDigest);

    private static StandingLeaseWindowEligibilityRequest CreateWindowEligibilityRequest(TemporaryDatabase database) =>
        new(
            database.Scope.PlanId,
            database.Scope.OccurrenceId,
            database.Scope.LeaseId,
            database.Scope.ScopeId,
            database.Scope.ScopeDigest);

    private static (PlanOccurrenceStatus Status, string? TerminalReason, string? RunId, long Version, long RunCount, long UseCount) ReadWindowState(TemporaryDatabase database)
    {
        var occurrence = database.Occurrences.Get("occ-1");
        return (
            occurrence.Status,
            occurrence.TerminalReasonCode,
            occurrence.RunId,
            occurrence.Version,
            RawScalar(database.Store.DatabasePath, "SELECT COUNT(*) FROM recording_runs;"),
            RawScalar(database.Store.DatabasePath, "SELECT COUNT(*) FROM lease_uses;"));
    }

    private static void AssertWindowState(
        TemporaryDatabase database,
        PlanOccurrenceStatus status,
        string? terminalReason,
        string? runId,
        long occurrenceVersion,
        long runCount,
        long useCount)
    {
        Assert.Equal(
            (status, terminalReason, runId, occurrenceVersion, runCount, useCount),
            ReadWindowState(database));
    }

    private static void PrepareRecoveryActiveState(
        TemporaryDatabase database,
        StandingLeaseCaptureSpecification specification,
        RecordingRunStatus desiredStatus)
    {
        var lifecycle = new SqliteStandingLeaseLifecycleTransaction(database.Store, specification);
        Assert.True(lifecycle.ObserveFirstFrame(new FirstFrameObservation
        {
            EvidenceKind = "test_first_frame",
            FrameNumber = 1,
            TotalSizeBytes = 4096,
        }, At(2)).Succeeded);

        if (desiredStatus is RecordingRunStatus.Finalizing or RecordingRunStatus.MediaReady)
        {
            Assert.True(lifecycle.ObserveCaptureEnded(At(3)).Succeeded);
        }

        if (desiredStatus == RecordingRunStatus.MediaReady)
        {
            var run = database.Runs.Get("run-1");
            var expectedVersion = run.Version;
            Assert.True(run.TryTransition(RecordingRunStatus.MediaReady, At(4)).Succeeded);
            database.Runs.Update(run, expectedVersion);
        }
    }

    private static void PrepareRecoveryTerminalState(
        TemporaryDatabase database,
        StandingLeaseCaptureSpecification specification,
        string terminalKind)
    {
        var lifecycle = new SqliteStandingLeaseLifecycleTransaction(database.Store, specification);
        if (terminalKind == "settled")
        {
            Assert.True(lifecycle.ObserveFirstFrame(new FirstFrameObservation
            {
                EvidenceKind = "test_first_frame",
                FrameNumber = 1,
                TotalSizeBytes = 4096,
            }, At(2)).Succeeded);
            Assert.True(lifecycle.CompleteTermination(
                StandingLeaseLifecycleTerminationKind.NaturalExit,
                0,
                ValidOutputMeta(1),
                At(4)).Succeeded);
            return;
        }

        if (terminalKind == "started_unknown")
        {
            Assert.True(lifecycle.CompleteTermination(
                StandingLeaseLifecycleTerminationKind.NaturalExit,
                0,
                null,
                At(4)).Succeeded);
            return;
        }

        Assert.True(lifecycle.ObserveFirstFrame(new FirstFrameObservation
        {
            EvidenceKind = "test_first_frame",
            FrameNumber = 1,
            TotalSizeBytes = 4096,
        }, At(2)).Succeeded);
        Assert.True(lifecycle.CompleteTermination(
            terminalKind == "session_interrupted"
                ? StandingLeaseLifecycleTerminationKind.UserStop
                : StandingLeaseLifecycleTerminationKind.NaturalExit,
            terminalKind == "failed" ? 7 : -1,
            terminalKind == "failed" ? ValidOutputMeta(1) : null,
            At(4)).Succeeded);
    }

    private static (long LeaseVersion, long OccurrenceVersion, long RunVersion, long UseVersion, string? RunReason, string? OccurrenceReason, TimeSpan? ActualDuration) ReadRecoveryState(TemporaryDatabase database) =>
        (database.Leases.Get("lease-1").Version,
         database.Occurrences.Get("occ-1").Version,
         database.Runs.Get("run-1").Version,
         database.Uses.Get("use-1").Version,
         database.Runs.Get("run-1").TerminalReasonCode,
         database.Occurrences.Get("occ-1").TerminalReasonCode,
         database.Uses.Get("use-1").ActualSettledDuration);

    private static void AssertRecoveryState(
        TemporaryDatabase database,
        RecordingRunStatus runStatus,
        LeaseUseStatus useStatus,
        PlanOccurrenceStatus occurrenceStatus,
        string reason,
        TimeSpan? expectedActualDuration,
        long expectedRunVersion,
        long expectedUseVersion,
        long expectedOccurrenceVersion)
    {
        var run = database.Runs.Get("run-1");
        var use = database.Uses.Get("use-1");
        var occurrence = database.Occurrences.Get("occ-1");
        Assert.Equal(runStatus, run.Status);
        Assert.Equal(useStatus, use.Status);
        Assert.Equal(occurrenceStatus, occurrence.Status);
        Assert.Equal(reason, run.TerminalReasonCode);
        Assert.Equal(reason, occurrence.TerminalReasonCode);
        Assert.Equal(expectedActualDuration, use.ActualSettledDuration);
        Assert.Equal(expectedRunVersion, run.Version);
        Assert.Equal(expectedUseVersion, use.Version);
        Assert.Equal(expectedOccurrenceVersion, occurrence.Version);
    }

    private static StandingLeaseOneShotExecutionCoordinator CreateLifecycleCoordinator(
        TemporaryDatabase database,
        LifecycleCaptureBackend backend) =>
        new(
            database.Store,
            new FixedExecutionEnvironmentProvider(scope => EnvironmentAt(scope, At(2))),
            _ => backend,
            (_, _) => Task.CompletedTask,
            () => At(2));

    private static OutputMeta ValidOutputMeta(double durationSeconds) => new()
    {
        OutputFileExists = true,
        SizeBytes = 4096,
        DurationSeconds = durationSeconds,
    };

    private static string CaptureGateCode(Func<Phase3StartGateCommitResult> operation)
    {
        try
        {
            return operation().Code;
        }
        catch (Phase3PersistenceException exception)
        {
            return exception.Code;
        }
    }

    private static string CaptureActionCode(Action operation)
    {
        try
        {
            operation();
            return "success";
        }
        catch (Phase3PersistenceException exception)
        {
            return exception.Code;
        }
    }

    private static (long PlanVersion, long OccurrenceVersion, long LeaseVersion, long RunCount, long UseCount) ReadEvidenceState(TemporaryDatabase database, Phase3StartGateRequest request) =>
        (database.Plans.Get(request.PlanId).Version,
         database.Occurrences.Get(request.OccurrenceId).Version,
         database.Leases.Get(request.LeaseId).Version,
         RawScalar(database.Store.DatabasePath, "SELECT COUNT(*) FROM recording_runs;"),
         RawScalar(database.Store.DatabasePath, "SELECT COUNT(*) FROM lease_uses;"));

    private static void AssertNoStartGateRows(TemporaryDatabase database, bool expectActiveLease = true)
    {
        Assert.Equal(0L, RawScalar(database.Store.DatabasePath, "SELECT COUNT(*) FROM recording_runs;"));
        Assert.Equal(0L, RawScalar(database.Store.DatabasePath, "SELECT COUNT(*) FROM lease_uses;"));
        Assert.Equal(0L, RawScalar(database.Store.DatabasePath, "SELECT COUNT(*) FROM plan_occurrences WHERE run_id IS NOT NULL;"));
        Assert.Equal(expectActiveLease ? 1L : 0L, RawScalar(database.Store.DatabasePath, "SELECT COUNT(*) FROM consent_leases WHERE status_code = 'active';"));
    }

    private static DateTimeOffset At(int seconds) =>
        new DateTimeOffset(2035, 1, 1, 0, 0, 0, TimeSpan.Zero).AddSeconds(seconds);

    private static StandingLeaseOneShotExecutionRequest CreateOneShotRequest(
        TemporaryDatabase database,
        string runId = "run-1",
        string leaseUseId = "use-1") =>
        new(
            database.Scope.PlanId,
            database.Scope.OccurrenceId,
            database.Scope.LeaseId,
            runId,
            leaseUseId,
            0,
            0,
            0);

    private static Func<DateTimeOffset?> SequenceClock(params DateTimeOffset[] samples)
    {
        var remaining = new Queue<DateTimeOffset>(samples);
        return () => remaining.Count == 0
            ? throw new InvalidOperationException("The deterministic clock was sampled too many times.")
            : remaining.Dequeue();
    }

    private static StandingLeaseExecutionEnvironment EnvironmentAt(
        AuthorizedFixedRegionScope scope,
        DateTimeOffset nowUtc,
        string currentUserSid = "S-1-5-21-1",
        string sessionBinding = "session-1",
        bool isInteractiveDesktop = true) =>
        CompleteEnvironment(
            scope,
            new StandingLeaseExecutionEnvironment(
                nowUtc,
                currentUserSid,
                sessionBinding,
                isInteractiveDesktop));

    private static StandingLeaseExecutionEnvironment EnvironmentAt(
        DateTimeOffset nowUtc,
        string currentUserSid = "S-1-5-21-1",
        string sessionBinding = "session-1",
        bool isInteractiveDesktop = true) =>
        new(nowUtc, currentUserSid, sessionBinding, isInteractiveDesktop);

    private static (
        StandingLeaseUseProof Proof,
        StandingLeaseCaptureAuthorization Authorization,
        StandingLeaseCaptureSpecification Specification,
        StandingLeaseCaptureExecutionTicket Ticket) CreateStandingAuthorizationAndSpecification(
        TemporaryDatabase database,
        TimeSpan? duration = null)
    {
        var proof = database.Gate.Commit(
            database.CreateRequest(duration: duration ?? database.Scope.ReservedDuration)).FirstCommitProof!;
        Assert.NotNull(proof);
        Assert.True(database.ExecutionSnapshots.TryAuthorizeAndConsumeStandingLeaseUse(
            proof,
            EnvironmentAt(
                database.Scope,
                At(2),
                currentUserSid: database.Scope.CurrentUserSid,
                sessionBinding: database.Scope.SessionBinding),
            out var authorization,
            out var authorizationReason), authorizationReason);
        Assert.NotNull(authorization);
        Assert.True(StandingLeaseCaptureSpecification.TryCreate(
            authorization,
            out var specification,
            out var specificationReason), specificationReason);
        Assert.NotNull(specification);
        Assert.True(StandingLeaseCaptureExecutionTicket.TryCreate(
            authorization,
            specification,
            out var ticket,
            out var ticketReason), ticketReason);
        Assert.NotNull(ticket);
        return (proof, authorization!, specification!, ticket!);
    }

    private static StandingLeaseExecutionSnapshot CreateExecutionSnapshot(TemporaryDatabase database) =>
        new(
            database.Plans.Get("plan-1"),
            database.Occurrences.Get("occ-1"),
            database.Leases.Get("lease-1"),
            database.Scope,
            database.Runs.Get("run-1"),
            database.Uses.Get("use-1"));

    private static StandingLeaseExecutionEnvironment CompleteEnvironment(
        AuthorizedFixedRegionScope scope,
        StandingLeaseExecutionEnvironment environment,
        StandingLeaseDisplayMetadata? displayOverride = null,
        string? topologyDigestOverride = null,
        StandingLeaseOutputFileSystemSnapshot? outputOverride = null)
    {
        var display = displayOverride ?? new StandingLeaseDisplayMetadata(
            "display-1", scope.StableDisplayFingerprint, DisplayIdentityResolutionStatus.Resolved,
            scope.DisplayBounds, scope.DpiX, scope.DpiY, scope.PhysicalWidth,
            scope.PhysicalHeight, scope.Orientation);
        var directory = StandingLeaseOutputPath.NormalizeDirectory(scope.OutputDirectory);
        var filePath = System.IO.Path.GetFullPath(scope.OutputFilePath);
        var output = outputOverride ?? new StandingLeaseOutputFileSystemSnapshot(
            directory, filePath, directoryExists: true, frozenFileExists: false,
            directoryWritable: true, freeSpaceAvailable: true,
            availableFreeBytes: RecordingPreflightChecker.RequiredFreeSpaceBytes(scope.ReservedDuration),
            requiredFreeBytes: RecordingPreflightChecker.RequiredFreeSpaceBytes(scope.ReservedDuration));
        return new StandingLeaseExecutionEnvironment(
            environment.NowUtc,
            environment.CurrentUserSid,
            environment.SessionBinding,
            environment.IsInteractiveDesktop,
            new StandingLeaseDisplayMetadata[] { display },
            topologyDigestOverride ?? scope.TopologyDigest,
            output);
    }

    private static StandingLeaseDisplayMetadata ValidDisplay(AuthorizedFixedRegionScope scope) =>
        new(
            "display-1",
            scope.StableDisplayFingerprint,
            DisplayIdentityResolutionStatus.Resolved,
            scope.DisplayBounds,
            scope.DpiX,
            scope.DpiY,
            scope.PhysicalWidth,
            scope.PhysicalHeight,
            scope.Orientation);

    private static StandingLeaseExecutionEnvironment WithOutput(
        StandingLeaseExecutionEnvironment source,
        StandingLeaseOutputFileSystemSnapshot output) =>
        new(
            source.NowUtc,
            source.CurrentUserSid,
            source.SessionBinding,
            source.IsInteractiveDesktop,
            source.Displays,
            source.TopologyDigest,
            output);

    private sealed class FixedExecutionEnvironmentProvider : IStandingLeaseExecutionEnvironmentProvider
    {
        private readonly Func<AuthorizedFixedRegionScope, StandingLeaseExecutionEnvironment> capture;

        public FixedExecutionEnvironmentProvider(
            Func<AuthorizedFixedRegionScope, StandingLeaseExecutionEnvironment> capture)
        {
            this.capture = capture;
        }

        public StandingLeaseExecutionEnvironment Capture(AuthorizedFixedRegionScope scope) => capture(scope);
    }

    private sealed class CountingCaptureBackend : ICaptureBackend
    {
        private int startCalls;
        private int disposeCalls;

        public int StartCalls => Volatile.Read(ref startCalls);

        public int DisposeCalls => Volatile.Read(ref disposeCalls);

        public CaptureConfig? Configuration { get; private set; }

        public CaptureAuthorizationProof? AuthorizationProof { get; private set; }

        public bool ThrowOnStart { get; init; }

        public void Start(CaptureConfig cfg, CaptureAuthorizationProof authorizationProof)
        {
            Configuration = cfg;
            AuthorizationProof = authorizationProof;
            Interlocked.Increment(ref startCalls);
            if (ThrowOnStart)
                throw new InvalidOperationException("fake backend start failure");
        }

        public OutputMeta Stop() => new();

        public void Dispose() => Interlocked.Increment(ref disposeCalls);
    }

    private sealed class LifecycleCaptureBackend : ICaptureBackend, IFirstFrameObservableCaptureBackend, ICaptureEndedObservableBackend
    {
        private int startCalls;
        private int stopCalls;
        private int disposeCalls;
        private Action<int, OutputMeta>? naturalExit;

        public event Action<FirstFrameObservation>? FirstFrameObserved;

        public event Action<CaptureEndedObservation>? CaptureEnded;

        public int StartCalls => Volatile.Read(ref startCalls);

        public int StopCalls => Volatile.Read(ref stopCalls);

        public int DisposeCalls => Volatile.Read(ref disposeCalls);

        public bool EmitFirstFrameOnStart { get; init; }

        public bool EmitNaturalExitOnStart { get; init; }

        public bool ThrowAfterFirstFrameOnStart { get; init; }

        public int ExitCode { get; init; }

        public OutputMeta StopResult { get; init; } = ValidOutputMeta(1);

        public void Start(CaptureConfig cfg, CaptureAuthorizationProof authorizationProof)
        {
            Interlocked.Increment(ref startCalls);
            if (EmitFirstFrameOnStart)
                RaiseFirstFrame();
            if (EmitNaturalExitOnStart)
                RaiseNaturalExit(ExitCode, ValidOutputMeta(1));
            if (ThrowAfterFirstFrameOnStart)
                throw new InvalidOperationException("fake backend start failure after first frame");
        }

        public OutputMeta Stop()
        {
            Interlocked.Increment(ref stopCalls);
            naturalExit?.Invoke(ExitCode, StopResult);
            return StopResult;
        }

        public void OnNaturalExit(Action<int, OutputMeta> callback) => naturalExit = callback;

        public void RaiseFirstFrame() => FirstFrameObserved?.Invoke(new FirstFrameObservation
        {
            EvidenceKind = "test_first_frame",
            FrameNumber = 1,
            TotalSizeBytes = 4096,
        });

        public void RaiseCaptureEnded() => CaptureEnded?.Invoke(new CaptureEndedObservation
        {
            ExitCode = ExitCode,
            Reason = "natural",
            EndedAtUtc = At(2).UtcDateTime,
        });

        public void RaiseNaturalExit(int exitCode, OutputMeta meta) => naturalExit?.Invoke(exitCode, meta);

        public void Dispose() => Interlocked.Increment(ref disposeCalls);
    }

    private static StandingLeaseUseProof CloneStandingProof(
        StandingLeaseUseProof proof,
        string? runId = null,
        string? authorizationSourceId = null,
        string? capturePlanDigest = null,
        string? scopeDigest = null,
        string? currentUserSid = null,
        string? sessionBinding = null,
        string? leaseId = null,
        string? leaseUseId = null,
        TimeSpan? maxDuration = null) =>
        new(
            proofId: proof.ProofId + "-clone-" + Guid.NewGuid().ToString("N"),
            runId: runId ?? proof.RunId,
            authorizationSourceId: authorizationSourceId ?? proof.AuthorizationSourceId,
            capturePlanDigest: capturePlanDigest ?? proof.CapturePlanDigest,
            scopeDigest: scopeDigest ?? proof.ScopeDigest,
            issuedAtUtc: proof.IssuedAtUtc,
            expiresAtUtc: proof.ExpiresAtUtc,
            currentUserSid: currentUserSid ?? proof.CurrentUserSid,
            sessionBinding: sessionBinding ?? proof.SessionBinding,
            leaseId: leaseId ?? proof.LeaseId,
            leaseUseId: leaseUseId ?? proof.LeaseUseId,
            oneTimeNonce: Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(16)).ToLowerInvariant(),
            maxDuration: maxDuration ?? proof.MaxDuration);

    private static void RawExecute(string databasePath, string sql, params (string Name, object Value)[] parameters)
    {
        using var connection = OpenRaw(databasePath);
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        command.ExecuteNonQuery();
    }

    private static long RawScalar(string databasePath, string sql, params (string Name, object Value)[] parameters)
    {
        using var connection = OpenRaw(databasePath);
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        return Convert.ToInt64(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
    }

    private static string? RawText(string databasePath, string sql, params (string Name, object Value)[] parameters)
    {
        using var connection = OpenRaw(databasePath);
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        return command.ExecuteScalar() as string;
    }

    private static SqliteConnection OpenRaw(string databasePath)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWrite,
            Cache = SqliteCacheMode.Default,
            Pooling = false,
        }.ToString());
        connection.Open();
        return connection;
    }

    private sealed class TestAuditLogger : AuditLogger
    {
        public override void Log(string evt, object payload) { }
    }

    private sealed class StandingEngineTestTray : ITrayContext
    {
        public int RecordingCalls { get; private set; }

        public string HostMode => "headless";
        public bool SupportsRegionSelectionUi => false;
        public void RequestConfirmation(RecordingConfirmationPresentation presentation, Action<ConfirmationDecision> callback) { }
        public void RequestRegionSelection(int timeoutSeconds, Action<string, int, int, int, int, string, string> callback) { }
        public void SetRecording(RecordingUiPresentation presentation) => RecordingCalls++;
        public void SetIdle(RecordingUiPresentation presentation) { }
        public void SetAllIdle() { }
        public void ShowError(string text) { }
    }

    private sealed class StandingDisplayTopologyProvider : IDisplayTopologyProvider
    {
        private readonly AuthorizedFixedRegionScope scope;

        public StandingDisplayTopologyProvider(AuthorizedFixedRegionScope scope) => this.scope = scope;

        public IReadOnlyList<DisplayTopologySnapshot> GetCurrentDisplays() => new[]
        {
            new DisplayTopologySnapshot(
                "display-1",
                scope.StableDisplayFingerprint,
                DisplayIdentityResolutionStatus.Resolved,
                new CapturePlanBounds(
                    scope.DisplayBounds.X,
                    scope.DisplayBounds.Y,
                    scope.DisplayBounds.Width,
                    scope.DisplayBounds.Height))
        };
    }

    private sealed class TemporaryDatabase : IDisposable
    {
        private readonly TemporaryDirectory directory = new();
        private readonly string planId;

        public TemporaryDatabase(
            string planId = "plan-1",
            PlanDefinitionStatus planStatus = PlanDefinitionStatus.Enabled,
            PlanOccurrenceStatus occurrenceStatus = PlanOccurrenceStatus.Authorized,
            ConsentLeaseStatus leaseStatus = ConsentLeaseStatus.Active,
            int occurrenceEndSeconds = 60,
            int leaseEndSeconds = 120,
            TimeSpan? scopeDuration = null,
            Action? snapshotLoadedBeforeCoreGateForTest = null,
            Action<SqliteConnection>? beforeCommitForTest = null,
            IStandingLeaseExecutionEnvironmentProvider? environmentProviderForTest = null,
            int scopeCountdownSeconds = 0,
            string scopeSessionBinding = "session-1")
        {
            this.planId = planId;
            Store = new SqliteOperationalStore(Path.Combine(directory.Path, "state", "agent-recorder.db"));
            Directory.CreateDirectory(Path.GetDirectoryName(Store.DatabasePath)!);
            Store.Initialize();
            // Schema v6 is intentionally default-off. These legacy execution
            // tests exercise the already-enabled standing-lease path, so make
            // that fixture choice explicit instead of relying on the default.
            RawExecute(Store.DatabasePath,
                "UPDATE unattended_safety_state SET unattended_mode_code = 'enabled' WHERE state_id = 'global';");
            Plans = new SqlitePlanDefinitionRepository(Store);
            Occurrences = new SqlitePlanOccurrenceRepository(Store);
            Runs = new SqliteRecordingRunRepository(Store);
            Leases = new SqliteConsentLeaseRepository(Store);
            Uses = new SqliteLeaseUseRepository(Store);

            var plan = PlanDefinition.Rehydrate(planId, true, planStatus, At(0), At(0), 0);
            Plans.Insert(plan);
            var occurrence = PlanOccurrence.Rehydrate("occ-1", planId, At(0), At(occurrenceEndSeconds), At(0), occurrenceStatus, null, null, At(0), 0);
            Occurrences.Insert(occurrence);
            var lease = ConsentLease.Rehydrate("lease-1", planId, occurrence.Id, At(0), At(leaseEndSeconds), 1, TimeSpan.FromSeconds(30), ConsentLeaseStatus.Active, At(0), 0);
            Leases.Insert(lease);

            Scopes = new SqliteAuthorizedCaptureScopeRepository(Store);
            Scope = CreateScope(scopeDuration ?? TimeSpan.FromSeconds(5), scopeCountdownSeconds, scopeSessionBinding);
            Scopes.Insert(Scope);
            if (leaseStatus != ConsentLeaseStatus.Active)
            {
                RawExecute(Store.DatabasePath, "UPDATE consent_leases SET status_code = $status_code WHERE id = $id;",
                    ("$status_code", Phase3StateCodes.ToCode(leaseStatus)), ("$id", lease.Id));
            }

            Gate = new SqlitePhase3StartGateTransaction(Store);
            ExecutionSnapshots = new SqliteStandingLeaseExecutionSnapshotLoader(
                Store,
                snapshotLoadedBeforeCoreGateForTest,
                beforeCommitForTest,
                environmentProviderForTest);
        }

        public SqliteOperationalStore Store { get; }

        public SqlitePlanDefinitionRepository Plans { get; }

        public SqlitePlanOccurrenceRepository Occurrences { get; }

        public SqliteRecordingRunRepository Runs { get; }

        public SqliteConsentLeaseRepository Leases { get; }

        public SqliteAuthorizedCaptureScopeRepository Scopes { get; }

        public AuthorizedFixedRegionScope Scope { get; }

        public SqliteLeaseUseRepository Uses { get; }

        public SqlitePhase3StartGateTransaction Gate { get; }

        public SqliteStandingLeaseExecutionSnapshotLoader ExecutionSnapshots { get; }

        public string RootPath => directory.Path;

        public Phase3StartGateRequest CreateRequest(
            string runId = "run-1",
            string leaseUseId = "use-1",
            DateTimeOffset? commitAt = null,
            TimeSpan? duration = null) => new(
                planId,
                "occ-1",
                "lease-1",
                Scope.ScopeId,
                Scope.ScopeDigest,
                runId,
                leaseUseId,
                0,
                0,
                0,
                duration ?? TimeSpan.FromSeconds(5),
                commitAt ?? At(1));

        private AuthorizedFixedRegionScope CreateScope(TimeSpan duration, int countdownSeconds, string sessionBinding) =>
            AuthorizedFixedRegionScope.CreateFor(
                Plans.Get(planId),
                Occurrences.Get("occ-1"),
                Leases.Get("lease-1"),
                "scope-1",
                At(0),
                AuthorizedScopeTargetType.FixedRegion,
                AuthorizedCaptureSemantics.DesktopRegion,
                AuthorizedCoordinateSpace.PhysicalVirtualScreen,
                AuthorizedDisplayIdentityStatus.Resolved,
                "display-fingerprint-1",
                new AuthorizedPhysicalRectangle(0, 0, 1920, 1080),
                new AuthorizedPhysicalRectangle(10, 10, 640, 480),
                96,
                96,
                1920,
                1080,
                AuthorizedDisplayOrientation.Landscape,
                AuthorizedCaptureBackend.FfmpegRegion,
                AuthorizedAudioMode.None,
                duration,
                countdownSeconds,
                Path.Combine(RootPath, "output"),
                "capture.mp4",
                AuthorizedOutputConflictPolicy.FailIfExists,
                AuthorizedWakePolicy.NaturalWakeOnly,
                AuthorizedDesktopRequirement.InteractiveDesktopRequired,
                "S-1-5-21-1",
                sessionBinding,
                "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");

        public void Dispose() => directory.Dispose();
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "AgentRecorderStartGate_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            IOException? lastIoException = null;
            for (var attempt = 0; attempt < 40; attempt++)
            {
                try
                {
                    Directory.Delete(Path, recursive: true);
                    return;
                }
                catch (DirectoryNotFoundException)
                {
                    return;
                }
                catch (IOException exception)
                {
                    lastIoException = exception;
                    Thread.Sleep(50);
                }
            }

            throw lastIoException ?? new IOException($"Could not delete temporary directory '{Path}'.");
        }
    }
}

// Keeps the Task 230R tests explicit about their injected environment while
// leaving the production loader overload provider-driven.
internal static class StandingLeaseExecutionLoaderTestExtensions
{
    internal static bool TryAuthorizeAndConsumeStandingLeaseUse(
        this SqliteStandingLeaseExecutionSnapshotLoader loader,
        CaptureAuthorizationProof? proof,
        StandingLeaseExecutionEnvironment? environment,
        out StandingLeaseCaptureAuthorization? authorization,
        out string failureReason) =>
        loader.TryAuthorizeAndConsumeStandingLeaseUseForTest(
            proof,
            environment,
            out authorization,
            out failureReason);
}
