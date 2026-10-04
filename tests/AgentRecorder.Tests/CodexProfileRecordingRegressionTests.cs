using System.Net;
using System.Text;
using System.Text.Json;
using AgentRecorder.Api;
using AgentRecorder.App;
using AgentRecorder.Capture;
using AgentRecorder.Core;
using AgentRecorder.Core.Automation;
using AgentRecorder.Infrastructure;
using AgentRecorder.Logging;
using AgentRecorder.Persistence;
using Microsoft.Data.Sqlite;
using AgentRecorder.Windows;
using Xunit;

namespace AgentRecorder.Tests;

[Collection("NonParallel-AgentRecorderDataDir")]
public sealed class CodexProfileRecordingRegressionTests : IDisposable
{
    private readonly TempDirectory _temp = new();
    private readonly string? _oldOverride;
    private readonly string? _oldMode;
    private readonly string? _oldBackend;
    private readonly SqliteFixedRegionProfileManagementRepository _management;
    private readonly SqliteRecurringFixedRegionProfileRepository _versions;
    private readonly SqliteFixedRegionProfileManagementGateway _gateway;
    private readonly DisplayProvider _display;
    private readonly Tray _tray = new();
    private readonly Backend _backend = new();
    private readonly RecordingEngine _engine;
    private readonly AuditLogger _audit;
    private ApiServer _server;
    private HttpClient _client;

    public CodexProfileRecordingRegressionTests()
    {
        _oldOverride = DataDirResolver.HasOverride ? DataDirResolver.Resolve() : null;
        _oldMode = Environment.GetEnvironmentVariable("AGENT_RECORDER_TEST_MODE");
        _oldBackend = Environment.GetEnvironmentVariable(CaptureBackendSelector.RegionBackendEnvVar);
        DataDirResolver.SetOverride(_temp.Path);
        Environment.SetEnvironmentVariable("AGENT_RECORDER_TEST_MODE", null);
        Environment.SetEnvironmentVariable(CaptureBackendSelector.RegionBackendEnvVar, null);
        var store = new SqliteOperationalStore(Path.Combine(_temp.Path, "state.db"));
        store.Initialize();
        _management = new SqliteFixedRegionProfileManagementRepository(store);
        _versions = new SqliteRecurringFixedRegionProfileRepository(store);
        _gateway = new SqliteFixedRegionProfileManagementGateway(store);
        var fingerprint = "display-stable-v1-" + new string('b', 64);
        _display = new DisplayProvider(new StandingLeaseDisplayMetadata("display_1", fingerprint,
            DisplayIdentityResolutionStatus.Resolved, new(-100, 50, 1920, 1080),
            96, 96, 1920, 1080, AuthorizedDisplayOrientation.Landscape));
        SystemQuery.SetDisplayTopologyProvider(() => new List<SystemQuery.DisplayTopologyInfo>
        {
            new("display_1", "Display 1", true, new(-100, 50, 1920, 1080), 1.0,
                fingerprint, DisplayIdentityResolutionStatus.Resolved)
        });
        ApiKeyAuth.InitializeForTesting(_temp.Path);
        _audit = new AuditLogger(Path.Combine(_temp.Path, "audit.jsonl"));
        _engine = new RecordingEngine(_audit) { CountdownInterval = TimeSpan.FromMilliseconds(1) };
        _engine.SetTray(_tray);
        _engine.BackendFactory = _ => (_backend, "ffmpeg-region");
        _server = new ApiServer(_engine, _audit, _tray, null, null, null, null, null, null, null, null, null, 0,
            profileManagementGateway: _gateway, profileDisplayEnvironmentProvider: _display);
        _server.Start();
        _client = new HttpClient(new HttpClientHandler { UseProxy = false })
        {
            BaseAddress = new Uri($"http://127.0.0.1:{_server.BoundPort}"), Timeout = TimeSpan.FromSeconds(10)
        };
        _client.DefaultRequestHeaders.Add("X-Agent-Recorder-Key", ApiKeyAuth.CurrentApiKey);
    }

    [Fact]
    public async Task AcceptedHistoricalConfigurationStaysFrozenAndLocalDirectoryOverrideBindsProof()
    {
        var profile = CreateProfile();
        var record = await CreateRecording(profile);
        Assert.Equal(RecState.pending_confirmation, record.State);
        Assert.Equal(0, _backend.StartCalls);
        var current = _management.Get(profile.ProfileId)!;
        var patched = _management.Patch(profile.ProfileId, FixedRegionProfileETag.Compute(current), "renamed",
            new FixedRegionProfileChanges(DurationSeconds: 120), DateTimeOffset.UtcNow);
        Assert.Equal(2, patched.CurrentVersion.ProfileVersion);
        var selected = Path.Combine(_temp.Path, "selected-output");
        _tray.Decision!(ConfirmationDecision.Approve(selected));
        Assert.True(SpinWait.SpinUntil(() => record.State == RecState.recording, TimeSpan.FromSeconds(3)));
        Assert.Equal(1, _backend.StartCalls);
        Assert.Equal(60, _backend.Config!.DurationSeconds);
        Assert.Equal((-90, 70, 640, 480), _backend.Config.Bounds);
        Assert.Equal(selected, Path.GetDirectoryName(_backend.Config.OutputPath));
        Assert.Equal("fail", _backend.Config.OutputConflictPolicy);
        Assert.Equal(CaptureAuthorizationProofKind.InteractiveConfirmation, _backend.Proof!.Kind);
        Assert.Equal(CaptureAuthorizationProofState.Consumed, _backend.Proof.State);
        Assert.Equal(profile.Reference, record.FixedRegionProfileReference);
        Assert.Equal(profile.OutputDirectory, _versions.Get(profile.ProfileId, 1).OutputDirectory);
        _engine.Stop(record.Id, "user_requested");
        Assert.True(record.IsFinalized);
        Assert.Equal(1, _backend.StartCalls);
    }

