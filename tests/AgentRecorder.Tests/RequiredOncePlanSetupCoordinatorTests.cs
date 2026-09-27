using System.Collections.Concurrent;
using System.Reflection;
using AgentRecorder.Api;
using AgentRecorder.App;
using AgentRecorder.Core;
using AgentRecorder.Core.Automation;
using AgentRecorder.Infrastructure;
using AgentRecorder.Logging;
using AgentRecorder.Persistence;
using AgentRecorder.Windows;
using Microsoft.Data.Sqlite;
using Xunit;

namespace AgentRecorder.Tests;

[Collection("HeadlessHostIntegration")]
public sealed partial class RequiredOncePlanSetupCoordinatorTests
{
    private const string Sid = "S-1-5-21-required-once-tests";
    private const string Session = "required-once-session-1";

    [Fact]
    public async Task ApprovalAtomicallyCreatesScheduledPlanAndFrozenSpecWithoutLeaseProofOrRun_AndDueRemainsInert()
    {
        using var database = new TemporaryDatabase();
        var clock = new MutableClock(DateTimeOffset.UtcNow);
        var ui = new FakeUi();
        var fixture = CreateFixture(database, clock, ui);
        using var coordinator = fixture.Coordinator;

        var created = coordinator.CreateOrGet(fixture.ApiRequest("required-success"));
        Assert.Equal(StandingPlanSetupCreateStatus.Created, created.Status);
        Assert.NotNull(created.SetupIntentId);
        await coordinator.WaitForIdleAsync().WaitAsync(TimeSpan.FromSeconds(5));

        var state = coordinator.Get(created.SetupIntentId!);
        Assert.NotNull(state);
        Assert.Equal("scheduled", state!.StatusCode);
        Assert.Equal("required", state.AuthorizationMode);
        Assert.Null(state.LeaseId);
        Assert.True(state.RequiresExecutionConfirmation);
        Assert.False(state.ExecutionSupported);
        Assert.Equal("execution_runtime_unavailable", state.NextAction);

        var query = new PlanExecutionStatusQueryService(database.Store);
        var execution = query.Get(state.PlanId, Sid, Session);
        Assert.NotNull(execution);
        Assert.Equal("once", execution!.Kind);
        Assert.Equal(1, execution.OccurrenceCount);
        Assert.Equal("scheduled", execution.NextOccurrence!.Status);
        Assert.Null(execution.NextOccurrence.RunId);
        Assert.False(execution.NextOccurrence.OutputPathRecorded);

        Assert.Equal(1, Count(database.Store, "plans"));
        Assert.Equal(1, Count(database.Store, "plan_occurrences"));
        Assert.Equal(1, Count(database.Store, "required_once_authorized_specs"));
        Assert.Equal(0, Count(database.Store, "consent_leases"));
        Assert.Equal(0, Count(database.Store, "lease_uses"));
        Assert.Equal(0, Count(database.Store, "authorized_capture_scopes"));
        Assert.Equal(0, Count(database.Store, "recording_runs"));

        // No due-time dispatcher or capture dependency is composed into this
        // coordinator. Advancing beyond the full window leaves the stored
        // occurrence scheduled, with the test-only backend boundary untouched.
        var captureBoundary = new FakeCaptureBoundary();
        Assert.DoesNotContain(
            typeof(RequiredOncePlanSetupCoordinator).GetConstructors(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                .SelectMany(constructor => constructor.GetParameters())
                .Select(parameter => parameter.ParameterType),
            type => type == typeof(RecordingEngine));
        clock.Set(fixture.Request.LatestStartUtc.AddSeconds(1));
        Assert.Equal("scheduled", coordinator.Get(created.SetupIntentId!)!.StatusCode);
        var afterDue = query.Get(state.PlanId, Sid, Session);
        Assert.Equal("scheduled", afterDue!.NextOccurrence!.Status);
        Assert.Null(afterDue.NextOccurrence.RunId);
        var dueDispatch = new StandingLeasePreparedIntentNaturalWakeDispatcher(
            database.Store,
            () => clock.Now,
            (request, _) =>
            {
                captureBoundary.Start();
                return Task.FromResult(StandingLeaseOneShotExecutionResult.Started("unexpected-run", "unexpected-use"));
            },
            () => new StandingLeaseGeneratedExecutionIds("unexpected-run", "unexpected-use"));
        var dispatchResult = await dueDispatch.DispatchAsync(created.SetupIntentId!);
        Assert.Equal(StandingLeaseNaturalWakeDispatchStatus.Rejected, dispatchResult.Status);
        Assert.Equal("natural_wake_intent_not_found", dispatchResult.Reason);
        Assert.Equal(0, captureBoundary.StartCount);
    }

    [Theory]
    [InlineData("Rejected", "rejected", "plan_creation_rejected_by_user")]
    [InlineData("TimedOut", "expired", "plan_creation_approval_timed_out")]
    public async Task RejectionOrTimeoutLeavesNoRunnableRows(
        string approval,
        string expectedStatus,
        string expectedReason)
    {
        using var database = new TemporaryDatabase();
        var clock = new MutableClock(DateTimeOffset.UtcNow);
        var ui = new FakeUi { ApprovalResult = Enum.Parse<RequiredOnceCreationApprovalResult>(approval) };
        var fixture = CreateFixture(database, clock, ui);
        using var coordinator = fixture.Coordinator;

        var created = coordinator.CreateOrGet(fixture.ApiRequest("required-reject-" + expectedStatus));
        await coordinator.WaitForIdleAsync().WaitAsync(TimeSpan.FromSeconds(5));
        var state = coordinator.Get(created.SetupIntentId!);
        Assert.Equal(expectedStatus, state!.StatusCode);
        Assert.Equal(expectedReason, state.ReasonCode);
        Assert.Equal(0, Count(database.Store, "plans"));
        Assert.Equal(0, Count(database.Store, "plan_occurrences"));
        Assert.Equal(0, Count(database.Store, "required_once_authorized_specs"));
        Assert.Equal(0, Count(database.Store, "consent_leases"));
        Assert.Equal(0, Count(database.Store, "recording_runs"));
    }

    [Fact]
    public async Task IdempotencyReturnsSameSetupAndConflictsOnDifferentNormalizedRequest()
    {
        using var database = new TemporaryDatabase();
        var clock = new MutableClock(DateTimeOffset.UtcNow);
        var ui = new FakeUi { Selection = new StandingPlanSetupSelection("selection_timeout", 0, 0, 0, 0, "", "virtual_screen") };
        var fixture = CreateFixture(database, clock, ui);
        using var coordinator = fixture.Coordinator;

        var first = coordinator.CreateOrGet(fixture.ApiRequest("same-required-key"));
        var duplicate = coordinator.CreateOrGet(fixture.ApiRequest("same-required-key"));
        var different = coordinator.CreateOrGet(fixture.ApiRequest("same-required-key", duration: TimeSpan.FromSeconds(59)));
        Assert.Equal(first.SetupIntentId, duplicate.SetupIntentId);
        Assert.Equal(StandingPlanSetupCreateStatus.Existing, duplicate.Status);
        Assert.Equal(StandingPlanSetupCreateStatus.Conflict, different.Status);
        Assert.Equal("idempotency_key_reused", different.ReasonCode);
        await coordinator.WaitForIdleAsync().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(1, Count(database.Store, "required_once_setup_intents"));
    }

    [Fact]
    public async Task PendingSelectionRehydratesAfterCoordinatorRestartInSameIdentityAndSession()
    {
        using var database = new TemporaryDatabase();
        var clock = new MutableClock(DateTimeOffset.UtcNow);
        var firstUi = new FakeUi { HoldSelection = true };
        var fixture = CreateFixture(database, clock, firstUi);
        var firstCoordinator = fixture.Coordinator;
        var created = firstCoordinator.CreateOrGet(fixture.ApiRequest("restart-required"));
        await firstUi.SelectionRequested.Task.WaitAsync(TimeSpan.FromSeconds(3));
        firstCoordinator.Dispose();

        var recoveredUi = new FakeUi();
        using var recovered = CreateFixture(database, clock, recoveredUi).Coordinator;
        await recoveredUi.ApprovalRequested.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await recovered.WaitForIdleAsync().WaitAsync(TimeSpan.FromSeconds(5));
        var state = recovered.Get(created.SetupIntentId!);
        Assert.Equal("scheduled", state!.StatusCode);
        Assert.Equal(1, Count(database.Store, "plans"));
        Assert.Equal(1, Count(database.Store, "plan_occurrences"));
    }

    [Fact]
    public async Task LateApprovalCallbackAfterLatestStartExpiresWithoutActivatingPlan()
    {
        using var database = new TemporaryDatabase();
        var clock = new MutableClock(DateTimeOffset.UtcNow);
        var ui = new FakeUi { HoldApproval = true };
        var fixture = CreateFixture(database, clock, ui);
        using var coordinator = fixture.Coordinator;
        var created = coordinator.CreateOrGet(fixture.ApiRequest("late-approval"));
        await ui.ApprovalRequested.Task.WaitAsync(TimeSpan.FromSeconds(5));

        clock.Set(fixture.Request.LatestStartUtc);
        Assert.Equal("expired", coordinator.Get(created.SetupIntentId!)!.StatusCode);
        ui.CompleteApproval(RequiredOnceCreationApprovalResult.Approved);
        await coordinator.WaitForIdleAsync().WaitAsync(TimeSpan.FromSeconds(5));

        var state = coordinator.Get(created.SetupIntentId!);
        Assert.Equal("expired", state!.StatusCode);
        Assert.Equal("latest_start_window_missed", state.ReasonCode);
        Assert.Equal(0, Count(database.Store, "plans"));
        Assert.Equal(0, Count(database.Store, "plan_occurrences"));
        Assert.Equal(0, Count(database.Store, "required_once_authorized_specs"));
    }

    [Fact]
    public async Task DisplayTopologyDriftAfterLocalApprovalRejectsWithoutActivation()
    {
        using var database = new TemporaryDatabase();
        var clock = new MutableClock(DateTimeOffset.UtcNow);
        var ui = new FakeUi();
        var fixture = CreateFixture(database, clock, ui);
        ui.OnApprovalRequested = () => fixture.Displays.Set(new[]
        {
            new StandingLeaseDisplayMetadata("DISPLAY-1", "stable-display-fingerprint-1",
                DisplayIdentityResolutionStatus.Resolved,
                new AuthorizedPhysicalRectangle(0, 0, 1600, 900),
                96, 96, 1600, 900, AuthorizedDisplayOrientation.Landscape),
        });
        using var coordinator = fixture.Coordinator;

        var created = coordinator.CreateOrGet(fixture.ApiRequest("topology-drift"));
        await coordinator.WaitForIdleAsync().WaitAsync(TimeSpan.FromSeconds(5));
        var state = coordinator.Get(created.SetupIntentId!);
        Assert.Equal("rejected", state!.StatusCode);
        Assert.Equal("display_topology_changed", state.ReasonCode);
        Assert.Equal(0, Count(database.Store, "plans"));
        Assert.Equal(0, Count(database.Store, "plan_occurrences"));
        Assert.Equal(0, Count(database.Store, "required_once_authorized_specs"));
    }

    [Fact]
    public async Task DisplayDriftBeforeCreationApprovalUsesDisplayReasonNotPriorSuccessfulOutputReason()
    {
        using var database = new TemporaryDatabase();
        var clock = new MutableClock(DateTimeOffset.UtcNow);
        var displays = new MutableDisplaySet(new[]
        {
            new StandingLeaseDisplayMetadata("DISPLAY-1", "stable-display-fingerprint-1",
                DisplayIdentityResolutionStatus.Resolved,
                new AuthorizedPhysicalRectangle(0, 0, 1920, 1080),
                96, 96, 1920, 1080, AuthorizedDisplayOrientation.Landscape),
        });
        var ui = new FakeUi();
        var fixture = CreateFixture(database, clock, ui,
            failureHook: stage =>
            {
                if (stage == "selection_saved_before_commit")
                {
                    displays.Set(new[]
                    {
                        new StandingLeaseDisplayMetadata("DISPLAY-1", "stable-display-fingerprint-1",
                            DisplayIdentityResolutionStatus.Resolved,
                            new AuthorizedPhysicalRectangle(0, 0, 1600, 900),
                            96, 96, 1600, 900, AuthorizedDisplayOrientation.Landscape),
                    });
                }
            },
            displaysForTest: displays);
        using var coordinator = fixture.Coordinator;

        var created = coordinator.CreateOrGet(fixture.ApiRequest("display-drift-before-approval"));
        await coordinator.WaitForIdleAsync().WaitAsync(TimeSpan.FromSeconds(5));

        var state = coordinator.Get(created.SetupIntentId!);
        Assert.Equal("rejected", state!.StatusCode);
        Assert.Equal("display_topology_changed", state.ReasonCode);
        Assert.Equal(0, Count(database.Store, "plans"));
        Assert.Equal(0, Count(database.Store, "plan_occurrences"));
        Assert.Equal(0, Count(database.Store, "required_once_authorized_specs"));
    }

    [Fact]
    public async Task CreationApprovalDialogClosesWhenCancellationRacesWithModalLoopEntry()
    {
        using var cancellation = new CancellationTokenSource();
        var completion = new TaskCompletionSource<RequiredOnceCreationApprovalResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var details = new RequiredOnceCreationApprovalDetails(
            "Display", "display-fingerprint",
            new AuthorizedPhysicalRectangle(0, 0, 1920, 1080),
            new AuthorizedPhysicalRectangle(10, 20, 320, 240),
            DateTimeOffset.UtcNow.AddMinutes(1), DateTimeOffset.UtcNow.AddMinutes(2),
            DateTimeOffset.UtcNow.AddMinutes(3), TimeSpan.FromSeconds(30),
            Path.GetTempPath(), "one-time.mp4");
        var thread = new Thread(() =>
        {
            try
            {
                completion.TrySetResult(RequiredPlanCreationApprovalForm.ShowModal(
                    details, cancellation.Token, cancellation.Cancel));
            }
            catch (Exception exception)
            {
                completion.TrySetException(exception);
            }
        }) { IsBackground = true, Name = "Required approval cancellation race test" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();

        Assert.Equal(RequiredOnceCreationApprovalResult.HostShutdown,
            await completion.Task.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task OutputReadinessChangeAfterApprovalRejectsWithoutActivation()
    {
        using var database = new TemporaryDatabase();
        var clock = new MutableClock(DateTimeOffset.UtcNow);
        var ui = new FakeUi();
        var fixture = CreateFixture(database, clock, ui);
        ui.OnApprovalRequested = () => fixture.OutputReadiness.ReasonCode = "execution_output_directory_changed";
        using var coordinator = fixture.Coordinator;

        var created = coordinator.CreateOrGet(fixture.ApiRequest("output-drift"));
        await coordinator.WaitForIdleAsync().WaitAsync(TimeSpan.FromSeconds(5));
        var state = coordinator.Get(created.SetupIntentId!);
        Assert.Equal("rejected", state!.StatusCode);
        Assert.Equal("execution_output_directory_changed", state.ReasonCode);
        Assert.Equal(0, Count(database.Store, "plans"));
        Assert.Equal(0, Count(database.Store, "plan_occurrences"));
        Assert.Equal(0, Count(database.Store, "required_once_authorized_specs"));
    }

    [Fact]
    public async Task MidTransactionFailureRollsBackPlanOccurrenceAndSpecTogether()
    {
        using var database = new TemporaryDatabase();
        var clock = new MutableClock(DateTimeOffset.UtcNow);
        var ui = new FakeUi();
        var fixture = CreateFixture(database, clock, ui, failureHook: stage =>
        {
            if (stage == "occurrence_inserted") throw new IOException("test crash after occurrence insert");
        });
        using var coordinator = fixture.Coordinator;
        var created = coordinator.CreateOrGet(fixture.ApiRequest("atomic-failure"));
        await coordinator.WaitForIdleAsync().WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal("rejected", coordinator.Get(created.SetupIntentId!)!.StatusCode);
        Assert.Equal(0, Count(database.Store, "plans"));
        Assert.Equal(0, Count(database.Store, "plan_occurrences"));
        Assert.Equal(0, Count(database.Store, "required_once_authorized_specs"));
        Assert.Equal(0, Count(database.Store, "consent_leases"));
        Assert.Equal(0, Count(database.Store, "lease_uses"));
        Assert.Equal(0, Count(database.Store, "recording_runs"));
    }

    [Fact]
    public async Task StatusQueryRejectsARequiredPlanWhoseImmutableSpecWasTampered()
    {
        using var database = new TemporaryDatabase();
        var clock = new MutableClock(DateTimeOffset.UtcNow);
        var fixture = CreateFixture(database, clock, new FakeUi());
        using var coordinator = fixture.Coordinator;
        var created = coordinator.CreateOrGet(fixture.ApiRequest("corrupt-spec"));
        await coordinator.WaitForIdleAsync().WaitAsync(TimeSpan.FromSeconds(5));
        var planId = coordinator.Get(created.SetupIntentId!)!.PlanId!;

        using (var connection = database.Store.OpenConnection())
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "DROP TRIGGER trg_required_once_authorized_specs_immutable_update;";
            command.ExecuteNonQuery();
        }
        using (var connection = database.Store.OpenConnection())
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "PRAGMA ignore_check_constraints = ON;";
            command.ExecuteNonQuery();
            command.CommandText = "UPDATE required_once_authorized_specs SET audio_mode_code = 'microphone' WHERE plan_id = $plan;";
            command.Parameters.AddWithValue("$plan", planId);
            command.ExecuteNonQuery();
        }

        Assert.Throws<Phase3PersistenceException>(() => new PlanExecutionStatusQueryService(database.Store).Get(planId, Sid, Session));
    }

    private static Fixture CreateFixture(
        TemporaryDatabase database,
        MutableClock clock,
        FakeUi ui,
        Action<string>? failureHook = null,
        MutableDisplaySet? displaysForTest = null)
    {
        var audit = new AuditLogger();
        var outputPath = database.OutputPath;
        Directory.CreateDirectory(outputPath);
        var displays = new[]
        {
            new StandingLeaseDisplayMetadata("DISPLAY-1", "stable-display-fingerprint-1",
                DisplayIdentityResolutionStatus.Resolved,
                new AuthorizedPhysicalRectangle(0, 0, 1920, 1080),
                96, 96, 1920, 1080, AuthorizedDisplayOrientation.Landscape),
        };
        var request = ApiRequest("fixture", outputPath, clock.Now);
        var principal = new RequiredOncePrincipal(Sid, Session);
        var readiness = new ReadyOutputReadiness(outputPath);
        var mutableDisplays = displaysForTest ?? new MutableDisplaySet(displays);
        var coordinator = new RequiredOncePlanSetupCoordinator(
            database.Store, audit, ui,
            currentDisplaysForTest: () => mutableDisplays.Current,
            principalForTest: () => principal,
            clockForTest: () => clock.Now,
            outputReadinessForTest: readiness,
            failureHookForTest: failureHook);
        return new(coordinator, request, outputPath, readiness, mutableDisplays, clock);
    }

    private static RequiredOncePlanApiRequest ApiRequest(string key, string? outputPath = null, DateTimeOffset? now = null, TimeSpan? duration = null)
    {
        var current = now ?? DateTimeOffset.UtcNow;
        var start = current.AddMinutes(2);
        var latest = current.AddMinutes(3);
        var runDuration = duration ?? TimeSpan.FromSeconds(60);
        return new(key, start, latest, start.AddMinutes(4), runDuration, outputPath ?? Path.GetTempPath(), "required-once-test.mp4");
    }

    private static long Count(SqliteOperationalStore store, string table)
    {
        Assert.Matches("^[a-z_]+$", table);
        using var connection = store.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = $"SELECT COUNT(*) FROM {table};";
        return (long)command.ExecuteScalar()!;
    }

    private sealed record Fixture(RequiredOncePlanSetupCoordinator Coordinator, RequiredOncePlanApiRequest Request, string OutputPath,
        ReadyOutputReadiness OutputReadiness, MutableDisplaySet Displays, MutableClock Clock)
    {
        internal RequiredOncePlanApiRequest ApiRequest(string key, TimeSpan? duration = null) =>
            RequiredOncePlanSetupCoordinatorTests.ApiRequest(key, OutputPath, Request.ScheduledStartUtc.AddMinutes(-2), duration);
    }

    private sealed class MutableDisplaySet(IReadOnlyList<StandingLeaseDisplayMetadata> initial)
    {
        internal IReadOnlyList<StandingLeaseDisplayMetadata> Current { get; private set; } = initial;
        internal void Set(IReadOnlyList<StandingLeaseDisplayMetadata> displays) => Current = displays;
    }

    private sealed class MutableClock(DateTimeOffset initial)
    {
        private long _ticks = initial.UtcDateTime.Ticks;
        internal DateTimeOffset Now => new(new DateTime(Interlocked.Read(ref _ticks), DateTimeKind.Utc));
        internal void Set(DateTimeOffset value) => Interlocked.Exchange(ref _ticks, value.UtcDateTime.Ticks);
    }

    private sealed class FakeUi : IRequiredOncePlanSetupUi
    {
        private readonly TaskCompletionSource<RequiredOnceCreationApprovalResult> _approval = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal bool IsAvailable { get; set; } = true;
        internal bool HoldSelection { get; set; }
        internal bool HoldApproval { get; set; }
        internal StandingPlanSetupSelection Selection { get; set; } = new("selected", 120, 140, 640, 360, "DISPLAY-1", "virtual_screen");
        internal RequiredOnceCreationApprovalResult ApprovalResult { get; set; } = RequiredOnceCreationApprovalResult.Approved;
        internal Action? OnApprovalRequested { get; set; }
        internal TaskCompletionSource<object?> SelectionRequested { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource<object?> ApprovalRequested { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public bool IsInteractiveDesktopAvailable => IsAvailable;

        public Task<StandingPlanSetupSelection> SelectFreshRegionAsync(CancellationToken cancellationToken)
        {
            SelectionRequested.TrySetResult(null);
            if (!HoldSelection) return Task.FromResult(Selection);
            cancellationToken.Register(() => _selection.TrySetResult(new("host_shutdown", 0, 0, 0, 0, "", "virtual_screen")));
            return _selection.Task;
        }

        private readonly TaskCompletionSource<StandingPlanSetupSelection> _selection = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<RequiredOnceCreationApprovalResult> ShowCreationApprovalAsync(RequiredOnceCreationApprovalDetails details, CancellationToken cancellationToken)
        {
            OnApprovalRequested?.Invoke();
            ApprovalRequested.TrySetResult(null);
            if (!HoldApproval) return Task.FromResult(ApprovalResult);
            cancellationToken.Register(() => _approval.TrySetResult(RequiredOnceCreationApprovalResult.HostShutdown));
            return _approval.Task;
        }

        internal void CompleteApproval(RequiredOnceCreationApprovalResult result) => _approval.TrySetResult(result);
    }

    private sealed class ReadyOutputReadiness(string expectedPath) : IStandingLeaseOutputReadinessProvider
    {
        internal string? ReasonCode { get; set; }
        public StandingLeaseOutputReadinessResult Check(string outputDirectory, string frozenFileName, TimeSpan reservedDuration)
        {
            var path = StandingLeaseOutputPath.NormalizeDirectory(outputDirectory);
            var file = Path.GetFullPath(Path.Combine(path, frozenFileName));
            var expected = StandingLeaseOutputPath.NormalizeDirectory(expectedPath);
            if (ReasonCode is { } reason)
                return StandingLeaseOutputReadinessResult.Rejected(reason,
                    new StandingLeaseOutputFileSystemSnapshot(path, file, true, false, true, true, long.MaxValue, 1));
            if (!string.Equals(path, expected, StringComparison.OrdinalIgnoreCase))
                return StandingLeaseOutputReadinessResult.Rejected("execution_output_directory_changed",
                    new StandingLeaseOutputFileSystemSnapshot(path, file, true, false, true, true, long.MaxValue, 1));
            return StandingLeaseOutputReadinessResult.Ready(
                new StandingLeaseOutputFileSystemSnapshot(path, file, true, false, true, true, long.MaxValue, 1));
        }
    }

    private sealed class FakeCaptureBoundary
    {
        internal int StartCount { get; private set; }
        internal void Start() => StartCount++;
    }

    private sealed class TemporaryDatabase : IDisposable
    {
        private readonly string _directory = Path.Combine(Path.GetTempPath(), "AgentRecorderRequiredOnce_" + Guid.NewGuid().ToString("N"));
        internal TemporaryDatabase()
        {
            Directory.CreateDirectory(_directory);
            OutputPath = Path.Combine(_directory, "output");
            Directory.CreateDirectory(OutputPath);
            Store = new SqliteOperationalStore(Path.Combine(_directory, "state", SqliteOperationalStore.DatabaseFileName));
            Store.Initialize();
        }
        internal string OutputPath { get; }
        internal SqliteOperationalStore Store { get; }
        public void Dispose()
        {
            try { if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true); } catch { }
        }
    }
}
