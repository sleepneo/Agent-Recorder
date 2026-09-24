using System.Globalization;
using AgentRecorder.Core;
using AgentRecorder.Core.Automation;
using AgentRecorder.Infrastructure;
using AgentRecorder.Persistence;
using Microsoft.Data.Sqlite;
using Xunit;

namespace AgentRecorder.Tests;

[Collection("NonParallel-AgentRecorderDataDir")]
public sealed class RecurringSetupTerminalTests
{
    private const string Sid = "S-1-5-21-terminal";
    private const string Session = "terminal-session";
    private const string Reason = "setup_conflict";
    private static DateTimeOffset At(int seconds) => new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero).AddSeconds(seconds);

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void InitialAndPreparedTerminalChainsRoundTripAndReplayWithoutWrites(bool prepared, bool expired)
    {
        using var db = new Database();
        db.Create("one", prepared);
        var kind = expired ? RecurringSetupIntentStatus.Expired : RecurringSetupIntentStatus.Rejected;
        var reason = expired ? RecurringSetupTerminalReasonPolicy.IntentExpired : Reason;
        var now = expired ? At(172800) : At(5);
        var auditBefore = db.Audit();
        var result = db.Settle("one", kind, reason, now);
        Assert.Equal(RecurringSetupTerminalResultStatus.Settled, result.Status);
        Assert.True(result.Changed);
        db.AssertTerminal("one", prepared, kind, reason, now);
        Assert.Equal(auditBefore, db.Audit());
        var before = db.State();
        Assert.Equal(RecurringSetupTerminalResultStatus.AlreadyTerminal, db.Settle("one", kind, reason, now.AddSeconds(1)).Status);
        Assert.Equal(before, db.State());
        Assert.Equal(RecurringSetupTerminalResultStatus.Conflict,
            db.Settle("one", RecurringSetupIntentStatus.Rejected, "unattended_disabled", now.AddSeconds(2)).Status);
        Assert.Equal(RecurringSetupTerminalResultStatus.Conflict,
            db.Settle("one", expired ? RecurringSetupIntentStatus.Rejected : RecurringSetupIntentStatus.Expired,
                expired ? Reason : RecurringSetupTerminalReasonPolicy.IntentExpired, At(172801)).Status);
        Assert.Equal(before, db.State());
        db.AssertNoExecution();
    }

    [Theory]
    [InlineData("region_selection_cancelled")]
    [InlineData("region_selection_timed_out")]
    [InlineData("region_selection_invalid")]
    [InlineData("lease_rejected_by_user")]
    [InlineData("lease_approval_timed_out")]
    [InlineData("interactive_desktop_unavailable")]
    [InlineData("unattended_disabled")]
    [InlineData("setup_conflict")]
    public void ClosedRejectionReasonsAreDurable(string reason)
    {
        using var db = new Database();
        db.Create("one", true);
        Assert.Equal(RecurringSetupTerminalResultStatus.Settled, db.Settle("one", RecurringSetupIntentStatus.Rejected, reason, At(5)).Status);
        db.AssertTerminal("one", true, RecurringSetupIntentStatus.Rejected, reason, At(5));
    }

    [Theory]
    [InlineData("unknown")]
    [InlineData(" setup_conflict")]
    [InlineData("recurring_setup_intent_expired")]
    public void InvalidReasonCannotEnterDurableRejection(string reason)
    {
        using var db = new Database();
        db.Create("one", false);
        var before = db.State();
        Assert.Equal(RecurringSetupTerminalResultStatus.Rejected, db.Settle("one", RecurringSetupIntentStatus.Rejected, reason, At(5)).Status);
        Assert.Equal(before, db.State());
    }

    [Fact]
    public void WrongIdentityKindTargetAndActivatedIntentFailWithoutWrites()
    {
        using var db = new Database();
        db.Create("one", true);
        var service = new RecurringPlanSetupTerminalService(db.Store, () => At(5));
        var before = db.State();
        foreach (var (id, sid, session) in new[] { ("missing", Sid, Session), ("one", "other-sid", Session), ("one", Sid, "other-session") })
            Assert.Equal(RecurringSetupTerminalResultStatus.Rejected, service.Settle(id, sid, session, RecurringSetupIntentStatus.Rejected, Reason).Status);
        Assert.Equal(RecurringSetupTerminalResultStatus.Rejected, service.Settle("one", Sid, Session, RecurringSetupIntentStatus.Activated, Reason).Status);
        Assert.Equal(RecurringSetupTerminalResultStatus.Rejected, service.Settle("one", Sid, Session, RecurringSetupIntentStatus.Expired, Reason).Status);
        Assert.Equal(before, db.State());

        db.Corrupt("UPDATE setup_intents SET intent_kind_code = 'standing_once_fixed_region';");
        before = db.State();
        Assert.Equal("recurring_setup_terminal_not_found", service.Settle("one", Sid, Session, RecurringSetupIntentStatus.Rejected, Reason).Reason);
        Assert.Equal(before, db.State());
        db.Corrupt("UPDATE setup_intents SET intent_kind_code = 'recurring_daily_weekly_fixed_region';");

        Assert.Equal(RecurringLeaseLocalApprovalActivationStatus.Activated, db.Activate("one").Status);
        before = db.State();
        Assert.Equal(RecurringSetupTerminalResultStatus.Conflict, db.Settle("one", RecurringSetupIntentStatus.Rejected, Reason, At(6)).Status);
        Assert.Equal(RecurringSetupTerminalResultStatus.Conflict, db.Settle("one", RecurringSetupIntentStatus.Expired, RecurringSetupTerminalReasonPolicy.IntentExpired, At(172800)).Status);
        Assert.Equal(before, db.State());
    }

    [Fact]
    public void TrustedClockRejectsUnavailableNonUtcRegressionAndEarlyExpiry()
    {
        using var db = new Database();
        db.Create("one", true);
        var before = db.State();
        foreach (var sample in new DateTimeOffset?[] { null, At(5).ToOffset(TimeSpan.FromHours(8)), At(2) })
        {
            var failed = new RecurringPlanSetupTerminalService(db.Store, () => sample)
                .Settle("one", Sid, Session, RecurringSetupIntentStatus.Rejected, Reason);
            Assert.Equal(RecurringSetupTerminalResultStatus.Rejected, failed.Status);
        }
        Assert.Equal(RecurringSetupTerminalResultStatus.Rejected,
            new RecurringPlanSetupTerminalService(db.Store, () => throw new InvalidOperationException())
                .Settle("one", Sid, Session, RecurringSetupIntentStatus.Rejected, Reason).Status);
        Assert.Equal("recurring_setup_terminal_not_expired",
            db.Settle("one", RecurringSetupIntentStatus.Expired, RecurringSetupTerminalReasonPolicy.IntentExpired, At(172800).AddTicks(-1)).Reason);
        var clock = At(7);
        var service = new RecurringPlanSetupTerminalService(db.Store, () => clock);
        service.Settle("missing", Sid, Session, RecurringSetupIntentStatus.Rejected, Reason);
        clock = At(6);
        Assert.Equal("recurring_setup_terminal_time_non_monotonic", service.Settle("one", Sid, Session, RecurringSetupIntentStatus.Rejected, Reason).Reason);
        Assert.Equal(before, db.State());
        Assert.Equal(RecurringSetupTerminalResultStatus.Settled,
            db.Settle("one", RecurringSetupIntentStatus.Expired, RecurringSetupTerminalReasonPolicy.IntentExpired, At(172800)).Status);
        db.AssertTerminal("one", true, RecurringSetupIntentStatus.Expired, RecurringSetupTerminalReasonPolicy.IntentExpired, At(172800));
    }

    [Theory]
    [InlineData("AfterChildLeaseUpdate", true)]
    [InlineData("AfterIntentUpdate", true)]
    [InlineData("BeforeCommitAfterFinalRead", true)]
    [InlineData("AfterIntentUpdate", false)]
    [InlineData("BeforeCommitAfterFinalRead", false)]
    public void EveryWriteAndFinalReadFailureRollsBackAndCleanRetryWorks(string pointName, bool prepared)
    {
        using var db = new Database();
        db.Create("one", prepared);
        var before = db.State();
        var point = Enum.Parse<RecurringSetupTerminalFailurePoint>(pointName);
        var result = new RecurringPlanSetupTerminalService(db.Store, () => At(5), actual =>
        {
            if (actual == point) throw new InvalidOperationException("injected");
        }).Settle("one", Sid, Session, RecurringSetupIntentStatus.Rejected, Reason);
        Assert.Equal(RecurringSetupTerminalResultStatus.Rejected, result.Status);
        Assert.Equal(before, db.State());
        Assert.NotNull(db.Get("one"));
        Assert.Equal(RecurringSetupTerminalResultStatus.Settled, db.Settle("one", RecurringSetupIntentStatus.Rejected, Reason, At(5)).Status);
        db.AssertTerminal("one", prepared, RecurringSetupIntentStatus.Rejected, Reason, At(5));
    }

    [Theory]
    [InlineData("UPDATE setup_intents SET version = 1;")]
    [InlineData("UPDATE setup_intents SET version = 3;")]
    [InlineData("UPDATE setup_intents SET terminal_reason_code = 'unknown';")]
    [InlineData("UPDATE setup_intents SET terminal_reason_code = 'recurring_setup_intent_expired';")]
    [InlineData("UPDATE setup_intents SET status_code = 'expired', terminal_reason_code = 'recurring_setup_intent_expired';")]
    [InlineData("UPDATE setup_intents SET updated_at_utc = updated_at_utc - 1;")]
    [InlineData("UPDATE setup_intents SET plan_id = 'one-plan';")]
    [InlineData("DELETE FROM recurring_setup_preparations;")]
    [InlineData("UPDATE recurring_setup_preparations SET selection_digest = 'recurring-fixed-region-selection/v1:' || printf('%064d', 9);")]
    [InlineData("UPDATE recurring_setup_preparations SET configuration_digest = 'recurring-plan-configuration/v1:' || printf('%064d', 9);")]
    [InlineData("UPDATE recurring_setup_preparations SET prepared_at_utc = prepared_at_utc + 1;")]
    [InlineData("DELETE FROM recurring_fixed_region_profile_versions;")]
    [InlineData("UPDATE recurring_fixed_region_profile_versions SET dpi_x = 120;")]
    [InlineData("DELETE FROM recurring_schedule_versions;")]
    [InlineData("UPDATE recurring_schedule_versions SET schedule_digest = 'corrupt';")]
    [InlineData("DELETE FROM recurring_plan_profile_bindings;")]
    [InlineData("UPDATE recurring_plan_profile_bindings SET bound_at_utc = bound_at_utc + 1;")]
    [InlineData("DELETE FROM plans;")]
    [InlineData("UPDATE plans SET status_code = 'enabled', version = 1;")]
    [InlineData("UPDATE plans SET version = 1;")]
    [InlineData("DELETE FROM recurring_consent_leases;")]
    [InlineData("UPDATE recurring_consent_leases SET status_code = 'pending', version = 0;")]
    [InlineData("UPDATE recurring_consent_leases SET status_code = 'active';")]
    [InlineData("UPDATE recurring_consent_leases SET status_code = 'expired';")]
    [InlineData("UPDATE recurring_consent_leases SET status_code = 'revoked';")]
    [InlineData("UPDATE recurring_consent_leases SET status_code = 'exhausted';")]
    [InlineData("UPDATE recurring_consent_leases SET version = 0;")]
    [InlineData("UPDATE recurring_consent_leases SET version = 2;")]
    [InlineData("UPDATE recurring_consent_leases SET updated_at_utc = updated_at_utc + 1;")]
    [InlineData("UPDATE recurring_consent_leases SET max_uses = max_uses + 1;")]
    public void PreparedTerminalCorruptionCannotReadReplayOrReconcile(string mutation)
    {
        using var db = new Database();
        db.Create("one", true);
        Assert.True(db.Settle("one", RecurringSetupIntentStatus.Rejected, Reason, At(5)).Changed);
        db.Corrupt(mutation);
        var before = db.State();
        Assert.Null(db.Get("one"));
        Assert.Equal(RecurringSetupTerminalResultStatus.Rejected, db.Settle("one", RecurringSetupIntentStatus.Rejected, Reason, At(6)).Status);
        Assert.Equal(0, new RecurringSetupIntentService(db.Store, () => At(172800)).ReconcileExpiredPending());
        Assert.Equal(before, db.State());
    }

    [Theory]
    [InlineData("UPDATE setup_intents SET version = 2;")]
    [InlineData("UPDATE setup_intents SET lease_id = 'orphan';")]
    [InlineData("UPDATE setup_intents SET terminal_reason_code = 'lease_rejected_by_user';")]
    [InlineData("UPDATE setup_intents SET updated_at_utc = expires_at_utc - 1;")]
    public void InitialExpiredCorruptionFailsClosed(string mutation)
    {
        using var db = new Database();
        db.Create("one", false);
        Assert.True(db.Settle("one", RecurringSetupIntentStatus.Expired, RecurringSetupTerminalReasonPolicy.IntentExpired, At(172800)).Changed);
        db.Corrupt(mutation);
        Assert.Null(db.Get("one"));
    }

    [Fact]
    public void InitialTerminalCannotCarryPreparedRelationOrChildIds()
    {
        using var db = new Database();
        db.Create("one", true);
        Assert.True(db.Settle("one", RecurringSetupIntentStatus.Rejected, Reason, At(5)).Changed);
        db.Corrupt("UPDATE setup_intents SET version = 1;");
        Assert.Null(db.Get("one"));
        Assert.Equal(1L, db.Scalar("SELECT COUNT(*) FROM recurring_setup_preparations;"));
        Assert.Equal(1L, db.Scalar("SELECT COUNT(*) FROM recurring_consent_leases;"));
    }

    [Fact]
    public void ApprovalAndExecutionEvidenceBlockBothSettlementAndTerminalReadback()
    {
        using (var db = new Database())
        {
            db.Create("one", true);
            Assert.Equal(RecurringLeaseLocalApprovalActivationStatus.Activated, db.Activate("one").Status);
            db.Corrupt("""
                UPDATE plans SET status_code = 'draft', version = 0, updated_at_utc = created_at_utc;
                UPDATE recurring_consent_leases SET status_code = 'rejected', version = 1;
                UPDATE setup_intents SET status_code = 'rejected', terminal_reason_code = 'setup_conflict';
                """);
            Assert.Null(db.Get("one"));
            Assert.Equal(1L, db.Scalar("SELECT COUNT(*) FROM recurring_lease_local_approvals;"));
        }
        using (var db = new Database())
        {
            db.Create("one", true);
            var candidate = new RecurringOccurrenceCalculator().CalculateNext("one-plan", 1, db.Schedule, null).Candidate!;
            new SqliteRecurringOccurrenceMaterializationTransaction(db.Store).Materialize(candidate, At(4));
            var before = db.State();
            Assert.Equal(RecurringSetupTerminalResultStatus.Rejected, db.Settle("one", RecurringSetupIntentStatus.Rejected, Reason, At(5)).Status);
            Assert.Equal(before, db.State());
            db.Corrupt($"UPDATE setup_intents SET status_code = 'rejected', version = 2, terminal_reason_code = 'setup_conflict', updated_at_utc = {At(5).UtcTicks}; UPDATE recurring_consent_leases SET status_code = 'rejected', version = 1, updated_at_utc = {At(5).UtcTicks};");
            Assert.Null(db.Get("one"));
        }
    }

    [Theory]
    [InlineData("same")]
    [InlineData("reason")]
    [InlineData("kind")]
    public async Task ConcurrentTerminalRequestsHaveOneWinner(string scenario)
    {
        using var db = new Database();
        db.Create("one", true);
        using var barrier = new Barrier(2);
        var results = await Task.WhenAll(Enumerable.Range(0, 2).Select(index => Task.Run(() =>
        {
            Assert.True(barrier.SignalAndWait(TimeSpan.FromSeconds(10)));
            var expired = scenario == "kind" && index == 1;
            return db.Settle("one", expired ? RecurringSetupIntentStatus.Expired : RecurringSetupIntentStatus.Rejected,
                expired ? RecurringSetupTerminalReasonPolicy.IntentExpired : scenario == "reason" && index == 1 ? "unattended_disabled" : Reason, At(172800));
        })));
        Assert.Single(results, r => r.Status == RecurringSetupTerminalResultStatus.Settled);
        Assert.Single(results, r => r.Status == (scenario == "same" ? RecurringSetupTerminalResultStatus.AlreadyTerminal : RecurringSetupTerminalResultStatus.Conflict));
        db.AssertTerminal("one", true, db.Get("one")!.Status, db.Get("one")!.TerminalReasonCode!, At(172800));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PreparationVersusSettlementSerializesBothWinnerOrders(bool preparationFirst)
    {
        using var db = new Database();
        db.Create("one", false);
        var (prepared, terminal) = await RunOrderedRace(preparationFirst,
            hold => new RecurringSetupPreparationService(db.Store, () => At(5), new Ids("one"), beforeWritesForTest: (_, _) => hold()).Prepare(db.Selection("one")),
            hold => new RecurringPlanSetupTerminalService(db.Store, () => At(5), point => { if (point == RecurringSetupTerminalFailurePoint.AfterIntentUpdate) hold(); })
                .Settle("one", Sid, Session, RecurringSetupIntentStatus.Rejected, Reason));
        Assert.Equal(preparationFirst ? RecurringSetupPreparationResultStatus.Prepared : RecurringSetupPreparationResultStatus.Conflict, prepared.Status);
        Assert.Equal(RecurringSetupTerminalResultStatus.Settled, terminal.Status);
        db.AssertTerminal("one", preparationFirst, RecurringSetupIntentStatus.Rejected, Reason, At(5));
        db.AssertNoExecution();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ActivationVersusSettlementSerializesBothWinnerOrders(bool activationFirst)
    {
        using var db = new Database();
        db.Create("one", true);
        var (activated, terminal) = await RunOrderedRace(activationFirst,
            hold => db.Activate("one", hold),
            hold => new RecurringPlanSetupTerminalService(db.Store, () => At(5), point => { if (point == RecurringSetupTerminalFailurePoint.AfterIntentUpdate) hold(); })
                .Settle("one", Sid, Session, RecurringSetupIntentStatus.Rejected, Reason));
        Assert.Equal(activationFirst ? RecurringLeaseLocalApprovalActivationStatus.Activated : RecurringLeaseLocalApprovalActivationStatus.Rejected, activated.Status);
        if (!activationFirst) Assert.Equal("activation_intent_state_invalid", activated.Reason);
        Assert.Equal(activationFirst ? RecurringSetupTerminalResultStatus.Conflict : RecurringSetupTerminalResultStatus.Settled, terminal.Status);
        Assert.Equal(activationFirst ? RecurringSetupIntentStatus.Activated : RecurringSetupIntentStatus.Rejected, db.Get("one")!.Status);
        if (!activationFirst) db.AssertTerminal("one", true, RecurringSetupIntentStatus.Rejected, Reason, At(5));
        else
        {
            Assert.Equal("active", db.Text("SELECT status_code FROM recurring_consent_leases;"));
            Assert.Equal("enabled", db.Text("SELECT status_code FROM plans;"));
            Assert.Equal(1L, db.Scalar("SELECT COUNT(*) FROM recurring_lease_local_approvals;"));
        }
        db.AssertNoExecution();
    }

    private static async Task<(TLeft Left, TRight Right)> RunOrderedRace<TLeft, TRight>(bool leftFirst, Func<Action, TLeft> left, Func<Action, TRight> right)
    {
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        using var competitorStarted = new ManualResetEventSlim();
        void Hold() { entered.Set(); if (!release.Wait(TimeSpan.FromSeconds(15))) throw new TimeoutException(); }
        TLeft leftResult = default!;
        TRight rightResult = default!;
        Task winner = Task.Run(() => { if (leftFirst) leftResult = left(Hold); else rightResult = right(Hold); });
        Task? competitor = null;
        try
        {
            Assert.True(entered.Wait(TimeSpan.FromSeconds(10)));
            competitor = Task.Run(() => { competitorStarted.Set(); if (leftFirst) rightResult = right(() => { }); else leftResult = left(() => { }); });
            Assert.True(competitorStarted.Wait(TimeSpan.FromSeconds(10)));
        }
        finally { release.Set(); }
        await winner;
        if (competitor is not null) await competitor;
        return (leftResult, rightResult);
    }

    [Theory]
    [InlineData(5)]
    [InlineData(6)]
    public void BusyOrLockedAfterChildWriteReturnsStableConflictAndRollsBack(int errorCode)
    {
        using var db = new Database();
        db.Create("one", true);
        var before = db.State();
        var result = new RecurringPlanSetupTerminalService(db.Store, () => At(5), point =>
        {
            if (point == RecurringSetupTerminalFailurePoint.AfterChildLeaseUpdate)
                throw new SqliteException("injected SQLite contention", errorCode);
        }).Settle("one", Sid, Session, RecurringSetupIntentStatus.Rejected, Reason);
        Assert.Equal(RecurringSetupTerminalResultStatus.Conflict, result.Status);
        Assert.Equal("recurring_setup_terminal_concurrency_conflict", result.Reason);
        Assert.Equal(before, db.State());
        Assert.True(db.Settle("one", RecurringSetupIntentStatus.Rejected, Reason, At(5)).Changed);
    }

    [Fact]
    public async Task RealSqliteWriteLockReturnsStableConflictWithoutWrites()
    {
        using var db = new Database();
        db.Create("one", true);
        var before = db.State();
        using (var connection = db.Store.OpenConnection())
        using (var transaction = connection.BeginTransaction(deferred: false))
        {
            var result = await Task.Run(() => db.Settle("one", RecurringSetupIntentStatus.Rejected, Reason, At(5)));
            Assert.Equal(RecurringSetupTerminalResultStatus.Conflict, result.Status);
            Assert.Equal("recurring_setup_terminal_concurrency_conflict", result.Reason);
        }
        Assert.Equal(before, db.State());
        Assert.True(db.Settle("one", RecurringSetupIntentStatus.Rejected, Reason, At(5)).Changed);
    }

    [Fact]
    public void ReconciliationBoundsAttemptsSkipsCorruptionAndCountsOnlyCommittedReadableChains()
    {
        using var db = new Database();
        db.Create("a-corrupt", true);
        db.Create("b-initial", false);
        db.Create("c-prepared", true);
        db.Create("d-active", true);
        db.Create("e-rejected", true);
        Assert.Equal(RecurringLeaseLocalApprovalActivationStatus.Activated, db.Activate("d-active").Status);
        Assert.True(db.Settle("e-rejected", RecurringSetupIntentStatus.Rejected, Reason, At(5)).Changed);
        db.Corrupt("UPDATE recurring_consent_leases SET max_uses = max_uses + 1 WHERE lease_id = 'a-corrupt-lease';");
        var service = new RecurringSetupIntentService(db.Store, () => At(172800));
        Assert.Equal(0, service.ReconcileExpiredPending(0));
        Assert.Equal(0, service.ReconcileExpiredPending(257));
        Assert.Equal(0, service.ReconcileExpiredPending(1));
        Assert.Equal(1, service.ReconcileExpiredPending(2));
        db.AssertTerminal("b-initial", false, RecurringSetupIntentStatus.Expired, RecurringSetupTerminalReasonPolicy.IntentExpired, At(172800));
        Assert.Equal(1, service.ReconcileExpiredPending(2));
        db.AssertTerminal("c-prepared", true, RecurringSetupIntentStatus.Expired, RecurringSetupTerminalReasonPolicy.IntentExpired, At(172800));
        var before = db.State();
        Assert.Equal(0, service.ReconcileExpiredPending());
        Assert.Equal(before, db.State());
        Assert.Null(db.Get("a-corrupt"));
        Assert.Equal("lease_approval_pending", db.Text("SELECT status_code FROM setup_intents WHERE intent_id = 'a-corrupt';"));
        Assert.Equal("pending", db.Text("SELECT status_code FROM recurring_consent_leases WHERE lease_id = 'a-corrupt-lease';"));
        Assert.Equal(RecurringSetupIntentStatus.Activated, db.Get("d-active")!.Status);
        Assert.Equal(RecurringSetupIntentStatus.Rejected, db.Get("e-rejected")!.Status);
        db.AssertNoExecution();
    }

    private sealed class Ids(string id) : IRecurringSetupPreparationIdProvider
    {
        public string CreateProfileId() => id + "-profile";
        public string CreatePlanId() => id + "-plan";
        public string CreateLeaseId() => id + "-lease";
    }

    private sealed class Database : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "AgentRecorderRecurringSetupTerminal_" + Guid.NewGuid().ToString("N"));
        internal SqliteOperationalStore Store { get; }
        internal RecurringPlanSchedule Schedule { get; } = RecurringPlanSchedule.CreateDaily("China Standard Time",
            new DateOnly(2026, 1, 1), new DateOnly(2026, 1, 2), new TimeOnly(9, 30), 2, TimeSpan.FromSeconds(30), TimeSpan.FromMinutes(5));
        internal Database()
        {
            Directory.CreateDirectory(_root);
            Store = new SqliteOperationalStore(Path.Combine(_root, "state.db"));
            Store.Initialize();
            Execute("UPDATE unattended_safety_state SET unattended_mode_code = 'enabled';");
        }
        internal void Create(string id, bool prepared)
        {
            var request = RecurringSetupIntentSnapshot.CreateForTrustedSetupAdapter(id, id + "-key", Sid, Session,
                At(0), At(172800), Schedule, 2, TimeSpan.FromSeconds(60), At(172800), "D:\\Recordings\\Agent", "terminal-test");
            Assert.Equal(RecurringSetupIntentResultStatus.Created, new RecurringSetupIntentService(Store, () => At(1)).CreateOrGet(request).Result);
            if (prepared)
                Assert.Equal(RecurringSetupPreparationResultStatus.Prepared,
                    new RecurringSetupPreparationService(Store, () => At(3), new Ids(id)).Prepare(Selection(id)).Status);
        }
        internal RecurringFixedRegionSelectionSnapshot Selection(string id) =>
            RecurringFixedRegionSelectionSnapshot.CreateForTrustedLocalSelectionAdapter(id, Sid, Session, At(2), "display-terminal",
                new AuthorizedPhysicalRectangle(0, 0, 1920, 1080), new AuthorizedPhysicalRectangle(10, 20, 640, 480),
                96, 96, 1920, 1080, AuthorizedDisplayOrientation.Landscape, new string('a', 64));
        internal RecurringSetupIntentReadback? Get(string id) => new RecurringSetupIntentService(Store, () => At(172801)).Get(id, Sid, Session);
        internal RecurringSetupTerminalResult Settle(string id, RecurringSetupIntentStatus kind, string reason, DateTimeOffset at) =>
            new RecurringPlanSetupTerminalService(Store, () => at).Settle(id, Sid, Session, kind, reason);
        internal RecurringLeasePreparedIntentApprovalActivationResult Activate(string id, Action? hold = null)
        {
            var lease = new SqliteRecurringConsentLeaseRepository(Store).Get(id + "-lease");
            var receipt = RecurringLeaseLocalApprovalReceipt.CreateForTest(id + "-approval", lease.LeaseId, lease.PlanId,
                lease.ConfigurationRef.ConfigurationDigest, lease.AuthorizationDigest, Sid, Session, At(4));
            return new RecurringLeasePreparedIntentApprovalActivationService(Store, () => At(5), beforeWritesForTest: (_, _) => hold?.Invoke()).Activate(id, receipt);
        }
        internal void AssertTerminal(string id, bool prepared, RecurringSetupIntentStatus kind, string reason, DateTimeOffset at)
        {
            var readback = Get(id);
            Assert.NotNull(readback);
            Assert.Equal(kind, readback!.Status);
            Assert.Equal(prepared ? 2 : 1, readback.Version);
            Assert.Equal(reason, readback.TerminalReasonCode);
            Assert.Equal(at.UtcTicks, Scalar($"SELECT updated_at_utc FROM setup_intents WHERE intent_id = '{id}';"));
            Assert.Equal(prepared ? 1 : 0, Scalar($"SELECT COUNT(*) FROM recurring_setup_preparations WHERE intent_id = '{id}';"));
            Assert.Equal(0, Scalar($"SELECT COUNT(*) FROM recurring_lease_local_approvals WHERE lease_id = '{id}-lease';"));
            if (prepared)
            {
                var lease = new SqliteRecurringConsentLeaseRepository(Store).Get(id + "-lease");
                Assert.Equal(ConsentLeaseStatus.Rejected, lease.Status);
                Assert.Equal(1, lease.Version);
                Assert.Equal(at, lease.UpdatedAtUtc);
                var plan = new SqlitePlanDefinitionRepository(Store).Get(id + "-plan");
                Assert.Equal(PlanDefinitionStatus.Draft, plan.Status);
                Assert.Equal(0, plan.Version);
                Assert.Equal(plan.CreatedAtUtc, plan.UpdatedAtUtc);
            }
            else
            {
                Assert.Equal(0, Scalar($"SELECT COUNT(*) FROM plans WHERE id = '{id}-plan';"));
                Assert.Equal(0, Scalar($"SELECT COUNT(*) FROM recurring_consent_leases WHERE lease_id = '{id}-lease';"));
                Assert.Equal(0, Scalar($"SELECT COUNT(*) FROM recurring_fixed_region_profile_versions WHERE profile_id = '{id}-profile';"));
            }
        }
        internal void AssertNoExecution()
        {
            foreach (var table in new[] { "plan_occurrences", "recording_runs", "recurring_occurrence_slots", "recurring_lease_uses", "recurring_occurrence_execution_specs", "lease_uses" })
                Assert.Equal(0, Scalar($"SELECT COUNT(*) FROM {table};"));
        }
        internal string Audit() => Rows("plans", "recurring_setup_preparations", "recurring_fixed_region_profile_versions", "recurring_schedule_versions", "recurring_plan_profile_bindings");
        internal string State() => Rows("setup_intents", "plans", "recurring_consent_leases", "recurring_setup_preparations", "recurring_fixed_region_profile_versions", "recurring_schedule_versions", "recurring_plan_profile_bindings", "recurring_lease_local_approvals", "plan_occurrences", "recording_runs", "recurring_lease_uses");
        private string Rows(params string[] tables)
        {
            using var c = Store.OpenConnection();
            var rows = new List<string>();
            foreach (var table in tables)
            {
                using var command = c.CreateCommand(); command.CommandText = $"SELECT * FROM {table} ORDER BY 1;";
                using var reader = command.ExecuteReader();
                while (reader.Read()) rows.Add(table + ":" + string.Join("|", Enumerable.Range(0, reader.FieldCount).Select(i => Convert.ToString(reader.GetValue(i), CultureInfo.InvariantCulture))));
            }
            return string.Join("\n", rows);
        }
        internal string Text(string sql) { using var c = Store.OpenConnection(); using var command = c.CreateCommand(); command.CommandText = sql; return Convert.ToString(command.ExecuteScalar(), CultureInfo.InvariantCulture)!; }
        internal long Scalar(string sql) => long.Parse(Text(sql), CultureInfo.InvariantCulture);
        private void Execute(string sql) { using var c = Store.OpenConnection(); Execute(c, sql); }
        private static void Execute(SqliteConnection c, string sql) { using var command = c.CreateCommand(); command.CommandText = sql; command.ExecuteNonQuery(); }
        internal void Corrupt(string sql)
        {
            using var c = Store.OpenConnection();
            var triggers = new List<(string Name, string Sql)>();
            using (var command = c.CreateCommand())
            {
                command.CommandText = "SELECT name, sql FROM sqlite_master WHERE type = 'trigger';";
                using var reader = command.ExecuteReader();
                while (reader.Read()) triggers.Add((reader.GetString(0), reader.GetString(1)));
            }
            Execute(c, "PRAGMA foreign_keys = OFF; PRAGMA ignore_check_constraints = ON;");
            try
            {
                foreach (var trigger in triggers) Execute(c, $"DROP TRIGGER \"{trigger.Name}\";");
                Execute(c, sql);
            }
            finally
            {
                foreach (var trigger in triggers) Execute(c, trigger.Sql);
                Execute(c, "PRAGMA foreign_keys = ON; PRAGMA ignore_check_constraints = OFF;");
            }
        }
        public void Dispose() { Directory.Delete(_root, recursive: true); }
    }
}
