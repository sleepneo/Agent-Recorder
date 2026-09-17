using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace AgentRecorder.Core.Automation;

internal static class StandingSetupIntentCodes
{
    internal const string IntentKind = "standing_once_fixed_region";
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

    internal static bool IsStatus(string? value) => value is
        InitialStatus or LeaseApprovalPendingStatus or ActivatedStatus or RejectedStatus or ExpiredStatus;
}

internal enum StandingSetupIntentStatus
{
    RegionSelectionPending,
    LeaseApprovalPending,
    Activated,
    Rejected,
    Expired,
}

internal enum StandingSetupIntentResultStatus
{
    Created,
    Existing,
    Conflict,
    Rejected,
    Expired,
}

internal static class StandingSetupIntentStatusCodes
{
    internal static string ToCode(StandingSetupIntentStatus status) => status switch
    {
        StandingSetupIntentStatus.RegionSelectionPending => StandingSetupIntentCodes.InitialStatus,
        StandingSetupIntentStatus.LeaseApprovalPending => StandingSetupIntentCodes.LeaseApprovalPendingStatus,
        StandingSetupIntentStatus.Activated => StandingSetupIntentCodes.ActivatedStatus,
        StandingSetupIntentStatus.Rejected => StandingSetupIntentCodes.RejectedStatus,
        StandingSetupIntentStatus.Expired => StandingSetupIntentCodes.ExpiredStatus,
        _ => throw new ArgumentOutOfRangeException(nameof(status)),
    };

    internal static StandingSetupIntentStatus Parse(string value) => value switch
    {
        StandingSetupIntentCodes.InitialStatus => StandingSetupIntentStatus.RegionSelectionPending,
        StandingSetupIntentCodes.LeaseApprovalPendingStatus => StandingSetupIntentStatus.LeaseApprovalPending,
        StandingSetupIntentCodes.ActivatedStatus => StandingSetupIntentStatus.Activated,
        StandingSetupIntentCodes.RejectedStatus => StandingSetupIntentStatus.Rejected,
        StandingSetupIntentCodes.ExpiredStatus => StandingSetupIntentStatus.Expired,
        _ => throw new Phase3DomainException("setup_intent_status_invalid", "The setup intent status is not supported."),
    };
}

/// <summary>
/// Internal immutable request summary for the first standing setup step. It is
/// deliberately not an API DTO, authorization proof, or capture configuration.
/// The only production construction seam is the trusted adapter factory below.
/// </summary>
internal sealed class StandingSetupIntentSnapshot
{
    internal const int MaximumIdLength = 128;
    internal const int MaximumBindingLength = 256;
    internal const int MaximumPathLength = 4096;
    internal const int MaximumFileNameLength = 255;

    private StandingSetupIntentSnapshot(
        string intentId,
        string intentKindCode,
        string idempotencyKey,
        string requestDigest,
        string currentUserSid,
        string sessionBinding,
        DateTimeOffset requestedAtUtc,
        DateTimeOffset expiresAtUtc,
        DateTimeOffset scheduledStartUtc,
        DateTimeOffset latestStartUtc,
        DateTimeOffset plannedEndUtc,
        TimeSpan maximumDuration,
        DateTimeOffset leaseValidUntilUtc,
        string outputDirectory,
        string frozenFileName,
        string targetTypeCode,
        string audioModeCode,
        string backendCode,
        string captureSemanticsCode,
        string coordinateSpaceCode,
        string wakePolicyCode,
        string desktopRequirementCode,
        string outputConflictPolicyCode)
    {
        IntentId = intentId;
        IntentKindCode = intentKindCode;
        IdempotencyKey = idempotencyKey;
        RequestDigest = requestDigest;
        CurrentUserSid = currentUserSid;
        SessionBinding = sessionBinding;
        RequestedAtUtc = requestedAtUtc;
        ExpiresAtUtc = expiresAtUtc;
        ScheduledStartUtc = scheduledStartUtc;
        LatestStartUtc = latestStartUtc;
        PlannedEndUtc = plannedEndUtc;
        MaximumDuration = maximumDuration;
        LeaseValidUntilUtc = leaseValidUntilUtc;
        OutputDirectory = outputDirectory;
        FrozenFileName = frozenFileName;
        TargetTypeCode = targetTypeCode;
        AudioModeCode = audioModeCode;
        BackendCode = backendCode;
        CaptureSemanticsCode = captureSemanticsCode;
        CoordinateSpaceCode = coordinateSpaceCode;
        WakePolicyCode = wakePolicyCode;
        DesktopRequirementCode = desktopRequirementCode;
        OutputConflictPolicyCode = outputConflictPolicyCode;
    }

