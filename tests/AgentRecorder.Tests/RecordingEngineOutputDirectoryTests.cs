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
/// Verifies that RecordingEngine honors the output directory selected in the
/// local confirmation UI, applies conflict policies, persists the default
/// directory when requested, and fails safely when the directory cannot be used.
/// </summary>
[Collection("NonParallel-AgentRecorderDataDir")]
public class RecordingEngineOutputDirectoryTests : IDisposable
{
    private readonly TempDirectory _tmp = new();
    private string? _previousTestMode;

    private class ControllableTray : ITrayContext
    {
        public string HostMode => "tray";
        public bool SupportsRegionSelectionUi => false;

        public ConfirmationDecision? Decision { get; set; }
        public int DecisionDelayMs { get; set; }
        public string? LastError { get; private set; }
        public List<object> RecordingObjects { get; } = new();
        public List<object> IdleObjects { get; } = new();

        public void RequestConfirmation(object summary, Action<ConfirmationDecision> callback)
        {
            var decision = Decision ?? ConfirmationDecision.Reject();
            if (DecisionDelayMs > 0)
                _ = Task.Delay(DecisionDelayMs).ContinueWith(_ => callback(decision));
            else
                callback(decision);
        }

        public void RequestRegionSelection(int timeoutSeconds,
            Action<string, int, int, int, int, string, string> callback) { }

        public void SetRecording(object rec) => RecordingObjects.Add(rec);
        public void SetIdle(object rec) => IdleObjects.Add(rec);
        public void SetAllIdle() { }
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

    public RecordingEngineOutputDirectoryTests()
    {
        DataDirResolver.SetOverride(_tmp.Path);
        _previousTestMode = Environment.GetEnvironmentVariable("AGENT_RECORDER_TEST_MODE");
        Environment.SetEnvironmentVariable("AGENT_RECORDER_TEST_MODE", "1");
        SystemQuery.SetDisplayProvider(() => new List<SystemQuery.DisplayInfo>
        {
            new("display_1", "Display 1", true, new SystemQuery.Bounds(0, 0, 1920, 1080), 1.0)
        });
    }

    public void Dispose()
    {
        DataDirResolver.ClearOverride();
        Environment.SetEnvironmentVariable("AGENT_RECORDER_TEST_MODE", _previousTestMode);
        SystemQuery.SetDisplayProvider(null);
        _tmp.Dispose();
    }

    private static RecordingEngine MakeEngine(AuditLogger audit, ControllableTray tray)
    {
        var engine = new RecordingEngine(audit);
        engine.SetTray(tray);
        engine.BackendFactory = _ => (new FakeCaptureBackend(), "fake");
        return engine;
    }

    private static JsonNode DisplayConfig(string filename) =>
        new JsonObject
        {
            ["source"] = new JsonObject
            {
                ["type"] = "display",
                ["display_id"] = "display_1"
            },
            ["video"] = new JsonObject { ["fps"] = 15 },
            ["duration_seconds"] = 10,
            ["output"] = new JsonObject
            {
                ["filename"] = filename,
                ["conflict_policy"] = "rename"
            }
        };

    [Fact]
    public void Approve_WithOutputDirectory_MovesOutputPathAndKeepsFileName()
    {
        var audit = new CapturingAuditLogger();
        var tray = new ControllableTray
        {
            Decision = ConfirmationDecision.Approve(_tmp.Path)
        };
        var engine = MakeEngine(audit, tray);

        engine.CreateRecording(DisplayConfig("my-video.mp4"), "test-agent", tray);

        var rec = engine._recs.Values.Single();
        Assert.Equal(Path.Combine(_tmp.Path, "my-video.mp4"), rec.OutputPath);
        Assert.Equal(rec.OutputPath, rec.Config.OutputPath);

        var started = audit.Events.FirstOrDefault(e => e.Event == "recording.started");
        Assert.NotEqual(default, started);
        Assert.Equal(rec.OutputPath, started.OutputPath);
    }

    [Fact]
    public void Approve_WithOutputDirectory_Conflict_Renames()
    {
        var existingDir = Path.Combine(_tmp.Path, "existing");
        Directory.CreateDirectory(existingDir);
        var existingFile = Path.Combine(existingDir, "my-video.mp4");
        File.WriteAllText(existingFile, "placeholder");

        var audit = new CapturingAuditLogger();
        var tray = new ControllableTray
        {
            Decision = ConfirmationDecision.Approve(existingDir)
        };
        var engine = MakeEngine(audit, tray);

        engine.CreateRecording(DisplayConfig("my-video.mp4"), "test-agent", tray);

        var rec = engine._recs.Values.Single();
        Assert.Equal(Path.Combine(existingDir, "my-video-1.mp4"), rec.OutputPath);
        Assert.True(File.Exists(existingFile));
    }

