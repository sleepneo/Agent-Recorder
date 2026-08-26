using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using AgentRecorder.Api;
using AgentRecorder.Capture;
using AgentRecorder.Core;
using AgentRecorder.Infrastructure;
using AgentRecorder.Logging;
using Xunit;

namespace AgentRecorder.Tests;

/// <summary>
/// Regression tests for AirPods re-enumeration scenarios: the enumeration cache
/// must not hold no_devices for long, the fresh CoreAudio/dshow merge must not
/// return stale devices, and an id returned by the API must round-trip into a
/// recording request unchanged.
/// </summary>
public class DeviceEnumerationFreshnessTests
{
    private const string AirPodsDshowId = "@device_cm_{33D9A762-90C8-11D0-BD43-00A0C911CE86}\\wave_{1A2B3C4D-5E6F-7A8B-9C0D-1E2F3A4B5C6D}";

    private sealed class MutableProvider : IMicrophoneDeviceProvider
    {
        private IReadOnlyList<MicrophoneDeviceInfo> _devices;
        public int CallCount { get; private set; }

        public MutableProvider(IReadOnlyList<MicrophoneDeviceInfo> devices)
        {
            _devices = devices;
        }

        public void SetDevices(IReadOnlyList<MicrophoneDeviceInfo> devices) => _devices = devices;

        public Task<IReadOnlyList<MicrophoneDeviceInfo>> GetDevicesAsync(CancellationToken cancellationToken = default)
        {
            CallCount++;
            return Task.FromResult(_devices);
        }
    }

    private sealed class StubStatusProvider : IMicrophoneStatusProvider
    {
        private readonly Func<string, MicrophoneStatus> _query;
        public StubStatusProvider(Func<string, MicrophoneStatus> query) => _query = query;
        public Task<MicrophoneStatus> GetStatusAsync(string deviceId, CancellationToken cancellationToken = default)
            => Task.FromResult(_query(deviceId));
    }

    // -----------------------------------------------------------------
    // 1. The cache must not hold an empty (no_devices) result for the full TTL
    // -----------------------------------------------------------------

    [Fact]
    public async Task EmptyResult_ExpiresMuchFasterThanFullTtl()
    {
        var inner = new MutableProvider(Array.Empty<MicrophoneDeviceInfo>());
        var cache = new CachingMicrophoneDeviceProvider(inner, ttl: TimeSpan.FromSeconds(30), emptyResultTtl: TimeSpan.FromMilliseconds(20));

        var first = await cache.GetDevicesAsync();
        Assert.Empty(first);
        Assert.Equal(1, inner.CallCount);

        // A device appears (AirPods reconnect). Within the full TTL but past the
        // empty-result TTL, the next call must re-enumerate and see it.
        inner.SetDevices(new[] { new MicrophoneDeviceInfo(AirPodsDshowId, "AirPods", null, null) });
        await Task.Delay(60);
        var second = await cache.GetDevicesAsync();

        Assert.Equal(2, inner.CallCount);
        Assert.Single(second);
    }

    [Fact]
    public async Task NonEmptyResult_StillUsesFullTtl()
    {
        var device = new MicrophoneDeviceInfo(AirPodsDshowId, "AirPods", null, null);
        var inner = new MutableProvider(new[] { device });
        var cache = new CachingMicrophoneDeviceProvider(inner, ttl: TimeSpan.FromSeconds(30), emptyResultTtl: TimeSpan.FromMilliseconds(20));

        await cache.GetDevicesAsync();
        await Task.Delay(60);
        await cache.GetDevicesAsync();

        Assert.Equal(1, inner.CallCount);
    }

    // -----------------------------------------------------------------
    // 2. Fresh CoreAudio + dshow merge never returns stale devices
    // -----------------------------------------------------------------

    [Fact]
    public void Assembler_DropsDefinitivelyNotPresent_KeepsAndMergesOthers()
    {
        var stale = new MicrophoneDeviceInfo("id_stale", "AirPods (stale)", null, null);
        var healthy = new MicrophoneDeviceInfo("id_healthy", "Realtek", null, null);
        var unknown = new MicrophoneDeviceInfo("id_unknown", "Mystery Mic", null, null);

        var assembly = AudioDeviceListAssembler.Assemble(
            new[] { stale, healthy, unknown },
            id => id switch
            {
                "id_stale" => new MicrophoneStatus(null, null, null, "not_present"),
                "id_healthy" => new MicrophoneStatus(false, 27, true, "active"),
                _ => new MicrophoneStatus(null, null, null, null)
            });

        Assert.True(assembly.RemovedStaleDevices);
        Assert.Equal(2, assembly.Devices.Count);
        Assert.DoesNotContain(assembly.Devices, d => d.Id == "id_stale");

        var mergedHealthy = assembly.Devices.Single(d => d.Id == "id_healthy");
        Assert.Equal("active", mergedHealthy.State);
        Assert.Equal(false, mergedHealthy.IsMuted);
        Assert.Equal(27, mergedHealthy.VolumePercent);
        Assert.Equal(true, mergedHealthy.IsDefault);

        // Inconclusive status never drops a device.
        var mergedUnknown = assembly.Devices.Single(d => d.Id == "id_unknown");
        Assert.Null(mergedUnknown.State);
    }

