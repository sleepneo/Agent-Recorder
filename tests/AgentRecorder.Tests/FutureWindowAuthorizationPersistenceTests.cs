using AgentRecorder.Core;
using AgentRecorder.Core.Automation;
using AgentRecorder.Capture;
using AgentRecorder.Infrastructure;
using AgentRecorder.Persistence;
using AgentRecorder.Windows;
using Xunit;

namespace AgentRecorder.Tests;

[Collection("NonParallel-AgentRecorderDataDir")]
public sealed class FutureWindowAuthorizationPersistenceTests
{
    private const string Sid = "S-1-5-21-100-200-300-1001";
    private const string Session = "session-future-window-test";

    [Fact]
    public void PendingSetupIsIdempotentAndConflictingPayloadCannotReplaceIt()
    {
        using var database = new TemporaryDatabase();
        database.Store.Initialize();
        var repository = new SqliteFutureWindowAuthorizationRepository(database.Store);
        var now = DateTimeOffset.UtcNow;
        var first = CreateScope("fwa_" + Guid.NewGuid().ToString("N"), new string('a', 64), now);

        var created = repository.CreateOrGet(first);
        var replay = repository.CreateOrGet(first with
        {
            AuthorizationId = "fwa_" + Guid.NewGuid().ToString("N"),
            CreatedAtUtc = now.AddSeconds(1),
        });
        var attemptedWidening = repository.CreateOrGet(first with
        {
            AuthorizationId = "fwa_" + Guid.NewGuid().ToString("N"),
            CreatedAtUtc = now.AddSeconds(2),
            MaximumDurationSeconds = 1800,
            ValiditySeconds = 3600,
        });
        var conflict = repository.CreateOrGet(first with
        {
            AuthorizationId = "fwa_" + Guid.NewGuid().ToString("N"),
            RequestDigest = "b".PadLeft(64, 'b'),
            MaximumDurationSeconds = 1800,
            ValiditySeconds = 3600,
        });

        Assert.Equal(FutureWindowCreateDisposition.Created, created.Disposition);
        Assert.Equal(FutureWindowCreateDisposition.Existing, replay.Disposition);
        Assert.Equal(first.AuthorizationId, replay.Row!.AuthorizationId);
        Assert.Equal(FutureWindowCreateDisposition.Existing, attemptedWidening.Disposition);
        Assert.Equal(60, attemptedWidening.Row!.MaximumDurationSeconds);
        Assert.Equal(FutureWindowCreateDisposition.Conflict, conflict.Disposition);
        Assert.Equal("pending", repository.Get(first.AuthorizationId, Sid, Session)!.StatusCode);
        Assert.Single(repository.List(Sid, Session));
    }

    [Fact]
    public void OnlyApprovedPendingSetupCanBeConsumedAndConsumptionIsOneUse()
    {
        using var database = new TemporaryDatabase();
        database.Store.Initialize();
        var repository = new SqliteFutureWindowAuthorizationRepository(database.Store);
        var createdAt = DateTimeOffset.UtcNow.AddSeconds(-2);
        var scope = CreateScope("fwa_" + Guid.NewGuid().ToString("N"), new string('c', 64), createdAt);
        repository.CreateOrGet(scope);
        var process = ProcessFor(scope, createdAt.AddSeconds(2));

        Assert.Null(repository.TryCommitStart(scope.AuthorizationId, Sid, Session, process,
            createdAt.AddSeconds(1), out var pendingReason));
        Assert.Equal("future_window_not_active", pendingReason);

        var approved = repository.Approve(scope.AuthorizationId, Sid, Session,
            "approval-" + Guid.NewGuid().ToString("N"), createdAt.AddSeconds(1));
        Assert.Equal("active", approved!.StatusCode);
        var receipt = repository.TryCommitStart(scope.AuthorizationId, Sid, Session, process,
            createdAt.AddSeconds(3), out var commitReason);

        Assert.NotNull(receipt);
        Assert.Equal(string.Empty, commitReason);
        Assert.Equal("used", repository.Get(scope.AuthorizationId, Sid, Session)!.StatusCode);
        Assert.Null(repository.TryCommitStart(scope.AuthorizationId, Sid, Session, process,
            createdAt.AddSeconds(4), out var replayReason));
        Assert.Equal("future_window_not_active", replayReason);
    }

