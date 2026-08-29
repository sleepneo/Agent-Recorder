using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using AgentRecorder.Api;
using AgentRecorder.Capture;
using AgentRecorder.Core;
using AgentRecorder.Infrastructure;
using AgentRecorder.Logging;
using AgentRecorder.Windows;
using Xunit;

namespace AgentRecorder.Tests;

/// <summary>
/// Verifies that the public HTTP JSON contract uses snake_case for stop_reason
/// across stop, status, status-wait and output responses, and that the
/// <c>/displays</c> endpoint returns a stable, serializable contract without
/// internal Win32 handles or DPI fields.
/// </summary>
[Collection("HeadlessHostIntegration")]
public class ApiResponseSerializationTests : IDisposable
{
    private sealed class NoOpTray : ITrayContext
    {
        public string HostMode => "headless";
        public bool SupportsRegionSelectionUi => false;

        public void RequestConfirmation(RecordingConfirmationPresentation presentation, Action<ConfirmationDecision> callback) { }
        public void RequestRegionSelection(int timeoutSeconds,
            Action<string, int, int, int, int, string, string> callback) { }
        public void SetRecording(RecordingUiPresentation rec) { }
        public void SetIdle(RecordingUiPresentation rec) { }
        public void SetAllIdle() { }
        public void ShowError(string text) { }
    }

    private sealed class FakeCaptureBackend : ICaptureBackend
    {
        public OutputMeta StopResult { get; set; } = new();
        public int ExitCodeValue { get; set; }
        private Action<int, OutputMeta>? _onNaturalExit;

        public void Start(CaptureConfig cfg) => cfg.CommandArgs = "fake args";
        public OutputMeta Stop() => StopResult;
        public void OnNaturalExit(Action<int, OutputMeta> callback) => _onNaturalExit = callback;
        public int ExitCode => ExitCodeValue;
        public void Dispose() { }
    }

    [Fact]
    public void ApiResponses_AlwaysSerializeStopReasonAsSnakeCase()
    {
        var audit = new CaptureAuditLogger();
        var tray = new NoOpTray();
        var engine = new RecordingEngine(audit);
        engine.SetTray(tray);

        var backend = new FakeCaptureBackend
        {
            StopResult = new OutputMeta { DurationSeconds = 4.4, SizeBytes = 263781 },
            ExitCodeValue = 0
        };
        engine.BackendFactory = _ => (backend, "fake");

        var outputPath = Path.Combine(Path.GetTempPath(), $"test-api-{Guid.NewGuid():N}.mp4");
        var rec = new Recording
        {
            SourceType = "region",
            DurationSeconds = 30,
            OutputPath = outputPath,
            Config = new CaptureConfig
            {
                SourceKind = "region",
                Bounds = (0, 0, 1920, 1080),
                Fps = 30,
                OutputPath = outputPath
            }
        };

        engine.StartCaptureForTests(rec, tray);
        engine.Stop(rec.Id, "floating_button");

        Assert.Equal("floating_button", rec.StopReason);

        var stopJson = ApiResponse.Ok(engine.Stop(rec.Id, "tray_menu"), "req1");
        Assert.Contains("\"stop_reason\":\"floating_button\"", stopJson);
        Assert.DoesNotContain("\"StopReason\"", stopJson);

        var statusJson = ApiResponse.Ok(engine.GetStatus(rec.Id), "req2");
        Assert.Contains("\"stop_reason\":\"floating_button\"", statusJson);
        Assert.DoesNotContain("\"StopReason\"", statusJson);

        var waitJson = ApiResponse.Ok(engine.GetStatusWait(rec.Id, "recording", 100), "req3");
        Assert.Contains("\"stop_reason\":\"floating_button\"", waitJson);
        Assert.DoesNotContain("\"StopReason\"", waitJson);

        var outputJson = ApiResponse.Ok(engine.GetOutput(rec.Id), "req4");
        Assert.Contains("\"stop_reason\":\"floating_button\"", outputJson);
        Assert.DoesNotContain("\"StopReason\"", outputJson);
    }

    private ApiServer? _server;
    private string? _dataDir;

    private ApiServer CreateServer()
    {
        _dataDir = Path.Combine(Path.GetTempPath(), $"api-serialization-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_dataDir);
        Environment.SetEnvironmentVariable("AGENT_RECORDER_DATA_DIR", _dataDir, EnvironmentVariableTarget.Process);
        ApiKeyAuth.InitializeForTesting(_dataDir);

        var tray = new NoOpTray();
        var audit = new AuditLogger();
        var engine = new RecordingEngine(audit);
        engine.SetTray(tray);
        _server = new ApiServer(engine, audit, tray);
        return _server;
    }

    private static HttpClient CreateClient()
    {
        var handler = new HttpClientHandler { UseProxy = false };
        return new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(5) };
    }

