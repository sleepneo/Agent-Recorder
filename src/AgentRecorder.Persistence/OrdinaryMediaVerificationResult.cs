using AgentRecorder.Core;

namespace AgentRecorder.Persistence;

public sealed record ObservedMediaHash(string Algorithm, string Value, long SizeBytes);

/// <summary>A comparison for this read handle only, never a durable integrity or deletion permission.</summary>
public sealed record OrdinaryMediaVerificationResult(string RecordingId, DateTimeOffset CheckStartedAtUtc,
    DateTimeOffset CheckCompletedAtUtc, string CheckState, string Comparison, string ReasonCode,
    long BytesRead, OrdinaryMediaHashSnapshot? RecordedHash, ObservedMediaHash? ObservedHash, bool ContentVerified);
