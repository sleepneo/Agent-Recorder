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
            if (DataDirResolver.UsingEnvOverride)
                return Path.Combine(DataDirResolver.Resolve(), "Videos");
            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.MyVideos),
                "AgentRecorder");
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
