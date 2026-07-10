using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using AgentRecorder.Api;
using AgentRecorder.Core;
using AgentRecorder.Infrastructure;
using AgentRecorder.Logging;
using AgentRecorder.Windows;
using Xunit;

namespace AgentRecorder.Tests;

[Collection("HeadlessHostIntegration")]
public class QuickRecordingApiTests
{
    private sealed class ControllableTray : ITrayContext
    {
        public string HostMode => "headless";
        public bool SupportsRegionSelectionUi => true;

        public string RegionSelectionStatus { get; set; } = "display_unavailable";
        public int RegionX { get; set; }
        public int RegionY { get; set; }
        public int RegionW { get; set; } = 800;
        public int RegionH { get; set; } = 600;
        public string RegionDisplayId { get; set; } = "display_1";
        public string RegionCoordSpace { get; set; } = "virtual_screen";

        public int ConfirmationCallbackDelayMs { get; set; } = 0;
        public bool AutoApprove { get; set; } = false;

        public void RequestConfirmation(object summary, Action<ConfirmationDecision> callback)
        {
            var decision = AutoApprove ? ConfirmationDecision.Approve() : ConfirmationDecision.Reject();
            if (ConfirmationCallbackDelayMs > 0)
                _ = Task.Delay(ConfirmationCallbackDelayMs).ContinueWith(_ => callback(decision));
            else
                callback(decision);
        }

        public int RegionSelectionRequestCount { get; private set; }

        public void RequestRegionSelection(int timeoutSeconds,
            Action<string, int, int, int, int, string, string> callback)
        {
            RegionSelectionRequestCount++;
            callback(RegionSelectionStatus, RegionX, RegionY, RegionW, RegionH, RegionDisplayId, RegionCoordSpace);
        }

        public void SetRecording(object rec) { }
        public void SetIdle(object rec) { }
        public void SetAllIdle() { }
        public void ShowError(string text) { }
    }

    private static ApiServer CreateServer(ControllableTray tray, out string dataDir)
    {
        dataDir = Path.Combine(Path.GetTempPath(), $"quick-api-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dataDir);
        Environment.SetEnvironmentVariable("AGENT_RECORDER_DATA_DIR", dataDir, EnvironmentVariableTarget.Process);
        ApiKeyAuth.InitializeForTesting(dataDir);

        var audit = new AuditLogger();
        var engine = new RecordingEngine(audit);
        engine.SetTray(tray);
        return new ApiServer(engine, audit, tray);
    }

    private static void Cleanup(string dataDir)
    {
        SystemQuery.SetDisplayProvider(null);
        SystemQuery.SetActiveWindowProvider(null);
        SystemQuery.SetWindowProvider(null);
        try { if (Directory.Exists(dataDir)) Directory.Delete(dataDir, recursive: true); } catch { }
        Environment.SetEnvironmentVariable("AGENT_RECORDER_DATA_DIR", null, EnvironmentVariableTarget.Process);
        ApiKeyAuth.ResetForTesting(null);
    }

    private static HttpClient CreateClient()
    {
        var handler = new HttpClientHandler { UseProxy = false };
        return new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(5) };
    }

    private static StringContent JsonContent(string json) =>
        new(json, Encoding.UTF8, "application/json");

    [Fact]
    public async Task QuickRecording_MissingApiKey_Returns401()
    {
        var tray = new ControllableTray();
        var server = CreateServer(tray, out var dataDir);
        try
        {
            server.Start();
            using var client = CreateClient();
            var response = await client.PostAsync(
                $"http://127.0.0.1:{ApiServer.Port}/api/v1/recordings/quick",
                JsonContent("{\"target\":{\"type\":\"primary_display\"}}"));
            Assert.Equal(401, (int)response.StatusCode);
        }
        finally
        {
            server.Stop();
            Cleanup(dataDir);
        }
    }

    [Fact]
    public async Task QuickRecording_MissingTargetType_Returns400()
    {
        var tray = new ControllableTray();
        var server = CreateServer(tray, out var dataDir);
        try
        {
            server.Start();
            using var client = CreateClient();
            client.DefaultRequestHeaders.Add("X-Agent-Recorder-Key", ApiKeyAuth.CurrentApiKey);
            var response = await client.PostAsync(
                $"http://127.0.0.1:{ApiServer.Port}/api/v1/recordings/quick",
                JsonContent("{}"));
            Assert.Equal(400, (int)response.StatusCode);
            var body = await response.Content.ReadAsStringAsync();
            Assert.Contains("INVALID_ARGUMENT", body);
        }
        finally
        {
            server.Stop();
            Cleanup(dataDir);
        }
    }

    [Fact]
    public async Task QuickRecording_InvalidTargetType_Returns400()
    {
        var tray = new ControllableTray();
        var server = CreateServer(tray, out var dataDir);
        try
        {
            server.Start();
            using var client = CreateClient();
            client.DefaultRequestHeaders.Add("X-Agent-Recorder-Key", ApiKeyAuth.CurrentApiKey);
            var response = await client.PostAsync(
                $"http://127.0.0.1:{ApiServer.Port}/api/v1/recordings/quick",
                JsonContent("{\"target\":{\"type\":\"invalid_type\"}}"));
            Assert.Equal(400, (int)response.StatusCode);
            var body = await response.Content.ReadAsStringAsync();
            Assert.Contains("INVALID_ARGUMENT", body);
        }
        finally
        {
            server.Stop();
            Cleanup(dataDir);
        }
    }