    [Fact]
    public async Task ProfilePurposeSelectionCreatesFirstImmutableProfileWithoutCreatingOutputDirectory()
    {
        _tray.Selection = ("selected", -90, 70, 640, 480, "display_1", "virtual_screen");
        using var selectionRequest = new StringContent("{\"purpose\":\"profile\",\"timeout_seconds\":10}",
            Encoding.UTF8, "application/json");
        using var selectionResponse = await _client.PostAsync("/api/v1/regions/select", selectionRequest);
        Assert.Equal(HttpStatusCode.OK, selectionResponse.StatusCode);
        using var selectionJson = JsonDocument.Parse(await selectionResponse.Content.ReadAsStringAsync());
        var selection = selectionJson.RootElement.GetProperty("data");
        var selectionRef = selection.GetProperty("selection_ref").GetString();
        Assert.StartsWith("sel_", selectionRef);
        Assert.Equal("profile", _tray.LastSelectionPurpose);
        Assert.False(File.Exists(Path.Combine(_temp.Path, "region-selection.json")));

        _tray.Selection = ("selected", -80, 80, 320, 240, "display_1", "virtual_screen");
        using var secondSelection = await _client.PostAsync("/api/v1/regions/select",
            new StringContent("{\"purpose\":\"profile\"}", Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.OK, secondSelection.StatusCode);
        using var secondSelectionJson = JsonDocument.Parse(await secondSelection.Content.ReadAsStringAsync());
        Assert.NotEqual(selectionRef, secondSelectionJson.RootElement.GetProperty("data")
            .GetProperty("selection_ref").GetString());

        var output = Path.Combine(_temp.Path, "profile-output-not-created");
        using var create = new HttpRequestMessage(HttpMethod.Post, "/api/v1/profiles")
        {
            Content = new StringContent(JsonSerializer.Serialize(new
            {
                name = "First profile",
                source_selection_ref = selectionRef,
                changes = new { duration_seconds = 10, countdown_seconds = 0, output_directory = output }
            }), Encoding.UTF8, "application/json")
        };
        create.Headers.Add("Idempotency-Key", "task307-first-profile-1");
        using var created = await _client.SendAsync(create);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        using var createdJson = JsonDocument.Parse(await created.Content.ReadAsStringAsync());
        var profile = createdJson.RootElement.GetProperty("data").GetProperty("profile");
        var reference = profile.GetProperty("profile_ref");
        Assert.Equal(1, reference.GetProperty("version").GetInt64());
        Assert.Equal("First profile", profile.GetProperty("name").GetString());
        var spec = profile.GetProperty("specification");
        Assert.Equal(new[] { -90, 70, 640, 480 }, new[]
        {
            spec.GetProperty("target_policy").GetProperty("display_bounds").GetProperty("x").GetInt32() +
                spec.GetProperty("target_policy").GetProperty("region_within_display").GetProperty("x").GetInt32(),
            spec.GetProperty("target_policy").GetProperty("display_bounds").GetProperty("y").GetInt32() +
                spec.GetProperty("target_policy").GetProperty("region_within_display").GetProperty("y").GetInt32(),
            spec.GetProperty("target_policy").GetProperty("region_within_display").GetProperty("width").GetInt32(),
            spec.GetProperty("target_policy").GetProperty("region_within_display").GetProperty("height").GetInt32()
        });
        Assert.Equal(10m, spec.GetProperty("duration_seconds").GetDecimal());
        Assert.Equal(0, spec.GetProperty("countdown_seconds").GetInt32());
        Assert.Equal(Path.TrimEndingDirectorySeparator(Path.GetFullPath(output)),
            Path.TrimEndingDirectorySeparator(spec.GetProperty("output").GetProperty("directory").GetString()!));
        Assert.False(Directory.Exists(output));

        var profileId = reference.GetProperty("id").GetString();
        using var listed = await _client.GetAsync("/api/v1/profiles?limit=100");
        Assert.Equal(HttpStatusCode.OK, listed.StatusCode);
        using var listedJson = JsonDocument.Parse(await listed.Content.ReadAsStringAsync());
        Assert.Contains(listedJson.RootElement.GetProperty("data").GetProperty("profiles").EnumerateArray(), item =>
            item.GetProperty("profile_ref").GetProperty("id").GetString() == profileId);
        using var detail = await _client.GetAsync($"/api/v1/profiles/{profileId}");
        Assert.Equal(HttpStatusCode.OK, detail.StatusCode);
        using var history = await _client.GetAsync($"/api/v1/profiles/{profileId}/versions/1");
        Assert.Equal(HttpStatusCode.OK, history.StatusCode);
        using var historyJson = JsonDocument.Parse(await history.Content.ReadAsStringAsync());
        Assert.Equal(reference.GetProperty("digest").GetString(), historyJson.RootElement.GetProperty("data")
            .GetProperty("version").GetProperty("profile_ref").GetProperty("digest").GetString());

        using var replay = new HttpRequestMessage(HttpMethod.Post, "/api/v1/profiles")
        {
            Content = new StringContent(JsonSerializer.Serialize(new
            {
                name = "First profile", source_selection_ref = selectionRef,
                changes = new { duration_seconds = 10, countdown_seconds = 0, output_directory = output }
            }), Encoding.UTF8, "application/json")
        };
        replay.Headers.Add("Idempotency-Key", "task307-first-profile-1");
        using var replayResponse = await _client.SendAsync(replay);
        Assert.Equal(HttpStatusCode.Created, replayResponse.StatusCode);
        using var replayJson = JsonDocument.Parse(await replayResponse.Content.ReadAsStringAsync());
        Assert.True(replayJson.RootElement.GetProperty("data").GetProperty("idempotent_replay").GetBoolean());
        Assert.Equal(profileId, replayJson.RootElement.GetProperty("data")
            .GetProperty("profile").GetProperty("profile_ref").GetProperty("id").GetString());
        Assert.False(Directory.Exists(output));
    }

    [Fact]
    public async Task SelectionProfileCreationRejectsOddGeometryAndInvalidReferences()
    {
        _tray.Selection = ("selected", -90, 70, 641, 480, "display_1", "virtual_screen");
        using var selectionRequest = new StringContent("{\"purpose\":\"profile\"}", Encoding.UTF8, "application/json");
        using var selectionResponse = await _client.PostAsync("/api/v1/regions/select", selectionRequest);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, selectionResponse.StatusCode);

        using var invalidCreate = new HttpRequestMessage(HttpMethod.Post, "/api/v1/profiles")
        {
            Content = new StringContent("{\"name\":\"invalid\",\"source_selection_ref\":\"sel_not-real\",\"changes\":{\"duration_seconds\":10}}",
                Encoding.UTF8, "application/json")
        };
        invalidCreate.Headers.Add("Idempotency-Key", "task307-invalid-ref-1");
        using var invalidResponse = await _client.SendAsync(invalidCreate);
        Assert.Equal(HttpStatusCode.Conflict, invalidResponse.StatusCode);
        using var json = JsonDocument.Parse(await invalidResponse.Content.ReadAsStringAsync());
        Assert.Equal("selection_ref_invalid_or_expired", json.RootElement.GetProperty("error")
            .GetProperty("details").GetProperty("reason_code").GetString());
        Assert.Empty(_management.List(100, null, includeDeleted: true).Items);
    }

