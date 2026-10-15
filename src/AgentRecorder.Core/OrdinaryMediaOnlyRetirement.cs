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
        var backend=rec.Backend;
        if(backend is WgcContinuousCaptureBackend wgc)
        {
            _=wgc.OutputRetirement.ContinueWith(done=>{if(done.IsCompletedSuccessfully)ReleaseSettledMediaOnlyOutput(rec,backend);},
                CancellationToken.None,TaskContinuationOptions.ExecuteSynchronously,TaskScheduler.Default);
        }
        else if(backend is FfmpegCaptureBackend ffmpeg&&ffmpeg.IsVideoOnlyOutputSettled||
                backend is AvSplitCaptureBackend split&&split.IsOutputSettled)
            ReleaseSettledMediaOnlyOutput(rec,backend);
        // Unknown backends and unproven cleanup retain their original claims.
    }
    private void ReleaseSettledMediaOnlyOutput(Recording rec,ICaptureBackend? backend)
    {
        lock(_ordinaryOutputLock)
        {
            if(RetentionHostStopping||_ordinaryOutputCleanup.ContainsKey(rec.Id)||!ReferenceEquals(rec.Backend,backend))return;
            if(_ordinaryOutputs.TryGetValue(rec.Id,out var owner)&&ReferenceEquals(owner.Recording,rec)&&
                string.Equals(owner.Path,rec.OutputPath,StringComparison.OrdinalIgnoreCase))_ordinaryOutputs.Remove(rec.Id);
        }
    }
}
