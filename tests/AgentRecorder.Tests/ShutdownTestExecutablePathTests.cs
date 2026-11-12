using System.Text.Json.Nodes;
using Xunit;

namespace AgentRecorder.Tests;

public sealed class ShutdownTestExecutablePathTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ResolvesOnlyCopiedSameBuildInputsInStandardAndFlavorLayout(bool flavor)
    {
        using var root = new TempDirectory();
        var output = Populate(root.Path, flavor);
        var result = ShutdownTestExecutables.FromOutput(output);
        Assert.Equal(Path.Combine(output, "AgentRecorder.FutureWindowUiTestHost.exe"), result.Host);
        Assert.Equal(Path.Combine(output, "AgentRecorder.Cli.exe"), result.Cli);
    }

    [Theory]
    [InlineData("AgentRecorder.FutureWindowUiTestHost.exe")]
    [InlineData("AgentRecorder.FutureWindowUiTestHost.dll")]
    [InlineData("AgentRecorder.FutureWindowUiTestHost.deps.json")]
    [InlineData("AgentRecorder.FutureWindowUiTestHost.runtimeconfig.json")]
    [InlineData("AgentRecorder.Cli.exe")]
    [InlineData("AgentRecorder.Cli.runtimeconfig.json")]
    public void MissingCurrentInputFailsWithoutFallingBackToCompleteOldFlavor(string missing)
    {
        using var root = new TempDirectory();
        var output = Populate(root.Path, false);
        _ = Populate(root.Path, true); // A complete other flavor is deliberately available.
        File.Delete(Path.Combine(output, missing));
        var error = Assert.Throws<FileNotFoundException>(() => ShutdownTestExecutables.FromOutput(output));
        Assert.Equal(Path.Combine(output, missing), error.FileName);
        Assert.Contains("no other flavor", error.Message);
    }

    [Theory]
    [InlineData("runtime")]
    [InlineData("apphost")]
    [InlineData("framework")]
    [InlineData("configuration")]
    public void WrongCopiedInputsOrOutputLayoutFailClearly(string kind)
    {
        using var root = new TempDirectory(); var output = Populate(root.Path, false);
        if (kind == "runtime")
        {
            var file = Path.Combine(output, "AgentRecorder.Cli.runtimeconfig.json");
            var json = JsonNode.Parse(File.ReadAllText(file))!; json["runtimeOptions"]!["tfm"] = "net9.0"; File.WriteAllText(file, json.ToJsonString());
        }
        else if (kind == "apphost") File.Copy(Path.Combine(output, "AgentRecorder.Cli.exe"), Path.Combine(output, "AgentRecorder.FutureWindowUiTestHost.exe"), true);
        else
        {
            var destination = kind == "framework" ? Path.Combine(Path.GetDirectoryName(output)!, "net9.0") :
                Path.Combine(root.Path, "unrelated", "net8.0-windows10.0.19041.0");
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!); Directory.Move(output, destination); output = destination;
        }
        Assert.Throws<InvalidDataException>(() => ShutdownTestExecutables.FromOutput(output));
    }

    private static string Populate(string root, bool flavor)
    {
        var output = Path.Combine(root, "bin", flavor ? "Task429/Release" : "Release", new DirectoryInfo(AppContext.BaseDirectory).Name);
        Directory.CreateDirectory(output);
        foreach (var name in new[] { "AgentRecorder.Tests.deps.json", "AgentRecorder.Tests.runtimeconfig.json",
            "AgentRecorder.FutureWindowUiTestHost.exe", "AgentRecorder.FutureWindowUiTestHost.dll", "AgentRecorder.FutureWindowUiTestHost.deps.json", "AgentRecorder.FutureWindowUiTestHost.runtimeconfig.json",
            "AgentRecorder.Cli.exe", "AgentRecorder.Cli.dll", "AgentRecorder.Cli.deps.json", "AgentRecorder.Cli.runtimeconfig.json" })
            File.Copy(Path.Combine(AppContext.BaseDirectory, name), Path.Combine(output, name));
        return Path.GetFullPath(output);
    }
}
