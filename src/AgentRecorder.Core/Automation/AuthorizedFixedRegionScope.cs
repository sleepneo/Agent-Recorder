using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace AgentRecorder.Core.Automation;

public enum AuthorizedScopeTargetType
{
    FixedRegion,
    Window,
}

public enum AuthorizedCaptureSemantics
{
    DesktopRegion,
    WindowSurface,
}

public enum AuthorizedCoordinateSpace
{
    PhysicalVirtualScreen,
    LogicalDesktop,
}

public enum AuthorizedDisplayIdentityStatus
{
    Resolved,
    Unresolved,
}

public enum AuthorizedDisplayOrientation
{
    Landscape,
    Portrait,
    LandscapeFlipped,
    PortraitFlipped,
}

public enum AuthorizedCaptureBackend
{
    FfmpegRegion,
    Wgc,
}

public enum AuthorizedAudioMode
{
    None,
    Microphone,
    SystemAudio,
}

public enum AuthorizedOutputConflictPolicy
{
    FailIfExists,
    Rename,
}

public enum AuthorizedWakePolicy
{
    NaturalWakeOnly,
    ScheduledWake,
}

public enum AuthorizedDesktopRequirement
{
    InteractiveDesktopRequired,
    AnyDesktop,
}

public readonly record struct AuthorizedPhysicalRectangle
{
    public AuthorizedPhysicalRectangle(int x, int y, int width, int height)
    {
        if (width <= 0 || height <= 0)
        {
            throw new Phase3DomainException("invalid_scope_geometry", "Rectangle width and height must be positive.");
        }

        X = x;
        Y = y;
        Width = width;
        Height = height;
    }

    public int X { get; }

    public int Y { get; }

    public int Width { get; }

    public int Height { get; }
}

/// <summary>
/// Immutable, first-MVP authorization binding for one fixed physical display region.
/// It is a durable authorization scope, not an API DTO and not a capture proof.
/// </summary>
public sealed class AuthorizedFixedRegionScope
{
    public const int CurrentAuthorizationVersion = 1;
    public static readonly TimeSpan MaximumReservedDuration = TimeSpan.FromMinutes(10);

    private AuthorizedFixedRegionScope(
        string scopeId,
        string planId,
        string occurrenceId,
        string leaseId,
        DateTimeOffset createdAtUtc,
        AuthorizedScopeTargetType targetType,
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
        AuthorizedCaptureBackend backend,
        AuthorizedAudioMode audioMode,
        TimeSpan reservedDuration,
        int countdownSeconds,
        string outputDirectory,
        string frozenFileName,
        AuthorizedOutputConflictPolicy outputConflictPolicy,
        AuthorizedWakePolicy wakePolicy,
        AuthorizedDesktopRequirement desktopRequirement,
        string currentUserSid,
        string sessionBinding,
        string topologyDigest,
        string? persistedDigest)
    {
        ScopeId = Phase3Validation.RequiredId(scopeId, nameof(scopeId));
        PlanId = Phase3Validation.RequiredId(planId, nameof(planId));
        OccurrenceId = Phase3Validation.RequiredId(occurrenceId, nameof(occurrenceId));
        LeaseId = Phase3Validation.RequiredId(leaseId, nameof(leaseId));
        CreatedAtUtc = Phase3Validation.Utc(createdAtUtc, nameof(createdAtUtc));

        if (targetType != AuthorizedScopeTargetType.FixedRegion ||
            captureSemantics != AuthorizedCaptureSemantics.DesktopRegion ||
            coordinateSpace != AuthorizedCoordinateSpace.PhysicalVirtualScreen ||
            displayIdentityStatus != AuthorizedDisplayIdentityStatus.Resolved ||
            backend != AuthorizedCaptureBackend.FfmpegRegion ||
            audioMode != AuthorizedAudioMode.None ||
            outputConflictPolicy != AuthorizedOutputConflictPolicy.FailIfExists ||
            wakePolicy != AuthorizedWakePolicy.NaturalWakeOnly ||
            desktopRequirement != AuthorizedDesktopRequirement.InteractiveDesktopRequired)
        {
            throw new Phase3DomainException("scope_policy_not_supported", "The scope contains a policy outside the fixed-region first-MVP boundary.");
        }

        TargetType = targetType;
        CaptureSemantics = captureSemantics;
        CoordinateSpace = coordinateSpace;
        DisplayIdentityStatus = displayIdentityStatus;
        StableDisplayFingerprint = RequiredCanonicalText(stableDisplayFingerprint, nameof(stableDisplayFingerprint), allowSeparators: false);
        DisplayBounds = displayBounds;
        RegionWithinDisplay = regionWithinDisplay;
        DpiX = Positive(dpiX, nameof(dpiX));
        DpiY = Positive(dpiY, nameof(dpiY));
        PhysicalWidth = Positive(physicalWidth, nameof(physicalWidth));
        PhysicalHeight = Positive(physicalHeight, nameof(physicalHeight));
        Orientation = RequireDefined(orientation, nameof(orientation));
        Backend = backend;
        AudioMode = audioMode;
        ReservedDuration = ValidateDuration(reservedDuration);
        CountdownSeconds = Phase3Validation.NonNegative(countdownSeconds, nameof(countdownSeconds));
        OutputDirectory = NormalizeOutputDirectory(outputDirectory);
        FrozenFileName = NormalizeFileName(frozenFileName);
        OutputConflictPolicy = outputConflictPolicy;
        WakePolicy = wakePolicy;
        DesktopRequirement = desktopRequirement;
        CurrentUserSid = RequiredCanonicalText(currentUserSid, nameof(currentUserSid), allowSeparators: false);
        SessionBinding = RequiredCanonicalText(sessionBinding, nameof(sessionBinding), allowSeparators: false);
        TopologyDigest = ValidateSha256Digest(topologyDigest, nameof(topologyDigest));

        ValidateGeometry();
        ValidateOutputPath();

        ScopeDigest = AuthorizedFixedRegionScopeDigest.Compute(this);
        if (persistedDigest is not null)
        {
            var canonicalPersistedDigest = ValidateSha256Digest(persistedDigest, nameof(persistedDigest));
            if (!string.Equals(ScopeDigest, canonicalPersistedDigest, StringComparison.Ordinal))
            {
                throw new Phase3DomainException("scope_digest_mismatch", "The persisted scope digest does not match the canonical scope contents.");
            }
        }
    }

