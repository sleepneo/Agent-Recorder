using System;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Threading;

namespace AgentRecorder.Infrastructure;

/// <summary>
/// Runtime readiness state model. Tracks startup timing, writes ready.json,
/// and sets a named event so AI agents can detect service readiness without
/// blind-polling /capabilities.
/// </summary>
public sealed class RuntimeReadiness : IDisposable
{
    public const string NamedEventName = @"Local\AgentRecorderReady";

    private readonly Stopwatch _stopwatch;
    private readonly string _mode;
    private readonly int _pid;
    private readonly int _port;
    private EventWaitHandle? _namedEvent;
    private string? _readyFileWritten;
    private bool _disposed;

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        WriteIndented = false
    };

    public RuntimeReadiness(string mode, int port)
    {
        _mode = mode;
        _pid = Environment.ProcessId;
        _port = port;
        _stopwatch = Stopwatch.StartNew();
    }

    /// <summary>
    /// Data directory resolved the same way as Paths.DataDir (Logging).
    /// </summary>
    public static string ResolveDataDir()
    {
        var env = Environment.GetEnvironmentVariable("AGENT_RECORDER_DATA_DIR");
        if (!string.IsNullOrWhiteSpace(env) && Path.IsPathFullyQualified(env))
            return env;
        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "AgentRecorder");
    }

    public string DataDir => ResolveDataDir();
    public string ReadyFilePath => Path.Combine(DataDir, "runtime", "ready.json");
    public string ApiKeyFilePath => ApiKeyAuth.GetTokenFilePath();
    public string AuditLogPathResolved => Path.Combine(DataDir, "logs", "audit.jsonl");

    /// <summary>
    /// Remove a stale ready.json from a previous run. Call at startup before MarkReady.
    /// Returns true if the file did not exist or was successfully deleted.
    /// </summary>
    public bool CleanupOldReadyFile()
    {
        try
        {
            var path = ReadyFilePath;
            if (File.Exists(path))
                File.Delete(path);
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Mark the service as ready: stop the timer, write ready.json atomically,
    /// and set the named event. Returns the snapshot for audit logging.
    /// </summary>
    public ReadySnapshot MarkReady()
    {
        _stopwatch.Stop();
        var elapsedMs = _stopwatch.ElapsedMilliseconds;
        var readyAt = DateTime.UtcNow;
        var startedAt = readyAt.AddMilliseconds(-elapsedMs);

        var snapshot = new ReadySnapshot
        {
            Ready = true,
            Pid = _pid,
            Port = _port,
            ApiVersion = "v1",
            Mode = _mode,
            StartedAt = startedAt.ToString("yyyy-MM-ddTHH:mm:ssZ"),
            ReadyAt = readyAt.ToString("yyyy-MM-ddTHH:mm:ssZ"),
            StartupElapsedMs = elapsedMs,
            DataDir = DataDir,
            ApiKeyFile = ApiKeyFilePath,
            AuditLogPath = AuditLogPathResolved,
            ReadyFile = ReadyFilePath,
            NamedEvent = NamedEventName
        };

        WriteReadyFileAtomic(snapshot);
        TrySetNamedEvent();
        _readyFileWritten = ReadyFilePath;

        return snapshot;
    }

    private void WriteReadyFileAtomic(ReadySnapshot snapshot)
    {
        var dir = Path.GetDirectoryName(ReadyFilePath)!;
        Directory.CreateDirectory(dir);

        var json = JsonSerializer.Serialize(snapshot, JsonOpts);
        var tempPath = ReadyFilePath + ".tmp";

        // Atomic write: write to temp, then move (replace existing).
        File.WriteAllText(tempPath, json);
        if (File.Exists(ReadyFilePath))
            File.Delete(ReadyFilePath);
        File.Move(tempPath, ReadyFilePath);
    }

    private void TrySetNamedEvent()
    {
        try
        {
            _namedEvent = new EventWaitHandle(false, EventResetMode.ManualReset, NamedEventName);
            _namedEvent.Set();
        }
        catch
        {
            // Named event is a bonus signal; failure is non-fatal.
        }
    }

    /// <summary>
    /// Returns a lightweight object suitable for inclusion in /capabilities.
    /// Never exposes the API key content, only the file path.
    /// </summary>
    public object ToCapabilitiesObject()
    {
        return new
        {
            ready = _readyFileWritten != null,
            pid = _pid,
            port = _port,
            api_version = "v1",
            mode = _mode,
            startup_elapsed_ms = _stopwatch.IsRunning ? (long?)null : _stopwatch.ElapsedMilliseconds,
            ready_file = ReadyFilePath,
            api_key_file = ApiKeyFilePath,
            named_event = NamedEventName
        };
    }

    /// <summary>
    /// Attempt to delete the ready file. Returns a result with success flag
    /// and error details so callers can log accurate audit events.
    /// </summary>
    public ReadyFileDeleteResult TryDeleteReadyFile()
    {
        if (_readyFileWritten == null)
            return new ReadyFileDeleteResult { Success = true, Path = ReadyFilePath, WasPresent = false };

        try
        {
            if (File.Exists(_readyFileWritten))
                File.Delete(_readyFileWritten);
            var result = new ReadyFileDeleteResult
            {
                Success = true,
                Path = _readyFileWritten,
                WasPresent = true
            };
            _readyFileWritten = null;
            return result;
        }
        catch (Exception ex)
        {
            return new ReadyFileDeleteResult
            {
                Success = false,
                Path = _readyFileWritten ?? ReadyFilePath,
                WasPresent = true,
                Error = ex.Message ?? ex.GetType().Name,
                ErrorType = ex.GetType().FullName ?? ex.GetType().Name
            };
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        // Delete ready file (best-effort, no throw).
        try { TryDeleteReadyFile(); } catch { }

        // Release named event handle.
        try { _namedEvent?.Dispose(); } catch { }
    }
}

/// <summary>
/// Serializable readiness snapshot written to ready.json.
/// </summary>
public sealed class ReadySnapshot
{
    public bool Ready { get; set; }
    public int Pid { get; set; }
    public int Port { get; set; }
    public string ApiVersion { get; set; } = "v1";
    public string Mode { get; set; } = "";
    public string StartedAt { get; set; } = "";
    public string ReadyAt { get; set; } = "";
    public long StartupElapsedMs { get; set; }
    public string DataDir { get; set; } = "";
    public string ApiKeyFile { get; set; } = "";
    public string AuditLogPath { get; set; } = "";
    public string ReadyFile { get; set; } = "";
    public string NamedEvent { get; set; } = "";
}

public sealed class ReadyFileDeleteResult
{
    public bool Success { get; set; }
    public bool WasPresent { get; set; }
    public string Path { get; set; } = "";
    public string? Error { get; set; }
    public string? ErrorType { get; set; }
}
