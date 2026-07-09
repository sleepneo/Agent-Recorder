using System;
using System.IO;
using AgentRecorder.Infrastructure;

namespace AgentRecorder.Logging;

public static class Paths
{
    public static string DefaultOutputDir
    {
        get
        {
            // Delegates to OutputSettingsStore so that a user-configured default
            // directory (if valid) takes precedence over the built-in fallback.
            return OutputSettingsStore.GetEffectiveDefaultOutputDir();
        }
    }

    public static string AuditLogPath
    {
        get
        {
            return Path.Combine(DataDirResolver.Resolve(), "logs", "audit.jsonl");
        }
    }

    public static bool UsingEnvOverride => DataDirResolver.UsingEnvOverride;

    public static string DataDir => DataDirResolver.Resolve();
}
