using System.Collections.ObjectModel;

namespace AgentRecorder.Core.Automation;

public enum PlanDefinitionStatus
{
    Draft,
    Enabled,
    Paused,
    Cancelled,
}

public enum PlanOccurrenceStatus
{
    Scheduled,
    Due,
    Rechecking,
    PendingLeaseApproval,
    Authorized,
    PendingConfirmation,
    RunCreated,
    Completed,
    Missed,
    Blocked,
    Cancelled,
    Expired,
}

public enum RecordingRunStatus
{
    Created,
    Preparing,
    StartCommitted,
    Recording,
    StartedUnknown,
    Finalizing,
    MediaReady,
    Settled,
    SessionInterrupted,
    Failed,
}

public enum ConsentLeaseStatus
{
    Pending,
    Active,
    Rejected,
    Revoked,
    Expired,
    Exhausted,
}

public enum LeaseUseStatus
{
    Available,
    Reserved,
    StartCommitted,
    Consumed,
    Settled,
    StartedUnknown,
}

/// <summary>
/// The only public status-code mapping for the Phase 3 domain states.
/// Do not replace these mappings with enum names or default JSON conversion.
/// </summary>
public static class Phase3StateCodes
{
    private static readonly IReadOnlyDictionary<PlanDefinitionStatus, string> PlanDefinitionCodeMap =
        new ReadOnlyDictionary<PlanDefinitionStatus, string>(new Dictionary<PlanDefinitionStatus, string>
        {
            [PlanDefinitionStatus.Draft] = "draft",
            [PlanDefinitionStatus.Enabled] = "enabled",
            [PlanDefinitionStatus.Paused] = "paused",
            [PlanDefinitionStatus.Cancelled] = "cancelled",
        });

    private static readonly IReadOnlyDictionary<PlanOccurrenceStatus, string> PlanOccurrenceCodeMap =
        new ReadOnlyDictionary<PlanOccurrenceStatus, string>(new Dictionary<PlanOccurrenceStatus, string>
        {
            [PlanOccurrenceStatus.Scheduled] = "scheduled",
            [PlanOccurrenceStatus.Due] = "due",
            [PlanOccurrenceStatus.Rechecking] = "rechecking",
            [PlanOccurrenceStatus.PendingLeaseApproval] = "pending_lease_approval",
            [PlanOccurrenceStatus.Authorized] = "authorized",
            [PlanOccurrenceStatus.PendingConfirmation] = "pending_confirmation",
            [PlanOccurrenceStatus.RunCreated] = "run_created",
            [PlanOccurrenceStatus.Completed] = "completed",
            [PlanOccurrenceStatus.Missed] = "missed",
            [PlanOccurrenceStatus.Blocked] = "blocked",
            [PlanOccurrenceStatus.Cancelled] = "cancelled",
            [PlanOccurrenceStatus.Expired] = "expired",
        });

    private static readonly IReadOnlyDictionary<RecordingRunStatus, string> RecordingRunCodeMap =
        new ReadOnlyDictionary<RecordingRunStatus, string>(new Dictionary<RecordingRunStatus, string>
        {
            [RecordingRunStatus.Created] = "created",
            [RecordingRunStatus.Preparing] = "preparing",
            [RecordingRunStatus.StartCommitted] = "start_committed",
            [RecordingRunStatus.Recording] = "recording",
            [RecordingRunStatus.StartedUnknown] = "started_unknown",
            [RecordingRunStatus.Finalizing] = "finalizing",
            [RecordingRunStatus.MediaReady] = "media_ready",
            [RecordingRunStatus.Settled] = "settled",
            [RecordingRunStatus.SessionInterrupted] = "session_interrupted",
            [RecordingRunStatus.Failed] = "failed",
        });

    private static readonly IReadOnlyDictionary<ConsentLeaseStatus, string> ConsentLeaseCodeMap =
        new ReadOnlyDictionary<ConsentLeaseStatus, string>(new Dictionary<ConsentLeaseStatus, string>
        {
            [ConsentLeaseStatus.Pending] = "pending",
            [ConsentLeaseStatus.Active] = "active",
            [ConsentLeaseStatus.Rejected] = "rejected",
            [ConsentLeaseStatus.Revoked] = "revoked",
            [ConsentLeaseStatus.Expired] = "expired",
            [ConsentLeaseStatus.Exhausted] = "exhausted",
        });

