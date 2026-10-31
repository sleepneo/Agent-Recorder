using AgentRecorder.Capture;

namespace AgentRecorder.Core;

public sealed partial class RecordingEngine
{
    // A media-only recording has no later Bundle reader. Its original output
    // claim can retire at the known production backend's actual settlement
    // boundary. Neither GET nor retention preparation infers this from status.
    private void RetireMediaOnlyOutputAfterFinalization(Recording rec)
    {
        if(rec.GenerateBundle||!rec.IsOrdinaryLocalRequest||rec.Mode!="video"||!rec.IsFinalized||rec.State!=RecState.completed||
            rec.OrdinaryBundleOperation is not null||rec.BundleSnapshot.Status!="not_applicable"||rec.LastMeta?.PublicationReceipt is null)return;
        var backend = rec.Backend;
        OrdinaryOutputOwner? owner;
        lock (_ordinaryOutputLock)
        {
            _ordinaryOutputs.TryGetValue(rec.Id, out owner);
            if (owner is null || !ReferenceEquals(owner.Recording, rec) ||
                !string.Equals(owner.RegisteredPath, rec.OutputPath, StringComparison.OrdinalIgnoreCase)) return;
        }
        if (backend is WgcContinuousCaptureBackend wgc)
        {
            _ = wgc.OutputRetirement.ContinueWith(done =>
            {
                if (done.IsCompletedSuccessfully) ReleaseSettledMediaOnlyOutput(owner, backend);
            }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        }
        else if (IsOrdinaryOutputRetired(backend, rec.BackendStartAttempted))
            ReleaseSettledMediaOnlyOutput(owner, backend!);
        // Unknown backends and unproven cleanup retain their original claims.
    }

    private void ReleaseSettledMediaOnlyOutput(OrdinaryOutputOwner owner, ICaptureBackend backend)
    {
        // Observe worker evidence before taking the output lock; a late callback
        // carries the immutable registration token, never a mutable current path.
        if (!IsOrdinaryOutputRetired(backend, owner.Recording.BackendStartAttempted)) return;
        lock (_ordinaryOutputLock) ReleaseRetiredOrdinaryOutputUnderLock(owner, backend);
    }
}