    [Fact]
    public async Task QuickRecording_PrimaryDisplay_CreatesPendingConfirmation()
    {
        var tray = new ControllableTray();
        var server = CreateServer(tray, out var dataDir);
        try
        {
            SystemQuery.SetDisplayProvider(() => new List<SystemQuery.DisplayInfo>
            {
                new("display_1", "Display 1", true, new SystemQuery.Bounds(0, 0, 1920, 1080), 1.0)
            });

            server.Start();
            using var client = CreateClient();
            client.DefaultRequestHeaders.Add("X-Agent-Recorder-Key", ApiKeyAuth.CurrentApiKey);
            var response = await client.PostAsync(
                $"http://127.0.0.1:{ApiServer.Port}/api/v1/recordings/quick",
                JsonContent("{\"target\":{\"type\":\"primary_display\"},\"duration_seconds\":60}"));
            Assert.Equal(200, (int)response.StatusCode);

            var body = await response.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(body);
            var data = doc.RootElement.GetProperty("data");

            Assert.Equal("requires_user_confirmation", data.GetProperty("status").GetString());
            Assert.True(data.TryGetProperty("confirmation_id", out _));
            Assert.True(data.TryGetProperty("summary", out _));

            var quick = data.GetProperty("quick");
            Assert.Equal("primary_display", quick.GetProperty("target_type").GetString());
            Assert.True(quick.GetProperty("recording_created").GetBoolean());
            Assert.True(quick.GetProperty("requires_user_confirmation").GetBoolean());

            var resolved = quick.GetProperty("resolved_source");
            Assert.Equal("display", resolved.GetProperty("type").GetString());
            Assert.Equal("display_1", resolved.GetProperty("display_id").GetString());
        }
        finally
        {
            server.Stop();
            Cleanup(dataDir);
        }
    }

    [Fact]
    public async Task QuickRecording_PrimaryDisplay_NoDisplays_ReturnsSourceNotFound()
    {
        var tray = new ControllableTray();
        var server = CreateServer(tray, out var dataDir);
        try
        {
            SystemQuery.SetDisplayProvider(() => new List<SystemQuery.DisplayInfo>());

            server.Start();
            using var client = CreateClient();
            client.DefaultRequestHeaders.Add("X-Agent-Recorder-Key", ApiKeyAuth.CurrentApiKey);
            var response = await client.PostAsync(
                $"http://127.0.0.1:{ApiServer.Port}/api/v1/recordings/quick",
                JsonContent("{\"target\":{\"type\":\"primary_display\"}}"));
            Assert.Equal(400, (int)response.StatusCode);

            var body = await response.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(body);
            var error = doc.RootElement.GetProperty("error");
            Assert.Equal("SOURCE_NOT_FOUND", error.GetProperty("code").GetString());
        }
        finally
        {
            server.Stop();
            Cleanup(dataDir);
        }
    }

    [Fact]
    public async Task QuickRecording_ActiveWindow_NoWindow_ReturnsSourceNotFound()
    {
        var tray = new ControllableTray();
        var server = CreateServer(tray, out var dataDir);
        try
        {
            SystemQuery.SetActiveWindowProvider(() => null);

            server.Start();
            using var client = CreateClient();
            client.DefaultRequestHeaders.Add("X-Agent-Recorder-Key", ApiKeyAuth.CurrentApiKey);
            var response = await client.PostAsync(
                $"http://127.0.0.1:{ApiServer.Port}/api/v1/recordings/quick",
                JsonContent("{\"target\":{\"type\":\"active_window\"}}"));
            Assert.Equal(400, (int)response.StatusCode);

            var body = await response.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(body);
            var error = doc.RootElement.GetProperty("error");
            Assert.Equal("SOURCE_NOT_FOUND", error.GetProperty("code").GetString());
        }
        finally
        {
            server.Stop();
            Cleanup(dataDir);
        }
    }

    [Fact]
    public async Task QuickRecording_ActiveWindow_Resolves_CreatesWindowRecording()
    {
        var tray = new ControllableTray { AutoApprove = false };
        var server = CreateServer(tray, out var dataDir);
        try
        {
            SystemQuery.SetDisplayProvider(() => new List<SystemQuery.DisplayInfo>
            {
                new("display_1", "Display 1", true, new SystemQuery.Bounds(0, 0, 1920, 1080), 1.0)
            });
            SystemQuery.SetActiveWindowProvider(() => new SystemQuery.WindowInfo(
                "window_1234", "Active Test Window", "test.exe", 123,
                true, false,
                new SystemQuery.Bounds(100, 50, 801, 603)));
            SystemQuery.SetWindowProvider((includeMin, includeSys) => new List<SystemQuery.WindowInfo>
            {
                new("window_1234", "Active Test Window", "test.exe", 123,
                    true, false,
                    new SystemQuery.Bounds(100, 50, 801, 603))
            });

            server.Start();
            using var client = CreateClient();
            client.DefaultRequestHeaders.Add("X-Agent-Recorder-Key", ApiKeyAuth.CurrentApiKey);
            var response = await client.PostAsync(
                $"http://127.0.0.1:{ApiServer.Port}/api/v1/recordings/quick",
                JsonContent("{\"target\":{\"type\":\"active_window\"},\"duration_seconds\":10}"));
            Assert.Equal(200, (int)response.StatusCode);

            var body = await response.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(body);
            var data = doc.RootElement.GetProperty("data");

            Assert.Equal("requires_user_confirmation", data.GetProperty("status").GetString());

            var quick = data.GetProperty("quick");
            Assert.Equal("active_window", quick.GetProperty("target_type").GetString());
            Assert.True(quick.GetProperty("recording_created").GetBoolean());
            Assert.True(quick.GetProperty("requires_user_confirmation").GetBoolean());

            var resolved = quick.GetProperty("resolved_source");
            Assert.Equal("window", resolved.GetProperty("type").GetString());
            Assert.Equal("window_1234", resolved.GetProperty("window_id").GetString());
            Assert.Equal("Active Test Window", resolved.GetProperty("title").GetString());
            Assert.Equal(100, resolved.GetProperty("bounds").GetProperty("x").GetInt32());
            Assert.Equal(50, resolved.GetProperty("bounds").GetProperty("y").GetInt32());
            Assert.Equal(801, resolved.GetProperty("bounds").GetProperty("width").GetInt32());
            Assert.Equal(603, resolved.GetProperty("bounds").GetProperty("height").GetInt32());

            // capture_bounds reflects the clamped/normalized bounds actually used by the backend
            Assert.True(resolved.TryGetProperty("capture_bounds", out var captureBounds), "capture_bounds should be present");
            Assert.True(captureBounds.GetProperty("width").GetInt32() > 0);
            Assert.True(captureBounds.GetProperty("height").GetInt32() > 0);
        }
        finally
        {
            SystemQuery.SetDisplayProvider(null);
            SystemQuery.SetActiveWindowProvider(null);
            SystemQuery.SetWindowProvider(null);
            server.Stop();
            Cleanup(dataDir);
        }
    }

