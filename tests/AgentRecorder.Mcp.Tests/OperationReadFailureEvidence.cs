using System.Text.Json;

namespace AgentRecorder.Mcp.Tests;

// Only synthetic callers opt in. No hook is registered in the MCP process.
internal static class OperationReadFailureEvidence
{
    public static string Save(string directory, string operationId, OperationStore.ReadEvidence? evidence, object? owner = null)
    {
        var allowed = Path.GetFullPath(Path.Combine(Fixture.Repo, ".local-data", "task434R")) + Path.DirectorySeparatorChar;
        var full = Path.GetFullPath(directory);
        if (!full.StartsWith(allowed, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Not a private evidence root");
        Directory.CreateDirectory(full);
        var path = Path.Combine(full, "first-failure.json");
        using var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        JsonSerializer.Serialize(file, new { operation_id = operationId, read = evidence, owner }, Json.Options);
        file.Flush(true);
        return path;
    }
}
