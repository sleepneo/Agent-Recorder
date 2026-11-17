using System.Text.Json;
using Xunit;

namespace AgentRecorder.Mcp.Tests;

// Explicit diagnostic entry, never a Fact with conditional Skip/return. Normal
// source tests need neither historical files nor an installed plugin.
public static class HistoricalUpgradeProbe
{
    public static async Task<int> RunAsync(string oldPlugin, string currentPlugin, string evidenceDirectory)
    {
        var evidence = LocalPaths.Normalize(evidenceDirectory); var parent = Directory.GetParent(evidence)!;
        var allowed = Path.Combine(Fixture.Repo, ".local-data", "task430");
        if (!string.Equals(parent.Parent?.FullName, allowed, StringComparison.OrdinalIgnoreCase) ||
            !parent.Name.StartsWith("task434-", StringComparison.Ordinal) || !Guid.TryParseExact(parent.Name[8..], "N", out _) ||
            Directory.Exists(evidence) || File.Exists(evidence)) throw new InvalidOperationException("exclusive_task434_evidence_directory_required");
        Directory.CreateDirectory(evidence);
        var path = Path.Combine(evidence, "historical-upgrade.json");
        var facts = new Dictionary<string, object?> { ["schema_version"] = 1, ["scope"] = "isolated_historical_binary_upgrade",
            ["result"] = "running", ["started_at_utc"] = DateTimeOffset.UtcNow, ["no_real_binding_or_host_access"] = true };
        Fixture? fixture = null;
        try
        {
            facts["old_candidate"] = Validate(oldPlugin, "0.2.1", "0E88E8CFFA4CFFA1619C0A4452538F68AC148DD2DF58675EABE4DEBBE6F324C8", "E5A2CE2855D9C89DFFA93EE59B9F096F89555E792E27AE1532FD635CCE72E6DA");
            using var currentManifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(LocalPaths.Normalize(currentPlugin), "plugin.json")));
            var currentVersion = currentManifest.RootElement.GetProperty("version").GetString();
            facts["current_candidate"] = currentVersion switch
            {
                "0.3.0" => Validate(currentPlugin, "0.3.0", "EDB2B5FC29014D9E8CE4D2938B7DD92E2431CFAB6AA32784BD04FCC1D5AB0A09", "5DFBF982C22E4A0A01ADE9F5954EF4611E1FB0B21F19AA92ED38864B50958939"),
                "0.3.1" => Validate(currentPlugin, "0.3.1", "177994F104574C4365DD9307AFB21C2D0EF6AF4CAE27EE568F854695C4D4D868", "8FCE5C5BBC71205A620E12F8F747D254DB650B963785502D47CEB5EA5808445C"),
                "0.3.2" => Validate(currentPlugin, "0.3.2", "E65700A4F59C402A4F6F996FD73B971E8553B4705C410BF59E02999DC016962F", "FC5E841B1DBBCE635694237F48E21D8107DF9321A9776D00ECCFAC9AA4369110"),
                _ => throw new InvalidOperationException("unsupported_historical_upgrade_pair")
            };
            fixture = new Fixture(); facts["private_fixture_root"] = fixture.Root;
            await fixture.InitializeAsync();
            var cases = new List<Dictionary<string, object?>>(); facts["cases"] = cases;
            foreach (var state in new[] { "completed", "unknown-submit" })
                cases.Add(await UpgradeCompatibilityScenarios.RunAsync(fixture, oldPlugin, currentPlugin, "0.2.1", currentVersion!, state, freezeSchema: false));
            facts["result"] = currentVersion switch
            {
                "0.3.0" => "historical_0_2_1_to_0_3_0_verified", "0.3.1" => "historical_0_2_1_to_0_3_1_verified",
                "0.3.2" => "historical_0_2_1_to_0_3_2_verified", _ => throw new InvalidOperationException("unsupported_pair")
            };
            return 0;
        }
        catch (Exception error)
        {
            facts["result"] = fixture == null ? "blocked_historical_candidate_missing_or_mismatched" : "isolated_upgrade_failed";
            facts["reason_code"] = error.GetType().Name;
            return 1;
        }
        finally
        {
            try { if (fixture != null) { await fixture.DisposeAsync(); facts["private_fixture_normal_cleanup"] = true; } }
            finally
            {
                facts["finished_at_utc"] = DateTimeOffset.UtcNow;
                var bytes = JsonSerializer.SerializeToUtf8Bytes(facts, Json.Options);
                if (bytes.Length > 65536) throw new InvalidOperationException("diagnostic_evidence_limit");
                File.WriteAllBytes(path, bytes); Console.WriteLine("Historical upgrade evidence: " + path);
            }
        }
    }
    private static object Validate(string root, string version, string dll, string manifest)
    {
        root = LocalPaths.Normalize(root);
        using var metadata = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "plugin.json")));
        Assert.Equal("agent-recorder-bootstrap", metadata.RootElement.GetProperty("name").GetString());
        Assert.Equal(version, metadata.RootElement.GetProperty("version").GetString());
        Assert.Equal(dll, UpgradeCompatibilityScenarios.Hash(Path.Combine(root, "server", "AgentRecorder.Mcp.dll")));
        Assert.Equal(manifest, UpgradeCompatibilityScenarios.Hash(Path.Combine(root, "plugin.json")));
        using var deps = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "server", "AgentRecorder.Mcp.deps.json")));
        Assert.True(deps.RootElement.GetProperty("libraries").TryGetProperty("ModelContextProtocol.Core/2.4.0", out _));
        _ = PluginStdioLaunch.Read(root);
        return new { source = root, version, dll_sha256 = dll, manifest_sha256 = manifest, sdk = "2.4.0" };
    }
}
