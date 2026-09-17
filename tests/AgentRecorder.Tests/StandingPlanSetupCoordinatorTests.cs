using AgentRecorder.Api;
using AgentRecorder.App;
using AgentRecorder.Core;
using AgentRecorder.Core.Automation;
using AgentRecorder.Infrastructure;
using AgentRecorder.Logging;
using AgentRecorder.Persistence;
using AgentRecorder.Windows;
using Xunit;

namespace AgentRecorder.Tests;

[Collection("NonParallel-AgentRecorderDataDir")]
public sealed class StandingPlanSetupCoordinatorTests
{
    [Fact]
    public async Task ProductionCoordinatorSeamCompletesSelectionApprovalAndScheduling()
    {
        using var database = new TemporaryDatabase();
        var ui = new CompletingSetupUi();
        using var coordinator = new StandingPlanSetupCoordinator(
            database.Store,
            new AuditLogger(),
            ui,
            new StandingLeaseSafetyControlService(database.Store),
            () => new[]
            {
                new StandingLeaseDisplayMetadata(
                    "display-a", "display-a", DisplayIdentityResolutionStatus.Resolved,
                    new AuthorizedPhysicalRectangle(0, 0, 1920, 1080),
                    96, 96, 1920, 1080, AuthorizedDisplayOrientation.Landscape),
            },
            outputReadinessProviderForTest: new ReadyOutputReadinessProvider());

        var result = coordinator.CreateOrGet(CreateRequest("full-flow-key"));
        Assert.Equal(StandingPlanSetupCreateStatus.Created, result.Status);
        Assert.NotNull(result.SetupIntentId);

        StandingPlanSetupState? state = null;
        for (var attempt = 0; attempt < 100; attempt++)
        {
            state = coordinator.Get(result.SetupIntentId);
            if (state?.StatusCode == "scheduled")
                break;
            await Task.Delay(20);
        }

        Assert.NotNull(state);
        Assert.Equal("scheduled", state!.StatusCode);
        Assert.Equal(1, ui.SelectionCalls);
        Assert.Equal(1, ui.ApprovalCalls);
        Assert.NotNull(state.PlanId);
        Assert.NotNull(state.OccurrenceId);
        Assert.NotNull(state.LeaseId);
        Assert.Equal("activated", ReadText(database.Store, "SELECT status_code FROM setup_intents;"));
        Assert.Equal("enabled", ReadText(database.Store, "SELECT status_code FROM plans;"));
        Assert.Equal("authorized", ReadText(database.Store, "SELECT status_code FROM plan_occurrences;"));
        Assert.Equal("active", ReadText(database.Store, "SELECT status_code FROM consent_leases;"));
    }

