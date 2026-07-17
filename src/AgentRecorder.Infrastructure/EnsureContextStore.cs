using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;

namespace AgentRecorder.Infrastructure;

/// <summary>
/// Default implementation of <see cref="IEnsureContextStore"/>. Stores contexts
/// in <c>&lt;data-dir&gt;\runtime\ensure-contexts</c>, validates IDs with a strict
/// allowlist, and binds consumption to the current service instance via ready.json.
/// </summary>
public sealed class EnsureContextStore : IEnsureContextStore
{
    public const string ContextIdPrefix = "ensure_";
    public const int ContextIdHexLength = 32;
    public const int MaxContextIdLength = 39; // "ensure_" + 32 hex chars
    public const string ContextDirectoryName = "ensure-contexts";
    public const int MaxFileBytes = 4096;
    public const int DefaultTtlMinutes = 5;
    public const int DefaultMaxFiles = 100;
    public const long MaxElapsedMs = 3600_000; // 1 hour
    public const string HeaderName = "X-Agent-Recorder-Ensure-Context";

    // Internal temp file naming: "<contextId>-<guid>.tmp" inside the context directory.
    // The suffix is always appended so the file is not enumerated as a context.
    internal const string TempFileSuffix = ".tmp";

    private static readonly Regex ContextIdRegex = new(
        $"^{ContextIdPrefix}[0-9a-f]{{{ContextIdHexLength}}}$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private readonly string _dataDir;
    private readonly Func<DateTime> _utcNow;
    private readonly TimeSpan _ttl;
    private readonly int _maxFiles;
    private readonly object _consumeLock = new();

    /// <summary>
    /// Test-only seam for simulating file-move failures after the temp file has
    /// been written. Production code leaves this null and uses File.Move directly.
    /// The delegate receives (tempPath, destinationPath) and returns true if the
    /// move succeeded. Returning false causes TryCreate to clean up and return null.
    /// </summary>
    internal Action<string, string>? TestMoveFile { get; set; }
    // Bounded in-memory tombstone map: contextId -> expiresAtUtc. Mirrors the
    // file-system TTL so that repeated consumption attempts return Reused for
    // as long as the original context would have lived, then age out naturally.
    private readonly Dictionary<string, DateTime> _tombstones = new();

    public EnsureContextStore(string dataDir, Func<DateTime>? utcNow = null, TimeSpan? ttl = null, int? maxFiles = null)
    {
        if (string.IsNullOrWhiteSpace(dataDir))
            throw new ArgumentException("Data directory must not be empty.", nameof(dataDir));
        _dataDir = dataDir;
        _utcNow = utcNow ?? (() => DateTime.UtcNow);
        _ttl = ttl ?? TimeSpan.FromMinutes(DefaultTtlMinutes);
        _maxFiles = maxFiles ?? DefaultMaxFiles;
    }

    public string ContextDirectory => Path.Combine(_dataDir, "runtime", ContextDirectoryName);

    public static string GenerateContextId()
    {
        // 128 bits of randomness -> 32 hex chars.
        var bytes = RandomNumberGenerator.GetBytes(ContextIdHexLength / 2);
        var sb = new StringBuilder(MaxContextIdLength);
        sb.Append(ContextIdPrefix);
        foreach (var b in bytes)
            sb.Append(b.ToString("x2"));
        return sb.ToString();
    }

    public string? TryCreate(EnsureContext context)
    {
        if (!IsValidContextId(context.EnsureContextId))
            return null;

        var dir = ContextDirectory;
        string? tempPath = null;
        try
        {
            Directory.CreateDirectory(dir);

            CleanupExpiredFiles(dir);
            CleanupExpiredTempFiles(dir);

            var path = GetContextFilePath(context.EnsureContextId);

            // An existing file at the target path means we generated a duplicate
            // context ID (128-bit collision) or a stale file was left behind.
            // Do not overwrite; treat as a diagnostic failure.
            if (File.Exists(path))
                return null;

            var json = JsonSerializer.Serialize(context, new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
                WriteIndented = false
            });

            // Atomic write: temp file with a unique name, then move.
            tempPath = Path.Combine(dir, $".tmp-{context.EnsureContextId}-{Guid.NewGuid():N}{TempFileSuffix}");
            File.WriteAllText(tempPath, json, Encoding.UTF8);

            if (TestMoveFile != null)
                TestMoveFile(tempPath, path);
            else
                File.Move(tempPath, path);

            // A test seam or unusual filesystem must not report success without
            // actually placing the final context file.
            if (!File.Exists(path))
                return null;

            tempPath = null; // move succeeded; do not delete in finally.

            // Align filesystem timestamp with the context's logical creation time
            // so that injected clocks can drive TTL deterministically in tests.
            try { File.SetCreationTimeUtc(path, context.CreatedAtUtc); }
            catch { /* non-critical: fallback to filesystem time */ }

            // Enforce the count limit after the new file exists so that creation
            // of a new context always triggers eviction of the oldest one.
            try { EnforceCountLimit(dir); }
            catch { /* non-critical */ }

            return File.Exists(path) ? context.EnsureContextId : null;
        }
        catch
        {
            // Context creation is diagnostic; failures must not break ensure-running.
            return null;
        }
        finally
        {
            // Ensure a partially-written temp file is never left behind.
            if (tempPath != null)
            {
                try { if (File.Exists(tempPath)) File.Delete(tempPath); }
                catch { }
            }
        }
    }

