using System.Diagnostics;
using Xunit;
using Xunit.Abstractions;

namespace AgentRecorder.Mcp.Tests;

public sealed class PublicationReadConsistencyTests(ITestOutputHelper output) : IDisposable
{
    private readonly string root = Path.Combine(Fixture.Repo, ".local-data", "task430", "tests-" + Guid.NewGuid().ToString("N"));
    private readonly string id = Guid.NewGuid().ToString("D");
    private BindingStore BindingStore => new(Path.Combine(root, "binding.json"));
    private OperationStore Store => new(BindingStore);
    private Binding Binding => new()
    {
        SchemaVersion = 1, PackageRoot = Path.Combine(root, "package"), AppPath = Path.Combine(root, "package", "app.exe"),
        CliPath = Path.Combine(root, "package", "cli.exe"), DataDir = Path.Combine(root, "data"), ProductVersion = "0.1.25",
        InitializationPolicy = "existing", InitializationState = "initialized", HostEnsureState = "idle",
        DataDirectoryIdentity = "synthetic-directory", DatabaseIdentity = "synthetic-database"
    };
    private OperationEntry Entry(string state = "submitting") => new()
    {
        SchemaVersion = 1, OperationId = id, DurationSeconds = 10, Binding = Binding, OwnerPid = Environment.ProcessId,
        OwnerStartedTicks = Process.GetCurrentProcess().StartTime.ToUniversalTime().Ticks, Status = state, ReasonCode = "submission_intent"
    };
    private OperationStore Prepare()
    { var store = Store; Directory.CreateDirectory(store.Root); BindingStore.Write(Binding); store.Write(Entry()); return store; }

