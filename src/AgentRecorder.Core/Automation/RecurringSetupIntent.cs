using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace AgentRecorder.Core.Automation;

internal static class RecurringSetupIntentCodes
{
    internal const string IntentKind = "recurring_daily_weekly_fixed_region";
    internal const string InitialStatus = "region_selection_pending";
    internal const string LeaseApprovalPendingStatus = "lease_approval_pending";
    internal const string ActivatedStatus = "activated";
    internal const string RejectedStatus = "rejected";
    internal const string ExpiredStatus = "expired";
    internal const string FixedRegionTarget = "fixed_region";
    internal const string AudioNone = "none";
    internal const string FfmpegRegionBackend = "ffmpeg-region";
    internal const string DesktopRegionSemantics = "desktop_region";
    internal const string PhysicalVirtualScreen = "physical_virtual_screen";
    internal const string NaturalWakeOnly = "natural_wake_only";
    internal const string InteractiveDesktopRequired = "interactive_desktop_required";
    internal const string FailIfExists = "fail_if_exists";
}

internal enum RecurringSetupIntentStatus
{
    RegionSelectionPending,
    LeaseApprovalPending,
    Activated,
    Rejected,
    Expired,
}

internal static class RecurringSetupIntentStatusCodes
{
    internal static string ToCode(RecurringSetupIntentStatus value) => value switch
    {
        RecurringSetupIntentStatus.RegionSelectionPending => RecurringSetupIntentCodes.InitialStatus,
        RecurringSetupIntentStatus.LeaseApprovalPending => RecurringSetupIntentCodes.LeaseApprovalPendingStatus,
        RecurringSetupIntentStatus.Activated => RecurringSetupIntentCodes.ActivatedStatus,
        RecurringSetupIntentStatus.Rejected => RecurringSetupIntentCodes.RejectedStatus,
        RecurringSetupIntentStatus.Expired => RecurringSetupIntentCodes.ExpiredStatus,
        _ => throw new ArgumentOutOfRangeException(nameof(value)),
    };

    internal static RecurringSetupIntentStatus Parse(string value) => value switch
    {
        RecurringSetupIntentCodes.InitialStatus => RecurringSetupIntentStatus.RegionSelectionPending,
        RecurringSetupIntentCodes.LeaseApprovalPendingStatus => RecurringSetupIntentStatus.LeaseApprovalPending,
        RecurringSetupIntentCodes.ActivatedStatus => RecurringSetupIntentStatus.Activated,
        RecurringSetupIntentCodes.RejectedStatus => RecurringSetupIntentStatus.Rejected,
        RecurringSetupIntentCodes.ExpiredStatus => RecurringSetupIntentStatus.Expired,
        _ => throw new Phase3DomainException("recurring_setup_intent_status_invalid", "The recurring setup intent status is not supported."),
    };
}

internal enum RecurringSetupIntentResultStatus
{
    Created,
    Existing,
    Conflict,
    Rejected,
    Expired,
}

internal sealed class RecurringSetupIntentSnapshot
{
    internal const int MaximumIdLength = 128;
    internal const int MaximumBindingLength = 256;
    internal const int MaximumDigestLength = 128;

    private RecurringSetupIntentSnapshot(
        string intentId,
        string intentKindCode,
        string idempotencyKey,
        string requestDigest,
        string currentUserSid,
        string sessionBinding,
        DateTimeOffset requestedAtUtc,
        DateTimeOffset expiresAtUtc,
        RecurringPlanSchedule schedule,
        string scheduleDigest,
        string timeZoneRulesDigest,
        int maxUses,
        TimeSpan maxCumulativeDuration,
        DateTimeOffset leaseValidUntilUtc,
        string outputDirectory,
        string filenamePrefix)
    {
        IntentId = intentId;
        IntentKindCode = intentKindCode;
        IdempotencyKey = idempotencyKey;
        RequestDigest = requestDigest;
        CurrentUserSid = currentUserSid;
        SessionBinding = sessionBinding;
        RequestedAtUtc = requestedAtUtc;
        ExpiresAtUtc = expiresAtUtc;
        Schedule = schedule;
        ScheduleDigest = scheduleDigest;
        TimeZoneRulesDigest = timeZoneRulesDigest;
        MaxUses = maxUses;
        MaxCumulativeDuration = maxCumulativeDuration;
        LeaseValidUntilUtc = leaseValidUntilUtc;
        OutputDirectory = outputDirectory;
        FilenamePrefix = filenamePrefix;
    }