    [Fact]
    public void ExpiredGrantIsTerminalAndCannotBeConsumed()
    {
        using var database = new TemporaryDatabase();
        database.Store.Initialize();
        var repository = new SqliteFutureWindowAuthorizationRepository(database.Store);
        var createdAt = DateTimeOffset.UtcNow.AddSeconds(-10);
        var scope = CreateScope("fwa_" + Guid.NewGuid().ToString("N"), new string('d', 64), createdAt) with
        { MaximumDurationSeconds = 1, ValiditySeconds = 2 };
        repository.CreateOrGet(scope);
        repository.Approve(scope.AuthorizationId, Sid, Session, "approval-expiry", createdAt.AddSeconds(1));

        var receipt = repository.TryCommitStart(scope.AuthorizationId, Sid, Session,
            ProcessFor(scope, createdAt.AddSeconds(3)), createdAt.AddSeconds(4), out var reason);

        Assert.Null(receipt);
        Assert.Equal("future_window_expired", reason);
        Assert.Equal("expired", repository.Get(scope.AuthorizationId, Sid, Session)!.StatusCode);
    }

    [Fact]
    public void SetupRejectsValidityShorterThanTheFullApprovedRun()
    {
        using var database = new TemporaryDatabase();
        database.Store.Initialize();
        var repository = new SqliteFutureWindowAuthorizationRepository(database.Store);
        var scope = CreateScope("fwa_" + Guid.NewGuid().ToString("N"), new string('3', 64), DateTimeOffset.UtcNow)
            with { MaximumDurationSeconds = 60, ValiditySeconds = 59 };

        var failure = Assert.Throws<ArgumentException>(() => repository.CreateOrGet(scope));

        Assert.Contains("complete approved run", failure.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(repository.List(Sid, Session));
    }

    [Fact]
    public void AtomicCommitAcceptsExactLatestStartAndRejectsAnyLaterStartWithoutShortening()
    {
        using var database = new TemporaryDatabase();
        database.Store.Initialize();
        var repository = new SqliteFutureWindowAuthorizationRepository(database.Store);
        var createdAt = DateTimeOffset.UtcNow.AddMinutes(-10);
        var exact = CreateScope("fwa_" + Guid.NewGuid().ToString("N"), new string('4', 64), createdAt)
            with { MaximumDurationSeconds = 1800, ValiditySeconds = 3600 };
        repository.CreateOrGet(exact);
        var approvedAt = createdAt.AddSeconds(1);
        var activated = repository.Approve(exact.AuthorizationId, Sid, Session, "approval-exact-start", approvedAt)!;
        var latestStart = activated.ExpiresAtUtc!.Value.AddSeconds(-activated.MaximumDurationSeconds);

        var receipt = repository.TryCommitStart(exact.AuthorizationId, Sid, Session,
            ProcessFor(exact, approvedAt.AddSeconds(1)), latestStart, out var exactReason);

        Assert.NotNull(receipt);
        Assert.Equal(string.Empty, exactReason);
        var ticket = TicketFor(receipt!);
        Assert.Null(repository.ValidateAtBackendStart(ticket, latestStart));

        var late = CreateScope("fwa_" + Guid.NewGuid().ToString("N"), new string('5', 64), createdAt)
            with { MaximumDurationSeconds = 1800, ValiditySeconds = 3600 };
        repository.CreateOrGet(late);
        var lateApproval = createdAt.AddSeconds(2);
        var lateActivated = repository.Approve(late.AuthorizationId, Sid, Session, "approval-late-start", lateApproval)!;
        var oneSecondLate = lateActivated.ExpiresAtUtc!.Value.AddSeconds(-late.MaximumDurationSeconds + 1);

        var rejected = repository.TryCommitStart(late.AuthorizationId, Sid, Session,
            ProcessFor(late, lateApproval.AddSeconds(1)), oneSecondLate, out var lateReason);

        Assert.Null(rejected);
        Assert.Equal("future_window_run_would_exceed_expiry", lateReason);
        var persisted = repository.Get(late.AuthorizationId, Sid, Session)!;
        Assert.Equal("expired", persisted.StatusCode);
        Assert.Equal("authorization_start_window_expired", persisted.ReasonCode);
        Assert.Equal(1800, persisted.MaximumDurationSeconds);
    }

    [Fact]
    public void ExpiryBetweenConsumeAndFinalBackendGateIsRejected()
    {
        using var database = new TemporaryDatabase();
        database.Store.Initialize();
        var repository = new SqliteFutureWindowAuthorizationRepository(database.Store);
        var createdAt = DateTimeOffset.UtcNow.AddMinutes(-10);
        var scope = CreateScope("fwa_" + Guid.NewGuid().ToString("N"), new string('6', 64), createdAt)
            with { MaximumDurationSeconds = 60, ValiditySeconds = 300 };
        repository.CreateOrGet(scope);
        var approvedAt = createdAt.AddSeconds(1);
        repository.Approve(scope.AuthorizationId, Sid, Session, "approval-final-expiry", approvedAt);
        var receipt = repository.TryCommitStart(scope.AuthorizationId, Sid, Session,
            ProcessFor(scope, approvedAt.AddSeconds(1)), approvedAt.AddSeconds(2), out var commitReason);
        Assert.NotNull(receipt);
        Assert.Equal(string.Empty, commitReason);
        var ticket = TicketFor(receipt!);
        var justTooLate = receipt!.Authorization.ExpiresAtUtc!.Value
            .AddSeconds(-receipt.Authorization.MaximumDurationSeconds).AddTicks(1);

        var finalFailure = repository.ValidateAtBackendStart(ticket, justTooLate);

        Assert.Equal("future_window_run_would_exceed_expiry", finalFailure);
        Assert.Equal("start_committed", repository.Get(scope.AuthorizationId, Sid, Session)!.RunStatus);
    }

    [Fact]
    public void StopAllRevokesPendingAndApprovedFutureGrantsOnlyForCurrentSidAndSession()
    {
        using var database = new TemporaryDatabase();
        database.Store.Initialize();
        var repository = new SqliteFutureWindowAuthorizationRepository(database.Store);
        var now = DateTimeOffset.UtcNow;
        var createdAt = now.AddMinutes(-3);
        var pending = CreateScope("fwa_" + Guid.NewGuid().ToString("N"), new string('a', 64), createdAt);
        var active = CreateScope("fwa_" + Guid.NewGuid().ToString("N"), new string('b', 64), createdAt);
        var otherSession = CreateScope("fwa_" + Guid.NewGuid().ToString("N"), new string('c', 64), createdAt)
            with { SessionBinding = "other-session" };
        repository.CreateOrGet(pending);
        repository.CreateOrGet(active);
        repository.CreateOrGet(otherSession);
        repository.Approve(active.AuthorizationId, Sid, Session, "approval-stop-all-active", createdAt.AddSeconds(1));
        repository.Approve(otherSession.AuthorizationId, Sid, "other-session", "approval-other-session", createdAt.AddSeconds(1));
        var service = new StandingLeaseSafetyControlService(database.Store, () => now);

        var result = service.StopAllAndRevokeAll("task297r-stop-all", "task297r-test", Sid, Session);

        Assert.Equal(StandingLeaseSafetyControlResultStatus.Changed, result.Status);
        Assert.True(result.DurableOperationCommitted);
        Assert.Equal("revoked", repository.Get(pending.AuthorizationId, Sid, Session)!.StatusCode);
        Assert.Equal("revoked", repository.Get(active.AuthorizationId, Sid, Session)!.StatusCode);
        Assert.Equal("active", repository.Get(otherSession.AuthorizationId, Sid, "other-session")!.StatusCode);
    }

    [Fact]
    public void DisableRevokesApprovedGrantAndReenableDoesNotResurrectIt()
    {
        using var database = new TemporaryDatabase();
        database.Store.Initialize();
        var repository = new SqliteFutureWindowAuthorizationRepository(database.Store);
        var now = DateTimeOffset.UtcNow;
        var createdAt = now.AddMinutes(-3);
        var scope = CreateScope("fwa_" + Guid.NewGuid().ToString("N"), new string('d', 64), createdAt);
        repository.CreateOrGet(scope);
        repository.Approve(scope.AuthorizationId, Sid, Session, "approval-disable", createdAt.AddSeconds(1));
        var clockOffset = 0;
        var service = new StandingLeaseSafetyControlService(database.Store,
            () => now.AddSeconds(Interlocked.Increment(ref clockOffset)));

        var disabled = service.DisableUnattended("task297r-disable", "task297r-test", Sid, Session);
        var afterDisable = repository.Get(scope.AuthorizationId, Sid, Session)!;
        var enabled = service.EnableUnattended("task297r-reenable", "task297r-test");
        var commitAfterReenable = repository.TryCommitStart(scope.AuthorizationId, Sid, Session,
            ProcessFor(scope, createdAt.AddSeconds(2)), now.AddSeconds(2), out var reason);

        Assert.Equal(StandingLeaseSafetyControlResultStatus.Changed, disabled.Status);
        Assert.Equal("revoked", afterDisable.StatusCode);
        Assert.Equal(StandingLeaseSafetyControlResultStatus.Changed, enabled.Status);
        Assert.Null(commitAfterReenable);
        Assert.Equal("future_window_not_active", reason);
        Assert.Equal("enabled", ReadSafetyMode(database.Store));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void GlobalSafetyControlRequestsStopForConsumedFutureRun(bool useStopAll)
    {
        using var database = new TemporaryDatabase();
        database.Store.Initialize();
        var repository = new SqliteFutureWindowAuthorizationRepository(database.Store);
        var now = DateTimeOffset.UtcNow;
        var createdAt = now.AddMinutes(-3);
        var scope = CreateScope("fwa_" + Guid.NewGuid().ToString("N"), new string('e', 64), createdAt);
        repository.CreateOrGet(scope);
        repository.Approve(scope.AuthorizationId, Sid, Session, "approval-active-run", createdAt.AddSeconds(1));
        var receipt = repository.TryCommitStart(scope.AuthorizationId, Sid, Session,
            ProcessFor(scope, createdAt.AddSeconds(2)), createdAt.AddSeconds(3), out _)!;
        var stopper = new FakeActiveRunStopper();
        var service = new StandingLeaseSafetyControlService(
            database.Store, () => now, activeRunStopper: stopper,
            startSafetyInterlock: new StandingLeaseStartSafetyInterlock());

        var result = useStopAll
            ? service.StopAllAndRevokeAll("task297r-active-stop", "task297r-test", Sid, Session)
            : service.DisableUnattended("task297r-active-disable", "task297r-test", Sid, Session);

        Assert.Equal(StandingLeaseSafetyControlResultStatus.Changed, result.Status);
        Assert.True(result.RequiresActiveRunStop);
        Assert.True(result.DurableOperationCommitted);
        Assert.Equal(1, stopper.StopAllCalls);
        var revoked = repository.Get(scope.AuthorizationId, Sid, Session)!;
        Assert.Equal("revoked", revoked.StatusCode);
        Assert.Equal(receipt.RunId, revoked.RunId);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task GlobalSafetyWinningBetweenCommitAndBackendGatePreventsPhysicalStart(bool useStopAll)
    {
        using var database = new TemporaryDatabase();
        database.Store.Initialize();
        var repository = new SqliteFutureWindowAuthorizationRepository(database.Store);
        var createdAt = DateTimeOffset.UtcNow.AddMinutes(-3);
        var scope = CreateScope("fwa_" + Guid.NewGuid().ToString("N"), new string('f', 64), createdAt);
        repository.CreateOrGet(scope);
        repository.Approve(scope.AuthorizationId, Sid, Session, "approval-safety-race", createdAt.AddSeconds(1));
        var process = ProcessFor(scope, createdAt.AddSeconds(2));
        var interlock = new StandingLeaseStartSafetyInterlock();
        var transaction = new SqliteStandingLeaseSafetyControlTransaction(database.Store);
        using var committed = new ManualResetEventSlim(false);
        using var allowFinalGate = new ManualResetEventSlim(false);
        var backendStarts = 0;

        var start = Task.Run(() =>
        {
            var receipt = interlock.Execute("future_window_start_commit", () =>
                repository.TryCommitStart(scope.AuthorizationId, Sid, Session, process,
                    createdAt.AddSeconds(3), out _));
            committed.Set();
            allowFinalGate.Wait();
            var failure = interlock.Execute("future_window_backend_start", () =>
                repository.ValidateAtBackendStart(TicketFor(receipt!), createdAt.AddSeconds(4)));
            if (failure is null) Interlocked.Increment(ref backendStarts);
            return (receipt, failure);
        });

        Assert.True(committed.Wait(TimeSpan.FromSeconds(5)));
        var safety = Task.Run(() => interlock.Execute("global_safety_control", () => useStopAll
            ? transaction.StopAllAndRevokeAll("task297r-race-stop", "task297r-test", createdAt.AddSeconds(4), Sid, Session)
            : transaction.SetUnattendedMode("task297r-race-disable", false, "task297r-test", createdAt.AddSeconds(4), Sid, Session)));
        var safetyResult = await safety;
        allowFinalGate.Set();
        var outcome = await start;

        Assert.NotNull(outcome.receipt);
        Assert.Equal(0, backendStarts);
        Assert.NotNull(outcome.failure);
        Assert.Contains(outcome.failure, new[] { "unattended_disabled", "future_window_stop_all_applied" });
        Assert.True(safetyResult.DurableOperationCommitted);
    }

    [Fact]
    public void StopAllPersistenceFailureDoesNotClaimGrantWasRevoked()
    {
        using var database = new TemporaryDatabase();
        database.Store.Initialize();
        var repository = new SqliteFutureWindowAuthorizationRepository(database.Store);
        var now = DateTimeOffset.UtcNow;
        var createdAt = now.AddMinutes(-3);
        var scope = CreateScope("fwa_" + Guid.NewGuid().ToString("N"), new string('7', 64), createdAt);
        repository.CreateOrGet(scope);
        repository.Approve(scope.AuthorizationId, Sid, Session, "approval-failure", createdAt.AddSeconds(1));
        var service = new StandingLeaseSafetyControlService(
            database.Store,
            () => now,
            beforeCommitForTest: (_, _) => throw new InvalidOperationException("injected transaction failure"));

        var result = service.StopAllAndRevokeAll("task297r-failed-stop", "task297r-test", Sid, Session);

        Assert.Equal(StandingLeaseSafetyControlResultStatus.Rejected, result.Status);
        Assert.False(result.DurableOperationCommitted);
        Assert.Equal("active", repository.Get(scope.AuthorizationId, Sid, Session)!.StatusCode);
    }

    [Fact]
    public void RevokeWinsBeforeRunCommitAndRestartMarksUncertainCommitUnknownWithoutRetry()
    {
        using var database = new TemporaryDatabase();
        database.Store.Initialize();
        var repository = new SqliteFutureWindowAuthorizationRepository(database.Store);
        var createdAt = DateTimeOffset.UtcNow.AddSeconds(-3);

        var revokedScope = CreateScope("fwa_" + Guid.NewGuid().ToString("N"), new string('e', 64), createdAt);
        repository.CreateOrGet(revokedScope);
        repository.Approve(revokedScope.AuthorizationId, Sid, Session, "approval-revoke", createdAt.AddSeconds(1));
        Assert.Equal("revoked", repository.Revoke(revokedScope.AuthorizationId, Sid, Session, createdAt.AddSeconds(2))!.StatusCode);
        Assert.Null(repository.TryCommitStart(revokedScope.AuthorizationId, Sid, Session,
            ProcessFor(revokedScope, createdAt.AddSeconds(2)), createdAt.AddSeconds(2), out var revokedReason));
        Assert.Equal("future_window_not_active", revokedReason);

        var crashScope = CreateScope("fwa_" + Guid.NewGuid().ToString("N"), new string('f', 64), createdAt);
        repository.CreateOrGet(crashScope);
        repository.Approve(crashScope.AuthorizationId, Sid, Session, "approval-crash", createdAt.AddSeconds(1));
        var committed = repository.TryCommitStart(crashScope.AuthorizationId, Sid, Session,
            ProcessFor(crashScope, createdAt.AddSeconds(2)), createdAt.AddSeconds(2), out var commitReason);
        Assert.True(committed is not null, commitReason);

        repository.RecoverUncertainStarts(createdAt.AddSeconds(3));

        var recovered = repository.Get(crashScope.AuthorizationId, Sid, Session)!;
        Assert.Equal("used", recovered.StatusCode);
        Assert.Equal("started_unknown", recovered.RunStatus);
        Assert.Null(repository.TryCommitStart(crashScope.AuthorizationId, Sid, Session,
            ProcessFor(crashScope, createdAt.AddSeconds(4)), createdAt.AddSeconds(4), out var retryReason));
        Assert.Equal("future_window_not_active", retryReason);
    }

    [Fact]
    public async Task ConcurrentRevokeAndRunCommitAlwaysLeaveAuthorizationRevoked()
    {
        using var database = new TemporaryDatabase();
        database.Store.Initialize();
        var repository = new SqliteFutureWindowAuthorizationRepository(database.Store);
        var createdAt = DateTimeOffset.UtcNow.AddSeconds(-3);
        var scope = CreateScope("fwa_" + Guid.NewGuid().ToString("N"), new string('2', 64), createdAt);
        repository.CreateOrGet(scope);
        repository.Approve(scope.AuthorizationId, Sid, Session, "approval-race", createdAt.AddSeconds(1));
        using var start = new ManualResetEventSlim(false);

        var consume = Task.Run(() =>
        {
            start.Wait();
            var receipt = repository.TryCommitStart(scope.AuthorizationId, Sid, Session,
                ProcessFor(scope, createdAt.AddSeconds(2)), createdAt.AddSeconds(2), out var reason);
            return (receipt, reason);
        });
        var revoke = Task.Run(() =>
        {
            start.Wait();
            return repository.Revoke(scope.AuthorizationId, Sid, Session, createdAt.AddSeconds(2));
        });
        start.Set();
        await Task.WhenAll(consume, revoke);
        var consumed = await consume;
        var revoked = await revoke;

        var final = repository.Get(scope.AuthorizationId, Sid, Session)!;
        Assert.Equal("revoked", final.StatusCode);
        Assert.Equal("revoked", revoked!.StatusCode);
        if (consumed.receipt is null)
        {
            Assert.Equal("future_window_not_active", consumed.reason);
            Assert.Null(final.RunId);
        }
        else
        {
            Assert.Equal(consumed.receipt.RunId, final.RunId);
            Assert.Equal("start_committed", final.RunStatus);
        }
    }

    [Fact]
    public async Task ConcurrentRunCommitsConsumeExactlyOneUse()
    {
        using var database = new TemporaryDatabase();
        database.Store.Initialize();
        var repository = new SqliteFutureWindowAuthorizationRepository(database.Store);
        var createdAt = DateTimeOffset.UtcNow.AddSeconds(-2);
        var scope = CreateScope("fwa_" + Guid.NewGuid().ToString("N"), new string('9', 64), createdAt);
        repository.CreateOrGet(scope);
        repository.Approve(scope.AuthorizationId, Sid, Session, "approval-concurrent", createdAt.AddSeconds(1));

        var outcomes = await Task.WhenAll(Enumerable.Range(0, 8).Select(index => Task.Run(() =>
        {
            var process = ProcessFor(scope, createdAt.AddSeconds(2)) with { ProcessId = 100 + index };
            var receipt = repository.TryCommitStart(scope.AuthorizationId, Sid, Session,
                process, createdAt.AddSeconds(2), out var reason);
            return (receipt, reason);
        })));

        Assert.Single(outcomes, outcome => outcome.receipt is not null);
        Assert.Equal("used", repository.Get(scope.AuthorizationId, Sid, Session)!.StatusCode);
        Assert.All(outcomes.Where(outcome => outcome.receipt is null), outcome =>
            Assert.Contains(outcome.reason, new[] { "future_window_not_active", "future_window_start_race_lost" }));
    }

    [Fact]
    public void ProcessUserMismatchCannotConsumeTheAuthorization()
    {
        using var database = new TemporaryDatabase();
        database.Store.Initialize();
        var repository = new SqliteFutureWindowAuthorizationRepository(database.Store);
        var createdAt = DateTimeOffset.UtcNow.AddSeconds(-2);
        var scope = CreateScope("fwa_" + Guid.NewGuid().ToString("N"), new string('8', 64), createdAt);
        repository.CreateOrGet(scope);
        repository.Approve(scope.AuthorizationId, Sid, Session, "approval-identity", createdAt.AddSeconds(1));

        var receipt = repository.TryCommitStart(scope.AuthorizationId, Sid, Session,
            ProcessFor(scope, createdAt.AddSeconds(2)) with { ProcessUserSid = "S-1-5-21-other" },
            createdAt.AddSeconds(2), out var reason);

        Assert.Null(receipt);
        Assert.Equal("future_window_process_identity_mismatch", reason);
        Assert.Equal("active", repository.Get(scope.AuthorizationId, Sid, Session)!.StatusCode);
    }

    [Fact]
    public void AuthorizationDigestBindsExecutableAudioDurationValidityAndFrozenOutput()
    {
        var scope = CreateScope("fwa_" + Guid.NewGuid().ToString("N"), new string('7', 64), DateTimeOffset.UtcNow);
        var digest = scope.AuthorizationDigest;

        Assert.NotEqual(digest, (scope with { ExecutableIdentity = scope.ExecutableIdentity with { Sha256 = new string('c', 64) } }).AuthorizationDigest);
        Assert.NotEqual(digest, (scope with { SystemAudioEndpointId = "another-endpoint" }).AuthorizationDigest);
        Assert.NotEqual(digest, (scope with { MaximumDurationSeconds = 61 }).AuthorizationDigest);
        Assert.NotEqual(digest, (scope with { ValiditySeconds = 301 }).AuthorizationDigest);
        Assert.NotEqual(digest, (scope with { OutputDirectory = @"C:\Other" }).AuthorizationDigest);
        Assert.NotEqual(digest, (scope with { OutputFileName = "other.mp4" }).AuthorizationDigest);
    }

    [Fact]
    public void SuccessRequiresExactOutputEvidenceAndStoppedRunIsNotSuccessfulCompletion()
    {
        using var database = new TemporaryDatabase();
        database.Store.Initialize();
        var repository = new SqliteFutureWindowAuthorizationRepository(database.Store);
        var createdAt = DateTimeOffset.UtcNow.AddSeconds(-3);
        var scope = CreateScope("fwa_" + Guid.NewGuid().ToString("N"), new string('1', 64), createdAt);
        repository.CreateOrGet(scope);
        repository.Approve(scope.AuthorizationId, Sid, Session, "approval-output", createdAt.AddSeconds(1));
        var receipt = repository.TryCommitStart(scope.AuthorizationId, Sid, Session,
            ProcessFor(scope, createdAt.AddSeconds(2)), createdAt.AddSeconds(2), out _)!;

        Assert.Throws<ArgumentException>(() => repository.CompleteRun(
            scope.AuthorizationId, receipt.RunId, true, "", null, 10, 500, createdAt.AddSeconds(3)));
        repository.CompleteRun(scope.AuthorizationId, receipt.RunId, false, "stopped_before_duration",
            null, null, null, createdAt.AddSeconds(3));

        var failed = repository.Get(scope.AuthorizationId, Sid, Session)!;
        Assert.Equal("failed", failed.StatusCode);
        Assert.Equal("failed", failed.RunStatus);
        Assert.Null(failed.OutputPath);
        Assert.Null(failed.OutputSizeBytes);
    }

    private static FutureWindowAuthorizationScope CreateScope(string id, string requestDigest, DateTimeOffset createdAt)
    {
        var executable = new FutureWindowExecutableIdentity(
            1, @"C:\Program Files\Player\player.exe", "1A2B3C4D:0000000000000010",
            new string('a', 64), "CN=Test Publisher", new string('b', 64));
        return new FutureWindowAuthorizationScope(
            id, "idempotency-key-" + id, requestDigest, Sid, Session, executable,
            "endpoint-test", "Render endpoint", 60, 300,
            @"C:\Recordings", "future-window-test.mp4", "pending", createdAt,
            null, null, null, null, 0);
    }

    private static FutureWindowProcessSnapshot ProcessFor(FutureWindowAuthorizationScope scope, DateTimeOffset createdAt) =>
        new("window_12345", new nint(12345), 42, createdAt.UtcDateTime.ToFileTimeUtc(),
            Sid, 1, scope.ExecutableIdentity.CanonicalPath, scope.ExecutableIdentity);

    private static FutureWindowOneShotExecutionTicket TicketFor(FutureWindowStartCommitReceipt receipt)
    {
        var authorization = receipt.Authorization;
        var process = receipt.Process;
        var proof = new FutureWindowOneShotProof(
            receipt.ProofId, receipt.RunId, authorization.AuthorizationId,
            new string('a', 64), new string('b', 64), receipt.CommittedAtUtc,
            authorization.ExpiresAtUtc!.Value, Sid, Session, new string('c', 64),
            process.WindowId, process.ProcessId, process.ProcessCreationFileTimeUtc,
            receipt.ProofNonce, TimeSpan.FromSeconds(authorization.MaximumDurationSeconds));
        return new FutureWindowOneShotExecutionTicket(receipt, proof);
    }

    private static string ReadSafetyMode(SqliteOperationalStore store)
    {
        using var connection = store.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT unattended_mode_code FROM unattended_safety_state WHERE state_id = 'global';";
        return (string)command.ExecuteScalar()!;
    }

    private sealed class FakeActiveRunStopper : IStandingLeaseActiveRunStopper
    {
        internal int StopAllCalls { get; private set; }
        public void StopAll(string reason) => StopAllCalls++;
    }

    private sealed class TemporaryDatabase : IDisposable
    {
        private readonly string _directory = Path.Combine(Path.GetTempPath(), "AgentRecorderTests", "future-window-" + Guid.NewGuid().ToString("N"));
        internal SqliteOperationalStore Store { get; }
        internal TemporaryDatabase()
        {
            Directory.CreateDirectory(_directory);
            Store = new SqliteOperationalStore(Path.Combine(_directory, "future-window.db"));
            Store.Initialize();
            using var connection = Store.OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText = "UPDATE unattended_safety_state SET unattended_mode_code='enabled' WHERE state_id='global';";
            command.ExecuteNonQuery();
        }
        public void Dispose()
        {
            if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
        }
    }
}
