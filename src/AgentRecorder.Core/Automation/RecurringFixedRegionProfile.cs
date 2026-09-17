using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using AgentRecorder.Capture;

namespace AgentRecorder.Core.Automation;

/// <summary>
/// The only rebind policy supported by the first recurring fixed-region
/// profile slice. A profile is a recording specification, not an approval.
/// </summary>
public enum RecurringFixedRegionRebindPolicy
{
    ExactMatchOnly,
}

/// <summary>
/// Stable failure codes for the recurring fixed-region profile domain.
/// </summary>
public static class RecurringFixedRegionProfileReasonCodes
{
    public const string ProfileIdInvalid = "profile_id_invalid";
    public const string ProfileVersionInvalid = "profile_version_invalid";
    public const string ProfilePolicyNotSupported = "profile_policy_not_supported";
    public const string ProfileGeometryInvalid = "profile_geometry_invalid";
    public const string ProfileDurationInvalid = "profile_duration_invalid";
    public const string ProfileCountdownInvalid = "profile_countdown_invalid";
    public const string ProfileOutputDirectoryInvalid = "profile_output_directory_invalid";
    public const string ProfileFilenamePrefixInvalid = "profile_filename_prefix_invalid";
    public const string ProfileOccurrenceIdentityInvalid = "profile_occurrence_identity_invalid";
    public const string ProfileDigestInvalid = "profile_digest_invalid";
    public const string ProfileDigestMismatch = "profile_digest_mismatch";
    public const string ProfileVersionNotContiguous = "profile_version_not_contiguous";
    public const string ProfileNonMonotonicTime = "profile_non_monotonic_time";
    public const string ProfileOutputPathEscape = "profile_output_path_escape";
    public const string ProfileValueOverflow = "profile_value_overflow";
    public const string ProfileNonUtcTime = "profile_non_utc_time";
    public const string ProfileScheduledTimeInvalid = "profile_scheduled_time_invalid";
}

/// <summary>
/// The configuration portion of a recurring fixed-region profile. It contains
/// no plan, occurrence, run, lease, proof, or authorization state.
/// </summary>
public sealed class RecurringFixedRegionProfileSpecification
{
    public RecurringFixedRegionProfileSpecification(
        AuthorizedScopeTargetType targetType,
        RecurringFixedRegionRebindPolicy rebindPolicy,
        AuthorizedCaptureSemantics captureSemantics,
        AuthorizedCoordinateSpace coordinateSpace,
        AuthorizedDisplayIdentityStatus displayIdentityStatus,
        string stableDisplayFingerprint,
        AuthorizedPhysicalRectangle displayBounds,
        AuthorizedPhysicalRectangle regionWithinDisplay,
        int dpiX,
        int dpiY,
        int physicalWidth,
        int physicalHeight,
        AuthorizedDisplayOrientation orientation,
        string topologyDigest,
        AuthorizedCaptureBackend backend,
        AuthorizedAudioMode audioMode,
        TimeSpan duration,
        int countdownSeconds,
        string outputDirectory,
        string filenamePrefix,
        AuthorizedOutputConflictPolicy outputConflictPolicy,
        AuthorizedWakePolicy wakePolicy,
        AuthorizedDesktopRequirement desktopRequirement)
    {
        TargetType = targetType;
        RebindPolicy = rebindPolicy;
        CaptureSemantics = captureSemantics;
        CoordinateSpace = coordinateSpace;
        DisplayIdentityStatus = displayIdentityStatus;
        StableDisplayFingerprint = stableDisplayFingerprint;
        DisplayBounds = displayBounds;
        RegionWithinDisplay = regionWithinDisplay;
        DpiX = dpiX;
        DpiY = dpiY;
        PhysicalWidth = physicalWidth;
        PhysicalHeight = physicalHeight;
        Orientation = orientation;
        TopologyDigest = topologyDigest;
        Backend = backend;
        AudioMode = audioMode;
        Duration = duration;
        CountdownSeconds = countdownSeconds;
        OutputDirectory = outputDirectory;
        FilenamePrefix = filenamePrefix;
        OutputConflictPolicy = outputConflictPolicy;
        WakePolicy = wakePolicy;
        DesktopRequirement = desktopRequirement;
    }

    public AuthorizedScopeTargetType TargetType { get; }

    public RecurringFixedRegionRebindPolicy RebindPolicy { get; }

    public AuthorizedCaptureSemantics CaptureSemantics { get; }

    public AuthorizedCoordinateSpace CoordinateSpace { get; }

    public AuthorizedDisplayIdentityStatus DisplayIdentityStatus { get; }

    public string StableDisplayFingerprint { get; }

    public AuthorizedPhysicalRectangle DisplayBounds { get; }

    public AuthorizedPhysicalRectangle RegionWithinDisplay { get; }

    public int DpiX { get; }

    public int DpiY { get; }

    public int PhysicalWidth { get; }

    public int PhysicalHeight { get; }

    public AuthorizedDisplayOrientation Orientation { get; }

    public string TopologyDigest { get; }

    public AuthorizedCaptureBackend Backend { get; }