    [Fact]
    public async Task QuickRecording_SelectedRegion_Selected_CreatesRecordingWithResolvedSource()
    {
        var tray = new ControllableTray
        {
            RegionSelectionStatus = "selected",
            RegionX = 100,
            RegionY = 150,
            RegionW = 800,
            RegionH = 600,
            RegionDisplayId = "display_1",
            RegionCoordSpace = "virtual_screen"
        };
        var server = CreateServer(tray, out var dataDir);
        try
        {
            SystemQuery.SetDisplayProvider(() => new List<SystemQuery.DisplayInfo>
            {
                new("display_1", "Display 1", true, new SystemQuery.Bounds(0, 0, 1920, 1080), 1.0)
            });

            server.Start();
            using var client = CreateClient();
            client.DefaultRequestHeaders.Add("X-Agent-Recorder-Key", ApiKeyAuth.CurrentApiKey);
            var response = await client.PostAsync(
                $"http://127.0.0.1:{ApiServer.Port}/api/v1/recordings/quick",
                JsonContent("{\"target\":{\"type\":\"selected_region\",\"selection_timeout_seconds\":30},\"duration_seconds\":120}"));
            Assert.Equal(200, (int)response.StatusCode);

            var body = await response.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(body);
            var data = doc.RootElement.GetProperty("data");

            Assert.Equal("requires_user_confirmation", data.GetProperty("status").GetString());

            var quick = data.GetProperty("quick");
            Assert.Equal("selected_region", quick.GetProperty("target_type").GetString());
            Assert.True(quick.GetProperty("recording_created").GetBoolean());
            Assert.True(quick.GetProperty("requires_user_confirmation").GetBoolean());

            var resolved = quick.GetProperty("resolved_source");
            Assert.Equal("region", resolved.GetProperty("type").GetString());
            Assert.Equal("display_1", resolved.GetProperty("display_id").GetString());
            Assert.Equal("virtual_screen", resolved.GetProperty("coordinate_space").GetString());

            var bounds = resolved.GetProperty("bounds");
            Assert.Equal(100, bounds.GetProperty("x").GetInt32());
            Assert.Equal(150, bounds.GetProperty("y").GetInt32());
            Assert.Equal(800, bounds.GetProperty("width").GetInt32());
            Assert.Equal(600, bounds.GetProperty("height").GetInt32());
        }
        finally
        {
            server.Stop();
            Cleanup(dataDir);
        }
    }

    [Theory]
    [InlineData("selection_cancelled")]
    [InlineData("selection_timeout")]
    [InlineData("display_unavailable")]
    [InlineData("selection_failed")]
    public async Task QuickRecording_SelectedRegion_NotSelected_DoesNotCreateRecording(string status)
    {
        var tray = new ControllableTray { RegionSelectionStatus = status };
        var server = CreateServer(tray, out var dataDir);
        try
        {
            server.Start();
            using var client = CreateClient();
            client.DefaultRequestHeaders.Add("X-Agent-Recorder-Key", ApiKeyAuth.CurrentApiKey);
            var response = await client.PostAsync(
                $"http://127.0.0.1:{ApiServer.Port}/api/v1/recordings/quick",
                JsonContent("{\"target\":{\"type\":\"selected_region\"}}"));
            Assert.Equal(200, (int)response.StatusCode);

            var body = await response.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(body);
            Assert.True(doc.RootElement.GetProperty("ok").GetBoolean());

            var data = doc.RootElement.GetProperty("data");
            Assert.Equal(status, data.GetProperty("status").GetString());

            var quick = data.GetProperty("quick");
            Assert.Equal("selected_region", quick.GetProperty("target_type").GetString());
            Assert.False(quick.GetProperty("recording_created").GetBoolean());
        }
        finally
        {
            server.Stop();
            Cleanup(dataDir);
        }
    }

    [Fact]
    public async Task ConfirmationApprove_StillReturns405()
    {
        var tray = new ControllableTray();
        var server = CreateServer(tray, out var dataDir);
        try
        {
            server.Start();
            using var client = CreateClient();
            client.DefaultRequestHeaders.Add("X-Agent-Recorder-Key", ApiKeyAuth.CurrentApiKey);
            var response = await client.PostAsync(
                $"http://127.0.0.1:{ApiServer.Port}/api/v1/confirmations/test-id/approve",
                JsonContent("{}"));
            Assert.Equal(405, (int)response.StatusCode);
            var body = await response.Content.ReadAsStringAsync();
            Assert.Contains("METHOD_NOT_ALLOWED", body);
        }
        finally
        {
            server.Stop();
            Cleanup(dataDir);
        }
    }

