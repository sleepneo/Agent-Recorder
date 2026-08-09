using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using AgentRecorder.Capture;
using AgentRecorder.Core;
using AgentRecorder.Infrastructure;
using AgentRecorder.Logging;
using AgentRecorder.Windows;
using Xunit;

namespace AgentRecorder.Tests;

[Collection("NonParallel-AgentRecorderEnvVar")]
public sealed class RecordingEngineTopologyRevalidationTests : IDisposable
{
    private readonly RecordingPreflightChecker.TryGetFreeSpace _oldFreeSpace;
    private readonly RecordingPreflightChecker.TryGetEncoderPaths _oldEncoder;
    private readonly string? _oldTestMode;
    private readonly string? _oldRegionBackend;

    public RecordingEngineTopologyRevalidationTests()
    {
        _oldFreeSpace = RecordingPreflightChecker.FreeSpaceProvider;
        _oldEncoder = RecordingPreflightChecker.EncoderProvider;
        _oldTestMode = Environment.GetEnvironmentVariable("AGENT_RECORDER_TEST_MODE");
        _oldRegionBackend = Environment.GetEnvironmentVariable(CaptureBackendSelector.RegionBackendEnvVar);

        Environment.SetEnvironmentVariable("AGENT_RECORDER_TEST_MODE", "1");
        // Keep the focused tests on the default non-WGC plan. The topology
        // assertion is independent of backend capability probing.
        Environment.SetEnvironmentVariable(CaptureBackendSelector.RegionBackendEnvVar, "not-exact");
        RecordingPreflightChecker.FreeSpaceProvider = (string _, out long free) =>
        {
            free = 10L * 1024 * 1024 * 1024;
            return true;
        };
        RecordingPreflightChecker.EncoderProvider = (out string? ffmpeg, out string? ffprobe) =>
        {
            ffmpeg = typeof(RecordingEngineTopologyRevalidationTests).Assembly.Location;
            ffprobe = ffmpeg;
            return true;
        };
    }

    public void Dispose()
    {
        RecordingPreflightChecker.FreeSpaceProvider = _oldFreeSpace;
        RecordingPreflightChecker.EncoderProvider = _oldEncoder;
        Environment.SetEnvironmentVariable("AGENT_RECORDER_TEST_MODE", _oldTestMode);
        Environment.SetEnvironmentVariable(CaptureBackendSelector.RegionBackendEnvVar, _oldRegionBackend);
    }

