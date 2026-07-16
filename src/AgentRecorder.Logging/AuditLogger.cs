using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
namespace AgentRecorder.Logging;
public class AuditLogger
{
    private readonly string _path;
    private readonly object _lock = new();

    public AuditLogger()
        : this(Paths.AuditLogPath)
    {
    }

    internal AuditLogger(string path)
    {
        if (path is null)
            throw new ArgumentNullException(nameof(path));
        if (string.IsNullOrWhiteSpace(path))
            throw new ArgumentException("Audit log path cannot be empty or whitespace.", nameof(path));

        _path = path;
        var dir = Path.GetDirectoryName(_path);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);
    }

    public virtual void Log(string evt, object payload)
    {
        var dict = new Dictionary<string, object?>
        {
            ["time"] = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ"),
            ["event"] = evt
        };
        foreach (var p in payload.GetType().GetProperties())
        {
            var val = p.GetValue(payload);
            if (val != null) dict[p.Name] = val;
        }
        var line = JsonSerializer.Serialize(dict);
        lock (_lock) File.AppendAllText(_path, line + Environment.NewLine, Encoding.UTF8);
    }
}
