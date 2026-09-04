using System.Globalization;
using System.Reflection;
using AgentRecorder.Core.Automation;
using Xunit;

namespace AgentRecorder.Tests;

[Collection("NonParallel-AgentRecorderDataDir")]
public sealed class AuthorizedFixedRegionScopeTests
{
    [Fact]
    public void CreatesAnImmutableFixedRegionAndDerivesVirtualScreenGeometry()
    {
        var fixtures = CreateFixtures();
        var scope = CreateScope(fixtures);

        Assert.Equal(1, scope.AuthorizationVersion);
        Assert.Equal(AuthorizedScopeTargetType.FixedRegion, scope.TargetType);
        Assert.Equal(AuthorizedCaptureSemantics.DesktopRegion, scope.CaptureSemantics);
        Assert.Equal(AuthorizedCoordinateSpace.PhysicalVirtualScreen, scope.CoordinateSpace);
        Assert.Equal(AuthorizedDisplayIdentityStatus.Resolved, scope.DisplayIdentityStatus);
        Assert.Equal(new AuthorizedPhysicalRectangle(110, 150, 640, 480), scope.VirtualScreenRegion);
        Assert.Equal(Path.Combine(scope.OutputDirectory, scope.FrozenFileName), scope.OutputFilePath);
        Assert.Matches("^[0-9a-f]{64}$", scope.ScopeDigest);
        Assert.Equal(scope.ScopeDigest, AuthorizedFixedRegionScopeDigest.Compute(scope));
    }

