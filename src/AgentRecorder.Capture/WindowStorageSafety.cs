using System.Diagnostics;
using System.Text.Json;
using AgentRecorder.Infrastructure;

namespace AgentRecorder.Capture;

// These paths are application-owned. They are never read from a request body.
internal sealed record CaptureWritePaths(string OutputDirectory, string? AvTempDirectory, string? WgcTempRoot)
{
    // FFmpeg video-only owns its stage beneath the approved output directory.
    // No WGC or AV temporary location is used by this layout.
    internal static CaptureWritePaths FreezeFfmpegVideoOnly(CaptureConfig config, string? approvedOutputPath = null) => new(
        Path.GetDirectoryName(Path.GetFullPath(approvedOutputPath ?? config.OutputPath))
            ?? throw new StorageSafetyException(WindowStorageSafety.UnavailableCode), null, null);
    internal static CaptureWritePaths FreezeFfmpegAv(CaptureConfig config, string? approvedOutputPath = null) => new(
        Path.GetDirectoryName(Path.GetFullPath(approvedOutputPath ?? config.OutputPath))
            ?? throw new StorageSafetyException(WindowStorageSafety.UnavailableCode),
        Path.GetFullPath(ResolveAvTempDirectory()), null);
    internal static string ResolveAvTempDirectory() => Path.Combine(DataDirResolver.Resolve(), "temp");
    internal static string ResolveWgcTempRoot() => Path.Combine(
        Environment.GetEnvironmentVariable("TEMP") ?? Path.GetTempPath(), "AgentRecorder");

    internal static CaptureWritePaths Freeze(CaptureConfig config, string? approvedOutputPath = null) => new(
        Path.GetDirectoryName(Path.GetFullPath(approvedOutputPath ?? config.OutputPath))
            ?? throw new InvalidOperationException("storage_capacity_unavailable"),
        config.IsSystemLoopback ? Path.GetFullPath(ResolveAvTempDirectory()) : null,
        Path.GetFullPath(ResolveWgcTempRoot()));

    internal string[] Directories => new[] { OutputDirectory, AvTempDirectory, WgcTempRoot }
        .OfType<string>().ToArray();

    internal void ValidateOutputPath(CaptureConfig config)
    {
        var expected = config.StorageIntermediatePublication ? AvTempDirectory : OutputDirectory;
        if (expected is null || !string.Equals(expected,
                Path.GetDirectoryName(Path.GetFullPath(config.OutputPath)), StringComparison.OrdinalIgnoreCase))
            throw new StorageSafetyException(WindowStorageSafety.UnavailableCode);
    }
}

internal sealed record StorageVolumeSample(string Path, string VolumeIdentity, long AvailableBytes);

internal interface IStorageCapacityProvider
{
    Task<IReadOnlyList<StorageVolumeSample>> QueryAsync(IReadOnlyList<string> paths, CancellationToken token);
}

// A blocking filesystem/redirector query runs in a disposable child process,
// never on the UI or a permanently blocked ThreadPool thread. Cancellation and
// the deadline kill only this probe child. No capture or UI is initialized there.
internal sealed class ProcessStorageCapacityProvider : IStorageCapacityProvider
{
    internal int LastProbeProcessId { get; private set; }
    internal Func<string> ExecutablePath { get; init; } = () =>
        Path.Combine(AppContext.BaseDirectory,
            System.Reflection.Assembly.GetEntryAssembly()?.GetName().Name == "AgentRecorder.Headless"
                ? "AgentRecorder.Headless.exe" : "AgentRecorder.App.exe");