    internal string IntentId { get; }
    internal string IntentKindCode { get; }
    internal string IdempotencyKey { get; }
    internal string RequestDigest { get; }
    internal string CurrentUserSid { get; }
    internal string SessionBinding { get; }
    internal DateTimeOffset RequestedAtUtc { get; }
    internal DateTimeOffset ExpiresAtUtc { get; }
    internal RecurringPlanSchedule Schedule { get; }
    internal string ScheduleDigest { get; }
    internal string TimeZoneRulesDigest { get; }
    internal int MaxUses { get; }
    internal TimeSpan MaxCumulativeDuration { get; }
    internal DateTimeOffset LeaseValidUntilUtc { get; }
    internal string OutputDirectory { get; }
    internal string FilenamePrefix { get; }
    internal string TargetTypeCode => RecurringSetupIntentCodes.FixedRegionTarget;
    internal string AudioModeCode => RecurringSetupIntentCodes.AudioNone;
    internal string BackendCode => RecurringSetupIntentCodes.FfmpegRegionBackend;
    internal string CaptureSemanticsCode => RecurringSetupIntentCodes.DesktopRegionSemantics;
    internal string CoordinateSpaceCode => RecurringSetupIntentCodes.PhysicalVirtualScreen;
    internal string WakePolicyCode => RecurringSetupIntentCodes.NaturalWakeOnly;
    internal string DesktopRequirementCode => RecurringSetupIntentCodes.InteractiveDesktopRequired;
    internal string OutputConflictPolicyCode => RecurringSetupIntentCodes.FailIfExists;

    internal static RecurringSetupIntentSnapshot CreateForTrustedSetupAdapter(
        string intentId,
        string idempotencyKey,
        string currentUserSid,
        string sessionBinding,
        DateTimeOffset requestedAtUtc,
        DateTimeOffset expiresAtUtc,
        RecurringPlanSchedule schedule,
        int maxUses,
        TimeSpan maxCumulativeDuration,
        DateTimeOffset leaseValidUntilUtc,
        string outputDirectory,
        string filenamePrefix)
    {
        ArgumentNullException.ThrowIfNull(schedule);
        if (RecurringPlanSetupLimits.ContainsTraversalPathSegment(outputDirectory))
            throw new Phase3DomainException("recurring_setup_intent_output_directory_traversal", "The recurring output directory must not contain traversal segments.");
        var normalizedDirectory = AuthorizedFixedRegionScope.NormalizeOutputDirectoryForAuthorization(outputDirectory);
        var snapshot = CreateRaw(
            intentId, RecurringSetupIntentCodes.IntentKind, idempotencyKey, string.Empty,
            currentUserSid, sessionBinding, requestedAtUtc, expiresAtUtc, schedule,
            schedule.CanonicalDigest, RecurringTimeZoneRulesDigest.Compute(schedule.TimeZoneInfo),
            maxUses, maxCumulativeDuration, leaseValidUntilUtc, normalizedDirectory, filenamePrefix);
        return WithDigest(snapshot, snapshot.ComputeCanonicalDigest());
    }