    [Fact]
    public void Approve_WithRememberDefault_PersistsDefaultDirectory()
    {
        var customDir = Path.Combine(_tmp.Path, "CustomClips");
        Directory.CreateDirectory(customDir);

        var audit = new CapturingAuditLogger();
        var tray = new ControllableTray
        {
            Decision = ConfirmationDecision.Approve(customDir, rememberOutputDirectory: true)
        };
        var engine = MakeEngine(audit, tray);

        engine.CreateRecording(DisplayConfig("clip.mp4"), "test-agent", tray);

        var persisted = OutputSettingsStore.GetEffectiveDefaultOutputDir();
        Assert.Equal(Path.GetFullPath(customDir), Path.GetFullPath(persisted));

        Assert.Contains(audit.Events, e => e.Event == "output.default_directory_saved");
    }

    [Fact]
    public void Reject_DoesNotPersistDefaultDirectory()
    {
        var customDir = Path.Combine(_tmp.Path, "ShouldNotPersist");
        Directory.CreateDirectory(customDir);

        var audit = new CapturingAuditLogger();
        var tray = new ControllableTray
        {
            Decision = ConfirmationDecision.Reject()
        };
        var engine = MakeEngine(audit, tray);

        engine.CreateRecording(DisplayConfig("clip.mp4"), "test-agent", tray);

        var persisted = OutputSettingsStore.GetEffectiveDefaultOutputDir();
        Assert.NotEqual(Path.GetFullPath(customDir), Path.GetFullPath(persisted));
        Assert.DoesNotContain(audit.Events, e => e.Event == "output.default_directory_saved");
    }

    [Fact]
    public void Approve_OutputDirectoryOverrideFailed_DoesNotStartRecording()
    {
        var invalidDir = Path.Combine(_tmp.Path, "system32-impersonation");
        Directory.CreateDirectory(invalidDir);

        var audit = new CapturingAuditLogger();
        var tray = new ControllableTray
        {
            Decision = ConfirmationDecision.Approve(invalidDir, rememberOutputDirectory: true)
        };
        var engine = MakeEngine(audit, tray);

        // Make the directory fail validation by placing a denied keyword in the path.
        // The easiest reliable way is to use a directory under Windows itself.
        var sysDir = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        tray.Decision = ConfirmationDecision.Approve(sysDir, rememberOutputDirectory: true);

        engine.CreateRecording(DisplayConfig("clip.mp4"), "test-agent", tray);

        var rec = engine._recs.Values.Single();
        Assert.Equal(RecState.rejected, rec.State);

        Assert.Contains(audit.Events, e => e.Event == "confirmation.output_directory_override_failed");
        Assert.Contains(audit.Events, e => e.Event == "confirmation.output_directory_rejected");
        Assert.DoesNotContain(audit.Events, e => e.Event == "recording.started");
        Assert.DoesNotContain(audit.Events, e => e.Event == "output.default_directory_saved");

        Assert.NotNull(tray.LastError);
        Assert.Contains("保存目录不可用", tray.LastError);
    }

    [Fact]
    public void Approve_OutputDirectoryOverrideFailed_DoesNotPersistInvalidDirectory()
    {
        var sysDir = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        var audit = new CapturingAuditLogger();
        var tray = new ControllableTray
        {
            Decision = ConfirmationDecision.Approve(sysDir, rememberOutputDirectory: true)
        };
        var engine = MakeEngine(audit, tray);

        engine.CreateRecording(DisplayConfig("clip.mp4"), "test-agent", tray);

        var persisted = OutputSettingsStore.GetEffectiveDefaultOutputDir();
        Assert.NotEqual(Path.GetFullPath(sysDir), Path.GetFullPath(persisted));
        Assert.DoesNotContain(audit.Events, e => e.Event == "output.default_directory_saved");
    }

    private sealed class CapturingAuditLogger : AuditLogger
    {
        public List<(string Event, string OutputPath)> Events { get; } = new();

        public override void Log(string evt, object payload)
        {
            var json = JsonSerializer.Serialize(payload);
            var outputPath = "";
            try
            {
                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.TryGetProperty("output_path", out var op))
                    outputPath = op.GetString() ?? "";
            }
            catch { }
            Events.Add((evt, outputPath));
            base.Log(evt, payload);
        }
    }
}