    public EnsureContextResult TryConsume(string contextId)
    {
        lock (_consumeLock)
        {
            CleanupTombstones();

            if (!IsValidContextId(contextId))
                return EnsureContextResult.Failed(EnsureContextStatus.Invalid);

            if (_tombstones.ContainsKey(contextId))
                return EnsureContextResult.Failed(EnsureContextStatus.Reused, contextId);

            var path = GetContextFilePath(contextId);
            if (!File.Exists(path))
                return EnsureContextResult.Failed(EnsureContextStatus.Missing, contextId);

            try
            {
                var fileInfo = new FileInfo(path);
                if (!fileInfo.Exists)
                    return EnsureContextResult.Failed(EnsureContextStatus.Missing, contextId);

                var now = _utcNow();
                if (fileInfo.CreationTimeUtc == default || now - fileInfo.CreationTimeUtc > _ttl)
                {
                    TryDelete(path);
                    return EnsureContextResult.Failed(EnsureContextStatus.Expired, contextId);
                }

                CleanupExpiredFiles(ContextDirectory);

                if (fileInfo.Length > MaxFileBytes)
                {
                    TryDelete(path);
                    return EnsureContextResult.Failed(EnsureContextStatus.Invalid, contextId);
                }

                var json = File.ReadAllText(path, Encoding.UTF8);
                var context = JsonSerializer.Deserialize<EnsureContext>(json, new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true,
                    PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower
                });

                if (context == null || context.SchemaVersion != 1)
                {
                    TryDelete(path);
                    return EnsureContextResult.Failed(EnsureContextStatus.Invalid, contextId);
                }

                if (!string.Equals(context.EnsureContextId, contextId, StringComparison.Ordinal))
                {
                    TryDelete(path);
                    return EnsureContextResult.Failed(EnsureContextStatus.Invalid, contextId);
                }

                if (!IsValidStartupKind(context.StartupKind))
                {
                    TryDelete(path);
                    return EnsureContextResult.Failed(EnsureContextStatus.Invalid, contextId);
                }

                if (context.EnsureElapsedMs < 0 || context.EnsureElapsedMs > MaxElapsedMs ||
                    context.ServiceStartupElapsedMs < 0 || context.ServiceStartupElapsedMs > MaxElapsedMs)
                {
                    TryDelete(path);
                    return EnsureContextResult.Failed(EnsureContextStatus.Invalid, contextId);
                }

                if (context.CreatedAtUtc == default || now - context.CreatedAtUtc > _ttl)
                {
                    TryDelete(path);
                    return EnsureContextResult.Failed(EnsureContextStatus.Expired, contextId);
                }

                if (!ValidateInstanceIdentity(context))
                {
                    TryDelete(path);
                    return EnsureContextResult.Failed(EnsureContextStatus.InstanceMismatch, contextId);
                }

                // One-time consumption: the file must actually be deleted before we
                // claim success. If deletion fails, return Unavailable and do not
                // record a tombstone, so a retry or concurrent call cannot obtain a
                // second successful consumption.
                if (!TryVerifiedDelete(path))
                {
                    return EnsureContextResult.Failed(EnsureContextStatus.Unavailable, contextId);
                }

                // Record a bounded tombstone so in-process reuse returns Reused
                // for the remainder of the original TTL.
                _tombstones[contextId] = now + _ttl;
                EnforceTombstoneLimit();

                return EnsureContextResult.Consumed(context);
            }
            catch (JsonException)
            {
                TryDelete(path);
                return EnsureContextResult.Failed(EnsureContextStatus.Invalid, contextId);
            }
            catch
            {
                TryDelete(path);
                return EnsureContextResult.Failed(EnsureContextStatus.Unavailable, contextId);
            }
        }
    }

    internal static bool IsValidContextId(string? contextId)
    {
        if (string.IsNullOrWhiteSpace(contextId))
            return false;
        if (contextId.Length != MaxContextIdLength)
            return false;
        if (!ContextIdRegex.IsMatch(contextId))
            return false;
        return true;
    }

    internal static bool IsValidStartupKind(string? kind)
    {
        return kind == "cold" || kind == "warm";
    }

    private string GetContextFilePath(string contextId)
    {
        // contextId is allowlist-validated before this is called.
        return Path.Combine(ContextDirectory, $"{contextId}.json");
    }

    private bool ValidateInstanceIdentity(EnsureContext context)
    {
        try
        {
            var readyPath = Path.Combine(_dataDir, "runtime", "ready.json");
            if (!File.Exists(readyPath))
                return false;

            var json = File.ReadAllText(readyPath, Encoding.UTF8);
            var ready = JsonSerializer.Deserialize<ReadySnapshot>(json, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
                PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower
            });

            if (ready == null)
                return false;

            // Bind to both PID and ready_at. ready_at is a stable instance identity
            // that survives PID reuse across service restarts.
            if (ready.Pid != context.ServicePid)
                return false;

            if (!string.Equals(ready.ReadyAt, context.ServiceReadyAt, StringComparison.Ordinal))
                return false;

            return true;
        }
        catch
        {
            return false;
        }
    }

    private void CleanupExpiredFiles(string dir)
    {
        try
        {
            if (!Directory.Exists(dir))
                return;

            var cutoff = _utcNow() - _ttl;
            foreach (var file in Directory.EnumerateFiles(dir, "*.json"))
            {
                try
                {
                    var created = File.GetCreationTimeUtc(file);
                    if (created < cutoff)
                        File.Delete(file);
                }
                catch { }
            }
        }
        catch { }
    }

    private void CleanupExpiredTempFiles(string dir)
    {
        try
        {
            if (!Directory.Exists(dir))
                return;

            var cutoff = _utcNow() - _ttl;
            foreach (var file in Directory.EnumerateFiles(dir, $".tmp-*{TempFileSuffix}"))
            {
                try
                {
                    var created = File.GetCreationTimeUtc(file);
                    if (created < cutoff)
                        File.Delete(file);
                }
                catch { }
            }
        }
        catch { }
    }

    private void EnforceCountLimit(string dir)
    {
        try
        {
            var files = Directory.EnumerateFiles(dir, "*.json")
                .Select(f => new FileInfo(f))
                .OrderBy(fi => fi.CreationTimeUtc)
                .ThenBy(fi => fi.LastWriteTimeUtc)
                .ThenBy(fi => fi.FullName, StringComparer.Ordinal)
                .ToList();

            while (files.Count > _maxFiles)
            {
                try
                {
                    files[0].Delete();
                }
                catch { }
                files.RemoveAt(0);
            }
        }
        catch { }
    }

    private void CleanupTombstones()
    {
        var now = _utcNow();
        var expired = _tombstones
            .Where(kvp => kvp.Value <= now)
            .Select(kvp => kvp.Key)
            .ToList();

        foreach (var id in expired)
            _tombstones.Remove(id);
    }

    private void EnforceTombstoneLimit()
    {
        if (_tombstones.Count <= _maxFiles)
            return;

        var toRemove = _tombstones
            .OrderBy(kvp => kvp.Value)
            .ThenBy(kvp => kvp.Key, StringComparer.Ordinal)
            .Take(_tombstones.Count - _maxFiles)
            .Select(kvp => kvp.Key)
            .ToList();

        foreach (var id in toRemove)
            _tombstones.Remove(id);
    }

    private static bool TryVerifiedDelete(string path)
    {
        try
        {
            if (!File.Exists(path))
                return true;
            File.Delete(path);
            return !File.Exists(path);
        }
        catch
        {
            // If deletion threw and the file is still present, the claim failed.
            return !File.Exists(path);
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch { }
    }
}