    internal string IntentId { get; }
    internal string IntentKindCode { get; }
    internal string IdempotencyKey { get; }
    internal string RequestDigest { get; }
    internal string CurrentUserSid { get; }
    internal string SessionBinding { get; }
    internal DateTimeOffset RequestedAtUtc { get; }
    internal DateTimeOffset ExpiresAtUtc { get; }
    internal DateTimeOffset ScheduledStartUtc { get; }
    internal DateTimeOffset LatestStartUtc { get; }
    internal DateTimeOffset PlannedEndUtc { get; }
    internal TimeSpan MaximumDuration { get; }
    internal DateTimeOffset LeaseValidUntilUtc { get; }
    internal string OutputDirectory { get; }
    internal string FrozenFileName { get; }
    internal string TargetTypeCode { get; }
    internal string AudioModeCode { get; }
    internal string BackendCode { get; }
    internal string CaptureSemanticsCode { get; }
    internal string CoordinateSpaceCode { get; }
    internal string WakePolicyCode { get; }
    internal string DesktopRequirementCode { get; }
    internal string OutputConflictPolicyCode { get; }

    internal static StandingSetupIntentSnapshot CreateForTrustedSetupAdapter(
        string intentId,
        string idempotencyKey,
        string currentUserSid,
        string sessionBinding,
        DateTimeOffset requestedAtUtc,
        DateTimeOffset expiresAtUtc,
        DateTimeOffset scheduledStartUtc,
        DateTimeOffset latestStartUtc,
        DateTimeOffset plannedEndUtc,
        TimeSpan maximumDuration,
        DateTimeOffset leaseValidUntilUtc,
        string outputDirectory,
        string frozenFileName)
    {
        var normalizedDirectory = AuthorizedFixedRegionScope.NormalizeOutputDirectoryForAuthorization(outputDirectory);
        var normalizedFileName = AuthorizedFixedRegionScope.NormalizeFrozenFileNameForAuthorization(frozenFileName);
        var snapshot = CreateRaw(
            intentId,
            StandingSetupIntentCodes.IntentKind,
            idempotencyKey,
            requestDigest: null,
            currentUserSid,
            sessionBinding,
            requestedAtUtc,
            expiresAtUtc,
            scheduledStartUtc,
            latestStartUtc,
            plannedEndUtc,
            maximumDuration,
            leaseValidUntilUtc,
            normalizedDirectory,
            normalizedFileName,
            StandingSetupIntentCodes.FixedRegionTarget,
            StandingSetupIntentCodes.AudioNone,
            StandingSetupIntentCodes.FfmpegRegionBackend,
            StandingSetupIntentCodes.DesktopRegionSemantics,
            StandingSetupIntentCodes.PhysicalVirtualScreen,
            StandingSetupIntentCodes.NaturalWakeOnly,
            StandingSetupIntentCodes.InteractiveDesktopRequired,
            StandingSetupIntentCodes.FailIfExists);
        return WithDigest(snapshot, snapshot.ComputeCanonicalDigest());
    }

