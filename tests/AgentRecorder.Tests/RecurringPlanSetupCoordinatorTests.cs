using System.Text.Json;
using AgentRecorder.Api;
using AgentRecorder.App;
using AgentRecorder.Core;
using AgentRecorder.Core.Automation;
using AgentRecorder.Persistence;
using AgentRecorder.Windows;
using Xunit;
using static AgentRecorder.Tests.RecurringPlanSetupTestFixture;

namespace AgentRecorder.Tests;

public sealed class RecurringPlanSetupCoordinatorTests
{
    [Fact]
    public void RuntimeUnavailableRejectsCreateBeforeIntentPersistence()
    {
        using var db = new RecurringPlanSetupTestFixture();
        var ui = new FakeUi();
        using var coordinator = db.Coordinator(ui, executionSupported: () => false);

        var result = coordinator.CreateOrGet(db.Request());

        Assert.Equal(RecurringPlanSetupCreateStatus.Rejected, result.Status);
        Assert.Equal("recurring_execution_unavailable", result.ReasonCode);
        Assert.Equal(0, db.Scalar("SELECT COUNT(*) FROM setup_intents;"));
        Assert.Equal(0, ui.Selections);
        Assert.Equal(0, ui.Approvals);
        db.AssertNoExecution();
    }

    [Fact]
    public void UnknownCurrentPrincipalCannotEnableUnattendedRecurringSetup()
    {
        using var db = new RecurringPlanSetupTestFixture();
        using var coordinator = new RecurringPlanSetupCoordinator(
            db.Store, db.Audit, new FakeUi(), principalForTest: () => null,
            executionSupportedProvider: () => true);

        Assert.False(coordinator.IsUnattendedEnabled);
        Assert.Equal(RecurringPlanSetupCreateStatus.Rejected, coordinator.CreateOrGet(db.Request()).Status);
        Assert.Equal(0, db.Scalar("SELECT COUNT(*) FROM setup_intents;"));
        db.AssertNoExecution();
    }

    [Fact]
    public void RuntimeUnavailableSettlesPendingRecoveryWithoutOpeningUi()
    {
        using var db = new RecurringPlanSetupTestFixture();
        db.Create("recover-runtime-offline");
        var ui = new FakeUi();

        using var coordinator = db.Coordinator(ui, executionSupported: () => false);

        var recovered = coordinator.Get("recover-runtime-offline");
        Assert.NotNull(recovered);
        Assert.Equal("rejected", recovered.Status);
        Assert.Equal("recurring_execution_unavailable", recovered.ReasonCode);
        Assert.Equal(0, ui.Selections);
        Assert.Equal(0, ui.Approvals);
        db.AssertNoExecution();
    }

    [Fact]
    public async Task RuntimeFailureAfterLocalApprovalCannotActivateLease()
    {
        using var db = new RecurringPlanSetupTestFixture();
        var executionSupported = true;
        var ui = new FakeUi();
        using var coordinator = new RecurringPlanSetupCoordinator(
            db.Store,
            db.Audit,
            ui,
            () => db.Principal,
            () => db.Now,
            () => db.Displays,
            db.Output,
            beforeApprovalCommitForTest: () =>
            {
                executionSupported = false;
                return Task.CompletedTask;
            },
            executionSupportedProvider: () => executionSupported);

        var created = coordinator.CreateOrGet(db.Request());
        await coordinator.WaitForIdleAsync().WaitAsync(TimeSpan.FromSeconds(10));

        var state = coordinator.Get(created.State!.SetupIntentId);
        Assert.Equal("rejected", state!.Status);
        Assert.Equal("recurring_execution_unavailable", state.ReasonCode);
        Assert.Equal(0, db.Scalar("SELECT COUNT(*) FROM recurring_lease_local_approvals;"));
        Assert.Equal(0, db.Scalar("SELECT COUNT(*) FROM recurring_consent_leases WHERE status_code = 'active';"));
        db.AssertNoExecution();
    }

