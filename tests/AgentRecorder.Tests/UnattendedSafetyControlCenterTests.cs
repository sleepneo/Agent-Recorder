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
using Xunit;

namespace AgentRecorder.Tests;

[Collection("NonParallel-AgentRecorderDataDir")]
public sealed class UnattendedSafetyControlCenterTests
{
    private const string UserSid = "S-1-5-21-control-center";

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
            form.Show();
            Application.DoEvents();

            Assert.Contains("无人值守安全控制", form.Text);
            Assert.Contains("已启用", form.GlobalStatusTextForTests);
            Assert.Equal(1, form.LeaseCardCountForTests);
            Assert.Same(form.RefreshButtonForTests, form.AcceptButton);
            Assert.Same(form.CloseButtonForTests, form.CancelButton);

            var card = (TableLayoutPanel)form.LeaseCardForTests(0).Controls[0];
            var revoke = (Button)card.GetControlFromPosition(1, 0)!;
            revoke.PerformClick();
            Assert.True(confirmation.CallCount == 1);
            Assert.Single(gateway.Operations);
            Assert.StartsWith("control-center-revoke-", gateway.Operations[0].OperationId);

            gateway.State = CreateState(ConsentLeaseStatus.Active);
            revoke = (Button)((TableLayoutPanel)form.LeaseCardForTests(0).Controls[0]).GetControlFromPosition(1, 0)!;
            revoke.PerformClick();
            Assert.Equal(2, gateway.Operations.Count);
            Assert.NotEqual(gateway.Operations[0].OperationId, gateway.Operations[1].OperationId);

            form.UpdateLanguage(new UiTextProvider(UiLanguage.EnUs));
            Assert.Contains("Unattended Safety Control", form.Text);
            Assert.Contains("Enabled", form.GlobalStatusTextForTests);
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
            form.Show();
            Application.DoEvents();

            form.StopAllButtonForTests.PerformClick();
            Assert.Equal(1, confirmation.CallCount);
            Assert.Empty(gateway.Operations);

            confirmation.NextAnswer = true;
            gateway.BusyProbe = () =>
                form.BusyForTests &&
                !form.StopAllButtonForTests.Enabled &&
                !form.DisableButtonForTests.Enabled &&
                !form.EnableButtonForTests.Enabled &&
                !form.RefreshButtonForTests.Enabled;
            form.DisableButtonForTests.PerformClick();
            Assert.Equal(2, confirmation.CallCount);
            Assert.Contains(gateway.Operations, operation => operation.Kind == "disable");
            Assert.True(gateway.BusyObserved);

            gateway.QueryResult = StandingLeaseControlCenterQueryResult.Rejected("safety_sqlite_failure");
            form.RefreshButtonForTests.PerformClick();
            Assert.Contains("unavailable", form.GlobalStatusTextForTests, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(0, form.LeaseCardCountForTests);
            Assert.DoesNotContain("Completed", form.ResultTextForTests);
            Assert.False(form.StopAllButtonForTests.Enabled);
            Assert.False(form.DisableButtonForTests.Enabled);
            Assert.False(form.EnableButtonForTests.Enabled);
        });
    }

    [Fact]
    public void TrayMenuOpensOneReusableControlFormAndClosesWithoutExitingTray()
    {
        RunOnSta(() =>
        {
            using var database = new TestDatabase();
            DataDirResolver.SetOverride(Path.GetDirectoryName(Path.GetDirectoryName(database.Store.DatabasePath))!);
            try
            {
                var audit = new AuditLogger();
                var engine = new RecordingEngine(audit);
                var service = new StandingLeaseSafetyControlService(database.Store, () => At(1));
                using var tray = new TrayContext(
                    engine,
                    audit,
                    FakeGlobalStopHotkeyFactory.Create(),
                    unattendedSafetyService: service);
                engine.SetTray(tray);

                var icon = GetPrivateField<NotifyIcon>(tray, "_icon");
                var item = icon.ContextMenuStrip!.Items
                    .OfType<ToolStripMenuItem>()
                    .Single(menuItem => menuItem.Text == "无人值守安全控制");
                item.PerformClick();
                var first = GetPrivateField<UnattendedSafetyControlForm>(tray, "_unattendedSafetyForm");
                item.PerformClick();
                var second = GetPrivateField<UnattendedSafetyControlForm>(tray, "_unattendedSafetyForm");
                Assert.Same(first, second);

                first.Close();
                Assert.False(first.Visible);
                item.PerformClick();
                Assert.Same(first, GetPrivateField<UnattendedSafetyControlForm>(tray, "_unattendedSafetyForm"));
            }
            finally
            {
                DataDirResolver.ClearOverride();
            }
        });
    }

    private static StandingLeaseControlCenterState CreateState(ConsentLeaseStatus leaseStatus)
    {
        var now = At(1);
        return new StandingLeaseControlCenterState(
            UnattendedModeStatus.Enabled,
            false,
            null,
            null,
            null,
            null,
            new[]
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
            });
    }

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

        public bool Confirm(IWin32Window owner, string title, string message)
        {
            CallCount++;
            return NextAnswer;
        }
    }

    private sealed class FakeGateway : IUnattendedSafetyControlGateway
    {
        internal StandingLeaseControlCenterState State { get; set; }
        internal StandingLeaseControlCenterQueryResult QueryResult { get; set; }
        internal Func<bool>? BusyProbe { get; set; }
        internal bool BusyObserved { get; private set; }
        internal List<(string Kind, string OperationId, string? IntentId)> Operations { get; } = new();

        internal FakeGateway(StandingLeaseControlCenterState state)
        {
            State = state;
            QueryResult = StandingLeaseControlCenterQueryResult.Available(state);
        }

        public StandingLeaseControlCenterQueryResult Query() => QueryResult;

        public StandingLeaseSafetyControlResult RevokeLease(string intentId, string operationId)
        {
            Operations.Add(("revoke", operationId, intentId));
            return StandingLeaseSafetyControlResult.ChangedResult(operationId, StandingLeaseSafetyReasonCodes.LeaseRevoked);
        }

        public StandingLeaseSafetyControlResult StopAll(string operationId)
        {
            Operations.Add(("stop_all", operationId, null));
            return StandingLeaseSafetyControlResult.ChangedResult(operationId, "stop_all_applied");
        }

        public StandingLeaseSafetyControlResult Disable(string operationId)
        {
            BusyObserved = BusyProbe?.Invoke() ?? false;
            Operations.Add(("disable", operationId, null));
            return StandingLeaseSafetyControlResult.ChangedResult(operationId, StandingLeaseSafetyReasonCodes.UnattendedDisabled);
        }

        public StandingLeaseSafetyControlResult Enable(string operationId)
        {
            Operations.Add(("enable", operationId, null));
            return StandingLeaseSafetyControlResult.ChangedResult(operationId, "unattended_enabled");
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
