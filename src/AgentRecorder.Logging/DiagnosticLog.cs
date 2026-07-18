using System;
using System.IO;
using System.Text;
using System.Text.Json;

namespace AgentRecorder.Logging;

/// <summary>
/// Best-effort local diagnostic logging. Unlike <see cref="AuditLogger"/>,
/// this is intended for internal troubleshooting only and may include
/// limited exception detail; it must never contain API keys, credentials,
/// or user-sensitive data.
/// </summary>
public static class DiagnosticLog
{
    private static readonly object Lock = new();
    private static string? _cachedPath;

    /// <summary>
    /// Writes a diagnostic entry with a limited-length message.
    /// </summary>
    public static void Write(string category, string recordingId, string errorCode, string? detail = null)
    {
        const int MaxDetailLength = 500;

        var entry = new
        {
            time = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ"),
            category,
            recording_id = recordingId,
            error_code = errorCode,
            detail = detail == null ? null : Truncate(detail, MaxDetailLength)
        };

        try
        {
            var path = GetPath();
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);

            var line = JsonSerializer.Serialize(entry);
            lock (Lock)
            {
                File.AppendAllText(path, line + Environment.NewLine, Encoding.UTF8);
            }
        }
        catch
        {
            // Diagnostic logging is best-effort and must not fail the caller.
        }
    }

    private static string GetPath()
    {
        if (_cachedPath != null) return _cachedPath;

        var auditDir = Path.GetDirectoryName(Paths.AuditLogPath);
        var dir = string.IsNullOrEmpty(auditDir)
            ? Path.Combine(AppContext.BaseDirectory, "logs")
            : auditDir;
        _cachedPath = Path.Combine(dir, "diagnostics.jsonl");
        return _cachedPath;
    }

    private static string? Truncate(string? value, int maxLength)
    {
        if (string.IsNullOrEmpty(value)) return value;
        if (value.Length <= maxLength) return value;
        return value[..maxLength] + "...";
    }
}
