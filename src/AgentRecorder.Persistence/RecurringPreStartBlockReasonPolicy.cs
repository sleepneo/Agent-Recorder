namespace AgentRecorder.Persistence;

/// <summary>
/// Closed set of reasons that the recurring start-commit final recheck may
/// persist while releasing a reservation before start commit. Rejected,
/// request/clock/corruption and post-start lifecycle reasons are deliberately
/// excluded.
/// </summary>
internal static class RecurringPreStartBlockReasonPolicy
{
    private static readonly HashSet<string> KnownReasons = new(StringComparer.Ordinal)
    {
        "recurring_start_commit_plan_not_enabled",
        "recurring_start_commit_lease_pending",
        "recurring_start_commit_lease_rejected",
        "recurring_start_commit_lease_revoked",
        "recurring_start_commit_lease_expired",
        "recurring_start_commit_lease_exhausted",
        "unattended_disabled",
        "stop_all_boundary",
        "unattended_reenable_requires_new_authorization",
        "recurring_start_commit_approval_missing",
        "recurring_start_commit_approval_mismatch",
        "recurring_start_commit_after_latest_start",
        "recurring_start_commit_duration_exceeds_planned_end",
        "recurring_start_commit_duration_exceeds_lease",
        "execution_environment_unavailable",
        "execution_environment_missing",
        "execution_environment_mismatch",
        "execution_display_identity_unavailable",
        "execution_display_missing",
        "execution_display_ambiguous",
        "execution_display_metadata_unavailable",
        "execution_display_metadata_mismatch",
        "execution_topology_unavailable",
        "execution_topology_mismatch",
        "execution_region_invalid",
        "execution_output_environment_unavailable",
        "execution_output_directory_mismatch",
        "execution_output_file_path_mismatch",
        "execution_output_conflict_policy_mismatch",
        "execution_output_directory_unavailable",
        "execution_output_directory_unwritable",
        "execution_output_file_exists",
        "execution_disk_space_unavailable",
        "execution_disk_space_insufficient",
    };

    internal static bool IsKnown(string? reason) =>
        reason is not null && KnownReasons.Contains(reason);
}
