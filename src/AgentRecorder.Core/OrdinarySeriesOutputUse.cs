using AgentRecorder.Infrastructure;

namespace AgentRecorder.Core;

public sealed partial class RecordingEngine
{
    // A directory use for this exact ordinary series registration. Local moves
    // replace the token before dispatch; publication may additionally reserve one
    // frozen rename target. Neither public state nor a dispatcher ack retires it.
    private sealed class OrdinarySeriesOutputUse(Recording recording, string path)
    {
        internal readonly Recording Recording = recording;
        internal readonly string Path = path;
        internal string? PublicationPath; // assigned once under the original path lock
        internal bool Dispatched;
        internal Task? Worker;
    }
    private readonly Dictionary<string, OrdinarySeriesOutputUse> _ordinarySeriesOutputUses = new();
    private readonly HashSet<OrdinarySeriesOutputUse> _ordinarySeriesHostUses = new();
    internal Func<Func<Task>, Task>? ScreenshotSeriesDispatcherForTests { get; set; }

    private static IEnumerable<string> SeriesPaths(OrdinarySeriesOutputUse use)
        => use.PublicationPath is { } published ? new[] { use.Path, published } : new[] { use.Path };
    private IEnumerable<OrdinarySeriesOutputUse> SeriesUsesUnderLock()
        => _ordinarySeriesOutputUses.Values.Concat(_ordinarySeriesHostUses).Distinct();
    private IEnumerable<string> SeriesPathsUnderLock()
        => SeriesUsesUnderLock().SelectMany(SeriesPaths);
    private static bool OutputDirectoriesOverlap(string a, string b)
        => InBundleDirectory(a, b) || InBundleDirectory(b, a);
    private bool HasSeriesNamespaceConflictUnderLock(params string[] paths)
        => SeriesPathsUnderLock().Any(directory => paths.Any(path => InBundleDirectory(path, directory)));
    private bool HasSeriesDirectoryOverlapUnderLock(string directory, OrdinarySeriesOutputUse? except = null)
        => SeriesUsesUnderLock().Where(use => !ReferenceEquals(use, except))
            .SelectMany(SeriesPaths).Any(path => OutputDirectoriesOverlap(path, directory));

    private OrdinarySeriesOutputUse? FindOrdinarySeriesOutputUse(Recording rec)
    {
        if (!rec.IsOrdinaryLocalRequest || !rec.IsScreenshotSeries) return null;
        lock (_ordinaryOutputLock)
            return _ordinarySeriesOutputUses.TryGetValue(rec.Id, out var use) && ReferenceEquals(use.Recording, rec) ? use : null;
    }
    private void RetireUnstartedOrdinarySeriesOutput(OrdinarySeriesOutputUse? use)
    {
        if (use is null) return;
        lock (_ordinaryOutputLock)
            if (!use.Dispatched && use.Recording.IsFinalized &&
                _ordinarySeriesOutputUses.TryGetValue(use.Recording.Id, out var current) && ReferenceEquals(current, use))
                _ordinarySeriesOutputUses.Remove(use.Recording.Id);
    }

    private void ClaimAndCreateOrdinarySeriesOutput(Recording rec, string path, string? requestedPath)
    {
        var normalized = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        var requested = requestedPath is null ? normalized : Path.TrimEndingDirectorySeparator(Path.GetFullPath(requestedPath));
        OrdinarySeriesOutputUse current;
        OrdinarySeriesOutputUse? previous;
        lock (_ordinaryOutputLock)
        {
            _ordinarySeriesOutputUses.TryGetValue(rec.Id, out previous);
            if (previous is not null && (!ReferenceEquals(previous.Recording, rec) || previous.Dispatched))
                throw SeriesOutputInUse();
            ValidateOrdinarySeriesTargetUnderLock(normalized, previous);
            if (!string.Equals(normalized, requested, StringComparison.OrdinalIgnoreCase))
                ValidateOrdinarySeriesTargetUnderLock(requested, previous);
            current = new(rec, normalized);
            _ordinarySeriesOutputUses[rec.Id] = current; // admission/replacement linearization point
        }
        // Parent creation is owned by the registration and deliberately outside
        // the lock. A competing Bundle bind already sees the frozen directory.
        try { Directory.CreateDirectory(Path.GetDirectoryName(normalized)!); }
        catch
        {
            lock (_ordinaryOutputLock)
                if (_ordinarySeriesOutputUses.TryGetValue(rec.Id, out var actual) && ReferenceEquals(actual, current))
                {
                    if (previous is null) _ordinarySeriesOutputUses.Remove(rec.Id);
                    else _ordinarySeriesOutputUses[rec.Id] = previous;
                }
            throw;
        }
    }