    public string ScopeId { get; }

    public string PlanId { get; }

    public string OccurrenceId { get; }

    public string LeaseId { get; }

    public int AuthorizationVersion => CurrentAuthorizationVersion;

    public DateTimeOffset CreatedAtUtc { get; }

    public string ScopeDigest { get; }

    public AuthorizedScopeTargetType TargetType { get; }

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
                throw new Phase3DomainException("invalid_scope_geometry", "The virtual-screen region calculation overflowed.");
            }
        }
    }

    public int DpiX { get; }

    public int DpiY { get; }

    public int PhysicalWidth { get; }

    public int PhysicalHeight { get; }

    public AuthorizedDisplayOrientation Orientation { get; }

    public AuthorizedCaptureBackend Backend { get; }

    public AuthorizedAudioMode AudioMode { get; }

    public TimeSpan ReservedDuration { get; }

    public int CountdownSeconds { get; }

    public string OutputDirectory { get; }

    public string FrozenFileName { get; }

    public string OutputFilePath => Path.Combine(OutputDirectory, FrozenFileName);

    public AuthorizedOutputConflictPolicy OutputConflictPolicy { get; }

    public AuthorizedWakePolicy WakePolicy { get; }

    public AuthorizedDesktopRequirement DesktopRequirement { get; }

    public string CurrentUserSid { get; }

    public string SessionBinding { get; }

    public string TopologyDigest { get; }

    public static AuthorizedFixedRegionScope CreateFor(
        PlanDefinition plan,
        PlanOccurrence occurrence,
        ConsentLease lease,
        string scopeId,
        DateTimeOffset createdAtUtc,
        AuthorizedScopeTargetType targetType,
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
        AuthorizedCaptureBackend backend,
        AuthorizedAudioMode audioMode,
        TimeSpan reservedDuration,
        int countdownSeconds,
        string outputDirectory,
        string frozenFileName,
        AuthorizedOutputConflictPolicy outputConflictPolicy,
        AuthorizedWakePolicy wakePolicy,
        AuthorizedDesktopRequirement desktopRequirement,
        string currentUserSid,
        string sessionBinding,
        string topologyDigest)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(occurrence);
        ArgumentNullException.ThrowIfNull(lease);

        if (!plan.IsOneTime)
        {
            throw new Phase3DomainException("scope_plan_not_one_time", "An authorization scope requires a one-time plan.");
        }

        if (!string.Equals(occurrence.PlanId, plan.Id, StringComparison.Ordinal))
        {
            throw new Phase3DomainException("scope_relation_mismatch", "The occurrence does not belong to the plan.");
        }

        if (!string.Equals(lease.PlanId, plan.Id, StringComparison.Ordinal) ||
            !string.Equals(lease.OccurrenceId, occurrence.Id, StringComparison.Ordinal))
        {
            throw new Phase3DomainException("scope_relation_mismatch", "The lease does not belong to the plan and occurrence.");
        }

        if (lease.Status is not (ConsentLeaseStatus.Pending or ConsentLeaseStatus.Active))
        {
            throw new Phase3DomainException("scope_lease_not_creatable", "A new scope may only be created for a pending or active lease.");
        }

        if (lease.MaxUses != 1)
        {
            throw new Phase3DomainException("scope_quota_not_supported", "An authorization scope requires a single-use lease.");
        }

        var duration = ValidateDuration(reservedDuration);
        if (duration > lease.MaxDuration)
        {
            throw new Phase3DomainException("scope_duration_out_of_scope", "The scope duration exceeds the lease maximum duration.");
        }

        if (lease.ValidUntilUtc < occurrence.WindowEndUtc)
        {
            throw new Phase3DomainException("scope_lease_window_insufficient", "The lease validity must cover the occurrence window.");
        }

        var created = Phase3Validation.Utc(createdAtUtc, nameof(createdAtUtc));
        if (created < occurrence.CreatedAtUtc ||
            created < lease.ValidFromUtc ||
            created >= lease.ValidUntilUtc)
        {
            throw new Phase3DomainException("scope_created_at_invalid", "The scope creation time is outside the authorization lifecycle.");
        }

        return new AuthorizedFixedRegionScope(
            scopeId,
            plan.Id,
            occurrence.Id,
            lease.Id,
            created,
            targetType,
            captureSemantics,
            coordinateSpace,
            displayIdentityStatus,
            stableDisplayFingerprint,
            displayBounds,
            regionWithinDisplay,
            dpiX,
            dpiY,
            physicalWidth,
            physicalHeight,
            orientation,
            backend,
            audioMode,
            duration,
            countdownSeconds,
            outputDirectory,
            frozenFileName,
            outputConflictPolicy,
            wakePolicy,
            desktopRequirement,
            currentUserSid,
            sessionBinding,
            topologyDigest,
            persistedDigest: null);
    }

    internal static AuthorizedFixedRegionScope Rehydrate(
        string scopeId,
        string planId,
        string occurrenceId,
        string leaseId,
        int authorizationVersion,
        DateTimeOffset createdAtUtc,
        string scopeDigest,
        AuthorizedScopeTargetType targetType,
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
        AuthorizedCaptureBackend backend,
        AuthorizedAudioMode audioMode,
        TimeSpan reservedDuration,
        int countdownSeconds,
        string outputDirectory,
        string frozenFileName,
        AuthorizedOutputConflictPolicy outputConflictPolicy,
        AuthorizedWakePolicy wakePolicy,
        AuthorizedDesktopRequirement desktopRequirement,
        string currentUserSid,
        string sessionBinding,
        string topologyDigest)
    {
        if (authorizationVersion != CurrentAuthorizationVersion)
        {
            throw new Phase3DomainException("unsupported_authorization_version", "The authorization scope version is not supported.");
        }

        return new AuthorizedFixedRegionScope(
            scopeId,
            planId,
            occurrenceId,
            leaseId,
            createdAtUtc,
            targetType,
            captureSemantics,
            coordinateSpace,
            displayIdentityStatus,
            stableDisplayFingerprint,
            displayBounds,
            regionWithinDisplay,
            dpiX,
            dpiY,
            physicalWidth,
            physicalHeight,
            orientation,
            backend,
            audioMode,
            reservedDuration,
            countdownSeconds,
            outputDirectory,
            frozenFileName,
            outputConflictPolicy,
            wakePolicy,
            desktopRequirement,
            currentUserSid,
            sessionBinding,
            topologyDigest,
            scopeDigest);
    }

    internal bool MatchesExactly(AuthorizedFixedRegionScope other) =>
        string.Equals(ScopeId, other.ScopeId, StringComparison.Ordinal) &&
        string.Equals(PlanId, other.PlanId, StringComparison.Ordinal) &&
        string.Equals(OccurrenceId, other.OccurrenceId, StringComparison.Ordinal) &&
        string.Equals(LeaseId, other.LeaseId, StringComparison.Ordinal) &&
        CreatedAtUtc == other.CreatedAtUtc &&
        ScopeDigest == other.ScopeDigest &&
        TargetType == other.TargetType &&
        CaptureSemantics == other.CaptureSemantics &&
        CoordinateSpace == other.CoordinateSpace &&
        DisplayIdentityStatus == other.DisplayIdentityStatus &&
        string.Equals(StableDisplayFingerprint, other.StableDisplayFingerprint, StringComparison.Ordinal) &&
        DisplayBounds == other.DisplayBounds &&
        RegionWithinDisplay == other.RegionWithinDisplay &&
        DpiX == other.DpiX &&
        DpiY == other.DpiY &&
        PhysicalWidth == other.PhysicalWidth &&
        PhysicalHeight == other.PhysicalHeight &&
        Orientation == other.Orientation &&
        Backend == other.Backend &&
        AudioMode == other.AudioMode &&
        ReservedDuration == other.ReservedDuration &&
        CountdownSeconds == other.CountdownSeconds &&
        string.Equals(OutputDirectory, other.OutputDirectory, StringComparison.Ordinal) &&
        string.Equals(FrozenFileName, other.FrozenFileName, StringComparison.Ordinal) &&
        OutputConflictPolicy == other.OutputConflictPolicy &&
        WakePolicy == other.WakePolicy &&
        DesktopRequirement == other.DesktopRequirement &&
        string.Equals(CurrentUserSid, other.CurrentUserSid, StringComparison.Ordinal) &&
        string.Equals(SessionBinding, other.SessionBinding, StringComparison.Ordinal) &&
        string.Equals(TopologyDigest, other.TopologyDigest, StringComparison.Ordinal);

    private void ValidateGeometry()
    {
        if (RegionWithinDisplay.X < 0 || RegionWithinDisplay.Y < 0 ||
            RegionWithinDisplay.Width <= 0 || RegionWithinDisplay.Height <= 0 ||
            PhysicalWidth != DisplayBounds.Width ||
            PhysicalHeight != DisplayBounds.Height)
        {
            throw new Phase3DomainException("invalid_scope_geometry", "The region and physical display dimensions are invalid.");
        }

        try
        {
            if (checked(RegionWithinDisplay.X + RegionWithinDisplay.Width) > DisplayBounds.Width ||
                checked(RegionWithinDisplay.Y + RegionWithinDisplay.Height) > DisplayBounds.Height)
            {
                throw new Phase3DomainException("region_out_of_bounds", "The fixed region must be fully inside the display bounds.");
            }

            _ = VirtualScreenRegion;
        }
        catch (OverflowException)
        {
            throw new Phase3DomainException("invalid_scope_geometry", "The region geometry calculation overflowed.");
        }
    }

    private void ValidateOutputPath()
    {
        try
        {
            var candidate = Path.GetFullPath(Path.Combine(OutputDirectory, FrozenFileName));
            var directoryPrefix = OutputDirectory.EndsWith(Path.DirectorySeparatorChar) ||
                OutputDirectory.EndsWith(Path.AltDirectorySeparatorChar)
                ? OutputDirectory
                : OutputDirectory + Path.DirectorySeparatorChar;
            if (candidate.Equals(OutputDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar), StringComparison.OrdinalIgnoreCase) ||
                !candidate.StartsWith(directoryPrefix, StringComparison.OrdinalIgnoreCase))
            {
                throw new Phase3DomainException("invalid_output_path", "The frozen output file must remain inside the normalized output directory.");
            }
        }
        catch (ArgumentException)
        {
            throw new Phase3DomainException("invalid_output_path", "The output path is invalid.");
        }
        catch (NotSupportedException)
        {
            throw new Phase3DomainException("invalid_output_path", "The output path is not supported.");
        }
    }

    private static TimeSpan ValidateDuration(TimeSpan duration)
    {
        Phase3Validation.Positive(duration, nameof(duration));
        if (duration.Ticks % TimeSpan.TicksPerMillisecond != 0)
        {
            throw new Phase3DomainException("duration_not_representable", "The scope duration must be exactly representable in milliseconds.");
        }

        if (duration > MaximumReservedDuration)
        {
            throw new Phase3DomainException("scope_duration_too_long", "The scope duration must not exceed ten minutes.");
        }

        return duration;
    }

    private static int Positive(int value, string name)
    {
        if (value <= 0)
        {
            throw new Phase3DomainException("invalid_scope_dimension", $"{name} must be positive.");
        }

        return value;
    }

    private static T RequireDefined<T>(T value, string name)
        where T : struct, Enum
    {
        if (!Enum.IsDefined(value))
        {
            throw new Phase3DomainException("invalid_scope_enum", $"{name} is not recognized.");
        }

        return value;
    }

    private static string RequiredCanonicalText(string? value, string name, bool allowSeparators)
    {
        if (string.IsNullOrWhiteSpace(value) || !string.Equals(value, value.Trim(), StringComparison.Ordinal))
        {
            throw new Phase3DomainException("invalid_scope_text", $"{name} must be a non-blank canonical value.");
        }

        if (!allowSeparators && (value.Contains('\\') || value.Contains('/')))
        {
            throw new Phase3DomainException("invalid_scope_text", $"{name} must not contain path separators.");
        }

        return value;
    }

    private static string ValidateSha256Digest(string? value, string name)
    {
        var canonical = RequiredCanonicalText(value, name, allowSeparators: false);
        if (canonical.Length != 64 || canonical.Any(character =>
                character < '0' || (character > '9' && character < 'a') || character > 'f'))
        {
            throw new Phase3DomainException("invalid_scope_digest", $"{name} must be lowercase hexadecimal SHA-256.");
        }

        return canonical;
    }

    internal static string NormalizeOutputDirectoryForAuthorization(string? value) =>
        NormalizeOutputDirectory(value);

    internal static string NormalizeFrozenFileNameForAuthorization(string? value) =>
        NormalizeFileName(value);

    private static string NormalizeOutputDirectory(string? value)
    {
        var canonical = RequiredCanonicalText(value, nameof(OutputDirectory), allowSeparators: true);
        if (!Path.IsPathFullyQualified(canonical))
        {
            throw new Phase3DomainException("invalid_output_directory", "The output directory must be absolute.");
        }

        try
        {
            var fullPath = Path.GetFullPath(canonical);
            var root = Path.GetPathRoot(fullPath);
            if (string.IsNullOrEmpty(root))
            {
                throw new Phase3DomainException("invalid_output_directory", "The output directory has no root.");
            }

            if (string.Equals(fullPath, root, StringComparison.OrdinalIgnoreCase))
            {
                return root;
            }

            return fullPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        }
        catch (ArgumentException)
        {
            throw new Phase3DomainException("invalid_output_directory", "The output directory is invalid.");
        }
        catch (NotSupportedException)
        {
            throw new Phase3DomainException("invalid_output_directory", "The output directory is not supported.");
        }
    }

    private static string NormalizeFileName(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || !string.Equals(value, value.Trim(), StringComparison.Ordinal))
        {
            throw new Phase3DomainException("invalid_file_name", "The frozen file name is invalid.");
        }

        var canonical = value;
        if (canonical is "." or ".." ||
            canonical.Any(char.IsControl) ||
            canonical.Contains('\\') ||
            canonical.Contains('/') ||
            canonical.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            throw new Phase3DomainException("invalid_file_name", "The frozen file name is invalid.");
        }

        return canonical;
    }
}

