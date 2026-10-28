using AgentRecorder.Capture;
using AgentRecorder.Infrastructure;

namespace AgentRecorder.Core;

public sealed partial class RecordingEngine
{
    public HostWorkAdmission HostAdmission { get; } = new();

    /// <summary>Read owner state and close recording admission at the same lock used by starts.</summary>
    public string? TryReserveIdleHostShutdown(Func<string?> otherOwners)
    {
        lock (_lock)
        {
            if (_disposeStarted != 0) return null;
            var active = _recs.Values.FirstOrDefault(r => r.State == RecState.created || IsActiveRecordingState(r));
            if (active is not null) return "recording_" + active.State;
            if (!_countdownOps.IsEmpty || !_seriesOps.IsEmpty || !_displayRuntimeMonitors.IsEmpty ||
                Volatile.Read(ref _pendingOrdinaryHistoryRegistrations) != 0)
                return "recording_resources_pending";
            // Physical owners may outlive a terminal public state.
            foreach (var rec in _recs.Values)
            {
                if (rec.BundleGenerationStarted != 0 && rec.BundleSnapshot.Status is "pending" or "generating") return "bundle_in_flight";
                if (rec.Backend is not null && !BackendRetired(rec.Backend)) return "worker_retirement_unknown";
                if (rec.Config.StorageSafety is { } storage &&
                    (!storage.MonitorCompletion.IsCompleted || !storage.ProbeRetirementCompletion.IsCompletedSuccessfully || storage.ProbeRetirementFailure is not null))
                    return "storage_resources_pending";
            }
            if (!Monitor.TryEnter(_ordinaryOutputLock)) return "output_owner_unknown";
            try
            {
                if (_ordinaryRetentionPreparation is not null || !_ordinaryOutputCleanup.IsEmpty)
                    return "retention_resources_pending";
                var reason = otherOwners();
                if (reason is not null) return reason;
                Interlocked.Exchange(ref _disposeStarted, 1);
                return null;
            }
            finally { Monitor.Exit(_ordinaryOutputLock); }
        }
    }

    private static void RetireNeverStartedBackend(ICaptureBackend? backend)
    {
        // Called only after the engine owns the failed start. No Stop/Dispose probe of unknown resources.
        try
        {
            if (backend is AvSplitCaptureBackend split) split.RetireUnstartedHostResources();
            else if (backend is FfmpegCaptureBackend ffmpeg) ffmpeg.RetireUnstartedHostResources();
            else if (backend is WgcContinuousCaptureBackend wgc) wgc.RetireUnstartedHostResources();
        }
        catch { } // Cleanup uncertainty keeps the normal host retirement predicate false.
    }

    private static bool BackendRetired(ICaptureBackend backend)
    {
        try
        {
            return backend switch
            {
                FfmpegCaptureBackend ffmpeg => ffmpeg.HostResourcesRetired,
                AvSplitCaptureBackend split => split.HostResourcesRetired,
                WgcContinuousCaptureBackend wgc => wgc.OutputRetirement.IsCompletedSuccessfully,
                _ => false
            };
        }
        catch { return false; }
    }
}