    [Fact]
    public async Task Capabilities_ContainsQuickRecordingInfo()
    {
        var tray = new ControllableTray();
        var server = CreateServer(tray, out var dataDir);
        try
        {
            server.Start();
            using var client = CreateClient();
            var response = await client.GetAsync(
                $"http://127.0.0.1:{ApiServer.Port}/api/v1/capabilities");
            Assert.Equal(200, (int)response.StatusCode);

            var body = await response.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(body);
            var data = doc.RootElement.GetProperty("data");
            var interaction = data.GetProperty("interaction");

            Assert.Equal("0.1.2", data.GetProperty("app").GetProperty("version").GetString());

            Assert.Equal("/api/v1/recordings/quick", interaction.GetProperty("quick_recording_endpoint").GetString());
            Assert.True(interaction.GetProperty("quick_recording_supported").GetBoolean());

            var recipes = interaction.GetProperty("quick_recipes");
            Assert.Equal(4, recipes.GetArrayLength());

            var recipeNames = new List<string>();
            foreach (var r in recipes.EnumerateArray())
                recipeNames.Add(r.GetProperty("name").GetString()!);

            Assert.Contains("record_primary_display", recipeNames);
            Assert.Contains("record_active_window", recipeNames);
            Assert.Contains("record_selected_region", recipeNames);
            Assert.Contains("record_last_region", recipeNames);
        }
        finally
        {
            server.Stop();
            Cleanup(dataDir);
        }
    }

    [Fact]
    public async Task QuickRecording_NestedRoleOuter_PassesThrough()
    {
        var tray = new ControllableTray();
        var server = CreateServer(tray, out var dataDir);
        try
        {
            SystemQuery.SetDisplayProvider(() => new List<SystemQuery.DisplayInfo>
            {
                new("display_1", "Display 1", true, new SystemQuery.Bounds(0, 0, 1920, 1080), 1.0)
            });

            server.Start();
            using var client = CreateClient();
            client.DefaultRequestHeaders.Add("X-Agent-Recorder-Key", ApiKeyAuth.CurrentApiKey);
            var response = await client.PostAsync(
                $"http://127.0.0.1:{ApiServer.Port}/api/v1/recordings/quick",
                JsonContent("{\"target\":{\"type\":\"primary_display\"},\"nested\":{\"role\":\"outer\",\"session_id\":\"test-session\"},\"duration_seconds\":60}"));
            Assert.Equal(200, (int)response.StatusCode);

            var body = await response.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(body);
            var summary = doc.RootElement.GetProperty("data").GetProperty("summary");
            Assert.Equal("outer", summary.GetProperty("nested_role").GetString());
        }
        finally
        {
            server.Stop();
            Cleanup(dataDir);
        }
    }

    [Fact]
    public async Task CreateRecording_SummaryContainsMetadataFields()
    {
        var tray = new ControllableTray();
        var server = CreateServer(tray, out var dataDir);
        try
        {
            SystemQuery.SetDisplayProvider(() => new List<SystemQuery.DisplayInfo>
            {
                new("display_1", "Display 1", true, new SystemQuery.Bounds(0, 0, 1920, 1080), 1.0)
            });

            server.Start();
            using var client = CreateClient();
            client.DefaultRequestHeaders.Add("X-Agent-Recorder-Key", ApiKeyAuth.CurrentApiKey);
            var response = await client.PostAsync(
                $"http://127.0.0.1:{ApiServer.Port}/api/v1/recordings/quick",
                JsonContent("{\"target\":{\"type\":\"primary_display\"},\"duration_seconds\":60}"));
            Assert.Equal(200, (int)response.StatusCode);

            var body = await response.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(body);
            var data = doc.RootElement.GetProperty("data");

            Assert.Equal("requires_user_confirmation", data.GetProperty("status").GetString());
            Assert.True(data.TryGetProperty("confirmation_id", out _));
            Assert.True(data.TryGetProperty("summary", out var summary));

            // Verify summary metadata fields
            Assert.True(summary.TryGetProperty("recording_id", out _), "summary.recording_id missing");
            Assert.True(summary.TryGetProperty("confirmation_id", out _), "summary.confirmation_id missing");
            Assert.True(summary.TryGetProperty("timeout_seconds", out _), "summary.timeout_seconds missing");
            Assert.True(summary.TryGetProperty("expires_at", out _), "summary.expires_at missing");

            var confId = data.GetProperty("confirmation_id").GetString();
            var summaryConfId = summary.GetProperty("confirmation_id").GetString();
            Assert.Equal(confId, summaryConfId);

            Assert.True(summary.GetProperty("timeout_seconds").GetInt32() > 0);
            Assert.Contains("T", summary.GetProperty("expires_at").GetString()!);
        }
        finally
        {
            server.Stop();
            Cleanup(dataDir);
        }
    }

    [Fact]
    public async Task Capabilities_ContextDisplays_ContainsAllFields()
    {
        var tray = new ControllableTray();
        var server = CreateServer(tray, out var dataDir);
        try
        {
            SystemQuery.SetDisplayProvider(() => new List<SystemQuery.DisplayInfo>
            {
                new("display_1", "Primary Display", true, new SystemQuery.Bounds(0, 0, 1920, 1080), 1.0),
                new("display_2", "Secondary Display", false, new SystemQuery.Bounds(-1920, 0, 1920, 1080), 1.25)
            });

            server.Start();
            using var client = CreateClient();
            var response = await client.GetAsync($"http://127.0.0.1:{ApiServer.Port}/api/v1/capabilities");
            Assert.Equal(200, (int)response.StatusCode);

            var body = await response.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(body);
            var context = doc.RootElement.GetProperty("data").GetProperty("context");
            var displays = context.GetProperty("displays");

            Assert.True(displays.GetProperty("available").GetBoolean());
            Assert.Equal(2, displays.GetProperty("count").GetInt32());
            Assert.Equal("display_1", displays.GetProperty("primary_display_id").GetString());

            var virtualBounds = displays.GetProperty("virtual_bounds");
            Assert.Equal(-1920, virtualBounds.GetProperty("x").GetInt32());
            Assert.Equal(0, virtualBounds.GetProperty("y").GetInt32());
            Assert.Equal(3840, virtualBounds.GetProperty("width").GetInt32());
            Assert.Equal(1080, virtualBounds.GetProperty("height").GetInt32());

            var items = displays.GetProperty("items");
            Assert.Equal(2, items.GetArrayLength());

            var firstDisplay = items[0];
            Assert.Equal("display_1", firstDisplay.GetProperty("id").GetString());
            Assert.Equal("Primary Display", firstDisplay.GetProperty("name").GetString());
            Assert.True(firstDisplay.GetProperty("is_primary").GetBoolean());

            Assert.Null(displays.GetProperty("error").GetString());
        }
        finally
        {
            server.Stop();
            Cleanup(dataDir);
        }
    }