public static class AuthorizedFixedRegionScopeCodes
{
    public static string ToCode(AuthorizedScopeTargetType value) => value switch
    {
        AuthorizedScopeTargetType.FixedRegion => "fixed_region",
        AuthorizedScopeTargetType.Window => "window",
        _ => throw Unknown(nameof(value)),
    };

    public static string ToCode(AuthorizedCaptureSemantics value) => value switch
    {
        AuthorizedCaptureSemantics.DesktopRegion => "desktop_region",
        AuthorizedCaptureSemantics.WindowSurface => "window_surface",
        _ => throw Unknown(nameof(value)),
    };

    public static string ToCode(AuthorizedCoordinateSpace value) => value switch
    {
        AuthorizedCoordinateSpace.PhysicalVirtualScreen => "physical_virtual_screen",
        AuthorizedCoordinateSpace.LogicalDesktop => "logical_desktop",
        _ => throw Unknown(nameof(value)),
    };

    public static string ToCode(AuthorizedDisplayIdentityStatus value) => value switch
    {
        AuthorizedDisplayIdentityStatus.Resolved => "resolved",
        AuthorizedDisplayIdentityStatus.Unresolved => "unresolved",
        _ => throw Unknown(nameof(value)),
    };

    public static string ToCode(AuthorizedDisplayOrientation value) => value switch
    {
        AuthorizedDisplayOrientation.Landscape => "landscape",
        AuthorizedDisplayOrientation.Portrait => "portrait",
        AuthorizedDisplayOrientation.LandscapeFlipped => "landscape_flipped",
        AuthorizedDisplayOrientation.PortraitFlipped => "portrait_flipped",
        _ => throw Unknown(nameof(value)),
    };

