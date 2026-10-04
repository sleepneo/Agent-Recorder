using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using AgentRecorder.Api;
using AgentRecorder.App;
using AgentRecorder.Core;
using AgentRecorder.Core.Automation;
using AgentRecorder.Capture;
using AgentRecorder.Headless;
using AgentRecorder.Infrastructure;
using AgentRecorder.Logging;
using AgentRecorder.Persistence;
using AgentRecorder.Windows;
using Xunit;

namespace AgentRecorder.Tests;

[Collection("NonParallel-AgentRecorderDataDir")]
public sealed class FixedRegionProfileManagementApiTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "AgentRecorderTask305_" + Guid.NewGuid().ToString("N"));
    private readonly SqliteOperationalStore _store;
    private ApiServer? _server;

    public FixedRegionProfileManagementApiTests()
    {
        Directory.CreateDirectory(_root);
        _store = new SqliteOperationalStore(Path.Combine(_root, "state.db"));
        _store.Initialize();
        ApiKeyAuth.InitializeForTesting(_root);
    }

    [Fact]
    public async Task RealHttpAndSqliteCopyPatchRestartAndDeleteAreVersionedAndIdempotent()
    {
        var source = new SqliteRecurringFixedRegionProfileRepository(_store).CreateVersion1(
            RecurringFixedRegionProfileVersion.CreateVersion1("legacy-source", DateTimeOffset.UtcNow, Spec()));
        StartServer();
        using var client = Client(authenticated: true);

        using var listed = await client.GetAsync("/api/v1/profiles?limit=1");
        Assert.Equal(HttpStatusCode.OK, listed.StatusCode);
        using var listJson = JsonDocument.Parse(await listed.Content.ReadAsStringAsync());
        Assert.Equal("legacy-source", listJson.RootElement.GetProperty("data").GetProperty("profiles")[0]
            .GetProperty("profile_ref").GetProperty("id").GetString());
        var payload = CreateBody("课件演示", source.Reference.ProfileId, source.ProfileDigest);
        using var created = await PostCreate(client, payload, "copy-key");
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        Assert.Equal(created.Headers.ETag!.Tag, ParseData(created).GetProperty("etag").GetString());
        using var createdJson = JsonDocument.Parse(await created.Content.ReadAsStringAsync());
        var copied = createdJson.RootElement.GetProperty("data").GetProperty("profile");
        var profileId = copied.GetProperty("profile_ref").GetProperty("id").GetString()!;
        Assert.Equal(1, copied.GetProperty("profile_ref").GetProperty("version").GetInt64());
        var initialEtag = created.Headers.ETag.Tag;

        using var replay = await PostCreate(client, payload, "copy-key");
        Assert.Equal(HttpStatusCode.Created, replay.StatusCode);
        Assert.Equal(initialEtag, replay.Headers.ETag!.Tag);
        Assert.True(ParseData(replay).GetProperty("idempotent_replay").GetBoolean());
        using var collision = await PostCreate(client, CreateBody("different", source.Reference.ProfileId, source.ProfileDigest), "copy-key");
        Assert.Equal(HttpStatusCode.Conflict, collision.StatusCode);

        using var detail = await client.GetAsync($"/api/v1/profiles/{profileId}");
        Assert.Equal(initialEtag, detail.Headers.ETag!.Tag);
        using var patch = await Patch(client, profileId, initialEtag,
            "{\"name\":\"演示更新\",\"duration_seconds\":45,\"countdown_seconds\":0,\"output_directory\":\"D:\\\\safe\\\\Videos\",\"filename_prefix\":\"lesson-1\"}");
        Assert.Equal(HttpStatusCode.OK, patch.StatusCode);
        using var patchJson = JsonDocument.Parse(await patch.Content.ReadAsStringAsync());
        var updated = patchJson.RootElement.GetProperty("data").GetProperty("profile");
        Assert.Equal(2, updated.GetProperty("profile_ref").GetProperty("version").GetInt64());
        Assert.Equal(patch.Headers.ETag!.Tag, patchJson.RootElement.GetProperty("data").GetProperty("etag").GetString());
        using var oldVersion = await client.GetAsync($"/api/v1/profiles/{profileId}/versions/1");
        using var oldJson = JsonDocument.Parse(await oldVersion.Content.ReadAsStringAsync());
        Assert.Equal(120, oldJson.RootElement.GetProperty("data").GetProperty("version")
            .GetProperty("specification").GetProperty("duration_seconds").GetInt32());
        using var versionPage = await client.GetAsync($"/api/v1/profiles/{profileId}/versions?limit=1");
        using var versionPageJson = JsonDocument.Parse(await versionPage.Content.ReadAsStringAsync());
        var versionCursor = versionPageJson.RootElement.GetProperty("data").GetProperty("next_cursor").GetString()!;
        using var priorVersionPage = await client.GetAsync($"/api/v1/profiles/{profileId}/versions?limit=1&cursor={Uri.EscapeDataString(versionCursor)}");
        using var priorVersionPageJson = JsonDocument.Parse(await priorVersionPage.Content.ReadAsStringAsync());
        Assert.Equal(1, priorVersionPageJson.RootElement.GetProperty("data").GetProperty("versions")[0]
            .GetProperty("profile_ref").GetProperty("version").GetInt64());
        using var crossProfileCursor = await client.GetAsync($"/api/v1/profiles/{source.ProfileId}/versions?cursor={Uri.EscapeDataString(versionCursor)}");
        Assert.Equal(HttpStatusCode.BadRequest, crossProfileCursor.StatusCode);

        StopServer();
        StartServer();
        using var restarted = Client(authenticated: true);
        using var replayAfterRestart = await PostCreate(restarted, payload, "copy-key");
        Assert.Equal(HttpStatusCode.Created, replayAfterRestart.StatusCode);
        Assert.Equal(initialEtag, replayAfterRestart.Headers.ETag!.Tag);
        using var replayAfterRestartJson = JsonDocument.Parse(await replayAfterRestart.Content.ReadAsStringAsync());
        Assert.True(replayAfterRestartJson.RootElement.GetProperty("data").GetProperty("idempotent_replay").GetBoolean());
        Assert.False(Directory.Exists(source.OutputDirectory));
        using var afterRestart = await restarted.GetAsync($"/api/v1/profiles/{profileId}");
        Assert.Equal(patch.Headers.ETag.Tag, afterRestart.Headers.ETag!.Tag);
        using var removed = await Delete(restarted, profileId, patch.Headers.ETag.Tag);
        Assert.Equal(HttpStatusCode.OK, removed.StatusCode);
        using var tombstoneList = await restarted.GetAsync("/api/v1/profiles?include_deleted=true");
        using var tombstoneJson = JsonDocument.Parse(await tombstoneList.Content.ReadAsStringAsync());
        Assert.Contains(tombstoneJson.RootElement.GetProperty("data").GetProperty("profiles").EnumerateArray(),
            item => item.GetProperty("profile_ref").GetProperty("id").GetString() == profileId && item.GetProperty("is_deleted").GetBoolean());
        using var tombstonePatch = await Patch(restarted, profileId, removed.Headers.ETag!.Tag, "{\"name\":\"nope\"}");
        Assert.Equal(HttpStatusCode.Conflict, tombstonePatch.StatusCode);
        using var tombstoneCopy = await PostCreate(restarted, CreateBody("copy-deleted", profileId,
            copied.GetProperty("profile_ref").GetProperty("digest").GetString()!), "copy-deleted-key");
        Assert.Equal(HttpStatusCode.Conflict, tombstoneCopy.StatusCode);
    }

    [Fact]
    public async Task IfMatchAndStrictBodyMatrixIsEnforcedAndSameTagConcurrentPatchHasOneWinner()
    {
        var source = new SqliteRecurringFixedRegionProfileRepository(_store).CreateVersion1(
            RecurringFixedRegionProfileVersion.CreateVersion1("etag-source", DateTimeOffset.UtcNow, Spec()));
        StartServer();
        using var client = Client(authenticated: true);
        using var created = await PostCreate(client, CreateBody("copy", source.ProfileId, source.ProfileDigest), "etag-key");
        using var createdJson = JsonDocument.Parse(await created.Content.ReadAsStringAsync());
        var id = createdJson.RootElement.GetProperty("data").GetProperty("profile").GetProperty("profile_ref").GetProperty("id").GetString()!;
        var etag = created.Headers.ETag!.Tag;

        using var missing = await Patch(client, id, null, "{\"name\":\"next\"}");
        Assert.Equal((HttpStatusCode)428, missing.StatusCode);
        using var missingDelete = await Delete(client, id, null);
        Assert.Equal((HttpStatusCode)428, missingDelete.StatusCode);
        foreach (var invalid in new[] { "W/" + etag, "*", etag + ", " + etag })
        {
            using var response = await Patch(client, id, invalid, "{\"name\":\"next\"}");
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        }
        using var unknown = await Patch(client, id, etag, "{\"name\":\"x\",\"audio\":{\"mode\":\"none\"}}");
        Assert.Equal(HttpStatusCode.BadRequest, unknown.StatusCode);
        using var duplicate = await Patch(client, id, etag, "{\"name\":\"x\",\"name\":\"y\"}");
        Assert.Equal(HttpStatusCode.BadRequest, duplicate.StatusCode);
        using var traversal = await Patch(client, id, etag, "{\"output_directory\":\"D:\\\\Videos\\\\..\\\\escape\"}");
        Assert.Equal(HttpStatusCode.BadRequest, traversal.StatusCode);

        var requests = new[]
        {
            Patch(client, id, etag, "{\"name\":\"same\"}"),
            Patch(client, id, etag, "{\"name\":\"same\"}")
        };
        using var first = await requests[0];
        using var second = await requests[1];
        Assert.Equal(new[] { HttpStatusCode.OK, HttpStatusCode.PreconditionFailed },
            new[] { first.StatusCode, second.StatusCode }.OrderBy(status => status == HttpStatusCode.OK ? 0 : 1));
        using var staleRetry = await Patch(client, id, etag, "{\"name\":\"same\"}");
        Assert.Equal(HttpStatusCode.PreconditionFailed, staleRetry.StatusCode);
        using var withoutAuth = Client(authenticated: false);
        using var unauthorized = await withoutAuth.GetAsync($"/api/v1/profiles/{id}");
        Assert.Equal(HttpStatusCode.Unauthorized, unauthorized.StatusCode);
        using var missingProfile = await client.GetAsync("/api/v1/profiles/not-present");
        Assert.Equal(HttpStatusCode.NotFound, missingProfile.StatusCode);

        var otherSource = new SqliteRecurringFixedRegionProfileRepository(_store).CreateVersion1(
            RecurringFixedRegionProfileVersion.CreateVersion1("etag-source-other", DateTimeOffset.UtcNow, Spec()));
        using var otherCreated = await PostCreate(client, CreateBody("other", otherSource.ProfileId, otherSource.ProfileDigest), "etag-other-key");
        using var otherJson = JsonDocument.Parse(await otherCreated.Content.ReadAsStringAsync());
        var otherId = otherJson.RootElement.GetProperty("data").GetProperty("profile").GetProperty("profile_ref").GetProperty("id").GetString()!;
        using var crossProfileEtag = await Patch(client, otherId, etag, "{\"name\":\"cross\"}");
        Assert.Equal(HttpStatusCode.PreconditionFailed, crossProfileEtag.StatusCode);
    }

    [Fact]
    public async Task DeleteChecksEveryBoundHistoricalVersionAndNeverChangesPlanBinding()
    {
        var profiles = new SqliteRecurringFixedRegionProfileRepository(_store);
        var initial = profiles.CreateVersion1(RecurringFixedRegionProfileVersion.CreateVersion1("bound-profile", DateTimeOffset.UtcNow, Spec()));
        var management = new SqliteFixedRegionProfileManagementRepository(_store);
        var record = management.Get(initial.ProfileId)!;
        var patched = management.Patch(initial.ProfileId, FixedRegionProfileETag.Compute(record), null,
            new FixedRegionProfileChanges(DurationSeconds: 90), DateTimeOffset.UtcNow.AddSeconds(1));

        var plan = new PlanDefinition("bound-profile-plan", false, DateTimeOffset.UtcNow);
        new SqlitePlanDefinitionRepository(_store).Insert(plan);
        var bindings = new SqliteRecurringPlanProfileBindingRepository(_store);
        bindings.Bind(plan.Id, initial.Reference, DateTimeOffset.UtcNow.AddSeconds(2));
        StartServer();
        using var client = Client(authenticated: true);
        using var remove = await Delete(client, initial.ProfileId, FixedRegionProfileETag.Compute(patched));
        Assert.Equal(HttpStatusCode.Conflict, remove.StatusCode);
        using var error = JsonDocument.Parse(await remove.Content.ReadAsStringAsync());
        Assert.Equal("profile_in_use", error.RootElement.GetProperty("error").GetProperty("details").GetProperty("reason_code").GetString());
        Assert.Equal(initial.Reference, bindings.Get(plan.Id).ProfileRef);
        Assert.Null(management.Get(initial.ProfileId)!.DeletedAtUtc);
    }

    [Fact]
    public async Task StrictRoutesRejectBadCursorDuplicateQueryAndMissingSourceWithoutCreatingRows()
    {
        StartServer();
        using var client = Client(authenticated: true);
        using var noSource = await PostCreate(client, "{\"name\":\"new\",\"source_profile_ref\":{\"id\":\"missing\",\"version\":1,\"digest\":\"recurring-fixed-region-profile/v1:" + new string('0', 64) + "\"}}", "missing-source");
        Assert.Equal(HttpStatusCode.NotFound, noSource.StatusCode);
        var valid = new SqliteRecurringFixedRegionProfileRepository(_store).CreateVersion1(
            RecurringFixedRegionProfileVersion.CreateVersion1("bad-digest-source", DateTimeOffset.UtcNow, Spec()));
        using var mismatch = await PostCreate(client, CreateBody("bad-digest", valid.ProfileId,
            RecurringFixedRegionProfileVersion.DigestPrefix + new string('0', 64)), "bad-digest-key");
        Assert.Equal(HttpStatusCode.BadRequest, mismatch.StatusCode);
        using var badCursor = await client.GetAsync("/api/v1/profiles?cursor=not-a-cursor!");
        Assert.Equal(HttpStatusCode.BadRequest, badCursor.StatusCode);
        using var duplicateQuery = await client.GetAsync("/api/v1/profiles?limit=1&limit=2");
        Assert.Equal(HttpStatusCode.BadRequest, duplicateQuery.StatusCode);
        Assert.Single(new SqliteFixedRegionProfileManagementRepository(_store).List(100, null, true).Items);
        Assert.Equal(0L, Scalar("SELECT COUNT(*) FROM fixed_region_profile_create_idempotency;"));
    }

    [Fact]
    public async Task ConcurrentCreateReplayAndConflictAreAtomicAndDirectoryPaginationHasNoDuplicates()
    {
        var source = new SqliteRecurringFixedRegionProfileRepository(_store).CreateVersion1(
            RecurringFixedRegionProfileVersion.CreateVersion1("concurrent-source", DateTimeOffset.UtcNow, Spec()));
        StartServer();
        using var client = Client(authenticated: true);
        var identical = await Task.WhenAll(
            PostCreate(client, CreateBody("same", source.ProfileId, source.ProfileDigest), "race-idem"),
            PostCreate(client, CreateBody("same", source.ProfileId, source.ProfileDigest), "race-idem"));
        using (identical[0]) using (identical[1])
        {
            Assert.All(identical, response => Assert.Equal(HttpStatusCode.Created, response.StatusCode));
            var first = JsonDocument.Parse(await identical[0].Content.ReadAsStringAsync());
            var second = JsonDocument.Parse(await identical[1].Content.ReadAsStringAsync());
            Assert.Equal(first.RootElement.GetProperty("data").GetProperty("profile").GetProperty("profile_ref").GetProperty("id").GetString(),
                second.RootElement.GetProperty("data").GetProperty("profile").GetProperty("profile_ref").GetProperty("id").GetString());
        }

        var conflicting = await Task.WhenAll(
            PostCreate(client, CreateBody("left", source.ProfileId, source.ProfileDigest), "race-conflict"),
            PostCreate(client, CreateBody("right", source.ProfileId, source.ProfileDigest), "race-conflict"));
        using (conflicting[0]) using (conflicting[1])
            Assert.Equal(new[] { HttpStatusCode.Created, HttpStatusCode.Conflict },
                conflicting.Select(response => response.StatusCode).OrderBy(status => status == HttpStatusCode.Created ? 0 : 1));

        var ids = new HashSet<string>(StringComparer.Ordinal);
        string? cursor = null;
        do
        {
            using var page = await client.GetAsync("/api/v1/profiles?limit=1" + (cursor is null ? "" : "&cursor=" + Uri.EscapeDataString(cursor)));
            Assert.Equal(HttpStatusCode.OK, page.StatusCode);
            using var json = JsonDocument.Parse(await page.Content.ReadAsStringAsync());
            foreach (var item in json.RootElement.GetProperty("data").GetProperty("profiles").EnumerateArray())
                Assert.True(ids.Add(item.GetProperty("profile_ref").GetProperty("id").GetString()!));
            cursor = json.RootElement.GetProperty("data").GetProperty("next_cursor").GetString();
        } while (cursor is not null);
        Assert.Equal(3, ids.Count); // one legacy source plus one row per winning key.
        Assert.Equal(2L, Scalar("SELECT COUNT(*) FROM fixed_region_profile_create_idempotency;"));
        Assert.False(Directory.Exists(source.OutputDirectory));
    }

    [Fact]
    public async Task DeleteAndInitialBindingRaceSerializeAndTombstoneRejectsOnlyNewBinding()
    {
        var profileRepository = new SqliteRecurringFixedRegionProfileRepository(_store);
        var profile = profileRepository.CreateVersion1(RecurringFixedRegionProfileVersion.CreateVersion1("race-profile", DateTimeOffset.UtcNow, Spec()));
        var plan = new PlanDefinition("race-plan", false, DateTimeOffset.UtcNow);
        new SqlitePlanDefinitionRepository(_store).Insert(plan);
        var management = new SqliteFixedRegionProfileManagementRepository(_store);
        var binding = new SqliteRecurringPlanProfileBindingRepository(_store);
        var initial = management.Get(profile.ProfileId)!;
        using var gate = new ManualResetEventSlim(false);
        var delete = Task.Run(() =>
        {
            gate.Wait();
            try { management.Delete(profile.ProfileId, FixedRegionProfileETag.Compute(initial), DateTimeOffset.UtcNow); return "deleted"; }
            catch (FixedRegionProfileInUseException) { return "in_use"; }
        });
        var bind = Task.Run(() =>
        {
            gate.Wait();
            try { binding.Bind(plan.Id, profile.Reference, DateTimeOffset.UtcNow); return "bound"; }
            catch (Phase3PersistenceException exception) when (exception.Code == RecurringPlanProfileBindingPersistenceReasonCodes.ProfileDeleted) { return "deleted"; }
        });
        gate.Set();
        var results = await Task.WhenAll(delete, bind);
        var final = management.Get(profile.ProfileId)!;
        var persistedBinding = binding.TryGet(plan.Id);
        Assert.False(final.IsDeleted && persistedBinding is not null);
        Assert.True((final.IsDeleted && persistedBinding is null && results.Contains("deleted")) ||
            (!final.IsDeleted && persistedBinding is not null && results.Contains("in_use")));

        if (final.IsDeleted)
        {
            new SqlitePlanDefinitionRepository(_store).Insert(new PlanDefinition("race-plan-2", false, DateTimeOffset.UtcNow));
            Assert.Equal(RecurringPlanProfileBindingPersistenceReasonCodes.ProfileDeleted,
                Assert.Throws<Phase3PersistenceException>(() => binding.Bind("race-plan-2", profile.Reference, DateTimeOffset.UtcNow)).Code);
        }
    }

    [Fact]
    public async Task InteractiveRecordingProfileRequestIsStrictAndCapabilitiesSplitPlansFromRuns()
    {
        var sourceSpec = Spec();
        var fractionalSpec = new RecurringFixedRegionProfileSpecification(
            sourceSpec.TargetType, sourceSpec.RebindPolicy, sourceSpec.CaptureSemantics, sourceSpec.CoordinateSpace,
            sourceSpec.DisplayIdentityStatus, sourceSpec.StableDisplayFingerprint, sourceSpec.DisplayBounds,
            sourceSpec.RegionWithinDisplay, sourceSpec.DpiX, sourceSpec.DpiY, sourceSpec.PhysicalWidth,
            sourceSpec.PhysicalHeight, sourceSpec.Orientation, sourceSpec.TopologyDigest, sourceSpec.Backend,
            sourceSpec.AudioMode, TimeSpan.FromMilliseconds(1500), sourceSpec.CountdownSeconds,
            sourceSpec.OutputDirectory, sourceSpec.FilenamePrefix, sourceSpec.OutputConflictPolicy,
            sourceSpec.WakePolicy, sourceSpec.DesktopRequirement);
        var fractional = new SqliteRecurringFixedRegionProfileRepository(_store).CreateVersion1(
            RecurringFixedRegionProfileVersion.CreateVersion1("fractional-profile", DateTimeOffset.UtcNow, fractionalSpec));
        StartServer();
        using var client = Client(authenticated: true);
        foreach (var body in new[]
        {
            "{\"profile_ref\":{\"id\":\"p\",\"version\":1}}",
            "{\"profile_ref\":{\"id\":\"p\",\"version\":1,\"digest\":\"bad\",\"extra\":true}}",
            "{\"profile_ref\":{\"id\":\"p\",\"version\":1,\"digest\":\"bad\"},\"source\":{\"type\":\"display\"}}",
            "{\"profile_ref\":{\"id\":\"p\",\"version\":1,\"digest\":\"bad\"},\"profile_ref\":{\"id\":\"q\",\"version\":1,\"digest\":\"bad\"}}"
        })
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/recordings")
            { Content = new StringContent(body, Encoding.UTF8, "application/json") };
            using var response = await client.SendAsync(request);
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            Assert.Equal("INVALID_PROFILE_RECORDING_REQUEST", json.RootElement.GetProperty("error").GetProperty("code").GetString());
            Assert.True(json.RootElement.GetProperty("error").GetProperty("details").TryGetProperty("reason_code", out _));
        }

        var fractionalBody = JsonSerializer.Serialize(new { profile_ref = new
        {
            id = fractional.ProfileId, version = fractional.ProfileVersion, digest = fractional.ProfileDigest
        }});
        using (var fractionalResponse = await client.PostAsync("/api/v1/recordings",
                   new StringContent(fractionalBody, Encoding.UTF8, "application/json")))
        {
            Assert.Equal(HttpStatusCode.UnprocessableEntity, fractionalResponse.StatusCode);
            using var fractionalJson = JsonDocument.Parse(await fractionalResponse.Content.ReadAsStringAsync());
            Assert.Equal("unsupported_duration_precision", fractionalJson.RootElement.GetProperty("error")
                .GetProperty("details").GetProperty("reason_code").GetString());
        }
        var mismatchedDigest = fractional.ProfileDigest[..^1] +
            (fractional.ProfileDigest[^1] == '0' ? "1" : "0");
        var mismatchedBody = JsonSerializer.Serialize(new { profile_ref = new
        {
            id = fractional.ProfileId, version = fractional.ProfileVersion, digest = mismatchedDigest
        }});
        using (var mismatch = await client.PostAsync("/api/v1/recordings",
                   new StringContent(mismatchedBody, Encoding.UTF8, "application/json")))
        {
            Assert.Equal(HttpStatusCode.Conflict, mismatch.StatusCode);
            using var mismatchJson = JsonDocument.Parse(await mismatch.Content.ReadAsStringAsync());
            Assert.Equal("PROFILE_REF_MISMATCH", mismatchJson.RootElement.GetProperty("error").GetProperty("code").GetString());
        }
        var missingVersionBody = JsonSerializer.Serialize(new { profile_ref = new
        {
            id = fractional.ProfileId, version = 999, digest = fractional.ProfileDigest
        }});
        using (var missingVersion = await client.PostAsync("/api/v1/recordings",
                   new StringContent(missingVersionBody, Encoding.UTF8, "application/json")))
        {
            Assert.Equal(HttpStatusCode.NotFound, missingVersion.StatusCode);
            using var missingJson = JsonDocument.Parse(await missingVersion.Content.ReadAsStringAsync());
            Assert.Equal("PROFILE_VERSION_NOT_FOUND", missingJson.RootElement.GetProperty("error").GetProperty("code").GetString());
        }

        using var capabilities = await client.GetAsync("/api/v1/capabilities");
        using var capabilitiesJson = JsonDocument.Parse(await capabilities.Content.ReadAsStringAsync());
        var profileManagement = capabilitiesJson.RootElement.GetProperty("data").GetProperty("profile_management");
        Assert.False(profileManagement.GetProperty("recording_or_plan_profile_ref_supported").GetBoolean());
        Assert.True(profileManagement.GetProperty("ordinary_recording_profile_ref_supported").GetBoolean());
        Assert.False(profileManagement.GetProperty("ordinary_recording_execution_supported").GetBoolean());
        Assert.False(profileManagement.GetProperty("plan_profile_ref_supported").GetBoolean());
    }

    [Fact]
    public async Task ExactProfileRecordingUsesProductionSqliteSnapshotAndStopsAtLocalConfirmation()
    {
        var stableId = "display-stable-v1-" + new string('a', 64);
        var displayBounds = new AuthorizedPhysicalRectangle(-100, 50, 1920, 1080);
        var metadata = new StandingLeaseDisplayMetadata("display_1", stableId,
            DisplayIdentityResolutionStatus.Resolved, displayBounds, 96, 144, 1920, 1080,
            AuthorizedDisplayOrientation.Landscape);
        Assert.True(StandingLeaseDisplayTopologyDigest.TryCompute(new[] { metadata }, out var topologyDigest));
        var outputDirectory = Path.Combine(_root, "profile-output");
        var specification = new RecurringFixedRegionProfileSpecification(
            AuthorizedScopeTargetType.FixedRegion, RecurringFixedRegionRebindPolicy.ExactMatchOnly,
            AuthorizedCaptureSemantics.DesktopRegion, AuthorizedCoordinateSpace.PhysicalVirtualScreen,
            AuthorizedDisplayIdentityStatus.Resolved, stableId, displayBounds,
            new AuthorizedPhysicalRectangle(10, 20, 640, 480), 96, 144, 1920, 1080,
            AuthorizedDisplayOrientation.Landscape, topologyDigest,
            AuthorizedCaptureBackend.FfmpegRegion, AuthorizedAudioMode.None,
            TimeSpan.FromSeconds(60), 3, outputDirectory, "interactive", AuthorizedOutputConflictPolicy.FailIfExists,
            AuthorizedWakePolicy.NaturalWakeOnly, AuthorizedDesktopRequirement.InteractiveDesktopRequired);
        var profile = new SqliteRecurringFixedRegionProfileRepository(_store).CreateVersion1(
            RecurringFixedRegionProfileVersion.CreateVersion1("interactive-profile", DateTimeOffset.UtcNow, specification));

        SystemQuery.SetDisplayTopologyProvider(() => new List<SystemQuery.DisplayTopologyInfo>
        {
            new("display_1", "Display 1", true, new SystemQuery.Bounds(-100, 50, 1920, 1080), 1.0,
                stableId, DisplayIdentityResolutionStatus.Resolved)
        });
        var priorRegionBackend = Environment.GetEnvironmentVariable(CaptureBackendSelector.RegionBackendEnvVar);
        Environment.SetEnvironmentVariable(CaptureBackendSelector.RegionBackendEnvVar, null);
        var audit = new AuditLogger();
        var engine = new RecordingEngine(audit);
        var tray = new PendingConfirmationTray();
        engine.SetTray(tray);
        engine.ConfirmationTimeout = TimeSpan.FromHours(1);
        var displayProvider = new FixedProfileDisplayProvider(metadata);
        var backend = new NonStartingBackend();
        engine.BackendFactory = _ => (backend, "ffmpeg-region");
        var countdownGateReached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        engine.BeforeStartActionForTests = (_, stage) =>
        {
            if (stage == "backend.start")
                displayProvider.Set(metadata with { DpiX = 120 });
        };
        engine.BeforeStartFailureForTests = (_, stage) =>
        {
            if (stage == "countdown.backend.start") countdownGateReached.TrySetResult();
        };
        _server = new ApiServer(engine, audit, tray, null, null, null, null, null, null, null, null, null, 0,
            profileManagementGateway: new SqliteFixedRegionProfileManagementGateway(_store),
            profileDisplayEnvironmentProvider: displayProvider);
        _server.Start();
        using var client = Client(authenticated: true);
        try
        {
            var body = JsonSerializer.Serialize(new { profile_ref = new
            {
                id = profile.ProfileId, version = profile.ProfileVersion, digest = profile.ProfileDigest
            }});
            using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/recordings?creation_wait_ms=0")
            { Content = new StringContent(body, Encoding.UTF8, "application/json") };
            using var response = await client.SendAsync(request);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            var data = json.RootElement.GetProperty("data");
            Assert.Equal("requires_user_confirmation", data.GetProperty("status").GetString());
            Assert.Equal(profile.ProfileId, data.GetProperty("profile_ref").GetProperty("id").GetString());
            Assert.Equal(profile.ProfileVersion, data.GetProperty("profile_ref").GetProperty("version").GetInt64());
            Assert.True(tray.CallbackCaptured);
            tray.CapturedCallback!(ConfirmationDecision.Approve());
            await countdownGateReached.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(0, backend.StartCalls);
            var statusJson = JsonDocument.Parse(JsonSerializer.Serialize(engine.GetStatus(data.GetProperty("recording_id").GetString()!)));
            Assert.Equal("failed", statusJson.RootElement.GetProperty("status").GetString());
            Assert.Equal(profile.ProfileId, statusJson.RootElement.GetProperty("profile_ref").GetProperty("id").GetString());
            var effectiveOutputPath = statusJson.RootElement.GetProperty("output").GetProperty("path").GetString()!;
            Assert.Equal(outputDirectory, Path.GetDirectoryName(effectiveOutputPath));
            Assert.StartsWith("interactive-", Path.GetFileName(effectiveOutputPath));
            Assert.Contains(statusJson.RootElement.GetProperty("recording_id").GetString()!, effectiveOutputPath);
        }
        finally
        {
            StopServer();
            SystemQuery.SetDisplayTopologyProvider(null);
            Environment.SetEnvironmentVariable(CaptureBackendSelector.RegionBackendEnvVar, priorRegionBackend);
        }
    }

    private sealed class FixedProfileDisplayProvider(StandingLeaseDisplayMetadata metadata)
        : IFixedRegionProfileDisplayEnvironmentProvider
    {
        private StandingLeaseDisplayMetadata _metadata = metadata;
        public void Set(StandingLeaseDisplayMetadata current) => _metadata = current;
        public IReadOnlyList<StandingLeaseDisplayMetadata> GetExecutionMetadata() => new[] { _metadata };
        public IReadOnlyList<DisplayTopologySnapshot> GetTopology() => new[]
        {
            new DisplayTopologySnapshot("display_1", _metadata.StableDisplayFingerprint,
                _metadata.IdentityStatus, new CapturePlanBounds(-100, 50, 1920, 1080))
        };
    }

    private sealed class PendingConfirmationTray : ITrayContext
    {
        public string HostMode => "tray";
        public bool SupportsRegionSelectionUi => true;
        public bool CallbackCaptured { get; private set; }
        public Action<ConfirmationDecision>? CapturedCallback { get; private set; }
        public void RequestConfirmation(RecordingConfirmationPresentation presentation, Action<ConfirmationDecision> callback)
        { CallbackCaptured = true; CapturedCallback = callback; }
        public void RequestRegionSelection(int timeoutSeconds, Action<string, int, int, int, int, string, string> callback) { }
        public void SetRecording(RecordingUiPresentation presentation) { }
        public void SetIdle(RecordingUiPresentation presentation) { }
        public void SetAllIdle() { }
        public void ShowError(string text) { }
    }

    private sealed class NonStartingBackend : ICaptureBackend
    {
        public int StartCalls { get; private set; }
        public void Start(CaptureConfig config, CaptureAuthorizationProof authorizationProof) => StartCalls++;
        public OutputMeta Stop() => new();
        public void OnNaturalExit(Action<int, OutputMeta> callback) { }
        public int ExitCode => 0;
        public void Dispose() { }
    }

    [Fact]
    public void TombstonedProfilePreservesExactExistingBindingReplayButRejectsNewBinding()
    {
        var profile = new SqliteRecurringFixedRegionProfileRepository(_store).CreateVersion1(
            RecurringFixedRegionProfileVersion.CreateVersion1("tombstone-replay-profile", DateTimeOffset.UtcNow, Spec()));
        var plans = new SqlitePlanDefinitionRepository(_store);
        var bindings = new SqliteRecurringPlanProfileBindingRepository(_store);
        var existing = new PlanDefinition("existing-binding-plan", false, DateTimeOffset.UtcNow);
        var fresh = new PlanDefinition("new-binding-plan", false, DateTimeOffset.UtcNow);
        plans.Insert(existing);
        plans.Insert(fresh);
        var original = bindings.Bind(existing.Id, profile.Reference, DateTimeOffset.UtcNow);
        using (var connection = _store.OpenConnection())
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "UPDATE fixed_region_profile_directory SET deleted_at_utc = updated_at_utc + 1 WHERE profile_id = $id;";
            command.Parameters.AddWithValue("$id", profile.ProfileId);
            Assert.Equal(1, command.ExecuteNonQuery());
        }

        var replay = bindings.Bind(existing.Id, profile.Reference, DateTimeOffset.UtcNow.AddMinutes(1));
        Assert.Equal(original.BoundAtUtc, replay.BoundAtUtc);
        Assert.Equal(profile.Reference, bindings.Get(existing.Id).ProfileRef);
        Assert.Equal(RecurringPlanProfileBindingPersistenceReasonCodes.ProfileDeleted,
            Assert.Throws<Phase3PersistenceException>(() => bindings.Bind(fresh.Id, profile.Reference, DateTimeOffset.UtcNow)).Code);
    }

    [Fact]
    public void VersionTwentyUpgradeBackfillsLegacyRowsAndRepeatedOpenPreservesDirectory()
    {
        var legacyId = new string('l', 128);
        var source = new SqliteRecurringFixedRegionProfileRepository(_store).CreateVersion1(
            RecurringFixedRegionProfileVersion.CreateVersion1(legacyId, DateTimeOffset.UtcNow, Spec()));
        using (var connection = _store.OpenConnection())
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "DROP TRIGGER trg_fixed_region_profile_directory_insert; DROP TRIGGER trg_recurring_plan_profile_bindings_reject_deleted_profile; DROP TRIGGER trg_fixed_region_profile_create_idempotency_immutable_update; DROP TRIGGER trg_fixed_region_profile_create_idempotency_immutable_delete; DROP INDEX idx_fixed_region_profile_directory_live_id; DROP TABLE fixed_region_profile_create_idempotency; DROP TABLE fixed_region_profile_directory; DELETE FROM schema_migrations WHERE version = 21; PRAGMA user_version = 20;";
            command.ExecuteNonQuery();
        }
        _store.Initialize();
        var management = new SqliteFixedRegionProfileManagementRepository(_store);
        var migrated = Assert.Single(management.List(100, null, false).Items);
        Assert.Equal(source.ProfileId, migrated.ProfileId);
        Assert.Equal("Profile " + new string('l', 72), migrated.Name);
        Assert.Equal(source.ProfileDigest, migrated.CurrentVersion.ProfileDigest);
        _store.Initialize();
        var reopened = management.Get(source.ProfileId)!;
        Assert.Equal(migrated.Name, reopened.Name);
        Assert.Equal(migrated.CurrentVersion.ProfileDigest, reopened.CurrentVersion.ProfileDigest);
        Assert.Equal("bc9b43603cedb067ce33e62cb4110c5779609a00af17086a631dba5c4573c34b",
            SqliteSchemaV21.Migrations.Single().Checksum);
    }

    private void StartServer()
    {
        var audit = new AuditLogger();
        var engine = new RecordingEngine(audit);
        var tray = new HeadlessTrayContext(audit);
        engine.SetTray(tray);
        _server = new ApiServer(engine, audit, tray, null, null, null, null, null, null, null, null, null, 0,
            profileManagementGateway: new SqliteFixedRegionProfileManagementGateway(_store));
        _server.Start();
    }

    private void StopServer()
    {
        _server?.Stop();
        _server = null;
    }

    private HttpClient Client(bool authenticated)
    {
        var client = new HttpClient(new HttpClientHandler { UseProxy = false })
        {
            BaseAddress = new Uri($"http://127.0.0.1:{_server!.BoundPort}"), Timeout = TimeSpan.FromSeconds(8)
        };
        if (authenticated) client.DefaultRequestHeaders.Add("X-Agent-Recorder-Key", ApiKeyAuth.CurrentApiKey);
        return client;
    }

    private static async Task<HttpResponseMessage> PostCreate(HttpClient client, string body, string key)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/profiles")
        { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        request.Headers.Add("Idempotency-Key", key);
        return await client.SendAsync(request);
    }

    private static async Task<HttpResponseMessage> Patch(HttpClient client, string id, string? etag, string body)
    {
        using var request = new HttpRequestMessage(HttpMethod.Patch, $"/api/v1/profiles/{Uri.EscapeDataString(id)}")
        { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        if (etag is not null) request.Headers.TryAddWithoutValidation("If-Match", etag);
        return await client.SendAsync(request);
    }

    private static async Task<HttpResponseMessage> Delete(HttpClient client, string id, string? etag)
    {
        using var request = new HttpRequestMessage(HttpMethod.Delete, $"/api/v1/profiles/{Uri.EscapeDataString(id)}");
        if (etag is not null) request.Headers.TryAddWithoutValidation("If-Match", etag);
        return await client.SendAsync(request);
    }

    private static JsonElement ParseData(HttpResponseMessage response) =>
        JsonDocument.Parse(response.Content.ReadAsStringAsync().GetAwaiter().GetResult()).RootElement.GetProperty("data").Clone();

    private static string CreateBody(string name, string sourceId, string digest) => JsonSerializer.Serialize(new
    {
        name,
        source_profile_ref = new { id = sourceId, version = 1, digest },
        changes = new { }
    });

    private static RecurringFixedRegionProfileSpecification Spec() => new(
        AuthorizedScopeTargetType.FixedRegion, RecurringFixedRegionRebindPolicy.ExactMatchOnly,
        AuthorizedCaptureSemantics.DesktopRegion, AuthorizedCoordinateSpace.PhysicalVirtualScreen,
        AuthorizedDisplayIdentityStatus.Resolved, "DISPLAY-FP-1", new(-100, 50, 1920, 1080), new(10, 20, 640, 480),
        96, 144, 1920, 1080, AuthorizedDisplayOrientation.Landscape, new string('a', 64),
        AuthorizedCaptureBackend.FfmpegRegion, AuthorizedAudioMode.None, TimeSpan.FromMinutes(2), 3,
        Path.Combine(Path.GetTempPath(), "task305-profile-output-" + Guid.NewGuid().ToString("N")), "demo", AuthorizedOutputConflictPolicy.FailIfExists,
        AuthorizedWakePolicy.NaturalWakeOnly, AuthorizedDesktopRequirement.InteractiveDesktopRequired);

    public void Dispose()
    {
        StopServer();
        ApiKeyAuth.ResetForTesting(null);
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); } catch { }
    }

    private long Scalar(string sql)
    {
        using var connection = _store.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt64(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
    }
}
