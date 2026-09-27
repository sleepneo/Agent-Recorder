using System.Security.Cryptography;
using System.Text;
using AgentRecorder.Capture;
using AgentRecorder.Core.Automation;

namespace AgentRecorder.Core;

/// <summary>
/// Frozen, Lease-independent fixed-region input to one required-once run.
/// Instances are created only by trusted App/Persistence composition after
/// the durable confirmation/start-commit sequence.
/// </summary>
internal sealed class RequiredOnceCaptureExecutionSpecification
{
    internal RequiredOnceCaptureExecutionSpecification(
        string planId,
        string occurrenceId,
        string setupIntentId,
        string creationApprovalId,
        string currentUserSid,
        string sessionBinding,
        DateTimeOffset scheduledStartUtc,
        DateTimeOffset latestStartUtc,
        DateTimeOffset plannedEndUtc,
        TimeSpan duration,
        string outputDirectory,
        string frozenFileName,
        string stableDisplayFingerprint,
        AuthorizedPhysicalRectangle displayBounds,
        AuthorizedPhysicalRectangle regionWithinDisplay,
        int dpiX,
        int dpiY,
        int physicalWidth,
        int physicalHeight,
        AuthorizedDisplayOrientation orientation,
        string topologyDigest)
    {
        PlanId = Required(planId, nameof(planId));
        OccurrenceId = Required(occurrenceId, nameof(occurrenceId));
        SetupIntentId = Required(setupIntentId, nameof(setupIntentId));
        CreationApprovalId = Required(creationApprovalId, nameof(creationApprovalId));
        CurrentUserSid = Required(currentUserSid, nameof(currentUserSid));
        SessionBinding = Required(sessionBinding, nameof(sessionBinding));
        if (scheduledStartUtc.Offset != TimeSpan.Zero || latestStartUtc.Offset != TimeSpan.Zero ||
            plannedEndUtc.Offset != TimeSpan.Zero || scheduledStartUtc >= latestStartUtc ||
            latestStartUtc > plannedEndUtc || duration <= TimeSpan.Zero ||
            duration > TimeSpan.FromMinutes(10) || duration.Ticks % TimeSpan.TicksPerSecond != 0 ||
            plannedEndUtc - scheduledStartUtc < duration)
            throw new ArgumentException("The required-once capture time window is invalid.");
        ScheduledStartUtc = scheduledStartUtc;
        LatestStartUtc = latestStartUtc;
        PlannedEndUtc = plannedEndUtc;
        Duration = duration;
        OutputDirectory = Required(outputDirectory, nameof(outputDirectory));
        FrozenFileName = Required(frozenFileName, nameof(frozenFileName));
        StableDisplayFingerprint = Required(stableDisplayFingerprint, nameof(stableDisplayFingerprint));
        TopologyDigest = RequireDigest(topologyDigest, nameof(topologyDigest));
        if (displayBounds.Width <= 0 || displayBounds.Height <= 0 ||
            regionWithinDisplay.X < 0 || regionWithinDisplay.Y < 0 ||
            regionWithinDisplay.Width <= 0 || regionWithinDisplay.Height <= 0 ||
            (long)regionWithinDisplay.X + regionWithinDisplay.Width > displayBounds.Width ||
            (long)regionWithinDisplay.Y + regionWithinDisplay.Height > displayBounds.Height ||
            dpiX <= 0 || dpiY <= 0 || physicalWidth != displayBounds.Width ||
            physicalHeight != displayBounds.Height || !Enum.IsDefined(orientation))
            throw new ArgumentException("The required-once physical display region is invalid.");
        DisplayBounds = displayBounds;
        RegionWithinDisplay = regionWithinDisplay;
        DpiX = dpiX;
        DpiY = dpiY;
        PhysicalWidth = physicalWidth;
        PhysicalHeight = physicalHeight;
        Orientation = orientation;
        FrozenOutputFilePath = Path.GetFullPath(Path.Combine(outputDirectory, frozenFileName));
        SpecificationDigest = ComputeDigest();
    }

    internal string PlanId { get; }
    internal string OccurrenceId { get; }
    internal string SetupIntentId { get; }
    internal string CreationApprovalId { get; }
    internal string CurrentUserSid { get; }
    internal string SessionBinding { get; }
    internal DateTimeOffset ScheduledStartUtc { get; }
    internal DateTimeOffset LatestStartUtc { get; }
    internal DateTimeOffset PlannedEndUtc { get; }
    internal TimeSpan Duration { get; }
    internal string OutputDirectory { get; }
    internal string FrozenFileName { get; }
    internal string FrozenOutputFilePath { get; }
    internal string StableDisplayFingerprint { get; }
    internal AuthorizedPhysicalRectangle DisplayBounds { get; }
    internal AuthorizedPhysicalRectangle RegionWithinDisplay { get; }
    internal int DpiX { get; }
    internal int DpiY { get; }
    internal int PhysicalWidth { get; }
    internal int PhysicalHeight { get; }
    internal AuthorizedDisplayOrientation Orientation { get; }
    internal string TopologyDigest { get; }
    internal string SpecificationDigest { get; }

    internal AuthorizedPhysicalRectangle AbsoluteRegion => new(
        checked(DisplayBounds.X + RegionWithinDisplay.X),
        checked(DisplayBounds.Y + RegionWithinDisplay.Y),
        RegionWithinDisplay.Width,
        RegionWithinDisplay.Height);

