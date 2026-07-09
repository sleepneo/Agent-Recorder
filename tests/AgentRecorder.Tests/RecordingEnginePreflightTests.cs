using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using AgentRecorder.Capture;
using AgentRecorder.Core;
using AgentRecorder.Infrastructure;
using AgentRecorder.Logging;
using AgentRecorder.Windows;
using Xunit;

namespace AgentRecorder.Tests;

/// <summary>
/// Verifies that <see cref="RecordingEngine"/> runs preflight checks at the
/// right lifecycle points and surfaces stable error codes / suggested actions.
/// </summary>
[Collection("NonParallel-SystemQueryProviders")]
public class RecordingEnginePreflightTests : IDisposable
{
    private readonly TempDirectory _tmp = new();
    private readonly RecordingPreflightChecker.TryGetFreeSpace _originalFreeSpace;
    private readonly RecordingPreflightChecker.TryGetEncoderPaths _originalEncoder;
    private readonly Func<bool, bool, List<SystemQuery.WindowInfo>>? _originalWindowProvider;
    private readonly Func<List<SystemQuery.DisplayInfo>>? _originalDisplayProvider;

    private class ControllableTray : ITrayContext
    {
        public string HostMode => "tray";
        public bool SupportsRegionSelectionUi => false;

        public ConfirmationDecision? Decision { get; set; }
        public bool DeferCallback { get; set; }
        public string? LastError { get; private set; }
        public List<object> RecordingObjects { get; } = new();
        public List<object> IdleObjects { get; } = new();
        public List<object> AllIdleCalls { get; } = new();

        private Action<ConfirmationDecision>? _pendingCallback;

        public void RequestConfirmation(object summary, Action<ConfirmationDecision> callback)
        {
            if (DeferCallback)
            {
                _pendingCallback = callback;
                return;
            }

            var decision = Decision ?? ConfirmationDecision.Reject();
            callback(decision);
        }

        public void InvokeApproved()
        {
            Decision = ConfirmationDecision.Approve();
            _pendingCallback?.Invoke(ConfirmationDecision.Approve());
        }

        public void RequestRegionSelection(int timeoutSeconds,
            Action<string, int, int, int, int, string, string> callback) { }

        public void SetRecording(object rec) => RecordingObjects.Add(rec);
        public void SetIdle(object rec) => IdleObjects.Add(rec);
        public void SetAllIdle() => AllIdleCalls.Add(new object());
        public void ShowError(string text) => LastError = text;
    }

    private class FakeCaptureBackend : ICaptureBackend
    {
        public bool Started { get; private set; }
        public void Start(CaptureConfig cfg)
        {
            Started = true;
            cfg.CommandArgs = "fake args";
        }
        public OutputMeta Stop() => new();
        public void OnNaturalExit(Action<int, OutputMeta> callback) { }
        public int ExitCode => 0;
        public void Dispose() { }
    }

    public RecordingEnginePreflightTests()
    {
        DataDirResolver.SetOverride(_tmp.Path);

        _originalFreeSpace = RecordingPreflightChecker.FreeSpaceProvider;
        _originalEncoder = RecordingPreflightChecker.EncoderProvider;
        _originalWindowProvider = GetWindowProviderField();
        _originalDisplayProvider = GetDisplayProviderField();

        RecordingPreflightChecker.FreeSpaceProvider = (string _, out long free) =>
        {
            free = 10L * 1024 * 1024 * 1024;
            return true;
        };
        RecordingPreflightChecker.EncoderProvider = (out string? f, out string? p) =>
        {
            f = Path.Combine(_tmp.Path, "ffmpeg.exe");
            p = Path.Combine(_tmp.Path, "ffprobe.exe");
            File.WriteAllText(f, "fake ffmpeg");
            File.WriteAllText(p, "fake ffprobe");
            return true;
        };

        SystemQuery.SetDisplayProvider(() => new()
        {
            new("display_1", "Display 1", true, new SystemQuery.Bounds(0, 0, 1920, 1080), 1.0)
        });
    }

