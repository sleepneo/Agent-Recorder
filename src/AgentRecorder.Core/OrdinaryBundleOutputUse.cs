using AgentRecorder.Capture;
using AgentRecorder.Logging;

namespace AgentRecorder.Core;

public sealed partial class RecordingEngine
{
    // Main-video uses only. This is not an attachment/arbitrary-reader registry.
    private sealed class OrdinaryBundleOutputUse(Recording recording, OrdinaryOutputOwner? owner,
        ICaptureBackend? backend, string path)
    {
        internal readonly string Id = Guid.NewGuid().ToString("N");
        internal readonly Recording Recording = recording;
        internal readonly OrdinaryOutputOwner? Owner = owner;
        internal readonly ICaptureBackend? Backend = backend;
        internal readonly string Path = path;
        internal int Started;
        internal Task? Worker;
    }
    private readonly HashSet<OrdinaryBundleOutputUse> _ordinaryBundleOutputUses = new();
    internal Func<Func<Task>, Task>? BundleGenerationDispatcherForTests { get; set; }

    private OrdinaryBundleOutputUse? BindOrdinaryBundleOutputUse(Recording rec, RecordingBundleRequest request)
    {
        if (!rec.IsOrdinaryLocalRequest || rec.Mode != "video") return null;
        var path = Path.GetFullPath(request.MediaPath);
        lock (_ordinaryOutputLock)
        {
            _ordinaryOutputs.TryGetValue(rec.Id, out var owner);
            if (owner is not null && (!ReferenceEquals(owner.Recording, rec) ||
                !string.Equals(owner.Path, path, StringComparison.OrdinalIgnoreCase))) owner = null;
            var use = new OrdinaryBundleOutputUse(rec, owner, rec.Backend, path);
            _ordinaryBundleOutputUses.Add(use); // before the completed publication
            return use;
        }
    }

    private bool HasOrdinaryBundleOutputUseUnderLock(OrdinaryOutputOwner owner)
        => _ordinaryBundleOutputUses.Any(use => ReferenceEquals(use.Owner, owner));

    private string[]? SnapshotOrdinaryOutputPathsUnderLock()
    {
        // Reject oversized registries before enumerating either collection.
        if (_ordinaryOutputs.Count > OrdinaryRuntimeOutputSnapshot.MaxClaims ||
            _ordinaryBundleOutputUses.Count > OrdinaryRuntimeOutputSnapshot.MaxClaims) return null;
        if (_ordinaryOutputs.Values.Any(owner => owner is null || owner.Recording is null || string.IsNullOrEmpty(owner.Path))) return null;
        // A use on its still-current owner is one booking. Replaced/orphan uses
        // count separately, so replacing an ID cannot bypass the 500-claim bound.
        var orphanUses = _ordinaryBundleOutputUses.Count(use => use.Owner is null ||
            !_ordinaryOutputs.TryGetValue(use.Recording.Id, out var current) || !ReferenceEquals(current, use.Owner));
        if (_ordinaryOutputs.Count + orphanUses > OrdinaryRuntimeOutputSnapshot.MaxClaims) return null;
        return _ordinaryOutputs.Values.Select(owner => owner.Path)
            .Concat(_ordinaryBundleOutputUses.Select(use => use.Path)).ToArray();
    }

    private void DispatchBundleGeneration(Recording rec, RecordingBundleRequest request, OrdinaryBundleOutputUse? use)
    {
        if (use is null)
        {
            _ = Task.Run(() => RunBundleGenerationAsync(rec, request)); // original nonordinary scope
            return;
        }
        Task Run()
        {
            if (Interlocked.CompareExchange(ref use.Started, 1, 0) != 0)
                return Task.FromException(new InvalidOperationException("Bundle work already entered."));
            // The actual original host method includes publication/history/audit.
            // Only its returned task can prove that method has exited. A queue
            // acknowledgement, timeout or failed dispatcher cannot retire this use.
            var work = RunBundleGenerationAsync(rec, request);
            Volatile.Write(ref use.Worker, work);
            _ = work.ContinueWith(done =>
            {
                try { CompleteOrdinaryBundleOutputUse(use, done); }
                catch { LogBundleUseEvent(use, "retirement_unknown"); }
            }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            return work;
        }
        try
        {
            var dispatched = BundleGenerationDispatcherForTests is { } dispatcher ? dispatcher(Run) : Task.Run(Run);
            if (dispatched is null) throw new InvalidOperationException("Bundle dispatch unavailable.");
            _ = dispatched.ContinueWith(done =>
            {
                if (done.IsFaulted) _ = done.Exception;
                if (!done.IsCompletedSuccessfully) LogBundleUseEvent(use, "dispatch_unknown");
            }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        }
        catch { LogBundleUseEvent(use, "dispatch_unknown"); }
    }

    private void CompleteOrdinaryBundleOutputUse(OrdinaryBundleOutputUse use, Task work)
    {
        if (!ReferenceEquals(Volatile.Read(ref use.Worker), work) || !work.IsCompleted) return;
        if (work.IsFaulted) _ = work.Exception;
        // Worker observation remains outside the original path lock.
        var writerRetired = use.Backend is null
            ? use.Recording.OrdinaryBackendCreation?.ObserveRetired(() => false) == true
            : IsOrdinaryOutputRetired(use.Backend, use.Recording.BackendStartAttempted);
        lock (_ordinaryOutputLock)
        {
            if (!_ordinaryBundleOutputUses.Remove(use)) return;
            if (writerRetired && use.Owner is not null)
                ReleaseRetiredOrdinaryOutputUnderLock(use.Owner, use.Backend);
        }
        // A WGC completion callback can publish logical completion before its
        // owner exits. Bundle completion must not substitute for that evidence.
        if (!writerRetired && use.Owner is not null && use.Backend is WgcContinuousCaptureBackend wgc)
            _ = wgc.OutputRetirement.ContinueWith(done =>
            {
                if (!done.IsCompletedSuccessfully) return;
                lock (_ordinaryOutputLock) ReleaseRetiredOrdinaryOutputUnderLock(use.Owner, use.Backend);
            }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        LogBundleUseEvent(use, work.IsFaulted ? "host_faulted" : work.IsCanceled ? "host_cancelled" : "retired");
    }

    private void LogBundleUseEvent(OrdinaryBundleOutputUse use, string outcome)
    {
        try
        {
            _audit.Log("recording.bundle_output_use", new { recording_id = use.Recording.Id, use_id = use.Id, outcome });
        }
        catch
        {
            try { DiagnosticLog.Write("recording.bundle_output_use", use.Recording.Id, outcome); } catch { }
        }
    }
}