    public AuthorizedAudioMode AudioMode { get; }

    public TimeSpan Duration { get; }

    public int CountdownSeconds { get; }

    public string OutputDirectory { get; }

    public string FilenamePrefix { get; }

    public AuthorizedOutputConflictPolicy OutputConflictPolicy { get; }

    public AuthorizedWakePolicy WakePolicy { get; }

    public AuthorizedDesktopRequirement DesktopRequirement { get; }
}

/// <summary>
/// Exact immutable reference to one profile version. There is deliberately no
/// latest-version or auto-follow behavior in this value object.
/// </summary>
public readonly record struct ProfileRef
{
    public ProfileRef(string profileId, long profileVersion, string profileDigest)
    {
        ProfileId = RecurringFixedRegionProfileValidation.ProfileId(profileId);
        ProfileVersion = RecurringFixedRegionProfileValidation.Version(profileVersion);
        ProfileDigest = RecurringFixedRegionProfileValidation.Digest(profileDigest, RecurringFixedRegionProfileReasonCodes.ProfileDigestInvalid);
    }

    public string ProfileId { get; }

    public long ProfileVersion { get; }

    public string ProfileDigest { get; }

    internal void Validate()
    {
        _ = RecurringFixedRegionProfileValidation.ProfileId(ProfileId);
        _ = RecurringFixedRegionProfileValidation.Version(ProfileVersion);
        _ = RecurringFixedRegionProfileValidation.Digest(
            ProfileDigest,
            RecurringFixedRegionProfileReasonCodes.ProfileDigestInvalid);
    }

    public bool Matches(RecurringFixedRegionProfileVersion profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        return string.Equals(ProfileId, profile.ProfileId, StringComparison.Ordinal) &&
            ProfileVersion == profile.ProfileVersion &&
            string.Equals(ProfileDigest, profile.ProfileDigest, StringComparison.Ordinal);
    }

    public override string ToString() => $"{ProfileId}@{ProfileVersion}:{ProfileDigest}";
}

/// <summary>
/// Versioned, immutable recording specification for the recurring unattended
/// fixed-region safe subset. This type is not an authorization scope and has
/// no lease, run, occurrence, proof, or confirmation state.
/// </summary>
public sealed class RecurringFixedRegionProfileVersion
{
    public const int CanonicalVersion = 1;
    public const long FirstProfileVersion = 1;
    public const string DigestSchemaName = "recurring-fixed-region-profile/v1";
    public const string DigestPrefix = DigestSchemaName + ":";
    public const string CanonicalFilenameTemplate = "{prefix}-{scheduled_utc}-{occurrence_id}.mp4";
    public static readonly TimeSpan MaximumDuration = TimeSpan.FromMinutes(10);

    private RecurringFixedRegionProfileVersion(
        string profileId,
        long profileVersion,
        DateTimeOffset createdAtUtc,
        RecurringFixedRegionProfileSpecification specification,
        string? persistedFilenameTemplate,
        string? persistedDigest)
    {
        ProfileId = RecurringFixedRegionProfileValidation.ProfileId(profileId);
        ProfileVersion = RecurringFixedRegionProfileValidation.Version(profileVersion);
        CreatedAtUtc = RecurringFixedRegionProfileValidation.Utc(createdAtUtc, RecurringFixedRegionProfileReasonCodes.ProfileNonUtcTime);

        ValidatePolicies(specification);
        StableDisplayFingerprint = RecurringFixedRegionProfileValidation.CanonicalText(
            specification.StableDisplayFingerprint,
            RecurringFixedRegionProfileReasonCodes.ProfileGeometryInvalid,
            "stable display fingerprint",
            allowSeparators: false);
        TopologyDigest = RecurringFixedRegionProfileValidation.Sha256(
            specification.TopologyDigest,
            RecurringFixedRegionProfileReasonCodes.ProfileGeometryInvalid);

        DisplayBounds = specification.DisplayBounds;
        RegionWithinDisplay = specification.RegionWithinDisplay;
        DpiX = specification.DpiX;
        DpiY = specification.DpiY;
        PhysicalWidth = specification.PhysicalWidth;
        PhysicalHeight = specification.PhysicalHeight;
        Orientation = specification.Orientation;
        ValidateGeometry();

        Duration = ValidateDuration(specification.Duration);
        CountdownSeconds = ValidateCountdown(specification.CountdownSeconds);

        OutputDirectory = NormalizeOutputDirectory(specification.OutputDirectory);
        FilenamePrefix = NormalizeFilenamePrefix(specification.FilenamePrefix);
        FilenameTemplate = BuildFilenameTemplate(FilenamePrefix);
        if (persistedFilenameTemplate is not null &&
            !string.Equals(persistedFilenameTemplate, FilenameTemplate, StringComparison.Ordinal))
        {
            throw new Phase3DomainException(
                RecurringFixedRegionProfileReasonCodes.ProfileFilenamePrefixInvalid,
                "The persisted filename template is not the canonical fixed recurring template.");
        }

        TargetType = specification.TargetType;
        RebindPolicy = specification.RebindPolicy;
        CaptureSemantics = specification.CaptureSemantics;
        CoordinateSpace = specification.CoordinateSpace;
        DisplayIdentityStatus = specification.DisplayIdentityStatus;
        Backend = specification.Backend;
        AudioMode = specification.AudioMode;
        OutputConflictPolicy = specification.OutputConflictPolicy;
        WakePolicy = specification.WakePolicy;
        DesktopRequirement = specification.DesktopRequirement;

        ProfileDigest = RecurringFixedRegionProfileDigest.Compute(this);
        if (persistedDigest is not null)
        {
            var canonicalPersistedDigest = RecurringFixedRegionProfileValidation.Digest(
                persistedDigest,
                RecurringFixedRegionProfileReasonCodes.ProfileDigestInvalid);
            if (!RecurringFixedRegionProfileValidation.FixedTimeEquals(ProfileDigest, canonicalPersistedDigest))
            {
                throw new Phase3DomainException(
                    RecurringFixedRegionProfileReasonCodes.ProfileDigestMismatch,
                    "The persisted profile digest does not match the canonical profile contents.");
            }
        }
    }

