using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace AgentRecorder.Logging;

/// <summary>
/// Non-blocking rolling JSONL writer. Uses a single background task to flush
/// a bounded queue to disk. Rolling and write failures are isolated so they
/// do not affect callers.
/// </summary>
public sealed class RollingJsonlWriter : IDisposable
{
    private readonly string _basePath;
    private readonly long _maxFileSize;
    private readonly int _maxHistoryFiles;
    private readonly BlockingCollection<string> _queue;
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _writerTask;
    private readonly object _faultLock = new();
    private readonly ManualResetEventSlim _processGate = new(initialState: true);
    private long _droppedCount;
    private long _enqueueCount;
    private long _processedCount;
    // 0 = false, 1 = true. Interlocked.Exchange makes Dispose idempotent.
    private int _disposed;

    public RollingJsonlWriter(string basePath, long maxFileSize = 5L * 1024 * 1024, int maxHistoryFiles = 3, int boundedCapacity = 10000)
    {
        if (string.IsNullOrWhiteSpace(basePath))
            throw new ArgumentException("Base path must not be empty.", nameof(basePath));
        _basePath = basePath;
        _maxFileSize = maxFileSize;
        _maxHistoryFiles = Math.Max(0, maxHistoryFiles);
        _queue = new BlockingCollection<string>(boundedCapacity);
        _writerTask = Task.Run(ProcessQueue);
    }

    public string BasePath => _basePath;

    /// <summary>Number of lines dropped because the queue was full.</summary>
    public long DroppedCount => Interlocked.Read(ref _droppedCount);

    /// <summary>
    /// Test seam: if greater than zero, the next N appends will throw before
    /// writing, simulating a write fault. The writer must continue operating
    /// once the counter returns to zero.
    /// </summary>
    internal int FailNextNAppends { get; set; }

    /// <summary>
    /// Test seam: pause the background consumer before writing the next line.
    /// Default gate state is signalled, so production code has zero wait.
    /// </summary>
    internal void PauseProcessing() => _processGate.Reset();

    /// <summary>Test seam: resume the background consumer.</summary>
    internal void ResumeProcessing() => _processGate.Set();

    /// <summary>Try to enqueue a line without blocking the caller.</summary>
    public void Enqueue(string line)
    {
        if (Volatile.Read(ref _disposed) == 1 || _queue.IsAddingCompleted)
            return;

        // Count the line as accepted first; decrement on failure so the count
        // is always >= accepted lines. Flush/Dispose may briefly over-wait but
        // never return before accepted lines are processed.
        Interlocked.Increment(ref _enqueueCount);
        try
        {
            if (!_queue.TryAdd(line, 0))
            {
                Interlocked.Increment(ref _droppedCount);
                Interlocked.Decrement(ref _enqueueCount);
            }
        }
        catch
        {
            // Adding completed, cancellation during Dispose, or any other race:
            // treat as dropped and keep counts self-consistent.
            Interlocked.Increment(ref _droppedCount);
            Interlocked.Decrement(ref _enqueueCount);
        }
    }

    /// <summary>
    /// Synchronously write one line, intended for tests that need deterministic
    /// file content without waiting for the background task.
    /// </summary>
    internal void WriteLineSynchronously(string line)
    {
        try
        {
            lock (_faultLock)
            {
                if (FailNextNAppends > 0)
                {
                    FailNextNAppends--;
                    throw new IOException("Simulated write fault injected by test.");
                }
            }

            var dir = Path.GetDirectoryName(_basePath);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);
            RollIfNeeded();
            File.AppendAllText(_basePath, line + Environment.NewLine);
        }
        catch
        {
            // Isolate failures: the caller's main flow must not be affected.
        }
    }

    private void ProcessQueue()
    {
        try
        {
            foreach (var line in _queue.GetConsumingEnumerable(_cts.Token))
            {
                // Test-controlled gate. Signalled by default, so production path
                // has no additional wait.
                _processGate.Wait(_cts.Token);
                WriteLineSynchronously(line);
                Interlocked.Increment(ref _processedCount);
            }
        }
        catch (OperationCanceledException)
        {
            // Expected during dispose when draining exceeds the timeout.
        }
    }

    private void RollIfNeeded()
    {
        if (!File.Exists(_basePath))
            return;

        var fi = new FileInfo(_basePath);
        if (fi.Length < _maxFileSize)
            return;

        var ext = Path.GetExtension(_basePath);
        var stem = _basePath.Substring(0, _basePath.Length - ext.Length);

        if (_maxHistoryFiles == 0)
        {
            // No history retention: discard the current file and start fresh.
            File.Delete(_basePath);
            return;
        }

        // Shift existing history files: .(n-1) -> .n, ..., .1 -> .2, base -> .1
        for (int i = _maxHistoryFiles - 1; i >= 1; i--)
        {
            var src = $"{stem}.{i}{ext}";
            var dst = $"{stem}.{i + 1}{ext}";
            if (File.Exists(src))
            {
                if (i == _maxHistoryFiles - 1 && File.Exists(dst))
                    File.Delete(dst);
                File.Move(src, dst, overwrite: true);
            }
        }

        var firstHistory = $"{stem}.1{ext}";
        File.Move(_basePath, firstHistory, overwrite: true);
    }

    /// <summary>
    /// Wait for currently queued lines to be written. The writer remains usable
    /// after flush; only <see cref="Dispose"/> stops accepting new data.
    /// </summary>
    public void Flush(TimeSpan? timeout = null)
    {
        if (Volatile.Read(ref _disposed) == 1 || _queue.IsAddingCompleted)
            return;

        var target = Interlocked.Read(ref _enqueueCount);
        var sw = Stopwatch.StartNew();
        var waitTimeout = timeout ?? TimeSpan.FromSeconds(2);
        while (sw.Elapsed < waitTimeout && !_queue.IsCompleted)
        {
            var processed = Interlocked.Read(ref _processedCount);
            if (processed >= target)
                break;
            Thread.Sleep(5);
        }
    }

    /// <summary>
    /// Stop accepting data, drain the queue with a bounded wait, and shut down
    /// the background writer.
    /// </summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
            return;

        try { _queue.CompleteAdding(); } catch { }

        // Drain already-accepted lines before cancelling. Use the queue's own
        // completion signal as the primary source of truth, with a bounded
        // fallback to cancellation so application exit cannot hang.
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed < TimeSpan.FromSeconds(2) && !_queue.IsCompleted)
        {
            var processed = Interlocked.Read(ref _processedCount);
            var enqueued = Interlocked.Read(ref _enqueueCount);
            if (processed >= enqueued)
                break;
            Thread.Sleep(5);
        }

        if (!_queue.IsCompleted)
        {
            try { _cts.Cancel(); } catch { }
        }

        try { _writerTask.Wait(TimeSpan.FromSeconds(1)); } catch { }
        _cts.Dispose();
    }

    /// <summary>Returns the paths of all current history files (base + .1, .2, ...).</summary>
    internal IReadOnlyList<string> GetExistingFiles()
    {
        var result = new List<string>();
        if (File.Exists(_basePath))
            result.Add(_basePath);

        var ext = Path.GetExtension(_basePath);
        var stem = _basePath.Substring(0, _basePath.Length - ext.Length);
        for (int i = 1; ; i++)
        {
            var path = $"{stem}.{i}{ext}";
            if (!File.Exists(path))
                break;
            result.Add(path);
        }
        return result;
    }
}