    [Fact]
    public async Task Capabilities_ContextWindows_ActiveWindowFirstInSample()
    {
        var tray = new ControllableTray();
        var server = CreateServer(tray, out var dataDir);
        try
        {
            var activeWindow = new SystemQuery.WindowInfo(
                "window_1", "Active Window", "app1.exe", 1001,
                true, false, new SystemQuery.Bounds(10, 20, 1200, 800));

            SystemQuery.SetActiveWindowProvider(() => activeWindow);
            SystemQuery.SetWindowProvider((_, _) => new List<SystemQuery.WindowInfo>
            {
                new("window_2", "Other Window 1", "app2.exe", 1002, false, false, new SystemQuery.Bounds(0, 0, 800, 600)),
                activeWindow,
                new("window_3", "Other Window 2", "app3.exe", 1003, false, false, new SystemQuery.Bounds(0, 0, 600, 400))
            });

            server.Start();
            using var client = CreateClient();
            var response = await client.GetAsync($"http://127.0.0.1:{ApiServer.Port}/api/v1/capabilities");
            Assert.Equal(200, (int)response.StatusCode);

            var body = await response.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(body);
            var context = doc.RootElement.GetProperty("data").GetProperty("context");
            var windows = context.GetProperty("windows");

            Assert.True(windows.GetProperty("available").GetBoolean());
            Assert.Equal(3, windows.GetProperty("visible_count").GetInt32());
            Assert.Equal(10, windows.GetProperty("sample_limit").GetInt32());

            var active = windows.GetProperty("active");
            Assert.Equal("window_1", active.GetProperty("id").GetString());
            Assert.Equal("Active Window", active.GetProperty("title").GetString());
            Assert.Equal("app1.exe", active.GetProperty("app_name").GetString());

            var itemsSample = windows.GetProperty("items_sample");
            Assert.Equal(3, itemsSample.GetArrayLength());
            Assert.Equal("window_1", itemsSample[0].GetProperty("id").GetString());
            Assert.True(itemsSample[0].GetProperty("is_active").GetBoolean());

            Assert.Null(windows.GetProperty("error").GetString());
        }
        finally
        {
            server.Stop();
            Cleanup(dataDir);
        }
    }

    [Fact]
    public async Task Capabilities_ContextWindows_ActiveWindowNotInEnum_FirstInSample()
    {
        var tray = new ControllableTray();
        var server = CreateServer(tray, out var dataDir);
        try
        {
            var activeWindow = new SystemQuery.WindowInfo(
                "window_active", "Active Window", "active.exe", 9999,
                true, false, new SystemQuery.Bounds(10, 20, 1200, 800));

            var enumWindows = new List<SystemQuery.WindowInfo>
            {
                new("window_2", "Other Window 1", "app2.exe", 1002, false, false, new SystemQuery.Bounds(0, 0, 800, 600)),
                new("window_3", "Other Window 2", "app3.exe", 1003, false, false, new SystemQuery.Bounds(0, 0, 600, 400)),
                new("window_4", "Other Window 3", "app4.exe", 1004, false, false, new SystemQuery.Bounds(0, 0, 500, 300))
            };

            SystemQuery.SetActiveWindowProvider(() => activeWindow);
            SystemQuery.SetWindowProvider((_, _) => enumWindows);

            server.Start();
            using var client = CreateClient();
            var response = await client.GetAsync($"http://127.0.0.1:{ApiServer.Port}/api/v1/capabilities");
            Assert.Equal(200, (int)response.StatusCode);

            var body = await response.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(body);
            var context = doc.RootElement.GetProperty("data").GetProperty("context");
            var windows = context.GetProperty("windows");

            Assert.True(windows.GetProperty("available").GetBoolean());
            Assert.Equal(3, windows.GetProperty("visible_count").GetInt32());

            var active = windows.GetProperty("active");
            Assert.Equal("window_active", active.GetProperty("id").GetString());

            var itemsSample = windows.GetProperty("items_sample");
            Assert.True(itemsSample.GetArrayLength() <= 10);
            Assert.Equal(4, itemsSample.GetArrayLength());
            Assert.Equal("window_active", itemsSample[0].GetProperty("id").GetString());
            Assert.True(itemsSample[0].GetProperty("is_active").GetBoolean());

            Assert.Null(windows.GetProperty("error").GetString());
        }
        finally
        {
            server.Stop();
            Cleanup(dataDir);
        }
    }