    [Fact]
    public void Assembler_AllInconclusive_NeverDrops()
    {
        var devices = new[]
        {
            new MicrophoneDeviceInfo("id_a", "Mic A", null, null),
            new MicrophoneDeviceInfo("id_b", "Mic B", null, null)
        };

        var assembly = AudioDeviceListAssembler.Assemble(devices, _ => new MicrophoneStatus(null, null, null, null));

        Assert.False(assembly.RemovedStaleDevices);
        Assert.Equal(2, assembly.Devices.Count);
    }

    // -----------------------------------------------------------------
    // 3. An id returned by the API round-trips into a recording request
    // -----------------------------------------------------------------

    [Fact]
    public void ApiReturnedId_RoundTrips_ThroughIntentAndEndpointMapping()
    {
        // Parse the tagged FFmpeg 8.x listing format the production provider consumes.
        const string taggedListing =
            "[in#0 @ 000001] \"耳机 (AirPods Pro)\" (audio)\r\n" +
            "[in#0 @ 000001]   Alternative name \"@device_cm_{33D9A762-90C8-11D0-BD43-00A0C911CE86}\\wave_{1A2B3C4D-5E6F-7A8B-9C0D-1E2F3A4B5C6D}\"\r\n";

        var parsed = DshowAudioDeviceParser.Parse(taggedListing);
        var device = Assert.Single(parsed.Devices);
        var apiReturnedId = device.Id;

        // The id must map to a CoreAudio endpoint id for the WASAPI helper.
        var endpointId = CoreAudioCaptureStatusProvider.ToCoreAudioEndpointId(apiReturnedId);
        Assert.False(string.IsNullOrEmpty(endpointId));
        Assert.Equal("{0.0.1.00000000}.{1A2B3C4D-5E6F-7A8B-9C0D-1E2F3A4B5C6D}", endpointId);

        // The same id, passed back verbatim as audio.microphone.device_id, must
        // resolve to the same device in the same service instance.
        var provider = new MutableProvider(new[] { device });
        var statusProvider = new StubStatusProvider(_ => new MicrophoneStatus(false, 27, true, "active"));
        var cfg = new JsonObject
        {
            ["audio"] = new JsonObject
            {
                ["microphone"] = new JsonObject
                {
                    ["enabled"] = true,
                    ["device_id"] = apiReturnedId
                }
            }
        };

        var resolved = ConfigParser.ResolveAudioIntent(cfg, provider, statusProvider);

        Assert.NotNull(resolved);
        Assert.Equal(apiReturnedId, resolved!.Id);
    }

    [Fact]
    public void ResolveAudioIntent_RejectsNotPresentEndpoint()
    {
        var device = new MicrophoneDeviceInfo(AirPodsDshowId, "AirPods", null, null);
        var provider = new MutableProvider(new[] { device });
        var statusProvider = new StubStatusProvider(_ => new MicrophoneStatus(null, null, null, "not_present"));
        var cfg = new JsonObject
        {
            ["audio"] = new JsonObject
            {
                ["microphone"] = new JsonObject
                {
                    ["enabled"] = true,
                    ["device_id"] = AirPodsDshowId
                }
            }
        };

        var ex = Assert.Throws<ApiException>(() => ConfigParser.ResolveAudioIntent(cfg, provider, statusProvider));
        Assert.Equal("AUDIO_DEVICE_NOT_AVAILABLE", ex.Code);
    }
}

/// <summary>
/// API-level proof that the audio/devices response never returns stale devices:
/// an entry the fresh CoreAudio lookup proves is gone is dropped, the cache is
/// invalidated, and the next request re-enumerates.
/// </summary>
[Collection("HeadlessHostIntegration")]
public class AudioDevicesStalenessApiTests : IDisposable
{
    private ApiServer? _server;
    private string? _dataDir;

    private sealed class NoOpTray : ITrayContext
    {
        public string HostMode => "headless";
        public bool SupportsRegionSelectionUi => false;
        public void RequestConfirmation(RecordingConfirmationPresentation presentation, Action<ConfirmationDecision> callback) { }
        public void RequestRegionSelection(int timeoutSeconds, Action<string, int, int, int, int, string, string> callback) { }
        public void SetRecording(RecordingUiPresentation rec) { }
        public void SetIdle(RecordingUiPresentation rec) { }
        public void SetAllIdle() { }
        public void ShowError(string text) { }
    }