    public string ProfileId { get; }

    public long ProfileVersion { get; }

    public long Version => ProfileVersion;

    public string ProfileDigest { get; }

    public DateTimeOffset CreatedAtUtc { get; }

    public AuthorizedScopeTargetType TargetType { get; }

    public RecurringFixedRegionRebindPolicy RebindPolicy { get; }

    public AuthorizedCaptureSemantics CaptureSemantics { get; }

    public AuthorizedCoordinateSpace CoordinateSpace { get; }

    public AuthorizedDisplayIdentityStatus DisplayIdentityStatus { get; }

    public string StableDisplayFingerprint { get; }

    public AuthorizedPhysicalRectangle DisplayBounds { get; }

    public AuthorizedPhysicalRectangle RegionWithinDisplay { get; }

    public AuthorizedPhysicalRectangle VirtualScreenRegion
    {
        get
        {
            try
            {
                return new AuthorizedPhysicalRectangle(
                    checked(DisplayBounds.X + RegionWithinDisplay.X),
                    checked(DisplayBounds.Y + RegionWithinDisplay.Y),
                    RegionWithinDisplay.Width,
                    RegionWithinDisplay.Height);
            }
            catch (OverflowException)
            {
                throw new Phase3DomainException(
                    RecurringFixedRegionProfileReasonCodes.ProfileValueOverflow,
                    "The virtual-screen region calculation overflowed.");
            }
        }
    }

    public int DpiX { get; }

    public int DpiY { get; }

    public int PhysicalWidth { get; }

    public int PhysicalHeight { get; }

    public AuthorizedDisplayOrientation Orientation { get; }

    public string TopologyDigest { get; }

    public AuthorizedCaptureBackend Backend { get; }

    public AuthorizedAudioMode AudioMode { get; }

    public TimeSpan Duration { get; }

    public TimeSpan RecordingDuration => Duration;

    public int CountdownSeconds { get; }

    public string OutputDirectory { get; }

    public string FilenamePrefix { get; }

    public string FilenameTemplate { get; }

    public AuthorizedOutputConflictPolicy OutputConflictPolicy { get; }

    public AuthorizedWakePolicy WakePolicy { get; }

    public AuthorizedDesktopRequirement DesktopRequirement { get; }

    public ProfileRef Reference => new(ProfileId, ProfileVersion, ProfileDigest);

    public ProfileRef ProfileReference => Reference;

    public ProfileRef ProfileRef => Reference;

    public static RecurringFixedRegionProfileVersion CreateVersion1(
        string profileId,
        DateTimeOffset createdAtUtc,
        RecurringFixedRegionProfileSpecification specification) =>
        new(profileId, FirstProfileVersion, createdAtUtc, specification, persistedFilenameTemplate: null, persistedDigest: null);

    public static RecurringFixedRegionProfileVersion CreateNextVersion(
        RecurringFixedRegionProfileVersion previous,
        RecurringFixedRegionProfileSpecification specification,
        DateTimeOffset createdAtUtc)
    {
        ArgumentNullException.ThrowIfNull(previous);
        if (previous.ProfileVersion == long.MaxValue)
        {
            throw new Phase3DomainException(
                RecurringFixedRegionProfileReasonCodes.ProfileValueOverflow,
                "The next profile version would overflow.");
        }

        var nextVersion = checked(previous.ProfileVersion + 1);
        if (nextVersion != previous.ProfileVersion + 1)
        {
            throw new Phase3DomainException(
                RecurringFixedRegionProfileReasonCodes.ProfileVersionNotContiguous,
                "The next profile version is not contiguous.");
        }

        var canonicalCreatedAt = RecurringFixedRegionProfileValidation.Utc(
            createdAtUtc,
            RecurringFixedRegionProfileReasonCodes.ProfileNonUtcTime);
        if (canonicalCreatedAt < previous.CreatedAtUtc)
        {
            throw new Phase3DomainException(
                RecurringFixedRegionProfileReasonCodes.ProfileNonMonotonicTime,
                "The next profile creation time must not move backwards.");
        }

        return new(
            previous.ProfileId,
            nextVersion,
            canonicalCreatedAt,
            specification,
            persistedFilenameTemplate: null,
            persistedDigest: null);
    }