    private static StringContent JsonContent(string json) =>
        new(json, Encoding.UTF8, "application/json");

    [Fact]
    public async Task RecordingConfirmationSummary_OrdinaryAndQuick_PreserveLegacyValueKinds()
    {
        SystemQuery.SetDisplayProvider(() => new List<SystemQuery.DisplayInfo>
        {
            new("display_1", "Display 1", true, new SystemQuery.Bounds(0, 0, 1920, 1080), 1.0)
        });

        var ordinaryServer = CreateServer();
        ordinaryServer.Start();
        JsonDocument ordinaryDocument;
        var ordinaryDataDir = _dataDir!;
        using (var client = CreateClient())
        {
            client.DefaultRequestHeaders.Add("X-Agent-Recorder-Key", ApiKeyAuth.CurrentApiKey);
            var ordinaryResponse = await client.PostAsync(
                $"http://127.0.0.1:{ApiServer.Port}/api/v1/recordings",
                JsonContent("{\"source\":{\"type\":\"display\",\"display_id\":\"display_1\"},\"stop_condition\":{\"type\":\"duration\",\"seconds\":60}}"));

            Assert.Equal(200, (int)ordinaryResponse.StatusCode);
            ordinaryDocument = JsonDocument.Parse(await ordinaryResponse.Content.ReadAsStringAsync());
        }

        ordinaryServer.Stop();
        try { if (Directory.Exists(ordinaryDataDir)) Directory.Delete(ordinaryDataDir, recursive: true); } catch { }
        Environment.SetEnvironmentVariable("AGENT_RECORDER_DATA_DIR", null, EnvironmentVariableTarget.Process);
        ApiKeyAuth.ResetForTesting(null);

        var quickServer = CreateServer();
        quickServer.Start();
        JsonDocument quickDocument;
        using (var client = CreateClient())
        {
            client.DefaultRequestHeaders.Add("X-Agent-Recorder-Key", ApiKeyAuth.CurrentApiKey);
            var quickResponse = await client.PostAsync(
                $"http://127.0.0.1:{ApiServer.Port}/api/v1/recordings/quick",
                JsonContent("{\"target\":{\"type\":\"primary_display\"},\"duration_seconds\":60}"));

            Assert.Equal(200, (int)quickResponse.StatusCode);
            quickDocument = JsonDocument.Parse(await quickResponse.Content.ReadAsStringAsync());
        }

        using (ordinaryDocument)
        using (quickDocument)
        {
            var ordinarySummary = ordinaryDocument.RootElement.GetProperty("data").GetProperty("summary");
            var quickSummary = quickDocument.RootElement.GetProperty("data").GetProperty("summary");

            AssertLegacySummaryValueKinds(ordinarySummary);
            AssertLegacySummaryValueKinds(quickSummary);

            foreach (var field in new[]
            {
                "audio_system_enabled", "audio_system_default_output", "audio_system_output_name",
                "audio_system_output_is_default", "series", "series_interval_ms",
                "series_max_count", "series_max_duration_seconds", "series_planned_frame_count",
                "selection_fallback", "target_display_bounds", "capture_bounds"
            })
            {
                Assert.Equal(ordinarySummary.GetProperty(field).ValueKind, quickSummary.GetProperty(field).ValueKind);
            }
        }
    }