    public static string ToCode(AuthorizedCaptureBackend value) => value switch
    {
        AuthorizedCaptureBackend.FfmpegRegion => "ffmpeg-region",
        AuthorizedCaptureBackend.Wgc => "wgc",
        _ => throw Unknown(nameof(value)),
    };

    public static string ToCode(AuthorizedAudioMode value) => value switch
    {
        AuthorizedAudioMode.None => "none",
        AuthorizedAudioMode.Microphone => "microphone",
        AuthorizedAudioMode.SystemAudio => "system_audio",
        _ => throw Unknown(nameof(value)),
    };

    public static string ToCode(AuthorizedOutputConflictPolicy value) => value switch
    {
        AuthorizedOutputConflictPolicy.FailIfExists => "fail_if_exists",
        AuthorizedOutputConflictPolicy.Rename => "rename",
        _ => throw Unknown(nameof(value)),
    };

    public static string ToCode(AuthorizedWakePolicy value) => value switch
    {
        AuthorizedWakePolicy.NaturalWakeOnly => "natural_wake_only",
        AuthorizedWakePolicy.ScheduledWake => "scheduled_wake",
        _ => throw Unknown(nameof(value)),
    };

    public static string ToCode(AuthorizedDesktopRequirement value) => value switch
    {
        AuthorizedDesktopRequirement.InteractiveDesktopRequired => "interactive_desktop_required",
        AuthorizedDesktopRequirement.AnyDesktop => "any_desktop",
        _ => throw Unknown(nameof(value)),
    };