    public static RecurringFixedRegionProfileVersion CreateNextVersion(
        RecurringFixedRegionProfileVersion previous,
        DateTimeOffset createdAtUtc,
        RecurringFixedRegionProfileSpecification specification) =>
        CreateNextVersion(previous, specification, createdAtUtc);

    // This seam is internal so tests can exercise checked version overflow
    // without exposing arbitrary version allocation as a product API.
    internal static RecurringFixedRegionProfileVersion CreateVersionForTests(
        string profileId,
        long profileVersion,
        DateTimeOffset createdAtUtc,
        RecurringFixedRegionProfileSpecification specification) =>
        new(profileId, profileVersion, createdAtUtc, specification, persistedFilenameTemplate: null, persistedDigest: null);

    public static RecurringFixedRegionProfileVersion Rehydrate(
        string profileId,
        long profileVersion,
        DateTimeOffset createdAtUtc,
        RecurringFixedRegionProfileSpecification specification,
        string profileDigest,
        string? filenameTemplate = null) =>
        new(profileId, profileVersion, createdAtUtc, specification, filenameTemplate, profileDigest);

    public static string RenderOutputFileName(
        RecurringFixedRegionProfileVersion profile,
        string occurrenceIdentity,
        DateTimeOffset scheduledStartUtc) =>
        RecurringFixedRegionProfileOutput.RenderOutputFileName(profile, occurrenceIdentity, scheduledStartUtc);

    public static string ResolveOutputPath(
        RecurringFixedRegionProfileVersion profile,
        string occurrenceIdentity,
        DateTimeOffset scheduledStartUtc) =>
        RecurringFixedRegionProfileOutput.ResolveOutputPath(profile, occurrenceIdentity, scheduledStartUtc);

    public string RenderOutputFileName(string occurrenceIdentity, DateTimeOffset scheduledStartUtc) =>
        RecurringFixedRegionProfileOutput.RenderOutputFileName(this, occurrenceIdentity, scheduledStartUtc);

    public string ResolveOutputPath(string occurrenceIdentity, DateTimeOffset scheduledStartUtc) =>
        RecurringFixedRegionProfileOutput.ResolveOutputPath(this, occurrenceIdentity, scheduledStartUtc);

    private static void ValidatePolicies(RecurringFixedRegionProfileSpecification specification)
    {
        ArgumentNullException.ThrowIfNull(specification);
        if (!Enum.IsDefined(specification.TargetType) ||
            !Enum.IsDefined(specification.RebindPolicy) ||
            !Enum.IsDefined(specification.CaptureSemantics) ||
            !Enum.IsDefined(specification.CoordinateSpace) ||
            !Enum.IsDefined(specification.DisplayIdentityStatus) ||
            !Enum.IsDefined(specification.Backend) ||
            !Enum.IsDefined(specification.AudioMode) ||
            !Enum.IsDefined(specification.OutputConflictPolicy) ||
            !Enum.IsDefined(specification.WakePolicy) ||
            !Enum.IsDefined(specification.DesktopRequirement) ||
            specification.TargetType != AuthorizedScopeTargetType.FixedRegion ||
            specification.RebindPolicy != RecurringFixedRegionRebindPolicy.ExactMatchOnly ||
            specification.CaptureSemantics != AuthorizedCaptureSemantics.DesktopRegion ||
            specification.CoordinateSpace != AuthorizedCoordinateSpace.PhysicalVirtualScreen ||
            specification.DisplayIdentityStatus != AuthorizedDisplayIdentityStatus.Resolved ||
            specification.Backend != AuthorizedCaptureBackend.FfmpegRegion ||
            specification.AudioMode != AuthorizedAudioMode.None ||
            specification.OutputConflictPolicy != AuthorizedOutputConflictPolicy.FailIfExists ||
            specification.WakePolicy != AuthorizedWakePolicy.NaturalWakeOnly ||
            specification.DesktopRequirement != AuthorizedDesktopRequirement.InteractiveDesktopRequired)
        {
            throw new Phase3DomainException(
                RecurringFixedRegionProfileReasonCodes.ProfilePolicyNotSupported,
                "The profile contains a policy outside the recurring fixed-region safe subset.");
        }
    }