    internal static RecurringSetupIntentSnapshot CreateForTest(
        string intentId,
        string idempotencyKey,
        string currentUserSid,
        string sessionBinding,
        DateTimeOffset requestedAtUtc,
        DateTimeOffset expiresAtUtc,
        RecurringPlanSchedule schedule,
        int maxUses,
        TimeSpan maxCumulativeDuration,
        DateTimeOffset leaseValidUntilUtc,
        string outputDirectory,
        string filenamePrefix,
        string? requestDigest = null,
        string? scheduleDigest = null,
        string? timeZoneRulesDigest = null,
        string intentKindCode = RecurringSetupIntentCodes.IntentKind) =>
        CreateRaw(intentId, intentKindCode, idempotencyKey, requestDigest ?? string.Empty,
            currentUserSid, sessionBinding, requestedAtUtc, expiresAtUtc, schedule,
            scheduleDigest ?? schedule.CanonicalDigest,
            timeZoneRulesDigest ?? RecurringTimeZoneRulesDigest.Compute(schedule.TimeZoneInfo),
            maxUses, maxCumulativeDuration, leaseValidUntilUtc, outputDirectory, filenamePrefix);

    internal static RecurringSetupIntentSnapshot CreateForPersistence(
        string intentId,
        string idempotencyKey,
        string currentUserSid,
        string sessionBinding,
        DateTimeOffset requestedAtUtc,
        DateTimeOffset expiresAtUtc,
        RecurringPlanSchedule schedule,
        string scheduleDigest,
        string timeZoneRulesDigest,
        int maxUses,
        TimeSpan maxCumulativeDuration,
        DateTimeOffset leaseValidUntilUtc,
        string outputDirectory,
        string filenamePrefix,
        string requestDigest) =>
        CreateRaw(intentId, RecurringSetupIntentCodes.IntentKind, idempotencyKey, requestDigest,
            currentUserSid, sessionBinding, requestedAtUtc, expiresAtUtc, schedule,
            scheduleDigest, timeZoneRulesDigest, maxUses, maxCumulativeDuration,
            leaseValidUntilUtc, outputDirectory, filenamePrefix);

    internal string ComputeCanonicalDigest()
    {
        var bytes = new List<byte>(1024);
        AppendString(bytes, "agent-recorder-recurring-setup-intent-v1");
        AppendString(bytes, "POST");
        AppendString(bytes, "/api/v1/plans");
        AppendString(bytes, IdempotencyKey);
        AppendString(bytes, CurrentUserSid);
        AppendString(bytes, SessionBinding);
        AppendString(bytes, IntentKindCode);
        AppendString(bytes, TargetTypeCode);
        AppendString(bytes, CaptureSemanticsCode);
        AppendString(bytes, CoordinateSpaceCode);
        AppendString(bytes, AudioModeCode);
        AppendString(bytes, BackendCode);
        AppendString(bytes, ScheduleDigest);
        AppendString(bytes, TimeZoneRulesDigest);
        AppendString(bytes, Schedule.Kind.ToString());
        AppendString(bytes, Schedule.TimeZoneId);
        AppendInt64(bytes, Schedule.LocalStartDate.DayNumber);
        AppendInt64(bytes, Schedule.LocalEndDate.DayNumber);
        AppendInt64(bytes, Schedule.LocalWallClockTime.Ticks);
        AppendInt64(bytes, Schedule.WeekdayMask);
        AppendInt64(bytes, Schedule.MaximumOccurrences);
        AppendInt64(bytes, Schedule.RecordingDuration.Ticks);
        AppendInt64(bytes, Schedule.LatestStartGrace.Ticks);
        AppendInt64(bytes, MaxUses);
        AppendInt64(bytes, MaxCumulativeDuration.Ticks);
        AppendInt64(bytes, LeaseValidUntilUtc.UtcDateTime.Ticks);
        AppendInt64(bytes, ExpiresAtUtc.UtcDateTime.Ticks);
        AppendString(bytes, WakePolicyCode);
        AppendString(bytes, DesktopRequirementCode);
        AppendString(bytes, OutputDirectory);
        AppendString(bytes, FilenamePrefix);
        AppendString(bytes, OutputConflictPolicyCode);
        return Convert.ToHexString(SHA256.HashData(bytes.ToArray())).ToLowerInvariant();
    }