    public static AuthorizedScopeTargetType ParseTargetType(string code) =>
        code switch
        {
            "fixed_region" => AuthorizedScopeTargetType.FixedRegion,
            "window" => AuthorizedScopeTargetType.Window,
            _ => throw Unknown(code),
        };

    public static AuthorizedCaptureSemantics ParseCaptureSemantics(string code) =>
        code switch
        {
            "desktop_region" => AuthorizedCaptureSemantics.DesktopRegion,
            "window_surface" => AuthorizedCaptureSemantics.WindowSurface,
            _ => throw Unknown(code),
        };

    public static AuthorizedCoordinateSpace ParseCoordinateSpace(string code) =>
        code switch
        {
            "physical_virtual_screen" => AuthorizedCoordinateSpace.PhysicalVirtualScreen,
            "logical_desktop" => AuthorizedCoordinateSpace.LogicalDesktop,
            _ => throw Unknown(code),
        };

    public static AuthorizedDisplayIdentityStatus ParseDisplayIdentityStatus(string code) =>
        code switch
        {
            "resolved" => AuthorizedDisplayIdentityStatus.Resolved,
            "unresolved" => AuthorizedDisplayIdentityStatus.Unresolved,
            _ => throw Unknown(code),
        };

    public static AuthorizedDisplayOrientation ParseOrientation(string code) =>
        code switch
        {
            "landscape" => AuthorizedDisplayOrientation.Landscape,
            "portrait" => AuthorizedDisplayOrientation.Portrait,
            "landscape_flipped" => AuthorizedDisplayOrientation.LandscapeFlipped,
            "portrait_flipped" => AuthorizedDisplayOrientation.PortraitFlipped,
            _ => throw Unknown(code),
        };