    private void ValidateGeometry()
    {
        if (DpiX <= 0 || DpiY <= 0 ||
            PhysicalWidth <= 0 || PhysicalHeight <= 0 ||
            !Enum.IsDefined(Orientation) ||
            DisplayBounds.Width <= 0 || DisplayBounds.Height <= 0 ||
            RegionWithinDisplay.X < 0 || RegionWithinDisplay.Y < 0 ||
            RegionWithinDisplay.Width <= 0 || RegionWithinDisplay.Height <= 0 ||
            PhysicalWidth != DisplayBounds.Width ||
            PhysicalHeight != DisplayBounds.Height)
        {
            throw new Phase3DomainException(
                RecurringFixedRegionProfileReasonCodes.ProfileGeometryInvalid,
                "The display and region geometry is invalid.");
        }

        try
        {
            if (checked(RegionWithinDisplay.X + RegionWithinDisplay.Width) > DisplayBounds.Width ||
                checked(RegionWithinDisplay.Y + RegionWithinDisplay.Height) > DisplayBounds.Height)
            {
                throw new Phase3DomainException(
                    RecurringFixedRegionProfileReasonCodes.ProfileGeometryInvalid,
                    "The region must be fully contained in the display bounds.");
            }

            _ = VirtualScreenRegion;
        }
        catch (OverflowException)
        {
            throw new Phase3DomainException(
                RecurringFixedRegionProfileReasonCodes.ProfileValueOverflow,
                "The display or region geometry calculation overflowed.");
        }
    }

    private static TimeSpan ValidateDuration(TimeSpan duration)
    {
        if (duration <= TimeSpan.Zero || duration > MaximumDuration ||
            duration.Ticks % TimeSpan.TicksPerMillisecond != 0)
        {
            throw new Phase3DomainException(
                RecurringFixedRegionProfileReasonCodes.ProfileDurationInvalid,
                "The profile duration must be positive, millisecond-representable, and no longer than ten minutes.");
        }

        return duration;
    }

    private static int ValidateCountdown(int seconds)
    {
        if (seconds < CaptureConfig.MinCountdownSeconds || seconds > CaptureConfig.MaxCountdownSeconds)
        {
            throw new Phase3DomainException(
                RecurringFixedRegionProfileReasonCodes.ProfileCountdownInvalid,
                "The profile countdown is outside the current product range.");
        }

        return seconds;
    }

    private static string NormalizeOutputDirectory(string value)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(value) || value.Any(char.IsControl))
            {
                throw new Phase3DomainException(
                    RecurringFixedRegionProfileReasonCodes.ProfileOutputDirectoryInvalid,
                    "The output directory must be a non-blank path without control characters.");
            }

            var normalized = AuthorizedFixedRegionScope.NormalizeOutputDirectoryForAuthorization(value);
            if (normalized.Length > 32767)
            {
                throw new Phase3DomainException(
                    RecurringFixedRegionProfileReasonCodes.ProfileOutputDirectoryInvalid,
                    "The output directory is too long.");
            }

            return normalized;
        }
        catch (Phase3DomainException exception) when (
            exception.ReasonCode != RecurringFixedRegionProfileReasonCodes.ProfileOutputDirectoryInvalid)
        {
            throw new Phase3DomainException(
                RecurringFixedRegionProfileReasonCodes.ProfileOutputDirectoryInvalid,
                "The output directory is not a valid normalized absolute path.");
        }
        catch (ArgumentException)
        {
            throw new Phase3DomainException(
                RecurringFixedRegionProfileReasonCodes.ProfileOutputDirectoryInvalid,
                "The output directory is not a valid normalized absolute path.");
        }
        catch (NotSupportedException)
        {
            throw new Phase3DomainException(
                RecurringFixedRegionProfileReasonCodes.ProfileOutputDirectoryInvalid,
                "The output directory is not supported.");
        }
    }

    private static string NormalizeFilenamePrefix(string value)
    {
        if (value is null)
        {
            throw new Phase3DomainException(
                RecurringFixedRegionProfileReasonCodes.ProfileFilenamePrefixInvalid,
                "The filename prefix must not be null.");
        }

        var normalized = value.Trim();
        var runeCount = normalized.EnumerateRunes().Count();
        if (runeCount is < 1 or > 64 ||
            normalized.EndsWith('.') ||
            normalized.EndsWith(' ') ||
            normalized.Contains("..", StringComparison.Ordinal) ||
            IsReservedDeviceName(normalized))
        {
            throw new Phase3DomainException(
                RecurringFixedRegionProfileReasonCodes.ProfileFilenamePrefixInvalid,
                "The filename prefix length or final form is invalid.");
        }

        foreach (var rune in normalized.EnumerateRunes())
        {
            if (rune.Value is (>= 'A' and <= 'Z') or (>= 'a' and <= 'z') or (>= '0' and <= '9') or '-' or '_' or ' ')
            {
                continue;
            }

            if (!Rune.IsLetterOrDigit(rune))
            {
                throw new Phase3DomainException(
                    RecurringFixedRegionProfileReasonCodes.ProfileFilenamePrefixInvalid,
                    "The filename prefix contains a character outside the safe filename subset.");
            }
        }

        return normalized;
    }

    private static bool IsReservedDeviceName(string value)
    {
        var stem = value.TrimEnd(' ').Split('-', StringSplitOptions.None)[0];
        return stem.Equals("CON", StringComparison.OrdinalIgnoreCase) ||
            stem.Equals("PRN", StringComparison.OrdinalIgnoreCase) ||
            stem.Equals("AUX", StringComparison.OrdinalIgnoreCase) ||
            stem.Equals("NUL", StringComparison.OrdinalIgnoreCase) ||
            (stem.Length == 4 && (stem.StartsWith("COM", StringComparison.OrdinalIgnoreCase) || stem.StartsWith("LPT", StringComparison.OrdinalIgnoreCase)) && stem[3] is >= '1' and <= '9');
    }

    private static string BuildFilenameTemplate(string prefix) =>
        $"{prefix}-{{scheduled_utc}}-{{occurrence_id}}.mp4";
}

