using System.Globalization;
using System.Reflection;
using AgentRecorder.Core.Automation;
using Xunit;

namespace AgentRecorder.Tests;

[Collection("NonParallel-AgentRecorderDataDir")]
public sealed class RecurringFixedRegionProfileTests
{
    private static readonly DateTimeOffset CreatedAt = new(2026, 9, 1, 10, 0, 0, TimeSpan.Zero);
    private const string TopologyDigest = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";

    [Fact]
    public void Version1ContainsTheCompleteImmutableSafeSubset()
    {
        var profile = CreateProfile();

        Assert.Equal("daily-demo", profile.ProfileId);
        Assert.Equal(1, profile.ProfileVersion);
        Assert.Equal(CreatedAt, profile.CreatedAtUtc);
        Assert.Equal(AuthorizedScopeTargetType.FixedRegion, profile.TargetType);
        Assert.Equal(RecurringFixedRegionRebindPolicy.ExactMatchOnly, profile.RebindPolicy);
        Assert.Equal(AuthorizedCaptureSemantics.DesktopRegion, profile.CaptureSemantics);
        Assert.Equal(AuthorizedCoordinateSpace.PhysicalVirtualScreen, profile.CoordinateSpace);
        Assert.Equal(AuthorizedDisplayIdentityStatus.Resolved, profile.DisplayIdentityStatus);
        Assert.Equal(new AuthorizedPhysicalRectangle(-100, 50, 1920, 1080), profile.DisplayBounds);
        Assert.Equal(new AuthorizedPhysicalRectangle(10, 20, 640, 480), profile.RegionWithinDisplay);
        Assert.Equal(new AuthorizedPhysicalRectangle(-90, 70, 640, 480), profile.VirtualScreenRegion);
        Assert.Equal(96, profile.DpiX);
        Assert.Equal(144, profile.DpiY);
        Assert.Equal(1920, profile.PhysicalWidth);
        Assert.Equal(1080, profile.PhysicalHeight);
        Assert.Equal(AuthorizedDisplayOrientation.Landscape, profile.Orientation);
        Assert.Equal(AuthorizedCaptureBackend.FfmpegRegion, profile.Backend);
        Assert.Equal(AuthorizedAudioMode.None, profile.AudioMode);
        Assert.Equal(TimeSpan.FromMinutes(2), profile.Duration);
        Assert.Equal(3, profile.CountdownSeconds);
        Assert.Equal(AuthorizedOutputConflictPolicy.FailIfExists, profile.OutputConflictPolicy);
        Assert.Equal(AuthorizedWakePolicy.NaturalWakeOnly, profile.WakePolicy);
        Assert.Equal(AuthorizedDesktopRequirement.InteractiveDesktopRequired, profile.DesktopRequirement);
        Assert.Equal("demo-区域", profile.FilenamePrefix);
        Assert.Equal("demo-区域-{scheduled_utc}-{occurrence_id}.mp4", profile.FilenameTemplate);
        Assert.StartsWith(RecurringFixedRegionProfileVersion.DigestPrefix, profile.ProfileDigest, StringComparison.Ordinal);
        Assert.Equal(profile.ProfileDigest, RecurringFixedRegionProfileDigest.Compute(profile));
        Assert.Equal(profile.Reference, profile.ProfileReference);
    }