    /// <summary>
    /// Test-only construction seam. It intentionally permits malformed values
    /// so the service can prove that invalid trusted-adapter output fails closed.
    /// </summary>
    internal static StandingSetupIntentSnapshot CreateForTest(
        string intentId,
        string idempotencyKey,
        string currentUserSid,
        string sessionBinding,
        DateTimeOffset requestedAtUtc,
        DateTimeOffset expiresAtUtc,
        DateTimeOffset scheduledStartUtc,
        DateTimeOffset latestStartUtc,
        DateTimeOffset plannedEndUtc,
        TimeSpan maximumDuration,
        DateTimeOffset leaseValidUntilUtc,
        string outputDirectory,
        string frozenFileName,
        string intentKindCode = StandingSetupIntentCodes.IntentKind,
        string targetTypeCode = StandingSetupIntentCodes.FixedRegionTarget,
        string audioModeCode = StandingSetupIntentCodes.AudioNone,
        string backendCode = StandingSetupIntentCodes.FfmpegRegionBackend,
        string captureSemanticsCode = StandingSetupIntentCodes.DesktopRegionSemantics,
        string coordinateSpaceCode = StandingSetupIntentCodes.PhysicalVirtualScreen,
        string wakePolicyCode = StandingSetupIntentCodes.NaturalWakeOnly,
        string desktopRequirementCode = StandingSetupIntentCodes.InteractiveDesktopRequired,
        string outputConflictPolicyCode = StandingSetupIntentCodes.FailIfExists,
        string? requestDigest = null) =>
        CreateRaw(
            intentId,
            intentKindCode,
            idempotencyKey,
            requestDigest,
            currentUserSid,
            sessionBinding,
            requestedAtUtc,
            expiresAtUtc,
            scheduledStartUtc,
            latestStartUtc,
            plannedEndUtc,
            maximumDuration,
            leaseValidUntilUtc,
            outputDirectory,
            frozenFileName,
            targetTypeCode,
            audioModeCode,
            backendCode,
            captureSemanticsCode,
            coordinateSpaceCode,
            wakePolicyCode,
            desktopRequirementCode,
            outputConflictPolicyCode);

    internal static StandingSetupIntentSnapshot CreateForPersistence(
        string intentId,
        string idempotencyKey,
        string currentUserSid,
        string sessionBinding,
        DateTimeOffset requestedAtUtc,
        DateTimeOffset expiresAtUtc,
        DateTimeOffset scheduledStartUtc,
        DateTimeOffset latestStartUtc,
        DateTimeOffset plannedEndUtc,
        TimeSpan maximumDuration,
        DateTimeOffset leaseValidUntilUtc,
        string outputDirectory,
        string frozenFileName,
        string requestDigest) =>
        CreateRaw(
            intentId,
            StandingSetupIntentCodes.IntentKind,
            idempotencyKey,
            requestDigest,
            currentUserSid,
            sessionBinding,
            requestedAtUtc,
            expiresAtUtc,
            scheduledStartUtc,
            latestStartUtc,
            plannedEndUtc,
            maximumDuration,
            leaseValidUntilUtc,
            outputDirectory,
            frozenFileName,
            StandingSetupIntentCodes.FixedRegionTarget,
            StandingSetupIntentCodes.AudioNone,
            StandingSetupIntentCodes.FfmpegRegionBackend,
            StandingSetupIntentCodes.DesktopRegionSemantics,
            StandingSetupIntentCodes.PhysicalVirtualScreen,
            StandingSetupIntentCodes.NaturalWakeOnly,
            StandingSetupIntentCodes.InteractiveDesktopRequired,
            StandingSetupIntentCodes.FailIfExists);

    internal string ComputeCanonicalDigest() => ComputeDigest(this);

    private static StandingSetupIntentSnapshot CreateRaw(
        string intentId,
        string intentKindCode,
        string idempotencyKey,
        string? requestDigest,
        string currentUserSid,
        string sessionBinding,
        DateTimeOffset requestedAtUtc,
        DateTimeOffset expiresAtUtc,
        DateTimeOffset scheduledStartUtc,
        DateTimeOffset latestStartUtc,
        DateTimeOffset plannedEndUtc,
        TimeSpan maximumDuration,
        DateTimeOffset leaseValidUntilUtc,
        string outputDirectory,
        string frozenFileName,
        string targetTypeCode,
        string audioModeCode,
        string backendCode,
        string captureSemanticsCode,
        string coordinateSpaceCode,
        string wakePolicyCode,
        string desktopRequirementCode,
        string outputConflictPolicyCode) =>
        new(
            intentId,
            intentKindCode,
            idempotencyKey,
            requestDigest ?? string.Empty,
            currentUserSid,
            sessionBinding,
            requestedAtUtc,
            expiresAtUtc,
            scheduledStartUtc,
            latestStartUtc,
            plannedEndUtc,
            maximumDuration,
            leaseValidUntilUtc,
            outputDirectory,
            frozenFileName,
            targetTypeCode,
            audioModeCode,
            backendCode,
            captureSemanticsCode,
            coordinateSpaceCode,
            wakePolicyCode,
            desktopRequirementCode,
            outputConflictPolicyCode);

