using AgentRecorder.Core;

namespace AgentRecorder.Persistence;

public sealed record OrdinaryAttachmentVerificationItem(string Name, string CheckState, string Comparison,
    string ReasonCode, long BytesRead, OrdinaryMediaHashSnapshot? RecordedHash, ObservedMediaHash? ObservedHash,
    bool ContentVerified, DateTimeOffset? CheckStartedAtUtc, DateTimeOffset? CheckCompletedAtUtc);

/// <summary>Immutable sequential observations, never a simultaneous directory snapshot.</summary>
public sealed record OrdinaryAttachmentVerificationResult(string RecordingId, DateTimeOffset CheckStartedAtUtc,
    DateTimeOffset CheckCompletedAtUtc, string CheckState, string Comparison, string ReasonCode, long BytesRead,
    IReadOnlyList<OrdinaryAttachmentVerificationItem> Items, bool ContentVerified);
