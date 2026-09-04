using AgentRecorder.Core.Automation;
using AgentRecorder.Infrastructure;
using AgentRecorder.Persistence;
using Microsoft.Data.Sqlite;
using Xunit;

namespace AgentRecorder.Tests;

[Collection("NonParallel-AgentRecorderDataDir")]
public sealed class AuthorizedCaptureScopeRepositoryTests
{
    [Fact]
    public void RoundTripUsesTypedSnapshotAndValidatesAllRelatedAggregatesInOneReadTransaction()
    {
        using var fixture = new Fixture();
        fixture.Repository.Insert(fixture.Scope);

        var byId = fixture.Repository.GetById(fixture.Scope.ScopeId);
        var byLease = fixture.Repository.GetByLeaseId(fixture.Scope.LeaseId);
        var byOccurrence = fixture.Repository.GetByOccurrence(fixture.Scope.OccurrenceId);

        Assert.Equal(fixture.Scope.ScopeDigest, byId.ScopeDigest);
        Assert.Equal(fixture.Scope.VirtualScreenRegion, byId.VirtualScreenRegion);
        Assert.Equal(fixture.Scope.ScopeDigest, byLease.ScopeDigest);
        Assert.Equal(fixture.Scope.ScopeDigest, byOccurrence.ScopeDigest);
    }

    [Fact]
    public void MissingScopeIsNotFound()
    {
        using var fixture = new Fixture();

        var exception = Assert.Throws<Phase3PersistenceException>(() => fixture.Repository.Get("missing-scope"));
        var leaseException = Assert.Throws<Phase3PersistenceException>(() => fixture.Repository.GetByLeaseId("lease-1"));
        var occurrenceException = Assert.Throws<Phase3PersistenceException>(() => fixture.Repository.GetByOccurrence("occ-1"));

        Assert.Equal("not_found", exception.Code);
        Assert.Equal("not_found", leaseException.Code);
        Assert.Equal("not_found", occurrenceException.Code);
    }

    [Fact]
    public void ExactDuplicateInsertIsIdempotentAndDoesNotCreateAnotherRow()
    {
        using var fixture = new Fixture();
        fixture.Repository.Insert(fixture.Scope);

        fixture.Repository.Insert(fixture.Scope);

        using var connection = fixture.OpenRawConnection();
        Assert.Equal(1L, ScalarInt64(connection, "SELECT COUNT(*) FROM authorized_capture_scopes;"));
    }

    [Fact]
    public void DuplicateScopeOrLeaseWithDifferentImmutableContentsIsRejected()
    {
        using var fixture = new Fixture();
        fixture.Repository.Insert(fixture.Scope);

        var sameScopeDifferentRegion = fixture.CreateScope(regionX: 11);
        var sameScopeException = Assert.Throws<Phase3PersistenceException>(() => fixture.Repository.Insert(sameScopeDifferentRegion));
        Assert.Equal("immutable_mismatch", sameScopeException.Code);

        var sameLeaseDifferentScope = fixture.CreateScope(scopeId: "scope-other", regionX: 12);
        var sameLeaseException = Assert.Throws<Phase3PersistenceException>(() => fixture.Repository.Insert(sameLeaseDifferentScope));
        Assert.Equal("immutable_mismatch", sameLeaseException.Code);
    }

    [Fact]
    public void RawFieldChangeWithoutMatchingDigestIsPersistedSnapshotInvalid()
    {
        using var fixture = new Fixture();
        fixture.Repository.Insert(fixture.Scope);
        using (var connection = fixture.OpenRawConnection())
        {
            Execute(connection, "UPDATE authorized_capture_scopes SET region_x = 11 WHERE scope_id = 'scope-1';");
        }

        var exception = Assert.Throws<Phase3PersistenceException>(() => fixture.Repository.Get(fixture.Scope.ScopeId));

        Assert.Equal("persisted_snapshot_invalid", exception.Code);
    }