    [Fact]
    public async Task NewlyCreatedProfileUsesTask306ConfirmationAndCannotCaptureOnRejection()
    {
        _tray.Selection = ("selected", -90, 70, 640, 480, "display_1", "virtual_screen");
        using var selectionResponse = await _client.PostAsync("/api/v1/regions/select",
            new StringContent("{\"purpose\":\"profile\"}", Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.OK, selectionResponse.StatusCode);
        using var selectionJson = JsonDocument.Parse(await selectionResponse.Content.ReadAsStringAsync());
        var selectionRef = selectionJson.RootElement.GetProperty("data").GetProperty("selection_ref").GetString();
        using var create = new HttpRequestMessage(HttpMethod.Post, "/api/v1/profiles")
        {
            Content = new StringContent(JsonSerializer.Serialize(new
            {
                name = "New-user profile", source_selection_ref = selectionRef,
                changes = new { duration_seconds = 10, output_directory = _temp.Path }
            }), Encoding.UTF8, "application/json")
        };
        create.Headers.Add("Idempotency-Key", "task307-recording-creation-1");
        using var createResponse = await _client.SendAsync(create);
        Assert.Equal(HttpStatusCode.Created, createResponse.StatusCode);
        using var createdJson = JsonDocument.Parse(await createResponse.Content.ReadAsStringAsync());
        var profileRef = createdJson.RootElement.GetProperty("data").GetProperty("profile")
            .GetProperty("profile_ref");
        var exactReference = new
        {
            id = profileRef.GetProperty("id").GetString(),
            version = profileRef.GetProperty("version").GetInt64(),
            digest = profileRef.GetProperty("digest").GetString()
        };

        async Task<Recording> StartRequest()
        {
            using var response = await _client.PostAsync("/api/v1/recordings?creation_wait_ms=0",
                new StringContent(JsonSerializer.Serialize(new { profile_ref = exactReference }),
                    Encoding.UTF8, "application/json"));
            var responseText = await response.Content.ReadAsStringAsync();
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            using var body = JsonDocument.Parse(responseText);
            Assert.Equal("requires_user_confirmation", body.RootElement.GetProperty("data").GetProperty("status").GetString());
            return _engine._recs[body.RootElement.GetProperty("data").GetProperty("recording_id").GetString()!];
        }

        var rejected = await StartRequest();
        Assert.Equal(0, _backend.StartCalls);
        _tray.Decision!(ConfirmationDecision.Reject());
        Assert.Equal(RecState.rejected, rejected.State);
        Assert.Equal(0, _backend.StartCalls);
        Assert.Null(rejected.AuthorizationProof);

        var approved = await StartRequest();
        Assert.Equal(0, _backend.StartCalls);
        _tray.Decision!(ConfirmationDecision.Approve());
        Assert.True(SpinWait.SpinUntil(() => approved.State == RecState.recording, TimeSpan.FromSeconds(3)));
        Assert.Equal(1, _backend.StartCalls);
        Assert.Equal(CaptureAuthorizationProofKind.InteractiveConfirmation, _backend.Proof!.Kind);
        Assert.Equal(new ProfileRef(exactReference.id!, exactReference.version, exactReference.digest!),
            approved.FixedRegionProfileReference);
        Assert.False(File.Exists(Path.Combine(_temp.Path, "plan.db")));
    }

    [Fact]
    public async Task OmittedDirectoryIsFrozenAndOriginalReplaySurvivesRestartPatchAndDelete()
    {
        var initialDefault = Path.Combine(_temp.Path, "default-at-create");
        var changedDefault = Path.Combine(_temp.Path, "default-after-create");
        Assert.True(OutputSettingsStore.SaveDefaultOutputDir(initialDefault));
        _tray.Selection = ("selected", -90, 70, 640, 480, "display_1", "virtual_screen");
        using var selected = await _client.PostAsync("/api/v1/regions/select",
            new StringContent("{\"purpose\":\"profile\"}", Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.OK, selected.StatusCode);
        using var selectionBody = JsonDocument.Parse(await selected.Content.ReadAsStringAsync());
        var selectionRef = selectionBody.RootElement.GetProperty("data").GetProperty("selection_ref").GetString();
        var body = JsonSerializer.Serialize(new
        {
            name = "Frozen default",
            source_selection_ref = selectionRef,
            changes = new { duration_seconds = 10 }
        });
        using var create = new HttpRequestMessage(HttpMethod.Post, "/api/v1/profiles")
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        };
        create.Headers.Add("Idempotency-Key", "task307-default-replay-1");
        using var created = await _client.SendAsync(create);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        using var createdJson = JsonDocument.Parse(await created.Content.ReadAsStringAsync());
        var createdData = createdJson.RootElement.GetProperty("data");
        var createdProfile = createdData.GetProperty("profile");
        var originalRef = createdProfile.GetProperty("profile_ref").Clone();
        var originalDigest = originalRef.GetProperty("digest").GetString();
        var originalEtag = createdData.GetProperty("etag").GetString();
        Assert.Equal(Path.TrimEndingDirectorySeparator(initialDefault), Path.TrimEndingDirectorySeparator(
            createdProfile.GetProperty("specification").GetProperty("output").GetProperty("directory").GetString()!));
        Assert.False(Directory.Exists(initialDefault));

        var profileId = originalRef.GetProperty("id").GetString()!;
        var current = _management.Get(profileId)!;
        var patched = _management.Patch(profileId, FixedRegionProfileETag.Compute(current), "Changed later",
            new FixedRegionProfileChanges(DurationSeconds: 20), DateTimeOffset.UtcNow);
        _management.Delete(profileId, FixedRegionProfileETag.Compute(patched), DateTimeOffset.UtcNow);
        Assert.True(OutputSettingsStore.SaveDefaultOutputDir(changedDefault));
        Assert.False(Directory.Exists(changedDefault));

        RestartApiServer(); // A new process-local selection cache has no live selection_ref.
        using var retry = new HttpRequestMessage(HttpMethod.Post, "/api/v1/profiles")
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        };
        retry.Headers.Add("Idempotency-Key", "task307-default-replay-1");
        using var replay = await _client.SendAsync(retry);
        Assert.Equal(HttpStatusCode.Created, replay.StatusCode);
        using var replayJson = JsonDocument.Parse(await replay.Content.ReadAsStringAsync());
        var replayData = replayJson.RootElement.GetProperty("data");
        Assert.True(replayData.GetProperty("idempotent_replay").GetBoolean());
        Assert.Equal(originalEtag, replayData.GetProperty("etag").GetString());
        Assert.Equal(originalRef.GetProperty("id").GetString(), replayData.GetProperty("profile")
            .GetProperty("profile_ref").GetProperty("id").GetString());
        Assert.Equal(originalDigest, replayData.GetProperty("profile").GetProperty("profile_ref")
            .GetProperty("digest").GetString());
        Assert.Equal("Frozen default", replayData.GetProperty("profile").GetProperty("name").GetString());
        Assert.False(replayData.GetProperty("profile").GetProperty("is_deleted").GetBoolean());
        Assert.Equal(Path.TrimEndingDirectorySeparator(initialDefault), Path.TrimEndingDirectorySeparator(
            replayData.GetProperty("profile").GetProperty("specification").GetProperty("output")
                .GetProperty("directory").GetString()!));

        using var conflictingRetry = new HttpRequestMessage(HttpMethod.Post, "/api/v1/profiles")
        {
            Content = new StringContent(body.Replace("duration_seconds\":10", "duration_seconds\":11", StringComparison.Ordinal),
                Encoding.UTF8, "application/json")
        };
        conflictingRetry.Headers.Add("Idempotency-Key", "task307-default-replay-1");
        using var conflictResponse = await _client.SendAsync(conflictingRetry);
        Assert.Equal(HttpStatusCode.Conflict, conflictResponse.StatusCode);
        Assert.Equal("IDEMPOTENCY_KEY_REUSED", Error(conflictResponse));
    }

