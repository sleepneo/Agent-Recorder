using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AgentRecorder.Capture;
using AgentRecorder.Core;
using AgentRecorder.Infrastructure;
using AgentRecorder.Logging;
using Xunit;

namespace AgentRecorder.Tests;

/// <summary>
/// Verifies that user-initiated stops produce <c>completed</c> when the output
/// is basically valid, while natural completions still enforce the planned
/// duration range. Also covers stop_reason propagation across API surfaces.
/// </summary>
public class RecordingEngineStopResultTests
{
    private sealed class NoOpTray : ITrayContext
    {
        public string HostMode => "headless";
        public bool SupportsRegionSelectionUi => false;

        public void RequestConfirmation(object summary, Action<ConfirmationDecision> callback) { }
        public void RequestRegionSelection(int timeoutSeconds,
            Action<string, int, int, int, int, string, string> callback) { }
        public void SetRecording(object rec) { }
        public void SetIdle(object rec) { }
        public void SetAllIdle() { }
        public void ShowError(string text) { }
    }

    private sealed class FakeCaptureBackend : ICaptureBackend
    {
        public OutputMeta StopResult { get; set; } = new();
        public Func<OutputMeta>? StopAction { get; set; }
        public int ExitCodeValue { get; set; }
        public int StopCallCount { get; private set; }
        private Action<int, OutputMeta>? _onNaturalExit;

        public void Start(CaptureConfig cfg) => cfg.CommandArgs = "fake args";

        public OutputMeta Stop()
        {
            StopCallCount++;
            return StopAction?.Invoke() ?? StopResult;
        }

        public void OnNaturalExit(Action<int, OutputMeta> callback) => _onNaturalExit = callback;

        public int ExitCode => ExitCodeValue;

        public void Dispose() { }

        public void FireNaturalExit(int exitCode, OutputMeta meta) => _onNaturalExit?.Invoke(exitCode, meta);
    }

    private sealed class ThrowingBackend : ICaptureBackend
    {
        public string Message { get; }
        public int StopCallCount { get; private set; }

        public ThrowingBackend(string message) => Message = message;

        public void Start(CaptureConfig cfg) => throw new Exception(Message);

        public OutputMeta Stop()
        {
            StopCallCount++;
            return new OutputMeta();
        }

        public void OnNaturalExit(Action<int, OutputMeta> callback) { }

        public int ExitCode => -1;

        public void Dispose() { }
    }

