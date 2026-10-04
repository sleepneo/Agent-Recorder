using System.Collections.Concurrent;
using System.Diagnostics;
using System.Security.Principal;
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
public sealed class FutureWindowApprovalCoordinatorLifecycleTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task GlobalSafetyRevocationCancelsPendingApprovalWithoutWaitingForUiDeadline(bool stopAll)
    {
        var ui = new ControllableApprovalUi(FutureWindowApprovalOutcome.Approved);
        using var context = new CoordinatorContext(ui, TimeSpan.FromSeconds(20));
        var scope = context.CreatePending();
        var flight = context.Coordinator.StartApprovalFlightForTests(scope.AuthorizationId);
        var pending = await ui.WaitForCallAsync(0, TimeSpan.FromSeconds(5));
        var service = new StandingLeaseSafetyControlService(context.Store,
            utcNowForTest: () => DateTimeOffset.UtcNow,
            auditForTest: (_, _) => { },
            activeRunStopper: stopAll ? new ThrowingActiveRunStopper() : null);
        var gateway = new StandingLeaseSafetyControlGateway(service,
            futureWindowGateway: context.Coordinator);
        var operationId = "acceptance-global-" + Guid.NewGuid().ToString("N");

        var result = stopAll ? gateway.StopAll(operationId) : gateway.Disable(operationId);

        Assert.True(result.DurableOperationCommitted);
        if (stopAll) Assert.True(result.PhysicalStopFailed);
        Assert.Equal("revoked", context.Get(scope.AuthorizationId).StatusCode);
        Assert.Null(context.Get(scope.AuthorizationId).ApprovedAtUtc);
        var wait = Stopwatch.StartNew();
        while (!pending.CancellationObserved && wait.Elapsed < TimeSpan.FromSeconds(2))
            await Task.Delay(20);

        Assert.True(pending.CancellationObserved,
            "Global safety revocation must cancel the pending approval UI, not wait for its deadline.");
        await flight.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("revoked", context.Get(scope.AuthorizationId).StatusCode);

        if (!stopAll)
            Assert.True(gateway.Enable("acceptance-enable-" + Guid.NewGuid().ToString("N")).DurableOperationCommitted);
        var later = context.CreatePending();
        var laterFlight = context.Coordinator.StartApprovalFlightForTests(later.AuthorizationId);
        var laterPresentation = await ui.WaitForCallAsync(1, TimeSpan.FromSeconds(5));
        laterPresentation.Complete(FutureWindowApprovalOutcome.Rejected);
        await laterFlight.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(2, ui.CallCount);
    }

    [Fact]
    public async Task GlobalSafetyTransactionFailureDoesNotCancelPendingApproval()
    {
        var ui = new ControllableApprovalUi(FutureWindowApprovalOutcome.Approved);
        using var context = new CoordinatorContext(ui, TimeSpan.FromSeconds(20), copyExecutable: true);
        var scope = context.CreatePending();
        var flight = context.Coordinator.StartApprovalFlightForTests(scope.AuthorizationId);
        var pending = await ui.WaitForCallAsync(0, TimeSpan.FromSeconds(5));
        var service = new StandingLeaseSafetyControlService(context.Store,
            utcNowForTest: () => DateTimeOffset.UtcNow,
            auditForTest: (_, _) => { },
            beforeCommitForTest: (_, _) => throw new InvalidOperationException("injected safety transaction failure"));
        var gateway = new StandingLeaseSafetyControlGateway(service,
            futureWindowGateway: context.Coordinator);

        var result = gateway.Disable("acceptance-failed-disable-" + Guid.NewGuid().ToString("N"));

        Assert.False(result.DurableOperationCommitted);
        Assert.False(pending.CancellationObserved);
        Assert.Equal("pending", context.Get(scope.AuthorizationId).StatusCode);
        Assert.Throws<IOException>(() =>
        {
            using var stillLocked = new FileStream(scope.ExecutableIdentity.CanonicalPath,
                FileMode.Open, FileAccess.ReadWrite, FileShare.Read);
        });
        pending.Complete(FutureWindowApprovalOutcome.Rejected);
        await flight.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task GlobalSafetyRevocationReleasesPendingExecutablePin(bool stopAll)
    {
        var ui = new ControllableApprovalUi(FutureWindowApprovalOutcome.Approved);
        using var context = new CoordinatorContext(ui, TimeSpan.FromSeconds(20), copyExecutable: true);
        var scope = context.CreatePending();
        var flight = context.Coordinator.StartApprovalFlightForTests(scope.AuthorizationId);
        await ui.WaitForCallAsync(0, TimeSpan.FromSeconds(5));
        Assert.Throws<IOException>(() =>
        {
            using var locked = new FileStream(scope.ExecutableIdentity.CanonicalPath,
                FileMode.Open, FileAccess.ReadWrite, FileShare.Read);
        });
        var service = new StandingLeaseSafetyControlService(context.Store,
            utcNowForTest: () => DateTimeOffset.UtcNow, auditForTest: (_, _) => { });
        var gateway = new StandingLeaseSafetyControlGateway(service,
            futureWindowGateway: context.Coordinator);
        var operationId = "acceptance-release-pin-" + Guid.NewGuid().ToString("N");

        var result = stopAll ? gateway.StopAll(operationId) : gateway.Disable(operationId);
        await flight.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(result.DurableOperationCommitted);
        Assert.Equal("revoked", context.Get(scope.AuthorizationId).StatusCode);
        Assert.Null(context.Get(scope.AuthorizationId).ApprovedAtUtc);
        // No program is launched and no bytes are written; only the released lock is checked.
        using var unlocked = new FileStream(scope.ExecutableIdentity.CanonicalPath,
            FileMode.Open, FileAccess.ReadWrite, FileShare.Read);
    }

    [Fact]
    public async Task GlobalSafetyRevocationDoesNotCancelApprovalOutsideGatewaySession()
    {
        var ui = new ControllableApprovalUi(FutureWindowApprovalOutcome.Approved);
        using var context = new CoordinatorContext(ui, TimeSpan.FromSeconds(20), copyExecutable: true);
        var scope = context.CreatePending();
        var flight = context.Coordinator.StartApprovalFlightForTests(scope.AuthorizationId);
        var pending = await ui.WaitForCallAsync(0, TimeSpan.FromSeconds(5));
        var service = new StandingLeaseSafetyControlService(context.Store,
            utcNowForTest: () => DateTimeOffset.UtcNow, auditForTest: (_, _) => { });
        var gateway = new StandingLeaseSafetyControlGateway(service,
            currentSessionBindingForTest: () => "unrelated-session",
            futureWindowGateway: context.Coordinator);

        var result = gateway.Disable("acceptance-unrelated-session-" + Guid.NewGuid().ToString("N"));

        Assert.True(result.DurableOperationCommitted);
        Assert.False(pending.CancellationObserved);
        Assert.Equal("pending", context.Get(scope.AuthorizationId).StatusCode);
        Assert.Throws<IOException>(() =>
        {
            using var stillLocked = new FileStream(scope.ExecutableIdentity.CanonicalPath,
                FileMode.Open, FileAccess.ReadWrite, FileShare.Read);
        });
        pending.Complete(FutureWindowApprovalOutcome.Rejected);
        await flight.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task GlobalSafetyOperationReplayDoesNotCancelLaterPendingApproval()
    {
        var ui = new ControllableApprovalUi(FutureWindowApprovalOutcome.Approved);
        using var context = new CoordinatorContext(ui, TimeSpan.FromSeconds(20));
        var first = context.CreatePending();
        var firstFlight = context.Coordinator.StartApprovalFlightForTests(first.AuthorizationId);
        var firstPresentation = await ui.WaitForCallAsync(0, TimeSpan.FromSeconds(5));
        var service = new StandingLeaseSafetyControlService(context.Store,
            utcNowForTest: () => DateTimeOffset.UtcNow, auditForTest: (_, _) => { });
        var gateway = new StandingLeaseSafetyControlGateway(service,
            futureWindowGateway: context.Coordinator);
        var operationId = "acceptance-replay-disable-" + Guid.NewGuid().ToString("N");

        var changed = gateway.Disable(operationId);
        Assert.True(changed.DurableOperationCommitted);
        Assert.True(firstPresentation.CancellationObserved);
        await firstFlight.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(gateway.Enable("acceptance-replay-enable-" + Guid.NewGuid().ToString("N")).DurableOperationCommitted);

        var later = context.CreatePending();
        var laterFlight = context.Coordinator.StartApprovalFlightForTests(later.AuthorizationId);
        var laterPresentation = await ui.WaitForCallAsync(1, TimeSpan.FromSeconds(5));
        var replay = gateway.Disable(operationId);

        Assert.Equal(StandingLeaseSafetyControlResultStatus.AlreadyApplied, replay.Status);
        Assert.True(replay.DurableOperationCommitted);
        Assert.False(laterPresentation.CancellationObserved);
        Assert.Equal("pending", context.Get(later.AuthorizationId).StatusCode);
        laterPresentation.Complete(FutureWindowApprovalOutcome.Rejected);
        await laterFlight.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task UiGateWaitTimeoutPublishesTerminalFailureAndReleasesNoForeignLease()
    {
        var ui = new ControllableApprovalUi(FutureWindowApprovalOutcome.Cancelled);
        using var context = new CoordinatorContext(ui, TimeSpan.FromMilliseconds(150));
        var scope = context.CreatePending();
        using var heldGate = await LocalSetupUiSerializationGate.Instance.WaitAsync(CancellationToken.None);

        var flight = context.Coordinator.StartApprovalFlightForTests(scope.AuthorizationId);
        await flight.WaitAsync(TimeSpan.FromSeconds(4));
        var row = context.Get(scope.AuthorizationId);

        Assert.Equal("failed", row.StatusCode);
        Assert.Equal("approval_ui_timeout", row.ReasonCode);
        Assert.Equal(0, ui.CallCount);

        heldGate.Dispose();
        using var reacquireTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        using var reacquired = await LocalSetupUiSerializationGate.Instance.WaitAsync(reacquireTimeout.Token);
    }

    [Fact]
    public async Task PresentationTimeoutIsTerminalAndDoesNotBecomeApproval()
    {
        var ui = new ControllableApprovalUi(FutureWindowApprovalOutcome.Cancelled);
        using var context = new CoordinatorContext(ui, TimeSpan.FromMilliseconds(250));
        var scope = context.CreatePending();

        await context.Coordinator.StartApprovalFlightForTests(scope.AuthorizationId).WaitAsync(TimeSpan.FromSeconds(5));
        var row = context.Get(scope.AuthorizationId);

        Assert.Equal("failed", row.StatusCode);
        Assert.Equal("approval_ui_timeout", row.ReasonCode);
        Assert.Null(row.ApprovedAtUtc);
        Assert.Equal(1, ui.CallCount);
        using var gateTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        using var releasedGate = await LocalSetupUiSerializationGate.Instance.WaitAsync(gateTimeout.Token);
    }

    [Fact]
    public async Task SingleRevokeCancelsOnlyItsFlightAndLateApprovalCannotReactivateIt()
    {
        var ui = new ControllableApprovalUi(FutureWindowApprovalOutcome.Approved);
        using var context = new CoordinatorContext(ui, TimeSpan.FromSeconds(20), copyExecutable: true);
        var scope = context.CreatePending();
        var flight = context.Coordinator.StartApprovalFlightForTests(scope.AuthorizationId);
        var pending = await ui.WaitForCallAsync(0, TimeSpan.FromSeconds(5));

        var revoked = context.Coordinator.Revoke(scope.AuthorizationId);
        await flight.WaitAsync(TimeSpan.FromSeconds(5));
        var row = context.Get(scope.AuthorizationId);

        Assert.NotNull(revoked);
        Assert.Equal("revoked", row.StatusCode);
        Assert.Null(row.ApprovedAtUtc);
        Assert.True(pending.CancellationObserved);
        Assert.Equal(1, ui.CallCount);
        // Single-item revocation follows the same terminal pin-release contract.
        using var unlocked = new FileStream(scope.ExecutableIdentity.CanonicalPath,
            FileMode.Open, FileAccess.ReadWrite, FileShare.Read);
        using var gateTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        using var releasedGate = await LocalSetupUiSerializationGate.Instance.WaitAsync(gateTimeout.Token);
    }

    [Fact]
    public async Task DuplicateFlightDoesNotStackModalAndNewAuthorizationCanUseReleasedGate()
    {
        var ui = new ControllableApprovalUi(FutureWindowApprovalOutcome.Cancelled);
        using var context = new CoordinatorContext(ui, TimeSpan.FromSeconds(20));
        var first = context.CreatePending();
        var firstFlight = context.Coordinator.StartApprovalFlightForTests(first.AuthorizationId);
        var sameFlight = context.Coordinator.StartApprovalFlightForTests(first.AuthorizationId);
        Assert.Same(firstFlight, sameFlight);

        var firstDialog = await ui.WaitForCallAsync(0, TimeSpan.FromSeconds(5));
        Assert.Equal(1, ui.CallCount);
        firstDialog.Complete(FutureWindowApprovalOutcome.Rejected);
        await firstFlight.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("blocked", context.Get(first.AuthorizationId).StatusCode);

        var second = context.CreatePending();
        var secondFlight = context.Coordinator.StartApprovalFlightForTests(second.AuthorizationId);
        var secondDialog = await ui.WaitForCallAsync(1, TimeSpan.FromSeconds(5));
        Assert.NotSame(firstFlight, secondFlight);
        secondDialog.Complete(FutureWindowApprovalOutcome.Rejected);
        await secondFlight.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(2, ui.CallCount);
        Assert.Equal("blocked", context.Get(second.AuthorizationId).StatusCode);
        using var gateTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        using var releasedGate = await LocalSetupUiSerializationGate.Instance.WaitAsync(gateTimeout.Token);
    }

    [Fact]
    public async Task DisplayFailurePublishesStableFailureAndReleasesUiGate()
    {
        var ui = new ControllableApprovalUi(FutureWindowApprovalOutcome.Unavailable);
        using var context = new CoordinatorContext(ui, TimeSpan.FromSeconds(5));
        var scope = context.CreatePending();
        var flight = context.Coordinator.StartApprovalFlightForTests(scope.AuthorizationId);
        var presentation = await ui.WaitForCallAsync(0, TimeSpan.FromSeconds(5));
        presentation.Complete(FutureWindowApprovalOutcome.Unavailable);
        await flight.WaitAsync(TimeSpan.FromSeconds(5));

        var row = context.Get(scope.AuthorizationId);
        Assert.Equal("failed", row.StatusCode);
        Assert.Equal("approval_ui_display_failed", row.ReasonCode);
        using var releasedGate = await LocalSetupUiSerializationGate.Instance.WaitAsync(
            new CancellationTokenSource(TimeSpan.FromSeconds(2)).Token);
    }

    [Fact]
    public async Task HostShutdownCancelsPendingApprovalAndReleasesUiGate()
    {
        var ui = new ControllableApprovalUi(FutureWindowApprovalOutcome.Approved);
        using var context = new CoordinatorContext(ui, TimeSpan.FromSeconds(20));
        var scope = context.CreatePending();
        var flight = context.Coordinator.StartApprovalFlightForTests(scope.AuthorizationId);
        var pending = await ui.WaitForCallAsync(0, TimeSpan.FromSeconds(5));

        context.Coordinator.Dispose();
        await flight.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(pending.CancellationObserved);
        Assert.Equal("pending", context.Get(scope.AuthorizationId).StatusCode);
        Assert.Null(context.Get(scope.AuthorizationId).ApprovedAtUtc);
        using var gateTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        using var releasedGate = await LocalSetupUiSerializationGate.Instance.WaitAsync(gateTimeout.Token);
    }

    private sealed class CoordinatorContext : IDisposable
    {
        private readonly string _directory;
        private readonly SqliteFutureWindowAuthorizationRepository _repository;
        private readonly FutureWindowExecutableIdentity _executable;
        private readonly string _sid;
        private readonly string _session;
        private readonly AuditLogger _audit;
        private readonly RecordingEngine _engine;

        internal SqliteOperationalStore Store { get; }
        internal FutureWindowOneShotAuthorizationCoordinator Coordinator { get; }

        internal CoordinatorContext(ControllableApprovalUi ui, TimeSpan waitLimit, bool copyExecutable = false)
        {
            _directory = Path.Combine(Path.GetTempPath(),
                "future-window-coordinator-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_directory);
            Store = new SqliteOperationalStore(Path.Combine(_directory, "state.db"));
            Store.Initialize();
            using (var connection = Store.OpenConnection())
            using (var command = connection.CreateCommand())
            {
                command.CommandText = "UPDATE unattended_safety_state SET unattended_mode_code='enabled' WHERE state_id='global';";
                Assert.Equal(1, command.ExecuteNonQuery());
            }

            _sid = WindowsIdentity.GetCurrent().User?.Value
                ?? throw new InvalidOperationException("The interactive user SID is unavailable.");
            _session = CaptureAuthorizationSessionBinding.Current;
            var processPath = Environment.ProcessPath
                ?? throw new InvalidOperationException("The current executable path is unavailable.");
            if (copyExecutable)
            {
                var copyPath = Path.Combine(_directory, "pin-test.exe");
                File.Copy(processPath, copyPath);
                processPath = copyPath;
            }
            Assert.True(FutureWindowProcessIdentity.TryResolveExecutable(processPath, out var executable, out var reason), reason);
            _executable = executable!;

            _audit = new AuditLogger(Path.Combine(_directory, "audit.jsonl"));
            _engine = new RecordingEngine(_audit);
            _repository = new SqliteFutureWindowAuthorizationRepository(Store);
            Coordinator = new FutureWindowOneShotAuthorizationCoordinator(
                Store, _engine, new TestTray(), _audit, new StandingLeaseStartSafetyInterlock(),
                approvalTextProvider: () => new UiTextProvider(UiLanguage.EnUs),
                approvalUi: ui, approvalUiWaitLimit: waitLimit, recoverPending: false,
                approvalDesktopAvailableForTests: () => true);
        }

        internal FutureWindowAuthorizationScope CreatePending()
        {
            var id = "fwa_" + Guid.NewGuid().ToString("N");
            var scope = new FutureWindowAuthorizationScope(
                id, "300t-" + id, new string('a', 64), _sid, _session, _executable,
                null, null, 20, 120, _directory, id + ".mp4", "pending", DateTimeOffset.UtcNow,
                null, null, null, null, 0);
            var created = _repository.CreateOrGet(scope);
            Assert.Equal(FutureWindowCreateDisposition.Created, created.Disposition);
            Assert.NotNull(created.Row);
            return scope;
        }

        internal FutureWindowAuthorizationRow Get(string authorizationId) =>
            _repository.Get(authorizationId, _sid, _session)
            ?? throw new InvalidOperationException("The test authorization disappeared.");

        public void Dispose()
        {
            Coordinator.Dispose();
            _engine.Dispose();
            TestDirectoryCleanup.DeleteOwnedDirectory(_directory);
        }
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

    private sealed class ThrowingActiveRunStopper : IStandingLeaseActiveRunStopper
    {
        public void StopAll(string reason) => throw new InvalidOperationException("injected physical stop failure");
    }

    private sealed class ControllableApprovalUi : IFutureWindowAuthorizationApprovalUi
    {
        private readonly ConcurrentQueue<PendingPresentation> _presentations = new();
        private readonly FutureWindowApprovalOutcome _outcomeOnCancellation;
        private int _callCount;

        internal ControllableApprovalUi(FutureWindowApprovalOutcome outcomeOnCancellation) =>
            _outcomeOnCancellation = outcomeOnCancellation;

        internal int CallCount => Volatile.Read(ref _callCount);

        public Task<FutureWindowApprovalOutcome> ShowAsync(
            string authorizationId,
            FutureWindowAuthorizationApprovalDetails details,
            IUiTextProvider textProvider,
            Action<string, object> audit,
            CancellationToken cancellationToken)
        {
            var pending = new PendingPresentation();
            Interlocked.Increment(ref _callCount);
            _presentations.Enqueue(pending);
            cancellationToken.Register(() =>
            {
                pending.MarkCancellationObserved();
                pending.Complete(_outcomeOnCancellation);
            });
            return pending.Task;
        }

        internal async Task<PendingPresentation> WaitForCallAsync(int index, TimeSpan timeout)
        {
            var timer = Stopwatch.StartNew();
            while (timer.Elapsed < timeout)
            {
                if (_presentations.ToArray().ElementAtOrDefault(index) is { } presentation)
                    return presentation;
                await Task.Delay(10).ConfigureAwait(false);
            }
            throw new TimeoutException("The approval UI flight did not reach its presenter.");
        }
    }

    internal sealed class PendingPresentation
    {
        private readonly TaskCompletionSource<FutureWindowApprovalOutcome> _completion =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal Task<FutureWindowApprovalOutcome> Task => _completion.Task;
        private int _cancellationObserved;
        internal bool CancellationObserved => Volatile.Read(ref _cancellationObserved) != 0;
        internal void MarkCancellationObserved() => Interlocked.Exchange(ref _cancellationObserved, 1);
        internal void Complete(FutureWindowApprovalOutcome outcome) => _completion.TrySetResult(outcome);
    }
}
