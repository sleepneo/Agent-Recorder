namespace AgentRecorder.Core;

public sealed record UnifiedHistoryFilters(string Source = "all", DateTimeOffset? CreatedFrom = null, DateTimeOffset? CreatedBefore = null, string? Status = null, string? ProfileId = null, long? ProfileVersion = null, string? SourceType = null, string? PlanId = null, string? TargetKey = null)
{
    [System.Text.Json.Serialization.JsonIgnore]
    public bool HasValidTargetKey => TargetKey is null || Source == "ordinary" && (OrdinaryScreenTargetKey.IsValid(TargetKey) || OrdinaryWindowTargetKey.IsValid(TargetKey));
    [System.Text.Json.Serialization.JsonIgnore]
    public bool HasValidSourceType => SourceType is null || Source == "ordinary" && UnifiedHistoryFilterCodes.SourceTypes.Contains(SourceType, StringComparer.Ordinal);

    [System.Text.Json.Serialization.JsonIgnore]
    public bool HasValidPlanId => PlanId is null || Source == "plan" && UnifiedHistoryFilterCodes.IsValidPlanId(PlanId);

    [System.Text.Json.Serialization.JsonIgnore]
    public bool HasValidStatus => Status is null || Source == "ordinary" && OrdinaryRecordingHistoryStatusCodes.IsKnown(Status);

    [System.Text.Json.Serialization.JsonIgnore]
    public bool HasValidProfile
    {
        get
        {
            if (ProfileId is null) return ProfileVersion is null;
            if (Source != "ordinary" || ProfileVersion is <= 0 || ProfileId != ProfileId.Trim()) return false;
            try { return Automation.RecurringFixedRegionProfileValidation.ProfileId(ProfileId) == ProfileId; }
            catch (Automation.Phase3DomainException) { return false; }
        }
    }
}

/// <summary>Only durable public facts; no authorization material.</summary>
public sealed record UnifiedHistoryEntry(
    string EntryId, string Source, string EntryKind, DateTimeOffset CreatedAtUtc,
    string? RecordingId = null, string? PlanId = null, string? OccurrenceId = null,
    string? AuthorizationId = null, string? RunId = null, string? Status = null,
    string? ReasonCode = null, string? AuthorizationStatus = null, string? OccurrenceStatus = null,
    string? RunStatus = null, string? RunReasonCode = null, string? ExecutionStatusCode = null,
    string? OutputPath = null, bool OutputPathRecorded = false, bool? OutputFileExists = null,
    long? OutputSizeBytes = null, double? OutputDurationSeconds = null,
    DateTimeOffset? StartedAtUtc = null, DateTimeOffset? CompletedAtUtc = null,
    DateTimeOffset? WindowStartUtc = null, DateTimeOffset? WindowEndUtc = null,
    string? ProfileId = null, long? ProfileVersion = null, string? SourceType = null, OrdinaryRecordingBundleSnapshot? Bundle = null, string? TargetKey = null)
{
    public OrdinaryMediaHashSnapshot? MediaHash => Source == "ordinary" ? Bundle?.MediaHash : null;
}

public static class UnifiedHistoryFilterCodes
{
    // Query allowlist only: older durable source types remain readable without a filter.
    public static IReadOnlyList<string> SourceTypes { get; } = Array.AsReadOnly(new[] { "display", "window", "region", "fixed_region_profile" });
    // Same canonical boundary as the dedicated plan history/status APIs.
    public static bool IsValidPlanId(string? id) => !string.IsNullOrWhiteSpace(id) && id.Length <= 128 && id == id.Trim() &&
        id.All(c => c is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9' or '-' or '_' or '.');
}

public sealed record UnifiedHistoryPage(IReadOnlyList<UnifiedHistoryEntry> Items, string? NextCursor, int PageSize, UnifiedHistoryFilters Filters);

public sealed class UnifiedHistoryArgumentException : Exception
{
    public UnifiedHistoryArgumentException() : base("The history arguments or cursor are invalid.") { }
}

public sealed class UnifiedHistoryUnavailableException : Exception
{
    public UnifiedHistoryUnavailableException(Exception? inner = null) : base("Unified history is temporarily unavailable.", inner) { }
}
