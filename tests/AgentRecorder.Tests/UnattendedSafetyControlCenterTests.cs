using System.Drawing;
using System.Reflection;
using System.Threading;
using System.Windows.Forms;
using AgentRecorder.App;
using AgentRecorder.Core;
using AgentRecorder.Core.Automation;
using AgentRecorder.Infrastructure;
using AgentRecorder.Logging;
using AgentRecorder.Persistence;
using Xunit.Abstractions;
using Xunit;

namespace AgentRecorder.Tests;

[Collection("NonParallel-AgentRecorderDataDir")]
public sealed class UnattendedSafetyControlCenterTests
{
    private const string UserSid = "S-1-5-21-control-center";
    private readonly ITestOutputHelper _testOutput;

    public UnattendedSafetyControlCenterTests(ITestOutputHelper testOutput) => _testOutput = testOutput;

    [Fact]
    public void QueryControlCenterReadsEmptyPendingActiveExpiredAndPersistentRevokedStates()
    {
        using var database = new TestDatabase();
        var service = new StandingLeaseSafetyControlService(database.Store, () => At(15));

        var empty = service.QueryControlCenter(UserSid);
        Assert.Equal(StandingLeaseControlCenterQueryStatus.Available, empty.Status);
        Assert.Empty(empty.State!.Items);

        CreateIntent(database, "a", prepare: true, activate: true);
        CreateIntent(database, "b", prepare: false, activate: false);
        CreateIntent(database, "c", prepare: true, activate: true);

        var pendingAndActive = service.QueryControlCenter(UserSid);
        Assert.Equal(3, pendingAndActive.State!.Items.Count);
        Assert.Contains(pendingAndActive.State.Items, item => item.IntentId == "intent-b" && item.LeaseStatus is null);
        Assert.Contains(pendingAndActive.State.Items, item => item.IntentId == "intent-a" && item.LeaseStatus == ConsentLeaseStatus.Active);

        var cScope = new SqliteAuthorizedCaptureScopeRepository(database.Store).GetById("scope-c");
        var expired = new StandingLeaseWindowEligibilityService(database.Store, () => At(100))
            .Evaluate(new StandingLeaseWindowEligibilityRequest(
                "plan-c", "occurrence-c", "lease-c", "scope-c", cScope.ScopeDigest));
        Assert.Equal(StandingLeaseWindowEligibilityStatus.Expired, expired.Status);

        var afterExpiry = service.QueryControlCenter(UserSid);
        var expiredItem = Assert.Single(afterExpiry.State!.Items, item => item.IntentId == "intent-c");
        Assert.Equal(PlanOccurrenceStatus.Expired, expiredItem.OccurrenceStatus);
        Assert.Equal("occurrence_window_expired", expiredItem.BlockedReason);

        var revoked = service.RevokeLease("intent-a", "control-center-revoke-a");
        Assert.Equal(StandingLeaseSafetyControlResultStatus.Changed, revoked.Status);
        var afterRevoke = service.QueryControlCenter(UserSid);
        var revokedItem = Assert.Single(afterRevoke.State!.Items, item => item.IntentId == "intent-a");
        Assert.Equal(ConsentLeaseStatus.Revoked, revokedItem.LeaseStatus);
        Assert.Equal(PlanOccurrenceStatus.Blocked, revokedItem.OccurrenceStatus);
        Assert.Equal(StandingLeaseSafetyReasonCodes.LeaseRevoked, revokedItem.BlockedReason);

        var reopened = new SqliteOperationalStore(database.Store.DatabasePath);
        reopened.Initialize();
        var persisted = new StandingLeaseSafetyControlService(reopened, () => At(15))
            .QueryControlCenter(UserSid);
        Assert.Equal(3, persisted.State!.Items.Count);
        Assert.Equal(ConsentLeaseStatus.Revoked, persisted.State.Items.Single(item => item.IntentId == "intent-a").LeaseStatus);
    }

    [Fact]
    public void QueryControlCenterShowsActiveRunAndFailsClosedForMissingGlobalState()
    {
        using var database = new TestDatabase();
        CreateIntent(database, "run", prepare: true, activate: true);
        var scope = new SqliteAuthorizedCaptureScopeRepository(database.Store).GetById("scope-run");
        var plan = new SqlitePlanDefinitionRepository(database.Store).Get("plan-run");
        var occurrence = new SqlitePlanOccurrenceRepository(database.Store).Get("occurrence-run");
        var lease = new SqliteConsentLeaseRepository(database.Store).Get("lease-run");
        var start = new SqlitePhase3StartGateTransaction(database.Store).Commit(
            new Phase3StartGateRequest(
                plan.Id,
                occurrence.Id,
                lease.Id,
                scope.ScopeId,
                scope.ScopeDigest,
                "run-control-center",
                "use-control-center",
                plan.Version,
                occurrence.Version,
                lease.Version,
                scope.ReservedDuration,
                At(15)));
        Assert.Equal(Phase3StartGateCommitStatus.Committed, start.Status);

        var service = new StandingLeaseSafetyControlService(database.Store, () => At(15));
        var query = service.QueryControlCenter(UserSid);
        var item = Assert.Single(query.State!.Items);
        Assert.True(item.ActiveRunPresent);
        Assert.True(item.RequiresActiveRunStop);

        Execute(database.Store, "DELETE FROM unattended_safety_state WHERE state_id = 'global';");
        var failed = service.QueryControlCenter(UserSid);
        Assert.Equal(StandingLeaseControlCenterQueryStatus.Rejected, failed.Status);
        Assert.Equal("safety_snapshot_invalid", failed.Reason);
    }