    [Fact]
    public async Task NewRequestHasExactApprovedChainWithoutExecutionAndSafeAgentProjection()
    {
        using var db = new RecurringPlanSetupTestFixture(); var ui = new FakeUi();
        using var coordinator = db.Coordinator(ui);
        var created = coordinator.CreateOrGet(db.Request());
        Assert.Equal(RecurringPlanSetupCreateStatus.Created, created.Status);
        Assert.Equal("setup_pending", created.State!.Status);
        Assert.Equal("local_region_selection", created.State.NextAction); Assert.True(created.State.RequiresLocalAction);
        await coordinator.WaitForIdleAsync().WaitAsync(TimeSpan.FromSeconds(10));
        var state = coordinator.Get(created.State.SetupIntentId)!;
        Assert.Equal("scheduled", state.Status); Assert.Equal(2, state.StatusVersion);
        Assert.Equal("recurring-setup/v1:2", state.StatusVersionCursor); Assert.False(state.RequiresLocalAction);
        Assert.NotNull(state.PlanId); Assert.NotNull(state.LeaseId);
        Assert.Equal("enabled", db.Text("SELECT status_code FROM plans;"));
        Assert.Equal("active", db.Text("SELECT status_code FROM recurring_consent_leases;"));
        Assert.Equal(1, db.Scalar("SELECT COUNT(*) FROM recurring_lease_local_approvals;"));
        Assert.Equal(db.Text("SELECT configuration_digest FROM recurring_setup_preparations;"), db.Text("SELECT configuration_digest FROM recurring_lease_local_approvals;"));
        Assert.Equal(db.Text("SELECT authorization_digest FROM recurring_consent_leases;"), db.Text("SELECT authorization_digest FROM recurring_lease_local_approvals;"));
        Assert.Equal(Sid, db.Text("SELECT current_user_sid FROM recurring_lease_local_approvals;"));
        Assert.Equal(Session, db.Text("SELECT session_binding FROM recurring_lease_local_approvals;"));
        Assert.Equal(At(172800).UtcTicks, db.Scalar("SELECT expires_at_utc FROM setup_intents;"));
        Assert.Equal(1, ui.Selections); Assert.Equal(1, ui.Approvals); Assert.Equal(0, coordinator.FlightCountForTests);
        Assert.Equal(2, db.Output.Calls.Count);
        Assert.All(db.Output.Calls, call => { Assert.Equal(db.Request().OutputDirectory, call.Directory); Assert.Equal(TimeSpan.FromSeconds(30), call.Duration); });
        db.AssertNoExecution();
        var before = db.State();
        Assert.Equal(RecurringPlanSetupCreateStatus.Existing, coordinator.CreateOrGet(db.Request()).Status);
        Assert.Equal(before, db.State()); Assert.Equal(1, ui.Selections);
    }

    [Theory]
    [InlineData("Cancelled", "region_selection_cancelled")]
    [InlineData("TimedOut", "region_selection_timed_out")]
    [InlineData("Invalid", "region_selection_invalid")]
    [InlineData("Unavailable", "interactive_desktop_unavailable")]
    [InlineData("Conflict", "setup_conflict")]
    public async Task SelectionResultsHaveExactClosedTerminalReasons(string result, string reason)
    {
        using var db = new RecurringPlanSetupTestFixture();
        var ui = new FakeUi { Select = _ => Task.FromResult(new RecurringPlanSetupSelection(Enum.Parse<RecurringRegionSelectionStatus>(result))) };
        using var coordinator = db.Coordinator(ui); var created = coordinator.CreateOrGet(db.Request());
        await coordinator.WaitForIdleAsync().WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(reason, coordinator.Get(created.State!.SetupIntentId)!.ReasonCode);
        Assert.Equal(0, ui.Approvals); Assert.Equal(0, db.Scalar("SELECT COUNT(*) FROM plans;")); db.AssertNoExecution();
    }

