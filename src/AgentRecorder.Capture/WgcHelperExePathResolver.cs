using System.IO;

namespace AgentRecorder.Capture;

/// <summary>
/// Resolves the path to the native WGC helper executable.
/// Priority: 1) AGENT_RECORDER_WGC_HELPER_EXE env var, 2) project-relative
/// `tools\wgc-native-helper\bin\wgc-native-helper.exe`. Throws if neither
/// is found.
/// </summary>
public static class WgcHelperExePathResolver
{
    public const string EnvVarName = "AGENT_RECORDER_WGC_HELPER_EXE";
    public const string DefaultRelativePath = @"tools\wgc-native-helper\bin\wgc-native-helper.exe";

    public static string Resolve()
    {
        var fromEnv = Environment.GetEnvironmentVariable(EnvVarName)?.Trim();
        if (!string.IsNullOrEmpty(fromEnv) && File.Exists(fromEnv))
            return fromEnv;

        string projectRelative = Path.Combine(Directory.GetCurrentDirectory(), DefaultRelativePath);
        if (File.Exists(projectRelative))
            return projectRelative;

        throw new FileNotFoundException(
            "WGC helper executable not found. " +
            $"Set {EnvVarName} or build tools/wgc-native-helper (expected path: {projectRelative}).",
            fromEnv ?? projectRelative);
    }
}
