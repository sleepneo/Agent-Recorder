using System.Reflection;
using System.Text;
using System.Text.Json;

namespace AgentRecorder.Tests;

internal sealed record ShutdownTestExecutables(string OutputDirectory, string Host, string Cli)
{
    internal static ShutdownTestExecutables Current() => FromOutput(AppContext.BaseDirectory);

    internal static ShutdownTestExecutables FromOutput(string directory)
    {
        var output = new DirectoryInfo(Path.GetFullPath(directory));
        var configuration = output.Parent;
        if (configuration?.Name is not ("Release" or "Debug") ||
            (configuration.Parent?.Name != "bin" && configuration.Parent?.Parent?.Name != "bin"))
            throw new InvalidDataException("Expected the current test bin/[flavor/]configuration/framework output; no fallback is allowed.");
        var tests = ReadManifest("AgentRecorder.Tests");
        if (output.Name != tests.Tfm && !output.Name.StartsWith(tests.Tfm + "-", StringComparison.Ordinal))
            throw new InvalidDataException("Test output framework does not match its runtimeconfig.");
        return new(output.FullName, Validate("AgentRecorder.FutureWindowUiTestHost"), Validate("AgentRecorder.Cli"));

        string Require(string name)
        {
            var path = Path.Combine(output.FullName, name);
            if (!File.Exists(path)) throw new FileNotFoundException("Build the test project and its references into this same output before running shutdown tests; no other flavor is searched.", path);
            return path;
        }
        (string Tfm, string Target, string Library) ReadManifest(string name)
        {
            using var runtime = JsonDocument.Parse(File.ReadAllText(Require(name + ".runtimeconfig.json")));
            using var deps = JsonDocument.Parse(File.ReadAllText(Require(name + ".deps.json")));
            var tfm = runtime.RootElement.GetProperty("runtimeOptions").GetProperty("tfm").GetString()!;
            var target = deps.RootElement.GetProperty("runtimeTarget").GetProperty("name").GetString()!;
            var library = deps.RootElement.GetProperty("libraries").EnumerateObject()
                .Single(entry => entry.Name.StartsWith(name + "/", StringComparison.Ordinal) && entry.Value.GetProperty("type").GetString() == "project").Name;
            if (!deps.RootElement.GetProperty("targets").GetProperty(target).GetProperty(library)
                .GetProperty("runtime").TryGetProperty(name + ".dll", out _))
                throw new InvalidDataException("Executable deps runtime identity is inconsistent: " + name);
            return (tfm, target, library);
        }
        string Validate(string name)
        {
            var executable = Require(name + ".exe");
            var dll = Require(name + ".dll");
            var manifest = ReadManifest(name);
            using var testDeps = JsonDocument.Parse(File.ReadAllText(Require("AgentRecorder.Tests.deps.json")));
            if (manifest.Tfm != tests.Tfm || manifest.Target != tests.Target ||
                !testDeps.RootElement.GetProperty("libraries").TryGetProperty(manifest.Library, out _) ||
                AssemblyName.GetAssemblyName(dll).Name != name)
                throw new InvalidDataException("Executable does not match the current test build: " + name);
            var bytes = File.ReadAllBytes(executable);
            if (bytes.Length < 2 || bytes[0] != 'M' || bytes[1] != 'Z' ||
                bytes.AsSpan().IndexOf(Encoding.UTF8.GetBytes(name + ".dll\0")) < 0)
                throw new InvalidDataException("Executable apphost targets a different managed host: " + name);
            return executable;
        }
    }
}
