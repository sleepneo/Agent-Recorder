using System.Diagnostics;
using System.Reflection;
using System.Text.Json;

namespace AgentRecorder.Mcp;

internal static class PackageValidator
{
    public const string SupportedVersion = "0.1.25";

    public static (string App, string Cli) Validate(string root)
    {
        if (!Directory.Exists(root)) throw new ContractException("package_missing");
        if (File.Exists(Path.Combine(root, "agent-recorder.install.json")))
            throw new ContractException("portable_package_required");
        string? app = null, cli = null;
        foreach (var name in new[] { "AgentRecorder.App", "AgentRecorder.Cli" })
        {
            var directory = LocalPaths.Normalize(Path.Combine(root, name));
            var executable = LocalPaths.Normalize(Path.Combine(directory, name + ".exe"));
            var assembly = LocalPaths.Normalize(Path.Combine(directory, name + ".dll"));
            var runtime = LocalPaths.Normalize(Path.Combine(directory, name + ".runtimeconfig.json"));
            var deps = LocalPaths.Normalize(Path.Combine(directory, name + ".deps.json"));
            foreach (var path in new[] { executable, assembly, runtime, deps })
                if (!File.Exists(path)) throw new ContractException("package_dependency_missing");
            var identity = AssemblyName.GetAssemblyName(assembly);
            var info = FileVersionInfo.GetVersionInfo(executable);
            if (identity.Name != name || info.OriginalFilename != name + ".dll")
                throw new ContractException("package_identity_mismatch");
            if (identity.Version?.ToString(3) != SupportedVersion || info.ProductVersion?.Split('+')[0] != SupportedVersion)
                throw new ContractException("product_version_unsupported");
            foreach (var productAssembly in Directory.EnumerateFiles(directory, "AgentRecorder.*.dll"))
            {
                var metadata = AssemblyName.GetAssemblyName(LocalPaths.Normalize(productAssembly));
                if (metadata.Name != Path.GetFileNameWithoutExtension(productAssembly) || metadata.Version?.ToString(3) != SupportedVersion)
                    throw new ContractException("product_version_unsupported");
            }
            using var config = Json.Read(ReadBounded(runtime));
            var runtimeOptions = config.RootElement.GetProperty("runtimeOptions");
            if (runtimeOptions.GetProperty("tfm").GetString() != "net8.0")
                throw new ContractException("package_runtime_unsupported");
            // Initial integration supports the official self-contained x64 portable layout.
            // Framework-dependent packages must not accidentally work only on the developer's SDK.
            if (!runtimeOptions.TryGetProperty("includedFrameworks", out _))
                throw new ContractException("package_self_contained_required");
            foreach (var required in new[] { "hostfxr.dll", "hostpolicy.dll", "coreclr.dll", "System.Private.CoreLib.dll" })
                RequireFile(Path.Combine(directory, required));
            using var manifest = Json.Read(ReadBounded(deps));
            var targetName = manifest.RootElement.GetProperty("runtimeTarget").GetProperty("name").GetString()!;
            if (!targetName.EndsWith("/win-x64", StringComparison.Ordinal))
                throw new ContractException("package_runtime_unsupported");
            foreach (var library in manifest.RootElement.GetProperty("targets").GetProperty(targetName).EnumerateObject())
                foreach (var category in new[] { "runtime", "native" })
                    if (library.Value.TryGetProperty(category, out var assets))
                        foreach (var asset in assets.EnumerateObject())
                        {
                            var filename = Path.GetFileName(asset.Name);
                            if (filename == "_._") continue;
                            RequireFile(Path.Combine(directory, filename));
                        }
            if (name == "AgentRecorder.App") { app = executable; RequireFile(Path.Combine(directory, "ffmpeg.exe")); }
            else cli = executable;
        }
        return (app!, cli!);
    }

    private static void RequireFile(string path)
    {
        if (!File.Exists(LocalPaths.Normalize(path))) throw new ContractException("package_dependency_missing");
    }

    private static string ReadBounded(string path)
    {
        if (new FileInfo(path).Length > 4 * 1024 * 1024) throw new ContractException("package_metadata_invalid");
        return File.ReadAllText(path);
    }
}