    [Fact]
    public void ProfileAndSpecificationExposeNoAuthorizationOrRunState()
    {
        var profileNames = typeof(RecurringFixedRegionProfileVersion)
            .GetProperties(BindingFlags.Instance | BindingFlags.Public)
            .Select(property => property.Name)
            .ToArray();
        var specificationNames = typeof(RecurringFixedRegionProfileSpecification)
            .GetProperties(BindingFlags.Instance | BindingFlags.Public)
            .Select(property => property.Name)
            .ToArray();

        foreach (var name in profileNames.Concat(specificationNames))
        {
            Assert.DoesNotContain("Authorization", name, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("Lease", name, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("Run", name, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("Occurrence", name, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("Confirmation", name, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("Skip", name, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void NextVersionIsContiguousImmutableAndDigestDistinctEvenForSameSpecification()
    {
        var first = CreateProfile();
        var next = RecurringFixedRegionProfileVersion.CreateNextVersion(first, CreateSpec(), CreatedAt);

        Assert.Equal(2, next.ProfileVersion);
        Assert.Equal(first.ProfileId, next.ProfileId);
        Assert.Equal(first.CreatedAtUtc, next.CreatedAtUtc);
        Assert.NotEqual(first.ProfileDigest, next.ProfileDigest);
        Assert.Equal(1, first.ProfileVersion);
        Assert.Equal(first.ProfileDigest, RecurringFixedRegionProfileDigest.Compute(first));
    }

    [Fact]
    public void VersionCreationRejectsNonUtcAndBackwardsCreationTime()
    {
        AssertReason(RecurringFixedRegionProfileReasonCodes.ProfileNonUtcTime, () =>
            RecurringFixedRegionProfileVersion.CreateVersion1(
                "p",
                CreatedAt.ToOffset(TimeSpan.FromHours(8)),
                CreateSpec()));

        var first = CreateProfile();
        AssertReason(RecurringFixedRegionProfileReasonCodes.ProfileNonMonotonicTime, () =>
            RecurringFixedRegionProfileVersion.CreateNextVersion(first, CreateSpec(), CreatedAt.AddTicks(-1)));
    }

    [Fact]
    public void VersionRehydrateRejectsInvalidVersionAndNonContiguousOverflowIsFailClosed()
    {
        var profile = CreateProfile();
        AssertReason(RecurringFixedRegionProfileReasonCodes.ProfileVersionInvalid, () =>
            RecurringFixedRegionProfileVersion.Rehydrate(
                profile.ProfileId, 0, profile.CreatedAtUtc, CreateSpec(), profile.ProfileDigest));

        var maxVersion = CreateVersionForTest(long.MaxValue);
        AssertReason(RecurringFixedRegionProfileReasonCodes.ProfileValueOverflow, () =>
            RecurringFixedRegionProfileVersion.CreateNextVersion(maxVersion, CreateSpec(), CreatedAt));
    }

    [Fact]
    public void NegativeVirtualCoordinatesAreLegalButRegionAndCheckedOverflowAreRejected()
    {
        var negativeDisplay = CreateProfile(CreateSpec(displayBounds: new AuthorizedPhysicalRectangle(int.MinValue, -500, 1920, 1080)));
        Assert.Equal(int.MinValue + 10, negativeDisplay.VirtualScreenRegion.X);

        AssertReason(RecurringFixedRegionProfileReasonCodes.ProfileGeometryInvalid, () =>
            CreateProfile(CreateSpec(regionWithinDisplay: new AuthorizedPhysicalRectangle(-1, 0, 10, 10))));
        AssertReason(RecurringFixedRegionProfileReasonCodes.ProfileGeometryInvalid, () =>
            CreateProfile(CreateSpec(regionWithinDisplay: new AuthorizedPhysicalRectangle(1919, 0, 2, 10))));
        AssertReason(RecurringFixedRegionProfileReasonCodes.ProfileValueOverflow, () =>
            CreateProfile(CreateSpec(
                displayBounds: new AuthorizedPhysicalRectangle(int.MaxValue, 0, 1920, 1080))));
        AssertReason(RecurringFixedRegionProfileReasonCodes.ProfileGeometryInvalid, () =>
            CreateProfile(CreateSpec(physicalWidth: 1919)));
        AssertReason(RecurringFixedRegionProfileReasonCodes.ProfileGeometryInvalid, () =>
            CreateProfile(CreateSpec(dpiX: 0)));
        AssertReason(RecurringFixedRegionProfileReasonCodes.ProfileGeometryInvalid, () =>
            CreateProfile(CreateSpec(orientation: (AuthorizedDisplayOrientation)999)));
    }

    [Fact]
    public void DurationAndCountdownUseTheCurrentProductBoundaries()
    {
        Assert.Equal(TimeSpan.FromMilliseconds(1), CreateProfile(CreateSpec(duration: TimeSpan.FromMilliseconds(1))).Duration);
        Assert.Equal(TimeSpan.FromMinutes(10), CreateProfile(CreateSpec(duration: TimeSpan.FromMinutes(10))).Duration);
        Assert.Equal(0, CreateProfile(CreateSpec(countdownSeconds: 0)).CountdownSeconds);
        Assert.Equal(10, CreateProfile(CreateSpec(countdownSeconds: 10)).CountdownSeconds);
        AssertReason(RecurringFixedRegionProfileReasonCodes.ProfileDurationInvalid, () => CreateProfile(CreateSpec(duration: TimeSpan.Zero)));
        AssertReason(RecurringFixedRegionProfileReasonCodes.ProfileDurationInvalid, () => CreateProfile(CreateSpec(duration: TimeSpan.FromMinutes(10) + TimeSpan.FromMilliseconds(1))));
        AssertReason(RecurringFixedRegionProfileReasonCodes.ProfileCountdownInvalid, () => CreateProfile(CreateSpec(countdownSeconds: -1)));
        AssertReason(RecurringFixedRegionProfileReasonCodes.ProfileCountdownInvalid, () => CreateProfile(CreateSpec(countdownSeconds: 11)));
    }

    [Fact]
    public void EveryUnsupportedPolicyFailsClosedWithoutDefaulting()
    {
        AssertUnsupported(CreateSpec(targetType: AuthorizedScopeTargetType.Window));
        AssertUnsupported(CreateSpec(rebindPolicy: (RecurringFixedRegionRebindPolicy)999));
        AssertUnsupported(CreateSpec(captureSemantics: AuthorizedCaptureSemantics.WindowSurface));
        AssertUnsupported(CreateSpec(coordinateSpace: AuthorizedCoordinateSpace.LogicalDesktop));
        AssertUnsupported(CreateSpec(displayIdentityStatus: AuthorizedDisplayIdentityStatus.Unresolved));
        AssertUnsupported(CreateSpec(backend: AuthorizedCaptureBackend.Wgc));
        AssertUnsupported(CreateSpec(audioMode: AuthorizedAudioMode.Microphone));
        AssertUnsupported(CreateSpec(audioMode: AuthorizedAudioMode.SystemAudio));
        AssertUnsupported(CreateSpec(outputConflictPolicy: AuthorizedOutputConflictPolicy.Rename));
        AssertUnsupported(CreateSpec(wakePolicy: AuthorizedWakePolicy.ScheduledWake));
        AssertUnsupported(CreateSpec(desktopRequirement: AuthorizedDesktopRequirement.AnyDesktop));
    }

    [Fact]
    public void OutputDirectoryIsNormalizedAndMustBeAbsolute()
    {
        var profile = CreateProfile(CreateSpec(outputDirectory: Path.Combine(Path.GetTempPath(), "..", "task253-output")));
        Assert.True(Path.IsPathFullyQualified(profile.OutputDirectory));
        Assert.EndsWith(Path.DirectorySeparatorChar.ToString(), profile.OutputDirectory, StringComparison.Ordinal);
        AssertReason(RecurringFixedRegionProfileReasonCodes.ProfileOutputDirectoryInvalid, () => CreateProfile(CreateSpec(outputDirectory: "relative-output")));
        AssertReason(RecurringFixedRegionProfileReasonCodes.ProfileOutputDirectoryInvalid, () => CreateProfile(CreateSpec(outputDirectory: "")));
        AssertReason(RecurringFixedRegionProfileReasonCodes.ProfileOutputDirectoryInvalid, () => CreateProfile(CreateSpec(outputDirectory: "C:\\bad\0path")));
    }

    [Fact]
    public void FilenamePrefixAcceptsUnicodeAndRejectsPathTraversalAndWindowsDevices()
    {
        Assert.Equal("中文 demo-1", CreateProfile(CreateSpec(filenamePrefix: "  中文 demo-1  ")).FilenamePrefix);

        foreach (var invalid in new[]
        {
            "", " ", "a.", "a..b", "a/b", "a\\b", "a:b", "a?b", "a\tb", "a\u0001b",
            "CON", "PRN", "AUX", "NUL", "COM1", "LPT9", new string('a', 65)
        })
        {
            AssertReason(RecurringFixedRegionProfileReasonCodes.ProfileFilenamePrefixInvalid, () => CreateProfile(CreateSpec(filenamePrefix: invalid)));
        }
    }

    [Fact]
    public void OutputFilenameUsesFixedUtcCultureAndOccurrenceIdentity()
    {
        var profile = CreateProfile();
        const string identity = "recurring-occurrence/v1:0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";
        var scheduled = new DateTimeOffset(2026, 9, 1, 2, 3, 4, TimeSpan.Zero);
        var fileName = profile.RenderOutputFileName(identity, scheduled);

        Assert.Equal("demo-区域-20260901T020304Z-0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef.mp4", fileName);
        Assert.EndsWith(".mp4", fileName, StringComparison.Ordinal);
        Assert.Equal(fileName, RecurringFixedRegionProfileVersion.RenderOutputFileName(profile, identity, scheduled));
        Assert.Equal(fileName, profile.RenderOutputFileName(identity, scheduled));
        Assert.Equal(
            Path.GetFullPath(Path.Combine(profile.OutputDirectory, fileName)),
            profile.ResolveOutputPath(identity, scheduled));
    }

    [Fact]
    public void OutputRenderingRejectsNonUtcSubsecondAndMalformedOccurrenceIdentity()
    {
        var profile = CreateProfile();
        const string identity = "recurring-occurrence/v1:0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";
        var scheduled = new DateTimeOffset(2026, 9, 1, 2, 3, 4, TimeSpan.Zero);

        AssertReason(RecurringFixedRegionProfileReasonCodes.ProfileNonUtcTime, () =>
            profile.RenderOutputFileName(identity, scheduled.ToOffset(TimeSpan.FromHours(8))));
        AssertReason(RecurringFixedRegionProfileReasonCodes.ProfileScheduledTimeInvalid, () =>
            profile.RenderOutputFileName(identity, scheduled.AddTicks(1)));
        AssertReason(RecurringFixedRegionProfileReasonCodes.ProfileOccurrenceIdentityInvalid, () =>
            profile.RenderOutputFileName("recurring-occurrence/v1:ABC", scheduled));
        AssertReason(RecurringFixedRegionProfileReasonCodes.ProfileOccurrenceIdentityInvalid, () =>
            profile.RenderOutputFileName("other/v1:0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef", scheduled));
    }

    [Fact]
    public void FilenameRenderingIsDeterministicDistinctAndFilesystemFree()
    {
        var directory = Path.Combine(Path.GetTempPath(), "task253-no-side-effect-" + Guid.NewGuid().ToString("N"));
        var profile = CreateProfile(CreateSpec(outputDirectory: directory));
        const string first = "recurring-occurrence/v1:0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";
        const string second = "recurring-occurrence/v1:fedcba9876543210fedcba9876543210fedcba9876543210fedcba9876543210";
        var scheduled = new DateTimeOffset(2026, 9, 1, 2, 3, 4, TimeSpan.Zero);

        var firstPath = profile.ResolveOutputPath(first, scheduled);
        Assert.Equal(firstPath, profile.ResolveOutputPath(first, scheduled));
        Assert.NotEqual(firstPath, profile.ResolveOutputPath(second, scheduled));
        Assert.False(Directory.Exists(directory));
        Assert.False(File.Exists(firstPath));
    }

    [Fact]
    public void RehydrateRecomputesDigestAndRejectsFormatMismatchContentMismatchAndTemplateTamper()
    {
        var source = CreateProfile();
        var rehydrated = RecurringFixedRegionProfileVersion.Rehydrate(
            source.ProfileId,
            source.ProfileVersion,
            source.CreatedAtUtc,
            CreateSpec(),
            source.ProfileDigest,
            source.FilenameTemplate);
        Assert.Equal(source.ProfileDigest, rehydrated.ProfileDigest);
        Assert.Equal(source.Reference, rehydrated.Reference);

        AssertReason(RecurringFixedRegionProfileReasonCodes.ProfileDigestInvalid, () =>
            RecurringFixedRegionProfileVersion.Rehydrate(source.ProfileId, source.ProfileVersion, source.CreatedAtUtc, CreateSpec(), new string('0', 64)));
        AssertReason(RecurringFixedRegionProfileReasonCodes.ProfileDigestMismatch, () =>
            RecurringFixedRegionProfileVersion.Rehydrate(source.ProfileId, source.ProfileVersion, source.CreatedAtUtc, CreateSpec(), RecurringFixedRegionProfileVersion.DigestPrefix + new string('0', 64)));
        AssertReason(RecurringFixedRegionProfileReasonCodes.ProfileFilenamePrefixInvalid, () =>
            RecurringFixedRegionProfileVersion.Rehydrate(source.ProfileId, source.ProfileVersion, source.CreatedAtUtc, CreateSpec(), source.ProfileDigest, "other-{scheduled_utc}-{occurrence_id}.mp4"));
    }

    [Fact]
    public void ProfileRefRequiresExactIdVersionAndDigest()
    {
        var profile = CreateProfile();
        Assert.True(profile.Reference.Matches(profile));
        Assert.False(new ProfileRef(profile.ProfileId, profile.ProfileVersion + 1, profile.ProfileDigest).Matches(profile));
        Assert.False(new ProfileRef(profile.ProfileId + "-other", profile.ProfileVersion, profile.ProfileDigest).Matches(profile));
        Assert.False(new ProfileRef(profile.ProfileId, profile.ProfileVersion, RecurringFixedRegionProfileVersion.DigestPrefix + new string('0', 64)).Matches(profile));
    }

    [Fact]
    public void DigestChangesForEveryMutableSpecificationFieldAndCultureDoesNotMatter()
    {
        var originalCulture = CultureInfo.CurrentCulture;
        var originalUiCulture = CultureInfo.CurrentUICulture;
        try
        {
            var baseline = CreateProfile();
            var variants = new[]
            {
                CreateProfile(CreateSpec(stableDisplayFingerprint: "DISPLAY-FP-2")),
                CreateProfile(CreateSpec(displayBounds: new AuthorizedPhysicalRectangle(-100, 50, 1921, 1080), physicalWidth: 1921)),
                CreateProfile(CreateSpec(regionWithinDisplay: new AuthorizedPhysicalRectangle(11, 20, 640, 480))),
                CreateProfile(CreateSpec(dpiX: 97)),
                CreateProfile(CreateSpec(dpiY: 145)),
                CreateProfile(CreateSpec(physicalHeight: 1079, displayBounds: new AuthorizedPhysicalRectangle(-100, 50, 1920, 1079))),
                CreateProfile(CreateSpec(orientation: AuthorizedDisplayOrientation.Portrait)),
                CreateProfile(CreateSpec(topologyDigest: "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb")),
                CreateProfile(CreateSpec(duration: TimeSpan.FromMinutes(3))),
                CreateProfile(CreateSpec(countdownSeconds: 4)),
                CreateProfile(CreateSpec(outputDirectory: Path.Combine(Path.GetTempPath(), "task253-other-output"))),
                CreateProfile(CreateSpec(filenamePrefix: "other-prefix")),
            };

            foreach (var variant in variants)
                Assert.NotEqual(baseline.ProfileDigest, variant.ProfileDigest);

            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("zh-CN");
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("zh-CN");
            var chineseCulture = CreateProfile().ProfileDigest;
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("en-US");
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("en-US");
            var englishCulture = CreateProfile().ProfileDigest;
            Assert.Equal(chineseCulture, englishCulture);
        }
        finally
        {
            CultureInfo.CurrentCulture = originalCulture;
            CultureInfo.CurrentUICulture = originalUiCulture;
        }
    }

    [Fact]
    public void FixedStrategyCodesAreCanonicalAndOneShotDigestSchemaRemainsSeparate()
    {
        Assert.Equal("fixed_region", RecurringFixedRegionProfileCode.ToCode(AuthorizedScopeTargetType.FixedRegion));
        Assert.Equal("exact_match_only", RecurringFixedRegionProfileCode.ToCode(RecurringFixedRegionRebindPolicy.ExactMatchOnly));
        Assert.Equal("desktop_region", RecurringFixedRegionProfileCode.ToCode(AuthorizedCaptureSemantics.DesktopRegion));
        Assert.Equal("physical_virtual_screen", RecurringFixedRegionProfileCode.ToCode(AuthorizedCoordinateSpace.PhysicalVirtualScreen));
        Assert.Equal("ffmpeg-region", RecurringFixedRegionProfileCode.ToCode(AuthorizedCaptureBackend.FfmpegRegion));
        Assert.Equal("none", RecurringFixedRegionProfileCode.ToCode(AuthorizedAudioMode.None));
        Assert.Equal("fail_if_exists", RecurringFixedRegionProfileCode.ToCode(AuthorizedOutputConflictPolicy.FailIfExists));
        Assert.Equal("natural_wake_only", RecurringFixedRegionProfileCode.ToCode(AuthorizedWakePolicy.NaturalWakeOnly));
        Assert.Equal("interactive_desktop_required", RecurringFixedRegionProfileCode.ToCode(AuthorizedDesktopRequirement.InteractiveDesktopRequired));
        Assert.Equal("authorized-fixed-region-scope/v1", AuthorizedFixedRegionScopeDigest.SchemaName);
        Assert.NotEqual(AuthorizedFixedRegionScopeDigest.SchemaName, RecurringFixedRegionProfileDigest.SchemaName);
    }

    private static RecurringFixedRegionProfileVersion CreateProfile(RecurringFixedRegionProfileSpecification? specification = null) =>
        RecurringFixedRegionProfileVersion.CreateVersion1("daily-demo", CreatedAt, specification ?? CreateSpec());

    private static RecurringFixedRegionProfileVersion CreateVersionForTest(long version)
    {
        var seed = CreateProfile();
        return RecurringFixedRegionProfileVersion.CreateVersionForTests(
            seed.ProfileId,
            version,
            seed.CreatedAtUtc,
            CreateSpec());
    }

    private static RecurringFixedRegionProfileSpecification CreateSpec(
        AuthorizedScopeTargetType targetType = AuthorizedScopeTargetType.FixedRegion,
        RecurringFixedRegionRebindPolicy rebindPolicy = RecurringFixedRegionRebindPolicy.ExactMatchOnly,
        AuthorizedCaptureSemantics captureSemantics = AuthorizedCaptureSemantics.DesktopRegion,
        AuthorizedCoordinateSpace coordinateSpace = AuthorizedCoordinateSpace.PhysicalVirtualScreen,
        AuthorizedDisplayIdentityStatus displayIdentityStatus = AuthorizedDisplayIdentityStatus.Resolved,
        string? stableDisplayFingerprint = null,
        AuthorizedPhysicalRectangle? displayBounds = null,
        AuthorizedPhysicalRectangle? regionWithinDisplay = null,
        int dpiX = 96,
        int dpiY = 144,
        int physicalWidth = 1920,
        int physicalHeight = 1080,
        AuthorizedDisplayOrientation orientation = AuthorizedDisplayOrientation.Landscape,
        string? topologyDigest = null,
        AuthorizedCaptureBackend backend = AuthorizedCaptureBackend.FfmpegRegion,
        AuthorizedAudioMode audioMode = AuthorizedAudioMode.None,
        TimeSpan? duration = null,
        int countdownSeconds = 3,
        string? outputDirectory = null,
        string filenamePrefix = "demo-区域",
        AuthorizedOutputConflictPolicy outputConflictPolicy = AuthorizedOutputConflictPolicy.FailIfExists,
        AuthorizedWakePolicy wakePolicy = AuthorizedWakePolicy.NaturalWakeOnly,
        AuthorizedDesktopRequirement desktopRequirement = AuthorizedDesktopRequirement.InteractiveDesktopRequired) =>
        new(
            targetType,
            rebindPolicy,
            captureSemantics,
            coordinateSpace,
            displayIdentityStatus,
            stableDisplayFingerprint ?? "DISPLAY-FP-1",
            displayBounds ?? new AuthorizedPhysicalRectangle(-100, 50, 1920, 1080),
            regionWithinDisplay ?? new AuthorizedPhysicalRectangle(10, 20, 640, 480),
            dpiX,
            dpiY,
            physicalWidth,
            physicalHeight,
            orientation,
            topologyDigest ?? TopologyDigest,
            backend,
            audioMode,
            duration ?? TimeSpan.FromMinutes(2),
            countdownSeconds,
            outputDirectory ?? Path.Combine(Path.GetTempPath(), "task253-output"),
            filenamePrefix,
            outputConflictPolicy,
            wakePolicy,
            desktopRequirement);

    private static void AssertUnsupported(RecurringFixedRegionProfileSpecification specification) =>
        AssertReason(RecurringFixedRegionProfileReasonCodes.ProfilePolicyNotSupported, () => CreateProfile(specification));

    private static void AssertReason(string reason, Action action)
    {
        var exception = Assert.Throws<Phase3DomainException>(action);
        Assert.Equal(reason, exception.ReasonCode);
    }
}
