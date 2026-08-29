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

        public void RequestConfirmation(RecordingConfirmationPresentation presentation, Action<ConfirmationDecision> callback) { }
        public void RequestRegionSelection(int timeoutSeconds,
            Action<string, int, int, int, int, string, string> callback) { }
        public void SetRecording(RecordingUiPresentation rec) { }
        public void SetIdle(RecordingUiPresentation rec) { }
        public void SetAllIdle() { }
        public void ShowError(string text) { }
    }

    private sealed class FailureTray : ITrayContext, IRecordingFailureNotifier
    {
        public string HostMode => "tray";
        public bool SupportsRegionSelectionUi => true;
        public int SetIdleCallCount { get; private set; }
        public List<string> FailureReasons { get; } = new();
        public List<string> CallOrder { get; } = new();

        public void RequestConfirmation(RecordingConfirmationPresentation presentation, Action<ConfirmationDecision> callback) { }
        public void RequestRegionSelection(int timeoutSeconds,
            Action<string, int, int, int, int, string, string> callback) { }
        public void SetRecording(RecordingUiPresentation rec) { }
        public void SetIdle(RecordingUiPresentation rec)
        {
            SetIdleCallCount++;
            CallOrder.Add("idle");
        }
        public void SetAllIdle() { }
        public void ShowError(string text) { }
        public void ShowRecordingFailure(string recordingId, string reasonCode)
        {
            FailureRecordingIds.Add(recordingId);
            FailureReasons.Add(reasonCode);
            CallOrder.Add("notify");
        }
        public List<string> FailureRecordingIds { get; } = new();
    }

    private sealed class TerminalTracer : IPerformanceTracer
    {
        public string? Status { get; private set; }
        public string? StopReason { get; private set; }
        public string? ErrorCode { get; private set; }

        public void RecordingTerminal(string traceId, string recordingId, string status,
            string? stopReason = null, string? errorCode = null)
        {
            Status = status;
            StopReason = stopReason;
            ErrorCode = errorCode;
        }

        public void IntentAccepted(string traceId, string endpoint, string? clientSentAtUtc = null) { }
        public void SetEnsureContextAssociation(string traceId, EnsureContextAssociation association) { }
        public void IntentValidated(string traceId, string endpoint, bool success, string? errorCode = null) { }
        public void CorrelationSet(string traceId, string recordingId, string? confirmationId = null, string? sourceType = null) { }
        public bool HasValidationResult(string traceId) => false;
        public void ConfirmationCreated(string traceId, string recordingId, string confirmationId) { }
        public void ConfirmationShown(string traceId, string recordingId, string confirmationId) { }
        public void ConfirmationApproved(string traceId, string recordingId, string confirmationId) { }
        public void ConfirmationRejected(string traceId, string recordingId, string confirmationId) { }
        public void ConfirmationExpired(string traceId, string recordingId, string confirmationId) { }
        public void CaptureStartRequested(string traceId, string recordingId, string backendType) { }
        public void CaptureBackendStartReturned(string traceId, string recordingId, string backendType) { }
        public void CaptureBackendStartFailed(string traceId, string recordingId, string backendType, string errorCode, string errorType) { }
        public void MicrophonePrepareStarted(string traceId, string recordingId) { }
        public void MicrophoneReady(string traceId, string recordingId) { }
        public void CountdownStarted(string traceId, string recordingId) { }
        public void CaptureFirstFrameObserved(string traceId, string recordingId, FirstFrameEvidence evidence) { }
        public void CaptureEnded(string traceId, string recordingId) { }
        public void FinalizationCompleted(string traceId, string recordingId, bool success) { }
        public void LongPollCompleted(string traceId, string kind, int requestedWaitMs, int actualWaitMs, bool changed, string? recordingId = null, string? confirmationId = null) { }
        public void Flush() { }
        public string? ResolveTraceId(string? recordingId = null, string? confirmationId = null) => null;
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
        OutputMeta? stopMeta = null,
        string backendType = "fake",
        ITrayContext? tray = null,
        IPerformanceTracer? tracer = null)
    {
        var audit = new CaptureAuditLogger();
        var chosenTray = tray ?? new NoOpTray();
        var engine = new RecordingEngine(audit, tracer);
        engine.SetTray(chosenTray);

        var backend = new FakeCaptureBackend
        {
            StopResult = stopMeta ?? new OutputMeta { DurationSeconds = 4.4, SizeBytes = 263781 }
        };
        engine.BackendFactory = _ => (backend, backendType);

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

        engine.StartCaptureForTests(rec, chosenTray);
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
    public void Stop_AutoHfpDiscoveryFailure_PreservesRootCauseAndPairDiagnosticsInApiAndAudit()
    {
        var (engine, rec, backend, audit) = Setup(30, new OutputMeta
        {
            DurationSeconds = 4.4,
            SizeBytes = 263781,
            AudioStatus = "lost",
            AudioContinuityStatus = "not_checked",
            AudioHelperErrorCode = "audio_hfp_pair_discovery_failed",
            AudioCaptureStrategy = "hfp-auto-pair-discovery",
            AudioPairEvidence = "hfp_pair_discovery_failed",
            AudioAutoHfpPairStatus = "ambiguous",
            AudioAutoHfpPairResultCode = "audio_hfp_pair_discovery_failed",
            AudioHelperFailureReason = "multiple active same-container candidates",
            AudioHelperFailureStage = "HfpPairDiscovery",
            AudioHelperFailureHresult = "0x80004005",
            AudioEstimatedGapMs = 0,
            AudioMaxEstimatedGapMs = 0,
            Stage = "HfpPairDiscovery"
        });
        rec.Microphone = true;
        backend.ExitCodeValue = 1;

        engine.Stop(rec.Id, "helper_failure");

        Assert.Equal(RecState.failed, rec.State);
        Assert.Equal("audio_hfp_pair_discovery_failed", rec.Error);

        using var status = JsonDocument.Parse(JsonSerializer.Serialize(engine.GetStatus(rec.Id)));
        var microphone = status.RootElement.GetProperty("audio").GetProperty("microphone");
        Assert.Equal("hfp-auto-pair-discovery", microphone.GetProperty("capture_strategy").GetString());
        Assert.Equal("ambiguous", microphone.GetProperty("auto_hfp_pair_status").GetString());
        Assert.Equal("audio_hfp_pair_discovery_failed", microphone.GetProperty("auto_hfp_pair_result_code").GetString());
        Assert.Equal("multiple active same-container candidates", microphone.GetProperty("helper_failure_reason").GetString());
        Assert.Equal("HfpPairDiscovery", microphone.GetProperty("helper_failure_stage").GetString());
        Assert.Equal("0x80004005", microphone.GetProperty("helper_failure_hresult").GetString());

        var failed = Assert.Single(audit.Events, e => e.evt == "recording.failed");
        using var auditJson = JsonDocument.Parse(failed.json);
        Assert.Equal("audio_hfp_pair_discovery_failed", auditJson.RootElement.GetProperty("error").GetString());
        Assert.Equal("ambiguous", auditJson.RootElement.GetProperty("audio_auto_hfp_pair_status").GetString());
        Assert.Equal("audio_hfp_pair_discovery_failed", auditJson.RootElement.GetProperty("audio_auto_hfp_pair_result_code").GetString());
        Assert.Equal("HfpPairDiscovery", auditJson.RootElement.GetProperty("stage").GetString());
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
    [InlineData("window_closed")]
    [InlineData("window_minimized")]
    [InlineData("size_changed")]
    [InlineData("display_unavailable")]
    public void WgcLifecycleFailure_PropagatesPublicEvidenceAndNotifiesOnce(string reason)
    {
        var tray = new FailureTray();
        var tracer = new TerminalTracer();
        string outputPath = Path.Combine(Path.GetTempPath(), $"wgc-lifecycle-{Guid.NewGuid():N}.mp4");
        var meta = new OutputMeta
        {
            DurationSeconds = 2.4,
            SizeBytes = 263781,
            StopReason = reason,
            OutputPath = outputPath,
            OutputFileExists = false,
            Warnings = new[] { "wgc_continuous_" + reason }
        };
        var (engine, rec, backend, audit) = Setup(
            30, meta, backendType: "wgc-continuous", tray: tray, tracer: tracer);

        backend.FireNaturalExit(0, meta);

        Assert.Equal("wgc-continuous", rec.BackendType);
        Assert.Equal(RecState.failed, rec.State);
        Assert.Equal(reason, rec.StopReason);
        Assert.Equal(reason, rec.Error);
        Assert.Equal(1, tray.SetIdleCallCount);
        Assert.Single(tray.FailureReasons, reason);
        Assert.Single(tray.FailureRecordingIds, rec.Id);
        Assert.Equal(new[] { "idle", "notify" }, tray.CallOrder);
        Assert.Equal("failed", tracer.Status);
        Assert.Equal(reason, tracer.StopReason);
        Assert.Equal(reason, tracer.ErrorCode);

        var statusJson = JsonSerializer.Serialize(engine.GetStatus(rec.Id));
        var waitJson = JsonSerializer.Serialize(engine.GetStatusWait(rec.Id, "recording", 100));
        var outputJson = JsonSerializer.Serialize(engine.GetOutput(rec.Id));
        Assert.Equal(reason, JsonDocument.Parse(statusJson).RootElement.GetProperty("stop_reason").GetString());
        Assert.Equal(reason, JsonDocument.Parse(waitJson).RootElement.GetProperty("stop_reason").GetString());
        Assert.Equal(reason, JsonDocument.Parse(outputJson).RootElement.GetProperty("stop_reason").GetString());
        Assert.Contains(audit.Events, e => e.evt == "recording.failed" && e.json.Contains($"\"error\":\"{reason}\""));
        Assert.Contains(audit.Events, e => e.evt == "recording.failed" && e.json.Contains($"\"stop_reason\":\"{reason}\""));
        Assert.False(File.Exists(outputPath));
    }

    [Fact]
    public void WgcContinuous_OutputValidationFailure_IsPublicCategoryWithoutProcessFailure()
    {
        var tray = new FailureTray();
        var tracer = new TerminalTracer();
        string outputPath = Path.Combine(Path.GetTempPath(), $"wgc-validation-{Guid.NewGuid():N}.mp4");
        var meta = new OutputMeta
        {
            DurationSeconds = 10.833,
            SizeBytes = 1340075,
            StopReason = "output_validation_failed",
            OutputPath = outputPath,
            OutputFileExists = false,
            Warnings = new[]
            {
                "duration_mismatch: probe=10833ms summary=10008ms",
                "wgc_continuous_output_validation_failed"
            }
        };
        var (engine, rec, backend, audit) = Setup(
            30, meta, backendType: "wgc-continuous", tray: tray, tracer: tracer);

        backend.FireNaturalExit(0, meta);

        Assert.Equal(RecState.failed, rec.State);
        Assert.Equal(0, rec.ExitCode);
        Assert.Equal("output_validation_failed", rec.StopReason);
        Assert.Equal("output_validation_failed", rec.Error);
        Assert.Equal(1, tray.SetIdleCallCount);
        Assert.Empty(tray.FailureReasons);
        Assert.Equal(new[] { "idle" }, tray.CallOrder);
        Assert.Equal("output_validation_failed", tracer.ErrorCode);
        Assert.DoesNotContain(rec.Warnings, warning =>
            warning.Contains("unexpected_exit", StringComparison.Ordinal) ||
            warning.Contains("non_zero_exit", StringComparison.Ordinal) ||
            warning.Contains("unexpected_terminal_state", StringComparison.Ordinal));
        Assert.Contains(audit.Events, e => e.evt == "recording.failed" &&
                                           e.json.Contains("\"error\":\"output_validation_failed\""));
        Assert.False(File.Exists(outputPath));
    }

    [Theory]
    [InlineData("window_closed")]
    [InlineData("window_minimized")]
    [InlineData("size_changed")]
    public void UnrelatedBackend_DoesNotAcquireContinuousLifecycleMapping(string reason)
    {
        var tray = new FailureTray();
        var outputPath = Path.Combine(Path.GetTempPath(), $"unrelated-backend-{Guid.NewGuid():N}.mp4");
        var (engine, rec, backend, audit) = Setup(
            30,
            new OutputMeta
            {
                DurationSeconds = 2.4,
                SizeBytes = 263781,
                StopReason = reason,
                OutputPath = outputPath,
                OutputFileExists = false
            },
            backendType: "unrelated-backend",
            tray: tray);

        backend.FireNaturalExit(0, backend.StopResult);

        Assert.Equal("unrelated-backend", rec.BackendType);
        Assert.Equal(RecState.failed, rec.State);
        Assert.Equal("unexpected_exit", rec.StopReason);
        Assert.NotEqual(reason, rec.Error);
        Assert.Empty(tray.FailureReasons);
        Assert.Equal(new[] { "idle" }, tray.CallOrder);
        Assert.False(File.Exists(outputPath));
    }

    [Fact]
    public void UnrelatedBackend_DoesNotAcquireWgcLifecycleNotificationOrErrorMapping()
    {
        var tray = new FailureTray();
        var outputPath = Path.Combine(Path.GetTempPath(), $"fake-lifecycle-{Guid.NewGuid():N}.mp4");
        var (engine, rec, backend, audit) = Setup(
            30,
            new OutputMeta
            {
                DurationSeconds = 2.4,
                SizeBytes = 263781,
                StopReason = "window_closed",
                OutputPath = outputPath,
                OutputFileExists = false
            },
            backendType: "fake",
            tray: tray);

        backend.FireNaturalExit(0, backend.StopResult);

        Assert.Equal("fake", rec.BackendType);
        Assert.Equal(RecState.failed, rec.State);
        Assert.Equal("unexpected_exit", rec.StopReason);
        Assert.NotEqual("window_closed", rec.Error);
        Assert.Empty(tray.FailureReasons);
        Assert.Equal(new[] { "idle" }, tray.CallOrder);
        Assert.False(File.Exists(outputPath));
    }

    [Fact]
    public void WgcContinuous_UnknownStopReasonDoesNotBecomeStablePublicErrorCode()
    {
        var tray = new FailureTray();
        var outputPath = Path.Combine(Path.GetTempPath(), $"wgc-unknown-{Guid.NewGuid():N}.mp4");
        var (engine, rec, backend, audit) = Setup(
            30,
            new OutputMeta
            {
                DurationSeconds = 2.4,
                SizeBytes = 263781,
                StopReason = "private_native_detail",
                OutputPath = outputPath,
                OutputFileExists = false
            },
            backendType: "wgc-continuous",
            tray: tray);

        backend.FireNaturalExit(0, backend.StopResult);

        Assert.Equal("wgc-continuous", rec.BackendType);
        Assert.Equal(RecState.failed, rec.State);
        Assert.Equal("unexpected_exit", rec.StopReason);
        Assert.NotEqual("private_native_detail", rec.Error);
        Assert.Empty(tray.FailureReasons);
        Assert.Equal(new[] { "idle" }, tray.CallOrder);
        Assert.False(File.Exists(outputPath));
    }

    [Fact]
    public void WgcContinuous_NaturalCompletionAndUserStopRemainUnchanged()
    {
        var completionTray = new FailureTray();
        var (completionEngine, completionRec, completionBackend, _) = Setup(
            30,
            new OutputMeta { DurationSeconds = 30, SizeBytes = 263781 },
            backendType: "wgc-continuous",
            tray: completionTray);
        completionBackend.FireNaturalExit(0, completionBackend.StopResult);

        Assert.Equal("wgc-continuous", completionRec.BackendType);
        Assert.Equal(RecState.completed, completionRec.State);
        Assert.Equal("duration_reached", completionRec.StopReason);
        Assert.Null(completionRec.Error);
        Assert.Empty(completionTray.FailureReasons);
        Assert.Equal(new[] { "idle" }, completionTray.CallOrder);

        var stopTray = new FailureTray();
        var (stopEngine, stopRec, stopBackend, _) = Setup(
            30,
            new OutputMeta { DurationSeconds = 4.4, SizeBytes = 263781, StopReason = "window_closed" },
            backendType: "wgc-continuous",
            tray: stopTray);
        stopBackend.ExitCodeValue = 0;
        stopEngine.Stop(stopRec.Id, "user_requested");

        Assert.Equal("wgc-continuous", stopRec.BackendType);
        Assert.Equal(RecState.completed, stopRec.State);
        Assert.Equal("user_requested", stopRec.StopReason);
        Assert.Null(stopRec.Error);
        Assert.Empty(stopTray.FailureReasons);
        Assert.Equal(new[] { "idle" }, stopTray.CallOrder);
    }

    [Fact]
    public void WgcLifecycleFailureText_IsLocalizedInChineseAndEnglish()
    {
        var zh = new UiTextProvider(UiLanguage.ZhCn);
        var en = new UiTextProvider(UiLanguage.EnUs);

        Assert.Contains("目标窗口已关闭", zh.Get("Tray_Balloon_WindowClosedBody"));
        Assert.Contains("target window closed", en.Get("Tray_Balloon_WindowClosedBody"), StringComparison.OrdinalIgnoreCase);
        Assert.Contains("目标窗口已最小化", zh.Get("Tray_Balloon_WindowMinimizedBody"));
        Assert.Contains("target window", en.Get("Tray_Balloon_WindowMinimizedBody"), StringComparison.OrdinalIgnoreCase);
        Assert.Contains("尺寸已改变", zh.Get("Tray_Balloon_WindowResizedBody"));
        Assert.Contains("changed size", en.Get("Tray_Balloon_WindowResizedBody"), StringComparison.OrdinalIgnoreCase);
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