    [Theory]
    [InlineData("coordinates")]
    [InlineData("zero")]
    [InlineData("cross_screen")]
    [InlineData("overflow")]
    [InlineData("dpi")]
    [InlineData("unresolved")]
    [InlineData("duplicate_identity")]
    [InlineData("ambiguous")]
    public async Task InvalidSelectionIsNeverClippedOrSubstituted(string scenario)
    {
        using var db = new RecurringPlanSetupTestFixture();
        var selected = FakeUi.Selected();
        if (scenario == "coordinates") selected = selected with { CoordinateSpace = "display_relative" };
        if (scenario == "zero") selected = selected with { Width = 0 };
        if (scenario == "cross_screen") selected = selected with { X = 1900, Width = 200 };
        if (scenario == "overflow") selected = selected with { X = int.MaxValue, Width = int.MaxValue };
        if (scenario is "dpi" or "unresolved")
            db.Displays = new[] { new StandingLeaseDisplayMetadata("token", "stable-display",
                scenario == "unresolved" ? DisplayIdentityResolutionStatus.Unresolved : DisplayIdentityResolutionStatus.Resolved,
                new(100, 200, 1920, 1080), scenario == "dpi" ? 0 : 96, 96, 1920, 1080, AuthorizedDisplayOrientation.Landscape) };
        if (scenario is "duplicate_identity" or "ambiguous")
            db.Displays = db.Displays.Concat(new[] { new StandingLeaseDisplayMetadata("token2", scenario == "duplicate_identity" ? "stable-display" : "other-display",
                DisplayIdentityResolutionStatus.Resolved, new(100, 200, 1920, 1080), 96, 96, 1920, 1080, AuthorizedDisplayOrientation.Landscape) }).ToArray();
        var ui = new FakeUi { Select = _ => Task.FromResult(selected) };
        using var coordinator = db.Coordinator(ui); coordinator.CreateOrGet(db.Request());
        await coordinator.WaitForIdleAsync().WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal("region_selection_invalid", db.Text("SELECT terminal_reason_code FROM setup_intents;"));
        Assert.Equal(0, db.Scalar("SELECT COUNT(*) FROM recurring_setup_preparations;")); db.AssertNoExecution();
    }

    [Theory]
    [InlineData("Rejected", "lease_rejected_by_user")]
    [InlineData("TimedOut", "lease_approval_timed_out")]
    [InlineData("Unavailable", "interactive_desktop_unavailable")]
    public async Task ApprovalResultsRejectBothIntentAndUnapprovedChild(string result, string reason)
    {
        using var db = new RecurringPlanSetupTestFixture();
        var ui = new FakeUi { Approve = (_, _) => Task.FromResult(Enum.Parse<RecurringLeaseApprovalResult>(result)) };
        using var coordinator = db.Coordinator(ui); var created = coordinator.CreateOrGet(db.Request());
        await coordinator.WaitForIdleAsync().WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(reason, coordinator.Get(created.State!.SetupIntentId)!.ReasonCode);
        Assert.Equal("rejected", db.Text("SELECT status_code FROM recurring_consent_leases;"));
        Assert.Equal("draft", db.Text("SELECT status_code FROM plans;"));
        Assert.Equal(0, db.Scalar("SELECT COUNT(*) FROM recurring_lease_local_approvals;")); db.AssertNoExecution();
    }

    [Fact]
    public async Task ConcurrentIdempotencySharesOneFlightAndConflictsNeverReopenUi()
    {
        using var db = new RecurringPlanSetupTestFixture(); var entered = Signal(); var release = Signal();
        var ui = new FakeUi { Select = async token => { entered.TrySetResult(); await release.Task.WaitAsync(token); return FakeUi.Selected(); } };
        using var coordinator = db.Coordinator(ui);
        var tasks = Enumerable.Range(0, 12).Select(_ => Task.Run(() => coordinator.CreateOrGet(db.Request()))).ToArray();
        var results = await Task.WhenAll(tasks).WaitAsync(TimeSpan.FromSeconds(10)); await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Single(results.Select(r => r.State!.SetupIntentId).Distinct()); Assert.Equal(1, ui.Selections);
        Assert.Equal(1, coordinator.FlightCountForTests); Assert.Equal(1, db.Scalar("SELECT COUNT(*) FROM setup_intents;"));
        Assert.Equal(RecurringPlanSetupCreateStatus.Conflict, coordinator.CreateOrGet(db.Request() with { FilenamePrefix = "conflict" }).Status);
        release.TrySetResult(); await coordinator.WaitForIdleAsync().WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(1, ui.Approvals); Assert.Equal(0, coordinator.FlightCountForTests); db.AssertNoExecution();
    }

