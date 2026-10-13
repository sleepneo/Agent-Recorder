namespace AgentRecorder.Core;

/// <summary>Time-rule evaluation only. No item grants permission to delete.</summary>
public sealed record RetentionPreviewEntry(string EntryId, string? RecordingId, string? Status,
    DateTimeOffset? CompletedAtUtc, string Evaluation, string ReasonCode, bool AgeCandidate,
    string? RecordedPath, long? RecordedBytes, long? ObservedFileBytes);

public sealed record RetentionPreviewPage(IReadOnlyList<RetentionPreviewEntry> Items, string? NextCursor,
    int PageSize, DateTimeOffset CompletedBefore, int EvaluatedCount, int AgeCandidateCount,
    long ObservedCandidateBytes, int ObservedCandidateFileCount, int UnknownCandidateFileCount);

public sealed class RetentionPreviewUnavailableException(Exception? inner = null)
    : Exception("Retention preview is temporarily unavailable.", inner);

// Private retention evidence, never part of the public unified-history projection.
public enum OrdinaryNestedReferenceState { None, Relationship, ParentReferenced, Invalid }
public enum SharedOutputPathReferenceState { None, Referenced }
public enum PlannedOutputPathReferenceState { None, Referenced, Indeterminate }
internal enum PendingOutputUseState { None, SetupExact, Template, Unknown }
public sealed record OrdinaryRetentionEvidence(UnifiedHistoryEntry Entry, OrdinaryNestedReferenceState? NestedReference,
    SharedOutputPathReferenceState? SharedOutputReference, PlannedOutputPathReferenceState? PlannedOutputReference)
{
    [System.Text.Json.Serialization.JsonIgnore]
    internal PendingOutputUseState PendingOutputUse { get; init; }
    [System.Text.Json.Serialization.JsonIgnore]
    internal AgentRecorder.Infrastructure.PublicationIdentityAnchor? PublicationAnchor { get; init; }
}
public sealed record OrdinaryRetentionEvidencePage(IReadOnlyList<OrdinaryRetentionEvidence> Items,
    string? NextCursor, int PageSize, UnifiedHistoryFilters Filters);