/// <summary>
/// Canonical SHA-256 representation for a recurring fixed-region profile.
/// Every value is encoded explicitly; no JSON, culture-sensitive formatting,
/// object ToString, or dictionary traversal participates in the digest.
/// </summary>
public static class RecurringFixedRegionProfileDigest
{
    public const string SchemaName = RecurringFixedRegionProfileVersion.DigestSchemaName;

    public static string Compute(RecurringFixedRegionProfileVersion profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        var bytes = new List<byte>(1024);
        AppendString(bytes, SchemaName);
        AppendInt64(bytes, profile.ProfileVersion);
        AppendString(bytes, profile.ProfileId);
        AppendInt64(bytes, profile.CreatedAtUtc.UtcDateTime.Ticks);
        AppendString(bytes, RecurringFixedRegionProfileCode.ToCode(profile.TargetType));
        AppendString(bytes, RecurringFixedRegionProfileCode.ToCode(profile.RebindPolicy));
        AppendString(bytes, RecurringFixedRegionProfileCode.ToCode(profile.CaptureSemantics));
        AppendString(bytes, RecurringFixedRegionProfileCode.ToCode(profile.CoordinateSpace));
        AppendString(bytes, RecurringFixedRegionProfileCode.ToCode(profile.DisplayIdentityStatus));
        AppendString(bytes, profile.StableDisplayFingerprint);
        AppendRectangle(bytes, profile.DisplayBounds);
        AppendRectangle(bytes, profile.RegionWithinDisplay);
        AppendInt32(bytes, profile.DpiX);
        AppendInt32(bytes, profile.DpiY);
        AppendInt32(bytes, profile.PhysicalWidth);
        AppendInt32(bytes, profile.PhysicalHeight);
        AppendString(bytes, RecurringFixedRegionProfileCode.ToCode(profile.Orientation));
        AppendString(bytes, profile.TopologyDigest);
        AppendString(bytes, RecurringFixedRegionProfileCode.ToCode(profile.Backend));
        AppendString(bytes, RecurringFixedRegionProfileCode.ToCode(profile.AudioMode));
        AppendInt64(bytes, profile.Duration.Ticks);
        AppendInt32(bytes, profile.CountdownSeconds);
        AppendString(bytes, profile.OutputDirectory);
        AppendString(bytes, profile.FilenamePrefix);
        AppendString(bytes, profile.FilenameTemplate);
        AppendString(bytes, RecurringFixedRegionProfileCode.ToCode(profile.OutputConflictPolicy));
        AppendString(bytes, RecurringFixedRegionProfileCode.ToCode(profile.WakePolicy));
        AppendString(bytes, RecurringFixedRegionProfileCode.ToCode(profile.DesktopRequirement));

        return RecurringFixedRegionProfileVersion.DigestPrefix +
            Convert.ToHexString(SHA256.HashData(bytes.ToArray())).ToLowerInvariant();
    }

    private static void AppendRectangle(List<byte> bytes, AuthorizedPhysicalRectangle rectangle)
    {
        AppendInt32(bytes, rectangle.X);
        AppendInt32(bytes, rectangle.Y);
        AppendInt32(bytes, rectangle.Width);
        AppendInt32(bytes, rectangle.Height);
    }

    private static void AppendString(List<byte> bytes, string value)
    {
        var utf8 = Encoding.UTF8.GetBytes(value);
        if (utf8.Length > int.MaxValue)
        {
            throw new Phase3DomainException(
                RecurringFixedRegionProfileReasonCodes.ProfileValueOverflow,
                "A profile string is too long to encode canonically.");
        }

        AppendInt32(bytes, utf8.Length);
        bytes.AddRange(utf8);
    }

    private static void AppendInt32(List<byte> bytes, int value)
    {
        Span<byte> buffer = stackalloc byte[sizeof(int)];
        BinaryPrimitives.WriteInt32BigEndian(buffer, value);
        bytes.AddRange(buffer.ToArray());
    }

    private static void AppendInt64(List<byte> bytes, long value)
    {
        Span<byte> buffer = stackalloc byte[sizeof(long)];
        BinaryPrimitives.WriteInt64BigEndian(buffer, value);
        bytes.AddRange(buffer.ToArray());
    }
}

/// <summary>
/// Pure, filesystem-free rendering for the recurring profile's constrained
/// output filename strategy.
/// </summary>
public static class RecurringFixedRegionProfileOutput
{
    public static string RenderOutputFileName(
        RecurringFixedRegionProfileVersion profile,
        string occurrenceIdentity,
        DateTimeOffset scheduledStartUtc)
    {
        ArgumentNullException.ThrowIfNull(profile);
        return RenderOutputFileName(profile.FilenamePrefix, occurrenceIdentity, scheduledStartUtc);
    }

