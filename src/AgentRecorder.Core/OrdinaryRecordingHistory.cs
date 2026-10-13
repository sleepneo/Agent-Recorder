namespace AgentRecorder.Core;

public sealed class OrdinaryRecordingHistoryUnavailableException : Exception
{
    public OrdinaryRecordingHistoryUnavailableException(string message, Exception? innerException = null)
        : base(message, innerException) { }
}

public static class OrdinaryRecordingHistoryStatusCodes
{
    public static IReadOnlyList<string> Supported { get; } = Array.AsReadOnly(new[]
    {
        "pending_confirmation", "preparing", "countdown", "recording", "finalizing", "completed",
        "failed", "cancelled", "rejected", "expired", "interrupted_unknown"
    });
    public static bool IsKnown(string? status) => status is not null && Supported.Contains(status, StringComparer.Ordinal);
}

/// <summary>Privacy-bounded snapshot persisted for an ordinary video recording.</summary>
public sealed record OrdinaryRecordingHistorySnapshot(
    string RecordingId,
    DateTimeOffset CreatedAtUtc,
    string Status,
    string SourceType,
    string CaptureSemantics,
    string SourceTitle,
    int? RequestedDurationSeconds,
    string AudioMode,
    string? MicrophoneDeviceId,
    string? MicrophoneDeviceName,
    string? SystemAudioEndpointId,
    string? SystemAudioEndpointName,
    string OutputPath,
    string? NestedRole,
    string? ParentRecordingId,
    string? NestedSessionId,
    string? ProfileId,
    long? ProfileVersion,
    string? ProfileDigest,
    DateTimeOffset? StartedAtUtc = null,
    DateTimeOffset? CompletedAtUtc = null,
    string? ReasonCode = null,
    long? OutputSizeBytes = null,
    double? OutputDurationSeconds = null)
{
    public OrdinaryRecordingBundleSnapshot? Bundle { get; internal init; }
    public OrdinaryMediaHashSnapshot? MediaHash => Bundle?.MediaHash;
    public string? TargetKey { get; internal init; }
    [System.Text.Json.Serialization.JsonIgnore]
    internal OrdinaryRecordingBundleRegistration? BundleRegistration { get; init; }
    [System.Text.Json.Serialization.JsonIgnore]
    internal AgentRecorder.Capture.PublicationReceipt? PublicationReceipt { get; init; }
}

public sealed record OrdinaryRecordingHistoryPage(
    IReadOnlyList<OrdinaryRecordingHistorySnapshot> Items,
    string? NextCursor);

/// <summary>
/// Core-facing seam for the independent durable history projection. Registration
/// is synchronous; lifecycle snapshots are accepted without waiting for SQLite.
/// </summary>
public interface IOrdinaryRecordingHistoryStore
{
    void Register(OrdinaryRecordingHistorySnapshot snapshot);
    bool TryPublish(OrdinaryRecordingHistorySnapshot snapshot);
    OrdinaryRecordingHistoryPage ReadPage(int limit, string? cursor);
    OrdinaryRecordingHistorySnapshot? Read(string recordingId);
    void RecoverIncomplete(DateTimeOffset recoveredAtUtc);
}

public interface IOrdinaryRecordingHistoryDrainable : IDisposable
{
    bool DrainAndDispose(TimeSpan timeout);
}