    public void Dispose()
    {
        DataDirResolver.ClearOverride();
        RecordingPreflightChecker.FreeSpaceProvider = _originalFreeSpace;
        RecordingPreflightChecker.EncoderProvider = _originalEncoder;
        SystemQuery.SetWindowProvider(_originalWindowProvider);
        SystemQuery.SetDisplayProvider(_originalDisplayProvider);
        _tmp.Dispose();
    }

    private static Func<bool, bool, List<SystemQuery.WindowInfo>>? GetWindowProviderField()
    {
        var field = typeof(SystemQuery).GetField("_windowProvider",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        return (Func<bool, bool, List<SystemQuery.WindowInfo>>?)field?.GetValue(null);
    }

    private static Func<List<SystemQuery.DisplayInfo>>? GetDisplayProviderField()
    {
        var field = typeof(SystemQuery).GetField("_displayProvider",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        return (Func<List<SystemQuery.DisplayInfo>>?)field?.GetValue(null);
    }

    private static RecordingEngine MakeEngine(AuditLogger audit, ControllableTray tray)
    {
        var engine = new RecordingEngine(audit);
        engine.SetTray(tray);
        engine.BackendFactory = _ => (new FakeCaptureBackend(), "fake");
        return engine;
    }

    private static JsonNode WindowConfig(string windowId, string filename, int? durationSeconds = null)
    {
        var output = new JsonObject { ["filename"] = filename };
        var source = new JsonObject
        {
            ["type"] = "window",
            ["window_id"] = windowId
        };
        var root = new JsonObject
        {
            ["source"] = source,
            ["video"] = new JsonObject { ["fps"] = 30 },
            ["output"] = output
        };
        if (durationSeconds is int s)
            root["duration_seconds"] = s;
        return root;
    }

    private static JsonNode DisplayConfig(string filename) =>
        new JsonObject
        {
            ["source"] = new JsonObject
            {
                ["type"] = "display",
                ["display_id"] = "display_1"
            },
            ["video"] = new JsonObject { ["fps"] = 30 },
            ["output"] = new JsonObject { ["filename"] = filename }
        };

    [Fact]
    public void CreateRecording_BeforeConfirmationPreflightFails_DoesNotCreateConfirmation()
    {
        RecordingPreflightChecker.EncoderProvider = (out string? f, out string? p) =>
        {
            f = null;
            p = null;
            return false;
        };

        var audit = new CapturingAuditLogger();
        var tray = new ControllableTray { Decision = ConfirmationDecision.Approve() };
        var engine = MakeEngine(audit, tray);

        var ex = Assert.Throws<ApiException>(() =>
            engine.CreateRecording(DisplayConfig("out.mp4"), "test-agent", tray));

        Assert.Equal("ENCODER_UNAVAILABLE", ex.Code);
        var detailsJson = JsonSerializer.Serialize(ex.Details);
        using var detailsDoc = JsonDocument.Parse(detailsJson);
        Assert.Equal("before_confirmation", detailsDoc.RootElement.GetProperty("stage").GetString());

        Assert.Empty(engine._confs);
        Assert.Empty(engine._recs);
        Assert.Contains(audit.Events, e => e.Event == "recording.preflight_failed");
        Assert.DoesNotContain(audit.Events, e => e.Event == "confirmation.created");
    }

    [Fact]
    public void CreateRecording_BeforeStart_WindowDisappeared_DoesNotStartBackend()
    {
        var hwnd = new nint(12345);
        var windowId = $"window_{hwnd.ToInt64()}";
        SystemQuery.SetWindowProvider((_, _) => new()
        {
            new SystemQuery.WindowInfo(windowId, "Notepad", "notepad.exe", 42, false, false,
                new SystemQuery.Bounds(0, 0, 1280, 720))
        });

        var audit = new CapturingAuditLogger();
        var tray = new ControllableTray { DeferCallback = true };
        var engine = MakeEngine(audit, tray);

        // Create a pending confirmation without invoking the callback yet.
        engine.CreateRecording(WindowConfig(windowId, "out.mp4"), "test-agent", tray);
        var rec = engine._recs.Values.Single();
        Assert.Equal(RecState.pending_confirmation, rec.State);
        var conf = engine._confs.Values.Single();
        Assert.Equal("pending", conf.Status);

        // Now simulate the window disappearing before the user confirms.
        SystemQuery.SetWindowProvider((_, _) => new());
        tray.InvokeApproved();

        Assert.Equal(RecState.failed, rec.State);
        Assert.Contains(rec.Warnings, w => w.StartsWith("preflight_failed: SOURCE_NOT_FOUND"));

        var backend = (FakeCaptureBackend?)rec.Backend;
        Assert.True(backend == null || !backend.Started);

        Assert.Contains(audit.Events, e =>
            e.Event == "recording.preflight_failed" &&
            e.ErrorCode == "SOURCE_NOT_FOUND" &&
            e.Stage == "before_start");

        Assert.NotNull(tray.LastError);
    }

    [Fact]
    public void CreateRecording_BeforeStart_WindowMinimized_DoesNotStartBackend()
    {
        var hwnd = new nint(12345);
        var windowId = $"window_{hwnd.ToInt64()}";
        SystemQuery.SetWindowProvider((_, _) => new()
        {
            new SystemQuery.WindowInfo(windowId, "Notepad", "notepad.exe", 42, false, false,
                new SystemQuery.Bounds(0, 0, 1280, 720))
        });

        var audit = new CapturingAuditLogger();
        var tray = new ControllableTray { DeferCallback = true };
        var engine = MakeEngine(audit, tray);

        // Create a pending confirmation without invoking the callback yet.
        engine.CreateRecording(WindowConfig(windowId, "out.mp4"), "test-agent", tray);
        var rec = engine._recs.Values.Single();
        Assert.Equal(RecState.pending_confirmation, rec.State);

        // Simulate minimization before the user confirms.
        SystemQuery.SetWindowProvider((_, _) => new()
        {
            new SystemQuery.WindowInfo(windowId, "Notepad", "notepad.exe", 42, false, true,
                new SystemQuery.Bounds(0, 0, 1280, 720))
        });
        tray.InvokeApproved();

        Assert.Equal(RecState.failed, rec.State);
        Assert.Contains(rec.Warnings, w => w.StartsWith("preflight_failed: SOURCE_UNAVAILABLE"));

        var backend = (FakeCaptureBackend?)rec.Backend;
        Assert.True(backend == null || !backend.Started);

        Assert.Contains(audit.Events, e =>
            e.Event == "recording.preflight_failed" &&
            e.ErrorCode == "SOURCE_UNAVAILABLE");

        Assert.NotNull(tray.LastError);
    }

    [Fact]
    public void CreateRecording_PreflightPass_PathsUnchanged()
    {
        var audit = new CapturingAuditLogger();
        var tray = new ControllableTray { Decision = ConfirmationDecision.Approve() };
        var engine = MakeEngine(audit, tray);

        var result = engine.CreateRecording(DisplayConfig("out.mp4"), "test-agent", tray);

        var rec = engine._recs.Values.Single();
        Assert.Equal(RecState.recording, rec.State);
        var backend = (FakeCaptureBackend)rec.Backend!;
        Assert.True(backend.Started);
        Assert.Contains(audit.Events, e => e.Event == "recording.started");
    }

    private sealed class CapturingAuditLogger : AuditLogger
    {
        public List<(string Event, string? ErrorCode, string? Stage)> Events { get; } = new();

        public override void Log(string evt, object payload)
        {
            var json = JsonSerializer.Serialize(payload);
            string? errorCode = null;
            string? stage = null;
            try
            {
                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.TryGetProperty("error_code", out var ec))
                    errorCode = ec.GetString();
                if (doc.RootElement.TryGetProperty("stage", out var st))
                    stage = st.GetString();
            }
            catch { }
            Events.Add((evt, errorCode, stage));
            base.Log(evt, payload);
        }
    }
}