    private static RecurringSetupIntentSnapshot CreateRaw(
        string intentId, string intentKindCode, string idempotencyKey, string requestDigest,
        string currentUserSid, string sessionBinding, DateTimeOffset requestedAtUtc,
        DateTimeOffset expiresAtUtc, RecurringPlanSchedule schedule, string scheduleDigest,
        string timeZoneRulesDigest, int maxUses, TimeSpan maxCumulativeDuration,
        DateTimeOffset leaseValidUntilUtc, string outputDirectory, string filenamePrefix) =>
        new(intentId, intentKindCode, idempotencyKey, requestDigest, currentUserSid, sessionBinding,
            requestedAtUtc, expiresAtUtc, schedule, scheduleDigest, timeZoneRulesDigest, maxUses,
            maxCumulativeDuration, leaseValidUntilUtc, outputDirectory, filenamePrefix);

    private static RecurringSetupIntentSnapshot WithDigest(RecurringSetupIntentSnapshot value, string digest) =>
        CreateRaw(value.IntentId, value.IntentKindCode, value.IdempotencyKey, digest, value.CurrentUserSid,
            value.SessionBinding, value.RequestedAtUtc, value.ExpiresAtUtc, value.Schedule, value.ScheduleDigest,
            value.TimeZoneRulesDigest, value.MaxUses, value.MaxCumulativeDuration, value.LeaseValidUntilUtc,
            value.OutputDirectory, value.FilenamePrefix);

    private static void AppendString(List<byte> destination, string value)
    {
        var encoded = Encoding.UTF8.GetBytes(value ?? string.Empty);
        AppendInt64(destination, encoded.Length);
        destination.AddRange(encoded);
    }

    private static void AppendInt64(List<byte> destination, long value)
    {
        Span<byte> buffer = stackalloc byte[sizeof(long)];
        BinaryPrimitives.WriteInt64LittleEndian(buffer, value);
        destination.AddRange(buffer.ToArray());
    }
}

internal sealed class RecurringSetupIntentResult
{
    private RecurringSetupIntentResult(RecurringSetupIntentResultStatus result, RecurringSetupIntentStatus status, string reason, string? intentId, bool changed)
    {
        Result = result;
        IntentStatus = status;
        Reason = reason;
        IntentId = intentId;
        Changed = changed;
    }

    internal RecurringSetupIntentResultStatus Result { get; }
    internal RecurringSetupIntentStatus IntentStatus { get; }
    internal string Reason { get; }
    internal string? IntentId { get; }
    internal bool Changed { get; }

    internal static RecurringSetupIntentResult Created(string id) => new(RecurringSetupIntentResultStatus.Created, RecurringSetupIntentStatus.RegionSelectionPending, "created", id, true);
    internal static RecurringSetupIntentResult Existing(string id, RecurringSetupIntentStatus status) => new(RecurringSetupIntentResultStatus.Existing, status, "existing", id, false);
    internal static RecurringSetupIntentResult Conflict(RecurringSetupIntentSnapshot snapshot, string reason) => new(RecurringSetupIntentResultStatus.Conflict, RecurringSetupIntentStatus.RegionSelectionPending, reason, snapshot.IntentId, false);
    internal static RecurringSetupIntentResult Rejected(RecurringSetupIntentSnapshot? snapshot, string reason) => new(RecurringSetupIntentResultStatus.Rejected, RecurringSetupIntentStatus.Rejected, reason, snapshot?.IntentId, false);
    internal static RecurringSetupIntentResult Expired(string id) => new(RecurringSetupIntentResultStatus.Expired, RecurringSetupIntentStatus.Expired, "setup_intent_expired", id, false);
}