    internal static string RenderOutputFileName(
        string filenamePrefix,
        string occurrenceIdentity,
        DateTimeOffset scheduledStartUtc)
    {
        ArgumentNullException.ThrowIfNull(filenamePrefix);
        var occurrenceDigest = ValidateOccurrenceIdentity(occurrenceIdentity);
        var scheduled = ValidateScheduledStart(scheduledStartUtc);
        var timestamp = scheduled.UtcDateTime.ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture);
        return $"{filenamePrefix}-{timestamp}-{occurrenceDigest}.mp4";
    }

    public static string ResolveOutputPath(
        RecurringFixedRegionProfileVersion profile,
        string occurrenceIdentity,
        DateTimeOffset scheduledStartUtc)
    {
        ArgumentNullException.ThrowIfNull(profile);
        return ResolveOutputPath(profile.OutputDirectory, profile.FilenamePrefix, occurrenceIdentity, scheduledStartUtc);
    }

    internal static string ResolveOutputPath(
        string outputDirectory,
        string filenamePrefix,
        string occurrenceIdentity,
        DateTimeOffset scheduledStartUtc)
    {
        ArgumentNullException.ThrowIfNull(outputDirectory);
        var filename = RenderOutputFileName(filenamePrefix, occurrenceIdentity, scheduledStartUtc);
        try
        {
            var candidate = Path.GetFullPath(Path.Combine(outputDirectory, filename));
            var directory = outputDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var candidateDirectory = Path.GetDirectoryName(candidate)?.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (candidateDirectory is null ||
                !string.Equals(candidateDirectory, directory, StringComparison.OrdinalIgnoreCase))
            {
                throw new Phase3DomainException(
                    RecurringFixedRegionProfileReasonCodes.ProfileOutputPathEscape,
                    "The rendered output path is not a direct child of the profile output directory.");
            }

            return candidate;
        }
        catch (Phase3DomainException)
        {
            throw;
        }
        catch (ArgumentException)
        {
            throw new Phase3DomainException(
                RecurringFixedRegionProfileReasonCodes.ProfileOutputPathEscape,
                "The rendered output path is invalid.");
        }
        catch (NotSupportedException)
        {
            throw new Phase3DomainException(
                RecurringFixedRegionProfileReasonCodes.ProfileOutputPathEscape,
                "The rendered output path is not supported.");
        }
    }

    private static string ValidateOccurrenceIdentity(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) ||
            value.Length != RecurringOccurrenceIdentity.Prefix.Length + 64 ||
            !value.StartsWith(RecurringOccurrenceIdentity.Prefix, StringComparison.Ordinal))
        {
            throw new Phase3DomainException(
                RecurringFixedRegionProfileReasonCodes.ProfileOccurrenceIdentityInvalid,
                "The occurrence identity has an invalid canonical prefix or length.");
        }

        var digest = value[RecurringOccurrenceIdentity.Prefix.Length..];
        if (digest.Any(character => character is < '0' or > '9' and < 'a' or > 'f'))
        {
            throw new Phase3DomainException(
                RecurringFixedRegionProfileReasonCodes.ProfileOccurrenceIdentityInvalid,
                "The occurrence identity digest must be lowercase hexadecimal.");
        }

        return digest;
    }

    private static DateTimeOffset ValidateScheduledStart(DateTimeOffset value)
    {
        if (value.Offset != TimeSpan.Zero)
        {
            throw new Phase3DomainException(
                RecurringFixedRegionProfileReasonCodes.ProfileNonUtcTime,
                "The scheduled occurrence start must use UTC.");
        }

        if (value.Ticks % TimeSpan.TicksPerSecond != 0)
        {
            throw new Phase3DomainException(
                RecurringFixedRegionProfileReasonCodes.ProfileScheduledTimeInvalid,
                "The scheduled occurrence start must be representable to whole seconds.");
        }

        return value;
    }
}

public static class RecurringFixedRegionProfileCode
{
    public static string ToCode(AuthorizedScopeTargetType value) => value switch
    {
        AuthorizedScopeTargetType.FixedRegion => "fixed_region",
        AuthorizedScopeTargetType.Window => "window",
        _ => throw Unsupported(),
    };

    public static string ToCode(RecurringFixedRegionRebindPolicy value) => value switch
    {
        RecurringFixedRegionRebindPolicy.ExactMatchOnly => "exact_match_only",
        _ => throw Unsupported(),
    };

    public static string ToCode(AuthorizedCaptureSemantics value) => value switch
    {
        AuthorizedCaptureSemantics.DesktopRegion => "desktop_region",
        AuthorizedCaptureSemantics.WindowSurface => "window_surface",
        _ => throw Unsupported(),
    };

    public static string ToCode(AuthorizedCoordinateSpace value) => value switch
    {
        AuthorizedCoordinateSpace.PhysicalVirtualScreen => "physical_virtual_screen",
        AuthorizedCoordinateSpace.LogicalDesktop => "logical_desktop",
        _ => throw Unsupported(),
    };

