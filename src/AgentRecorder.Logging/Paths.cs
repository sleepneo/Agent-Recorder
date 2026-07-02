using System;
using System.IO;
namespace AgentRecorder.Logging;
public static class Paths
{
    private const string DataDirEnvVar = "AGENT_RECORDER_DATA_DIR";

    private static string DataBaseDir
    {
        get
        {
            var env = Environment.GetEnvironmentVariable(DataDirEnvVar);
            if (!string.IsNullOrWhiteSpace(env) && Path.IsPathFullyQualified(env))
                return env;
            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "AgentRecorder");
        }
    }

    public static string DefaultOutputDir
    {
        get
        {
            var env = Environment.GetEnvironmentVariable(DataDirEnvVar);
            if (!string.IsNullOrWhiteSpace(env) && Path.IsPathFullyQualified(env))
                return Path.Combine(env, "Videos");
            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.MyVideos),
                "AgentRecorder");
        }
    }

    public static string AuditLogPath
    {
        get
        {
            var env = Environment.GetEnvironmentVariable(DataDirEnvVar);
            if (!string.IsNullOrWhiteSpace(env) && Path.IsPathFullyQualified(env))
                return Path.Combine(env, "logs", "audit.jsonl");
            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "AgentRecorder", "logs", "audit.jsonl");
        }
    }

    public static bool UsingEnvOverride =>
        !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(DataDirEnvVar));

    public static string DataDir => DataBaseDir;
}