    [Fact]
    public void QueryControlCenterBindsRecurringLeasesToSidAndSessionAndShowsTerminalHistory()
    {
        using var fixture = new RecurringPlanSetupTestFixture();
        fixture.Create("active-a", prepared: true);
        var preparedA = fixture.Query.GetPrepared("active-a", RecurringPlanSetupTestFixture.Sid, RecurringPlanSetupTestFixture.Session)!;
        fixture.Activate("active-a");
        fixture.Create("active-b", prepared: true);
        fixture.Activate("active-b");
        fixture.Create("other-session", prepared: true, sid: RecurringPlanSetupTestFixture.Sid, session: "other-session");
        fixture.Activate("other-session", RecurringPlanSetupTestFixture.Sid, "other-session");
        fixture.Create("other-user", prepared: true, sid: "S-1-5-21-other", session: RecurringPlanSetupTestFixture.Session);
        fixture.Activate("other-user", "S-1-5-21-other", RecurringPlanSetupTestFixture.Session);

        var service = new StandingLeaseSafetyControlService(fixture.Store, () => RecurringPlanSetupTestFixture.At(10));
        var initial = service.QueryControlCenter(RecurringPlanSetupTestFixture.Sid, RecurringPlanSetupTestFixture.Session);
        Assert.Equal(StandingLeaseControlCenterQueryStatus.Available, initial.Status);
        Assert.Equal(2, initial.State!.RecurringItems.Count);
        Assert.All(initial.State.RecurringItems, item => Assert.Equal(ConsentLeaseStatus.Active, item.LeaseStatus));
        Assert.All(initial.State.RecurringItems, item => Assert.Equal(new DateOnly(2026, 1, 1), item.NextOccurrenceLocalDate));
        Assert.All(initial.State.RecurringItems, item => Assert.Equal(new TimeOnly(9, 30), item.NextOccurrenceLocalTime));
        Assert.All(initial.State.RecurringItems, item => Assert.Equal(2, item.RemainingUses));
        Assert.All(initial.State.RecurringItems, item => Assert.Equal(TimeSpan.FromSeconds(60), item.RemainingDuration));
        Assert.DoesNotContain(fixture.Root, string.Join("\n", initial.State.RecurringItems));

        var selected = initial.State.RecurringItems.Single(item => item.PlanId == preparedA.PlanId);
        var revoked = service.RevokeRecurringLease(selected.LeaseId, "task289-exact-revoke", "control_center_user");
        Assert.Equal(StandingLeaseSafetyControlResultStatus.Changed, revoked.Status);
        var replay = service.RevokeRecurringLease(selected.LeaseId, "task289-exact-revoke", "control_center_user");
        Assert.Equal(StandingLeaseSafetyControlResultStatus.AlreadyApplied, replay.Status);

        var after = service.QueryControlCenter(RecurringPlanSetupTestFixture.Sid, RecurringPlanSetupTestFixture.Session);
        Assert.Equal(new[] { ConsentLeaseStatus.Active, ConsentLeaseStatus.Revoked }, after.State!.RecurringItems.Select(item => item.LeaseStatus));
        Assert.False(after.State.RecurringItems.Single(item => item.LeaseId == selected.LeaseId).CanRevoke);
        Assert.True(after.State.RecurringItems.Single(item => item.LeaseId != selected.LeaseId).CanRevoke);
        Assert.Equal(1L, fixture.Scalar("SELECT COUNT(*) FROM standing_lease_safety_operations WHERE operation_id = 'task289-exact-revoke';"));
    }

    [Fact]
    public void QueryControlCenterFailsClosedForCorruptRecurringOwnerBinding()
    {
        using var fixture = new RecurringPlanSetupTestFixture();
        fixture.Create("corrupt", prepared: true);
        fixture.Activate("corrupt");
        fixture.Corrupt("UPDATE recurring_lease_local_approvals SET plan_id = 'plan-tampered' WHERE current_user_sid = 'S-1-5-21-recurring-setup' AND session_binding = 'recurring-setup-session';");

        var query = new StandingLeaseSafetyControlService(fixture.Store, () => RecurringPlanSetupTestFixture.At(10))
            .QueryControlCenter(RecurringPlanSetupTestFixture.Sid, RecurringPlanSetupTestFixture.Session);

        Assert.Equal(StandingLeaseControlCenterQueryStatus.Rejected, query.Status);
        Assert.Equal("safety_snapshot_invalid", query.Reason);
        Assert.Null(query.State);
    }

    [Fact]
    public void ControlFormUsesLocalizedStateConfirmationAndUniqueOperations()
    {
        RunOnSta(() =>
        {
            var gateway = new FakeGateway(CreateState(ConsentLeaseStatus.Active));
            var confirmation = new FakeConfirmation { NextAnswer = true };
            using var form = new UnattendedSafetyControlForm(
                gateway,
                new UiTextProvider(UiLanguage.ZhCn),
                confirmation);
            form.PerformLayout();

            Assert.Contains("无人值守安全控制", form.Text);
            Assert.Contains("已启用", form.GlobalStatusTextForTests);
            Assert.Equal(1, form.LeaseCardCountForTests);
            Assert.Equal(0, form.RecurringLeaseCardCountForTests);
            Assert.Same(form.RefreshButtonForTests, form.AcceptButton);
            Assert.Same(form.CloseButtonForTests, form.CancelButton);

            var card = (TableLayoutPanel)form.LeaseCardForTests(0).Controls[0];
            var revoke = (Button)card.GetControlFromPosition(1, 0)!;
            ClickWithoutShowing(revoke);
            Assert.True(confirmation.CallCount == 1);
            Assert.Single(gateway.Operations);
            Assert.StartsWith("control-center-revoke-", gateway.Operations[0].OperationId);

            gateway.State = CreateState(ConsentLeaseStatus.Active);
            revoke = (Button)((TableLayoutPanel)form.LeaseCardForTests(0).Controls[0]).GetControlFromPosition(1, 0)!;
            ClickWithoutShowing(revoke);
            Assert.Equal(2, gateway.Operations.Count);
            Assert.NotEqual(gateway.Operations[0].OperationId, gateway.Operations[1].OperationId);

            form.UpdateLanguage(new UiTextProvider(UiLanguage.EnUs));
            Assert.Contains("Unattended Safety Control", form.Text);
            Assert.Contains("Enabled", form.GlobalStatusTextForTests);
        });
    }

