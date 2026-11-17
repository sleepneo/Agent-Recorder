using System.Diagnostics;
using System.Reflection;
using System.Runtime.ExceptionServices;
using Xunit;

namespace AgentRecorder.Mcp.Tests;

public sealed class StaleOperationObservationTests : IDisposable
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
    private OperationEntry Entry(string state) => new()
    {
        SchemaVersion = 1, OperationId = id, DurationSeconds = 10, Binding = Binding,
        OwnerPid = Environment.ProcessId, OwnerStartedTicks = Process.GetCurrentProcess().StartTime.ToUniversalTime().Ticks,
        Status = state, ReasonCode = "submission_intent"
    };
    private OperationResult Local(OperationEntry stale)
    {
        var manager = new OperationManager(BindingStore, new RecorderTools(BindingStore));
        try
        {
            return (OperationResult)typeof(OperationManager).GetMethod("Local", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(manager, [stale])!;
        }
        catch (TargetInvocationException error) when (error.InnerException != null)
        { ExceptionDispatchInfo.Capture(error.InnerException).Throw(); throw; }
        finally { manager.DisposeAsync().AsTask().GetAwaiter().GetResult(); }
    }
    private OperationEntry PublishInitial(string state)
    {
        Directory.CreateDirectory(Store.Root); BindingStore.Write(Binding); Store.Write(Entry(state)); return Store.Read(id)!;
    }

    [Theory]
    [InlineData("accepted", "no_recording")]
    [InlineData("submitting", "no_recording")]
    [InlineData("accepted", "failed")]
    [InlineData("submitting", "cancelled")]
    [InlineData("submitting", "not_submitted")]
    [InlineData("submitting", "unknown")]
    public void PublishedTerminalBeforeLeaseRetirementSupersedesStaleSnapshot(string previous, string terminal)
    {
        var initial = PublishInitial(previous); var lease = Store.WorkerLease(id);
        var stale = Store.Read(id)!;
        Store.Write(initial with { Status = terminal, ReasonCode = "selection_timeout", Terminal = true });
        lease.Dispose();
        var observed = Local(stale);
        Assert.Equal(terminal, observed.Status); Assert.Equal("selection_timeout", observed.ReasonCode);
        Assert.Equal(id, observed.OperationId); Assert.Null(observed.RecordingId);
    }

    [Theory]
    [InlineData("accepted")]
    [InlineData("submitting")]
    public void LiveLeaseAndUnresolvedRetirementRemainDistinct(string state)
    {
        var initial = PublishInitial(state);
        using (var lease = Store.WorkerLease(id))
        { var active = Local(initial); Assert.Equal(state, active.Status); Assert.True(active.Ok); }
        var retired = Local(initial); Assert.Equal("unknown", retired.Status);
        Assert.Equal("operation_owner_retired_unknown", retired.ReasonCode);
        Assert.True(Store.HasEvidence(id)); Assert.Equal(state, Store.Read(id)!.Status);
    }

    [Theory]
    [InlineData("accepted")]
    [InlineData("submitting")]
    public void LaterNativeMappingIsPreservedWithoutGuessingOtherOperations(string state)
    {
        var initial = PublishInitial(state); var lease = Store.WorkerLease(id);
        var mapped = initial with { Status = "recording", RecordingId = "rec_123456abcdef", ConfirmationId = "confirm_123456abcdef" };
        Store.Write(mapped); lease.Dispose();
        var other = mapped with { OperationId = Guid.NewGuid().ToString("D"), RecordingId = "rec_abcdef123456" }; Store.Write(other);
        var result = Local(initial); Assert.Equal("recording", result.Status);
        Assert.Equal(id, result.OperationId); Assert.Equal(mapped.RecordingId, result.RecordingId);
        Assert.Equal(mapped.ConfirmationId, result.ConfirmationId);
    }

    [Theory]
    [InlineData("missing", "operation_record_missing")]
    [InlineData("corrupt", "operation_record_corrupt")]
    [InlineData("duration", "operation_conflict")]
    [InlineData("owner", "operation_conflict")]
    [InlineData("owner-generation", "operation_conflict")]
    [InlineData("binding", "operation_conflict")]
    [InlineData("current-binding", "operation_conflict")]
    [InlineData("host", "operation_host_changed")]
    public void UntrustedReplacementDoesNotBorrowOldIdentity(string change, string reason)
    {
        var initial = PublishInitial("submitting") with { Host = new(Environment.ProcessId, 1234, Guid.NewGuid().ToString("D"), 1, Binding.AppPath, "synthetic") };
        Store.Write(initial); using (Store.WorkerLease(id)) { }
        switch (change)
        {
            case "missing": File.Delete(Store.PathFor(id)); break;
            case "corrupt": File.WriteAllText(Store.PathFor(id), "{"); break;
            case "duration": Store.Write(initial with { DurationSeconds = 30 }); break;
            case "owner": Store.Write(initial with { OwnerPid = initial.OwnerPid + 1 }); break;
            case "owner-generation": Store.Write(initial with { OwnerStartedTicks = initial.OwnerStartedTicks + 1 }); break;
            case "binding": Store.Write(initial with { Binding = Binding with { DataDir = Path.Combine(root, "other-data") } }); break;
            case "current-binding": BindingStore.Write(Binding with { DataDir = Path.Combine(root, "other-data") }); break;
            case "host": Store.Write(initial with { Host = initial.Host with { InstanceId = Guid.NewGuid().ToString("D") } }); break;
        }
        Assert.Equal(reason, Assert.Throws<ContractException>(() => Local(initial)).Code);
    }

    [Fact]
    public async Task QueryRechecksPublishedTerminalAndReplayNeverStartsWork()
    {
        var initial = PublishInitial("submitting"); var lease = Store.WorkerLease(id); int reads = 0;
        await using var manager = new OperationManager(BindingStore, new RecorderTools(BindingStore))
        {
            AfterOperationRead = () =>
            {
                if (++reads != 1) return;
                Store.Write(initial with { Status = "no_recording", ReasonCode = "selection_timeout", Terminal = true }); lease.Dispose();
            }
        };
        var query = new Dictionary<string, System.Text.Json.JsonElement> { ["operation_id"] = System.Text.Json.JsonSerializer.SerializeToElement(id) };
        var result = await manager.CallAsync("recorder_operation_get", query, CancellationToken.None);
        Assert.Equal("no_recording", result.Status); Assert.Equal("selection_timeout", result.ReasonCode);
        var replay = await manager.CallAsync("recorder_record_region", query, CancellationToken.None);
        Assert.Equal("no_recording", replay.Status); Assert.Single(Store.Entries());
        // These synthetic package paths do not even exist: a hidden ensure/POST would fail.
        Assert.False(Directory.Exists(Binding.PackageRoot)); Assert.Equal(2, reads);
    }

    [Fact]
    public void RetiredObservationPreservesPreviouslyAllowedInitializationPromotion()
    {
        var initial = PublishInitial("accepted") with { Binding = Binding with
            { InitializationPolicy = "initialize_new", InitializationState = "pending", DatabaseIdentity = null } };
        BindingStore.Write(initial.Binding); Store.Write(initial);
        var lease = Store.WorkerLease(id);
        var initialized = initial.Binding with { InitializationState = "initialized", DatabaseIdentity = "new-synthetic-database" };
        BindingStore.Write(initialized);
        Store.Write(initial with { Binding = initialized, Status = "no_recording", ReasonCode = "selection_timeout", Terminal = true });
        lease.Dispose();
        var result = Local(initial); Assert.Equal("no_recording", result.Status); Assert.Equal("selection_timeout", result.ReasonCode);
    }

    public void Dispose()
    {
        var allowed = Path.GetFullPath(Path.Combine(Fixture.Repo, ".local-data", "task430")) + Path.DirectorySeparatorChar;
        Assert.StartsWith(allowed, Path.GetFullPath(root), StringComparison.OrdinalIgnoreCase);
        if (Directory.Exists(root)) Directory.Delete(root, true);
    }
}
