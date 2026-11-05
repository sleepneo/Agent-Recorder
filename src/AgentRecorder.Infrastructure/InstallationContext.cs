using System.Text.Json;

namespace AgentRecorder.Infrastructure;

/// <summary>Packaging metadata only. Never conveys desktop proof, credentials or recording permission.</summary>
public static class InstallationContext
{
    public const string MarkerName = "agent-recorder.install.json";
    public const string ProductId = "AgentRecorder.PerUser.v1";
    private const string AcceptedUpgradeSource = "0.1.19";
    public static string ProductVersion { get; } = typeof(InstallationContext).Assembly.GetName().Version!.ToString(3);
    public static string MarkerJson => JsonSerializer.Serialize(new { format = 1, product = ProductId, version = ProductVersion, mode = "per-user" });
    public static string DefaultDataRoot => Path.Combine(LocalRoot, "AgentRecorder");
    private static string LocalRoot => Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
    public static string ProgramRoot => ExpectedProgramRoot(LocalRoot);
    public static string MaintenanceMutexName => @"Global\AgentRecorder.PerUser.Maintenance.v1." +
        (System.Security.Principal.WindowsIdentity.GetCurrent().User?.Value
            ?? throw new InvalidOperationException("INSTALL_USER_UNKNOWN"));
    internal static string ExpectedProgramRoot(string local) => Path.GetFullPath(Path.Combine(local, "Programs", "AgentRecorder"));

    public static string PackageRootFromBase(string baseDirectory)
    {
        var dir = new DirectoryInfo(baseDirectory);
        return dir.Name is "AgentRecorder.App" or "AgentRecorder.Cli" or "AgentRecorder.Headless"
            ? dir.Parent!.FullName : dir.FullName;
    }

    public static bool IsInstalled(string packageRoot) => IsInstalled(packageRoot, LocalRoot);

    internal static bool IsInstalled(string packageRoot, string localRoot)
        => ReadInstalledVersion(packageRoot, localRoot) != null;

    internal static string? ReadInstalledVersion(string packageRoot, string localRoot, bool allowAcceptedUpgradeSource = false)
    {
        var root = Path.GetFullPath(packageRoot);
        var marker = Path.Combine(root, MarkerName);
        foreach (var component in new[] { "AgentRecorder.App", "AgentRecorder.Cli", "AgentRecorder.Headless" })
            if (Path.Exists(Path.Combine(root, component, MarkerName)))
                throw new InvalidDataException("INSTALL_CONTEXT_CONFLICT: component installation marker.");
        try { _ = File.GetAttributes(marker); }
        catch (FileNotFoundException) { return null; }
        catch (DirectoryNotFoundException) { return null; }
        RequireNonReparse(root);
        RequireNonReparse(marker);
        if (!Equal(root, ExpectedProgramRoot(localRoot)))
            throw new InvalidDataException("INSTALL_CONTEXT_CONFLICT: installation belongs to a different package/user root.");
        var info = new FileInfo(marker);
        if (info.Length is <= 0 or > 1024) throw new InvalidDataException("INSTALL_MARKER_INVALID");
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(marker));
            var obj = doc.RootElement;
            if (obj.ValueKind != JsonValueKind.Object || obj.EnumerateObject().Count() != 4 ||
                obj.GetProperty("format").GetInt32() != 1 || obj.GetProperty("product").GetString() != ProductId ||
                obj.GetProperty("mode").GetString() != "per-user" ||
                obj.EnumerateObject().Select(p => p.Name).Distinct(StringComparer.Ordinal).Count() != 4)
                throw new InvalidDataException("INSTALL_MARKER_INVALID");
            var version = obj.GetProperty("version").GetString();
            if (version != ProductVersion && !(allowAcceptedUpgradeSource && version == AcceptedUpgradeSource))
                throw new InvalidDataException("INSTALL_MARKER_INVALID");
            return version;
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException or FormatException)
        {
            throw new InvalidDataException("INSTALL_MARKER_INVALID", ex);
        }
    }

    public static string ResolveCliDataRoot(string packageRoot, string? explicitDirectory)
        => ResolveCliDataRoot(packageRoot, explicitDirectory,
            Environment.GetEnvironmentVariable("AGENT_RECORDER_DATA_DIR"), LocalRoot);

    internal static string ResolveCliDataRoot(string packageRoot, string? explicitDirectory, string? environmentDirectory, string localRoot)
    {
        var installed = IsInstalled(packageRoot, localRoot);
        var selected = !string.IsNullOrWhiteSpace(explicitDirectory) ? explicitDirectory :
            installed ? (!string.IsNullOrWhiteSpace(environmentDirectory) ? environmentDirectory : Path.Combine(localRoot, "AgentRecorder")) :
            Path.Combine(packageRoot, ".local-data"); // Preserve existing portable CLI default, including ignored inherited env.
        var data = Path.GetFullPath(selected);
        if (installed) RequireSeparate(packageRoot, data);
        return data;
    }

    public static void RequireSeparate(string programRoot, string dataRoot)
    {
        if (Contains(programRoot, dataRoot) || Contains(dataRoot, programRoot))
            throw new InvalidDataException("INSTALL_DATA_OVERLAP");
        RequireNonReparse(dataRoot);
    }

    public static void RequireNonReparse(string path)
    {
        var full = Path.GetFullPath(path);
        if (!Path.IsPathFullyQualified(path) || full.StartsWith(@"\\", StringComparison.Ordinal))
            throw new InvalidDataException("INSTALL_PATH_INVALID");
        for (string? part = full; part != null; part = Path.GetDirectoryName(part))
        {
            try
            {
                if ((File.GetAttributes(part) & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidDataException("INSTALL_REPARSE_PATH");
            }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
        }
    }

    internal static bool Equal(string a, string b) => string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(a)),
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(b)), StringComparison.OrdinalIgnoreCase);
    internal static bool Contains(string parent, string child) => Equal(parent, child) ||
        Path.GetFullPath(child).StartsWith(Path.TrimEndingDirectorySeparator(Path.GetFullPath(parent)) + Path.DirectorySeparatorChar,
            StringComparison.OrdinalIgnoreCase);
}
