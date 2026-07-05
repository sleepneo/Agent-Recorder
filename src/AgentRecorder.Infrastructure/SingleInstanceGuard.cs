using System;
using System.IO;
using System.Text.Json;
using System.Threading;

namespace AgentRecorder.Infrastructure;

/// <summary>
/// Cross-process single-instance guard using a Windows named mutex.
/// Ensures only one Agent Recorder API service runs per user session.
/// Must be acquired BEFORE ready-file cleanup to prevent the second instance
/// from deleting the first instance's ready.json.
/// </summary>
public sealed class SingleInstanceGuard : IDisposable
{
    public const string MutexName = @"Local\AgentRecorder.Service.Instance";

    private Mutex? _mutex;
    private string _mutexName = MutexName;
    private bool _acquired;
    private bool _disposed;

    public string Mutex => _mutexName;
    public bool IsAcquired => _acquired;

    /// <summary>
    /// Try to acquire the single-instance mutex. Returns true if this is the
    /// first instance, false if another instance already holds the mutex.
    /// </summary>
    public static SingleInstanceGuard TryAcquire() => TryAcquire(MutexName);

    /// <summary>
    /// Try to acquire a single-instance mutex with a custom name (for testing).
    /// </summary>
    public static SingleInstanceGuard TryAcquire(string mutexName)
    {
        var guard = new SingleInstanceGuard();
        guard._mutexName = mutexName;
        try
        {
            guard._mutex = new Mutex(initiallyOwned: true, mutexName, out bool createdNew);
            if (createdNew)
            {
                guard._acquired = true;
            }
            else
            {
                // Mutex existed; we did not acquire it.
                guard._acquired = false;
                guard._mutex.Dispose();
                guard._mutex = null;
            }
        }
        catch (AbandonedMutexException)
        {
            // Previous instance crashed without releasing the mutex.
            // We now own it; treat as acquired.
            guard._acquired = true;
        }
        catch
        {
            // If we can't even create/open the mutex, fall through to not-acquired
            // so the caller can decide what to do.
            guard._acquired = false;
            guard._mutex?.Dispose();
            guard._mutex = null;
        }
        return guard;
    }

    /// <summary>
    /// Read the existing ready.json to find out about the running instance, if any.
    /// Returns null if the ready file doesn't exist or can't be parsed.
    /// </summary>
    public static ReadySnapshot? ReadExistingReadyFile(string dataDir)
    {
        try
        {
            var readyPath = Path.Combine(dataDir, "runtime", "ready.json");
            if (!File.Exists(readyPath)) return null;

            var json = File.ReadAllText(readyPath);
            var snapshot = JsonSerializer.Deserialize<ReadySnapshot>(json, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
                PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower
            });
            return snapshot;
        }
        catch
        {
            return null;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        try
        {
            if (_acquired && _mutex != null)
            {
                _mutex.ReleaseMutex();
            }
        }
        catch { }

        try { _mutex?.Dispose(); } catch { }
        _mutex = null;
        _acquired = false;
    }
}
