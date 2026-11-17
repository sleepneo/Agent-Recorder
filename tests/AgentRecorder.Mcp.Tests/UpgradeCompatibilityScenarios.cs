using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using Xunit;

namespace AgentRecorder.Mcp.Tests;

// Shared by ordinary source tests and an explicitly invoked historical-binary
// probe. No historical package path is a dependency of normal test discovery.
public static class UpgradeCompatibilityScenarios
{
    private const string Recording = "rec_123456abcdef";
    internal static string Reason(JsonElement result) => result.GetProperty("reason_code").GetString()!;
    internal static string Status(JsonElement result) => result.GetProperty("status").GetString()!;
    internal static int StopPosts(FakeApi api) => File.Exists(Path.Combine(api.Control, "stop-posts")) ? File.ReadAllLines(Path.Combine(api.Control, "stop-posts")).Length : 0;
    internal static Dictionary<string, object?> Query(string id) => new() { ["operation_id"] = id };
    internal static Dictionary<string, object?> Stop(string id) => new() { ["operation_id"] = id, ["recording_id"] = Recording };
    internal static string Hash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
    public static string CopyPlugin(Fixture fixture, string source, string label)
    {
        var destination = fixture.NewPath(label);
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var item = Path.Combine(destination, Path.GetRelativePath(source, file));
            Directory.CreateDirectory(Path.GetDirectoryName(item)!); File.Copy(file, item);
        }
        return destination;
    }
    private static string[] Inventory(string root) => Directory.EnumerateFileSystemEntries(root, "*", SearchOption.AllDirectories)
        .Select(path => Path.GetRelativePath(root, path)).Order(StringComparer.Ordinal).ToArray();
    internal static async Task<JsonElement> AwaitMapped(Session session, FakeApi api, string binding, string id)
    {
        var store = new OperationStore(new BindingStore(binding));
        OperationStore.ReadEvidence? firstFailure = null;
        try { await Fixture.Eventually(() => store.ReadObserved(id, failure => firstFailure ??= failure)?.RecordingId == Recording, 10000); }
        catch
        {
            var directory = Path.Combine(Fixture.Repo, ".local-data", "task434R", "upgrade-read-failure-" + Guid.NewGuid().ToString("N"));
            try
            {
                var path = OperationReadFailureEvidence.Save(directory, id, firstFailure,
                    new { process = session.ObserveReadFailureOwner(), stage = "upgrade_await_mapping",
                        read_evidence_scope = "rejected_await_mapping_read" });
                Console.WriteLine("First upgrade mapping read failure: " + path);
            }
            catch (Exception error) { Console.WriteLine("Evidence persistence failed: " + error.GetType().Name); }
            throw;
        }
        return await session.Call("recorder_operation_get", Query(id));
    }
    internal static async Task BindAndRecord(Fixture fixture, Session session, FakeApi api, string id)
    {
        fixture.Mode("reuse");
        Assert.Equal("binding_saved", Reason(await session.Call("recorder_binding_set", new() { ["package_root"] = fixture.Package, ["data_dir"] = api.Data })));
        Assert.Equal("accepted", Status(await session.Call("recorder_record_region", Query(id))));
    }

    // Frozen schema-1 declarations. Neither the binding nor operation envelope
    // is serialized from today's Binding/OperationEntry type.
    private static object FrozenBinding(Binding b) => new
    {
        SchemaVersion = 1, b.PackageRoot, b.AppPath, b.CliPath, b.DataDir, b.ProductVersion,
        b.InitializationPolicy, b.InitializationState, b.HostEnsureState, b.DataDirectoryIdentity, b.DatabaseIdentity
    };
    private static object? FrozenHost(HostStamp? h) => h == null ? null : new { h.Pid, h.Port, h.InstanceId, h.StartedTicks, h.Executable, h.UserSid };
    internal static void WriteFrozenSchemaOne(string binding, string id)
    {
        var store = new OperationStore(new BindingStore(binding)); var b = new BindingStore(binding).Read()!; var e = store.Read(id)!;
        var legacyBinding = FrozenBinding(b);
        var legacyOperation = new { SchemaVersion = 1, e.OperationId, e.DurationSeconds, Binding = FrozenBinding(e.Binding),
            e.OwnerPid, e.OwnerStartedTicks, e.Status, e.ReasonCode, Host = FrozenHost(e.Host), e.RecordingId,
            ConfirmationId = (string?)null, e.ProductStatus, e.ProductErrorCode, e.Terminal };
        var checksum = Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(legacyOperation, Json.Options)));
        File.WriteAllText(binding, JsonSerializer.Serialize(legacyBinding, Json.Options));
        File.WriteAllText(store.PathFor(id), JsonSerializer.Serialize(new { Entry = legacyOperation, Checksum = checksum }, Json.Options));
        Assert.Null(store.Read(id)!.ConfirmationId);
    }
    private static string Association(OperationEntry e) => Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new
    { e.OperationId, e.DurationSeconds, Binding = FrozenBinding(e.Binding), e.OwnerPid, e.OwnerStartedTicks,
        Host = FrozenHost(e.Host), e.RecordingId, e.ConfirmationId }, Json.Options)));

    public static async Task<Dictionary<string, object?>> RunAsync(Fixture fixture, string firstSource, string secondSource,
        string firstVersion, string secondVersion, string state, bool freezeSchema)
    {
        Assert.Contains(state, new[] { "completed", "unknown-submit", "stop-accepted", "stop-unknown" });
        var firstPlugin = CopyPlugin(fixture, firstSource, "first plugin with spaces");
        var secondPlugin = CopyPlugin(fixture, secondSource, "second independent plugin");
        var firstInventory = Inventory(firstPlugin); var secondInventory = Inventory(secondPlugin);
        var oldCwd = fixture.NewPath("unrelated caller A"); var newCwd = fixture.NewPath("unrelated caller B");
        var binding = fixture.NewPath("stable-registry/binding.json"); var id = Guid.NewGuid().ToString("D");
        await using var api = await FakeApi.Start(fixture);
        api.Set("selected"); api.Set("approved"); api.Set("state", "recording");
        if (state == "unknown-submit") api.Set("post-mode", "drop");
        var dataIdentity = FileIdentity.Read(api.Data); var database = Path.Combine(api.Data, "state", "agent-recorder.db");
        var dbIdentity = FileIdentity.Read(database); var dbHash = Hash(database);
        var store = new OperationStore(new BindingStore(binding)); var stops = new StopStore(store);
        var facts = new Dictionary<string, object?> { ["case"] = state, ["frozen_schema"] = freezeSchema,
            ["first_version"] = firstVersion, ["second_version"] = secondVersion, ["operation_id"] = id,
            ["first_plugin"] = firstPlugin, ["second_plugin"] = secondPlugin, ["first_cwd"] = oldCwd, ["second_cwd"] = newCwd };
        using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(40));
        var firstLaunch = PluginStdioLaunch.Read(firstPlugin); var secondLaunch = PluginStdioLaunch.Read(secondPlugin);
        await using (var first = await fixture.Connect(binding, firstLaunch.Command, oldCwd))
        {
            facts["first_pid"] = first.Pid;
            using var process = Process.GetProcessById(first.Pid); _ = process.Handle;
            facts["first_started_ticks"] = process.StartTime.ToUniversalTime().Ticks;
            Assert.Equal(firstVersion, first.Client.ServerInfo!.Version);
            Assert.Equal(firstVersion == "0.2.1" ? 5 : 6, (await first.Client.ListToolsAsync(cancellationToken: budget.Token)).Count);
            await BindAndRecord(fixture, first, api, id);
            if (state == "unknown-submit") await Fixture.Eventually(() => store.Read(id)?.Status == "unknown", 10000);
            else
            {
                await AwaitMapped(first, api, binding, id);
                if (state.StartsWith("stop-"))
                {
                    if (state == "stop-unknown") api.Set("stop-mode", "drop");
                    Assert.Equal(state == "stop-accepted" ? "accepted" : "unknown", Status(await first.Call("recorder_recording_stop", Stop(id), budget.Token)));
                }
                else
                {
                    api.Set("state", "completed"); Assert.Equal("publication_confirmed", Reason(await first.Call("recorder_operation_get", Query(id), budget.Token)));
                }
            }
            await first.Eof(); Assert.True(process.HasExited); facts["first_normal_exit_code"] = process.ExitCode;
        }
        if (freezeSchema) WriteFrozenSchemaOne(binding, id);
        var before = store.Read(id)!; var association = Association(before); var bindingHash = Hash(binding);
        var workerMarker = Path.Combine(store.Root, id + ".worker"); var markerHash = Hash(workerMarker);
        var stopHash = state.StartsWith("stop-") ? Hash(stops.PathFor(id)) : null;
        var stopMarker = Path.Combine(stops.Root, id + ".intent"); var stopMarkerHash = state.StartsWith("stop-") ? Hash(stopMarker) : null;
        var posts = api.Posts; var stopPosts = StopPosts(api); var routesBefore = api.Routes.Length;
        await using (var second = await fixture.Connect(binding, secondLaunch.Command, newCwd))
        {
            facts["second_pid"] = second.Pid;
            using var process = Process.GetProcessById(second.Pid); _ = process.Handle;
            facts["second_started_ticks"] = process.StartTime.ToUniversalTime().Ticks;
            Assert.NotEqual((facts["first_pid"], facts["first_started_ticks"]), (facts["second_pid"], facts["second_started_ticks"]));
            Assert.Equal(secondVersion, second.Client.ServerInfo!.Version);
            var valid = await second.Call("recorder_binding_get", ct: budget.Token);
            Assert.Equal("binding_valid", Reason(valid));
            Assert.Equal(fixture.Package, valid.GetProperty("binding").GetProperty("package_root").GetString());
            Assert.Equal(api.Data, valid.GetProperty("binding").GetProperty("data_dir").GetString());
            var result = await second.Call("recorder_operation_get", Query(id), budget.Token);
            if (state == "unknown-submit")
            {
                Assert.Equal("unknown", Status(result)); Assert.Null(result.GetProperty("recording_id").GetString());
                Assert.Equal("unknown", Status(await second.Call("recorder_record_region", Query(id), budget.Token)));
                Assert.Equal("operation_in_progress", Reason(await second.Call("recorder_record_region", Query(Guid.NewGuid().ToString("D")), budget.Token)));
                Assert.Equal(routesBefore, api.Routes.Length); // No native ID means no guessed HTTP lookup.
            }
            else
            {
                Assert.Equal(Recording, result.GetProperty("recording_id").GetString());
                if (state.StartsWith("stop-"))
                {
                    Assert.Equal("stopping", Status(result));
                    Assert.Equal(state == "stop-accepted" ? "accepted" : "unknown", Status(await second.Call("recorder_recording_stop", Stop(id), budget.Token)));
                    api.Set("state", "completed"); api.Set("output-duration", "3");
                    result = await second.Call("recorder_operation_get", Query(id), budget.Token);
                }
                Assert.Equal("publication_confirmed", Reason(result));
                Assert.Equal("publication_confirmed", Reason(await second.Call("recorder_record_region", Query(id), budget.Token)));
                if (freezeSchema) Assert.Null(result.GetProperty("confirmation_id").GetString());
                Assert.All(api.Routes.Skip(routesBefore).Where(route => route.Contains("/recordings/")), route => Assert.Contains(Recording, route));
            }
            await second.Eof(); Assert.True(process.HasExited); facts["second_normal_exit_code"] = process.ExitCode;
        }
        Assert.Equal(association, Association(store.Read(id)!)); Assert.Equal(bindingHash, Hash(binding));
        Assert.Equal(markerHash, Hash(workerMarker));
        if (stopHash != null) { Assert.Equal(stopHash, Hash(stops.PathFor(id))); Assert.Equal(stopMarkerHash, Hash(stopMarker)); }
        Assert.Single(store.Entries()); Assert.Equal(posts, api.Posts); Assert.Equal(1, posts); Assert.Equal(stopPosts, StopPosts(api));
        Assert.Single(File.ReadAllLines(Path.Combine(api.Data, "runtime", "cli-arguments.jsonl")));
        Assert.Equal(dataIdentity, FileIdentity.Read(api.Data)); Assert.Equal(dbIdentity, FileIdentity.Read(database)); Assert.Equal(dbHash, Hash(database));
        Assert.Equal(firstInventory, Inventory(firstPlugin)); Assert.Equal(secondInventory, Inventory(secondPlugin));
        Assert.Empty(Directory.EnumerateFileSystemEntries(oldCwd)); Assert.Empty(Directory.EnumerateFileSystemEntries(newCwd));
        Assert.Empty(Directory.EnumerateFiles(api.Data, "*.mp4", SearchOption.AllDirectories));
        facts["recording_id"] = before.RecordingId; facts["create_posts_before_after"] = new[] { posts, api.Posts };
        facts["stop_posts_before_after"] = new[] { stopPosts, StopPosts(api) }; facts["association_and_markers_unchanged"] = true;
        facts["binding_paths_and_directory_database_identity_unchanged"] = true; facts["plugin_and_cwd_inventories_unchanged"] = true;
        facts["result"] = "isolated_upgrade_verified";
        return facts;
    }
}