    private static void AssertLegacySummaryValueKinds(JsonElement summary)
    {
        Assert.Equal(JsonValueKind.String, summary.GetProperty("audio_system_enabled").ValueKind);
        Assert.Equal("False", summary.GetProperty("audio_system_enabled").GetString());
        Assert.Equal(JsonValueKind.String, summary.GetProperty("audio_system_default_output").ValueKind);
        Assert.Equal("", summary.GetProperty("audio_system_default_output").GetString());
        Assert.Equal(JsonValueKind.String, summary.GetProperty("audio_system_output_name").ValueKind);
        Assert.Equal("", summary.GetProperty("audio_system_output_name").GetString());
        Assert.Equal(JsonValueKind.String, summary.GetProperty("audio_system_output_is_default").ValueKind);
        Assert.Equal("", summary.GetProperty("audio_system_output_is_default").GetString());
        Assert.Equal(JsonValueKind.String, summary.GetProperty("series").ValueKind);
        Assert.Equal("", summary.GetProperty("series").GetString());
        Assert.Equal(JsonValueKind.Null, summary.GetProperty("series_interval_ms").ValueKind);
        Assert.Equal(JsonValueKind.Null, summary.GetProperty("series_max_count").ValueKind);
        Assert.Equal(JsonValueKind.Null, summary.GetProperty("series_max_duration_seconds").ValueKind);
        Assert.Equal(JsonValueKind.Null, summary.GetProperty("series_planned_frame_count").ValueKind);
        Assert.Equal(JsonValueKind.False, summary.GetProperty("selection_fallback").ValueKind);
        Assert.Equal(JsonValueKind.Object, summary.GetProperty("target_display_bounds").ValueKind);
        Assert.Equal(JsonValueKind.Object, summary.GetProperty("capture_bounds").ValueKind);
    }

    public void Dispose()
    {
        _server?.Stop();
        SystemQuery.SetDisplayProvider(null);
        if (_dataDir != null)
        {
            try { if (Directory.Exists(_dataDir)) Directory.Delete(_dataDir, recursive: true); } catch { }
        }
        Environment.SetEnvironmentVariable("AGENT_RECORDER_DATA_DIR", null, EnvironmentVariableTarget.Process);
        ApiKeyAuth.ResetForTesting(null);
    }

    [Fact]
    public async Task DisplaysEndpoint_Authenticated_Returns200WithPublicFields()
    {
        SystemQuery.SetDisplayProvider(() => new List<SystemQuery.DisplayInfo>
        {
            new("display_1", "Display 1", true, new SystemQuery.Bounds(0, 0, 1920, 1080), 1.5, 1),
            new("display_2", "Display 2", false, new SystemQuery.Bounds(1920, 0, 1920, 1080), 2.0, 2)
        });

        var server = CreateServer();
        server.Start();

        using var client = CreateClient();
        client.DefaultRequestHeaders.Add("X-Agent-Recorder-Key", ApiKeyAuth.CurrentApiKey);
        var response = await client.GetAsync($"http://127.0.0.1:{ApiServer.Port}/api/v1/displays");
        Assert.Equal(200, (int)response.StatusCode);

        var body = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(body);

        Assert.True(doc.RootElement.GetProperty("ok").GetBoolean());

        var data = doc.RootElement.GetProperty("data");
        var displays = data.GetProperty("displays");
        Assert.Equal(2, displays.GetArrayLength());

        var first = displays[0];
        Assert.Equal("display_1", first.GetProperty("id").GetString());
        Assert.Equal("Display 1", first.GetProperty("name").GetString());
        Assert.True(first.GetProperty("is_primary").GetBoolean());
        Assert.Equal(1, first.GetProperty("windows_display_number").GetInt32());

        var bounds = first.GetProperty("bounds");
        Assert.Equal(0, bounds.GetProperty("x").GetInt32());
        Assert.Equal(0, bounds.GetProperty("y").GetInt32());
        Assert.Equal(1920, bounds.GetProperty("width").GetInt32());
        Assert.Equal(1080, bounds.GetProperty("height").GetInt32());
        Assert.Equal(1.5, first.GetProperty("scale_factor").GetDouble());

        var second = displays[1];
        Assert.Equal("display_2", second.GetProperty("id").GetString());
        Assert.Equal(2, second.GetProperty("windows_display_number").GetInt32());
        Assert.Equal(2.0, second.GetProperty("scale_factor").GetDouble());
    }

    [Fact]
    public async Task DisplaysEndpoint_ResponseJson_DoesNotContainInternalFields()
    {
        SystemQuery.SetDisplayProvider(() => new List<SystemQuery.DisplayInfo>
        {
            new("display_1", "Display 1", true, new SystemQuery.Bounds(0, 0, 1920, 1080), 1.5)
        });

        var server = CreateServer();
        server.Start();

        using var client = CreateClient();
        client.DefaultRequestHeaders.Add("X-Agent-Recorder-Key", ApiKeyAuth.CurrentApiKey);
        var response = await client.GetAsync($"http://127.0.0.1:{ApiServer.Port}/api/v1/displays");
        var body = await response.Content.ReadAsStringAsync();

        Assert.DoesNotContain("handle", body);
        Assert.DoesNotContain("dpiX", body);
        Assert.DoesNotContain("dpiY", body);
        Assert.DoesNotContain("dpi_x", body);
        Assert.DoesNotContain("dpi_y", body);
        Assert.DoesNotContain("DpiX", body);
        Assert.DoesNotContain("DpiY", body);
    }