    [Fact]
    public void CanonicalDigestIsStableAcrossCultureAndEquivalentUtcConstruction()
    {
        var fixtures = CreateFixtures();
        var originalCulture = CultureInfo.CurrentCulture;
        var originalUiCulture = CultureInfo.CurrentUICulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("en-US");
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("en-US");
            var first = CreateScope(fixtures);

            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("tr-TR");
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("tr-TR");
            var second = CreateScope(fixtures);

            Assert.Equal(first.ScopeDigest, second.ScopeDigest);
        }
        finally
        {
            CultureInfo.CurrentCulture = originalCulture;
            CultureInfo.CurrentUICulture = originalUiCulture;
        }
    }

    [Fact]
    public void EveryAuthorizationFieldChangeChangesTheDigest()
    {
        var fixtures = CreateFixtures();
        var original = CreateScope(fixtures);
        var changedRegion = CreateScope(fixtures, regionWithinDisplay: new AuthorizedPhysicalRectangle(11, 200, 640, 480));
        var changedTopology = CreateScope(fixtures, topologyDigest: "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb");

        Assert.NotEqual(original.ScopeDigest, changedRegion.ScopeDigest);
        Assert.NotEqual(original.ScopeDigest, changedTopology.ScopeDigest);
    }

    [Fact]
    public void DisplayOrdinalIsNotAnAuthorizationField()
    {
        var names = typeof(AuthorizedFixedRegionScope)
            .GetProperties(BindingFlags.Instance | BindingFlags.Public)
            .Select(property => property.Name)
            .ToArray();

        Assert.DoesNotContain("DisplayOrdinal", names);
        Assert.DoesNotContain("DisplayIndex", names);
        Assert.DoesNotContain("DisplayName", names);
    }

    [Fact]
    public void DigestSchemaIsIndependentFromCaptureProofSchema()
    {
        Assert.Equal("authorized-fixed-region-scope/v1", AuthorizedFixedRegionScopeDigest.SchemaName);
        Assert.NotEqual("capture-scope/v1", AuthorizedFixedRegionScopeDigest.SchemaName);
    }

    [Fact]
    public void DigestCannotBeInjectedThroughAConstructorAndRehydrateRejectsMismatch()
    {
        var publicConstructors = typeof(AuthorizedFixedRegionScope)
            .GetConstructors(BindingFlags.Instance | BindingFlags.Public);
        Assert.Empty(publicConstructors);

        var scope = CreateScope(CreateFixtures());
        var exception = Assert.Throws<Phase3DomainException>(() => Rehydrate(scope, new string('0', 64)));
        Assert.Equal("scope_digest_mismatch", exception.ReasonCode);
    }

    [Fact]
    public void InvalidGeometryDpiAndResolutionAreRejected()
    {
        var fixtures = CreateFixtures();
        AssertReason("region_out_of_bounds", () => CreateScope(fixtures, regionWithinDisplay: new AuthorizedPhysicalRectangle(1500, 700, 600, 480)));
        AssertReason("invalid_scope_geometry", () => CreateScope(fixtures, regionWithinDisplay: new AuthorizedPhysicalRectangle(-1, 0, 100, 100)));
        AssertReason("invalid_scope_geometry", () => CreateScope(
            fixtures,
            displayBounds: new AuthorizedPhysicalRectangle(int.MaxValue, 0, 10, 10),
            regionWithinDisplay: new AuthorizedPhysicalRectangle(1, 1, 1, 1)));
        AssertReason("invalid_scope_dimension", () => CreateScope(fixtures, dpiX: 0));
        AssertReason("invalid_scope_dimension", () => CreateScope(fixtures, dpiY: -1));
        AssertReason("invalid_scope_geometry", () => CreateScope(fixtures, physicalWidth: 1919));
        AssertReason("invalid_scope_enum", () => CreateScope(fixtures, orientation: (AuthorizedDisplayOrientation)999));
    }

    [Fact]
    public void UnresolvedDisplayAndUnsupportedFirstMvpPoliciesAreRejected()
    {
        var fixtures = CreateFixtures();
        AssertReason("scope_policy_not_supported", () => CreateScope(fixtures, displayIdentityStatus: AuthorizedDisplayIdentityStatus.Unresolved));
        AssertReason("scope_policy_not_supported", () => CreateScope(fixtures, targetType: AuthorizedScopeTargetType.Window));
        AssertReason("scope_policy_not_supported", () => CreateScope(fixtures, captureSemantics: AuthorizedCaptureSemantics.WindowSurface));
        AssertReason("scope_policy_not_supported", () => CreateScope(fixtures, coordinateSpace: AuthorizedCoordinateSpace.LogicalDesktop));
        AssertReason("scope_policy_not_supported", () => CreateScope(fixtures, backend: AuthorizedCaptureBackend.Wgc));
        AssertReason("scope_policy_not_supported", () => CreateScope(fixtures, audioMode: AuthorizedAudioMode.Microphone));
        AssertReason("scope_policy_not_supported", () => CreateScope(fixtures, audioMode: AuthorizedAudioMode.SystemAudio));
        AssertReason("scope_policy_not_supported", () => CreateScope(fixtures, outputConflictPolicy: AuthorizedOutputConflictPolicy.Rename));
        AssertReason("scope_policy_not_supported", () => CreateScope(fixtures, wakePolicy: AuthorizedWakePolicy.ScheduledWake));
        AssertReason("scope_policy_not_supported", () => CreateScope(fixtures, desktopRequirement: AuthorizedDesktopRequirement.AnyDesktop));
    }

    [Fact]
    public void DurationAndCountdownBoundariesAreExplicit()
    {
        var fixtures = CreateFixtures(maxDuration: TimeSpan.FromMinutes(10));
        AssertReason("positive_duration_required", () => CreateScope(fixtures, reservedDuration: TimeSpan.Zero));
        AssertReason("scope_duration_too_long", () => CreateScope(fixtures, reservedDuration: TimeSpan.FromMinutes(10) + TimeSpan.FromMilliseconds(1)));
        AssertReason("negative_quota", () => CreateScope(fixtures, countdownSeconds: -1));

        var shortLease = CreateFixtures(maxDuration: TimeSpan.FromSeconds(5));
        AssertReason("scope_duration_out_of_scope", () => CreateScope(shortLease, reservedDuration: TimeSpan.FromSeconds(6)));
        var valid = CreateScope(fixtures, reservedDuration: TimeSpan.FromMinutes(10), countdownSeconds: 0);
        Assert.Equal(TimeSpan.FromMinutes(10), valid.ReservedDuration);
        Assert.Equal(0, valid.CountdownSeconds);
    }

    [Fact]
    public void OutputSidSessionAndDigestFieldsRequireCanonicalValues()
    {
        var fixtures = CreateFixtures();
        AssertReason("invalid_output_directory", () => CreateScope(fixtures, outputDirectory: "relative-output"));
        AssertReason("invalid_scope_text", () => CreateScope(fixtures, outputDirectory: string.Empty));
        AssertReason("invalid_file_name", () => CreateScope(fixtures, frozenFileName: "nested\\capture.mp4"));
        AssertReason("invalid_scope_text", () => CreateScope(fixtures, currentUserSid: " S-1-5-21-1"));
        AssertReason("invalid_scope_text", () => CreateScope(fixtures, currentUserSid: null!));
        AssertReason("invalid_scope_text", () => CreateScope(fixtures, sessionBinding: "session/1"));
        AssertReason("invalid_scope_text", () => CreateScope(fixtures, sessionBinding: null!));
        AssertReason("invalid_scope_digest", () => CreateScope(fixtures, topologyDigest: "ABC"));
    }

    [Fact]
    public void FactoryRequiresOneTimeConsistentLifecycleAndLeaseRelations()
    {
        var fixtures = CreateFixtures(isOneTime: false);
        AssertReason("scope_plan_not_one_time", () => CreateScope(fixtures));

        var baseFixtures = CreateFixtures();
        var wrongOccurrence = new PlanOccurrence("occ-other", baseFixtures.Plan.Id, baseFixtures.Occurrence.WindowStartUtc, baseFixtures.Occurrence.WindowEndUtc, baseFixtures.Occurrence.CreatedAtUtc);
        AssertReason("scope_relation_mismatch", () => AuthorizedFixedRegionScope.CreateFor(
            baseFixtures.Plan, wrongOccurrence, baseFixtures.Lease, "scope-1", ScopeCreatedAt,
            AuthorizedScopeTargetType.FixedRegion, AuthorizedCaptureSemantics.DesktopRegion, AuthorizedCoordinateSpace.PhysicalVirtualScreen,
            AuthorizedDisplayIdentityStatus.Resolved, "display-fingerprint-1", DisplayBounds, Region, 96, 96, 1920, 1080,
            AuthorizedDisplayOrientation.Landscape, AuthorizedCaptureBackend.FfmpegRegion, AuthorizedAudioMode.None, TimeSpan.FromMinutes(1), 3,
            OutputDirectory, "capture.mp4", AuthorizedOutputConflictPolicy.FailIfExists, AuthorizedWakePolicy.NaturalWakeOnly,
            AuthorizedDesktopRequirement.InteractiveDesktopRequired, "S-1-5-21-1", "session-1", TopologyDigest));

        var rejectedLease = CreateFixtures(leaseStatus: ConsentLeaseStatus.Rejected);
        AssertReason("scope_lease_not_creatable", () => CreateScope(rejectedLease));
        var multiUseLease = CreateFixtures(maxUses: 2);
        AssertReason("scope_quota_not_supported", () => CreateScope(multiUseLease));
        var narrowLease = CreateFixtures(leaseValidUntil: WindowEnd.AddMinutes(-1));
        AssertReason("scope_lease_window_insufficient", () => CreateScope(narrowLease));
        var lateScope = CreateFixtures();
        AssertReason("scope_created_at_invalid", () => CreateScope(lateScope, createdAtUtc: lateScope.Lease.ValidUntilUtc));
    }

    private static AuthorizedFixedRegionScope Rehydrate(AuthorizedFixedRegionScope source, string digest) =>
        AuthorizedFixedRegionScope.Rehydrate(
            source.ScopeId, source.PlanId, source.OccurrenceId, source.LeaseId, source.AuthorizationVersion, source.CreatedAtUtc, digest,
            source.TargetType, source.CaptureSemantics, source.CoordinateSpace, source.DisplayIdentityStatus, source.StableDisplayFingerprint,
            source.DisplayBounds, source.RegionWithinDisplay, source.DpiX, source.DpiY, source.PhysicalWidth, source.PhysicalHeight,
            source.Orientation, source.Backend, source.AudioMode, source.ReservedDuration, source.CountdownSeconds, source.OutputDirectory,
            source.FrozenFileName, source.OutputConflictPolicy, source.WakePolicy, source.DesktopRequirement, source.CurrentUserSid,
            source.SessionBinding, source.TopologyDigest);

    private static void AssertReason(string reason, Action action)
    {
        var exception = Assert.Throws<Phase3DomainException>(action);
        Assert.Equal(reason, exception.ReasonCode);
    }

    private static AuthorizedFixedRegionScope CreateScope(
        Fixtures fixtures,
        string scopeId = "scope-1",
        DateTimeOffset? createdAtUtc = null,
        AuthorizedScopeTargetType targetType = AuthorizedScopeTargetType.FixedRegion,
        AuthorizedCaptureSemantics captureSemantics = AuthorizedCaptureSemantics.DesktopRegion,
        AuthorizedCoordinateSpace coordinateSpace = AuthorizedCoordinateSpace.PhysicalVirtualScreen,
        AuthorizedDisplayIdentityStatus displayIdentityStatus = AuthorizedDisplayIdentityStatus.Resolved,
        string stableDisplayFingerprint = "display-fingerprint-1",
        AuthorizedPhysicalRectangle? displayBounds = null,
        AuthorizedPhysicalRectangle? regionWithinDisplay = null,
        int dpiX = 96,
        int dpiY = 96,
        int physicalWidth = 1920,
        int physicalHeight = 1080,
        AuthorizedDisplayOrientation orientation = AuthorizedDisplayOrientation.Landscape,
        AuthorizedCaptureBackend backend = AuthorizedCaptureBackend.FfmpegRegion,
        AuthorizedAudioMode audioMode = AuthorizedAudioMode.None,
        TimeSpan? reservedDuration = null,
        int countdownSeconds = 3,
        string? outputDirectory = null,
        string frozenFileName = "capture.mp4",
        AuthorizedOutputConflictPolicy outputConflictPolicy = AuthorizedOutputConflictPolicy.FailIfExists,
        AuthorizedWakePolicy wakePolicy = AuthorizedWakePolicy.NaturalWakeOnly,
        AuthorizedDesktopRequirement desktopRequirement = AuthorizedDesktopRequirement.InteractiveDesktopRequired,
        string currentUserSid = "S-1-5-21-1",
        string sessionBinding = "session-1",
        string topologyDigest = TopologyDigest) =>
        AuthorizedFixedRegionScope.CreateFor(
            fixtures.Plan, fixtures.Occurrence, fixtures.Lease, scopeId, createdAtUtc ?? ScopeCreatedAt, targetType, captureSemantics,
            coordinateSpace, displayIdentityStatus, stableDisplayFingerprint, displayBounds ?? DisplayBounds,
            regionWithinDisplay ?? Region, dpiX, dpiY, physicalWidth, physicalHeight, orientation, backend, audioMode,
            reservedDuration ?? TimeSpan.FromMinutes(1), countdownSeconds, outputDirectory ?? OutputDirectory, frozenFileName,
            outputConflictPolicy, wakePolicy, desktopRequirement, currentUserSid, sessionBinding, topologyDigest);

    private static Fixtures CreateFixtures(
        bool isOneTime = true,
        ConsentLeaseStatus leaseStatus = ConsentLeaseStatus.Pending,
        int maxUses = 1,
        TimeSpan? maxDuration = null,
        DateTimeOffset? leaseValidFrom = null,
        DateTimeOffset? leaseValidUntil = null)
    {
        var plan = new PlanDefinition("plan-1", isOneTime, PlanCreatedAt);
        var occurrence = PlanOccurrence.CreateFor(plan, "occ-1", WindowStart, WindowEnd, OccurrenceCreatedAt);
        var lease = ConsentLease.CreateFor(
            plan, occurrence, leaseValidFrom ?? LeaseValidFrom, leaseValidUntil ?? LeaseValidUntil, maxUses, maxDuration ?? TimeSpan.FromMinutes(5), "lease-1");
        if (leaseStatus != ConsentLeaseStatus.Pending)
        {
            var result = lease.TryTransition(leaseStatus, lease.ValidFromUtc);
            Assert.True(result.Succeeded);
        }

        return new Fixtures(plan, occurrence, lease);
    }

    private static readonly DateTimeOffset PlanCreatedAt = new(2030, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset OccurrenceCreatedAt = new(2030, 1, 2, 8, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset WindowStart = new(2030, 1, 2, 10, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset WindowEnd = new(2030, 1, 2, 11, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset LeaseValidFrom = new(2030, 1, 2, 9, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset LeaseValidUntil = new(2030, 1, 2, 12, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset ScopeCreatedAt = new(2030, 1, 2, 9, 30, 0, TimeSpan.Zero);
    private static readonly AuthorizedPhysicalRectangle DisplayBounds = new(100, -50, 1920, 1080);
    private static readonly AuthorizedPhysicalRectangle Region = new(10, 200, 640, 480);
    private static readonly string OutputDirectory = Path.Combine(Path.GetTempPath(), "AgentRecorderAuthorizedScopes");
    private const string TopologyDigest = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";

    private sealed record Fixtures(PlanDefinition Plan, PlanOccurrence Occurrence, ConsentLease Lease);
}
