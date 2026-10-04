using System.Net;
using System.Text;
using System.Text.Json;
using AgentRecorder.Api;
using AgentRecorder.App;
using AgentRecorder.Core;
using AgentRecorder.Core.Automation;
using AgentRecorder.Headless;
using AgentRecorder.Infrastructure;
using AgentRecorder.Logging;
using AgentRecorder.Persistence;
using Xunit;

namespace AgentRecorder.Tests;

[Collection("NonParallel-AgentRecorderDataDir")]
public sealed class CodexProfileManagementRegressionTests : IDisposable
{
    private readonly TempDirectory _temp = new();
    private readonly SqliteFixedRegionProfileManagementRepository _repository;
    private readonly SqliteRecurringFixedRegionProfileRepository _versionRepository;
    private readonly SqliteFixedRegionProfileManagementGateway _gateway;
    private readonly RecurringFixedRegionProfileVersion _source;
    private readonly string? _previousOverride;
    private ApiServer? _server;

    public CodexProfileManagementRegressionTests()
    {
        _previousOverride = DataDirResolver.HasOverride ? DataDirResolver.Resolve() : null;
        DataDirResolver.SetOverride(_temp.Path);
        var store = new SqliteOperationalStore(Path.Combine(_temp.Path, "state.db"));
        store.Initialize();
        _repository = new SqliteFixedRegionProfileManagementRepository(store);
        _gateway = new SqliteFixedRegionProfileManagementGateway(store);
        _versionRepository = new SqliteRecurringFixedRegionProfileRepository(store);
        _source = _versionRepository.CreateVersion1(
            "codex-profile", DateTimeOffset.UtcNow, new RecurringFixedRegionProfileSpecification(
                AuthorizedScopeTargetType.FixedRegion, RecurringFixedRegionRebindPolicy.ExactMatchOnly,
                AuthorizedCaptureSemantics.DesktopRegion, AuthorizedCoordinateSpace.PhysicalVirtualScreen,
                AuthorizedDisplayIdentityStatus.Resolved, "DISPLAY-FP-CODEX", new(-100, 50, 1920, 1080),
                new(10, 20, 640, 480), 96, 144, 1920, 1080, AuthorizedDisplayOrientation.LandscapeFlipped,
                new string('a', 64), AuthorizedCaptureBackend.FfmpegRegion, AuthorizedAudioMode.None,
                TimeSpan.FromMilliseconds(1500), 3, Path.Combine(_temp.Path, "never-created-output"),
                "demo", AuthorizedOutputConflictPolicy.FailIfExists, AuthorizedWakePolicy.NaturalWakeOnly,
                AuthorizedDesktopRequirement.InteractiveDesktopRequired));
        ApiKeyAuth.InitializeForTesting(_temp.Path);
    }

    [Fact]
    public async Task VersionPageAndCurrentMetadataComeFromOneSnapshot()
    {
        var interleaved = new InterleavingGateway(_gateway) { AfterVersionPageSnapshot = PatchToVersionTwo };
        using var client = Start(interleaved);
        using var response = await client.GetAsync($"/api/v1/profiles/{_source.ProfileId}/versions?limit=20");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var items = json.RootElement.GetProperty("data").GetProperty("versions");
        // Without a cursor, the first entry must be current in the same read snapshot.
        Assert.True(items[0].GetProperty("is_current").GetBoolean());
        Assert.Equal(2, _repository.Get(_source.ProfileId)!.CurrentVersion.ProfileVersion);
    }

    [Fact]
    public async Task ExactVersionCannotAppearBetweenDirectoryAndVersionReadsAsNonCurrent()
    {
        var interleaved = new InterleavingGateway(_gateway) { AfterExactVersionSnapshot = PatchToVersionTwo };
        using var client = Start(interleaved);
        using var response = await client.GetAsync($"/api/v1/profiles/{_source.ProfileId}/versions/2");
        Assert.Contains(response.StatusCode, new[] { HttpStatusCode.OK, HttpStatusCode.NotFound });
        if (response.StatusCode == HttpStatusCode.OK)
        {
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            var version = json.RootElement.GetProperty("data").GetProperty("version");
            Assert.Equal(2, version.GetProperty("profile_ref").GetProperty("version").GetInt64());
            Assert.True(version.GetProperty("is_current").GetBoolean());
        }
    }

