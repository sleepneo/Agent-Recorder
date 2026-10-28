namespace AgentRecorder.Persistence;

/// <summary>Actual worker quota, including native work surviving an HTTP deadline.</summary>
public static class HostReadOnlyWorkerState
{
    public static string? BusyReason => ArtifactMetadataWorker.HostBusy ? "artifact_metadata_worker_pending" :
        OrdinaryContentVerificationAdmission.HostBusy ? "content_verification_worker_pending" : null;
}
