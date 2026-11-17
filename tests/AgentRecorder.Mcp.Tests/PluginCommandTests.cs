using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using Xunit;
using Xunit.Abstractions;

namespace AgentRecorder.Mcp.Tests;

public sealed class PluginCommandTests(Fixture fixture, ITestOutputHelper output) : IClassFixture<Fixture>
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BuilderRejectsNonBuildOutputRootBeforeWriting(bool buildParent)
    {
        var target = buildParent ? Path.Combine(Fixture.Repo, ".local-data", "plugin-builds") : Fixture.Repo;
        var existed = Directory.Exists(target);
        using var process = Process.Start(new ProcessStartInfo("pwsh")
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
            ArgumentList = { "-NoProfile", "-File", Path.Combine(Fixture.Repo, "scripts", "build-codex-plugin.ps1"), "-OutputDirectory", target }
        })!;
        var stdout = process.StandardOutput.ReadToEndAsync(); var stderr = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15));
        Assert.NotEqual(0, process.ExitCode);
        Assert.Contains("OutputDirectory must be below", await stdout + await stderr);
        Assert.Equal(existed, Directory.Exists(target));
    }

    [Theory]
    [InlineData("0.2.1")]
    [InlineData("0.3.0")]
    [InlineData("0.3.1")]
    [InlineData("0.3.2")]
    [InlineData("0.3.4")]
    public async Task VerifyOnlyRejectsRelabelledOrUnsupportedVersions(string version)
    {
        var path = Path.Combine(fixture.Plugin, "plugin.json"); var original = File.ReadAllBytes(path);
        try
        {
            var node = JsonNode.Parse(original)!; node["version"] = version; File.WriteAllText(path, node.ToJsonString());
            var result = await Verify(fixture.Plugin); Assert.NotEqual(0, result.Exit);
            Assert.Contains(version == "0.3.4" ? "Unexpected plugin version" : "Historical plugin identity mismatch", result.Text);
        }
        finally { File.WriteAllBytes(path, original); }
    }

    private async Task<(int Exit, string Text)> Verify(string plugin)
    {
        using var process = Process.Start(new ProcessStartInfo("pwsh")
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
            ArgumentList = { "-NoProfile", "-File", Path.Combine(Fixture.Repo, "scripts", "build-codex-plugin.ps1"),
                "-OutputDirectory", Path.GetDirectoryName(plugin)!, "-VerifyOnly" }
        })!;
        var stdout = process.StandardOutput.ReadToEndAsync(); var stderr = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(20));
        return (process.ExitCode, await stdout + await stderr);
    }

    [Theory]
    [InlineData("${PLUGIN_ROOT}/server/AgentRecorder.Mcp.exe")]
    [InlineData("C:/outside/AgentRecorder.Mcp.exe")]
    [InlineData("./../AgentRecorder.Mcp.exe")]
    [InlineData("./server/../../AgentRecorder.Mcp.exe")]
    [InlineData("./server\\AgentRecorder.Mcp.exe")]
    [InlineData("AgentRecorder.Mcp.exe")]
    [InlineData("./server/missing.exe")]
    [InlineData("./server/AgentRecorder.Mcp.exe:stream")]
    public async Task InvalidBundledCommandFailsParserAndActualVerifyOnly(string command)
    {
        var path = Path.Combine(fixture.Plugin, "mcp.json"); var original = File.ReadAllBytes(path);
        try
        {
            var config = JsonNode.Parse(original)!;
            config["mcpServers"]!["agent-recorder-bootstrap"]!["command"] = command;
            File.WriteAllText(path, config.ToJsonString());
            Assert.Contains("PLUGIN_COMMAND_INVALID", Assert.Throws<InvalidDataException>(() => PluginStdioLaunch.Read(fixture.Plugin)).Message);
            var verification = await Verify(fixture.Plugin);
            Assert.NotEqual(0, verification.Exit);
            Assert.Contains("PLUGIN_COMMAND_INVALID", verification.Text);
            output.WriteLine("Rejected by restricted parser and build VerifyOnly: " + command);
        }
        finally { File.WriteAllBytes(path, original); }
    }

    [Theory]
    [InlineData("${PLUGIN_ROOT}", true)]
    [InlineData("${PLUGIN_ROOT}/server", true)]
    [InlineData("./server", true)]
    [InlineData("./../outside", false)]
    public async Task CwdIsValidatedSeparatelyAndSupportsPluginRootExpansion(string cwd, bool valid)
    {
        var path = Path.Combine(fixture.Plugin, "mcp.json"); var original = File.ReadAllBytes(path);
        try
        {
            var config = JsonNode.Parse(original)!;
            config["mcpServers"]!["agent-recorder-bootstrap"]!["cwd"] = cwd;
            File.WriteAllText(path, config.ToJsonString());
            if (valid)
            {
                var launch = PluginStdioLaunch.Read(fixture.Plugin);
                Assert.Equal(Path.Combine(fixture.Plugin, "server", "AgentRecorder.Mcp.exe"), launch.Command);
                Assert.Equal(cwd == "${PLUGIN_ROOT}" ? fixture.Plugin : Path.Combine(fixture.Plugin, "server"), launch.WorkingDirectory);
            }
            else Assert.Contains("PLUGIN_CWD_INVALID", Assert.Throws<InvalidDataException>(() => PluginStdioLaunch.Read(fixture.Plugin)).Message);
            var verification = await Verify(fixture.Plugin);
            Assert.Equal(valid, verification.Exit == 0);
            if (!valid) Assert.Contains("PLUGIN_CWD_INVALID", verification.Text);
        }
        finally { File.WriteAllBytes(path, original); }
    }

    [Fact]
    public async Task JunctionCommandEscapeFailsBothSemanticGates()
    {
        var plugin = fixture.NewPath(Path.Combine("linked-package", "agent-recorder-bootstrap")); Directory.CreateDirectory(plugin);
        File.Copy(Path.Combine(fixture.Plugin, "plugin.json"), Path.Combine(plugin, "plugin.json"));
        File.Copy(Path.Combine(fixture.Plugin, "mcp.json"), Path.Combine(plugin, "mcp.json"));
        var link = Path.Combine(plugin, "server");
        using var junction = Process.Start(new ProcessStartInfo("cmd.exe")
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true,
            ArgumentList = { "/c", "mklink", "/J", link, Path.Combine(fixture.Plugin, "server") }
        })!;
        await junction.StandardOutput.ReadToEndAsync(); await junction.WaitForExitAsync(); Assert.Equal(0, junction.ExitCode);
        try
        {
            Assert.Throws<InvalidDataException>(() => PluginStdioLaunch.Read(plugin));
            var verification = await Verify(plugin);
            Assert.NotEqual(0, verification.Exit);
            Assert.Contains("PLUGIN_COMMAND_INVALID", verification.Text);
        }
        finally { Directory.Delete(link); }
    }

    [Fact]
    public async Task CopiedThenMovedPluginWithSpacesLaunchesFromManifestInUnrelatedCwd()
    {
        var source = Environment.GetEnvironmentVariable("AGENT_RECORDER_MCP_TEST_PLUGIN_ROOT") ?? fixture.Plugin;
        var copied = fixture.NewPath("copied plugin with spaces 0.3.3");
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var destination = Path.Combine(copied, Path.GetRelativePath(source, file));
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!); File.Copy(file, destination);
        }
        var binding = fixture.NewPath("binding.json"); var data = fixture.NewPath("data"); Fixture.ExistingData(data); fixture.Mode("reuse");
        var unrelated = fixture.NewPath("unrelated caller project"); Directory.CreateDirectory(unrelated);
        var previousCwd = Environment.CurrentDirectory;
        try
        {
            Directory.SetCurrentDirectory(unrelated);
            await Exercise(copied);
            var moved = fixture.NewPath("moved plugin with spaces 0.3.3"); Directory.CreateDirectory(Path.GetDirectoryName(moved)!);
            Directory.Move(copied, moved);
            await Exercise(moved);
            Assert.Empty(Directory.GetFileSystemEntries(unrelated));
            Assert.Equal(2, File.ReadAllLines(Path.Combine(data, "runtime", "cli-arguments.jsonl")).Length);
            output.WriteLine("Manifest-resolved SDK calls passed before/after move, with spaces and unrelated caller CWD.");
        }
        finally { Directory.SetCurrentDirectory(previousCwd); }

        async Task Exercise(string plugin)
        {
            var launch = PluginStdioLaunch.Read(plugin);
            Assert.Equal(plugin, launch.WorkingDirectory);
            await using var session = await fixture.Connect(binding, launch.Command, launch.WorkingDirectory);
            Assert.Equal("0.3.3", session.Client.ServerInfo!.Version);
            Assert.Equal(6, (await session.Client.ListToolsAsync()).Count);
            Assert.True((await session.Call("recorder_binding_set", new()
            { ["package_root"] = fixture.Package, ["data_dir"] = data })).GetProperty("ok").GetBoolean());
            Assert.Equal("binding_valid", (await session.Call("recorder_binding_get")).GetProperty("reason_code").GetString());
            Assert.Equal("host_ready_cli_verified", (await session.Call("recorder_host_ensure")).GetProperty("reason_code").GetString());
        }
    }
}