    private static readonly IReadOnlyDictionary<LeaseUseStatus, string> LeaseUseCodeMap =
        new ReadOnlyDictionary<LeaseUseStatus, string>(new Dictionary<LeaseUseStatus, string>
        {
            [LeaseUseStatus.Available] = "available",
            [LeaseUseStatus.Reserved] = "reserved",
            [LeaseUseStatus.StartCommitted] = "start_committed",
            [LeaseUseStatus.Consumed] = "consumed",
            [LeaseUseStatus.Settled] = "settled",
            [LeaseUseStatus.StartedUnknown] = "started_unknown",
        });

    public static string ToCode(PlanDefinitionStatus status) => ToCode(status, PlanDefinitionCodeMap);
    public static string ToCode(PlanOccurrenceStatus status) => ToCode(status, PlanOccurrenceCodeMap);
    public static string ToCode(RecordingRunStatus status) => ToCode(status, RecordingRunCodeMap);
    public static string ToCode(ConsentLeaseStatus status) => ToCode(status, ConsentLeaseCodeMap);
    public static string ToCode(LeaseUseStatus status) => ToCode(status, LeaseUseCodeMap);

    public static PlanDefinitionStatus ParsePlanDefinition(string code) => Parse(code, PlanDefinitionCodeMap, "plan_definition");
    public static PlanOccurrenceStatus ParsePlanOccurrence(string code) => Parse(code, PlanOccurrenceCodeMap, "plan_occurrence");
    public static RecordingRunStatus ParseRecordingRun(string code) => Parse(code, RecordingRunCodeMap, "recording_run");
    public static ConsentLeaseStatus ParseConsentLease(string code) => Parse(code, ConsentLeaseCodeMap, "consent_lease");
    public static LeaseUseStatus ParseLeaseUse(string code) => Parse(code, LeaseUseCodeMap, "lease_use");

    public static bool TryParsePlanDefinition(string? code, out PlanDefinitionStatus status) => TryParse(code, PlanDefinitionCodeMap, out status);
    public static bool TryParsePlanOccurrence(string? code, out PlanOccurrenceStatus status) => TryParse(code, PlanOccurrenceCodeMap, out status);
    public static bool TryParseRecordingRun(string? code, out RecordingRunStatus status) => TryParse(code, RecordingRunCodeMap, out status);
    public static bool TryParseConsentLease(string? code, out ConsentLeaseStatus status) => TryParse(code, ConsentLeaseCodeMap, out status);
    public static bool TryParseLeaseUse(string? code, out LeaseUseStatus status) => TryParse(code, LeaseUseCodeMap, out status);

    private static string ToCode<TStatus>(TStatus status, IReadOnlyDictionary<TStatus, string> map)
        where TStatus : struct, Enum
    {
        if (!map.TryGetValue(status, out var code))
        {
            throw new Phase3DomainException("unknown_state", $"Unknown {typeof(TStatus).Name} value.");
        }

        return code;
    }

    private static TStatus Parse<TStatus>(string? code, IReadOnlyDictionary<TStatus, string> map, string aggregate)
        where TStatus : struct, Enum
    {
        if (code is null || !map.Any(pair => string.Equals(pair.Value, code, StringComparison.Ordinal)))
        {
            throw new Phase3DomainException("unknown_state_code", $"Unknown {aggregate} state code.");
        }

        return map.First(pair => string.Equals(pair.Value, code, StringComparison.Ordinal)).Key;
    }

    private static bool TryParse<TStatus>(string? code, IReadOnlyDictionary<TStatus, string> map, out TStatus status)
        where TStatus : struct, Enum
    {
        foreach (var pair in map)
        {
            if (string.Equals(pair.Value, code, StringComparison.Ordinal))
            {
                status = pair.Key;
                return true;
            }
        }

        status = default;
        return false;
    }
}