    public static AuthorizedCaptureBackend ParseBackend(string code) =>
        code switch
        {
            "ffmpeg-region" => AuthorizedCaptureBackend.FfmpegRegion,
            "wgc" => AuthorizedCaptureBackend.Wgc,
            _ => throw Unknown(code),
        };

    public static AuthorizedAudioMode ParseAudioMode(string code) =>
        code switch
        {
            "none" => AuthorizedAudioMode.None,
            "microphone" => AuthorizedAudioMode.Microphone,
            "system_audio" => AuthorizedAudioMode.SystemAudio,
            _ => throw Unknown(code),
        };

    public static AuthorizedOutputConflictPolicy ParseOutputConflictPolicy(string code) =>
        code switch
        {
            "fail_if_exists" => AuthorizedOutputConflictPolicy.FailIfExists,
            "rename" => AuthorizedOutputConflictPolicy.Rename,
            _ => throw Unknown(code),
        };

    public static AuthorizedWakePolicy ParseWakePolicy(string code) =>
        code switch
        {
            "natural_wake_only" => AuthorizedWakePolicy.NaturalWakeOnly,
            "scheduled_wake" => AuthorizedWakePolicy.ScheduledWake,
            _ => throw Unknown(code),
        };

    public static AuthorizedDesktopRequirement ParseDesktopRequirement(string code) =>
        code switch
        {
            "interactive_desktop_required" => AuthorizedDesktopRequirement.InteractiveDesktopRequired,
            "any_desktop" => AuthorizedDesktopRequirement.AnyDesktop,
            _ => throw Unknown(code),
        };