    private static ApiException SeriesOutputInUse()
        => new(409, "OUTPUT_PATH_IN_USE", "An ordinary output or Bundle publication still reserves this output directory.");
    private void ValidateOrdinarySeriesTargetUnderLock(string directory, OrdinarySeriesOutputUse? except)
    {
        if (_ordinaryBundleOutputUses.Any(use => use.NamespaceReserved && OutputDirectoriesOverlap(directory, use.BundleDirectory) ||
                InBundleDirectory(use.Path, directory)) ||
            _ordinaryOutputs.Values.Any(owner => InBundleDirectory(owner.Path, directory)) ||
            HasSeriesDirectoryOverlapUnderLock(directory, except) ||
            _ordinaryRetentionPreparation is { } preparation && InBundleDirectory(preparation.Path, directory))
            throw SeriesOutputInUse();
    }

    // Called with rec held, before publishing an operation that Stop can see.
    // The output lock never enters a recording/engine/worker lock.
    private OrdinarySeriesOutputUse? BindOrdinarySeriesHost(Recording rec)
    {
        if (!rec.IsOrdinaryLocalRequest) return null;
        lock (_ordinaryOutputLock)
        {
            if (!_ordinarySeriesOutputUses.TryGetValue(rec.Id, out var use) ||
                !ReferenceEquals(use.Recording, rec) || use.Dispatched ||
                !string.Equals(use.Path, Path.GetFullPath(rec.OutputPath), StringComparison.OrdinalIgnoreCase))
                throw SeriesOutputInUse();
            use.Dispatched = true; // no terminal-state pruning from here onward
            _ordinarySeriesHostUses.Add(use);
            return use;
        }
    }
    private void ClaimOrdinarySeriesPublication(OrdinarySeriesOutputUse? use, string target)
    {
        if (use is null) return;
        var path = Path.TrimEndingDirectorySeparator(Path.GetFullPath(target));
        lock (_ordinaryOutputLock)
        {
            if (!_ordinarySeriesOutputUses.TryGetValue(use.Recording.Id, out var current) || !ReferenceEquals(current, use) ||
                !use.Dispatched || use.PublicationPath is not null)
                throw SeriesOutputInUse();
            ValidateOrdinarySeriesTargetUnderLock(path, use);
            if (!string.Equals(path, use.Path, StringComparison.OrdinalIgnoreCase)) use.PublicationPath = path;
            // Keep both original and renamed namespaces until the actual host exits.
        }
    }
    private void DispatchOrdinarySeriesHost(Recording rec, ScreenshotSeriesOperation op, string? traceId, ITrayContext tray)
    {
        Task Run()
        {
            if (Interlocked.CompareExchange(ref op.HostEntered, 1, 0) != 0)
                return Task.FromException(new InvalidOperationException("Screenshot-series host already entered."));
            var work = RunScreenshotSeriesAsync(rec, op, traceId, tray);
            if (op.OutputUse is { } use)
            {
                Volatile.Write(ref use.Worker, work);
                _ = work.ContinueWith(done => CompleteOrdinarySeriesHost(use, done), CancellationToken.None,
                    TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            }
            return work;
        }
        // Unknown dispatch deliberately retains its reservation, like Bundle.
        try
        {
            var dispatched = ScreenshotSeriesDispatcherForTests is { } dispatcher ? dispatcher(Run) : Task.Run(Run);
            if (dispatched is not null) _ = dispatched.ContinueWith(done => { if (done.IsFaulted) _ = done.Exception; },
                CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        }
        catch { }
    }
    private void CompleteOrdinarySeriesHost(OrdinarySeriesOutputUse use, Task work)
    {
        if (!ReferenceEquals(Volatile.Read(ref use.Worker), work) || !work.IsCompleted) return;
        if (work.IsFaulted) _ = work.Exception;
        lock (_ordinaryOutputLock)
        {
            _ordinarySeriesHostUses.Remove(use);
            if (_ordinarySeriesOutputUses.TryGetValue(use.Recording.Id, out var current) && ReferenceEquals(current, use))
                _ordinarySeriesOutputUses.Remove(use.Recording.Id);
        }
    }
}