    private static StandingSetupIntentSnapshot WithDigest(
        StandingSetupIntentSnapshot source,
        string digest) => new(
            source.IntentId,
            source.IntentKindCode,
            source.IdempotencyKey,
            digest,
            source.CurrentUserSid,
            source.SessionBinding,
            source.RequestedAtUtc,
            source.ExpiresAtUtc,
            source.ScheduledStartUtc,
            source.LatestStartUtc,
            source.PlannedEndUtc,
            source.MaximumDuration,
            source.LeaseValidUntilUtc,
            source.OutputDirectory,
            source.FrozenFileName,
            source.TargetTypeCode,
            source.AudioModeCode,
            source.BackendCode,
            source.CaptureSemanticsCode,
            source.CoordinateSpaceCode,
            source.WakePolicyCode,
            source.DesktopRequirementCode,
            source.OutputConflictPolicyCode);

    private static string ComputeDigest(StandingSetupIntentSnapshot snapshot)
    {
        var bytes = new List<byte>(512);
        AppendString(bytes, "agent-recorder-standing-setup-intent-v1");
        AppendString(bytes, snapshot.IntentKindCode);
        AppendString(bytes, snapshot.TargetTypeCode);
        AppendString(bytes, snapshot.CaptureSemanticsCode);
        AppendString(bytes, snapshot.CoordinateSpaceCode);
        AppendString(bytes, "region_selection_required");
        AppendString(bytes, snapshot.AudioModeCode);
        AppendString(bytes, snapshot.BackendCode);
        AppendInt64(bytes, snapshot.ScheduledStartUtc.UtcDateTime.Ticks);
        AppendInt64(bytes, snapshot.LatestStartUtc.UtcDateTime.Ticks);
        AppendInt64(bytes, snapshot.PlannedEndUtc.UtcDateTime.Ticks);
        AppendInt64(bytes, snapshot.MaximumDuration.Ticks);
        AppendInt64(bytes, snapshot.LeaseValidUntilUtc.UtcDateTime.Ticks);
        AppendInt64(bytes, snapshot.ExpiresAtUtc.UtcDateTime.Ticks);
        AppendString(bytes, snapshot.WakePolicyCode);
        AppendString(bytes, snapshot.DesktopRequirementCode);
        AppendString(bytes, snapshot.OutputDirectory);
        AppendString(bytes, snapshot.FrozenFileName);
        AppendString(bytes, snapshot.OutputConflictPolicyCode);
        return Convert.ToHexString(SHA256.HashData(bytes.ToArray())).ToLowerInvariant();
    }

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

internal sealed class StandingSetupIntentResult
{
    private StandingSetupIntentResult(
        StandingSetupIntentResultStatus result,
        StandingSetupIntentStatus intentStatus,
        string reason,
        string? intentId,
        bool changed)
    {
        Result = result;
        IntentStatus = intentStatus;
        Reason = reason;
        IntentId = intentId;
        Changed = changed;
    }

    internal StandingSetupIntentResultStatus Result { get; }
    internal StandingSetupIntentStatus IntentStatus { get; }
    internal string Reason { get; }
    internal string? IntentId { get; }
    internal bool Changed { get; }

    internal static StandingSetupIntentResult Created(string intentId) =>
        new(StandingSetupIntentResultStatus.Created, StandingSetupIntentStatus.RegionSelectionPending, "created", intentId, true);

    internal static StandingSetupIntentResult Existing(string intentId, StandingSetupIntentStatus status) =>
        new(StandingSetupIntentResultStatus.Existing, status, "existing", intentId, false);

    internal static StandingSetupIntentResult Expired(string intentId) =>
        new(StandingSetupIntentResultStatus.Expired, StandingSetupIntentStatus.Expired, "expired", intentId, false);

    internal static StandingSetupIntentResult Conflict(StandingSetupIntentSnapshot? snapshot, string reason) =>
        new(StandingSetupIntentResultStatus.Conflict, StandingSetupIntentStatus.RegionSelectionPending, reason, snapshot?.IntentId, false);

    internal static StandingSetupIntentResult Rejected(StandingSetupIntentSnapshot? snapshot, string reason) =>
        new(StandingSetupIntentResultStatus.Rejected, StandingSetupIntentStatus.RegionSelectionPending, reason, snapshot?.IntentId, false);
}
