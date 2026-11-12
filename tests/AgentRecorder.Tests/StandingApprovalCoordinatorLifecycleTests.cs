using AgentRecorder.Api;
using AgentRecorder.App;
using AgentRecorder.Capture;
using AgentRecorder.Core;
using AgentRecorder.Core.Automation;
using AgentRecorder.Infrastructure;
using AgentRecorder.Logging;
using AgentRecorder.Persistence;
using AgentRecorder.Windows;
using Xunit;

namespace AgentRecorder.Tests;

[Collection("NonParallel-AgentRecorderDataDir")]
public sealed class StandingApprovalCoordinatorLifecycleTests
{
    [Fact]
    public async Task SameLoginApprovedUnconsumedChainSurvivesReopenWithoutSecondApproval()
    {
        using var fixture = new Fixture();
        var ui = new Ui(new() { PreviewProvider = new StandingApprovalLifecycleTests.Preview(), AfterVisibleForTests = form => form.DecideForTests(true) });
        var request = fixture.Request("same-login-reopen");
        string id;
        using (var coordinator = fixture.Coordinator(ui))
        {
            id = coordinator.CreateOrGet(request).SetupIntentId!;
            await coordinator.WaitForIdleAsync().WaitAsync(TimeSpan.FromSeconds(6));
            Assert.Equal("scheduled", coordinator.Get(id)!.StatusCode);
        }
        var sid = fixture.Text("SELECT current_user_sid FROM setup_intents;");
        var binding = fixture.Text("SELECT session_binding FROM setup_intents;");
        Assert.Equal(CaptureAuthorizationSessionBinding.Persistent, binding);
        var reopened = new SqliteOperationalStore(fixture.Store.DatabasePath);
        var query = new StandingLeaseNaturalWakeCandidateQuery(reopened);
        Assert.Equal(id, Assert.Single(query.ListCandidates(sid, CaptureAuthorizationSessionBinding.Persistent)).IntentId);
        Assert.True(new StandingLeaseNaturalWakeStartupRecovery(reopened, audit: (_, _) => { }).RecoverAll(sid, binding));
        var replay = new Ui(new() { PreviewProvider = new StandingApprovalLifecycleTests.Preview() });
        using var restarted = fixture.Coordinator(replay);
        restarted.CreateOrGet(request);
        Assert.Equal("scheduled", restarted.Get(id)!.StatusCode);
        Assert.Equal(0, replay.Approvals); Assert.Equal(0, replay.Selections);
        Assert.Equal("0", fixture.Text("SELECT COUNT(*) FROM recording_runs;"));
        Assert.Equal("0", fixture.Text("SELECT COUNT(*) FROM lease_uses;"));
        Assert.Empty(query.ListCandidates(sid, "legacy-or-other-login"));
        Assert.Equal(binding, fixture.Text("SELECT session_binding FROM setup_intents;"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InitializationFailureOrDisplayTimeoutIsDurableAndNeverReplaysUi(bool block)
    {
        using var release = new ManualResetEventSlim();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var ui = new Ui(new()
        {
            PreviewProvider = new StandingApprovalLifecycleTests.Preview(), DisplayBudget = TimeSpan.FromMilliseconds(500),
            BeforeConstructForTests = () => { entered.TrySetResult(); if (block) release.Wait(); else throw new InvalidOperationException(); },
        });
        using var fixture = new Fixture(); using var coordinator = fixture.Coordinator(ui);
        var request = fixture.Request("display-failure");
        var result = coordinator.CreateOrGet(request);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(4));
            await StandingApprovalLifecycleTests.WaitUntil(() => fixture.Text("SELECT status_code FROM setup_intents;") == "rejected");
            var state = coordinator.Get(result.SetupIntentId!)!;
            Assert.Equal(block ? "lease_approval_display_timeout" : "lease_approval_initialization_failed", state.ReasonCode);
            Assert.Null(state.RunId);
            coordinator.CreateOrGet(request);
            Assert.Equal(1, ui.Approvals); Assert.Equal(1, ui.Selections);
            if (block) Assert.True(coordinator.HostShutdownBusy);
        }
        finally { release.Set(); }
        await coordinator.WaitForIdleAsync().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(coordinator.HostShutdownBusy); fixture.AssertNoRunOrActiveLease();
        var replay = new Ui(new() { PreviewProvider = new StandingApprovalLifecycleTests.Preview() });
        using var restarted = fixture.Coordinator(replay);
        restarted.CreateOrGet(request);
        Assert.Equal(0, replay.Approvals); Assert.Equal(0, replay.Selections);
    }

    [Fact]
    public async Task SetupExpiryBeforeLateInitializationKeepsGateUntilExitAndNeverApproves()
    {
        using var release = new ManualResetEventSlim();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var visible = 0;
        var ui = new Ui(new()
        {
            PreviewProvider = new StandingApprovalLifecycleTests.Preview(), DisplayBudget = TimeSpan.FromSeconds(10),
            BeforeConstructForTests = () => { entered.TrySetResult(); release.Wait(); },
            AfterVisibleForTests = form => { Interlocked.Increment(ref visible); form.DecideForTests(true); },
        });
        using var fixture = new Fixture(); using var coordinator = fixture.Coordinator(ui);
        var result = coordinator.CreateOrGet(fixture.Request("expires-before-ui", shortWindow: true));
        Task<LocalSetupUiSerializationGate.Lease>? waiter = null;
        using var waitBudget = new CancellationTokenSource(TimeSpan.FromSeconds(12));
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(4));
            await StandingApprovalLifecycleTests.WaitUntil(() => fixture.Text("SELECT status_code FROM setup_intents;") == "expired");
            Assert.Equal("setup_intent_expired", coordinator.Get(result.SetupIntentId!)!.ReasonCode);
            Assert.True(coordinator.HostShutdownBusy);
            waiter = LocalSetupUiSerializationGate.Instance.WaitAsync(waitBudget.Token);
            Assert.False(waiter.IsCompleted);
            fixture.AssertNoRunOrActiveLease();
        }
        finally { release.Set(); }
        await coordinator.WaitForIdleAsync().WaitAsync(TimeSpan.FromSeconds(5));
        if (waiter is not null) using (await waiter) { }
        Assert.Equal(0, visible); Assert.False(coordinator.HostShutdownBusy);
        fixture.AssertNoRunOrActiveLease();
        Assert.Equal("expired", coordinator.Get(result.SetupIntentId!)!.StatusCode);
    }

    [Fact]
    public async Task HostShutdownCancelsVisibleApprovalButDoesNotRetireBlockedPreviewEarly()
    {
        using var release = new ManualResetEventSlim();
        var preview = new StandingApprovalLifecycleTests.Preview(release: release);
        var ui = new Ui(new() { PreviewProvider = preview, DisableApprovalForTests = true });
        using var fixture = new Fixture(); var coordinator = fixture.Coordinator(ui);
        var result = coordinator.CreateOrGet(fixture.Request("shutdown-visible"));
        try
        {
            await preview.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await Task.Run(coordinator.Dispose).WaitAsync(TimeSpan.FromSeconds(8));
            Assert.True(coordinator.HostShutdownBusy);
            Assert.Equal("lease_approval_host_shutdown", fixture.Text("SELECT terminal_reason_code FROM setup_intents;"));
            fixture.AssertNoRunOrActiveLease();
        }
        finally { release.Set(); }
        await coordinator.WaitForIdleAsync().WaitAsync(TimeSpan.FromSeconds(5)); coordinator.Dispose();
        Assert.False(coordinator.HostShutdownBusy);
        Assert.Equal("rejected", fixture.Text("SELECT status_code FROM setup_intents;"));
    }

    [Fact]
    public async Task VisibleApprovalBudgetTimeoutIsDurableAndDoesNotBecomeUserRejection()
    {
        var ui = new Ui(new() { PreviewProvider = new StandingApprovalLifecycleTests.Preview(), ApprovalBudget = TimeSpan.FromMilliseconds(150), DisableApprovalForTests = true });
        using var fixture = new Fixture(); using var coordinator = fixture.Coordinator(ui);
        var result = coordinator.CreateOrGet(fixture.Request("visible-timeout"));
        await coordinator.WaitForIdleAsync().WaitAsync(TimeSpan.FromSeconds(6));
        Assert.Equal("lease_approval_timed_out", coordinator.Get(result.SetupIntentId!)!.ReasonCode);
        Assert.False(coordinator.HostShutdownBusy); fixture.AssertNoRunOrActiveLease();
    }

    private sealed class Ui(StandingLeaseApprovalOptions options) : IStandingPlanSetupUi
    {
        private readonly TrayStandingPlanSetupUi _production = new(() => new UiTextProvider(UiLanguage.EnUs), options);
        internal int Selections, Approvals;
        public bool IsInteractiveDesktopAvailable => true;
        public Task<StandingPlanSetupSelection> SelectFreshRegionAsync(CancellationToken token)
        { Selections++; return Task.FromResult(new StandingPlanSetupSelection("selected", 10, 20, 64, 48, "synthetic", "virtual_screen")); }
        public Task<bool> ShowApprovalAsync(StandingLeaseApprovalDetails details, CancellationToken token) => throw new InvalidOperationException();
        public StandingLeaseApprovalOperation BeginApproval(string id, StandingLeaseApprovalDetails details, Action<string, object> audit, CancellationToken token)
        { Approvals++; return _production.BeginApproval(id, details, audit, token); }
    }
    private sealed class Fixture : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "standing-lifecycle-" + Guid.NewGuid().ToString("N"));
        internal readonly SqliteOperationalStore Store;
        internal Fixture()
        {
            Directory.CreateDirectory(_root); Store = new(Path.Combine(_root, "private.db")); Store.Initialize();
            using var c = Store.OpenConnection(); using var cmd = c.CreateCommand();
            cmd.CommandText = "UPDATE unattended_safety_state SET unattended_mode_code='enabled';"; cmd.ExecuteNonQuery();
        }
        internal StandingPlanSetupCoordinator Coordinator(IStandingPlanSetupUi ui) => new(Store, new AuditLogger(Path.Combine(_root, "audit.jsonl")), ui,
            new StandingLeaseSafetyControlService(Store), () => new[] { new StandingLeaseDisplayMetadata("synthetic", "synthetic", DisplayIdentityResolutionStatus.Resolved,
                new(0, 0, 800, 600), 96, 96, 800, 600, AuthorizedDisplayOrientation.Landscape) }, outputReadinessProviderForTest: new Output());
        internal StandingPlanApiRequest Request(string key, bool shortWindow = false)
        {
            var now = DateTimeOffset.UtcNow;
            return new(key, now.AddSeconds(shortWindow ? 1 : 60), now.AddSeconds(shortWindow ? 2 : 90), now.AddSeconds(shortWindow ? 3 : 91),
                now.AddSeconds(shortWindow ? 4 : 100), TimeSpan.FromSeconds(1), _root, "never-created.mp4");
        }
        internal string Text(string sql)
        { using var c = Store.OpenReadOnlyConnection(); using var cmd = c.CreateCommand(); cmd.CommandText = sql; return Convert.ToString(cmd.ExecuteScalar()) ?? ""; }
        internal void AssertNoRunOrActiveLease()
        { Assert.Equal("0", Text("SELECT COUNT(*) FROM recording_runs;")); Assert.Equal("0", Text("SELECT COUNT(*) FROM consent_leases WHERE status_code='active';")); Assert.False(File.Exists(Path.Combine(_root, "never-created.mp4"))); }
        public void Dispose() => TestDirectoryCleanup.DeleteOwnedDirectory(_root);
    }
    private sealed class Output : IStandingLeaseOutputReadinessProvider
    {
        public StandingLeaseOutputReadinessResult Check(string directory, string file, TimeSpan duration) => StandingLeaseOutputReadinessResult.Ready(
            new(directory, Path.Combine(directory, file), true, false, true, true, long.MaxValue, 1));
    }
}
