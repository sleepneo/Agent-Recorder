namespace AgentRecorder.Api;

/// <summary>
/// API-facing boundary for recurring setup. Implementations are composed by
/// the interactive tray host; callers cannot supply authorization evidence.
/// </summary>
public interface IRecurringPlanSetupGateway
{
    bool IsInteractiveDesktopAvailable { get; }
    bool IsUnattendedEnabled { get; }

    /// <summary>True only while the complete production recurring runtime is healthy.</summary>
    bool IsExecutionSupported => false;

    RecurringPlanSetupCreateResult CreateOrGet(RecurringPlanApiRequest request);
    RecurringPlanSetupState? Get(string setupIntentId);
}

public enum RecurringPlanSetupCreateStatus
{
    Created,
    Existing,
    Conflict,
    Rejected,
    Expired,
}

public sealed record RecurringPlanSetupCreateResult(
    RecurringPlanSetupCreateStatus Status,
    RecurringPlanSetupState? State,
    string ReasonCode);

public sealed record RecurringPlanSetupState(
    string SetupIntentId,
    string Status,
    long StatusVersion,
    string StatusVersionCursor,
    string? PlanId,
    string? LeaseId,
    bool RequiresLocalAction,
    string? NextAction,
    string? ReasonCode);