    private static (RecordingEngine engine, Recording rec, FakeCaptureBackend backend, CaptureAuditLogger audit) Setup(
        int durationSeconds = 30,
        OutputMeta? stopMeta = null)
    {
        var audit = new CaptureAuditLogger();
        var tray = new NoOpTray();
        var engine = new RecordingEngine(audit);
        engine.SetTray(tray);

        var backend = new FakeCaptureBackend
        {
            StopResult = stopMeta ?? new OutputMeta { DurationSeconds = 4.4, SizeBytes = 263781 }
        };
        engine.BackendFactory = _ => (backend, "fake");

        var outputPath = Path.Combine(Path.GetTempPath(), $"test-stop-{Guid.NewGuid():N}.mp4");
        var rec = new Recording
        {
            SourceType = "region",
            DurationSeconds = durationSeconds,
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
        return (engine, rec, backend, audit);
    }

    private static string? GetStringProperty(object anon, string propertyName)
    {
        var json = JsonSerializer.Serialize(anon);
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.TryGetProperty(propertyName, out var p) ? p.GetString() : null;
    }

    [Fact]
    public void Stop_UserInitiatedBeforePlannedDuration_ValidOutput_Completes()
    {
        var (engine, rec, backend, audit) = Setup(30, new OutputMeta
        {
            DurationSeconds = 4.4,
            SizeBytes = 263781
        });
        backend.ExitCodeValue = 0;

        var resp = engine.Stop(rec.Id, "floating_button");

        Assert.Equal(RecState.completed, rec.State);
        Assert.Equal("floating_button", rec.StopReason);
        Assert.DoesNotContain(rec.Warnings, w => w.Contains("duration_out_of_range"));
        Assert.DoesNotContain(rec.Warnings, w => w.Contains("Actual duration"));
        Assert.Contains(audit.Events, e => e.evt == "recording.stopping");
        Assert.Contains(audit.Events, e => e.evt == "recording.stopped");
        Assert.Contains(audit.Events, e => e.evt == "recording.completed");
        Assert.Single(audit.Events, e => e.evt == "recording.completed");

        var json = JsonSerializer.Serialize(resp);
        using var doc = JsonDocument.Parse(json);
        Assert.Equal("completed", doc.RootElement.GetProperty("status").GetString());
        Assert.Equal("floating_button", doc.RootElement.GetProperty("stop_reason").GetString());
    }

    [Fact]
    public void Finalize_NaturalShortOutput_FailsWithUnexpectedExitReason()
    {
        var (engine, rec, backend, audit) = Setup(30);

        backend.FireNaturalExit(0, new OutputMeta
        {
            DurationSeconds = 4.4,
            SizeBytes = 263781
        });

        Assert.Equal(RecState.failed, rec.State);
        Assert.Equal("unexpected_exit", rec.StopReason);
        Assert.Contains(rec.Warnings, w => w.Contains("duration_out_of_range"));
        Assert.Contains(audit.Events, e => e.evt == "recording.failed");
        Assert.DoesNotContain(audit.Events, e => e.evt == "recording.completed");
    }

    [Fact]
    public void Finalize_NaturalValidOutput_CompletesWithDurationReached()
    {
        var (engine, rec, backend, audit) = Setup(30);

        backend.FireNaturalExit(0, new OutputMeta
        {
            DurationSeconds = 30.0,
            SizeBytes = 263781
        });

        Assert.Equal(RecState.completed, rec.State);
        Assert.Equal("duration_reached", rec.StopReason);
        Assert.Contains(audit.Events, e => e.evt == "recording.completed");
        Assert.DoesNotContain(audit.Events, e => e.evt == "recording.failed");
    }

    [Theory]
    [InlineData(0, 263781, 0, "zero_duration")]
    [InlineData(4.4, 100, 0, "empty_output")]
    [InlineData(4.4, 263781, 1, "non_zero_exit")]
    public void Stop_UserInitiated_InvalidOutput_RemainsFailed(double duration, long size, int exitCode, string expectedWarning)
    {
        var (engine, rec, backend, audit) = Setup(30, new OutputMeta
        {
            DurationSeconds = duration,
            SizeBytes = size
        });
        backend.ExitCodeValue = exitCode;

        engine.Stop(rec.Id, "tray_menu");

        Assert.Equal(RecState.failed, rec.State);
        Assert.Equal("tray_menu", rec.StopReason);
        Assert.Contains(rec.Warnings, w => w.Contains(expectedWarning));
        Assert.DoesNotContain(rec.Warnings, w => w.Contains("duration_out_of_range"));
        Assert.Contains(audit.Events, e => e.evt == "recording.failed");
    }

    [Fact]
    public void GetOutput_UserStoppedRecording_DoesNotAddShortDurationWarning()
    {
        var (engine, rec, backend, audit) = Setup(30, new OutputMeta
        {
            DurationSeconds = 4.4,
            SizeBytes = 263781
        });
        engine.Stop(rec.Id, "global_hotkey");

        var output = engine.GetOutput(rec.Id);
        var json = JsonSerializer.Serialize(output);
        using var doc = JsonDocument.Parse(json);

        Assert.Equal("global_hotkey", doc.RootElement.GetProperty("stop_reason").GetString());
        var warnings = doc.RootElement.GetProperty("warnings").EnumerateArray()
            .Select(w => w.GetString()).ToList();
        Assert.DoesNotContain(warnings, w => w != null && w.Contains("Actual duration"));
        Assert.DoesNotContain(warnings, w => w != null && w.Contains("Duration is 0"));
    }

    [Fact]
    public void StopResponseAndStatus_ExposeSameStopReason()
    {
        var (engine, rec, backend, audit) = Setup(30, new OutputMeta
        {
            DurationSeconds = 4.4,
            SizeBytes = 263781
        });

        var stopResp = engine.Stop(rec.Id, "floating_button");

        Assert.Equal("floating_button", GetStringProperty(stopResp, "stop_reason"));
        Assert.Equal("floating_button", GetStringProperty(engine.GetStatus(rec.Id), "stop_reason"));
        Assert.Equal("floating_button", GetStringProperty(engine.GetOutput(rec.Id), "stop_reason"));
        Assert.Equal("floating_button", GetStringProperty(
            engine.GetStatusWait(rec.Id, "recording", 100), "stop_reason"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Stop_DefaultBlankReason_NormalizesToUserRequested(string? reason)
    {
        var (engine, rec, backend, audit) = Setup(30, new OutputMeta
        {
            DurationSeconds = 4.4,
            SizeBytes = 263781
        });

        engine.Stop(rec.Id, reason!);

        Assert.Equal("user_requested", rec.StopReason);
        Assert.Contains(audit.Events, e =>
            e.evt == "recording.stopping" && e.json.Contains("\"reason\":\"user_requested\""));
        Assert.Contains(audit.Events, e =>
            e.evt == "recording.stopped" && e.json.Contains("\"reason\":\"user_requested\""));
    }

    [Fact]
    public void Stop_Finalization_IsIdempotent_WhenNaturalExitRacesExplicitStop()
    {
        var (engine, rec, backend, audit) = Setup(30, new OutputMeta
        {
            DurationSeconds = 4.4,
            SizeBytes = 263781
        });
        backend.ExitCodeValue = 0;

        // Simulate the backend's natural-exit callback firing from inside Stop().
        backend.StopAction = () =>
        {
            backend.FireNaturalExit(0, backend.StopResult);
            return backend.StopResult;
        };

        engine.Stop(rec.Id, "floating_button");

        Assert.Equal(RecState.completed, rec.State);
        Assert.Equal("floating_button", rec.StopReason);
        Assert.Single(audit.Events, e => e.evt == "recording.completed");
        Assert.Single(audit.Events, e => e.evt == "recording.stopped");
    }

    [Theory]
    [InlineData(RecState.completed)]
    [InlineData(RecState.failed)]
    [InlineData(RecState.cancelled)]
    [InlineData(RecState.rejected)]
    [InlineData(RecState.expired)]
    public void Stop_TerminalStates_AreIdempotent(RecState terminalState)
    {
        var (engine, rec, backend, audit) = Setup(30);
        rec.State = terminalState;
        rec.StopReason = terminalState == RecState.completed ? "duration_reached" : null;
        rec.Error = terminalState == RecState.failed ? "original terminal error" : null;
        rec.Warnings.Add("original warning");
        var originalEventCount = audit.Events.Count;

        engine.Stop(rec.Id, "tray_menu");

        Assert.Equal(terminalState, rec.State);
        Assert.Equal(terminalState == RecState.completed ? "duration_reached" : null, rec.StopReason);
        if (terminalState == RecState.failed)
            Assert.Equal("original terminal error", rec.Error);
        Assert.Contains(rec.Warnings, w => w.Contains("original warning"));
        Assert.Equal(0, backend.StopCallCount);
        Assert.DoesNotContain(audit.Events.Skip(originalEventCount), e => e.evt == "recording.stopping");
        Assert.DoesNotContain(audit.Events.Skip(originalEventCount), e => e.evt == "recording.stopped");
        Assert.DoesNotContain(audit.Events.Skip(originalEventCount), e => e.evt == "recording.completed");
        Assert.DoesNotContain(audit.Events.Skip(originalEventCount), e => e.evt == "recording.failed");
    }

    [Fact]
    public void Stop_PreflightFailedRecording_IsIdempotentAndDoesNotCallBackend()
    {
        var (engine, rec, backend, audit) = Setup(30);
        rec.State = RecState.failed;
        rec.Error = "preflight check failed: INTERACTIVE_DESKTOP_VISIBLE";
        rec.Warnings.Add("preflight_not_ready");
        var originalEventCount = audit.Events.Count;

        engine.Stop(rec.Id, "tray_menu");

        Assert.Equal(RecState.failed, rec.State);
        Assert.Equal("preflight check failed: INTERACTIVE_DESKTOP_VISIBLE", rec.Error);
        Assert.Contains(rec.Warnings, w => w.Contains("preflight_not_ready"));
        Assert.Null(rec.StopReason);
        Assert.Equal(0, backend.StopCallCount);
        Assert.DoesNotContain(audit.Events.Skip(originalEventCount), e => e.evt == "recording.stopping");
        Assert.DoesNotContain(audit.Events.Skip(originalEventCount), e => e.evt == "recording.stopped");
    }

    [Fact]
    public void Stop_LaunchFailedRecording_IsIdempotentAndPreservesOriginalError()
    {
        var audit = new CaptureAuditLogger();
        var tray = new NoOpTray();
        var engine = new RecordingEngine(audit);
        engine.SetTray(tray);
        engine.BackendFactory = _ => (new ThrowingBackend("ffmpeg launch failed"), "fake");

        var outputPath = Path.Combine(Path.GetTempPath(), $"test-stop-{Guid.NewGuid():N}.mp4");
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

        Assert.Equal(RecState.failed, rec.State);
        var originalError = rec.Error;
        var originalWarnings = rec.Warnings.ToList();
        var originalEventCount = audit.Events.Count;

        engine.Stop(rec.Id, "tray_menu");

        Assert.Equal(RecState.failed, rec.State);
        Assert.Equal(originalError, rec.Error);
        Assert.Equal(originalWarnings, rec.Warnings);
        Assert.DoesNotContain(audit.Events.Skip(originalEventCount), e => e.evt == "recording.stopping");
        Assert.DoesNotContain(audit.Events.Skip(originalEventCount), e => e.evt == "recording.stopped");
    }

    [Fact]
    public async Task Stop_ConcurrentExplicitRequests_CallBackendOnceAndFirstReasonWins()
    {
        var (engine, rec, backend, audit) = Setup(30, new OutputMeta
        {
            DurationSeconds = 4.4,
            SizeBytes = 263781
        });
        backend.ExitCodeValue = 0;

        var firstStopEntered = new ManualResetEventSlim(false);
        var allowFirstStopToComplete = new ManualResetEventSlim(false);

        backend.StopAction = () =>
        {
            firstStopEntered.Set();
            allowFirstStopToComplete.Wait();
            return backend.StopResult;
        };

        object? secondResp = null;
        var first = Task.Run(() => engine.Stop(rec.Id, "floating_button"));
        firstStopEntered.Wait(TimeSpan.FromSeconds(5));
        var second = Task.Run(() => secondResp = engine.Stop(rec.Id, "global_hotkey"));
        Assert.True(await Task.WhenAny(second, Task.Delay(TimeSpan.FromSeconds(2))) == second,
            "second Stop should return immediately");

        allowFirstStopToComplete.Set();
        Assert.True(await Task.WhenAny(first, Task.Delay(TimeSpan.FromSeconds(5))) == first,
            "first Stop should complete");

        Assert.Equal(1, backend.StopCallCount);
        Assert.Equal(RecState.completed, rec.State);
        Assert.Equal("floating_button", rec.StopReason);
        Assert.Single(audit.Events, e => e.evt == "recording.stopping");
        Assert.Single(audit.Events, e => e.evt == "recording.stopped");
        Assert.Single(audit.Events, e => e.evt == "recording.completed");

        var json = JsonSerializer.Serialize(secondResp);
        using var doc = JsonDocument.Parse(json);
        Assert.Equal("stopping", doc.RootElement.GetProperty("status").GetString());
        Assert.Equal("floating_button", doc.RootElement.GetProperty("stop_reason").GetString());
    }

    [Fact]
    public void Stop_NonBlankReason_IsPreserved()
    {
        var (engine, rec, backend, audit) = Setup(30, new OutputMeta
        {
            DurationSeconds = 4.4,
            SizeBytes = 263781
        });

        engine.Stop(rec.Id, "  tray_menu  ");

        Assert.Equal("tray_menu", rec.StopReason);
    }
}