    [Fact]
    public async Task InjectedOpenNotFoundWaitsForActualPublisherAndReadsCommittedGeneration()
    {
        var store = Prepare(); var next = Entry("no_recording") with { Terminal = true, ReasonCode = "selection_timeout" };
        var injected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); int attempts = 0;
        using var gate = await store.LockAsync(CancellationToken.None);
        // Fault injection of the observed OS open error; no assertion that
        // File.Replace itself necessarily performs an observable unlink.
        var read = Task.Run(() => store.ReadObserved(id, null, beforeOpen: () =>
        {
            if (Interlocked.Increment(ref attempts) != 1) return;
            Assert.True(File.Exists(store.PathFor(id))); injected.SetResult(); throw new FileNotFoundException("synthetic open window");
        }));
        try { await injected.Task.WaitAsync(TimeSpan.FromSeconds(3)); store.Write(next); }
        finally { gate.Dispose(); }
        Assert.Equal(next, await read.WaitAsync(TimeSpan.FromSeconds(3))); Assert.Equal(2, attempts);
    }

    [Fact]
    public async Task InitialPublicationWithWorkerWaitsForCommitInsteadOfDeclaringMissing()
    {
        var waiting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var store = new OperationStore(BindingStore) { BeforePublicationRecheck = () => waiting.TrySetResult() };
        Directory.CreateDirectory(store.Root); using var lease = store.WorkerLease(id);
        using var gate = await store.LockAsync(CancellationToken.None);
        var read = Task.Run(() => store.Read(id));
        try { await waiting.Task.WaitAsync(TimeSpan.FromSeconds(3)); store.Write(Entry()); }
        finally { gate.Dispose(); }
        Assert.Equal(Entry(), await read.WaitAsync(TimeSpan.FromSeconds(3)));
    }

    [Fact]
    public async Task PermanentMissingWorkerBodyRemainsUnknownAndReplayDoesNotRepair()
    {
        var store = Prepare(); using (store.WorkerLease(id)) { } File.Delete(store.PathFor(id));
        Assert.Equal("operation_record_missing", Assert.Throws<ContractException>(() => store.Read(id)).Code);
        await using var manager = new OperationManager(BindingStore, new RecorderTools(BindingStore));
        var args = new Dictionary<string, System.Text.Json.JsonElement> { ["operation_id"] = System.Text.Json.JsonSerializer.SerializeToElement(id) };
        foreach (var tool in new[] { "recorder_operation_get", "recorder_record_region" })
        {
            var result = await manager.CallAsync(tool, args, CancellationToken.None);
            Assert.Equal("unknown", result.Status); Assert.Equal("operation_record_missing", result.ReasonCode);
        }
        Assert.True(store.HasEvidence(id)); Assert.False(File.Exists(store.PathFor(id)));
        Assert.False(Directory.Exists(Binding.PackageRoot));
    }

    [Fact]
    public void NeverPublishedObservationDoesNotCreateState()
    { Assert.Null(Store.Read(id)); Assert.False(Directory.Exists(root)); }

    [Fact]
    public void LosingObservedMarkerDuringCoordinationCannotTurnIntoNotFound()
    {
        var store = Prepare(); using (store.WorkerLease(id)) { } File.Delete(store.PathFor(id));
        // Deliberate external evidence removal, not a normal publisher action.
        var observing = new OperationStore(BindingStore) { BeforePublicationRecheck = () => File.Delete(Path.Combine(store.Root, id + ".worker")) };
        Assert.Equal("operation_record_unavailable", Assert.Throws<ContractException>(() => observing.Read(id)).Code);
        Assert.False(File.Exists(store.PathFor(id)));
    }

    [Fact]
    public async Task AlreadyHeldGateReadsAndMissingChecksNeverReenter()
    {
        var store = new OperationStore(BindingStore) { BeforePublicationRecheck = () => Assert.Fail("Gate was reentered") };
        Directory.CreateDirectory(store.Root); using var gate = await store.LockAsync(CancellationToken.None);
        store.Write(Entry()); Assert.Equal(Entry(), store.ReadUnderGate(id)); Assert.Single(store.EntriesUnderGate());
        using (store.WorkerLease(id)) { } File.Delete(store.PathFor(id));
        Assert.Equal("operation_record_missing", Assert.Throws<ContractException>(() => store.ReadUnderGate(id)).Code);
        Assert.Equal("operation_record_missing", Assert.Throws<ContractException>(() => store.EntriesUnderGate().ToArray()).Code);
    }

    [Fact]
    public async Task PublisherFailurePreservesOldBodyAndRetiredOwnerStaysUnknown()
    {
        var store = Prepare(); var failing = new OperationStore(BindingStore) { BeforePublish = () => throw new IOException("synthetic pre-publish failure") };
        using (var lease = store.WorkerLease(id))
        using (var gate = await store.LockAsync(CancellationToken.None))
            Assert.Throws<IOException>(() => failing.Write(Entry("no_recording") with { Terminal = true }));
        Assert.Equal(Entry(), store.Read(id)); Assert.Empty(Directory.GetFiles(store.Root, "*.tmp"));
        await using var manager = new OperationManager(BindingStore, new RecorderTools(BindingStore));
        var result = await manager.CallAsync("recorder_record_region", new Dictionary<string, System.Text.Json.JsonElement>
            { ["operation_id"] = System.Text.Json.JsonSerializer.SerializeToElement(id) }, CancellationToken.None);
        Assert.Equal("operation_owner_retired_unknown", result.ReasonCode); Assert.Single(store.Entries());
    }

    [Fact]
    public async Task ContendedRecheckExhaustsExistingGateBudgetWithoutCallingItCorruption()
    {
        var prepared = Prepare(); int rechecks = 0, attempts = 0; OperationStore.ReadEvidence? failure = null;
        var store = new OperationStore(BindingStore) { BeforePublicationRecheck = () => rechecks++ };
        using var held = await store.LockAsync(CancellationToken.None);
        var error = Assert.Throws<ContractException>(() => store.ReadObserved(id, read => failure = read,
            beforeOpen: () => { attempts++; throw new FileNotFoundException("synthetic open window"); }));
        Assert.Equal("operation_record_unavailable", error.Code); Assert.Equal(1, rechecks); Assert.Equal(1, attempts);
        Assert.Equal("coordination", failure!.Stage); Assert.Null(failure.Snapshot); Assert.Equal(Entry(), store.ReadUnderGate(id));
    }

    [Theory]
    [InlineData("json")]
    [InlineData("checksum")]
    public void CoordinatedRecheckStillRejectsInvalidBodyWithoutAnotherAttempt(string kind)
    {
        var store = Prepare(); int attempts = 0; OperationStore.ReadEvidence? evidence = null;
        var error = Assert.Throws<ContractException>(() => store.ReadObserved(id, read => evidence = read, beforeOpen: () =>
        {
            if (++attempts != 1) return;
            var path = store.PathFor(id);
            File.WriteAllText(path, kind == "json" ? "{" : File.ReadAllText(path).Replace("submission_intent", "changed_reason"));
            throw new FileNotFoundException("synthetic first-open failure");
        }));
        Assert.Equal("operation_record_corrupt", error.Code); Assert.Equal(2, attempts); Assert.Equal(kind, evidence!.Stage);
        Assert.NotNull(evidence.Snapshot); Assert.True(evidence.SnapshotComplete);
    }

    [Fact]
    public void NonMissingOpenFailureIsUnavailableWithoutRetryOrSnapshot()
    {
        var store = Prepare(); int attempts = 0; OperationStore.ReadEvidence? evidence = null;
        var error = Assert.Throws<ContractException>(() => store.ReadObserved(id, read => evidence = read,
            beforeOpen: () => { attempts++; throw new UnauthorizedAccessException("synthetic denied open"); }));
        Assert.Equal("operation_record_unavailable", error.Code); Assert.Equal(1, attempts);
        Assert.Equal("UnauthorizedAccessException", evidence!.ExceptionType); Assert.Equal("open", evidence.Stage); Assert.Null(evidence.Snapshot);
    }

    [Fact]
    public async Task OriginalUnavailableExceptionIsCapturedInsideActualPrivateDispatch()
    {
        var store = Prepare(); using var lease = store.WorkerLease(id); OperationManager.CallFailure? captured = null;
        await using var manager = new OperationManager(BindingStore, new RecorderTools(BindingStore)) { FailureObserver = failure => captured ??= failure };
        using var denied = new FileStream(BindingStore.Filename, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        var result = await manager.CallAsync("recorder_operation_get", new Dictionary<string, System.Text.Json.JsonElement>
            { ["operation_id"] = System.Text.Json.JsonSerializer.SerializeToElement(id) }, CancellationToken.None);
        Assert.Equal("operation_store_unavailable", result.ReasonCode); Assert.Equal("unknown", result.Status);
        Assert.Equal("IOException", captured!.ExceptionType); Assert.Equal(32, captured.HResult & 0xffff);
        Assert.Equal("operation_dispatch", captured.Stage); Assert.Contains(captured.Frames, frame => frame.Contains("BindingStore.Read"));
        Assert.DoesNotContain(System.Text.Json.JsonSerializer.Serialize(result), "IOException");
        var evidence = OperationReadFailureEvidence.Save(Path.Combine(Fixture.Repo, ".local-data", "task434R", "dispatch-failure-" + Guid.NewGuid().ToString("N")),
            id, null, new { scope = "private_in_process_injected_share_denial_not_historical_cause", original_error = captured,
                actual_response = result, process = AgentRecorder.LifecycleTests.LifecycleObservation.Self("operation_dispatch_failure") });
        output.WriteLine("Original dispatch exception evidence: " + evidence);
    }

    public void Dispose()
    {
        Assert.StartsWith(Path.GetFullPath(Path.Combine(Fixture.Repo, ".local-data", "task430")) + Path.DirectorySeparatorChar,
            Path.GetFullPath(root), StringComparison.OrdinalIgnoreCase);
        if (Directory.Exists(root)) Directory.Delete(root, true);
    }
}