    public static string ToCode(AuthorizedDisplayIdentityStatus value) => value switch
    {
        AuthorizedDisplayIdentityStatus.Resolved => "resolved",
        AuthorizedDisplayIdentityStatus.Unresolved => "unresolved",
        _ => throw Unsupported(),
    };

    public static string ToCode(AuthorizedDisplayOrientation value) => value switch
    {
        AuthorizedDisplayOrientation.Landscape => "landscape",
        AuthorizedDisplayOrientation.Portrait => "portrait",
        AuthorizedDisplayOrientation.LandscapeFlipped => "landscape_flipped",
        AuthorizedDisplayOrientation.PortraitFlipped => "portrait_flipped",
        _ => throw Unsupported(),
    };

    public static string ToCode(AuthorizedCaptureBackend value) => value switch
    {
        AuthorizedCaptureBackend.FfmpegRegion => "ffmpeg-region",
        AuthorizedCaptureBackend.Wgc => "wgc",
        _ => throw Unsupported(),
    };

    public static string ToCode(AuthorizedAudioMode value) => value switch
    {
        AuthorizedAudioMode.None => "none",
        AuthorizedAudioMode.Microphone => "microphone",
        AuthorizedAudioMode.SystemAudio => "system_audio",
        _ => throw Unsupported(),
    };

    public static string ToCode(AuthorizedOutputConflictPolicy value) => value switch
    {
        AuthorizedOutputConflictPolicy.FailIfExists => "fail_if_exists",
        AuthorizedOutputConflictPolicy.Rename => "rename",
        _ => throw Unsupported(),
    };

    public static string ToCode(AuthorizedWakePolicy value) => value switch
    {
        AuthorizedWakePolicy.NaturalWakeOnly => "natural_wake_only",
        AuthorizedWakePolicy.ScheduledWake => "scheduled_wake",
        _ => throw Unsupported(),
    };

    public static string ToCode(AuthorizedDesktopRequirement value) => value switch
    {
        AuthorizedDesktopRequirement.InteractiveDesktopRequired => "interactive_desktop_required",
        AuthorizedDesktopRequirement.AnyDesktop => "any_desktop",
        _ => throw Unsupported(),
    };

    private static Phase3DomainException Unsupported() => new(
        RecurringFixedRegionProfileReasonCodes.ProfilePolicyNotSupported,
        "The profile enum value is not supported.");
}

internal static class RecurringFixedRegionProfileValidation
{
    internal static string ProfileId(string? value)
    {
        if (value is null)
            throw InvalidId();

        var canonical = value.Trim();
        if (canonical.Length == 0 || canonical.Length > 128 || canonical.Any(char.IsControl) ||
            canonical.Contains('/') || canonical.Contains('\\') || canonical.Contains(':'))
        {
            throw InvalidId();
        }

        return canonical;
    }

    internal static long Version(long value)
    {
        if (value <= 0)
        {
            throw new Phase3DomainException(
                RecurringFixedRegionProfileReasonCodes.ProfileVersionInvalid,
                "The profile version must be positive.");
        }

        return value;
    }

    internal static DateTimeOffset Utc(DateTimeOffset value, string reasonCode)
    {
        if (value.Offset != TimeSpan.Zero)
        {
            throw new Phase3DomainException(reasonCode, "The profile time must use UTC.");
        }

        return value;
    }

    internal static string CanonicalText(string? value, string reasonCode, string field, bool allowSeparators)
    {
        if (string.IsNullOrWhiteSpace(value) || !string.Equals(value, value.Trim(), StringComparison.Ordinal) || value.Any(char.IsControl) ||
            value.Length > 256 ||
            (!allowSeparators && (value.Contains('/') || value.Contains('\\'))))
        {
            throw new Phase3DomainException(reasonCode, $"The {field} is not canonical.");
        }

        return value;
    }

    internal static string Digest(string? value, string reasonCode)
    {
        if (string.IsNullOrWhiteSpace(value) ||
            !value.StartsWith(RecurringFixedRegionProfileVersion.DigestPrefix, StringComparison.Ordinal) ||
            value.Length != RecurringFixedRegionProfileVersion.DigestPrefix.Length + 64)
        {
            throw new Phase3DomainException(reasonCode, "The profile digest format is invalid.");
        }

        var hex = value[RecurringFixedRegionProfileVersion.DigestPrefix.Length..];
        if (hex.Any(character => character is < '0' or > '9' and < 'a' or > 'f'))
        {
            throw new Phase3DomainException(reasonCode, "The profile digest must use lowercase hexadecimal.");
        }

        return value;
    }

    internal static string Sha256(string? value, string reasonCode)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length != 64 ||
            value.Any(character => character is < '0' or > '9' and < 'a' or > 'f'))
        {
            throw new Phase3DomainException(reasonCode, "The value must be lowercase hexadecimal SHA-256.");
        }

        return value;
    }

    internal static bool FixedTimeEquals(string left, string right) =>
        CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(left), Encoding.ASCII.GetBytes(right));

    private static Phase3DomainException InvalidId() => new(
        RecurringFixedRegionProfileReasonCodes.ProfileIdInvalid,
        "The profile id must be a canonical non-empty id.");
}