    private string ComputeDigest()
    {
        var fields = new[]
        {
            "required-once-fixed-region/v1", PlanId, OccurrenceId, SetupIntentId, CreationApprovalId,
            CurrentUserSid, SessionBinding, ScheduledStartUtc.UtcDateTime.Ticks.ToString(),
            LatestStartUtc.UtcDateTime.Ticks.ToString(), PlannedEndUtc.UtcDateTime.Ticks.ToString(),
            Duration.Ticks.ToString(), OutputDirectory, FrozenFileName, StableDisplayFingerprint,
            DisplayBounds.X.ToString(), DisplayBounds.Y.ToString(), DisplayBounds.Width.ToString(), DisplayBounds.Height.ToString(),
            RegionWithinDisplay.X.ToString(), RegionWithinDisplay.Y.ToString(), RegionWithinDisplay.Width.ToString(), RegionWithinDisplay.Height.ToString(),
            DpiX.ToString(), DpiY.ToString(), PhysicalWidth.ToString(), PhysicalHeight.ToString(),
            ((int)Orientation).ToString(), TopologyDigest, "ffmpeg-region", "none", "0", "fail_if_exists",
        };
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join("\n", fields))))
            .ToLowerInvariant();
    }

    private static string Required(string? value, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value) || value != value.Trim() || value.Any(char.IsControl))
            throw new ArgumentException("Required-once specification values must be canonical.", parameterName);
        return value;
    }

    private static string RequireDigest(string value, string parameterName)
    {
        if (value is null || value.Length != 64 || value.Any(character =>
                !(character is >= '0' and <= '9' or >= 'a' and <= 'f')))
            throw new ArgumentException("A lowercase SHA-256 digest is required.", parameterName);
        return value;
    }
}

/// <summary>Single process-local handoff for an already committed Run.</summary>
internal sealed class RequiredOnceCaptureExecutionTicket
{
    private int _claimed;
    private RequiredOnceExecutionProof? _proof;

    internal RequiredOnceCaptureExecutionTicket(
        RequiredOnceCaptureExecutionSpecification specification,
        string executionId,
        string runId,
        string executionApprovalId,
        string proofId,
        string proofNonce,
        DateTimeOffset committedAtUtc,
        Confirmation confirmation)
    {
        Specification = specification ?? throw new ArgumentNullException(nameof(specification));
        ExecutionId = Require(executionId, nameof(executionId));
        RunId = Require(runId, nameof(runId));
        ExecutionApprovalId = Require(executionApprovalId, nameof(executionApprovalId));
        ProofId = Require(proofId, nameof(proofId));
        ProofNonce = RequireNonce(proofNonce);
        if (committedAtUtc.Offset != TimeSpan.Zero || committedAtUtc < specification.ScheduledStartUtc ||
            committedAtUtc >= specification.LatestStartUtc)
            throw new ArgumentOutOfRangeException(nameof(committedAtUtc));
        CommittedAtUtc = committedAtUtc;
        Confirmation = confirmation ?? throw new ArgumentNullException(nameof(confirmation));
        if (confirmation.Id != ExecutionApprovalId || confirmation.RecordingId != RunId ||
            confirmation.Status != "approved")
            throw new ArgumentException("The local approval does not match the committed required-once ticket.", nameof(confirmation));
    }

    internal RequiredOnceCaptureExecutionSpecification Specification { get; }
    internal string ExecutionId { get; }
    internal string RunId { get; }
    internal string ExecutionApprovalId { get; }
    internal string ProofId { get; }
    internal string ProofNonce { get; }
    internal DateTimeOffset CommittedAtUtc { get; }
    internal RequiredOnceExecutionProof? Proof => Volatile.Read(ref _proof);
    internal Confirmation Confirmation { get; }
    internal bool IsClaimed => Volatile.Read(ref _claimed) != 0;
    internal bool IsProofConsumed => Proof?.IsConsumed == true;

    internal void AttachProof(RequiredOnceExecutionProof proof)
    {
        ArgumentNullException.ThrowIfNull(proof);
        if (proof.ProofId != ProofId || proof.RunId != RunId ||
            proof.ExecutionApprovalId != ExecutionApprovalId || proof.OneTimeNonce != ProofNonce ||
            proof.PlanId != Specification.PlanId || proof.OccurrenceId != Specification.OccurrenceId ||
            proof.SpecificationDigest != Specification.SpecificationDigest)
            throw new ArgumentException("The proof does not match the committed required-once ticket.", nameof(proof));
        if (Interlocked.CompareExchange(ref _proof, proof, null) is not null)
            throw new InvalidOperationException("A proof is already attached to the required-once ticket.");
    }

    internal bool TryClaim(out string reason)
    {
        if (Interlocked.CompareExchange(ref _claimed, 1, 0) == 0)
        {
            reason = string.Empty;
            return true;
        }
        reason = "required_once_execution_ticket_already_claimed";
        return false;
    }

    private static string Require(string? value, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value) || value != value.Trim() || value.Any(char.IsControl))
            throw new ArgumentException("Required-once ticket values must be canonical.", parameterName);
        return value;
    }

    private static string RequireNonce(string? value)
    {
        if (value is null || value.Length != 32 || value.Any(character =>
                !(character is >= '0' and <= '9' or >= 'a' and <= 'f')))
            throw new ArgumentException("A 128-bit lowercase hexadecimal proof nonce is required.", nameof(value));
        return value;
    }
}
