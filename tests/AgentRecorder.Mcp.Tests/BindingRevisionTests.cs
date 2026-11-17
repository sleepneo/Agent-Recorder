using System.Text.Json;
using Xunit;
using Xunit.Abstractions;

namespace AgentRecorder.Mcp.Tests;

public sealed class BindingRevisionTests(Fixture fixture, ITestOutputHelper output) : IClassFixture<Fixture>
{
    private Dictionary<string, object?> Bind(string data, bool initialize = true, string? package = null) => new()
    { ["package_root"] = package ?? fixture.Package, ["data_dir"] = data, ["initialize_new_data_directory"] = initialize };

    private static void AssertResult(JsonElement result, bool ok, string status, string reason)
    {
        Assert.Equal(ok, result.GetProperty("ok").GetBoolean());
        Assert.Equal(status, result.GetProperty("status").GetString());
        Assert.Equal(reason, result.GetProperty("reason_code").GetString());
    }

    private static string[] Entries(string root) => Directory.Exists(root)
        ? Directory.GetFileSystemEntries(root, "*", SearchOption.AllDirectories).Order(StringComparer.OrdinalIgnoreCase).ToArray()
        : [];

    [Fact]
    public void DefaultBindingResolutionIsSiblingAndDoesNotUseRealLocalAppData()
    {
        var simulatedLocal = fixture.NewPath("LocalAppData");
        var binding = BindingStore.DefaultFilename(simulatedLocal);
        var data = Path.Combine(simulatedLocal, "AgentRecorder");
        Assert.Equal(Path.Combine(simulatedLocal, "AgentRecorderIntegrations", "binding.json"), binding);
        Assert.False(LocalPaths.Contains(data, Path.GetDirectoryName(binding)!));
        Assert.False(LocalPaths.Contains(data, data + "Integrations"));
        Assert.True(LocalPaths.Contains(data.ToUpperInvariant(), data + "\\Integrations"));
        Assert.True(LocalPaths.Contains(Path.GetPathRoot(simulatedLocal)!, Path.GetDirectoryName(binding)!));
        Assert.False(Directory.Exists(simulatedLocal));
    }