    public async Task<IReadOnlyList<StorageVolumeSample>> QueryAsync(
        IReadOnlyList<string> paths, CancellationToken token)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo(ExecutablePath())
            {
                UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardInput = true, RedirectStandardOutput = true,
                RedirectStandardError = false,
                Arguments = "--internal-storage-capacity-probe"
            }
        };
        process.Start();
        LastProbeProcessId = process.Id;
        using var registration = token.Register(() => { try { process.Kill(); } catch { } });
        try
        {
            await process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(paths).AsMemory(), token).ConfigureAwait(false);
            process.StandardInput.Close();
            // Input and response are bounded (at most three directory samples).
            var buffer = new char[32768];
            int count = 0;
            while (count < buffer.Length)
            {
                int read = await process.StandardOutput.ReadAsync(buffer.AsMemory(count), token).ConfigureAwait(false);
                if (read == 0) break;
                count += read;
            }
            if (count == buffer.Length) throw new IOException("storage_probe_response_too_large");
            await process.WaitForExitAsync(token).ConfigureAwait(false);
            if (process.ExitCode != 0) throw new IOException("storage_capacity_unavailable");
            return JsonSerializer.Deserialize<StorageVolumeSample[]>(new string(buffer, 0, count))
                ?? throw new IOException("storage_capacity_unavailable");
        }
        finally
        {
            try { if (!process.HasExited) process.Kill(); } catch { }
            try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromMilliseconds(500)).ConfigureAwait(false); } catch { }
        }
    }
}

internal sealed class StorageSafetyException(string code) : IOException(code)
{
    internal string Code { get; } = code;
}

/// <summary>One finite monitor and one publish/abort arbiter per strict window run.</summary>
internal sealed class WindowStorageSafety : IDisposable
{
    internal const long LowWaterBytes = 256L * 1024 * 1024;
    internal const string LowSpaceCode = "storage_space_low";
    internal const string UnavailableCode = "storage_capacity_unavailable";
    private readonly object _gate = new();
    private readonly CaptureWritePaths _paths;
    private readonly IStorageCapacityProvider _provider;
    private readonly int _durationSeconds;
    private readonly CancellationTokenSource _cts = new();
    private readonly CancellationTokenSource _abortCts = new();
    private readonly CancellationToken _abortToken;
    private Dictionary<string, string>? _pathVolumes;
    private enum PublicationMode { Legacy, FfmpegVideoOnly, FfmpegAv }
    private PublicationMode _publicationMode;
    private bool TracksCopy => _publicationMode != PublicationMode.Legacy;
    internal bool IsFfmpegAvPublication => _publicationMode == PublicationMode.FfmpegAv;
    private long _publicationCopyBytes;
    private readonly SemaphoreSlim _copySamplingGate = new(1, 1);
    private CopyReservation? _activeCopy;
    internal Action? AfterPhysicalCopyWriteForTests { get; set; }
    internal long RemainingPublicationCopyBytes { get { lock (_gate) return _publicationCopyBytes; } }
    private bool _committed;
    private bool _disposed;
    private string? _failure;
    private Task? _monitor;
    private Action<string>? _onFailure;
    internal Func<TimeSpan, CancellationToken, Task> Delay { get; init; } = Task.Delay;
    internal TimeSpan QueryTimeout { get; init; } = TimeSpan.FromSeconds(2);
    internal Action? BeforeFailureClaimForTests { get; set; }
    internal Task MonitorCompletion => _monitor ?? Task.CompletedTask;
    internal string? FailureCode { get { lock (_gate) return _failure; } }
    internal bool Committed { get { lock (_gate) return _committed; } }
    internal CancellationToken AbortToken => _abortToken;
    internal CaptureWritePaths Paths => _paths;

    internal WindowStorageSafety(CaptureWritePaths paths, int durationSeconds, IStorageCapacityProvider provider)
    {
        _paths = paths;
        _durationSeconds = durationSeconds;
        _provider = provider;
        _abortToken = _abortCts.Token;
    }

    internal static long VideoEstimate(long durationSeconds) => durationSeconds > 0
        ? Math.Max(100L * 1024 * 1024, checked(durationSeconds * 2 * 1024 * 1024))
        : 100L * 1024 * 1024;

    // Sum simultaneous files per actual volume, not per drive letter. For
    // loopback: WGC video + AV adapter video + a separate PCM estimate using
    // the existing conservative media rate (not an assumed endpoint format)
    // + output mux partial and publication copy. Video-only: WGC video
    // + publication copy. The watermark remains available above these estimates.
    // The FFmpeg stage is on the output volume, which may differ from WGC temp.
    internal void ReserveFfmpegVideoOnlyPublication()
    {
        lock (_gate)
        {
            _publicationMode = PublicationMode.FfmpegVideoOnly;
            _publicationCopyBytes = VideoEstimate(_durationSeconds);
        }
    }

