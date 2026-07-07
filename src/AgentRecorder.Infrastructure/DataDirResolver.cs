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
        if (_override != null)
            return Path.GetFullPath(_override);

        var env = Environment.GetEnvironmentVariable(EnvVarName);
        if (!string.IsNullOrWhiteSpace(env))
            return Path.GetFullPath(env);

        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "AgentRecorder");
    }

    public static bool HasOverride => _override != null;

    public static bool UsingEnvOverride =>
        !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(EnvVarName));
}