    [Fact]
    public async Task SiblingBindingAllowsNewCommonDataRootAndPendingReadStaysClean()
    {
        var simulatedLocal = fixture.NewPath("LocalAppData");
        var data = Path.Combine(simulatedLocal, "AgentRecorder");
        var binding = BindingStore.DefaultFilename(simulatedLocal);
        await using var session = await fixture.Connect(binding);
        Assert.False(Directory.Exists(simulatedLocal));
        var saved = await session.Call("recorder_binding_set", Bind(data));
        AssertResult(saved, true, "valid", "binding_saved_pending_initialization");
        var before = File.ReadAllBytes(binding);
        AssertResult(await session.Call("recorder_binding_get"), true, "valid", "binding_valid");
        Assert.Equal("pending", saved.GetProperty("binding").GetProperty("initialization_state").GetString());
        Assert.Equal(before, File.ReadAllBytes(binding));
        Assert.Empty(Entries(data));
        Assert.False(File.Exists(Path.Combine(simulatedLocal, "AgentRecorder", "Integrations", "binding.json")));
        output.WriteLine("sibling_layout: saved=valid/pending; get=valid; data_entries=0; legacy_binding_created=false");
    }

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(false, true, false)]
    [InlineData(false, false, true)]
    [InlineData(true, false, false)]
    [InlineData(true, true, true)]
    public async Task ConflictingOverrideIsRejectedBeforeCreatingAnything(bool insidePackage, bool equalParent, bool changeCase)
    {
        var data = fixture.NewPath("fresh-data");
        var root = insidePackage ? fixture.Package : data;
        var binding = Path.Combine(root, equalParent ? "" : "Integrations", "binding.json");
        var packageBefore = Entries(fixture.Package);
        await using var session = await fixture.Connect(binding);
        Assert.False(Directory.Exists(data));
        var dataInput = changeCase ? data.ToUpperInvariant().Replace('\\', '/') : data;
        var packageInput = changeCase ? fixture.Package.ToUpperInvariant().Replace('\\', '/') : fixture.Package;
        AssertResult(await session.Call("recorder_binding_set", Bind(dataInput, package: packageInput)),
            false, "invalid", "binding_location_conflict");
        Assert.False(Directory.Exists(data));
        Assert.False(File.Exists(binding));
        Assert.False(File.Exists(binding + ".lock"));
        if (!insidePackage) Assert.False(Directory.Exists(Path.GetDirectoryName(binding)));
        Assert.Equal(packageBefore, Entries(fixture.Package));
        output.WriteLine($"conflicting_override: package={insidePackage}; equal={equalParent}; case_variant={changeCase}; status=invalid; reason=binding_location_conflict; data_exists_after=false; package_unchanged=true");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ConflictingRebindPreservesBytesAndDoesNotAcquireExistingLock(bool insidePackage)
    {
        // A currently valid binding is outside its old package/data, but inside a proposed
        // new root. The conflict must win before lock contention or package validation.
        var candidate = fixture.NewPath("proposed-root");
        var binding = Path.Combine(candidate, "Integrations", "binding.json");
        var oldData = fixture.NewPath("old-data"); Fixture.ExistingData(oldData);
        await using var session = await fixture.Connect(binding);
        AssertResult(await session.Call("recorder_binding_set", Bind(oldData, false)), true, "valid", "binding_saved");
        var before = File.ReadAllBytes(binding);
        var entries = Entries(candidate);
        var newData = insidePackage ? fixture.NewPath("new-data") : candidate;
        using (var held = new FileStream(binding + ".lock", FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            AssertResult(await session.Call("recorder_binding_set", Bind(newData, package: insidePackage ? candidate : null)),
                false, "invalid", "binding_location_conflict");
        }
        Assert.Equal(before, File.ReadAllBytes(binding));
        Assert.Equal(entries, Entries(candidate));
        if (insidePackage) Assert.False(Directory.Exists(newData));
        AssertResult(await session.Call("recorder_binding_get"), true, "valid", "binding_valid");
    }

    [Theory]
    [InlineData(false, "host_start_unknown")]
    [InlineData(true, "initialization_unknown")]
    public async Task DurableUnknownKeepsStatusEvidenceAndSingleSubmission(bool initialize, string reason)
    {
        var binding = fixture.NewPath("binding.json");
        var data = fixture.NewPath("data");
        if (!initialize) Fixture.ExistingData(data);
        fixture.Mode("bad-api");
        await using var session = await fixture.Connect(binding);
        await session.Call("recorder_binding_set", Bind(data, initialize));
        var first = await session.Call("recorder_host_ensure");
        AssertResult(first, false, "unknown", "cli_evidence_invalid_unknown");
        var persisted = File.ReadAllBytes(binding);
        for (var attempt = 0; attempt < 3; attempt++)
        {
            foreach (var tool in new[] { "recorder_binding_get", "recorder_host_ensure" })
            {
                var result = await session.Call(tool);
                AssertResult(result, false, "unknown", reason);
                Assert.Equal(first.GetProperty("binding").GetRawText(), result.GetProperty("binding").GetRawText());
                Assert.Equal(persisted, File.ReadAllBytes(binding));
                Assert.Single(File.ReadAllLines(Path.Combine(data, "runtime", "cli-arguments.jsonl")));
            }
        }
        output.WriteLine($"durable_unknown: initialize={initialize}; first_status=unknown; get_status=unknown; ensure_status=unknown; reason={reason}; submissions=1; persisted_bytes_unchanged=true");
    }

    [Fact]
    public async Task KnownInvalidAndUnconfiguredStatesRemainDistinct()
    {
        var binding = fixture.NewPath("binding.json");
        await using var session = await fixture.Connect(binding);
        AssertResult(await session.Call("recorder_binding_get"), true, "unconfigured", "binding_unconfigured");
        AssertResult(await session.Call("recorder_host_ensure"), false, "unconfigured", "binding_unconfigured");
        AssertResult(await session.Call("recorder_binding_set", Bind("relative")), false, "invalid", "path_unsupported");
        Directory.CreateDirectory(Path.GetDirectoryName(binding)!);
        File.WriteAllText(binding, "{");
        AssertResult(await session.Call("recorder_binding_get"), false, "invalid", "binding_corrupt");
        AssertResult(await session.Call("recorder_host_ensure"), false, "invalid", "binding_corrupt");
    }
}