    [Fact]
    public async Task ProductionCoordinatorWithValidDirectoryCompletesUsingRealOutputReadiness()
    {
        var directory = Path.Combine(Path.GetTempPath(), "AgentRecorderCoordinatorRealOutput_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var oldFreeSpaceProvider = RecordingPreflightChecker.FreeSpaceProvider;
        try
        {
            RecordingPreflightChecker.FreeSpaceProvider = (string _, out long bytes) =>
            {
                bytes = long.MaxValue;
                return true;
            };
            using var database = new TemporaryDatabase();
            var ui = new CompletingSetupUi();
            using var coordinator = new StandingPlanSetupCoordinator(
                database.Store,
                new AuditLogger(),
                ui,
                new StandingLeaseSafetyControlService(database.Store),
                CurrentDisplays());

            var result = coordinator.CreateOrGet(CreateRequest("real-output-flow", directory));
            var state = await WaitForStatusAsync(coordinator, result.SetupIntentId!, "scheduled");

            Assert.Equal("scheduled", state.StatusCode);
            Assert.Equal(1, ui.SelectionCalls);
            Assert.Equal(1, ui.ApprovalCalls);
            Assert.Empty(Directory.GetFiles(directory, ".agent-recorder-standing-output-readiness-*").ToArray());
        }
        finally
        {
            RecordingPreflightChecker.FreeSpaceProvider = oldFreeSpaceProvider;
            try { Directory.Delete(directory, recursive: true); } catch (IOException) { }
        }
    }

    [Fact]
    public async Task ShutdownWinsApprovalCommitBarrierAndLeavesAllPreparedRowsUnchanged()
    {
        using var database = new TemporaryDatabase();
        var audit = new CapturingAuditLogger();
        var ui = new ApprovalReturningSetupUi();
        var approvalCommitPaused = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseApprovalCommit = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var shutdownLinearized = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var approvalLinearized = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var coordinator = new StandingPlanSetupCoordinator(
            database.Store,
            audit,
            ui,
            new StandingLeaseSafetyControlService(database.Store),
            CurrentDisplays(),
            async () =>
            {
                approvalCommitPaused.TrySetResult(null);
                await releaseApprovalCommit.Task.ConfigureAwait(false);
            },
            () => approvalLinearized.TrySetResult(null),
            () => shutdownLinearized.TrySetResult(null),
            outputReadinessProviderForTest: new ReadyOutputReadinessProvider());

        try
        {
            var result = coordinator.CreateOrGet(CreateRequest("shutdown-approval-commit-key"));
            Assert.Equal(StandingPlanSetupCreateStatus.Created, result.Status);
            await ui.ApprovalReturned.Task.WaitAsync(TimeSpan.FromSeconds(2));
            await approvalCommitPaused.Task.WaitAsync(TimeSpan.FromSeconds(2));

            var versionsBeforeShutdown = ReadVersions(database.Store);
            var disposeTask = Task.Run(coordinator.Dispose);
            await shutdownLinearized.Task.WaitAsync(TimeSpan.FromSeconds(2));

            Assert.Equal("lease_approval_pending", ReadText(database.Store, "SELECT status_code FROM setup_intents;"));
            Assert.Equal("draft", ReadText(database.Store, "SELECT status_code FROM plans;"));
            Assert.Equal("pending_lease_approval", ReadText(database.Store, "SELECT status_code FROM plan_occurrences;"));
            Assert.Equal("pending", ReadText(database.Store, "SELECT status_code FROM consent_leases;"));
            Assert.Equal(versionsBeforeShutdown, ReadVersions(database.Store));
            Assert.False(approvalLinearized.Task.IsCompleted);
            Assert.False(disposeTask.IsCompleted);

            releaseApprovalCommit.TrySetResult(null);
            await disposeTask.WaitAsync(TimeSpan.FromSeconds(2));

            Assert.Equal("lease_approval_pending", ReadText(database.Store, "SELECT status_code FROM setup_intents;"));
            Assert.DoesNotContain("consent_lease.approved", audit.Events.ToArray());
        }
        finally
        {
            releaseApprovalCommit.TrySetResult(null);
            coordinator.Dispose();
        }
    }

    [Fact]
    public async Task ApprovalCommitWinsLinearizationAndDisposeWaitsForCompleteActivation()
    {
        using var database = new TemporaryDatabase();
        var audit = new CapturingAuditLogger();
        var ui = new ApprovalReturningSetupUi();
        var approvalCommitPaused = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseApprovalCommit = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var approvalLinearized = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var shutdownLinearized = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var coordinator = new StandingPlanSetupCoordinator(
            database.Store,
            audit,
            ui,
            new StandingLeaseSafetyControlService(database.Store),
            CurrentDisplays(),
            async () =>
            {
                approvalCommitPaused.TrySetResult(null);
                await releaseApprovalCommit.Task.ConfigureAwait(false);
            },
            () => approvalLinearized.TrySetResult(null),
            () => shutdownLinearized.TrySetResult(null),
            outputReadinessProviderForTest: new ReadyOutputReadinessProvider());

        try
        {
            var result = coordinator.CreateOrGet(CreateRequest("approval-commit-wins-key"));
            Assert.Equal(StandingPlanSetupCreateStatus.Created, result.Status);
            await ui.ApprovalReturned.Task.WaitAsync(TimeSpan.FromSeconds(2));
            await approvalCommitPaused.Task.WaitAsync(TimeSpan.FromSeconds(2));

            releaseApprovalCommit.TrySetResult(null);
            await approvalLinearized.Task.WaitAsync(TimeSpan.FromSeconds(2));
            var disposeTask = Task.Run(coordinator.Dispose);
            await disposeTask.WaitAsync(TimeSpan.FromSeconds(2));

            Assert.True(shutdownLinearized.Task.IsCompleted);
            Assert.Equal("activated", ReadText(database.Store, "SELECT status_code FROM setup_intents;"));
            Assert.Equal("enabled", ReadText(database.Store, "SELECT status_code FROM plans;"));
            Assert.Equal("authorized", ReadText(database.Store, "SELECT status_code FROM plan_occurrences;"));
            Assert.Equal("active", ReadText(database.Store, "SELECT status_code FROM consent_leases;"));
            Assert.Contains("consent_lease.approved", audit.Events.ToArray());
        }
        finally
        {
            releaseApprovalCommit.TrySetResult(null);
            coordinator.Dispose();
        }
    }

    [Fact]
    public async Task SameIdempotencyKeySharesOneInteractiveFlight()
    {
        using var database = new TemporaryDatabase();
        var ui = new BlockingSetupUi();
        var request = CreateRequest("shared-key");
        using var coordinator = new StandingPlanSetupCoordinator(
            database.Store, new AuditLogger(), ui, new StandingLeaseSafetyControlService(database.Store),
            outputReadinessProviderForTest: new ReadyOutputReadinessProvider());

        var first = coordinator.CreateOrGet(request);
        var second = coordinator.CreateOrGet(request);

        Assert.True(first.Status == StandingPlanSetupCreateStatus.Created, first.ReasonCode);
        Assert.Equal(StandingPlanSetupCreateStatus.Existing, second.Status);
        Assert.Equal(first.SetupIntentId, second.SetupIntentId);
        await ui.SelectionStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(1, ui.SelectionCalls);

        coordinator.Dispose();
        await ui.CancellationObserved.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal("region_selection_pending", ReadText(database.Store, "SELECT status_code FROM setup_intents;"));
    }

    [Fact]
    public async Task DisposeCancelsUiAndLeavesIntentRecoverableWithoutLateTerminalReceipt()
    {
        using var database = new TemporaryDatabase();
        var ui = new BlockingSetupUi();
        var request = CreateRequest("shutdown-key");
        var coordinator = new StandingPlanSetupCoordinator(
            database.Store, new AuditLogger(), ui, new StandingLeaseSafetyControlService(database.Store),
            outputReadinessProviderForTest: new ReadyOutputReadinessProvider());

        var result = coordinator.CreateOrGet(request);
        Assert.True(result.Status == StandingPlanSetupCreateStatus.Created, result.ReasonCode);
        await ui.SelectionStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));