    [Fact]
    public void ControlFormShowsCompleteFutureWindowGrantAndConfirmsExactLocalRevoke()
    {
        RunOnSta(() =>
        {
            var grant = new AgentRecorder.Api.FutureWindowAuthorizationState(
                "fwa_0123456789abcdef0123456789abcdef", "active", null,
                @"C:\Player\player.exe", "00AB12CD:0000000000000042", new string('a', 64),
                "CN=Publisher", new string('b', 64), "system_loopback", "endpoint-1", "Speakers",
                300, 900, @"D:\Recordings\future-window-test.mp4", At(1), At(2), At(902),
                null, null, null, null, null, null, null, 1);
            var gateway = new FakeGateway(CreateState(ConsentLeaseStatus.Active))
            {
                FutureWindowGrants = new[] { grant },
            };
            var confirmation = new FakeConfirmation { NextAnswer = true };
            using var form = new UnattendedSafetyControlForm(
                gateway, new UiTextProvider(UiLanguage.EnUs), confirmation);
            form.PerformLayout();

            Assert.Equal(1, form.FutureWindowCardCountForTests);
            var futurePanel = (FlowLayoutPanel)form.LeaseListsForTests.GetControlFromPosition(0, 2)!.Controls[0];
            var grantCard = (Panel)futurePanel.Controls.Cast<Control>().Single(control =>
                string.Equals(control.Tag as string, "future:" + grant.AuthorizationId, StringComparison.Ordinal));
            var card = (TableLayoutPanel)grantCard.Controls[0];
            var details = (Label)card.GetControlFromPosition(0, 0)!;
            Assert.Contains(grant.ExecutablePath, details.Text);
            Assert.Contains(grant.ExecutableSha256, details.Text);
            Assert.Contains(grant.OutputPath, details.Text);
            Assert.Contains("Speakers", details.Text);
            Assert.Contains("latest permissible start", details.Text, StringComparison.OrdinalIgnoreCase);
            Assert.Contains(grant.ExpiresAtUtc!.Value.AddSeconds(-grant.MaximumDurationSeconds).ToString("u"), details.Text);

            var revoke = (Button)card.GetControlFromPosition(1, 0)!;
            ClickWithoutShowing(revoke);

            Assert.Equal(1, confirmation.CallCount);
            Assert.Contains(grant.AuthorizationId, confirmation.LastMessage);
            Assert.Equal(grant.AuthorizationId, gateway.RevokedFutureAuthorizationId);
            Assert.Contains("revoked", form.ResultTextForTests, StringComparison.OrdinalIgnoreCase);
        });
    }