    // AV admission already includes mux + copy on output and video + PCM on AV temp.
    // Runtime copy demand starts only when the actual validated mux source exists.
    internal void EnableFfmpegAvPublication()
    {
        lock (_gate) _publicationMode = PublicationMode.FfmpegAv;
    }

    internal void ReserveAvPublicationCopy(long sourceBytes)
    {
        if (_publicationMode == PublicationMode.FfmpegAv) ReservePublicationCopy(sourceBytes);
    }

    internal void ReservePublicationCopy(long sourceBytes)
    {
        lock (_gate)
        {
            if (_activeCopy is not null) throw new InvalidOperationException("A publication copy is already active.");
            _publicationCopyBytes = Math.Max(_publicationCopyBytes, sourceBytes);
        }
    }

    internal void EndFfmpegVideoOnlyPublication()
    {
        lock (_gate) { if (_activeCopy is null) _publicationCopyBytes = 0; }
    }

    private async Task<IPublicationCopyReservation?> BeginCopyAsync(long bytes, CancellationToken token)
    {
        lock (_gate) { if (!TracksCopy) return null; }
        await _copySamplingGate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            lock (_gate)
            {
                if (_disposed || _failure is not null || _committed) throw new OperationCanceledException();
                if (_activeCopy is not null) throw new InvalidOperationException("A publication copy is already active.");
                _publicationCopyBytes = bytes; // Actual source size is now known; all bytes remain unwritten.
                return _activeCopy = new CopyReservation(this);
            }
        }
        finally { _copySamplingGate.Release(); }
    }

    private sealed class CopyReservation(WindowStorageSafety owner) : IPublicationCopyReservation
    {
        public async Task WriteAsync(FileStream destination, ReadOnlyMemory<byte> bytes, CancellationToken token)
        {
            await owner._copySamplingGate.WaitAsync(token).ConfigureAwait(false);
            try
            {
                lock (owner._gate)
                {
                    if (owner._disposed || owner._failure is not null || !ReferenceEquals(owner._activeCopy, this)) throw new OperationCanceledException();
                    if (bytes.Length > owner._publicationCopyBytes) throw new IOException("Publication source length changed.");
                }
                await destination.WriteAsync(bytes, token).ConfigureAwait(false);
                // Flush the managed buffer to the original native handle before reducing demand.
                // The existing final Flush(true) still establishes durability before rename.
                await destination.FlushAsync(token).ConfigureAwait(false);
                owner.AfterPhysicalCopyWriteForTests?.Invoke();
                lock (owner._gate) owner._publicationCopyBytes -= bytes.Length;
            }
            finally { owner._copySamplingGate.Release(); }
        }

        public async Task EnsureCapacityAsync(CancellationToken token)
        {
            var code = await owner.CheckAsync(false, token).ConfigureAwait(false);
            if (code is not null) { owner.BeforeFailureClaimForTests?.Invoke(); owner.TryAbort(code); }
            lock (owner._gate)
            {
                if (owner._failure is not null) throw new StorageSafetyException(owner._failure);
                if (owner._disposed) throw new OperationCanceledException();
            }
            token.ThrowIfCancellationRequested();
        }

        public void Dispose()
        {
            owner._copySamplingGate.Wait();
            try
            {
                lock (owner._gate)
                {
                    if (ReferenceEquals(owner._activeCopy, this)) { owner._activeCopy = null; owner._publicationCopyBytes = 0; }
                }
            }
            finally { owner._copySamplingGate.Release(); }
        }
    }

    internal Dictionary<string, long> AdmissionBudgets(IReadOnlyList<StorageVolumeSample> samples)
    {
        var video = VideoEstimate(_durationSeconds);
        var components = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase)
        {
            [_paths.OutputDirectory] = checked(video * (_paths.AvTempDirectory is null ? 1 : 2)),
        };
        if (_publicationMode == PublicationMode.FfmpegVideoOnly) AddComponent(components, _paths.OutputDirectory, video);
        if (_paths.WgcTempRoot is { } wgc) AddComponent(components, wgc, video);
        if (_paths.AvTempDirectory is { } av)
            AddComponent(components, av, checked(video * 2 + 4096));
        var budgets = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in components)
        {
            var volume = samples.Single(s => string.Equals(s.Path, pair.Key, StringComparison.OrdinalIgnoreCase)).VolumeIdentity;
            if (!budgets.ContainsKey(volume)) budgets[volume] = LowWaterBytes;
            budgets[volume] = checked(budgets[volume] + pair.Value);
        }
        return budgets;
    }

    private static void AddComponent(Dictionary<string, long> values, string path, long bytes)
        => values[path] = checked(values.GetValueOrDefault(path) + bytes);

    internal async Task<string?> CheckAsync(bool admission, CancellationToken token = default)
    {
        lock (_gate)
        {
            if (_disposed || _committed) return null;
            if (_failure is not null) return _failure;
        }
        var candidate = await ProbeAsync(admission, token).ConfigureAwait(false);
        // Every outcome, including invalid samples, exceptions and timeouts,
        // is advisory until checked against the same terminal arbiter.
        lock (_gate)
            return _committed || _disposed ? null : _failure ?? candidate;
    }

    private async Task<string?> ProbeAsync(bool admission, CancellationToken token)
    {
        bool samplingHeld = false;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token, _cts.Token);
            timeout.CancelAfter(QueryTimeout);
            // Serialize only this copy's native writes with its capacity sample. No recording/UI lock is held.
            if (TracksCopy)
            {
                await _copySamplingGate.WaitAsync(timeout.Token).ConfigureAwait(false);
                samplingHeld = true;
            }
            var paths = _paths.Directories.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            var samples = await _provider.QueryAsync(paths, timeout.Token)
                .WaitAsync(timeout.Token).ConfigureAwait(false);
            if (samples.Count != paths.Length || samples.Any(s => s is null ||
                s.AvailableBytes < 0 || string.IsNullOrWhiteSpace(s.VolumeIdentity)) ||
                paths.Any(p => samples.Count(s => string.Equals(s.Path, p, StringComparison.OrdinalIgnoreCase)) != 1))
                return UnavailableCode;
            lock (_gate)
            {
                if (_disposed || _committed) return null;
                if (_failure is not null) return _failure;
                if (_pathVolumes is not null && samples.Any(s =>
                    !string.Equals(_pathVolumes[s.Path], s.VolumeIdentity, StringComparison.OrdinalIgnoreCase)))
                    return UnavailableCode;
                _pathVolumes ??= samples.ToDictionary(s => s.Path, s => s.VolumeIdentity, StringComparer.OrdinalIgnoreCase);
            }
            var budgets = admission ? AdmissionBudgets(samples) : null;
            foreach (var volume in samples.GroupBy(s => s.VolumeIdentity, StringComparer.OrdinalIgnoreCase))
            {
                // Account-specific free space: take the conservative sample
                // if several paths on one volume were observed during a query.
                long minimum = budgets?[volume.Key] ?? LowWaterBytes;
                if (!admission && TracksCopy && volume.Any(s =>
                    string.Equals(s.Path, _paths.OutputDirectory, StringComparison.OrdinalIgnoreCase)))
                    minimum = checked(minimum + _publicationCopyBytes);
                if (volume.Min(s => s.AvailableBytes) < minimum)
                    return LowSpaceCode;
            }
            return null;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested || _cts.IsCancellationRequested) { return null; }
        catch { return UnavailableCode; }
        finally { if (samplingHeld) _copySamplingGate.Release(); }
    }

    internal void EnsureAdmission() => EnsureCapacity(admission: true);

    internal void EnsureRuntimeCapacity() => EnsureCapacity(admission: false);

    private void EnsureCapacity(bool admission)
    {
        lock (_gate)
        {
            if (_committed) return;
            if (_failure is not null) throw new StorageSafetyException(_failure);
            if (_disposed) throw new OperationCanceledException();
        }
        var code = CheckAsync(admission).GetAwaiter().GetResult();
        if (code is not null)
        {
            BeforeFailureClaimForTests?.Invoke();
            TryAbort(code);
        }
        // A commit or Dispose can win after CheckAsync calculated the result
        // but before TryAbort claimed it. Throw only the arbiter's failure,
        // never the losing probe's candidate. No lock is held while querying.
        lock (_gate)
        {
            if (_committed) return;
            if (_failure is not null) throw new StorageSafetyException(_failure);
            if (_disposed) throw new OperationCanceledException();
        }
    }

    internal void Start(Action<string> onFailure)
    {
        lock (_gate)
        {
            if (_monitor is not null || _disposed) return;
            _onFailure = onFailure;
            _monitor = Task.Run(async () =>
            {
                try
                {
                    while (!_cts.IsCancellationRequested)
                    {
                        await Delay(TimeSpan.FromSeconds(5), _cts.Token).ConfigureAwait(false);
                        var code = await CheckAsync(admission: false, _cts.Token).ConfigureAwait(false);
                        if (code is null) continue;
                        TryAbort(code);
                        return;
                    }
                }
                catch (OperationCanceledException) { }
                catch
                {
                    TryAbort(UnavailableCode);
                }
            });
        }
    }

    internal bool TryAbort(string code)
    {
        if (code != LowSpaceCode && code != UnavailableCode) throw new ArgumentOutOfRangeException(nameof(code));
        Action<string>? observer;
        lock (_gate)
        {
            if (_disposed || _committed || _failure is not null) return false;
            _failure = code;
            observer = _onFailure;
        }
        CancelSafely(_abortCts);
        CancelSafely(_cts);
        // Includes decisions made at deferred capture and mux boundaries.
        // The one observer runs outside the query/reader/countdown owner.
        if (observer is not null) _ = Task.Run(() => observer(code));
        return true;
    }

    internal bool TryCommit(Action move, bool finalPublication)
    {
        lock (_gate)
        {
            if (_disposed || _committed || _failure is not null) return false;
            move();
            if (finalPublication) _committed = true;
        }
        if (finalPublication) CancelSafely(_cts);
        return true;
    }

    internal IFileCommitGate CreateCommitGate(IFileCommitGate? localGate, bool finalPublication)
        => new StorageCommitGate(this, localGate, finalPublication);

    private sealed class StorageCommitGate(WindowStorageSafety owner, IFileCommitGate? local, bool final) : IFileCommitGate, IPublicationCopyCapacityGate
    {
        public Task<IPublicationCopyReservation?> BeginCopyAsync(long bytes, CancellationToken token) => owner.BeginCopyAsync(bytes, token);
        public bool IsClosed => owner.FailureCode is not null || (local?.IsClosed ?? false);
        public void Close() => local?.Close();
        public bool TryCommit(Action move) => owner.TryCommit(() =>
        {
            if (local is not null && !local.TryCommit(move)) throw new OperationCanceledException();
            if (local is null) move();
        }, final);
    }

    // Cancellation before media zero competes with failure under this same arbiter.
    // If failure already won, its existing observer owns teardown and history.
    internal bool TryCloseBeforeCaptureCancellation()
    {
        lock (_gate)
        {
            if (_failure is not null) return false;
            if (_disposed) return true;
            _disposed = true;
            if (_activeCopy is null) _publicationCopyBytes = 0;
        }
        CompleteDisposal();
        return true;
    }

    public void Dispose()
    {
        lock (_gate) { if (_disposed) return; _disposed = true; if (_activeCopy is null) _publicationCopyBytes = 0; }
        CompleteDisposal();
    }

    private void CompleteDisposal()
    {
        CancelSafely(_cts);
        if (!_committed) CancelSafely(_abortCts);
        // CTS resources are released after the monitor/probe finishes, without
        // any synchronous wait or self-await on the stop/UI thread.
        _ = MonitorCompletion.ContinueWith(_ => { _cts.Dispose(); _abortCts.Dispose(); }, TaskScheduler.Default);
    }

    private static void CancelSafely(CancellationTokenSource source)
    {
        try { source.Cancel(); } catch (ObjectDisposedException) { }
    }
}
