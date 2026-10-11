using System.Net;
using System.Text;
using System.Text.Json;
using AgentRecorder.Api;
using AgentRecorder.Core;
using AgentRecorder.Headless;
using AgentRecorder.Infrastructure;
using AgentRecorder.Logging;
using AgentRecorder.Persistence;
using AgentRecorder.Windows;
using Microsoft.Data.Sqlite;
using Xunit;

namespace AgentRecorder.Tests;

[Collection("HeadlessHostIntegration")]
public sealed class OrdinaryRecordingHistoryApiTests : IDisposable
{
    private readonly string _root = Path.GetFullPath(Path.Combine(
    AppContext.BaseDirectory, "..", "..", "..", "..", "..", ".local-data", "temp", "task309s", "api", Guid.NewGuid().ToString("N")));
    private ApiServer? _server;
    private string? _priorDataDir;

    [Fact]
    public async Task HistoryEndpointsRequireAuthAndReturnDurableEnvelopeAndMetadataOnlyDetail()
    {
        Directory.CreateDirectory(_root);
        ApiKeyAuth.InitializeForTesting(_root);
        var database = new SqliteOperationalStore(Path.Combine(_root, "state", "agent-recorder.db"));
        database.Initialize();
        using var history = new SqliteOrdinaryRecordingHistoryStore(database);
        var createdAt = DateTimeOffset.UtcNow.AddSeconds(-20);
        history.Register(new OrdinaryRecordingHistorySnapshot(
            "rec_api", createdAt, "completed", "display", "display_capture", "Primary display",
            15, "none", null, null, null, null, Path.Combine(_root, "capture.mp4"), null, null, null,
            null, null, null, createdAt.AddSeconds(5), createdAt.AddSeconds(20), "duration_reached", 123, 15));
        var audit = new AuditLogger();
        var tray = new HeadlessTrayContext(audit);
        var engine = new RecordingEngine(audit);
        engine.ConfigureOrdinaryRecordingHistory(history);
        engine.SetTray(tray);
        _server = new ApiServer(engine, audit, tray, null, null, null, null, null, null, null, null, null, 0);
        _server.Start();

        using var anonymous = NewClient(authenticated: false);
        using var denied = await anonymous.GetAsync("/api/v1/recording-history");
        Assert.Equal(HttpStatusCode.Unauthorized, denied.StatusCode);

        using var client = NewClient(authenticated: true);
        using var capabilities = await client.GetAsync("/api/v1/capabilities");
        using var capabilitiesJson = JsonDocument.Parse(await capabilities.Content.ReadAsStringAsync());
        Assert.Equal("ordinary_video", capabilitiesJson.RootElement.GetProperty("data").GetProperty("recording_history").GetProperty("scope").GetString());
        Assert.False(capabilitiesJson.RootElement.GetProperty("data").GetProperty("recording_history").GetProperty("screenshot_series_supported").GetBoolean());

        using var list = await client.GetAsync("/api/v1/recording-history?limit=1");
        Assert.Equal(HttpStatusCode.OK, list.StatusCode);
        using var listJson = JsonDocument.Parse(await list.Content.ReadAsStringAsync());
        Assert.True(listJson.RootElement.GetProperty("ok").GetBoolean());
        Assert.False(string.IsNullOrWhiteSpace(listJson.RootElement.GetProperty("request_id").GetString()));
        Assert.Equal("rec_api", listJson.RootElement.GetProperty("data").GetProperty("items")[0].GetProperty("recording_id").GetString());

        using var detail = await client.GetAsync("/api/v1/recording-history/rec_api");
        using var detailJson = JsonDocument.Parse(await detail.Content.ReadAsStringAsync());
        var recording = detailJson.RootElement.GetProperty("data").GetProperty("recording");
        Assert.Equal("completed", recording.GetProperty("status").GetString());
        Assert.False(recording.GetProperty("output_file").GetProperty("exists").GetBoolean());

        using var missing = await client.GetAsync("/api/v1/recording-history/unknown");
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        using var missingJson = JsonDocument.Parse(await missing.Content.ReadAsStringAsync());
        Assert.Equal("RECORDING_HISTORY_NOT_FOUND", missingJson.RootElement.GetProperty("error").GetProperty("code").GetString());

        var pending = new OrdinaryRecordingHistorySnapshot(
            "rec_worker_fault", createdAt.AddMinutes(1), "preparing", "display", "display_capture", "Pending",
            15, "none", null, null, null, null, Path.Combine(_root, "pending.mp4"), null, null, null, null, null, null);
        history.Register(pending);
        using (var connection = database.OpenConnection())
        using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                CREATE TRIGGER reject_history_update BEFORE UPDATE ON ordinary_recording_history
                BEGIN SELECT RAISE(ABORT, 'injected history worker failure'); END;
                """;
            command.ExecuteNonQuery();
        }
        Assert.True(history.TryPublish(pending with { Status = "recording" }));
        using var failedRead = await client.GetAsync("/api/v1/recording-history");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, failedRead.StatusCode);
        using var failedReadJson = JsonDocument.Parse(await failedRead.Content.ReadAsStringAsync());
        Assert.Equal("RECORDING_HISTORY_UNAVAILABLE", failedReadJson.RootElement.GetProperty("error").GetProperty("code").GetString());
    }

    [Fact]
    public async Task InitialHistoryWriteFailureReturns503AndDoesNotRetainOrStartRecording()
    {
        Directory.CreateDirectory(_root);
        _priorDataDir = Environment.GetEnvironmentVariable("AGENT_RECORDER_DATA_DIR");
        Environment.SetEnvironmentVariable("AGENT_RECORDER_DATA_DIR", _root);
        ApiKeyAuth.InitializeForTesting(_root);
        var captureDirectory = Path.Combine(_root, "captures");
        Directory.CreateDirectory(captureDirectory);
        var fingerprint = DisplayIdentityDeriver.Resolve("task309-failure-test", new[]
        {
            new DisplayTargetMapping("task309-failure-test", "task309-display")
        }).Fingerprint!;
        SystemQuery.SetDisplayProvider(() => new List<SystemQuery.DisplayInfo>
        {
            new("display_1", "Display 1", true, new SystemQuery.Bounds(0, 0, 1920, 1080), 1.0)
        });
        SystemQuery.SetDisplayTopologyProvider(() => new List<SystemQuery.DisplayTopologyInfo>
        {
            new("display_1", "Display 1", true, new SystemQuery.Bounds(0, 0, 1920, 1080), 1.0,
                fingerprint, DisplayIdentityResolutionStatus.Resolved)
        });
        var audit = new AuditLogger();
        var tray = new HeadlessTrayContext(audit);
        var engine = new RecordingEngine(audit);
        var history = new FailingHistoryStore();
        engine.ConfigureOrdinaryRecordingHistory(history);
        engine.SetTray(tray);
        _server = new ApiServer(engine, audit, tray, null, null, null, null, null, null, null, null, null, 0);
        _server.Start();

        using var client = NewClient(authenticated: true);
        var createBody = JsonSerializer.Serialize(new
        {
            target = new { type = "primary_display" },
            countdown_seconds = 10,
            output = new { directory = captureDirectory }
        });
        using var response = await client.PostAsync("/api/v1/recordings/quick",
            new StringContent(createBody, Encoding.UTF8, "application/json"));
        var responseBody = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == HttpStatusCode.ServiceUnavailable,
            $"Expected initial durable-history write failure to return 503; got {(int)response.StatusCode}: {responseBody}");
        using var json = JsonDocument.Parse(responseBody);
        Assert.Equal("RECORDING_HISTORY_UNAVAILABLE", json.RootElement.GetProperty("error").GetProperty("code").GetString());
        Assert.Equal(1, history.RegisterCount);
        Assert.Equal(0, history.PublishCount);
        Assert.Empty(engine.List());
    }

    private HttpClient NewClient(bool authenticated)
    {
        var client = new HttpClient(new HttpClientHandler { UseProxy = false })
        {
            BaseAddress = new Uri($"http://127.0.0.1:{_server!.BoundPort}"),
            Timeout = TimeSpan.FromSeconds(8)
        };
        if (authenticated) client.DefaultRequestHeaders.Add("X-Agent-Recorder-Key", ApiKeyAuth.CurrentApiKey);
        return client;
    }

    public void Dispose()
    {
        try { _server?.Stop(); } catch { }
        _server = null;
        ApiKeyAuth.ResetForTesting(null);
        SystemQuery.SetDisplayProvider(null);
        SystemQuery.SetDisplayTopologyProvider(null);
        if (_priorDataDir is not null)
            Environment.SetEnvironmentVariable("AGENT_RECORDER_DATA_DIR", _priorDataDir);
        else
            Environment.SetEnvironmentVariable("AGENT_RECORDER_DATA_DIR", null);
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); } catch { }
    }

    private sealed class FailingHistoryStore : IOrdinaryRecordingHistoryStore
    {
        public int RegisterCount { get; private set; }
        public int PublishCount { get; private set; }

        public void Register(OrdinaryRecordingHistorySnapshot snapshot)
        {
            RegisterCount++;
            throw new IOException("test history write failure");
        }

        public bool TryPublish(OrdinaryRecordingHistorySnapshot snapshot)
        {
            PublishCount++;
            return true;
        }

        public OrdinaryRecordingHistoryPage ReadPage(int limit, string? cursor) =>
            new(Array.Empty<OrdinaryRecordingHistorySnapshot>(), null);

        public OrdinaryRecordingHistorySnapshot? Read(string recordingId) => null;

        public void RecoverIncomplete(DateTimeOffset recoveredAtUtc) { }
    }
}