        coordinator.Dispose();
        await ui.CancellationObserved.Task.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(0, ui.ApprovalCalls);
        Assert.Equal("region_selection_pending", ReadText(database.Store, "SELECT status_code FROM setup_intents;"));
        Assert.Equal(0L, Scalar(database.Store, "SELECT version FROM setup_intents;"));
    }

    [Theory]
    [InlineData("execution_output_directory_unavailable")]
    [InlineData("execution_output_directory_unwritable")]
    [InlineData("execution_disk_space_insufficient")]
    [InlineData("execution_output_file_exists")]
    public async Task OutputReadinessFailureHappensBeforeSelectionAndIsIdempotent(string reasonCode)
    {
        using var database = new TemporaryDatabase();
        var ui = new CompletingSetupUi();
        var output = new MutableOutputReadinessProvider(reasonCode);
        var audit = new CapturingAuditLogger();
        using var coordinator = new StandingPlanSetupCoordinator(
            database.Store,
            audit,
            ui,
            new StandingLeaseSafetyControlService(database.Store),
            outputReadinessProviderForTest: output);

        var request = CreateRequest("output-failure-" + reasonCode);
        var first = coordinator.CreateOrGet(request);
        await output.Checked.Task.WaitAsync(TimeSpan.FromSeconds(2));
        try
        {
            await WaitForStatusAsync(coordinator, first.SetupIntentId!, "rejected");
        }
        catch (Xunit.Sdk.XunitException exception)
        {
            throw new Xunit.Sdk.XunitException(
                $"{exception.Message}; calls={output.Calls}; selection={ui.SelectionCalls}; failures={ui.FailureCalls}; audit={string.Join(',', audit.Events)}");
        }
        var replay = coordinator.CreateOrGet(request);

        Assert.Equal(StandingPlanSetupCreateStatus.Created, first.Status);
        Assert.Equal(StandingPlanSetupCreateStatus.Existing, replay.Status);
        Assert.Equal(first.SetupIntentId, replay.SetupIntentId);
        Assert.Equal(0, ui.SelectionCalls);
        Assert.Equal(1, ui.FailureCalls);
        Assert.Equal(reasonCode, ui.FailureReasons.Single());
        Assert.Equal(reasonCode, ReadText(database.Store, "SELECT terminal_reason_code FROM setup_intents;"));
        Assert.Equal(0L, Scalar(database.Store, "SELECT COUNT(*) FROM plans;"));
        Assert.Equal(0L, Scalar(database.Store, "SELECT COUNT(*) FROM consent_leases;"));
        Assert.Equal(1, output.Calls);
    }

    [Fact]
    public async Task ApprovalTimeOutputMutationRejectsPreparedChainWithoutActivation()
    {
        using var database = new TemporaryDatabase();
        var ui = new ApprovalReturningSetupUi();
        var output = new MutableOutputReadinessProvider();
        var audit = new CapturingAuditLogger();
        var coordinator = new StandingPlanSetupCoordinator(
            database.Store,
            audit,
            ui,
            new StandingLeaseSafetyControlService(database.Store),
            CurrentDisplays(),
            beforeApprovalCommitForTest: () =>
            {
                output.ReasonCode = "execution_output_file_exists";
                return Task.CompletedTask;
            },
            outputReadinessProviderForTest: output);

        try
        {
            var result = coordinator.CreateOrGet(CreateRequest("approval-output-mutation"));
            try
            {
                await WaitForStatusAsync(coordinator, result.SetupIntentId!, "rejected");
            }
            catch (Xunit.Sdk.XunitException exception)
            {
                throw new Xunit.Sdk.XunitException(
                    $"{exception.Message}; calls={output.Calls}; approval={ui.ApprovalCalls}; failures={ui.FailureCalls}; audit={string.Join(',', audit.Events)}");
            }

            Assert.Equal(1, ui.ApprovalCalls);
            Assert.Equal(1, ui.FailureCalls);
            Assert.Equal("execution_output_file_exists", ui.FailureReasons.Single());
            Assert.Equal("rejected", ReadText(database.Store, "SELECT status_code FROM setup_intents;"));
            Assert.Equal("cancelled", ReadText(database.Store, "SELECT status_code FROM plans;"));
            Assert.Equal("cancelled", ReadText(database.Store, "SELECT status_code FROM plan_occurrences;"));
            Assert.Equal("rejected", ReadText(database.Store, "SELECT status_code FROM consent_leases;"));
        }
        finally
        {
            coordinator.Dispose();
        }
    }

    [Fact]
    public void RealOutputProbeCleansUpOnSuccessAndFailure()
    {
        var directory = Path.Combine(Path.GetTempPath(), "AgentRecorderOutputReadiness_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var oldFreeSpaceProvider = RecordingPreflightChecker.FreeSpaceProvider;
        try
        {
            RecordingPreflightChecker.FreeSpaceProvider = (string _, out long bytes) =>
            {
                bytes = long.MaxValue;
                return true;
            };
            var provider = SystemQueryStandingLeaseOutputReadinessProvider.Instance;
            var prefix = ".agent-recorder-standing-output-readiness-";
            var missingDirectory = Path.Combine(directory, "missing");

            var missing = provider.Check(missingDirectory, "missing.mp4", TimeSpan.FromSeconds(30));
            Assert.False(missing.IsReady);
            Assert.Equal("execution_output_directory_unavailable", missing.ReasonCode);
            Assert.False(Directory.Exists(missingDirectory));

            var ready = provider.Check(directory, "capture.mp4", TimeSpan.FromSeconds(30));
            Assert.True(ready.IsReady, ready.ReasonCode);
            Assert.Empty(Directory.GetFiles(directory, prefix + "*"));

            File.WriteAllText(Path.Combine(directory, "existing.mp4"), "test");
            var rejected = provider.Check(directory, "existing.mp4", TimeSpan.FromSeconds(30));
            Assert.False(rejected.IsReady);
            Assert.Equal("execution_output_file_exists", rejected.ReasonCode);
            Assert.Empty(Directory.GetFiles(directory, prefix + "*"));

            RecordingPreflightChecker.FreeSpaceProvider = (string _, out long bytes) =>
            {
                bytes = 0;
                return true;
            };
            var insufficient = provider.Check(directory, "insufficient.mp4", TimeSpan.FromSeconds(30));
            Assert.False(insufficient.IsReady);
            Assert.Equal("execution_disk_space_insufficient", insufficient.ReasonCode);
            Assert.Empty(Directory.GetFiles(directory, prefix + "*"));

            RecordingPreflightChecker.FreeSpaceProvider = (string _, out long bytes) => throw new IOException("probe failure");
            var exceptionPath = provider.Check(directory, "exception.mp4", TimeSpan.FromSeconds(30));
            Assert.False(exceptionPath.IsReady);
            Assert.Equal("execution_disk_space_unavailable", exceptionPath.ReasonCode);
            Assert.Empty(Directory.GetFiles(directory, prefix + "*"));
        }
        finally
        {
            RecordingPreflightChecker.FreeSpaceProvider = oldFreeSpaceProvider;
            try { Directory.Delete(directory, recursive: true); } catch (IOException) { }
        }
    }

    private static async Task<StandingPlanSetupState> WaitForStatusAsync(
        StandingPlanSetupCoordinator coordinator,
        string intentId,
        string status)
    {
        for (var attempt = 0; attempt < 100; attempt++)
        {
            var state = coordinator.Get(intentId);
            if (state?.StatusCode == status)
                return state;
            await Task.Delay(20);
        }

        var finalState = coordinator.Get(intentId);
        throw new Xunit.Sdk.XunitException(
            $"Timed out waiting for status '{status}', actual '{finalState?.StatusCode}', reason '{finalState?.ReasonCode}'.");
    }

    private static StandingPlanApiRequest CreateRequest(string key, string? outputDirectory = null)
    {
        var now = DateTimeOffset.UtcNow;
        return new StandingPlanApiRequest(
            key,
            now.AddMinutes(2),
            now.AddMinutes(3),
            now.AddMinutes(5),
            now.AddMinutes(10),
            TimeSpan.FromSeconds(30),
            outputDirectory ?? Path.Combine(Path.GetTempPath(), "AgentRecorderCoordinator", "captures"),
            "capture.mp4");
    }

    private static long Scalar(SqliteOperationalStore store, string sql)
    {
        using var connection = store.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt64(command.ExecuteScalar());
    }

    private static long[] ReadVersions(SqliteOperationalStore store) =>
        new[]
        {
            Scalar(store, "SELECT version FROM setup_intents;"),
            Scalar(store, "SELECT version FROM plans;"),
            Scalar(store, "SELECT version FROM plan_occurrences;"),
            Scalar(store, "SELECT version FROM consent_leases;"),
        };

    private static Func<IReadOnlyList<StandingLeaseDisplayMetadata>> CurrentDisplays() => () => new[]
    {
        new StandingLeaseDisplayMetadata(
            "display-a", "display-a", DisplayIdentityResolutionStatus.Resolved,
            new AuthorizedPhysicalRectangle(0, 0, 1920, 1080),
            96, 96, 1920, 1080, AuthorizedDisplayOrientation.Landscape),
    };

    private static string ReadText(SqliteOperationalStore store, string sql)
    {
        using var connection = store.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToString(command.ExecuteScalar())!;
    }

    private sealed class BlockingSetupUi : IStandingPlanSetupUi
    {
        internal readonly TaskCompletionSource<object?> SelectionStarted =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource<object?> CancellationObserved =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal int SelectionCalls;
        internal int ApprovalCalls;

        public bool IsInteractiveDesktopAvailable => true;

        public Task<StandingPlanSetupSelection> SelectFreshRegionAsync(CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref SelectionCalls);
            SelectionStarted.TrySetResult(null);
            cancellationToken.Register(() =>
            {
                CancellationObserved.TrySetResult(null);
                _selection.TrySetResult(new StandingPlanSetupSelection("host_shutdown", 0, 0, 0, 0, "", ""));
            });
            return _selection.Task;
        }

        public Task<bool> ShowApprovalAsync(StandingLeaseApprovalDetails details, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref ApprovalCalls);
            return Task.FromResult(false);
        }

        private readonly TaskCompletionSource<StandingPlanSetupSelection> _selection =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private sealed class CompletingSetupUi : IStandingPlanSetupUi
    {
        internal int SelectionCalls;
        internal int ApprovalCalls;
        internal int FailureCalls;
        internal readonly List<string> FailureReasons = new();

        public bool IsInteractiveDesktopAvailable => true;

        public Task<StandingPlanSetupSelection> SelectFreshRegionAsync(CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref SelectionCalls);
            return Task.FromResult(new StandingPlanSetupSelection(
                "selected", 10, 20, 640, 480, "display-a", "virtual_screen"));
        }

        public Task<bool> ShowApprovalAsync(StandingLeaseApprovalDetails details, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref ApprovalCalls);
            return Task.FromResult(true);
        }

        public Task ShowFailureAsync(string reasonCode, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref FailureCalls);
            lock (FailureReasons)
                FailureReasons.Add(reasonCode);
            return Task.CompletedTask;
        }
    }

    private sealed class ApprovalReturningSetupUi : IStandingPlanSetupUi
    {
        internal readonly TaskCompletionSource<object?> ApprovalReturned =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal int ApprovalCalls;
        internal int FailureCalls;
        internal readonly List<string> FailureReasons = new();

        public bool IsInteractiveDesktopAvailable => true;

        public Task<StandingPlanSetupSelection> SelectFreshRegionAsync(CancellationToken cancellationToken) =>
            Task.FromResult(new StandingPlanSetupSelection(
                "selected", 10, 20, 640, 480, "display-a", "virtual_screen"));

        public Task<bool> ShowApprovalAsync(StandingLeaseApprovalDetails details, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref ApprovalCalls);
            ApprovalReturned.TrySetResult(null);
            return Task.FromResult(true);
        }

        public Task ShowFailureAsync(string reasonCode, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref FailureCalls);
            lock (FailureReasons)
                FailureReasons.Add(reasonCode);
            return Task.CompletedTask;
        }
    }

    private sealed class ReadyOutputReadinessProvider : IStandingLeaseOutputReadinessProvider
    {
        public StandingLeaseOutputReadinessResult Check(
            string outputDirectory,
            string frozenFileName,
            TimeSpan reservedDuration) =>
            StandingLeaseOutputReadinessResult.Ready(new StandingLeaseOutputFileSystemSnapshot(
                outputDirectory,
                Path.Combine(outputDirectory, frozenFileName),
                directoryExists: true,
                frozenFileExists: false,
                directoryWritable: true,
                freeSpaceAvailable: true,
                availableFreeBytes: long.MaxValue,
                requiredFreeBytes: 1));
    }

    private sealed class MutableOutputReadinessProvider : IStandingLeaseOutputReadinessProvider
    {
        internal MutableOutputReadinessProvider(string? reasonCode = null) => ReasonCode = reasonCode;
        internal string? ReasonCode { get; set; }
        internal int Calls;
        internal readonly TaskCompletionSource<object?> Checked =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public StandingLeaseOutputReadinessResult Check(
            string outputDirectory,
            string frozenFileName,
            TimeSpan reservedDuration)
        {
            Interlocked.Increment(ref Calls);
            Checked.TrySetResult(null);
            if (ReasonCode is null)
                return StandingLeaseOutputReadinessResult.Ready(new StandingLeaseOutputFileSystemSnapshot(
                    outputDirectory,
                    Path.Combine(outputDirectory, frozenFileName),
                    directoryExists: true,
                    frozenFileExists: false,
                    directoryWritable: true,
                    freeSpaceAvailable: true,
                    availableFreeBytes: long.MaxValue,
                    requiredFreeBytes: 1));

            return StandingLeaseOutputReadinessResult.Rejected(
                ReasonCode,
                new StandingLeaseOutputFileSystemSnapshot(
                    outputDirectory,
                    Path.Combine(outputDirectory, frozenFileName),
                    directoryExists: ReasonCode != "execution_output_directory_unavailable",
                    frozenFileExists: ReasonCode == "execution_output_file_exists",
                    directoryWritable: ReasonCode != "execution_output_directory_unwritable",
                    freeSpaceAvailable: !ReasonCode.StartsWith("execution_disk_space", StringComparison.Ordinal),
                    availableFreeBytes: 0,
                    requiredFreeBytes: 1));
        }
    }

    private sealed class CapturingAuditLogger : AuditLogger
    {
        internal CapturingAuditLogger()
            : base(Path.Combine(Path.GetTempPath(), "AgentRecorder_246RR_" + Guid.NewGuid().ToString("N") + ".jsonl"))
        {
        }

        internal readonly System.Collections.Concurrent.ConcurrentQueue<string> Events = new();

        public override void Log(string evt, object payload) => Events.Enqueue(evt);
    }

    private sealed class TemporaryDatabase : IDisposable
    {
        private readonly string _directory = Path.Combine(Path.GetTempPath(), "AgentRecorderCoordinator_" + Guid.NewGuid().ToString("N"));

        internal TemporaryDatabase()
        {
            Directory.CreateDirectory(_directory);
            Store = new SqliteOperationalStore(Path.Combine(_directory, "state", "agent-recorder.db"));
            Store.Initialize();
            using var connection = Store.OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText = "UPDATE unattended_safety_state SET unattended_mode_code = 'enabled' WHERE state_id = 'global';";
            command.ExecuteNonQuery();
        }

        internal SqliteOperationalStore Store { get; }

        public void Dispose()
        {
            try { Directory.Delete(_directory, recursive: true); }
            catch (DirectoryNotFoundException) { }
            catch (IOException) { }
        }
    }
}