    [Fact]
    public void FailedProfileTransactionLeavesNoVersionDirectoryOrIdempotencyRows()
    {
        using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder
               { DataSource = Path.Combine(_temp.Path, "state.db"), Pooling = false }.ToString()))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "CREATE TRIGGER task307_abort_directory_update BEFORE UPDATE ON fixed_region_profile_directory BEGIN SELECT RAISE(ABORT, 'task307 injected failure'); END;";
            command.ExecuteNonQuery();
        }
        var metadata = _display.Metadata;
        Assert.True(StandingLeaseDisplayTopologyDigest.TryCompute(new[] { metadata }, out var digest));
        var specification = new RecurringFixedRegionProfileSpecification(
            AuthorizedScopeTargetType.FixedRegion, RecurringFixedRegionRebindPolicy.ExactMatchOnly,
            AuthorizedCaptureSemantics.DesktopRegion, AuthorizedCoordinateSpace.PhysicalVirtualScreen,
            AuthorizedDisplayIdentityStatus.Resolved, metadata.StableDisplayFingerprint!, metadata.PhysicalBounds!.Value,
            new(10, 20, 640, 480), 96, 96, 1920, 1080, AuthorizedDisplayOrientation.Landscape,
            digest, AuthorizedCaptureBackend.FfmpegRegion, AuthorizedAudioMode.None, TimeSpan.FromSeconds(10), 3,
            _temp.Path, "transaction", AuthorizedOutputConflictPolicy.FailIfExists,
            AuthorizedWakePolicy.NaturalWakeOnly, AuthorizedDesktopRequirement.InteractiveDesktopRequired);

        Assert.Throws<Phase3PersistenceException>(() => _management.CreateFromSelection(
            "task307-transaction-failure", new string('e', 64), "Must roll back", specification, DateTimeOffset.UtcNow));
        Assert.Empty(_management.List(100, null, includeDeleted: true).Items);
        using var verify = new SqliteConnection(new SqliteConnectionStringBuilder
            { DataSource = Path.Combine(_temp.Path, "state.db"), Pooling = false }.ToString());
        verify.Open();
        foreach (var table in new[] { "recurring_fixed_region_profile_versions", "fixed_region_profile_directory", "fixed_region_profile_create_idempotency" })
        {
            using var count = verify.CreateCommand();
            count.CommandText = $"SELECT COUNT(*) FROM {table};";
            Assert.Equal(0L, (long)count.ExecuteScalar()!);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SelectedProfileRejectsEnvironmentDriftBeforePersisting(bool identityChanged)
    {
        var reference = await SelectForProfile();
        _display.Metadata = identityChanged
            ? _display.Metadata with { StableDisplayFingerprint = "display-stable-v1-" + new string('c', 64) }
            : _display.Metadata with { DpiX = 120 };
        using var response = await PostSelectionProfile(reference, "codex-selection-drift");
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("PROFILE_SELECTION_ENVIRONMENT_CHANGED", Error(response));
        Assert.Empty(_management.List(100, null, true).Items);
        Assert.Empty(_engine._recs);
        Assert.Equal(0, _backend.StartCalls);
        Assert.Null(_tray.Decision);
    }

    [Theory]
    [InlineData("{\"name\":\"x\",\"name\":\"y\",\"source_selection_ref\":\"$REF\",\"changes\":{\"duration_seconds\":10}}")]
    [InlineData("{\"name\":\"x\",\"source_selection_ref\":\"$REF\",\"source_profile_ref\":{},\"changes\":{\"duration_seconds\":10}}")]
    [InlineData("{\"name\":\"x\",\"source_selection_ref\":\"$REF\",\"changes\":{}}")]
    [InlineData("{\"name\":\"x\",\"source_selection_ref\":\"$REF\",\"changes\":{\"duration_seconds\":10.5}}")]
    [InlineData("{\"name\":\"x\",\"source_selection_ref\":\"$REF\",\"changes\":{\"duration_seconds\":10,\"countdown_seconds\":11}}")]
    [InlineData("{\"name\":\"x\",\"source_selection_ref\":\"$REF\",\"changes\":{\"duration_seconds\":10,\"duration_seconds\":20}}")]
    [InlineData("{\"name\":\"x\",\"source_selection_ref\":\"$REF\",\"bounds\":{},\"changes\":{\"duration_seconds\":10}}")]
    [InlineData("{\"name\":\"x\",\"source_selection_ref\":\"$REF\",\"changes\":{\"duration_seconds\":10,\"audio\":{\"enabled\":true}}}")]
    public async Task SelectionProfileRejectsInjectedOrAmbiguousConfiguration(string requestBody)
    {
        var reference = await SelectForProfile();
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/profiles")
        {
            Content = new StringContent(requestBody.Replace("$REF", reference, StringComparison.Ordinal),
                Encoding.UTF8, "application/json")
        };
        request.Headers.Add("Idempotency-Key", "codex-invalid-selection-body");
        using var response = await _client.SendAsync(request);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Empty(_management.List(100, null, true).Items);
        Assert.Empty(_engine._recs);
        Assert.Equal(0, _backend.StartCalls);
    }

    [Theory]
    [InlineData(-90, 70, 30, 480)]
    [InlineData(-90, 70, 640, 481)]
    [InlineData(1800, 70, 640, 480)]
    public async Task ProfileSelectionDoesNotSilentlyRepairUnsupportedGeometry(int x, int y, int width, int height)
    {
        _tray.Selection = ("selected", x, y, width, height, "display_1", "virtual_screen");
        using var response = await _client.PostAsync("/api/v1/regions/select",
            new StringContent("{\"purpose\":\"profile\"}", Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Empty(_management.List(100, null, true).Items);
        Assert.Equal(0, _backend.StartCalls);
    }

    [Fact]
    public async Task ProfileSelectionCancellationPreservesLastRegionAndRestartInvalidatesUncommittedReference()
    {
        var prior = new SelectedRegionState(true, "display_1", "virtual_screen", -80, 80, 320, 240,
            DateTime.UtcNow.ToString("O"), "region_selection");
        RegionSelectionStateStore.Save(prior);
        using var cancelled = await _client.PostAsync("/api/v1/regions/select",
            new StringContent("{\"purpose\":\"profile\"}", Encoding.UTF8, "application/json"));
        using var cancelledBody = JsonDocument.Parse(await cancelled.Content.ReadAsStringAsync());
        Assert.Equal("selection_cancelled", cancelledBody.RootElement.GetProperty("data").GetProperty("status").GetString());
        Assert.False(cancelledBody.RootElement.GetProperty("data").TryGetProperty("selection_ref", out _));
        var reference = await SelectForProfile();
        Assert.Equal(prior, RegionSelectionStateStore.Load());
        RestartApiServer();
        using var response = await PostSelectionProfile(reference, "codex-uncommitted-restart");
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("PROFILE_SELECTION_INVALID", Error(response));
        Assert.Empty(_management.List(100, null, true).Items);
        Assert.Equal(prior, RegionSelectionStateStore.Load());
        Assert.Equal(0, _backend.StartCalls);
    }

    [Fact]
    public async Task ConcurrentSelectionProfileCreationCommitsOnlyOneImmutableResult()
    {
        var reference = await SelectForProfile();
        var requests = Enumerable.Range(0, 8).Select(_ => PostSelectionProfile(reference, "codex-concurrent-selection")).ToArray();
        var results = await Task.WhenAll(requests);
        var digests = new List<string>();
        try
        {
            foreach (var response in results)
            {
                Assert.Equal(HttpStatusCode.Created, response.StatusCode);
                using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
                digests.Add(document.RootElement.GetProperty("data").GetProperty("profile").GetProperty("profile_ref").GetRawText());
            }
            Assert.Single(digests.Distinct(StringComparer.Ordinal));
            Assert.Single(_management.List(100, null, true).Items);
            Assert.Empty(_engine._recs);
            Assert.Equal(0, _backend.StartCalls);
            Assert.Null(_tray.Decision);
        }
        finally { foreach (var response in results) response.Dispose(); }
    }

    private async Task<string> SelectForProfile()
    {
        _tray.Selection = ("selected", -90, 70, 640, 480, "display_1", "virtual_screen");
        using var response = await _client.PostAsync("/api/v1/regions/select",
            new StringContent("{\"purpose\":\"profile\"}", Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.GetProperty("data").GetProperty("selection_ref").GetString()!;
    }

    private async Task<HttpResponseMessage> PostSelectionProfile(string reference, string key)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/profiles")
        {
            Content = new StringContent(JsonSerializer.Serialize(new
            {
                name = "Codex selection profile", source_selection_ref = reference,
                changes = new { duration_seconds = 10, output_directory = Path.Combine(_temp.Path, "not-created") }
            }), Encoding.UTF8, "application/json")
        };
        request.Headers.Add("Idempotency-Key", key);
        return await _client.SendAsync(request);
    }

    [Fact]
    public async Task TombstoneDoesNotRewriteAcceptedRequestButBlocksNextRequest()
    {
        var profile = CreateProfile();
        var record = await CreateRecording(profile);
        var directory = _management.Get(profile.ProfileId)!;
        _management.Delete(profile.ProfileId, FixedRegionProfileETag.Compute(directory), DateTimeOffset.UtcNow);
        using var second = await Post(profile);
        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
        Assert.Equal("PROFILE_DELETED", Error(second));
        _tray.Decision!(ConfirmationDecision.Approve());
        Assert.True(SpinWait.SpinUntil(() => record.State == RecState.recording, TimeSpan.FromSeconds(3)));
        Assert.Equal(1, _backend.StartCalls);
        Assert.Equal(profile.Reference, record.FixedRegionProfileReference);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RegionThatWouldBeNormalizedIsRejectedWithoutConfirmation(bool tooSmall)
    {
        var profile = CreateProfile(width: tooSmall ? 30 : 641);
        using var response = await Post(profile);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal("PROFILE_NOT_EXECUTABLE", Error(response));
        Assert.Null(_tray.Decision);
        Assert.Equal(0, _backend.StartCalls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ProfileDriftKeepsActionableFailureCodeAtEitherStartBoundary(bool atCountdownZero)
    {
        var profile = CreateProfile(countdown: 1);
        var record = await CreateRecording(profile);
        if (atCountdownZero)
            _engine.BeforeStartActionForTests = (_, stage) =>
            {
                if (stage == "backend.start") _display.Metadata = _display.Metadata with { DpiX = 120 };
            };
        else
            _display.Metadata = _display.Metadata with { DpiX = 120 };
        _tray.Decision!(ConfirmationDecision.Approve());
        Assert.True(SpinWait.SpinUntil(() => record.IsFinalized, TimeSpan.FromSeconds(3)));
        Assert.Equal(0, _backend.StartCalls);
        Assert.NotEqual(CaptureAuthorizationProofState.Consumed, record.AuthorizationProof?.State);
        Assert.Equal(1, _tray.ErrorCount);
        Assert.Equal(RecState.failed, record.State);
        using var status = await _client.GetAsync($"/api/v1/recordings/{record.Id}");
        using var document = JsonDocument.Parse(await status.Content.ReadAsStringAsync());
        var data = document.RootElement.GetProperty("data");
        Assert.Equal("PROFILE_ENVIRONMENT_CHANGED", data.GetProperty("stop_reason").GetString());
        Assert.Contains(data.GetProperty("warnings").EnumerateArray(), warning =>
            warning.GetString() == "PROFILE_ENVIRONMENT_CHANGED:display_topology_changed");
        var rejection = ReadProfileEnvironmentAudit(record.Id);
        Assert.Equal("display_topology_changed", rejection.GetProperty("reason_code").GetString());
        Assert.Equal(atCountdownZero ? "countdown_complete" : "after_confirmation",
            rejection.GetProperty("stage").GetString());
        var auditRef = rejection.GetProperty("profile_ref");
        Assert.Equal(profile.ProfileId, auditRef.GetProperty("id").GetString());
        Assert.Equal(profile.ProfileVersion, auditRef.GetProperty("version").GetInt64());
        Assert.Equal(profile.ProfileDigest, auditRef.GetProperty("digest").GetString());
    }

    [Fact]
    public async Task LocalOutputCollisionRejectsInsteadOfRenamingOrOverwriting()
    {
        var record = await CreateRecording(CreateProfile());
        var selected = Path.Combine(_temp.Path, "selected-output");
        Directory.CreateDirectory(selected);
        var existing = Path.Combine(selected, Path.GetFileName(record.OutputPath));
        File.WriteAllText(existing, "owned-test-sentinel");
        _tray.Decision!(ConfirmationDecision.Approve(selected));
        Assert.Equal(RecState.rejected, record.State);
        Assert.Equal(0, _backend.StartCalls);
        Assert.Equal("owned-test-sentinel", File.ReadAllText(existing));
        Assert.Single(Directory.GetFiles(selected));
    }

    [Fact]
    public async Task EnvironmentRejectionAuditKeepsReasonCodeAsScalar()
    {
        var profile = CreateProfile();
        var record = await CreateRecording(profile);
        _display.Metadata = _display.Metadata with { DpiX = 120 };
        _tray.Decision!(ConfirmationDecision.Approve());
        Assert.True(record.IsFinalized);
        Assert.Equal(0, _backend.StartCalls);
        var lines = File.ReadAllLines(Path.Combine(_temp.Path, "audit.jsonl"));
        var matched = lines.Select(line => JsonSerializer.Deserialize<JsonElement>(line))
            .Single(row => row.GetProperty("event").GetString() == "recording.profile_environment_changed");
        Assert.Equal(JsonValueKind.String, matched.GetProperty("reason_code").ValueKind);
        Assert.Equal("display_topology_changed", matched.GetProperty("reason_code").GetString());
        Assert.Equal("after_confirmation", matched.GetProperty("stage").GetString());
        Assert.Equal(record.Id, matched.GetProperty("recording_id").GetString());
        var auditRef = matched.GetProperty("profile_ref");
        Assert.Equal(profile.ProfileId, auditRef.GetProperty("id").GetString());
        Assert.Equal(profile.ProfileVersion, auditRef.GetProperty("version").GetInt64());
        Assert.Equal(profile.ProfileDigest, auditRef.GetProperty("digest").GetString());
    }

    [Fact]
    public async Task StopWinsCountdownProfileRejectionRaceWithoutBackendOrDuplicateTerminal()
    {
        var profile = CreateProfile(countdown: 1);
        var record = await CreateRecording(profile);
        var failureReached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFailure = new ManualResetEventSlim();
        _engine.BeforeStartActionForTests = (_, stage) =>
        {
            if (stage == "backend.start") _display.Metadata = _display.Metadata with { DpiX = 120 };
        };
        _engine.BeforeStartFailureForTests = (_, stage) =>
        {
            if (stage != "countdown.backend.start") return;
            failureReached.TrySetResult();
            releaseFailure.Wait(TimeSpan.FromSeconds(5));
        };

        _tray.Decision!(ConfirmationDecision.Approve());
        await failureReached.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var stop = Task.Run(() => _engine.Stop(record.Id, "user_requested"));
        Assert.True(SpinWait.SpinUntil(() => record.State == RecState.stopping || record.IsFinalized,
            TimeSpan.FromSeconds(5)));
        releaseFailure.Set();
        await stop.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(SpinWait.SpinUntil(() => record.IsFinalized, TimeSpan.FromSeconds(5)));
        Assert.Equal(0, _backend.StartCalls);
        Assert.Equal(0, _backend.StopCalls);
        Assert.NotEqual("PROFILE_ENVIRONMENT_CHANGED", record.StopReason);
        var events = File.ReadAllLines(Path.Combine(_temp.Path, "audit.jsonl"))
            .Select(line => JsonSerializer.Deserialize<JsonElement>(line))
            .Select(row => row.GetProperty("event").GetString())
            .Where(name => name is "recording.cancelled" or "recording.failed")
            .ToArray();
        Assert.Single(events);
        Assert.DoesNotContain("recording.profile_environment_changed",
            File.ReadAllLines(Path.Combine(_temp.Path, "audit.jsonl"))
                .Select(line => JsonSerializer.Deserialize<JsonElement>(line).GetProperty("event").GetString()));
    }

    [Fact]
    public async Task ExpiredProfileConfirmationCannotBeRevivedByLateApproval()
    {
        _engine.ConfirmationTimeout = TimeSpan.FromMilliseconds(30);
        var record = await CreateRecording(CreateProfile());
        Assert.True(SpinWait.SpinUntil(() => record.State == RecState.expired, TimeSpan.FromSeconds(5)));
        _tray.Decision!(ConfirmationDecision.Approve());
        Assert.Equal(RecState.expired, record.State);
        Assert.Equal(0, _backend.StartCalls);
        Assert.Null(record.AuthorizationProof);
        Assert.DoesNotContain(File.ReadAllLines(Path.Combine(_temp.Path, "audit.jsonl")), line =>
            line.Contains("confirmation.approved", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ConfirmationFileAsOutputDirectoryDoesNotFallBackOrCapture()
    {
        var profile = CreateProfile();
        var record = await CreateRecording(profile);
        var originalOutputPath = record.OutputPath;
        var occupiedPath = Path.Combine(_temp.Path, "not-a-directory");
        File.WriteAllText(occupiedPath, "owned-test-sentinel");
        _tray.Decision!(ConfirmationDecision.Approve(occupiedPath));
        Assert.Equal(RecState.rejected, record.State);
        Assert.Equal(0, _backend.StartCalls);
        Assert.Null(record.AuthorizationProof);
        Assert.Equal("owned-test-sentinel", File.ReadAllText(occupiedPath));
        Assert.Equal(originalOutputPath, record.OutputPath);
    }

    [Fact]
    public async Task OrdinaryBackendStartExceptionRetainsBackendDiagnostic()
    {
        var record = await CreateRecording(CreateProfile());
        _backend.ThrowOnStart = true;
        _tray.Decision!(ConfirmationDecision.Approve());
        Assert.True(SpinWait.SpinUntil(() => record.IsFinalized, TimeSpan.FromSeconds(5)));
        Assert.Equal(1, _backend.StartCalls);
        Assert.Equal("unexpected_exit", record.StopReason);
        var events = File.ReadAllLines(Path.Combine(_temp.Path, "audit.jsonl"));
        var failure = events.Select(line => JsonSerializer.Deserialize<JsonElement>(line))
            .Single(row => row.GetProperty("event").GetString() == "recording.failed");
        Assert.Equal("backend_start", failure.GetProperty("stage").GetString());
        Assert.Equal("unexpected_exit", failure.GetProperty("reason_code").GetString());
        Assert.Contains("controlled backend start failure", failure.GetProperty("error").GetString());
        Assert.DoesNotContain(events, line => line.Contains("recording.profile_environment_changed", StringComparison.Ordinal));
    }

    private JsonElement ReadProfileEnvironmentAudit(string recordingId) =>
        File.ReadAllLines(Path.Combine(_temp.Path, "audit.jsonl"))
            .Select(line => JsonSerializer.Deserialize<JsonElement>(line))
            .Single(row => row.GetProperty("event").GetString() == "recording.profile_environment_changed" &&
                row.GetProperty("recording_id").GetString() == recordingId);

    [Fact]
    public async Task UserRejectionCannotBeOverriddenByDuplicateApproval()
    {
        var record = await CreateRecording(CreateProfile());
        _tray.Decision!(ConfirmationDecision.Reject());
        _tray.Decision!(ConfirmationDecision.Approve());
        Assert.Equal(RecState.rejected, record.State);
        Assert.Equal(0, _backend.StartCalls);
        Assert.Null(record.AuthorizationProof);
    }

    private RecurringFixedRegionProfileVersion CreateProfile(int width = 640, int countdown = 0)
    {
        var metadata = _display.Metadata;
        Assert.True(StandingLeaseDisplayTopologyDigest.TryCompute(new[] { metadata }, out var digest));
        return _versions.CreateVersion1(RecurringFixedRegionProfileVersion.CreateVersion1(
            "codex-execution-" + Guid.NewGuid().ToString("N"), DateTimeOffset.UtcNow,
            new RecurringFixedRegionProfileSpecification(
                AuthorizedScopeTargetType.FixedRegion, RecurringFixedRegionRebindPolicy.ExactMatchOnly,
                AuthorizedCaptureSemantics.DesktopRegion, AuthorizedCoordinateSpace.PhysicalVirtualScreen,
                AuthorizedDisplayIdentityStatus.Resolved, metadata.StableDisplayFingerprint!, metadata.PhysicalBounds!.Value,
                new(10, 20, width, 480), 96, 96, 1920, 1080, AuthorizedDisplayOrientation.Landscape,
                digest, AuthorizedCaptureBackend.FfmpegRegion, AuthorizedAudioMode.None,
                TimeSpan.FromSeconds(60), countdown, Path.Combine(_temp.Path, "profile-output"), "codex",
                AuthorizedOutputConflictPolicy.FailIfExists, AuthorizedWakePolicy.NaturalWakeOnly,
                AuthorizedDesktopRequirement.InteractiveDesktopRequired)));
    }

    private async Task<Recording> CreateRecording(RecurringFixedRegionProfileVersion profile)
    {
        using var response = await Post(profile);
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, body);
        using var document = JsonDocument.Parse(body);
        var data = document.RootElement.GetProperty("data");
        Assert.Equal("requires_user_confirmation", data.GetProperty("status").GetString());
        return _engine._recs[data.GetProperty("recording_id").GetString()!];
    }

    private Task<HttpResponseMessage> Post(RecurringFixedRegionProfileVersion profile) =>
        _client.PostAsync("/api/v1/recordings?creation_wait_ms=0", new StringContent(JsonSerializer.Serialize(new
        {
            profile_ref = new { id = profile.ProfileId, version = profile.ProfileVersion, digest = profile.ProfileDigest }
        }), Encoding.UTF8, "application/json"));

    private static string? Error(HttpResponseMessage response)
    {
        using var document = JsonDocument.Parse(response.Content.ReadAsStringAsync().GetAwaiter().GetResult());
        return document.RootElement.GetProperty("error").GetProperty("code").GetString();
    }

    private void RestartApiServer()
    {
        _client.Dispose();
        _server.Stop();
        _server = new ApiServer(_engine, _audit, _tray, null, null, null, null, null, null, null, null, null, 0,
            profileManagementGateway: _gateway, profileDisplayEnvironmentProvider: _display);
        _server.Start();
        _client = new HttpClient(new HttpClientHandler { UseProxy = false })
        {
            BaseAddress = new Uri($"http://127.0.0.1:{_server.BoundPort}"), Timeout = TimeSpan.FromSeconds(10)
        };
        _client.DefaultRequestHeaders.Add("X-Agent-Recorder-Key", ApiKeyAuth.CurrentApiKey);
    }

    public void Dispose()
    {
        foreach (var record in _engine._recs.Values.Where(record => !record.IsFinalized))
            _engine.Stop(record.Id, "test_cleanup");
        _engine.Dispose();
        _client.Dispose();
        _server.Stop();
        SystemQuery.SetDisplayTopologyProvider(null);
        ApiKeyAuth.ResetForTesting(null);
        if (_oldOverride is null) DataDirResolver.ClearOverride(); else DataDirResolver.SetOverride(_oldOverride);
        Environment.SetEnvironmentVariable("AGENT_RECORDER_TEST_MODE", _oldMode);
        Environment.SetEnvironmentVariable(CaptureBackendSelector.RegionBackendEnvVar, _oldBackend);
        _temp.Dispose();
    }

    private sealed class DisplayProvider(StandingLeaseDisplayMetadata metadata) : IFixedRegionProfileDisplayEnvironmentProvider
    {
        internal StandingLeaseDisplayMetadata Metadata = metadata;
        public IReadOnlyList<StandingLeaseDisplayMetadata> GetExecutionMetadata() => new[] { Metadata };
        public IReadOnlyList<DisplayTopologySnapshot> GetTopology() => new[]
        {
            new DisplayTopologySnapshot("display_1", Metadata.StableDisplayFingerprint,
                Metadata.IdentityStatus, new(-100, 50, 1920, 1080))
        };
    }

    private sealed class Tray : ITrayContext
    {
        public string HostMode => "tray";
        public bool SupportsRegionSelectionUi => true;
        internal Action<ConfirmationDecision>? Decision;
        internal int ErrorCount;
        internal (string Status, int X, int Y, int W, int H, string DisplayId, string CoordinateSpace) Selection =
            ("selection_cancelled", 0, 0, 0, 0, "", "virtual_screen");
        internal string? LastSelectionPurpose;
        public void RequestConfirmation(RecordingConfirmationPresentation presentation, Action<ConfirmationDecision> callback) => Decision = callback;
        public void RequestRegionSelection(int timeoutSeconds, Action<string, int, int, int, int, string, string> callback) { }
        public void RequestRegionSelection(int timeoutSeconds, Action<string, int, int, int, int, string, string> callback, string purpose)
        {
            LastSelectionPurpose = purpose;
            var result = Selection;
            callback(result.Status, result.X, result.Y, result.W, result.H, result.DisplayId, result.CoordinateSpace);
        }
        public void SetRecording(RecordingUiPresentation presentation) { }
        public void SetIdle(RecordingUiPresentation presentation) { }
        public void SetAllIdle() { }
        public void ShowError(string text) => Interlocked.Increment(ref ErrorCount);
    }

    private sealed class Backend : ICaptureBackend, IFirstFrameObservableCaptureBackend
    {
        public event Action<FirstFrameObservation>? FirstFrameObserved;
        internal int StartCalls;
        internal int StopCalls;
        internal int CancelCalls;
        internal bool ThrowOnStart;
        internal CaptureConfig? Config;
        internal CaptureAuthorizationProof? Proof;
        public void Start(CaptureConfig config, CaptureAuthorizationProof authorizationProof)
        {
            Interlocked.Increment(ref StartCalls);
            if (ThrowOnStart) throw new InvalidOperationException("controlled backend start failure");
            Config = config;
            Proof = authorizationProof;
            FirstFrameObserved?.Invoke(new FirstFrameObservation { FrameNumber = 1, TotalSizeBytes = 1 });
        }
        public OutputMeta Stop() { Interlocked.Increment(ref StopCalls); return new(); }
        public void Cancel() => Interlocked.Increment(ref CancelCalls);
        public void OnNaturalExit(Action<int, OutputMeta> callback) { }
        public int ExitCode => 0;
        public void Dispose() { }
    }
}