    [Fact]
    public async Task DifferentIntentsUseOneUiGateAndQueuedFlightHonorsShutdown()
    {
        using var db = new RecurringPlanSetupTestFixture(); var entered = Signal(); var release = Signal();
        var ui = new FakeUi { Select = async token => { entered.TrySetResult(); await release.Task.WaitAsync(token); return FakeUi.Selected(); } };
        var coordinator = db.Coordinator(ui);
        try
        {
            coordinator.CreateOrGet(db.Request("one")); await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            coordinator.CreateOrGet(db.Request("two"));
            Assert.Equal(2, coordinator.FlightCountForTests); Assert.Equal(1, ui.Selections);
            await Task.Run(coordinator.Dispose).WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(1, ui.Selections); Assert.Equal(0, ui.Approvals);
            Assert.Equal(2, db.Scalar("SELECT COUNT(*) FROM setup_intents WHERE status_code = 'region_selection_pending';"));
            Assert.Equal(0, coordinator.FlightCountForTests);
            Assert.Equal("host_shutdown", coordinator.CreateOrGet(db.Request("three")).ReasonCode);
        }
        finally { release.TrySetResult(); coordinator.Dispose(); }
        var next = new FakeUi(); using var restored = db.Coordinator(next);
        await restored.WaitForIdleAsync().WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(2, next.Selections); Assert.Equal(2, next.Approvals); db.AssertNoExecution();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RestartUsesFreshSelectionOnlyWhenNotPrepared(bool prepared)
    {
        using var db = new RecurringPlanSetupTestFixture(); db.Create("recover", prepared);
        var ui = new FakeUi(); using var coordinator = db.Coordinator(ui);
        await coordinator.WaitForIdleAsync().WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(prepared ? 0 : 1, ui.Selections); Assert.Equal(1, ui.Approvals);
        Assert.Equal("scheduled", coordinator.Get("recover")!.Status); db.AssertNoExecution();
    }

    [Fact]
    public async Task RestartDoesNotShowUiForFinishedForeignOrCorruptIntentsAndExpiresDuePending()
    {
        using var db = new RecurringPlanSetupTestFixture();
        db.Create("active", true); db.Activate("active"); db.Create("rejected"); db.Reject("rejected");
        db.Create("bad", true); db.Create("due", true); db.Create("foreign", sid: "other");
        db.Corrupt("UPDATE setup_intents SET request_digest = 'corrupt' WHERE intent_id = 'bad';");
        db.Now = At(172800); var ui = new FakeUi();
        using (var coordinator = db.Coordinator(ui))
        {
            await coordinator.WaitForIdleAsync().WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal("expired", coordinator.Get("due")!.Status); Assert.Null(coordinator.Get("bad"));
        }
        using var again = db.Coordinator(ui); await again.WaitForIdleAsync().WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(0, ui.Selections); Assert.Equal(0, ui.Approvals);
        Assert.Equal("lease_approval_pending", db.Text("SELECT status_code FROM setup_intents WHERE intent_id='bad';"));
        Assert.Contains(db.Audit.Events, e => e.Contains("recurring_setup_query_unavailable")); db.AssertNoExecution();
    }

    [Theory]
    [InlineData("sid", "setup_conflict")]
    [InlineData("session", "setup_conflict")]
    [InlineData("disabled", "unattended_disabled")]
    [InlineData("desktop", "interactive_desktop_unavailable")]
    [InlineData("terminal", "setup_conflict")]
    [InlineData("corrupt", null)]
    [InlineData("missing", "setup_conflict")]
    [InlineData("unwritable", "setup_conflict")]
    [InlineData("space", "setup_conflict")]
    [InlineData("expired", "recurring_setup_intent_expired")]
    public async Task ApprovalCallbackChangesAreRevalidatedBeforeReceipt(string change, string? expectedReason)
    {
        using var db = new RecurringPlanSetupTestFixture(); var ui = new FakeUi();
        ui.Approve = (_, _) =>
        {
            if (change == "sid") db.Principal = new("wrong", Session);
            if (change == "session") db.Principal = new(Sid, "wrong");
            if (change == "disabled") db.Execute("UPDATE unattended_safety_state SET unattended_mode_code='disabled';");
            if (change == "desktop") ui.Available = false;
            if (change == "terminal") db.Reject(db.Text("SELECT intent_id FROM setup_intents;"));
            if (change == "corrupt") db.Corrupt("UPDATE recurring_consent_leases SET authorization_digest='corrupt';");
            if (change == "missing") db.Output.Result = RecurringSetupOutputReadiness.DirectoryUnavailable;
            if (change == "unwritable") db.Output.Result = RecurringSetupOutputReadiness.DirectoryUnwritable;
            if (change == "space") db.Output.Result = RecurringSetupOutputReadiness.InsufficientSpace;
            if (change == "expired") db.Now = At(172800);
            return Task.FromResult(RecurringLeaseApprovalResult.Approved);
        };
        using var coordinator = db.Coordinator(ui); coordinator.CreateOrGet(db.Request());
        await coordinator.WaitForIdleAsync().WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(0, db.Scalar("SELECT COUNT(*) FROM recurring_lease_local_approvals;"));
        Assert.Equal("draft", db.Text("SELECT status_code FROM plans;"));
        if (expectedReason is not null) Assert.Equal(expectedReason, db.Text("SELECT terminal_reason_code FROM setup_intents;"));
        db.AssertNoExecution();
    }

    [Theory]
    [InlineData("disabled", "unattended_disabled")]
    [InlineData("desktop", "interactive_desktop_unavailable")]
    [InlineData("missing", "setup_conflict")]
    [InlineData("unwritable", "setup_conflict")]
    [InlineData("space", "setup_conflict")]
    public async Task InitialEnvironmentFailureDoesNotShowUiOrCreateOutput(string failure, string reason)
    {
        using var db = new RecurringPlanSetupTestFixture(); var ui = new FakeUi();
        if (failure == "disabled") db.Execute("UPDATE unattended_safety_state SET unattended_mode_code='disabled';");
        if (failure == "desktop") ui.Available = false;
        if (failure == "missing") db.Output.Result = RecurringSetupOutputReadiness.DirectoryUnavailable;
        if (failure == "unwritable") db.Output.Result = RecurringSetupOutputReadiness.DirectoryUnwritable;
        if (failure == "space") db.Output.Result = RecurringSetupOutputReadiness.InsufficientSpace;
        using var coordinator = db.Coordinator(ui); coordinator.CreateOrGet(db.Request());
        await coordinator.WaitForIdleAsync().WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(reason, db.Text("SELECT terminal_reason_code FROM setup_intents;"));
        Assert.Equal(0, ui.Selections); Assert.Equal(0, ui.Approvals); db.AssertNoExecution();
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("non_utc")]
    [InlineData("regression")]
    public async Task InvalidClockNeverCreatesApproval(string clock)
    {
        using var db = new RecurringPlanSetupTestFixture(); var ui = new FakeUi();
        if (clock == "missing") db.Now = null;
        if (clock == "non_utc") db.Now = At(10).ToOffset(TimeSpan.FromHours(8));
        if (clock == "regression") ui.Approve = (_, _) => { db.Now = At(9); return Task.FromResult(RecurringLeaseApprovalResult.Approved); };
        using var coordinator = db.Coordinator(ui); coordinator.CreateOrGet(db.Request());
        await coordinator.WaitForIdleAsync().WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(0, db.Scalar("SELECT COUNT(*) FROM recurring_lease_local_approvals;")); db.AssertNoExecution();
    }

    [Fact]
    public async Task ShutdownBeforeCommitRetainsPreparedWithoutReceiptAndCanRecover()
    {
        using var db = new RecurringPlanSetupTestFixture(); var entered = Signal(); var release = Signal(); var shutdown = Signal(); var commits = 0;
        var coordinator = db.Coordinator(new FakeUi(), async () => { entered.TrySetResult(); await release.Task; },
            () => Interlocked.Increment(ref commits), () => shutdown.TrySetResult());
        try
        {
            coordinator.CreateOrGet(db.Request()); await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var before = db.State(); var disposing = Task.Run(coordinator.Dispose);
            await shutdown.Task.WaitAsync(TimeSpan.FromSeconds(5)); release.TrySetResult(); await disposing.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(before, db.State()); Assert.Equal(0, commits);
            Assert.Equal("lease_approval_pending", db.Text("SELECT status_code FROM setup_intents;"));
        }
        finally { release.TrySetResult(); coordinator.Dispose(); }
        var recoveredUi = new FakeUi(); using var recovered = db.Coordinator(recoveredUi);
        await recovered.WaitForIdleAsync().WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(0, recoveredUi.Selections); Assert.Equal(1, recoveredUi.Approvals); db.AssertNoExecution();
    }

    [Fact]
    public async Task ApprovalCommitBeforeShutdownFinishesAtomicActivation()
    {
        using var db = new RecurringPlanSetupTestFixture(); var entered = Signal(); using var release = new ManualResetEventSlim();
        var events = new System.Collections.Concurrent.ConcurrentQueue<string>();
        var coordinator = db.Coordinator(new FakeUi(), commit: () => { events.Enqueue("approval"); entered.TrySetResult(); Assert.True(release.Wait(TimeSpan.FromSeconds(10))); },
            shutdown: () => events.Enqueue("shutdown"));
        try
        {
            coordinator.CreateOrGet(db.Request()); await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var disposing = Task.Run(coordinator.Dispose); release.Set(); await disposing.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(new[] { "approval", "shutdown" }, events);
            Assert.Equal("activated", db.Text("SELECT status_code FROM setup_intents;"));
            Assert.Equal(1, db.Scalar("SELECT COUNT(*) FROM recurring_lease_local_approvals;")); db.AssertNoExecution();
        }
        finally { release.Set(); coordinator.Dispose(); }
    }

    [Fact]
    public async Task FaultingUiWritesOnlyClosedReasonAndAuditOmitsSensitivePayloads()
    {
        using var db = new RecurringPlanSetupTestFixture();
        var ui = new FakeUi { Select = _ => throw new InvalidOperationException(db.Request().OutputDirectory + " SECRET-API-KEY preview-pixels") };
        using var coordinator = db.Coordinator(ui); coordinator.CreateOrGet(db.Request());
        await coordinator.WaitForIdleAsync().WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal("setup_conflict", db.Text("SELECT terminal_reason_code FROM setup_intents;"));
        var audit = string.Join("\n", db.Audit.Events);
        Assert.Contains("InvalidOperationException", audit);
        Assert.DoesNotContain(db.Root, audit); Assert.DoesNotContain("SECRET-API-KEY", audit); Assert.DoesNotContain("preview-pixels", audit);
        Assert.DoesNotContain("private-prefix", audit);
        foreach (var line in db.Audit.Events)
        {
            using var json = JsonDocument.Parse(line[line.IndexOf('{')..]);
            Assert.All(json.RootElement.EnumerateObject(), property => Assert.Contains(property.Name,
                new[] { "intent_id", "plan_id", "lease_id", "status", "reason_code", "exception_type" }));
        }
        db.AssertNoExecution();
    }

    [Theory]
    [InlineData("selection_timeout", "TimedOut")]
    [InlineData("display_unavailable", "Invalid")]
    [InlineData("error", "Unavailable")]
    [InlineData("host_shutdown", "HostShutdown")]
    public void TrayAdapterUsesRecurringResultVocabulary(string raw, string expected) =>
        Assert.Equal(Enum.Parse<RecurringRegionSelectionStatus>(expected), TrayRecurringPlanSetupUi.MapSelectionStatus(raw));

    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    [Fact]
    public async Task TwoCoordinatorsShareGlobalUiGateAndRereadBeforeOpeningUi()
    {
        using var db = new RecurringPlanSetupTestFixture(); db.Create("shared");
        var entered = Signal(); var release = Signal();
        var firstUi = new FakeUi { Approve = async (_, token) => { entered.TrySetResult(); await release.Task.WaitAsync(token); return RecurringLeaseApprovalResult.Approved; } };
        using var first = db.Coordinator(firstUi);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var secondUi = new FakeUi(); using var second = db.Coordinator(secondUi);
        Assert.Equal(1, second.FlightCountForTests); Assert.Equal(0, secondUi.Selections); Assert.Equal(0, secondUi.Approvals);
        release.TrySetResult();
        await Task.WhenAll(first.WaitForIdleAsync(), second.WaitForIdleAsync()).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(1, firstUi.Selections); Assert.Equal(1, firstUi.Approvals);
        Assert.Equal(0, secondUi.Selections); Assert.Equal(0, secondUi.Approvals);
        Assert.Equal(1, db.Scalar("SELECT COUNT(*) FROM recurring_lease_local_approvals;")); db.AssertNoExecution();
    }

    [Fact]
    public async Task ShutdownWhileApprovalUiIsOpenPreservesPendingAndCancelsUi()
    {
        using var db = new RecurringPlanSetupTestFixture(); var entered = Signal(); var closed = Signal();
        var ui = new FakeUi { Approve = async (_, token) =>
        {
            entered.TrySetResult();
            try { await Task.Delay(Timeout.InfiniteTimeSpan, token); }
            finally { closed.TrySetResult(); }
            return RecurringLeaseApprovalResult.HostShutdown;
        } };
        using var coordinator = db.Coordinator(ui); var created = coordinator.CreateOrGet(db.Request());
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var state = coordinator.Get(created.State!.SetupIntentId)!;
        Assert.Equal("pending_lease_approval", state.Status); Assert.Equal(1, state.StatusVersion);
        Assert.True(state.RequiresLocalAction); Assert.Equal("local_lease_approval", state.NextAction);
        var before = db.State();
        await Task.Run(coordinator.Dispose).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(closed.Task.IsCompleted); Assert.Equal(before, db.State()); Assert.Equal(0, coordinator.FlightCountForTests); db.AssertNoExecution();
    }

    [Fact]
    public async Task DisposeIsBoundedAndDoesNotDisposeResourcesUsedByLateFlight()
    {
        using var db = new RecurringPlanSetupTestFixture(); var entered = Signal(); var release = Signal();
        var ui = new FakeUi { Select = async _ => { entered.TrySetResult(); await release.Task; return FakeUi.Selected(); } };
        var coordinator = db.Coordinator(ui);
        try
        {
            coordinator.CreateOrGet(db.Request()); await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await Task.Run(coordinator.Dispose).WaitAsync(TimeSpan.FromSeconds(8));
            Assert.Equal(1, coordinator.FlightCountForTests);
            var nextUi = new FakeUi(); using var next = db.Coordinator(nextUi);
            Assert.Equal(1, next.FlightCountForTests); Assert.Equal(0, nextUi.Selections);
            Assert.Equal("region_selection_pending", db.Text("SELECT status_code FROM setup_intents;"));
            release.TrySetResult();
            await Task.WhenAll(coordinator.WaitForIdleAsync(), next.WaitForIdleAsync()).WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(0, coordinator.FlightCountForTests);
            Assert.Equal(1, nextUi.Selections); Assert.Equal(1, nextUi.Approvals);
            Assert.DoesNotContain(db.Audit.Events, e => e.Contains("ObjectDisposedException")); db.AssertNoExecution();
        }
        finally { release.TrySetResult(); coordinator.Dispose(); }
        Assert.Equal("activated", db.Text("SELECT status_code FROM setup_intents;"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TerminalVersusLocalApprovalKeepsOneCompleteChain(bool terminalFirst)
    {
        using var db = new RecurringPlanSetupTestFixture(); var entered = Signal(); var release = Signal();
        using var coordinator = db.Coordinator(new FakeUi(), async () => { entered.TrySetResult(); await release.Task; });
        var created = coordinator.CreateOrGet(db.Request()); var id = created.State!.SetupIntentId;
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        if (terminalFirst) Assert.True(db.Reject(id).Changed);
        release.TrySetResult(); await coordinator.WaitForIdleAsync().WaitAsync(TimeSpan.FromSeconds(10));
        if (!terminalFirst) Assert.Equal(RecurringSetupTerminalResultStatus.Conflict, db.Reject(id).Status);
        Assert.Equal(terminalFirst ? "rejected" : "scheduled", coordinator.Get(id)!.Status);
        Assert.Equal(terminalFirst ? "draft" : "enabled", db.Text("SELECT status_code FROM plans;"));
        Assert.Equal(terminalFirst ? "rejected" : "active", db.Text("SELECT status_code FROM recurring_consent_leases;"));
        Assert.Equal(terminalFirst ? 0 : 1, db.Scalar("SELECT COUNT(*) FROM recurring_lease_local_approvals;")); db.AssertNoExecution();
    }
}
