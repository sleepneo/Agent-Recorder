using AgentRecorder.Capture;

namespace AgentRecorder.Core;

public sealed partial class RecordingEngine
{
    private sealed record OrdinaryOutputRetirementWitness(OrdinaryOutputOwner Owner, ICaptureBackend? Backend);

    // Non-mutating evidence observation, shared by admission pruning and the
    // media-only terminal path. Call outside _ordinaryOutputLock: the existing
    // no-resources/host-retirement evidence may acquire worker locks.
    private static bool IsOrdinaryOutputRetired(ICaptureBackend? backend, bool startAttempted)
    {
        try
        {
            return backend switch
            {
                WgcContinuousCaptureBackend wgc => wgc.OutputRetirement.IsCompletedSuccessfully,
                AvSplitCaptureBackend split => split.IsOutputSettled &&
                    (startAttempted || split.HostResourcesRetired),
                FfmpegCaptureBackend ffmpeg => ffmpeg.IsVideoOnlyOutputSettled &&
                    (startAttempted && ffmpeg.VideoOnlyCompletion is not null || ffmpeg.HostResourcesRetired),
                _ => false
            };
        }
        catch { return false; }
    }

    private OrdinaryOutputRetirementWitness[] ObserveRetiredOrdinaryOutputs()
    {
        OrdinaryOutputOwner[] owners;
        lock (_ordinaryOutputLock)
        {
            if (RetentionHostStopping) return Array.Empty<OrdinaryOutputRetirementWitness>();
            owners = _ordinaryOutputs.Values.Where(owner => !HasOrdinaryBundleOutputUseUnderLock(owner)).ToArray();
        }
        var retired = new List<OrdinaryOutputRetirementWitness>();
        foreach (var owner in owners)
        {
            var rec = owner.Recording;
            // IsFinalized is the acquire barrier for the published terminal
            // snapshot. It is a prerequisite, never retirement evidence itself.
            if (!rec.IsFinalized || _ordinaryOutputCleanup.ContainsKey(rec.Id)) continue;
            var backend = rec.Backend;
            var settled = backend is null
                ? rec.OrdinaryBackendCreation?.ObserveRetired(() => false) == true
                : IsOrdinaryOutputRetired(backend, rec.BackendStartAttempted);
            if (string.Equals(owner.RegisteredPath, rec.OutputPath, StringComparison.OrdinalIgnoreCase) && settled)
                retired.Add(new(owner, backend));
        }
        return retired.ToArray();
    }

    // Only immutable owner identity, published state, and concurrent cleanup
    // membership are checked under this lock. No backend calls, waits or I/O.
    private void ReleaseRetiredOrdinaryOutputUnderLock(OrdinaryOutputOwner owner, ICaptureBackend? backend)
    {
        var rec = owner.Recording;
        if (rec.IndependentWindowAdmissionPending || HasOrdinaryBundleOutputUseUnderLock(owner) || RetentionHostStopping || !rec.IsFinalized || _ordinaryOutputCleanup.ContainsKey(rec.Id) ||
            !ReferenceEquals(rec.Backend, backend) ||
            !string.Equals(owner.RegisteredPath, rec.OutputPath, StringComparison.OrdinalIgnoreCase)) return;
        if (_ordinaryOutputs.TryGetValue(rec.Id, out var current) && ReferenceEquals(current, owner))
            _ordinaryOutputs.Remove(rec.Id);
    }
}
