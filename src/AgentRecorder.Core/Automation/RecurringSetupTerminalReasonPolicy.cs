namespace AgentRecorder.Core.Automation;

internal static class RecurringSetupTerminalReasonPolicy
{
    internal const string IntentExpired = "recurring_setup_intent_expired";

    internal static bool Allows(RecurringSetupIntentStatus status, string? reason) => status switch
    {
        RecurringSetupIntentStatus.Expired => reason == IntentExpired,
        RecurringSetupIntentStatus.Rejected => reason is
            "region_selection_cancelled" or "region_selection_timed_out" or "region_selection_invalid" or
            "lease_rejected_by_user" or "lease_approval_timed_out" or "interactive_desktop_unavailable" or
            "unattended_disabled" or "recurring_execution_unavailable" or "setup_conflict",
        _ => false,
    };
}