    private static Phase3DomainException Unknown(string value) =>
        new("invalid_scope_enum", "Unknown authorization scope code.");
}

public static class AuthorizedFixedRegionScopeDigest
{
    public const string SchemaName = "authorized-fixed-region-scope/v1";

    public static string Compute(AuthorizedFixedRegionScope scope)
    {
        ArgumentNullException.ThrowIfNull(scope);
        var bytes = new List<byte>(1024);

        AppendString(bytes, SchemaName);
        AppendInt32(bytes, scope.AuthorizationVersion);
        AppendString(bytes, scope.ScopeId);
        AppendString(bytes, scope.PlanId);
        AppendString(bytes, scope.OccurrenceId);
        AppendString(bytes, scope.LeaseId);
        AppendInt64(bytes, scope.CreatedAtUtc.UtcDateTime.Ticks);
        AppendString(bytes, AuthorizedFixedRegionScopeCodes.ToCode(scope.TargetType));
        AppendString(bytes, AuthorizedFixedRegionScopeCodes.ToCode(scope.CaptureSemantics));
        AppendString(bytes, AuthorizedFixedRegionScopeCodes.ToCode(scope.CoordinateSpace));
        AppendString(bytes, AuthorizedFixedRegionScopeCodes.ToCode(scope.DisplayIdentityStatus));
        AppendString(bytes, scope.StableDisplayFingerprint);
        AppendRectangle(bytes, scope.DisplayBounds);
        AppendRectangle(bytes, scope.RegionWithinDisplay);
        AppendRectangle(bytes, scope.VirtualScreenRegion);
        AppendInt32(bytes, scope.DpiX);
        AppendInt32(bytes, scope.DpiY);
        AppendInt32(bytes, scope.PhysicalWidth);
        AppendInt32(bytes, scope.PhysicalHeight);
        AppendString(bytes, AuthorizedFixedRegionScopeCodes.ToCode(scope.Orientation));
        AppendString(bytes, AuthorizedFixedRegionScopeCodes.ToCode(scope.Backend));
        AppendString(bytes, AuthorizedFixedRegionScopeCodes.ToCode(scope.AudioMode));
        AppendInt64(bytes, scope.ReservedDuration.Ticks / TimeSpan.TicksPerMillisecond);
        AppendInt32(bytes, scope.CountdownSeconds);
        AppendString(bytes, scope.OutputDirectory);
        AppendString(bytes, scope.FrozenFileName);
        AppendString(bytes, AuthorizedFixedRegionScopeCodes.ToCode(scope.OutputConflictPolicy));
        AppendString(bytes, AuthorizedFixedRegionScopeCodes.ToCode(scope.WakePolicy));
        AppendString(bytes, AuthorizedFixedRegionScopeCodes.ToCode(scope.DesktopRequirement));
        AppendString(bytes, scope.CurrentUserSid);
        AppendString(bytes, scope.SessionBinding);
        AppendString(bytes, scope.TopologyDigest);

        return Convert.ToHexString(SHA256.HashData(bytes.ToArray())).ToLowerInvariant();
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
