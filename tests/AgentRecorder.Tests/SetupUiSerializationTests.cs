using AgentRecorder.Api;
using AgentRecorder.App;
using AgentRecorder.Core;
using AgentRecorder.Core.Automation;
using AgentRecorder.Persistence;
using Xunit;

namespace AgentRecorder.Tests;

[Collection("NonParallel-AgentRecorderDataDir")]
public sealed class SetupUiSerializationTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task StandingAndRecurringFlowsBlockEachOtherAndResumeAfterModalUiCloses(bool standingOwnsFirst)
    {
        using var db = new RecurringPlanSetupTestFixture();
        var activity = new UiActivity();
        var ownerSelectionStarted = Signal();
        var releaseOwnerSelection = Signal();
        var standingUi = new TrackedStandingUi(activity)
        {
            Select = async _ =>
            {
                if (standingOwnsFirst)
                {
                    ownerSelectionStarted.TrySetResult(null);
                    await releaseOwnerSelection.Task.ConfigureAwait(false);
                }
                return SelectedStanding();
            },
        };
        var recurringUi = new TrackedRecurringUi(activity)
        {
            Select = async _ =>
            {
                if (!standingOwnsFirst)
                {
                    ownerSelectionStarted.TrySetResult(null);
                    await releaseOwnerSelection.Task.ConfigureAwait(false);
                }
                return TrackedRecurringUi.Selected();
            },
        };
        using var standing = db.StandingCoordinator(standingUi);
        using var recurring = db.Coordinator(recurringUi);

        StandingPlanSetupCreateResult? standingResult = null;
        RecurringPlanSetupCreateResult? recurringResult = null;
        if (standingOwnsFirst)
        {
            standingResult = standing.CreateOrGet(db.StandingRequest("standing-owner"));
            await ownerSelectionStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            recurringResult = recurring.CreateOrGet(db.Request("recurring-waiter"));
        }
        else
        {
            recurringResult = recurring.CreateOrGet(db.Request("recurring-owner"));
            await ownerSelectionStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            standingResult = standing.CreateOrGet(db.StandingRequest("standing-waiter"));
        }

        if (standingOwnsFirst)
        {
            Assert.Equal(1, standingUi.SelectionCalls);
            Assert.Equal(0, standingUi.ApprovalCalls);
            Assert.Equal(0, recurringUi.SelectionCalls);
            Assert.Equal(0, recurringUi.ApprovalCalls);
        }
        else
        {
            Assert.Equal(0, standingUi.SelectionCalls);
            Assert.Equal(0, standingUi.ApprovalCalls);
            Assert.Equal(1, recurringUi.SelectionCalls);
            Assert.Equal(0, recurringUi.ApprovalCalls);
        }
        Assert.Equal(1, activity.MaximumConcurrentUiCalls);

        releaseOwnerSelection.TrySetResult(null);
        if (standingOwnsFirst)
            await recurringUi.SelectionStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
        else
            await standingUi.SelectionStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));

        await recurring.WaitForIdleAsync().WaitAsync(TimeSpan.FromSeconds(15));
        await WaitForStandingStatusAsync(standing, standingResult!.SetupIntentId!, "scheduled");
        Assert.Equal("scheduled", recurring.Get(recurringResult!.State!.SetupIntentId)!.Status);
        Assert.Equal(1, activity.MaximumConcurrentUiCalls);
        Assert.Equal(1, standingUi.SelectionCalls);
        Assert.Equal(1, standingUi.ApprovalCalls);
        Assert.Equal(1, recurringUi.SelectionCalls);
        Assert.Equal(1, recurringUi.ApprovalCalls);
    }

    [Fact]
    public async Task DisposingQueuedCoordinatorCancelsOnlyItsWaitAndDoesNotReleaseOwner()
    {
        using var db = new RecurringPlanSetupTestFixture();
        var activity = new UiActivity();
        var ownerStarted = Signal();
        var releaseOwner = Signal();
        var ownerUi = new TrackedRecurringUi(activity)
        {
            Select = async _ =>
            {
                ownerStarted.TrySetResult(null);
                await releaseOwner.Task.ConfigureAwait(false);
                return TrackedRecurringUi.Selected();
            },
        };
        var queuedUi = new TrackedStandingUi(activity);
        using var owner = db.Coordinator(ownerUi);
        var queued = db.StandingCoordinator(queuedUi);

        var ownerResult = owner.CreateOrGet(db.Request("queued-dispose-owner"));
        await ownerStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var queuedResult = queued.CreateOrGet(db.StandingRequest("queued-dispose-standing"));
        await Task.Run(queued.Dispose).WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(0, queuedUi.SelectionCalls);
        Assert.Equal(0, queuedUi.ApprovalCalls);
        Assert.Equal(1, ownerUi.SelectionCalls);
        Assert.Equal(0, ownerUi.ApprovalCalls);
        Assert.Equal("setup_pending", owner.Get(ownerResult.State!.SetupIntentId)!.Status);
        Assert.Equal("region_selection_pending", db.Text("SELECT status_code FROM setup_intents WHERE intent_kind_code = 'standing_once_fixed_region';"));

        releaseOwner.TrySetResult(null);
        await owner.WaitForIdleAsync().WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal("scheduled", owner.Get(ownerResult.State!.SetupIntentId)!.Status);
        Assert.Equal("region_selection_pending", db.Text($"SELECT status_code FROM setup_intents WHERE intent_id = '{queuedResult.SetupIntentId}';"));
        Assert.Equal(1, activity.MaximumConcurrentUiCalls);
    }

    [Fact]
    public async Task DisposedOwnerKeepsGateUntilCancellationIgnoringModalActuallyReturns()
    {
        using var db = new RecurringPlanSetupTestFixture();
        var activity = new UiActivity();
        var ownerStarted = Signal();
        var cancellationObserved = Signal();
        var releaseLateUi = Signal();
        var ownerUi = new TrackedRecurringUi(activity)
        {
            Select = async token =>
            {
                ownerStarted.TrySetResult(null);
                using var registration = token.Register(() => cancellationObserved.TrySetResult(null));
                await releaseLateUi.Task.ConfigureAwait(false);
                return TrackedRecurringUi.Selected();
            },
        };
        var queuedUi = new TrackedStandingUi(activity);
        var owner = db.Coordinator(ownerUi);
        using var queued = db.StandingCoordinator(queuedUi);

        owner.CreateOrGet(db.Request("late-modal-owner"));
        await ownerStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        queued.CreateOrGet(db.StandingRequest("late-modal-waiter"));
        var disposing = Task.Run(owner.Dispose);
        await cancellationObserved.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await disposing.WaitAsync(TimeSpan.FromSeconds(8));

        Assert.Equal(0, queuedUi.SelectionCalls);
        Assert.Equal(0, queuedUi.ApprovalCalls);
        Assert.Equal(1, ownerUi.SelectionCalls);
        Assert.Equal(1, activity.MaximumConcurrentUiCalls);

        releaseLateUi.TrySetResult(null);
        await queuedUi.SelectionStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(1, activity.MaximumConcurrentUiCalls);
        await WaitForStandingStatusAsync(queued, db.Text("SELECT intent_id FROM setup_intents WHERE intent_kind_code = 'standing_once_fixed_region';"), "scheduled");
        owner.Dispose();
    }

    [Fact]
    public async Task QueuedRecurringFlowRereadsAndSkipsUiAfterExternalActivation()
    {
        using var db = new RecurringPlanSetupTestFixture();
        var activity = new UiActivity();
        var ownerStarted = Signal();
        var releaseOwner = Signal();
        var ownerUi = new TrackedStandingUi(activity)
        {
            Select = async _ =>
            {
                ownerStarted.TrySetResult(null);
                await releaseOwner.Task.ConfigureAwait(false);
                return SelectedStanding();
            },
        };
        using var owner = db.StandingCoordinator(ownerUi);
        var ownerResult = owner.CreateOrGet(db.StandingRequest("activation-recheck-owner"));
        await ownerStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        const string queuedIntentId = "activated-while-queued";
        db.Create(queuedIntentId, prepared: true);
        var queuedUi = new TrackedRecurringUi(activity);
        using var queued = db.Coordinator(queuedUi);
        Assert.Equal(1, queued.FlightCountForTests);
        Assert.Equal(0, queuedUi.SelectionCalls);
        Assert.Equal(0, queuedUi.ApprovalCalls);
        db.Activate(queuedIntentId);

        releaseOwner.TrySetResult(null);
        await WaitForStandingStatusAsync(owner, ownerResult.SetupIntentId!, "scheduled");
        await queued.WaitForIdleAsync().WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal("activated", db.Text("SELECT status_code FROM setup_intents WHERE intent_id = 'activated-while-queued';"));
        Assert.Equal(0, queuedUi.SelectionCalls);
        Assert.Equal(0, queuedUi.ApprovalCalls);
        Assert.Equal(1, activity.MaximumConcurrentUiCalls);
    }

    [Fact]
    public async Task QueuedStandingFlowRereadsAndSkipsUiAfterExternalTerminalSettlement()
    {
        using var db = new RecurringPlanSetupTestFixture();
        var activity = new UiActivity();
        var ownerStarted = Signal();
        var releaseOwner = Signal();
        var ownerUi = new TrackedRecurringUi(activity)
        {
            Select = async _ =>
            {
                ownerStarted.TrySetResult(null);
                await releaseOwner.Task.ConfigureAwait(false);
                return TrackedRecurringUi.Selected();
            },
        };
        using var owner = db.Coordinator(ownerUi);
        owner.CreateOrGet(db.Request("terminal-recheck-owner"));
        await ownerStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var queuedUi = new TrackedStandingUi(activity);
        using var queued = db.StandingCoordinator(queuedUi);
        var queuedResult = queued.CreateOrGet(db.StandingRequest("terminal-while-queued"));
        Assert.Equal(0, queuedUi.SelectionCalls);
        Assert.True(new StandingPlanSetupTerminalService(db.Store)
            .TrySetTerminal(queuedResult.SetupIntentId, "setup_conflict"));

        releaseOwner.TrySetResult(null);
        await owner.WaitForIdleAsync().WaitAsync(TimeSpan.FromSeconds(10));
        await WaitForStandingStatusAsync(queued, queuedResult.SetupIntentId!, "rejected");
        Assert.Equal(0, queuedUi.SelectionCalls);
        Assert.Equal(0, queuedUi.ApprovalCalls);
        Assert.Equal(1, activity.MaximumConcurrentUiCalls);
    }

    [Fact]
    public async Task RepeatedMixedCoordinatorFlightsNeverOverlapUiOrDeadlock()
    {
        using var db = new RecurringPlanSetupTestFixture();
        const int rounds = 5;
        var activity = new UiActivity();
        var recurringUi = new TrackedRecurringUi(activity);
        var standingUi = new TrackedStandingUi(activity);
        using var recurring = db.Coordinator(recurringUi);
        using var standing = db.StandingCoordinator(standingUi);
        var standingIds = new List<string>();
        var recurringIds = new List<string>();

        for (var i = 0; i < rounds; i++)
        {
            var recurringResult = recurring.CreateOrGet(db.Request("stress-recurring-" + i));
            var standingResult = standing.CreateOrGet(db.StandingRequest("stress-standing-" + i));
            Assert.Equal(RecurringPlanSetupCreateStatus.Created, recurringResult.Status);
            Assert.Equal(StandingPlanSetupCreateStatus.Created, standingResult.Status);
            recurringIds.Add(recurringResult.State!.SetupIntentId);
            standingIds.Add(standingResult.SetupIntentId!);
        }

        await recurring.WaitForIdleAsync().WaitAsync(TimeSpan.FromSeconds(30));
        foreach (var id in standingIds)
            await WaitForStandingStatusAsync(standing, id, "scheduled");
        foreach (var id in recurringIds)
            Assert.Equal("scheduled", recurring.Get(id)!.Status);

        Assert.Equal(rounds, recurringUi.SelectionCalls);
        Assert.Equal(rounds, recurringUi.ApprovalCalls);
        Assert.Equal(rounds, standingUi.SelectionCalls);
        Assert.Equal(rounds, standingUi.ApprovalCalls);
        Assert.Equal(1, activity.MaximumConcurrentUiCalls);
        Assert.Equal(0, recurring.FlightCountForTests);
    }

    [Fact]
    public async Task SuccessfulRecurringSetupCreatesNoOccurrenceRunLeaseUseOrMedia()
    {
        using var db = new RecurringPlanSetupTestFixture();
        var ui = new TrackedRecurringUi(new UiActivity());
        using var coordinator = db.Coordinator(ui);

        var result = coordinator.CreateOrGet(db.Request("setup-no-execution-artifacts"));
        await coordinator.WaitForIdleAsync().WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal("scheduled", coordinator.Get(result.State!.SetupIntentId)!.Status);
        db.AssertNoExecution();
        Assert.Equal(0, db.Scalar("SELECT COUNT(*) FROM recurring_occurrence_slots;"));
        Assert.Equal(0, db.Scalar("SELECT COUNT(*) FROM recurring_lease_uses;"));
        Assert.False(Directory.Exists(db.Request().OutputDirectory));
        Assert.Empty(Directory.GetFiles(db.Root, "*.mp4", SearchOption.AllDirectories));
    }

    [Fact]
    public void OutputReadinessAlwaysAttemptsExplicitCleanupAfterSuccessfulProbe()
    {
        var directory = CreateTempDirectory();
        var deleteCalls = 0;
        try
        {
            var provider = new RecurringSetupOutputReadinessProvider(
                deleteProbeForTest: path => { Interlocked.Increment(ref deleteCalls); File.Delete(path); },
                freeSpaceForTest: _ => (true, long.MaxValue));

            Assert.Equal(RecurringSetupOutputReadiness.Ready, provider.Check(directory, TimeSpan.FromMinutes(1)));
            Assert.Equal(1, deleteCalls);
            Assert.Empty(Directory.GetFiles(directory));
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public void OutputReadinessAttemptsCleanupWhenProbeCreationFails()
    {
        var directory = CreateTempDirectory();
        var deleteCalls = 0;
        try
        {
            var provider = new RecurringSetupOutputReadinessProvider(
                createProbeForTest: _ => throw new IOException("simulated probe create failure"),
                deleteProbeForTest: path => { Interlocked.Increment(ref deleteCalls); File.Delete(path); },
                freeSpaceForTest: _ => throw new Xunit.Sdk.XunitException("free-space check must not run"));

            Assert.Equal(RecurringSetupOutputReadiness.DirectoryUnwritable, provider.Check(directory, TimeSpan.FromMinutes(1)));
            Assert.Equal(1, deleteCalls);
            Assert.Empty(Directory.GetFiles(directory));
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public void OutputReadinessFailsClosedWhenExplicitProbeDeletionFails()
    {
        var directory = CreateTempDirectory();
        string? probe = null;
        try
        {
            var provider = new RecurringSetupOutputReadinessProvider(
                createProbeForTest: path =>
                {
                    probe = path;
                    return new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                },
                deleteProbeForTest: _ => throw new IOException("simulated probe delete failure"),
                freeSpaceForTest: _ => throw new Xunit.Sdk.XunitException("free-space check must not run"));

            Assert.Equal(RecurringSetupOutputReadiness.DirectoryUnwritable, provider.Check(directory, TimeSpan.FromMinutes(1)));
            Assert.NotNull(probe);
            Assert.True(File.Exists(probe));
        }
        finally
        {
            if (probe is not null && File.Exists(probe)) File.Delete(probe);
            Assert.Empty(Directory.GetFiles(directory));
            Directory.Delete(directory, recursive: true);
        }
    }

    private static async Task WaitForStandingStatusAsync(StandingPlanSetupCoordinator coordinator, string id, string status)
    {
        for (var i = 0; i < 250; i++)
        {
            if (coordinator.Get(id)?.StatusCode == status) return;
            await Task.Delay(20).ConfigureAwait(false);
        }
        throw new Xunit.Sdk.XunitException($"Timed out waiting for standing status '{status}' for '{id}'.");
    }

    private static string CreateTempDirectory()
    {
        var directory = Path.Combine(Path.GetTempPath(), "AgentRecorderRecurringProbeTest_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return directory;
    }

    private static StandingPlanSetupSelection SelectedStanding() =>
        new("selected", 110, 220, 640, 480, "stable-display", "virtual_screen");

    private static TaskCompletionSource<object?> Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private sealed class UiActivity
    {
        private int _active;
        private int _maximum;

        internal int MaximumConcurrentUiCalls => Volatile.Read(ref _maximum);

        internal async Task<T> TrackAsync<T>(Func<Task<T>> action)
        {
            var active = Interlocked.Increment(ref _active);
            while (true)
            {
                var maximum = Volatile.Read(ref _maximum);
                if (maximum >= active || Interlocked.CompareExchange(ref _maximum, active, maximum) == maximum) break;
            }
            try { return await action().ConfigureAwait(false); }
            finally { Interlocked.Decrement(ref _active); }
        }
    }

    private sealed class TrackedStandingUi(UiActivity activity) : IStandingPlanSetupUi
    {
        private int _selectionCalls;
        private int _approvalCalls;
        internal Func<CancellationToken, Task<StandingPlanSetupSelection>>? Select { get; init; }
        internal TaskCompletionSource<object?> SelectionStarted { get; } = Signal();
        internal int SelectionCalls => Volatile.Read(ref _selectionCalls);
        internal int ApprovalCalls => Volatile.Read(ref _approvalCalls);
        public bool IsInteractiveDesktopAvailable => true;

        public Task<StandingPlanSetupSelection> SelectFreshRegionAsync(CancellationToken cancellationToken) =>
            activity.TrackAsync(async () =>
            {
                Interlocked.Increment(ref _selectionCalls);
                SelectionStarted.TrySetResult(null);
                return await (Select?.Invoke(cancellationToken) ?? Task.FromResult(SelectedStanding())).ConfigureAwait(false);
            });

        public Task<bool> ShowApprovalAsync(StandingLeaseApprovalDetails details, CancellationToken cancellationToken) =>
            activity.TrackAsync(() =>
            {
                Interlocked.Increment(ref _approvalCalls);
                return Task.FromResult(true);
            });
    }

    private sealed class TrackedRecurringUi(UiActivity activity) : IRecurringPlanSetupUi
    {
        private int _selectionCalls;
        private int _approvalCalls;
        internal Func<CancellationToken, Task<RecurringPlanSetupSelection>>? Select { get; init; }
        internal TaskCompletionSource<object?> SelectionStarted { get; } = Signal();
        internal int SelectionCalls => Volatile.Read(ref _selectionCalls);
        internal int ApprovalCalls => Volatile.Read(ref _approvalCalls);
        public bool IsInteractiveDesktopAvailable => true;

        public Task<RecurringPlanSetupSelection> SelectFreshRegionAsync(CancellationToken cancellationToken) =>
            activity.TrackAsync(async () =>
            {
                Interlocked.Increment(ref _selectionCalls);
                SelectionStarted.TrySetResult(null);
                return await (Select?.Invoke(cancellationToken) ?? Task.FromResult(Selected())).ConfigureAwait(false);
            });

        public Task<RecurringLeaseApprovalResult> ShowApprovalAsync(RecurringLeaseApprovalDetails details, CancellationToken cancellationToken) =>
            activity.TrackAsync(() =>
            {
                Interlocked.Increment(ref _approvalCalls);
                return Task.FromResult(RecurringLeaseApprovalResult.Approved);
            });

        internal static RecurringPlanSetupSelection Selected() =>
            new(RecurringRegionSelectionStatus.Selected, 110, 220, 640, 480);
    }
}