    [Fact]
    public void GlobalSafetyControlShowsWhenDurableActionCommittedButPhysicalStopFailed()
    {
        RunOnSta(() =>
        {
            var gateway = new FakeGateway(CreateState(ConsentLeaseStatus.Active))
            {
                GlobalControlRequiresStopForTest = true,
                GlobalControlStopFailedForTest = true,
            };
            using var form = new UnattendedSafetyControlForm(
                gateway,
                new UiTextProvider(UiLanguage.EnUs),
                new FakeConfirmation { NextAnswer = true });
            form.PerformLayout();

            ClickWithoutShowing(form.StopAllButtonForTests);

            Assert.Contains("did not confirm stopping", form.ResultTextForTests, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(Color.DarkRed, form.ResultColorForTests);
        });
    }

    [Fact]
    public void ControlFormRequiresConfirmationDisablesActionsDuringWriteAndFailsClosedOnQueryError()
    {
        RunOnSta(() =>
        {
            var gateway = new FakeGateway(CreateState(ConsentLeaseStatus.Active));
            var confirmation = new FakeConfirmation { NextAnswer = false };
            using var form = new UnattendedSafetyControlForm(
                gateway,
                new UiTextProvider(UiLanguage.EnUs),
                confirmation);
            form.PerformLayout();

            ClickWithoutShowing(form.StopAllButtonForTests);
            Assert.Equal(1, confirmation.CallCount);
            Assert.Empty(gateway.Operations);

            confirmation.NextAnswer = true;
            gateway.BusyProbe = () =>
                form.BusyForTests &&
                !form.StopAllButtonForTests.Enabled &&
                !form.DisableButtonForTests.Enabled &&
                !form.EnableButtonForTests.Enabled &&
                !form.RefreshButtonForTests.Enabled;
            ClickWithoutShowing(form.DisableButtonForTests);
            Assert.Equal(2, confirmation.CallCount);
            Assert.Contains(gateway.Operations, operation => operation.Kind == "disable");
            Assert.True(gateway.BusyObserved);

            gateway.QueryResult = StandingLeaseControlCenterQueryResult.Rejected("safety_sqlite_failure");
            ClickWithoutShowing(form.RefreshButtonForTests);
            Assert.Contains("unavailable", form.GlobalStatusTextForTests, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(0, form.LeaseCardCountForTests);
            Assert.DoesNotContain("Completed", form.ResultTextForTests);
            Assert.False(form.StopAllButtonForTests.Enabled);
            Assert.False(form.DisableButtonForTests.Enabled);
            Assert.False(form.EnableButtonForTests.Enabled);
        });
    }

    [Fact]
    public void ControlFormRendersLocalizedRecurringCardsAndRevokesTheConfirmedExactLease()
    {
        RunOnSta(() =>
        {
            var state = CreateState(
                ConsentLeaseStatus.Active,
                new[]
                {
                    CreateRecurringItem("plan-current", "lease-current", ConsentLeaseStatus.Active, activeRun: true),
                    CreateRecurringItem("plan-history", "lease-history", ConsentLeaseStatus.Revoked),
                });
            var gateway = new FakeGateway(state) { RecurringRequiresStopForTest = true };
            var confirmation = new FakeConfirmation { NextAnswer = true };
            using var form = new UnattendedSafetyControlForm(
                gateway,
                new UiTextProvider(UiLanguage.EnUs),
                confirmation);
            form.PerformLayout();

            Assert.Equal(1, form.LeaseCardCountForTests);
            Assert.Equal(2, form.RecurringLeaseCardCountForTests);
            var activeCard = (TableLayoutPanel)form.RecurringLeaseCardForTests(0).Controls[0];
            var terminalCard = (TableLayoutPanel)form.RecurringLeaseCardForTests(1).Controls[0];
            var activeDetails = (Label)activeCard.GetControlFromPosition(0, 0)!;
            var revoke = (Button)activeCard.GetControlFromPosition(1, 0)!;
            Assert.Contains("plan-current", activeDetails.Text);
            Assert.Contains("lease-current", activeDetails.Text);
            Assert.Contains("Weekly", activeDetails.Text);
            Assert.Contains("Monday, Friday", activeDetails.Text);
            Assert.Contains("Active Run: Yes", activeDetails.Text);
            Assert.True(revoke.Enabled);
            Assert.False(((Button)terminalCard.GetControlFromPosition(1, 0)!).Enabled);
            Assert.InRange(activeDetails.MaximumSize.Width, 220, form.ClientSize.Width);

            ClickWithoutShowing(revoke);
            Assert.Equal(1, confirmation.CallCount);
            Assert.Contains("plan-current", confirmation.LastMessage);
            Assert.Contains("lease-current", confirmation.LastMessage);
            var action = Assert.Single(gateway.Operations, operation => operation.Kind == "recurring_revoke");
            Assert.Equal("lease-current", action.IntentId);
            Assert.StartsWith("control-center-recurring-revoke-", action.OperationId);
            Assert.Contains("stop request was sent", form.ResultTextForTests, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("does not imply media settlement is complete", form.ResultTextForTests, StringComparison.OrdinalIgnoreCase);

            form.UpdateLanguage(new UiTextProvider(UiLanguage.ZhCn));
            Assert.Equal("周期授权", FindTaggedControl(form, "recurring_heading")!.Text);
            Assert.False(form.BusyForTests);
            form.RefreshFromTray();
            var localizedDetails = (Label)((TableLayoutPanel)form.RecurringLeaseCardForTests(0).Controls[0])
                .GetControlFromPosition(0, 0)!;
            Assert.Contains("每周", localizedDetails.Text);
            Assert.Contains("周一, 周五", localizedDetails.Text);
            ClickWithoutShowing((Button)((TableLayoutPanel)form.RecurringLeaseCardForTests(0).Controls[0])
                .GetControlFromPosition(1, 0)!);
            Assert.Contains("已向此 Lease 对应的 Run 发送定向停止请求", form.ResultTextForTests);
            Assert.Contains("不代表媒体结算已完成", form.ResultTextForTests);
            Assert.Contains("周期计划 plan-current", confirmation.LastMessage);
            Assert.Contains("授权 lease-current", confirmation.LastMessage);
            Assert.Contains("只会请求停止此 Lease 对应的 Run", confirmation.LastMessage);
            Assert.Contains("媒体结算仍会独立进行", confirmation.LastMessage);
        });
    }

    [Fact]
    public void ControlFormCompactsEmptySectionsAndKeepsBothPopulatedSectionsScrollable()
    {
        RunOnSta(() =>
        {
            var gateway = new FakeGateway(CreateState(
                ConsentLeaseStatus.Active,
                Array.Empty<StandingLeaseControlCenterRecurringLeaseSummary>(),
                includeOneShot: false));
            using var form = new UnattendedSafetyControlForm(gateway, new UiTextProvider(UiLanguage.ZhCn));
            form.PerformLayout();

            Assert.Equal(0, form.LeaseCardCountForTests);
            Assert.Equal(0, form.RecurringLeaseCardCountForTests);
            Assert.Equal("暂无一次性授权。", FindTaggedControl(form, "empty_state_one_shot")!.Text);
            Assert.Equal("暂无周期授权。", FindTaggedControl(form, "empty_state_recurring")!.Text);
            AssertCompactRow(form.LeaseListsForTests, row: 0);
            AssertCompactRow(form.LeaseListsForTests, row: 1);

            SetGatewayState(gateway, CreateState(
                ConsentLeaseStatus.Active,
                new[] { CreateRecurringItem("plan-recurring-only", "lease-recurring-only", ConsentLeaseStatus.Active) },
                includeOneShot: false));
            ClickWithoutShowing(form.RefreshButtonForTests);
            Assert.Equal(0, form.LeaseCardCountForTests);
            Assert.Equal(1, form.RecurringLeaseCardCountForTests);
            AssertCompactRow(form.LeaseListsForTests, row: 0);
            Assert.Equal(SizeType.Percent, form.LeaseListsForTests.RowStyles[1].SizeType);
            Assert.Equal(100, form.LeaseListsForTests.RowStyles[1].Height);

            form.UpdateLanguage(new UiTextProvider(UiLanguage.EnUs));
            Assert.Equal("No one-time authorizations.", FindTaggedControl(form, "empty_state_one_shot")!.Text);
            Assert.Equal("Recurring authorizations", FindTaggedControl(form, "recurring_heading")!.Text);

            SetGatewayState(gateway, CreateState(ConsentLeaseStatus.Active));
            ClickWithoutShowing(form.RefreshButtonForTests);
            Assert.Equal(1, form.LeaseCardCountForTests);
            Assert.Equal(0, form.RecurringLeaseCardCountForTests);
            Assert.Equal(SizeType.Percent, form.LeaseListsForTests.RowStyles[0].SizeType);
            Assert.Equal(100, form.LeaseListsForTests.RowStyles[0].Height);
            AssertCompactRow(form.LeaseListsForTests, row: 1);

            SetGatewayState(gateway, CreateState(
                ConsentLeaseStatus.Active,
                new[] { CreateRecurringItem("plan-both", "lease-both", ConsentLeaseStatus.Active) }));
            ClickWithoutShowing(form.RefreshButtonForTests);
            Assert.Equal(1, form.LeaseCardCountForTests);
            Assert.Equal(1, form.RecurringLeaseCardCountForTests);
            Assert.Equal(SizeType.Percent, form.LeaseListsForTests.RowStyles[0].SizeType);
            Assert.Equal(SizeType.Percent, form.LeaseListsForTests.RowStyles[1].SizeType);
            Assert.Equal(50, form.LeaseListsForTests.RowStyles[0].Height);
            Assert.Equal(50, form.LeaseListsForTests.RowStyles[1].Height);
            Assert.True(form.LeaseCardForTests(0).Parent is FlowLayoutPanel { AutoScroll: true });
            Assert.True(form.RecurringLeaseCardForTests(0).Parent is FlowLayoutPanel { AutoScroll: true });

            SetGatewayState(gateway, CreateState(
                ConsentLeaseStatus.Active,
                new[]
                {
                    CreateRecurringItem("plan-exhausted-active-refresh", "lease-exhausted-active-refresh", ConsentLeaseStatus.Exhausted, activeRun: true),
                },
                includeOneShot: false));
            ClickWithoutShowing(form.RefreshButtonForTests);
            Assert.Equal(0, form.LeaseCardCountForTests);
            Assert.Equal(1, form.RecurringLeaseCardCountForTests);
            var refreshedCard = (TableLayoutPanel)form.RecurringLeaseCardForTests(0).Controls[0];
            var refreshedDetails = (Label)refreshedCard.GetControlFromPosition(0, 0)!;
            var refreshedRevoke = (Button)refreshedCard.GetControlFromPosition(1, 0)!;
            Assert.Contains("Exhausted", refreshedDetails.Text);
            Assert.Contains("Active Run: Yes", refreshedDetails.Text);
            Assert.True(refreshedRevoke.Enabled);

            var confirmation = new FakeConfirmation { NextAnswer = true };
            using var refreshedForm = new UnattendedSafetyControlForm(
                new FakeGateway(gateway.State) { RecurringRequiresStopForTest = true },
                new UiTextProvider(UiLanguage.EnUs),
                confirmation);
            refreshedForm.Size = refreshedForm.MinimumSize;
            refreshedForm.PerformLayout();
            AssertControlBoundsFit(refreshedForm);
            ClickWithoutShowing((Button)((TableLayoutPanel)refreshedForm.RecurringLeaseCardForTests(0).Controls[0])
                .GetControlFromPosition(1, 0)!);
            Assert.Contains("Recurring Lease revoked", refreshedForm.ResultTextForTests);
            Assert.Contains("stop request was sent", refreshedForm.ResultTextForTests, StringComparison.OrdinalIgnoreCase);
            AssertControlBoundsFit(refreshedForm);
        });
    }

    [Fact]
    public void ControlFormLayoutRemainsNonOverlappingWhenScaledAtMinimumSize()
    {
        RunOnSta(() =>
        {
            var gateway = new FakeGateway(CreateState(
                ConsentLeaseStatus.Active,
                new[] { CreateRecurringItem("plan-scale", "lease-scale", ConsentLeaseStatus.Active) },
                includeOneShot: false,
                stopAllApplied: true));
            using var form = new UnattendedSafetyControlForm(gateway, new UiTextProvider(UiLanguage.ZhCn));
            var baseMinimum = form.MinimumSize;
            var previousScale = 1f;
            foreach (var scale in new[] { 1f, 1.5f, 2f })
            {
                if (scale != 1f)
                    form.Scale(new SizeF(scale / previousScale, scale / previousScale));
                previousScale = scale;
                form.MinimumSize = new Size(
                    (int)Math.Ceiling(baseMinimum.Width * scale),
                    (int)Math.Ceiling(baseMinimum.Height * scale));
                form.Size = form.MinimumSize;
                form.PerformLayout();
                AssertControlBoundsFit(form);
                AssertEmptyStateBoundsFit(form);
                var emptyState = FindTaggedControl(form, "empty_state_one_shot")!;
                var card = form.RecurringLeaseCardForTests(0);
                var content = (TableLayoutPanel)card.Controls[0];
                var revoke = content.GetControlFromPosition(1, 0)!;
                var stopValue = FindTaggedControl(form, "stop_summary_value")!;
                _testOutput.WriteLine(
                    $"scale={scale:0.##}x; minClient={form.ClientSize.Width}x{form.ClientSize.Height}; " +
                    $"emptyRow={form.LeaseListsForTests.RowStyles[0].Height:0}; emptyState={emptyState.Bounds}; " +
                    $"leaseLists={form.LeaseListsForTests.Bounds}; recurringGroup={card.Parent!.Parent!.Bounds}; " +
                    $"recurringList={card.Parent.Bounds}/{card.Parent.ClientSize}; " +
                    $"recurringCard={card.Bounds}; revoke={revoke.Bounds}; stopSummary={stopValue.Bounds}");
            }
        });
    }

    [Fact]
    public void ExhaustedRecurringLeaseIsRevocableOnlyWhileItsExactRunRemainsActive()
    {
        RunOnSta(() =>
        {
            var state = CreateState(
                ConsentLeaseStatus.Active,
                new[]
                {
                    CreateRecurringItem("plan-exhausted-active", "lease-exhausted-active", ConsentLeaseStatus.Exhausted, activeRun: true),
                    CreateRecurringItem("plan-exhausted-idle", "lease-exhausted-idle", ConsentLeaseStatus.Exhausted),
                });
            var gateway = new FakeGateway(state) { RecurringRequiresStopForTest = true };
            var confirmation = new FakeConfirmation { NextAnswer = true };
            using var form = new UnattendedSafetyControlForm(
                gateway,
                new UiTextProvider(UiLanguage.EnUs),
                confirmation);

            var activeCard = (TableLayoutPanel)form.RecurringLeaseCardForTests(0).Controls[0];
            var idleCard = (TableLayoutPanel)form.RecurringLeaseCardForTests(1).Controls[0];
            var activeRevoke = (Button)activeCard.GetControlFromPosition(1, 0)!;
            var idleRevoke = (Button)idleCard.GetControlFromPosition(1, 0)!;
            Assert.True(activeRevoke.Enabled);
            Assert.False(idleRevoke.Enabled);

            ClickWithoutShowing(activeRevoke);

            Assert.Contains(gateway.Operations, operation =>
                operation.Kind == "recurring_revoke" && operation.IntentId == "lease-exhausted-active");
            Assert.DoesNotContain(gateway.Operations, operation => operation.IntentId == "lease-exhausted-idle");
            Assert.Contains("plan-exhausted-active", confirmation.LastMessage);
            Assert.Contains("lease-exhausted-active", confirmation.LastMessage);
        });
    }

    [Fact]
    public void StaleRecurringCardRefreshesAndDoesNotConfirmOrRevokeTerminalLease()
    {
        RunOnSta(() =>
        {
            var stale = CreateState(
                ConsentLeaseStatus.Active,
                new[] { CreateRecurringItem("plan-stale", "lease-stale", ConsentLeaseStatus.Active, activeRun: true) });
            var current = CreateState(
                ConsentLeaseStatus.Active,
                new[] { CreateRecurringItem("plan-stale", "lease-stale", ConsentLeaseStatus.Exhausted) });
            var gateway = new FakeGateway(stale)
            {
                QuerySequence = new Queue<StandingLeaseControlCenterQueryResult>(new[]
                {
                    StandingLeaseControlCenterQueryResult.Available(stale),
                    StandingLeaseControlCenterQueryResult.Available(current),
                }),
                QueryResult = StandingLeaseControlCenterQueryResult.Available(current),
            };
            var confirmation = new FakeConfirmation { NextAnswer = true };
            using var form = new UnattendedSafetyControlForm(
                gateway,
                new UiTextProvider(UiLanguage.EnUs),
                confirmation);
            var card = (TableLayoutPanel)form.RecurringLeaseCardForTests(0).Controls[0];
            var revoke = (Button)card.GetControlFromPosition(1, 0)!;
            Assert.True(revoke.Enabled);

            ClickWithoutShowing(revoke);

            Assert.Empty(gateway.Operations);
            Assert.Equal(0, confirmation.CallCount);
            Assert.False(((Button)((TableLayoutPanel)form.RecurringLeaseCardForTests(0).Controls[0])
                .GetControlFromPosition(1, 0)!).Enabled);
            Assert.Contains("no revoke was issued", form.ResultTextForTests, StringComparison.OrdinalIgnoreCase);
        });
    }

    [Fact]
    public void RecurringStopFailureAndNoOpAreNotReportedAsRecordingCompletion()
    {
        RunOnSta(() =>
        {
            var state = CreateState(
                ConsentLeaseStatus.Active,
                new[] { CreateRecurringItem("plan-stop-result", "lease-stop-result", ConsentLeaseStatus.Active, activeRun: true) });
            var failedGateway = new FakeGateway(state)
            {
                RecurringRequiresStopForTest = true,
                RecurringStopFailedForTest = true,
            };
            var confirmation = new FakeConfirmation { NextAnswer = true };
            using var failedForm = new UnattendedSafetyControlForm(
                failedGateway,
                new UiTextProvider(UiLanguage.EnUs),
                confirmation);
            ClickWithoutShowing((Button)((TableLayoutPanel)failedForm.RecurringLeaseCardForTests(0).Controls[0])
                .GetControlFromPosition(1, 0)!);
            Assert.Contains("no global stop was issued", failedForm.ResultTextForTests, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("completed", failedForm.ResultTextForTests, StringComparison.OrdinalIgnoreCase);

            var noOpGateway = new FakeGateway(state)
            {
                RecurringRequiresStopForTest = true,
                RecurringStopNoOpForTest = true,
            };
            using var noOpForm = new UnattendedSafetyControlForm(
                noOpGateway,
                new UiTextProvider(UiLanguage.EnUs),
                new FakeConfirmation { NextAnswer = true });
            ClickWithoutShowing((Button)((TableLayoutPanel)noOpForm.RecurringLeaseCardForTests(0).Controls[0])
                .GetControlFromPosition(1, 0)!);
            Assert.Contains("no additional stop was sent", noOpForm.ResultTextForTests, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("completed", noOpForm.ResultTextForTests, StringComparison.OrdinalIgnoreCase);
        });
    }

    private static StandingLeaseControlCenterState CreateState(
        ConsentLeaseStatus leaseStatus,
        IReadOnlyList<StandingLeaseControlCenterRecurringLeaseSummary>? recurringItems = null,
        bool includeOneShot = true,
        bool stopAllApplied = false)
    {
        var now = At(1);
        return new StandingLeaseControlCenterState(
            UnattendedModeStatus.Enabled,
            stopAllApplied,
            stopAllApplied ? "stop-all-ui" : null,
            stopAllApplied ? "stop_all_reason_with_a_long_localized_summary_that_requires_wrapping_to_the_available_status_column_without_hiding_the_controls" : null,
            stopAllApplied ? At(0) : null,
            stopAllApplied ? At(1) : null,
            includeOneShot
                ? new[]
                {
                    new StandingLeaseControlCenterLeaseSummary(
                        "intent-ui",
                        StandingSetupIntentStatus.Activated,
                        At(3600),
                        At(10),
                        At(20),
                        At(80),
                        TimeSpan.FromSeconds(30),
                        At(3600),
                        "plan-ui",
                        "occurrence-ui",
                        "lease-ui",
                        leaseStatus,
                        PlanOccurrenceStatus.Authorized,
                        leaseStatus == ConsentLeaseStatus.Revoked ? StandingLeaseSafetyReasonCodes.LeaseRevoked : null,
                        null,
                        false,
                        false),
                }
                : Array.Empty<StandingLeaseControlCenterLeaseSummary>(),
            recurringItems ?? Array.Empty<StandingLeaseControlCenterRecurringLeaseSummary>());
    }

    private static void AssertCompactRow(TableLayoutPanel panel, int row)
    {
        Assert.Equal(SizeType.Absolute, panel.RowStyles[row].SizeType);
        Assert.InRange(panel.RowStyles[row].Height, 64, 120);
    }

    private static void SetGatewayState(FakeGateway gateway, StandingLeaseControlCenterState state)
    {
        gateway.State = state;
        gateway.QueryResult = StandingLeaseControlCenterQueryResult.Available(state);
    }

    private static void AssertControlBoundsFit(UnattendedSafetyControlForm form)
    {
        form.PerformLayout();
        var statusHeading = FindTaggedControl(form, "status_heading")!;
        var stopHeading = FindTaggedControl(form, "stop_heading")!;
        var globalValue = FindTaggedControl(form, "global_status_value")!;
        var stopValue = FindTaggedControl(form, "stop_summary_value")!;
        Assert.True(statusHeading.Width >= statusHeading.PreferredSize.Width);
        Assert.True(stopHeading.Width >= stopHeading.PreferredSize.Width);
        Assert.Same(statusHeading.Parent, globalValue.Parent);
        Assert.Same(stopHeading.Parent, stopValue.Parent);
        Assert.False(statusHeading.Bounds.IntersectsWith(globalValue.Bounds), "The global status heading overlaps its value.");
        Assert.False(stopHeading.Bounds.IntersectsWith(stopValue.Bounds), "The Stop All heading overlaps its value.");
        Assert.True(stopValue.Right <= stopValue.Parent!.ClientSize.Width);
        Assert.True(stopValue.Parent.Right <= stopValue.Parent.Parent!.ClientSize.Width);
        Assert.True(stopValue.Height >= stopValue.PreferredSize.Height, "The Stop All status value is vertically clipped.");
        var result = FindTaggedControl(form, "result")!;
        Assert.True(result.Right <= result.Parent!.ClientSize.Width, "The operation result is clipped horizontally.");
        Assert.True(result.Parent.Right <= result.Parent.Parent!.ClientSize.Width, "The result area extends past the window content.");
        Assert.True(result.Height >= result.PreferredSize.Height, "The operation result is vertically clipped.");

        foreach (var card in Enumerable.Range(0, form.LeaseCardCountForTests)
                     .Select(form.LeaseCardForTests)
                     .Concat(Enumerable.Range(0, form.RecurringLeaseCardCountForTests).Select(form.RecurringLeaseCardForTests)))
        {
            card.PerformLayout();
            Assert.True(card.Right <= card.Parent!.ClientSize.Width, $"Card extends past its list viewport: {card.Bounds}");
            var content = Assert.IsType<TableLayoutPanel>(card.Controls[0]);
            content.PerformLayout();
            var details = content.Controls[0];
            var revoke = content.Controls[1];
            Assert.False(details.Bounds.IntersectsWith(revoke.Bounds), "Card details overlap the revoke action.");
            Assert.True(revoke.Right <= content.ClientSize.Width, "Revoke action extends past the card content area.");
            if (card.Parent is FlowLayoutPanel list && list.Controls.GetChildIndex(card) == 0)
            {
                Assert.True(
                    card.Width >= list.ClientSize.Width - list.Padding.Horizontal - SystemInformation.VerticalScrollBarWidth - 8,
                    "The populated category is not using its available card width.");
                var revokeInList = revoke.Bounds;
                revokeInList.Offset(content.Left + card.Left, content.Top + card.Top);
                Assert.True(revokeInList.Bottom <= list.ClientSize.Height, "The first card's revoke action is below the visible list viewport.");
            }
        }
    }

    private static void AssertEmptyStateBoundsFit(UnattendedSafetyControlForm form)
    {
        foreach (var tag in new[] { "empty_state_one_shot", "empty_state_recurring" })
        {
            if (FindTaggedControl(form, tag) is not { } emptyState)
                continue;

            Assert.True(emptyState.Left >= 0 && emptyState.Top >= 0);
            Assert.True(emptyState.Right <= emptyState.Parent!.ClientSize.Width, $"{tag} is clipped horizontally.");
            Assert.True(emptyState.Bottom <= emptyState.Parent.ClientSize.Height, $"{tag} is clipped vertically.");
        }
    }

    private static Control? FindTaggedControl(Control parent, string tag)
    {
        foreach (Control child in parent.Controls)
        {
            if (string.Equals(child.Tag as string, tag, StringComparison.Ordinal))
                return child;
            var nested = FindTaggedControl(child, tag);
            if (nested is not null)
                return nested;
        }

        return null;
    }

    private static void ClickWithoutShowing(Control control) =>
        typeof(Control).GetMethod("OnClick", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(control, new object[] { EventArgs.Empty });

    private static StandingLeaseControlCenterRecurringLeaseSummary CreateRecurringItem(
        string planId,
        string leaseId,
        ConsentLeaseStatus status,
        bool activeRun = false) => new(
            planId,
            leaseId,
            RecurringScheduleKind.Weekly,
            "Pacific Standard Time",
            new DateOnly(2026, 1, 1),
            new DateOnly(2026, 1, 31),
            new TimeOnly(9, 30),
            new[] { DayOfWeek.Monday, DayOfWeek.Friday },
            new DateOnly(2026, 1, 5),
            new TimeOnly(9, 30),
            false,
            status,
            At(3600),
            3,
            TimeSpan.FromMinutes(2),
            activeRun);

    private static void CreateIntent(TestDatabase database, string suffix, bool prepare, bool activate)
    {
        var intentId = "intent-" + suffix;
        var created = new StandingSetupIntentSnapshotForTests(intentId, suffix);
        var create = new StandingSetupIntentService(database.Store, () => At(1)).CreateOrGet(created.Intent);
        Assert.Equal(StandingSetupIntentResultStatus.Created, create.Result);
        if (!prepare)
            return;

        var selection = StandingFixedRegionSelectionSnapshot.CreateForTrustedLocalSelectionAdapter(
            intentId,
            UserSid,
            "session-control-center",
            At(2),
            "display-" + suffix,
            new AuthorizedPhysicalRectangle(0, 0, 1920, 1080),
            new AuthorizedPhysicalRectangle(10, 20, 640, 480),
            96,
            96,
            1920,
            1080,
            AuthorizedDisplayOrientation.Landscape,
            "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef");
        var prepared = new StandingLeasePreparationService(
            database.Store,
            () => At(2),
            new FixedIds(suffix)).Prepare(selection);
        Assert.True(
            prepared.Status == StandingLeasePreparationResultStatus.Prepared,
            prepared.Reason);
        if (!activate)
            return;

        var digest = new SqliteAuthorizedCaptureScopeRepository(database.Store).GetById("scope-" + suffix).ScopeDigest;
        var receipt = StandingLeaseLocalApprovalReceipt.CreateForTest(
            "approval-" + suffix,
            "plan-" + suffix,
            "occurrence-" + suffix,
            "lease-" + suffix,
            "scope-" + suffix,
            digest,
            UserSid,
            "session-control-center",
            At(3));
        var activated = new StandingLeasePreparedIntentApprovalActivationService(database.Store, () => At(3))
            .Activate(intentId, receipt);
        Assert.Equal(StandingLeaseAuthorizationActivationStatus.Activated, activated.Status);
    }

    private static void Execute(SqliteOperationalStore store, string sql)
    {
        using var connection = store.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private static void RunOnSta(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try { action(); }
            catch (Exception exception) { failure = exception; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure is not null)
            throw new TargetInvocationException(failure);
    }

    private static T GetPrivateField<T>(object target, string name) =>
        (T)target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(target)!;

    private static DateTimeOffset At(int seconds) =>
        new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero).AddSeconds(seconds);

    private sealed class FakeConfirmation : IUnattendedSafetyConfirmation
    {
        public bool NextAnswer { get; set; }
        public int CallCount { get; private set; }
        internal string LastMessage { get; private set; } = string.Empty;

        public bool Confirm(IWin32Window owner, string title, string message)
        {
            CallCount++;
            LastMessage = message;
            return NextAnswer;
        }
    }

    private sealed class FakeGateway : IUnattendedSafetyControlGateway
    {
        internal StandingLeaseControlCenterState State { get; set; }
        internal StandingLeaseControlCenterQueryResult QueryResult { get; set; }
        internal Func<bool>? BusyProbe { get; set; }
        internal bool BusyObserved { get; private set; }
        internal bool RecurringRequiresStopForTest { get; set; }
        internal bool RecurringStopFailedForTest { get; set; }
        internal bool RecurringStopNoOpForTest { get; set; }
        internal bool GlobalControlRequiresStopForTest { get; set; }
        internal bool GlobalControlStopFailedForTest { get; set; }
        internal Queue<StandingLeaseControlCenterQueryResult>? QuerySequence { get; set; }
        internal List<(string Kind, string OperationId, string? IntentId)> Operations { get; } = new();
        internal IReadOnlyList<AgentRecorder.Api.FutureWindowAuthorizationState> FutureWindowGrants { get; set; } =
            Array.Empty<AgentRecorder.Api.FutureWindowAuthorizationState>();
        internal string? RevokedFutureAuthorizationId { get; private set; }

        internal FakeGateway(StandingLeaseControlCenterState state)
        {
            State = state;
            QueryResult = StandingLeaseControlCenterQueryResult.Available(state);
        }

        public StandingLeaseControlCenterQueryResult Query() =>
            QuerySequence is { Count: > 0 } ? QuerySequence.Dequeue() : QueryResult;

        public StandingLeaseSafetyControlResult RevokeLease(string intentId, string operationId)
        {
            Operations.Add(("revoke", operationId, intentId));
            return StandingLeaseSafetyControlResult.ChangedResult(operationId, StandingLeaseSafetyReasonCodes.LeaseRevoked);
        }

        public StandingLeaseSafetyControlResult RevokeRecurringLease(string leaseId, string operationId)
        {
            Operations.Add(("recurring_revoke", operationId, leaseId));
            var result = StandingLeaseSafetyControlResult.ChangedResult(
                operationId,
                StandingLeaseSafetyReasonCodes.LeaseRevoked,
                requiresActiveRunStop: RecurringRequiresStopForTest,
                durableOperationCommitted: true,
                targetRunId: RecurringRequiresStopForTest ? "run-ui" : null);
            if (RecurringStopFailedForTest)
                return result.WithPhysicalStopFailure();
            return RecurringStopNoOpForTest ? result.WithPhysicalStopNoOp() : result;
        }

        public StandingLeaseSafetyControlResult StopAll(string operationId)
        {
            Operations.Add(("stop_all", operationId, null));
            var result = StandingLeaseSafetyControlResult.ChangedResult(
                operationId, "stop_all_applied", GlobalControlRequiresStopForTest, durableOperationCommitted: true);
            return GlobalControlStopFailedForTest ? result.WithPhysicalStopFailure() : result;
        }

        public StandingLeaseSafetyControlResult Disable(string operationId)
        {
            BusyObserved = BusyProbe?.Invoke() ?? false;
            Operations.Add(("disable", operationId, null));
            var result = StandingLeaseSafetyControlResult.ChangedResult(
                operationId, StandingLeaseSafetyReasonCodes.UnattendedDisabled,
                GlobalControlRequiresStopForTest, durableOperationCommitted: true);
            return GlobalControlStopFailedForTest ? result.WithPhysicalStopFailure() : result;
        }

        public StandingLeaseSafetyControlResult Enable(string operationId)
        {
            Operations.Add(("enable", operationId, null));
            return StandingLeaseSafetyControlResult.ChangedResult(operationId, "unattended_enabled");
        }

        public IReadOnlyList<AgentRecorder.Api.FutureWindowAuthorizationState> ListFutureWindowAuthorizations() => FutureWindowGrants;

        public AgentRecorder.Api.FutureWindowAuthorizationState? RevokeFutureWindowAuthorization(string authorizationId)
        {
            RevokedFutureAuthorizationId = authorizationId;
            var target = FutureWindowGrants.SingleOrDefault(item => item.AuthorizationId == authorizationId);
            if (target is null) return null;
            var revoked = target with { Status = "revoked", ReasonCode = "locally_revoked" };
            FutureWindowGrants = FutureWindowGrants.Select(item => item.AuthorizationId == authorizationId ? revoked : item).ToArray();
            return revoked;
        }
    }

    private sealed class FixedIds : IStandingLeasePreparationIdProvider
    {
        private readonly string _suffix;

        internal FixedIds(string suffix) => _suffix = suffix;

        public string CreatePlanId() => "plan-" + _suffix;
        public string CreateOccurrenceId() => "occurrence-" + _suffix;
        public string CreateLeaseId() => "lease-" + _suffix;
        public string CreateScopeId() => "scope-" + _suffix;
    }

    private sealed class StandingSetupIntentSnapshotForTests
    {
        internal StandingSetupIntentSnapshotForTests(string intentId, string suffix)
        {
            Intent = StandingSetupIntentSnapshot.CreateForTrustedSetupAdapter(
                intentId,
                "request-" + suffix,
                UserSid,
                "session-control-center",
                At(0),
                At(300),
                At(10),
                At(20),
                At(80),
                TimeSpan.FromSeconds(30),
                At(3600),
                Path.Combine(Path.GetTempPath(), "AgentRecorderControlCenter", "captures"),
                "capture-" + suffix + ".mp4");
        }

        internal StandingSetupIntentSnapshot Intent { get; }
    }

    private sealed class TestDatabase : IDisposable
    {
        private readonly string _directory = Path.Combine(
            Path.GetTempPath(),
            "AgentRecorderControlCenter_" + Guid.NewGuid().ToString("N"));

        internal TestDatabase()
        {
            Directory.CreateDirectory(_directory);
            Store = new SqliteOperationalStore(Path.Combine(_directory, "state", "agent-recorder.db"));
            Store.Initialize();
            Execute(Store, "UPDATE unattended_safety_state SET unattended_mode_code = 'enabled' WHERE state_id = 'global';");
        }

        internal SqliteOperationalStore Store { get; }

        public void Dispose()
        {
            try { Directory.Delete(_directory, recursive: true); }
            catch (DirectoryNotFoundException) { }
        }
    }
}
