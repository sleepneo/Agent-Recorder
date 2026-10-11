using Microsoft.Data.Sqlite;

namespace AgentRecorder.Infrastructure;

// Versioned raw-text comparison. Never normalize paths or consult the filesystem.
internal static class SqliteOutputPathCollation
{
    internal const string Name = "AGENTRECORDER_PATH_ORDINAL_IGNORE_CASE_V1";
    internal static void Register(SqliteConnection connection) =>
        connection.CreateCollation(Name, StringComparer.OrdinalIgnoreCase.Compare);
}
