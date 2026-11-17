using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using Xunit;
using Xunit.Abstractions;

namespace AgentRecorder.Mcp.Tests;

public sealed class UpgradeCompatibilityTests(Fixture fixture, ITestOutputHelper output) : IClassFixture<Fixture>
{
    [Theory]
    [InlineData("completed")]
    [InlineData("unknown-submit")]
    [InlineData("stop-accepted")]
    [InlineData("stop-unknown")]
    public async Task FrozenSchemaUpgradeAcrossPluginAndCwdPreservesTargetsAndNeverReplays(string state)
    {
        var source = Environment.GetEnvironmentVariable("AGENT_RECORDER_MCP_TEST_PLUGIN_ROOT") ?? fixture.Plugin;
        var facts = await UpgradeCompatibilityScenarios.RunAsync(fixture, source, source, "0.3.2", "0.3.2", state, freezeSchema: true);
        output.WriteLine(JsonSerializer.Serialize(facts));
    }

    [Theory]
    [InlineData("missing-program", "package_dependency_missing")]
    [InlineData("replaced-program", "package_identity_mismatch")]
    [InlineData("data-directory", "data_directory_replaced")]
    [InlineData("database", "initialized_data_replaced")]
    [InlineData("capability", "capabilities_identity_unverified")]
    [InlineData("capability-version", "capabilities_identity_unverified")]
    [InlineData("package-version", "product_version_unsupported")]
    [InlineData("host-instance", "operation_host_changed")]
    public async Task UpgradeRejectsIncompatibleSyntheticStateWithoutRebindingOrNewRequests(string change, string expected)
    {
        var source = Environment.GetEnvironmentVariable("AGENT_RECORDER_MCP_TEST_PLUGIN_ROOT") ?? fixture.Plugin;
        var firstPlugin = UpgradeCompatibilityScenarios.CopyPlugin(fixture, source, "prior plugin");
        var secondPlugin = UpgradeCompatibilityScenarios.CopyPlugin(fixture, source, "upgraded plugin");
        var binding = fixture.NewPath("stable/binding.json"); var id = Guid.NewGuid().ToString("D");
        await using var api = await FakeApi.Start(fixture); api.Set("selected"); api.Set("approved"); api.Set("state", "completed");
        await using (var first = await fixture.Connect(binding, PluginStdioLaunch.Read(firstPlugin).Command))
        {
            await UpgradeCompatibilityScenarios.BindAndRecord(fixture, first, api, id);
            Assert.Equal("publication_confirmed", UpgradeCompatibilityScenarios.Reason(await UpgradeCompatibilityScenarios.AwaitMapped(first, api, binding, id)));
        }
        UpgradeCompatibilityScenarios.WriteFrozenSchemaOne(binding, id);
        var bindingBytes = File.ReadAllBytes(binding); var dataIdentity = FileIdentity.Read(api.Data);
        var cliExe = Path.Combine(fixture.Package, "AgentRecorder.Cli", "AgentRecorder.Cli.exe"); var cliDll = Path.ChangeExtension(cliExe, ".dll");
        var savedExe = File.ReadAllBytes(cliExe); var savedDll = File.ReadAllBytes(cliDll);
        try
        {
            switch (change)
            {
                case "missing-program": File.Delete(cliExe); break;
                case "replaced-program": File.Copy(Path.Combine(fixture.Package, "AgentRecorder.App", "AgentRecorder.App.dll"), cliDll, true); break;
                case "data-directory": Directory.Move(api.Data, api.Data + ".original"); Fixture.ExistingData(api.Data); break;
                case "database":
                    var db = Path.Combine(api.Data, "state", "agent-recorder.db"); File.Move(db, db + ".original"); Fixture.ExistingData(api.Data); break;
                case "capability": api.Set("caps-mode", "no-region"); break;
                case "capability-version": api.Set("caps-mode", "wrong-version"); break;
                case "host-instance":
                    var readyPath = Path.Combine(api.Data, "runtime", "ready.json"); var ready = JsonNode.Parse(File.ReadAllText(readyPath))!;
                    ready["instance_id"] = Guid.NewGuid().ToString("D"); File.WriteAllText(readyPath, ready.ToJsonString()); break;
                case "package-version":
                    var replaced = await PublishUnsupportedCli(); File.Copy(Path.Combine(replaced, "AgentRecorder.Cli.exe"), cliExe, true);
                    File.Copy(Path.Combine(replaced, "AgentRecorder.Cli.dll"), cliDll, true); break;
            }
            var cwd = fixture.NewPath("unrelated upgraded caller");
            await using var second = await fixture.Connect(binding, PluginStdioLaunch.Read(secondPlugin).Command, cwd);
            Assert.Equal(expected, UpgradeCompatibilityScenarios.Reason(await second.Call("recorder_operation_get", UpgradeCompatibilityScenarios.Query(id))));
            Assert.Equal(1, api.Posts); Assert.Equal(0, UpgradeCompatibilityScenarios.StopPosts(api)); Assert.False(api.HasExited);
            var observedData = change == "data-directory" ? api.Data + ".original" : api.Data;
            Assert.Single(File.ReadAllLines(Path.Combine(observedData, "runtime", "cli-arguments.jsonl")));
            Assert.Equal(bindingBytes, File.ReadAllBytes(binding)); Assert.Empty(Directory.EnumerateFileSystemEntries(cwd));
            Assert.False(Directory.Exists(Path.Combine(secondPlugin, ".local-data")));
            if (change != "data-directory") Assert.Equal(dataIdentity, FileIdentity.Read(api.Data));
            output.WriteLine($"Upgrade rejected {change}: {expected}; create=1/stop=0; no rebind or host replacement.");
        }
        finally { File.WriteAllBytes(cliExe, savedExe); File.WriteAllBytes(cliDll, savedDll); }
    }

    private async Task<string> PublishUnsupportedCli()
    {
        var target = fixture.NewPath("unsupported-cli");
        using var process = Process.Start(new ProcessStartInfo("dotnet") { UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true,
            ArgumentList = { "publish", Path.Combine(Fixture.Repo, "tests", "AgentRecorder.Mcp.FakeCli", "AgentRecorder.Mcp.FakeCli.csproj"),
                "-c", "Release", "-r", "win-x64", "--self-contained", "true", "-o", target, "--nologo", "-p:UseSharedCompilation=false",
                "-p:Version=0.1.26", "-p:AssemblyVersion=0.1.26.0", "-p:FileVersion=0.1.26.0" } })!;
        var stdout = process.StandardOutput.ReadToEndAsync(); var stderr = process.StandardError.ReadToEndAsync();
        output.WriteLine("Private publisher identity: " + JsonSerializer.Serialize(
            AgentRecorder.LifecycleTests.LifecycleObservation.Capture(process, "private_unsupported_cli_publish")));
        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(90)); Assert.True(process.ExitCode == 0, await stdout + await stderr);
        return target;
    }
}
