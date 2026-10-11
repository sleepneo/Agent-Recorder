using AgentRecorder.Core;
using AgentRecorder.Infrastructure;

namespace AgentRecorder.Persistence;

// Never serialized or returned from the public gateway; the anchor remains private.
internal sealed record OrdinaryArtifactFacts(OrdinaryRecordingHistorySnapshot Media, PublicationIdentityAnchor? Anchor);

public sealed record OrdinaryArtifactFileCheck(string Name, long? RecordedSizeBytes, long? ObservedSizeBytes,
    string Observation, string ReasonCode, DateTimeOffset? SampledAtUtc, string IdentityState);

public sealed class OrdinaryArtifactBundleCheck
{
    public string? HistoryStatus { get; }
    public int? ContentsEvidenceVersion { get; }
    public IReadOnlyList<OrdinaryArtifactFileCheck>? Contents { get; }
    internal OrdinaryArtifactBundleCheck(string? status, int? version, IEnumerable<OrdinaryArtifactFileCheck>? items)
    { HistoryStatus=status; ContentsEvidenceVersion=version; Contents=items is null?null:Array.AsReadOnly(items.Take(5).ToArray()); }
}

public sealed record OrdinaryArtifactCheckResult(string RecordingId, DateTimeOffset CheckStartedAtUtc,
    DateTimeOffset CheckCompletedAtUtc, string ProbeState, string? MediaHistoryStatus,
    OrdinaryArtifactFileCheck Media, OrdinaryArtifactBundleCheck Bundle);
