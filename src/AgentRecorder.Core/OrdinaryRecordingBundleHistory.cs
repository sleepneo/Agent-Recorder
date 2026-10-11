using AgentRecorder.Capture;

namespace AgentRecorder.Core;

/// <summary>A durable generation fact; never a current physical readiness assertion.</summary>
public sealed record OrdinaryRecordingBundleSnapshot(string Status, string? Path, string? ErrorCode,
    RecordingBundleContentsEvidence? ContentsEvidence = null)
{
    internal BundleIdentityEvidence? IdentityEvidence { get; init; }
    public RecordingBundleContentsEvidence? ContentsEvidence { get; init; } =
        ContentsEvidence is null ? null : new(ContentsEvidence.Contents);
}

internal sealed record OrdinaryRecordingBundleOperation(string RecordingId, string MediaPath, string BundlePath, string OperationId)
{
    internal static OrdinaryRecordingBundleOperation Create(string recordingId, string mediaPath) =>
        new(recordingId, mediaPath, DerivePath(mediaPath), Guid.NewGuid().ToString("N"));
    internal static string DerivePath(string mediaPath) =>
        System.IO.Path.Combine(System.IO.Path.GetDirectoryName(mediaPath) ?? "", System.IO.Path.GetFileNameWithoutExtension(mediaPath) + ".bundle");
    internal static bool IsErrorCode(string? code) => code is RecordingBundleErrorCodes.AlreadyExists or
        RecordingBundleErrorCodes.HashFailed or RecordingBundleErrorCodes.FrameExtractFailed or
        RecordingBundleErrorCodes.FrameOutputInvalid or RecordingBundleErrorCodes.MetadataWriteFailed or
        RecordingBundleErrorCodes.MarksWriteFailed or RecordingBundleErrorCodes.PublishFailed or RecordingBundleErrorCodes.GenerationFailed;
}

internal sealed record OrdinaryRecordingBundleRegistration(OrdinaryRecordingBundleOperation? Operation)
{
    internal OrdinaryRecordingBundleSnapshot Initial => Operation is { } op
        ? new("generating", op.BundlePath, null) : new("not_applicable", null, null);
}

// Trusted Core producer only. Neither operations nor this writer are API DTOs.
internal sealed record OrdinaryRecordingBundleUpdate(OrdinaryRecordingBundleOperation Operation, OrdinaryRecordingBundleSnapshot Result);
internal interface IOrdinaryRecordingBundleHistoryWriter
{
    bool TryPublishBundle(OrdinaryRecordingBundleUpdate update);
}