    private sealed class MutableProvider : IMicrophoneDeviceProvider
    {
        private IReadOnlyList<MicrophoneDeviceInfo> _devices;
        public int CallCount { get; private set; }

        public MutableProvider(IReadOnlyList<MicrophoneDeviceInfo> devices) => _devices = devices;
        public void SetDevices(IReadOnlyList<MicrophoneDeviceInfo> devices) => _devices = devices;

        public Task<IReadOnlyList<MicrophoneDeviceInfo>> GetDevicesAsync(CancellationToken cancellationToken = default)
        {
            CallCount++;
            return Task.FromResult(_devices);
        }
    }

    private sealed class StubStatusProvider : IMicrophoneStatusProvider
    {
        private readonly Func<string, MicrophoneStatus> _query;
        public StubStatusProvider(Func<string, MicrophoneStatus> query) => _query = query;
        public Task<MicrophoneStatus> GetStatusAsync(string deviceId, CancellationToken cancellationToken = default)
            => Task.FromResult(_query(deviceId));
    }

    public void Dispose()
    {
        try { _server?.Stop(); } catch { }
        if (_dataDir != null)
        {
            try { Directory.Delete(_dataDir, true); } catch { }
        }
    }

    [Fact]
    public async Task AudioDevices_DropsStaleCachedEntry_AndReEnumeratesNextCall()
    {
        const string staleId = "@device_cm_{33D9A762-90C8-11D0-BD43-00A0C911CE86}\\wave_{1A2B3C4D-5E6F-7A8B-9C0D-1E2F3A4B5C6D}";
        const string freshId = "@device_cm_{33D9A762-90C8-11D0-BD43-00A0C911CE86}\\wave_{A1B2C3D4-E5F6-7A8B-9C0D-1E2F3A4B5C6D}";

        var staleDevice = new MicrophoneDeviceInfo(staleId, "AirPods Pro", null, null);
        var freshDevice = new MicrophoneDeviceInfo(freshId, "AirPods Pro", null, null);

        var inner = new MutableProvider(new[] { staleDevice });
        var caching = new CachingMicrophoneDeviceProvider(inner, TimeSpan.FromSeconds(30));
        var statusProvider = new StubStatusProvider(id =>
            id == staleId
                ? new MicrophoneStatus(null, null, null, "not_present")
                : new MicrophoneStatus(false, 27, true, "active"));

        _dataDir = Path.Combine(Path.GetTempPath(), $"audio-stale-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_dataDir);
        Environment.SetEnvironmentVariable("AGENT_RECORDER_DATA_DIR", _dataDir, EnvironmentVariableTarget.Process);
        ApiKeyAuth.InitializeForTesting(_dataDir);

        var tray = new NoOpTray();
        var audit = new AuditLogger();
        var engine = new RecordingEngine(audit, microphoneProvider: caching, microphoneStatusProvider: statusProvider);
        engine.SetTray(tray);
        _server = new ApiServer(engine, audit, tray);
        _server.Start();

        using var client = new HttpClient(new HttpClientHandler { UseProxy = false }) { Timeout = TimeSpan.FromSeconds(5) };

        // First call: the cached dshow entry is proven stale by the fresh
        // CoreAudio lookup and must not be returned.
        var first = await client.GetAsync($"http://127.0.0.1:{ApiServer.Port}/api/v1/audio/devices");
        first.EnsureSuccessStatusCode();
        using var firstDoc = JsonDocument.Parse(await first.Content.ReadAsStringAsync());
        var firstData = firstDoc.RootElement.GetProperty("data");
        Assert.Equal("no_devices", firstData.GetProperty("status").GetString());
        Assert.Empty(firstData.GetProperty("input_devices").EnumerateArray());

        // The device reconnects; because the stale entry invalidated the cache,
        // the next call re-enumerates and returns the fresh device.
        inner.SetDevices(new[] { freshDevice });
        var second = await client.GetAsync($"http://127.0.0.1:{ApiServer.Port}/api/v1/audio/devices");
        second.EnsureSuccessStatusCode();
        using var secondDoc = JsonDocument.Parse(await second.Content.ReadAsStringAsync());
        var secondData = secondDoc.RootElement.GetProperty("data");
        Assert.Equal("ready", secondData.GetProperty("status").GetString());
        var devices = secondData.GetProperty("input_devices").EnumerateArray().ToList();
        var returned = Assert.Single(devices);
        Assert.Equal(freshId, returned.GetProperty("id").GetString());
        Assert.Equal("active", returned.GetProperty("state").GetString());

        Assert.True(inner.CallCount >= 2, "The stale detection must invalidate the enumeration cache");
    }
}
