using System;
using System.IO;

namespace AgentRecorder.Infrastructure;

public static class DataDirResolver
{
    private const string EnvVarName = "AGENT_RECORDER_DATA_DIR";
    private static string? _override;

    public static void SetOverride(string? path)
    {
        _override = path;
    }

    public static void ClearOverride()
    {
        _override = null;
    }

    public static string Resolve()
    {
        return ResolveForContext(InstallationContext.PackageRootFromBase(AppContext.BaseDirectory),
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), _override,
            Environment.GetEnvironmentVariable(EnvVarName));
    }

    internal static string ResolveForContext(string packageRoot, string localRoot, string? explicitDirectory, string? environmentDirectory)
    {
        var installed = InstallationContext.IsInstalled(packageRoot, localRoot);
        var data = explicitDirectory != null ? Path.GetFullPath(explicitDirectory) :
            !string.IsNullOrWhiteSpace(environmentDirectory) ? Path.GetFullPath(environmentDirectory) :
            Path.Combine(localRoot, "AgentRecorder");
        if (installed) InstallationContext.RequireSeparate(packageRoot, data);
        return data;
    }

    public static bool HasOverride => _override != null;

    public static bool UsingEnvOverride =>
        !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(EnvVarName));
}