    [Fact]
    public async Task Capabilities_ContextWindows_EnumFailsActiveSucceeds_AvailableWithError()
    {
        var tray = new ControllableTray();
        var server = CreateServer(tray, out var dataDir);
        try
        {
            var activeWindow = new SystemQuery.WindowInfo(
                "window_active", "Active Window", "active.exe", 9999,
                true, false, new SystemQuery.Bounds(10, 20, 1200, 800));

            SystemQuery.SetActiveWindowProvider(() => activeWindow);
            SystemQuery.SetWindowProvider((_, _) => throw new Exception("enum windows failed"));

            server.Start();
            using var client = CreateClient();
            var response = await client.GetAsync($"http://127.0.0.1:{ApiServer.Port}/api/v1/capabilities");
            Assert.Equal(200, (int)response.StatusCode);

            var body = await response.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(body);
            var context = doc.RootElement.GetProperty("data").GetProperty("context");
            var windows = context.GetProperty("windows");

            Assert.True(windows.GetProperty("available").GetBoolean());

            var active = windows.GetProperty("active");
            Assert.NotEqual(JsonValueKind.Null, active.ValueKind);
            Assert.Equal("window_active", active.GetProperty("id").GetString());

            Assert.Equal(0, windows.GetProperty("visible_count").GetInt32());

            var itemsSample = windows.GetProperty("items_sample");
            Assert.Equal(1, itemsSample.GetArrayLength());
            Assert.Equal("window_active", itemsSample[0].GetProperty("id").GetString());
            Assert.True(itemsSample[0].GetProperty("is_active").GetBoolean());

            var error = windows.GetProperty("error").GetString();
            Assert.NotNull(error);
            Assert.Contains("enum windows", error, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            server.Stop();
            Cleanup(dataDir);
        }
    }

    [Fact]
    public async Task Capabilities_ProviderException_Returns200WithError()
    {
        var tray = new ControllableTray();
        var server = CreateServer(tray, out var dataDir);
        try
        {
            SystemQuery.SetDisplayProvider(() => throw new Exception("test display error"));
            SystemQuery.SetActiveWindowProvider(() => throw new Exception("test active window error"));
            SystemQuery.SetWindowProvider((_, _) => throw new Exception("test window error"));

            server.Start();
            using var client = CreateClient();
            var response = await client.GetAsync($"http://127.0.0.1:{ApiServer.Port}/api/v1/capabilities");
            Assert.Equal(200, (int)response.StatusCode);

            var body = await response.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(body);
            var context = doc.RootElement.GetProperty("data").GetProperty("context");

            var displays = context.GetProperty("displays");
            Assert.False(displays.GetProperty("available").GetBoolean());
            Assert.Equal(0, displays.GetProperty("count").GetInt32());
            Assert.Equal("test display error", displays.GetProperty("error").GetString());

            var windows = context.GetProperty("windows");
            Assert.False(windows.GetProperty("available").GetBoolean());
            Assert.Equal(0, windows.GetProperty("visible_count").GetInt32());
            var winError = windows.GetProperty("error").GetString();
            Assert.NotNull(winError);
            Assert.Contains("active window", winError, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("test window error", winError, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            server.Stop();
            Cleanup(dataDir);
        }
    }

    [Fact]
    public async Task Capabilities_LastSelectedRegion_UpdatedAfterQuickSelectedRegion()
    {
        var tray = new ControllableTray
        {
            RegionSelectionStatus = "selected",
            RegionX = 100,
            RegionY = 150,
            RegionW = 800,
            RegionH = 600,
            RegionDisplayId = "display_1",
            RegionCoordSpace = "virtual_screen"
        };
        var server = CreateServer(tray, out var dataDir);
        try
        {
            SystemQuery.SetDisplayProvider(() => new List<SystemQuery.DisplayInfo>
            {
                new("display_1", "Display 1", true, new SystemQuery.Bounds(0, 0, 1920, 1080), 1.0)
            });

            server.Start();
            using var client = CreateClient();
            client.DefaultRequestHeaders.Add("X-Agent-Recorder-Key", ApiKeyAuth.CurrentApiKey);

            var createResponse = await client.PostAsync(
                $"http://127.0.0.1:{ApiServer.Port}/api/v1/recordings/quick",
                JsonContent("{\"target\":{\"type\":\"selected_region\",\"selection_timeout_seconds\":30},\"duration_seconds\":120}"));
            Assert.Equal(200, (int)createResponse.StatusCode);

            var capsResponse = await client.GetAsync($"http://127.0.0.1:{ApiServer.Port}/api/v1/capabilities");
            Assert.Equal(200, (int)capsResponse.StatusCode);

            var body = await capsResponse.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(body);
            var context = doc.RootElement.GetProperty("data").GetProperty("context");
            var lastRegion = context.GetProperty("last_selected_region");

            Assert.True(lastRegion.GetProperty("available").GetBoolean());
            Assert.Equal("display_1", lastRegion.GetProperty("display_id").GetString());
            Assert.Equal("virtual_screen", lastRegion.GetProperty("coordinate_space").GetString());
            Assert.Equal("quick_selected_region", lastRegion.GetProperty("source").GetString());

            var bounds = lastRegion.GetProperty("bounds");
            Assert.Equal(100, bounds.GetProperty("x").GetInt32());
            Assert.Equal(150, bounds.GetProperty("y").GetInt32());
            Assert.Equal(800, bounds.GetProperty("width").GetInt32());
            Assert.Equal(600, bounds.GetProperty("height").GetInt32());
        }
        finally
        {
            server.Stop();
            Cleanup(dataDir);
        }
    }

    [Fact]
    public async Task QuickRecording_LastRegion_NoState_ReturnsSourceNotFound()
    {
        var tray = new ControllableTray();
        var server = CreateServer(tray, out var dataDir);
        try
        {
            server.Start();
            using var client = CreateClient();
            client.DefaultRequestHeaders.Add("X-Agent-Recorder-Key", ApiKeyAuth.CurrentApiKey);
            var response = await client.PostAsync(
                $"http://127.0.0.1:{ApiServer.Port}/api/v1/recordings/quick",
                JsonContent("{\"target\":{\"type\":\"last_region\"},\"duration_seconds\":60}"));
            Assert.Equal(404, (int)response.StatusCode);

            var body = await response.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(body);
            Assert.False(doc.RootElement.GetProperty("ok").GetBoolean());

            var error = doc.RootElement.GetProperty("error");
            Assert.Equal("SOURCE_NOT_FOUND", error.GetProperty("code").GetString());
            Assert.Equal("use_selected_region_first", error.GetProperty("details").GetProperty("suggested_action").GetString());
        }
        finally
        {
            server.Stop();
            Cleanup(dataDir);
        }
    }

    [Fact]
    public async Task QuickRecording_LastRegion_UsesSavedRegionWithoutOpeningSelectionUi()
    {
        var tray = new ControllableTray
        {
            RegionSelectionStatus = "selected",
            RegionX = 100,
            RegionY = 150,
            RegionW = 800,
            RegionH = 600,
            RegionDisplayId = "display_1",
            RegionCoordSpace = "virtual_screen"
        };
        var server = CreateServer(tray, out var dataDir);
        try
        {
            SystemQuery.SetDisplayProvider(() => new List<SystemQuery.DisplayInfo>
            {
                new("display_1", "Display 1", true, new SystemQuery.Bounds(0, 0, 1920, 1080), 1.0)
            });

            server.Start();
            using var client = CreateClient();
            client.DefaultRequestHeaders.Add("X-Agent-Recorder-Key", ApiKeyAuth.CurrentApiKey);

            // First call selected_region to establish the persisted last region.
            var selectedResponse = await client.PostAsync(
                $"http://127.0.0.1:{ApiServer.Port}/api/v1/recordings/quick",
                JsonContent("{\"target\":{\"type\":\"selected_region\",\"selection_timeout_seconds\":30},\"duration_seconds\":120}"));
            Assert.Equal(200, (int)selectedResponse.StatusCode);

            var initialRequestCount = tray.RegionSelectionRequestCount;

            // Now call last_region - it should not open the selection UI again.
            var lastRegionResponse = await client.PostAsync(
                $"http://127.0.0.1:{ApiServer.Port}/api/v1/recordings/quick",
                JsonContent("{\"target\":{\"type\":\"last_region\"},\"duration_seconds\":60}"));
            Assert.Equal(200, (int)lastRegionResponse.StatusCode);

            Assert.Equal(initialRequestCount, tray.RegionSelectionRequestCount);

            var body = await lastRegionResponse.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(body);
            var data = doc.RootElement.GetProperty("data");

            Assert.Equal("requires_user_confirmation", data.GetProperty("status").GetString());

            var quick = data.GetProperty("quick");
            Assert.Equal("last_region", quick.GetProperty("target_type").GetString());
            Assert.True(quick.GetProperty("recording_created").GetBoolean());
            Assert.True(quick.GetProperty("requires_user_confirmation").GetBoolean());

            var resolved = quick.GetProperty("resolved_source");
            Assert.Equal("region", resolved.GetProperty("type").GetString());
            Assert.Equal("display_1", resolved.GetProperty("display_id").GetString());
            Assert.Equal("virtual_screen", resolved.GetProperty("coordinate_space").GetString());

            var bounds = resolved.GetProperty("bounds");
            Assert.Equal(100, bounds.GetProperty("x").GetInt32());
            Assert.Equal(150, bounds.GetProperty("y").GetInt32());
            Assert.Equal(800, bounds.GetProperty("width").GetInt32());
            Assert.Equal(600, bounds.GetProperty("height").GetInt32());
        }
        finally
        {
            server.Stop();
            Cleanup(dataDir);
        }
    }

    [Fact]
    public async Task Capabilities_LastRegionRecipe_AvailabilityReflectsState()
    {
        var tray = new ControllableTray
        {
            RegionSelectionStatus = "selected",
            RegionX = 100,
            RegionY = 150,
            RegionW = 800,
            RegionH = 600,
            RegionDisplayId = "display_1",
            RegionCoordSpace = "virtual_screen"
        };
        var server = CreateServer(tray, out var dataDir);
        try
        {
            server.Start();
            using var client = CreateClient();

            // Before any selection, record_last_region should be unavailable.
            var beforeResponse = await client.GetAsync($"http://127.0.0.1:{ApiServer.Port}/api/v1/capabilities");
            Assert.Equal(200, (int)beforeResponse.StatusCode);
            var beforeBody = await beforeResponse.Content.ReadAsStringAsync();
            using var beforeDoc = JsonDocument.Parse(beforeBody);
            var beforeRecipes = beforeDoc.RootElement.GetProperty("data").GetProperty("interaction").GetProperty("quick_recipes");
            var beforeLastRegionRecipe = FindRecipe(beforeRecipes, "record_last_region");
            Assert.False(beforeLastRegionRecipe.GetProperty("available").GetBoolean());
            Assert.Equal("no_last_selected_region", beforeLastRegionRecipe.GetProperty("unavailable_reason").GetString());

            // Create a selection to persist last region.
            client.DefaultRequestHeaders.Add("X-Agent-Recorder-Key", ApiKeyAuth.CurrentApiKey);
            var createResponse = await client.PostAsync(
                $"http://127.0.0.1:{ApiServer.Port}/api/v1/recordings/quick",
                JsonContent("{\"target\":{\"type\":\"selected_region\",\"selection_timeout_seconds\":30},\"duration_seconds\":120}"));
            Assert.Equal(200, (int)createResponse.StatusCode);

            // After selection, record_last_region should be available.
            var afterResponse = await client.GetAsync($"http://127.0.0.1:{ApiServer.Port}/api/v1/capabilities");
            Assert.Equal(200, (int)afterResponse.StatusCode);
            var afterBody = await afterResponse.Content.ReadAsStringAsync();
            using var afterDoc = JsonDocument.Parse(afterBody);
            var afterRecipes = afterDoc.RootElement.GetProperty("data").GetProperty("interaction").GetProperty("quick_recipes");
            var afterLastRegionRecipe = FindRecipe(afterRecipes, "record_last_region");
            Assert.True(afterLastRegionRecipe.GetProperty("available").GetBoolean());
            Assert.Null(afterLastRegionRecipe.GetProperty("unavailable_reason").GetString());
        }
        finally
        {
            server.Stop();
            Cleanup(dataDir);
        }
    }

    [Fact]
    public async Task Capabilities_LastSelectedRegion_LoadsFromPersistedStateAfterRestart()
    {
        var tray = new ControllableTray
        {
            RegionSelectionStatus = "selected",
            RegionX = 100,
            RegionY = 150,
            RegionW = 800,
            RegionH = 600,
            RegionDisplayId = "display_1",
            RegionCoordSpace = "virtual_screen"
        };
        var server = CreateServer(tray, out var dataDir);
        try
        {
            SystemQuery.SetDisplayProvider(() => new List<SystemQuery.DisplayInfo>
            {
                new("display_1", "Display 1", true, new SystemQuery.Bounds(0, 0, 1920, 1080), 1.0)
            });

            server.Start();
            using var client = CreateClient();
            client.DefaultRequestHeaders.Add("X-Agent-Recorder-Key", ApiKeyAuth.CurrentApiKey);

            var createResponse = await client.PostAsync(
                $"http://127.0.0.1:{ApiServer.Port}/api/v1/recordings/quick",
                JsonContent("{\"target\":{\"type\":\"selected_region\",\"selection_timeout_seconds\":30},\"duration_seconds\":120}"));
            Assert.Equal(200, (int)createResponse.StatusCode);

            server.Stop();

            // Create a new server instance with the same data dir.
            var audit = new AuditLogger();
            var engine = new RecordingEngine(audit);
            engine.SetTray(tray);
            var newServer = new ApiServer(engine, audit, tray);
            newServer.Start();
            try
            {
                using var newClient = CreateClient();
                var capsResponse = await newClient.GetAsync($"http://127.0.0.1:{ApiServer.Port}/api/v1/capabilities");
                Assert.Equal(200, (int)capsResponse.StatusCode);

                var body = await capsResponse.Content.ReadAsStringAsync();
                using var doc = JsonDocument.Parse(body);
                var lastRegion = doc.RootElement.GetProperty("data").GetProperty("context").GetProperty("last_selected_region");

                Assert.True(lastRegion.GetProperty("available").GetBoolean());
                Assert.Equal("display_1", lastRegion.GetProperty("display_id").GetString());
                Assert.Equal("virtual_screen", lastRegion.GetProperty("coordinate_space").GetString());

                var bounds = lastRegion.GetProperty("bounds");
                Assert.Equal(100, bounds.GetProperty("x").GetInt32());
                Assert.Equal(150, bounds.GetProperty("y").GetInt32());
                Assert.Equal(800, bounds.GetProperty("width").GetInt32());
                Assert.Equal(600, bounds.GetProperty("height").GetInt32());
            }
            finally
            {
                newServer.Stop();
            }
        }
        finally
        {
            server.Stop();
            Cleanup(dataDir);
        }
    }

    [Fact]
    public async Task Capabilities_QuickRecipes_EnhancedWithNewFields()
    {
        var tray = new ControllableTray();
        var server = CreateServer(tray, out var dataDir);
        try
        {
            SystemQuery.SetDisplayProvider(() => new List<SystemQuery.DisplayInfo>
            {
                new("display_1", "Display 1", true, new SystemQuery.Bounds(0, 0, 1920, 1080), 1.0)
            });
            SystemQuery.SetActiveWindowProvider(() => new SystemQuery.WindowInfo(
                "window_1", "Test Window", "test.exe", 123,
                true, false, new SystemQuery.Bounds(0, 0, 800, 600)));

            server.Start();
            using var client = CreateClient();
            var response = await client.GetAsync($"http://127.0.0.1:{ApiServer.Port}/api/v1/capabilities");
            Assert.Equal(200, (int)response.StatusCode);

            var body = await response.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(body);
            var recipes = doc.RootElement.GetProperty("data").GetProperty("interaction").GetProperty("quick_recipes");

            Assert.Equal(4, recipes.GetArrayLength());

            foreach (var recipe in recipes.EnumerateArray())
            {
                Assert.True(recipe.TryGetProperty("endpoint", out _));
                Assert.True(recipe.TryGetProperty("method", out _));
                Assert.True(recipe.TryGetProperty("request_template", out _));
                Assert.True(recipe.TryGetProperty("available", out _));
                Assert.True(recipe.TryGetProperty("unavailable_reason", out _));

                Assert.Equal("/api/v1/recordings/quick", recipe.GetProperty("endpoint").GetString());
                Assert.Equal("POST", recipe.GetProperty("method").GetString());

                var reqTemplate = recipe.GetProperty("request_template");
                Assert.True(reqTemplate.TryGetProperty("target", out _));
                Assert.Equal(60, reqTemplate.GetProperty("duration_seconds").GetInt32());
            }

            var primaryRecipe = recipes.EnumerateArray().First(r => r.GetProperty("name").GetString() == "record_primary_display");
            Assert.True(primaryRecipe.GetProperty("available").GetBoolean());
            Assert.Null(primaryRecipe.GetProperty("unavailable_reason").GetString());

            var activeRecipe = recipes.EnumerateArray().First(r => r.GetProperty("name").GetString() == "record_active_window");
            Assert.True(activeRecipe.GetProperty("available").GetBoolean());
            Assert.Null(activeRecipe.GetProperty("unavailable_reason").GetString());

            var regionRecipe = recipes.EnumerateArray().First(r => r.GetProperty("name").GetString() == "record_selected_region");
            Assert.True(regionRecipe.GetProperty("available").GetBoolean());
            Assert.Null(regionRecipe.GetProperty("unavailable_reason").GetString());
        }
        finally
        {
            server.Stop();
            Cleanup(dataDir);
        }
    }

    private static JsonElement FindRecipe(JsonElement recipes, string name)
    {
        foreach (var recipe in recipes.EnumerateArray())
        {
            if (recipe.GetProperty("name").GetString() == name)
                return recipe;
        }
        throw new InvalidOperationException($"Recipe '{name}' not found in quick_recipes.");
    }
}