    [Fact]
    public void RawFieldChangeWithAnArbitraryForgedDigestIsStillInvalid()
    {
        using var fixture = new Fixture();
        fixture.Repository.Insert(fixture.Scope);
        using (var connection = fixture.OpenRawConnection())
        {
            Execute(connection, "UPDATE authorized_capture_scopes SET region_x = 12, scope_digest = 'ffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff' WHERE scope_id = 'scope-1';");
        }

        var exception = Assert.Throws<Phase3PersistenceException>(() => fixture.Repository.Get(fixture.Scope.ScopeId));

        Assert.Equal("persisted_snapshot_invalid", exception.Code);
    }

    [Fact]
    public void RawRelatedAggregateChangeIsPersistedSnapshotInvalid()
    {
        using var fixture = new Fixture();
        fixture.Repository.Insert(fixture.Scope);
        using (var connection = fixture.OpenRawConnection())
        {
            Execute(connection, "UPDATE plans SET is_one_time = 0 WHERE id = 'plan-1';");
        }

        var exception = Assert.Throws<Phase3PersistenceException>(() => fixture.Repository.Get(fixture.Scope.ScopeId));

        Assert.Equal("persisted_snapshot_invalid", exception.Code);
    }

    [Fact]
    public void MissingV1LeaseRelationIsRejectedAsAPersistedInvalidSnapshot()
    {
        using var fixture = new Fixture();
        fixture.Repository.Insert(fixture.Scope);
        using (var connection = fixture.OpenRawConnection())
        {
            Execute(connection, "PRAGMA foreign_keys = OFF;");
            Execute(connection, "DELETE FROM consent_leases WHERE id = 'lease-1';");
        }

        var exception = Assert.Throws<Phase3PersistenceException>(() => fixture.Repository.Get(fixture.Scope.ScopeId));

        Assert.Equal("persisted_snapshot_invalid", exception.Code);
    }

    [Fact]
    public void InconsistentNewScopeRelationsAreRejectedBeforeInsert()
    {
        using var fixture = new Fixture();
        var differentPlan = new PlanDefinition("plan-other", true, Fixture.PlanCreatedAt);
        var differentOccurrence = PlanOccurrence.CreateFor(differentPlan, "occ-other", Fixture.WindowStart, Fixture.WindowEnd, Fixture.OccurrenceCreatedAt);
        var differentLease = ConsentLease.CreateFor(differentPlan, differentOccurrence, Fixture.LeaseValidFrom, Fixture.LeaseValidUntil, 1, TimeSpan.FromMinutes(5), "lease-other");
        var invalidScope = AuthorizedFixedRegionScope.CreateFor(
            differentPlan, differentOccurrence, differentLease, "scope-invalid", Fixture.ScopeCreatedAt,
            AuthorizedScopeTargetType.FixedRegion, AuthorizedCaptureSemantics.DesktopRegion, AuthorizedCoordinateSpace.PhysicalVirtualScreen,
            AuthorizedDisplayIdentityStatus.Resolved, Fixture.DisplayFingerprint, Fixture.DisplayBounds, Fixture.Region, 96, 96, 1920, 1080,
            AuthorizedDisplayOrientation.Landscape, AuthorizedCaptureBackend.FfmpegRegion, AuthorizedAudioMode.None, TimeSpan.FromMinutes(1), 3,
            Fixture.OutputDirectory, "capture.mp4", AuthorizedOutputConflictPolicy.FailIfExists, AuthorizedWakePolicy.NaturalWakeOnly,
            AuthorizedDesktopRequirement.InteractiveDesktopRequired, Fixture.UserSid, Fixture.SessionBinding, Fixture.TopologyDigest);

        var exception = Assert.Throws<Phase3PersistenceException>(() => fixture.Repository.Insert(invalidScope));

        Assert.Equal("invalid_argument", exception.Code);
    }