    [Fact]
    public void ApprovalRevalidation_SameIdAndBounds_StartsBackendExactlyOnce()
    {
        var topology = new FixedTopologyProvider(new[] { Display("display-left", 0, 0, 1920, 1080) });
        var audit = new TopologyAudit();
        var backend = new CountingBackend();
        var tray = new TopologyTray();
        var engine = NewEngine(audit, topology, backend);

        engine.CreateRecording(RegionJson(), "test-agent", tray);
        var rec = engine._recs.Values.Single();

        Assert.Equal(0, topology.Calls);
        tray.Approve();

        Assert.Equal(1, topology.Calls);
        Assert.Equal(1, backend.StartCalls);
        Assert.Equal(RecState.recording, rec.State);

        var payload = audit.Payload("recording.capture_plan_revalidated");
        Assert.Equal("passed", payload.GetProperty("topology_status").GetString());
        Assert.Equal("matched", payload.GetProperty("topology_reason").GetString());
        Assert.Equal("display-left", payload.GetProperty("approved_display_id").GetString());
        Assert.Equal("display-left", payload.GetProperty("revalidated_display_id").GetString());
        Assert.Equal("(0,0,1920,1080)", BoundsText(payload.GetProperty("revalidated_display_bounds")));
        Assert.Equal("synthetic-test-display:display-left", payload.GetProperty("approved_display_identity_fingerprint").GetString());
        Assert.DoesNotContain("device", payload.ToString(), StringComparison.OrdinalIgnoreCase);
        using var summary = JsonDocument.Parse(JsonSerializer.Serialize(tray.Summary));
        Assert.Equal("display-left", summary.RootElement.GetProperty("target_display_id").GetString());
        Assert.DoesNotContain("synthetic-test-display:display-left", summary.RootElement.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void ApprovalRevalidation_PublicOrdinalChangesButStableIdentityAndBoundsMatch_AllowsBackendOnce()
    {
        var topology = new FixedTopologyProvider(new[]
        {
            Display("display_2", 0, 0, 1920, 1080, "synthetic-test-display:display-left")
        });
        var audit = new TopologyAudit();
        var backend = new CountingBackend();
        var tray = new TopologyTray();
        var engine = NewEngine(audit, topology, backend);

        engine.CreateRecording(RegionJson(), "test-agent", tray);
        tray.Approve();

        Assert.Equal(RecState.recording, engine._recs.Values.Single().State);
        Assert.Equal(1, backend.StartCalls);
        var payload = audit.Payload("recording.capture_plan_revalidated");
        Assert.Equal("display-left", payload.GetProperty("approved_display_id").GetString());
        Assert.Equal("display_2", payload.GetProperty("revalidated_display_id").GetString());
        Assert.Equal("synthetic-test-display:display-left", payload.GetProperty("revalidated_display_identity_fingerprint").GetString());
    }

    [Fact]
    public void ApprovalRevalidation_SamePublicOrdinalAndBoundsButStableIdentityChanges_FailsIdentityMismatch()
    {
        var topology = new FixedTopologyProvider(new[]
        {
            Display("display-left", 0, 0, 1920, 1080, "synthetic-test-display:display-other")
        });
        var audit = new TopologyAudit();
        var backend = new CountingBackend();
        var tray = new TopologyTray();
        var engine = NewEngine(audit, topology, backend);

        engine.CreateRecording(RegionJson(), "test-agent", tray);
        tray.Approve();

        Assert.Equal(RecState.failed, engine._recs.Values.Single().State);
        Assert.Equal(0, backend.StartCalls);
        var payload = audit.Payload("recording.capture_plan_revalidated");
        Assert.Equal("identity_mismatch", payload.GetProperty("topology_reason").GetString());
        Assert.Equal("display-left", payload.GetProperty("approved_display_id").GetString());
        Assert.Equal("display-left", payload.GetProperty("revalidated_display_id").GetString());
    }

    [Fact]
    public void ApprovalRevalidation_UnresolvedCurrentIdentity_FailsBeforeCountdownAndBackend()
    {
        var topology = new FixedTopologyProvider(new[]
        {
            new DisplayTopologySnapshot(
                "display-left",
                null,
                DisplayIdentityResolutionStatus.Unresolved,
                new CapturePlanBounds(0, 0, 1920, 1080))
        });
        var audit = new TopologyAudit();
        var backend = new CountingBackend();
        var tray = new TopologyTray();
        var engine = NewEngine(audit, topology, backend);

        engine.CreateRecording(RegionJson(), "test-agent", tray);
        tray.Approve();

        Assert.Equal(RecState.failed, engine._recs.Values.Single().State);
        Assert.Equal(0, backend.StartCalls);
        Assert.Equal(0, tray.CountdownCalls);
        Assert.Equal("identity_unresolved", audit.Payload("recording.capture_plan_revalidated").GetProperty("topology_reason").GetString());
    }

    [Fact]
    public void ApprovalRevalidation_CurrentTargetUnavailable_FailsClosedBeforeBackend()
    {
        var topology = new FixedTopologyProvider(new[]
        {
            new DisplayTopologySnapshot(
                "display-left",
                "synthetic-test-display:display-left",
                DisplayIdentityResolutionStatus.Unavailable,
                new CapturePlanBounds(0, 0, 1920, 1080))
        });
        var audit = new TopologyAudit();
        var backend = new CountingBackend();
        var tray = new TopologyTray();
        var engine = NewEngine(audit, topology, backend);

        engine.CreateRecording(RegionJson(), "test-agent", tray);
        tray.Approve();

        var rec = engine._recs.Values.Single();
        Assert.Equal(RecState.failed, rec.State);
        Assert.Equal(0, backend.StartCalls);
        Assert.Equal(0, tray.PreparingCalls);
        Assert.Equal(0, tray.CountdownCalls);
        var payload = audit.Payload("recording.capture_plan_revalidated");
        Assert.Equal("identity_unavailable", payload.GetProperty("topology_reason").GetString());
        Assert.Equal("", payload.GetProperty("revalidated_display_identity_fingerprint").GetString());
        Assert.DoesNotContain("device", payload.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ApprovalRevalidation_MissingDisplay_FailsBeforeBackendAndAuditsBounds()
        => AssertTopologyFailure(
            Array.Empty<DisplayTopologySnapshot>(),
            "identity_missing",
            expectedCurrentId: "");

    [Fact]
    public void ApprovalRevalidation_ReplacedDisplayIdWithSameBounds_FailsClosed()
        => AssertTopologyFailure(
            new[] { Display("display-right", 0, 0, 1920, 1080) },
            "identity_missing",
            expectedCurrentId: "display-right");

    [Fact]
    public void ApprovalRevalidation_DisplayBoundsChanged_FailsClosed()
        => AssertTopologyFailure(
            new[] { Display("display-left", 0, 0, 1600, 900) },
            "topology_display_bounds_changed",
            expectedCurrentId: "display-left");

    [Fact]
    public void ApprovalRevalidation_DuplicateDisplayId_FailsClosed()
        => AssertTopologyFailure(
            new[]
            {
                Display("display-left", 0, 0, 1920, 1080),
                Display("display-left", 1920, 0, 1920, 1080)
            },
            "identity_ambiguous",
            expectedCurrentId: "display-left");

    [Fact]
    public void ApprovalRevalidation_ProviderException_FailsClosedWithoutExceptionText()
    {
        var topology = new FixedTopologyProvider(Array.Empty<DisplayTopologySnapshot>()) { ThrowOnRead = true };
        var audit = new TopologyAudit();
        var backend = new CountingBackend();
        var tray = new TopologyTray();
        var engine = NewEngine(audit, topology, backend);

        engine.CreateRecording(RegionJson(), "test-agent", tray);
        var rec = engine._recs.Values.Single();
        tray.Approve();

        Assert.Equal(RecState.failed, rec.State);
        Assert.Equal("capture_semantics_changed", rec.Error);
        Assert.Equal(0, backend.StartCalls);
        Assert.Equal("capture_semantics_changed", tray.LastFailureReason);
        var payload = audit.Payload("recording.capture_plan_revalidated");
        Assert.Equal("topology_provider_failed", payload.GetProperty("topology_reason").GetString());
        Assert.DoesNotContain("InvalidOperationException", payload.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void ApprovalRevalidation_RegionNoLongerContained_FailsBeforePlanFactoryAndBackend()
    {
        var topology = new FixedTopologyProvider(new[] { Display("display-left", 0, 0, 1920, 1080) });
        var audit = new TopologyAudit();
        var backend = new CountingBackend();
        var tray = new TopologyTray();
        var engine = NewEngine(audit, topology, backend);

        engine.CreateRecording(RegionJson(), "test-agent", tray);
        var rec = engine._recs.Values.Single();
        rec.Config.Bounds = (1800, 900, 640, 480);
        tray.Approve();

        Assert.Equal(RecState.failed, rec.State);
        Assert.Equal("capture_semantics_changed", rec.Error);
        Assert.Equal(0, backend.StartCalls);
        Assert.Equal("capture_semantics_changed", tray.LastFailureReason);
        var payload = audit.Payload("recording.capture_plan_revalidated");
        Assert.Equal("topology_region_not_contained", payload.GetProperty("topology_reason").GetString());
        Assert.Equal("display-left", payload.GetProperty("approved_display_id").GetString());
        Assert.Equal("display-left", payload.GetProperty("revalidated_display_id").GetString());
        Assert.DoesNotContain("pixels", payload.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("screen_content", payload.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    private void AssertTopologyFailure(
        IReadOnlyList<DisplayTopologySnapshot> current,
        string reason,
        string expectedCurrentId)
    {
        var topology = new FixedTopologyProvider(current);
        var audit = new TopologyAudit();
        var backend = new CountingBackend();
        var tray = new TopologyTray();
        var engine = NewEngine(audit, topology, backend);

        var outputPath = Path.Combine(Path.GetTempPath(), "agent-recorder-task-199b", Guid.NewGuid().ToString("N"), "clip.mp4");
        engine.CreateRecording(RegionJson(outputPath), "test-agent", tray);
        var rec = engine._recs.Values.Single();
        tray.Approve();

        Assert.Equal(RecState.failed, rec.State);
        Assert.Equal("capture_semantics_changed", rec.Error);
        Assert.Null(rec.Backend);
        Assert.Equal(0, backend.StartCalls);
        Assert.Equal(0, tray.PreparingCalls);
        Assert.Equal(0, tray.CountdownCalls);
        Assert.Equal(0, tray.RecordingCalls);
        Assert.Equal("capture_semantics_changed", tray.LastFailureReason);
        Assert.False(File.Exists(outputPath));

        var payload = audit.Payload("recording.capture_plan_revalidated");
        Assert.Equal("failed", payload.GetProperty("topology_status").GetString());
        Assert.Equal(reason, payload.GetProperty("topology_reason").GetString());
        Assert.Equal("display-left", payload.GetProperty("approved_display_id").GetString());
        Assert.Equal(expectedCurrentId, payload.GetProperty("revalidated_display_id").GetString());
        Assert.Equal("(0,0,1920,1080)", BoundsText(payload.GetProperty("approved_display_bounds")));
        Assert.DoesNotContain("screen_content", payload.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    private static RecordingEngine NewEngine(TopologyAudit audit, FixedTopologyProvider topology, CountingBackend backend)
    {
        var engine = new RecordingEngine(audit, displayTopologyProvider: topology)
        {
            CountdownSteps = 0,
            BackendFactory = _ => (backend, "fake")
        };
        return engine;
    }

    private static JsonNode RegionJson(string? outputPath = null) =>
        new JsonObject
        {
            ["source"] = new JsonObject
            {
                ["type"] = "region",
                ["display_id"] = "display-left",
                ["coordinate_space"] = "virtual_screen",
                ["bounds"] = new JsonObject
                {
                    ["x"] = 100,
                    ["y"] = 100,
                    ["width"] = 640,
                    ["height"] = 480
                }
            },
            ["video"] = new JsonObject { ["fps"] = 30 },
            ["stop_condition"] = new JsonObject { ["type"] = "duration", ["seconds"] = 5 },
            ["output"] = new JsonObject
            {
                ["filename"] = outputPath ?? Path.Combine(Path.GetTempPath(), "agent-recorder-task-199b", Guid.NewGuid().ToString("N"), "clip.mp4")
            }
        };

    private static DisplayTopologySnapshot Display(
        string id,
        int x,
        int y,
        int width,
        int height,
        string? stableIdentity = null) =>
        new(
            id,
            stableIdentity ?? $"synthetic-test-display:{id}",
            DisplayIdentityResolutionStatus.Resolved,
            new CapturePlanBounds(x, y, width, height));

    private static string BoundsText(JsonElement bounds)
        => $"({bounds.GetProperty("x").GetInt32()},{bounds.GetProperty("y").GetInt32()},{bounds.GetProperty("width").GetInt32()},{bounds.GetProperty("height").GetInt32()})";

    private sealed class FixedTopologyProvider : IDisplayTopologyProvider
    {
        private readonly IReadOnlyList<DisplayTopologySnapshot> _displays;
        public bool ThrowOnRead { get; set; }
        public int Calls { get; private set; }

        public FixedTopologyProvider(IReadOnlyList<DisplayTopologySnapshot> displays) => _displays = displays;

        public IReadOnlyList<DisplayTopologySnapshot> GetCurrentDisplays()
        {
            Calls++;
            if (ThrowOnRead)
                throw new InvalidOperationException("test-only provider exception");
            return _displays;
        }
    }

    private sealed class CountingBackend : ICaptureBackend
    {
        public int StartCalls { get; private set; }
        public void Start(CaptureConfig cfg)
        {
            StartCalls++;
            cfg.CommandArgs = "fake";
        }
        public OutputMeta Stop() => new();
        public void Dispose() { }
    }

    private sealed class TopologyTray : ITrayContext, IRecordingFailureNotifier
    {
        private Action<ConfirmationDecision>? _callback;
        public string HostMode => "headless";
        public bool SupportsRegionSelectionUi => false;
        public object? Summary { get; private set; }
        public int PreparingCalls { get; private set; }
        public int CountdownCalls { get; private set; }
        public int RecordingCalls { get; private set; }
        public string? LastFailureReason { get; private set; }

        public void RequestConfirmation(object summary, Action<ConfirmationDecision> callback)
        {
            Summary = summary;
            _callback = callback;
        }
        public void Approve() => _callback?.Invoke(ConfirmationDecision.Approve());
        public void RequestRegionSelection(int timeoutSeconds, Action<string, int, int, int, int, string, string> callback) { }
        public void SetRecording(object rec) => RecordingCalls++;
        public void SetIdle(object rec) { }
        public void SetAllIdle() { }
        public void ShowError(string text) { }
        public void SetPreparing(object rec) => PreparingCalls++;
        public void SetCountdown(object rec, int? remainingSeconds) => CountdownCalls++;
        public void ShowRecordingFailure(string recordingId, string reasonCode) => LastFailureReason = reasonCode;
    }

    private sealed class TopologyAudit : AuditLogger
    {
        private readonly List<(string Event, JsonElement Payload)> _payloads = new();

        public override void Log(string evt, object payload)
        {
            using var document = JsonDocument.Parse(JsonSerializer.Serialize(payload));
            _payloads.Add((evt, document.RootElement.Clone()));
        }

        public JsonElement Payload(string evt)
            => _payloads.Last(item => item.Event == evt).Payload;
    }
}