    [Theory]
    [InlineData("coordinate_space", "physical_virtual_screen")]
    [InlineData("orientation", "landscape_flipped")]
    [InlineData("wake_policy", "natural_wake_only")]
    [InlineData("desktop_requirement", "interactive_desktop_required")]
    [InlineData("conflict_policy", "fail_if_exists")]
    public async Task PublicSpecificationUsesEstablishedCanonicalPolicyCodes(string field, string expected)
    {
        using var client = Start(_gateway);
        using var response = await client.GetAsync($"/api/v1/profiles/{_source.ProfileId}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var specification = json.RootElement.GetProperty("data").GetProperty("profile").GetProperty("specification");
        var parent = field switch
        {
            "coordinate_space" or "orientation" => specification.GetProperty("target_policy"),
            "conflict_policy" => specification.GetProperty("output"),
            _ => specification
        };
        Assert.Equal(expected, parent.GetProperty(field).GetString());
    }

    [Fact]
    public async Task ExistingMillisecondDurationIsNotSilentlyTruncatedByTheReadApi()
    {
        using var client = Start(_gateway);
        using var response = await client.GetAsync($"/api/v1/profiles/{_source.ProfileId}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var specification = json.RootElement.GetProperty("data").GetProperty("profile").GetProperty("specification");
        Assert.Equal(1500, specification.GetProperty("duration_ms").GetInt64());
        Assert.Equal(1.5m, specification.GetProperty("duration_seconds").GetDecimal());
        AssertCanonicalSpecification(specification);
    }

    [Fact]
    public async Task CurrentHistoryPageListAndCopyExposeCanonicalPoliciesAndExactDurations()
    {
        var halfSecond = CreateDurationProfile("codex-profile-500ms", TimeSpan.FromMilliseconds(500));
        var oneSecond = CreateDurationProfile("codex-profile-1s", TimeSpan.FromSeconds(1));
        var upperBound = CreateDurationProfile("codex-profile-600s", TimeSpan.FromSeconds(600));
        var profiles = new[] { _source, halfSecond, oneSecond, upperBound };
        using var client = Start(_gateway);

        using var list = await client.GetAsync("/api/v1/profiles?limit=100");
        Assert.Equal(HttpStatusCode.OK, list.StatusCode);
        using var listJson = JsonDocument.Parse(await list.Content.ReadAsStringAsync());
        var listItems = listJson.RootElement.GetProperty("data").GetProperty("profiles");
        foreach (var profile in profiles)
        {
            var listed = Assert.Single(listItems.EnumerateArray(), item =>
                item.GetProperty("profile_ref").GetProperty("id").GetString() == profile.ProfileId);
            AssertReadSpecification(listed.GetProperty("specification"), profile.Duration);
        }

        foreach (var profile in profiles)
        {
            using var detail = await client.GetAsync($"/api/v1/profiles/{profile.ProfileId}");
            Assert.Equal(HttpStatusCode.OK, detail.StatusCode);
            using var detailJson = JsonDocument.Parse(await detail.Content.ReadAsStringAsync());
            AssertReadSpecification(detailJson.RootElement.GetProperty("data").GetProperty("profile")
                .GetProperty("specification"), profile.Duration);

            using var page = await client.GetAsync($"/api/v1/profiles/{profile.ProfileId}/versions?limit=10");
            Assert.Equal(HttpStatusCode.OK, page.StatusCode);
            using var pageJson = JsonDocument.Parse(await page.Content.ReadAsStringAsync());
            var entry = Assert.Single(pageJson.RootElement.GetProperty("data").GetProperty("versions").EnumerateArray());
            Assert.True(entry.GetProperty("is_current").GetBoolean());
            AssertReadSpecification(entry.GetProperty("specification"), profile.Duration);

            using var exact = await client.GetAsync($"/api/v1/profiles/{profile.ProfileId}/versions/1");
            Assert.Equal(HttpStatusCode.OK, exact.StatusCode);
            using var exactJson = JsonDocument.Parse(await exact.Content.ReadAsStringAsync());
            AssertReadSpecification(exactJson.RootElement.GetProperty("data").GetProperty("version")
                .GetProperty("specification"), profile.Duration);

            var sourceDigest = profile.ProfileDigest;
            using var copyRequest = new StringContent(JsonSerializer.Serialize(new
            {
                name = "copy-" + profile.ProfileId,
                source_profile_ref = new { id = profile.ProfileId, version = profile.ProfileVersion, digest = sourceDigest }
            }), Encoding.UTF8, "application/json");
            using var message = new HttpRequestMessage(HttpMethod.Post, "/api/v1/profiles") { Content = copyRequest };
            message.Headers.Add("Idempotency-Key", "copy-" + profile.ProfileId);
            using var copyResponse = await client.SendAsync(message);
            Assert.Equal(HttpStatusCode.Created, copyResponse.StatusCode);
            using var copyJson = JsonDocument.Parse(await copyResponse.Content.ReadAsStringAsync());
            var copy = copyJson.RootElement.GetProperty("data").GetProperty("profile");
            AssertReadSpecification(copy.GetProperty("specification"), profile.Duration);
            Assert.Equal(sourceDigest, profile.ProfileDigest);
            Assert.Equal(1, copy.GetProperty("profile_ref").GetProperty("version").GetInt64());
        }

        var originalDigest = _source.ProfileDigest;
        var current = _repository.Get(_source.ProfileId)!;
        _repository.Patch(_source.ProfileId, FixedRegionProfileETag.Compute(current), null,
            new FixedRegionProfileChanges(DurationSeconds: 30), DateTimeOffset.UtcNow);
        using (var historicalPage = await client.GetAsync($"/api/v1/profiles/{_source.ProfileId}/versions?limit=10"))
        {
            Assert.Equal(HttpStatusCode.OK, historicalPage.StatusCode);
            using var historyJson = JsonDocument.Parse(await historicalPage.Content.ReadAsStringAsync());
            var versions = historyJson.RootElement.GetProperty("data").GetProperty("versions");
            Assert.Equal(2, versions.GetArrayLength());
            Assert.True(versions[0].GetProperty("is_current").GetBoolean());
            Assert.False(versions[1].GetProperty("is_current").GetBoolean());
            AssertReadSpecification(versions[1].GetProperty("specification"), TimeSpan.FromMilliseconds(1500));
        }
        using (var historicalExact = await client.GetAsync($"/api/v1/profiles/{_source.ProfileId}/versions/1"))
        {
            Assert.Equal(HttpStatusCode.OK, historicalExact.StatusCode);
            using var historyJson = JsonDocument.Parse(await historicalExact.Content.ReadAsStringAsync());
            var version = historyJson.RootElement.GetProperty("data").GetProperty("version");
            Assert.False(version.GetProperty("is_current").GetBoolean());
            AssertReadSpecification(version.GetProperty("specification"), TimeSpan.FromMilliseconds(1500));
        }
        using var historicalCopyBody = new StringContent(JsonSerializer.Serialize(new
        {
            name = "copy-historical-1500ms",
            source_profile_ref = new { id = _source.ProfileId, version = 1, digest = originalDigest }
        }), Encoding.UTF8, "application/json");
        using var historicalCopyRequest = new HttpRequestMessage(HttpMethod.Post, "/api/v1/profiles")
        {
            Content = historicalCopyBody
        };
        historicalCopyRequest.Headers.Add("Idempotency-Key", "copy-historical-1500ms");
        using var historicalCopyResponse = await client.SendAsync(historicalCopyRequest);
        Assert.Equal(HttpStatusCode.Created, historicalCopyResponse.StatusCode);
        using var historicalCopyJson = JsonDocument.Parse(await historicalCopyResponse.Content.ReadAsStringAsync());
        AssertReadSpecification(historicalCopyJson.RootElement.GetProperty("data").GetProperty("profile")
            .GetProperty("specification"), TimeSpan.FromMilliseconds(1500));
        Assert.Equal(originalDigest, _versionRepository.Get(_source.ProfileId, 1).ProfileDigest);
    }

    [Fact]
    public async Task HistoricalReadSnapshotKeepsNameAndDeleteStateWithItsEtagAcrossConcurrentWrites()
    {
        var patchGateway = new InterleavingGateway(_gateway)
        {
            AfterVersionPageSnapshot = () =>
            {
                var current = _repository.Get(_source.ProfileId)!;
                _repository.Patch(_source.ProfileId, FixedRegionProfileETag.Compute(current), "renamed concurrently",
                    new FixedRegionProfileChanges(), DateTimeOffset.UtcNow);
            }
        };
        using (var client = Start(patchGateway))
        using (var response = await client.GetAsync($"/api/v1/profiles/{_source.ProfileId}/versions?limit=20"))
        {
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            var data = json.RootElement.GetProperty("data");
            Assert.False(data.GetProperty("is_deleted").GetBoolean());
            Assert.Equal("Profile codex-profile", data.GetProperty("versions")[0].GetProperty("name").GetString());
            Assert.Equal(FixedRegionProfileETag.Compute(_source.ProfileId, "Profile codex-profile", 1,
                _source.ProfileDigest, false), response.Headers.GetValues("ETag").Single());
        }

        var deleteGateway = new InterleavingGateway(_gateway)
        {
            AfterVersionPageSnapshot = () =>
            {
                var current = _repository.Get(_source.ProfileId)!;
                _repository.Patch(_source.ProfileId, FixedRegionProfileETag.Compute(current), null,
                    new FixedRegionProfileChanges(DurationSeconds: 10), DateTimeOffset.UtcNow);
                current = _repository.Get(_source.ProfileId)!;
                _repository.Delete(_source.ProfileId, FixedRegionProfileETag.Compute(current), DateTimeOffset.UtcNow);
            }
        };
        using var deletedClient = Start(deleteGateway);
        using var deletedResponse = await deletedClient.GetAsync($"/api/v1/profiles/{_source.ProfileId}/versions?limit=20");
        Assert.Equal(HttpStatusCode.OK, deletedResponse.StatusCode);
        using var deletedJson = JsonDocument.Parse(await deletedResponse.Content.ReadAsStringAsync());
        var deletedData = deletedJson.RootElement.GetProperty("data");
        Assert.False(deletedData.GetProperty("is_deleted").GetBoolean());
        Assert.All(deletedData.GetProperty("versions").EnumerateArray(), item =>
        {
            Assert.Equal("renamed concurrently", item.GetProperty("name").GetString());
            Assert.False(item.GetProperty("is_deleted").GetBoolean());
        });
        Assert.True(_repository.Get(_source.ProfileId)!.IsDeleted);
    }

    private void PatchToVersionTwo()
    {
        var current = _repository.Get(_source.ProfileId)!;
        _repository.Patch(_source.ProfileId, FixedRegionProfileETag.Compute(current), "version two",
            new FixedRegionProfileChanges(DurationSeconds: 20), DateTimeOffset.UtcNow);
    }

    private RecurringFixedRegionProfileVersion CreateDurationProfile(string id, TimeSpan duration) =>
        _versionRepository.CreateVersion1(id, DateTimeOffset.UtcNow, new RecurringFixedRegionProfileSpecification(
            AuthorizedScopeTargetType.FixedRegion, RecurringFixedRegionRebindPolicy.ExactMatchOnly,
            AuthorizedCaptureSemantics.DesktopRegion, AuthorizedCoordinateSpace.PhysicalVirtualScreen,
            AuthorizedDisplayIdentityStatus.Resolved, "DISPLAY-FP-CODEX", new(-100, 50, 1920, 1080),
            new(10, 20, 640, 480), 96, 144, 1920, 1080, AuthorizedDisplayOrientation.LandscapeFlipped,
            new string('a', 64), AuthorizedCaptureBackend.FfmpegRegion, AuthorizedAudioMode.None,
            duration, 3, Path.Combine(_temp.Path, "never-created-output"),
            "demo", AuthorizedOutputConflictPolicy.FailIfExists, AuthorizedWakePolicy.NaturalWakeOnly,
            AuthorizedDesktopRequirement.InteractiveDesktopRequired));

    private static void AssertReadSpecification(JsonElement specification, TimeSpan duration)
    {
        Assert.Equal(duration.Ticks / (decimal)TimeSpan.TicksPerSecond,
            specification.GetProperty("duration_seconds").GetDecimal());
        Assert.Equal(duration.Ticks / TimeSpan.TicksPerMillisecond,
            specification.GetProperty("duration_ms").GetInt64());
        AssertCanonicalSpecification(specification);
    }

    private static void AssertCanonicalSpecification(JsonElement specification)
    {
        var target = specification.GetProperty("target_policy");
        Assert.Equal("fixed_region", target.GetProperty("type").GetString());
        Assert.Equal("exact_match_only", target.GetProperty("rebind_policy").GetString());
        Assert.Equal("physical_virtual_screen", target.GetProperty("coordinate_space").GetString());
        Assert.Equal("resolved", target.GetProperty("display_identity_status").GetString());
        Assert.Equal("landscape_flipped", target.GetProperty("orientation").GetString());
        Assert.Equal("desktop_region", specification.GetProperty("capture").GetProperty("semantics").GetString());
        Assert.Equal("ffmpeg-region", specification.GetProperty("capture").GetProperty("backend").GetString());
        Assert.Equal("none", specification.GetProperty("audio").GetProperty("mode").GetString());
        Assert.Equal("fail_if_exists", specification.GetProperty("output").GetProperty("conflict_policy").GetString());
        Assert.Equal("natural_wake_only", specification.GetProperty("wake_policy").GetString());
        Assert.Equal("interactive_desktop_required", specification.GetProperty("desktop_requirement").GetString());
    }

    private HttpClient Start(IFixedRegionProfileManagementGateway gateway)
    {
        _server?.Stop();
        var audit = new AuditLogger(Path.Combine(_temp.Path, "audit.jsonl"));
        var engine = new RecordingEngine(audit);
        var tray = new HeadlessTrayContext(audit);
        engine.SetTray(tray);
        _server = new ApiServer(engine, audit, tray, null, null, null, null, null, null, null, null, null, 0,
            profileManagementGateway: gateway);
        _server.Start();
        var client = new HttpClient(new HttpClientHandler { UseProxy = false })
        {
            BaseAddress = new Uri($"http://127.0.0.1:{_server.BoundPort}"), Timeout = TimeSpan.FromSeconds(10)
        };
        client.DefaultRequestHeaders.Add("X-Agent-Recorder-Key", ApiKeyAuth.CurrentApiKey);
        return client;
    }

    public void Dispose()
    {
        _server?.Stop();
        ApiKeyAuth.ResetForTesting(null);
        DataDirResolver.SetOverride(_previousOverride);
        _temp.Dispose();
    }

    // Mutate real SQLite after reading the snapshot, before HTTP serialization.
    private sealed class InterleavingGateway(IFixedRegionProfileManagementGateway inner)
        : IFixedRegionProfileManagementGateway
    {
        public Action? AfterVersionPageSnapshot { get; init; }
        public Action? AfterExactVersionSnapshot { get; init; }
        public FixedRegionProfileCreateResult CreateFromExisting(string key, string hash, ProfileRef source,
            string name, FixedRegionProfileChanges changes, DateTimeOffset now) =>
            inner.CreateFromExisting(key, hash, source, name, changes, now);
        public FixedRegionProfileDirectoryPage List(int limit, string? after, bool deleted) =>
            inner.List(limit, after, deleted);
        public FixedRegionProfileDirectoryRecord? Get(string id) => inner.Get(id);
        public FixedRegionProfileVersionPageSnapshot? ReadVersionPageSnapshot(string id, int limit, long? before)
        {
            var result = inner.ReadVersionPageSnapshot(id, limit, before);
            AfterVersionPageSnapshot?.Invoke();
            return result;
        }
        public FixedRegionProfileExactVersionSnapshot? ReadExactVersionSnapshot(string id, long version)
        {
            var result = inner.ReadExactVersionSnapshot(id, version);
            AfterExactVersionSnapshot?.Invoke();
            return result;
        }
        public FixedRegionProfileDirectoryRecord Patch(string id, string tag, string? name,
            FixedRegionProfileChanges changes, DateTimeOffset now) => inner.Patch(id, tag, name, changes, now);
        public void Delete(string id, string tag, DateTimeOffset now) => inner.Delete(id, tag, now);
    }
}