    private static void Execute(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private static long ScalarInt64(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt64(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
    }

    private sealed class Fixture : IDisposable
    {
        public Fixture()
        {
            RootPath = Path.Combine(Path.GetTempPath(), "AgentRecorderAuthorizedScopeRepository_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(RootPath);
            Store = new SqliteOperationalStore(Path.Combine(RootPath, "state", SqliteOperationalStore.DatabaseFileName));
            Store.Initialize();

            Plan = new PlanDefinition("plan-1", true, PlanCreatedAt);
            Occurrence = PlanOccurrence.CreateFor(Plan, "occ-1", WindowStart, WindowEnd, OccurrenceCreatedAt);
            Lease = ConsentLease.CreateFor(Plan, Occurrence, LeaseValidFrom, LeaseValidUntil, 1, TimeSpan.FromMinutes(5), "lease-1");
            new SqlitePlanDefinitionRepository(Store).Insert(Plan);
            new SqlitePlanOccurrenceRepository(Store).Insert(Occurrence);
            new SqliteConsentLeaseRepository(Store).Insert(Lease);
            Repository = new SqliteAuthorizedCaptureScopeRepository(Store);
            Scope = CreateScope();
        }

        public static readonly DateTimeOffset PlanCreatedAt = new(2030, 1, 1, 0, 0, 0, TimeSpan.Zero);
        public static readonly DateTimeOffset OccurrenceCreatedAt = new(2030, 1, 2, 8, 0, 0, TimeSpan.Zero);
        public static readonly DateTimeOffset WindowStart = new(2030, 1, 2, 10, 0, 0, TimeSpan.Zero);
        public static readonly DateTimeOffset WindowEnd = new(2030, 1, 2, 11, 0, 0, TimeSpan.Zero);
        public static readonly DateTimeOffset LeaseValidFrom = new(2030, 1, 2, 9, 0, 0, TimeSpan.Zero);
        public static readonly DateTimeOffset LeaseValidUntil = new(2030, 1, 2, 12, 0, 0, TimeSpan.Zero);
        public static readonly DateTimeOffset ScopeCreatedAt = new(2030, 1, 2, 9, 30, 0, TimeSpan.Zero);
        public static readonly AuthorizedPhysicalRectangle DisplayBounds = new(100, -50, 1920, 1080);
        public static readonly AuthorizedPhysicalRectangle Region = new(10, 200, 640, 480);
        public static readonly string OutputDirectory = Path.Combine(Path.GetTempPath(), "AgentRecorderAuthorizedScopes");
        public const string DisplayFingerprint = "display-fingerprint-1";
        public const string UserSid = "S-1-5-21-1";
        public const string SessionBinding = "session-1";
        public const string TopologyDigest = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";

        public string RootPath { get; }
        public SqliteOperationalStore Store { get; }
        public PlanDefinition Plan { get; }
        public PlanOccurrence Occurrence { get; }
        public ConsentLease Lease { get; }
        public SqliteAuthorizedCaptureScopeRepository Repository { get; }
        public AuthorizedFixedRegionScope Scope { get; }

        public AuthorizedFixedRegionScope CreateScope(string scopeId = "scope-1", int regionX = 10) =>
            AuthorizedFixedRegionScope.CreateFor(
                Plan, Occurrence, Lease, scopeId, ScopeCreatedAt,
                AuthorizedScopeTargetType.FixedRegion, AuthorizedCaptureSemantics.DesktopRegion, AuthorizedCoordinateSpace.PhysicalVirtualScreen,
                AuthorizedDisplayIdentityStatus.Resolved, DisplayFingerprint, DisplayBounds, new AuthorizedPhysicalRectangle(regionX, 200, 640, 480),
                96, 96, 1920, 1080, AuthorizedDisplayOrientation.Landscape, AuthorizedCaptureBackend.FfmpegRegion, AuthorizedAudioMode.None,
                TimeSpan.FromMinutes(1), 3, OutputDirectory, "capture.mp4", AuthorizedOutputConflictPolicy.FailIfExists,
                AuthorizedWakePolicy.NaturalWakeOnly, AuthorizedDesktopRequirement.InteractiveDesktopRequired, UserSid, SessionBinding, TopologyDigest);

        public SqliteConnection OpenRawConnection()
        {
            var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = Store.DatabasePath,
                Mode = SqliteOpenMode.ReadWrite,
                Cache = SqliteCacheMode.Default,
                Pooling = false,
            }.ToString());
            connection.Open();
            return connection;
        }

        public void Dispose()
        {
            try
            {
                Directory.Delete(RootPath, recursive: true);
            }
            catch (DirectoryNotFoundException)
            {
            }
        }
    }
}