    [Fact]
    public void Displays_EnumDisplays_DirectJsonSerialization_DoesNotThrow()
    {
        SystemQuery.SetDisplayProvider(() => new List<SystemQuery.DisplayInfo>
        {
            new("display_1", "Display 1", true, new SystemQuery.Bounds(0, 0, 1920, 1080), 1.5)
        });

        var displays = SystemQuery.EnumDisplays();
        var json = JsonSerializer.Serialize(displays);

        Assert.False(string.IsNullOrEmpty(json));
        Assert.Contains("\"id\":\"display_1\"", json);
        Assert.Contains("\"scale_factor\":1.5", json);
        Assert.DoesNotContain("handle", json);
        Assert.DoesNotContain("dpi", json);
    }

    [Fact]
    public async Task DisplaysEndpoint_NoApiKey_Returns200BecausePublicEndpoint()
    {
        SystemQuery.SetDisplayProvider(() => new List<SystemQuery.DisplayInfo>
        {
            new("display_1", "Display 1", true, new SystemQuery.Bounds(0, 0, 1920, 1080), 1.0)
        });

        var server = CreateServer();
        server.Start();

        using var client = CreateClient();
        var response = await client.GetAsync($"http://127.0.0.1:{ApiServer.Port}/api/v1/displays");
        Assert.Equal(200, (int)response.StatusCode);

        var body = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(body);
        Assert.True(doc.RootElement.GetProperty("ok").GetBoolean());
        Assert.True(doc.RootElement.GetProperty("data").GetProperty("displays").GetArrayLength() > 0);
    }

    [Fact]
    public async Task DisplaysAndCapabilities_ExposeTheSameWindowsDisplayNumberContract()
    {
        SystemQuery.SetDisplayProvider(() => new List<SystemQuery.DisplayInfo>
        {
            new("display_1", "Display 2", true, new SystemQuery.Bounds(0, 0, 1920, 1080), 1.0, 2),
            new("display_2", "Display 3", false, new SystemQuery.Bounds(1920, 0, 1920, 1080), 1.0, 3),
            new("display_3", "Display 1", false, new SystemQuery.Bounds(-1920, 0, 1920, 1080), 1.0, 1)
        });

        var server = CreateServer();
        server.Start();
        using var client = CreateClient();
        client.DefaultRequestHeaders.Add("X-Agent-Recorder-Key", ApiKeyAuth.CurrentApiKey);

        using var displaysDocument = JsonDocument.Parse(await (
            await client.GetAsync($"http://127.0.0.1:{ApiServer.Port}/api/v1/displays"))
            .Content.ReadAsStringAsync());
        using var capabilitiesDocument = JsonDocument.Parse(await (
            await client.GetAsync($"http://127.0.0.1:{ApiServer.Port}/api/v1/capabilities"))
            .Content.ReadAsStringAsync());

        var displays = displaysDocument.RootElement.GetProperty("data").GetProperty("displays");
        var capabilityItems = capabilitiesDocument.RootElement
            .GetProperty("data")
            .GetProperty("context")
            .GetProperty("displays")
            .GetProperty("items");
        Assert.Equal(displays.GetArrayLength(), capabilityItems.GetArrayLength());
        for (int i = 0; i < displays.GetArrayLength(); i++)
        {
            foreach (var field in new[] { "id", "name", "windows_display_number" })
                Assert.Equal(displays[i].GetProperty(field).ToString(), capabilityItems[i].GetProperty(field).ToString());
        }

        Assert.Equal("display_1", displays[0].GetProperty("id").GetString());
        Assert.Equal(2, displays[0].GetProperty("windows_display_number").GetInt32());
        Assert.Equal("Display 2", displays[0].GetProperty("name").GetString());
        Assert.Equal("display_2", displays[1].GetProperty("id").GetString());
        Assert.Equal(3, displays[1].GetProperty("windows_display_number").GetInt32());
        Assert.Equal("display_3", displays[2].GetProperty("id").GetString());
        Assert.Equal(1, displays[2].GetProperty("windows_display_number").GetInt32());
        Assert.DoesNotContain(@"\.\DISPLAY", displaysDocument.RootElement.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("stable_identity", displaysDocument.RootElement.ToString(), StringComparison.OrdinalIgnoreCase);
    }
}
