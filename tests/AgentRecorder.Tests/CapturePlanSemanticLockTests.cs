using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using AgentRecorder.App;
using AgentRecorder.Capture;
using AgentRecorder.Core;
using AgentRecorder.Infrastructure;
using AgentRecorder.Logging;
using AgentRecorder.Windows;
using Xunit;

namespace AgentRecorder.Tests;

[Collection("NonParallel-SystemQueryProviders")]
public sealed class CapturePlanSemanticLockTests : IDisposable
{
    private readonly RecordingPreflightChecker.TryGetFreeSpace _oldFreeSpace;
    private readonly RecordingPreflightChecker.TryGetEncoderPaths _oldEncoder;
    private readonly Func<bool, bool, List<SystemQuery.WindowInfo>>? _oldWindows;
    private readonly string? _oldTestMode;
    private readonly string? _oldWindowBackend;

    public CapturePlanSemanticLockTests()
    {
        _oldFreeSpace = RecordingPreflightChecker.FreeSpaceProvider;
        _oldEncoder = RecordingPreflightChecker.EncoderProvider;
        _oldWindows = GetWindowProvider();
        _oldTestMode = Environment.GetEnvironmentVariable("AGENT_RECORDER_TEST_MODE");
        _oldWindowBackend = Environment.GetEnvironmentVariable(CaptureBackendSelector.WgcEnvVar);

        RecordingPreflightChecker.FreeSpaceProvider = (string _, out long free) =>
        {
            free = 10L * 1024 * 1024 * 1024;
            return true;
        };
        RecordingPreflightChecker.EncoderProvider = (out string? ffmpeg, out string? ffprobe) =>
        {
            ffmpeg = typeof(CapturePlanSemanticLockTests).Assembly.Location;
            ffprobe = ffmpeg;
            return true;
        };
    }

    public void Dispose()
    {
        RecordingPreflightChecker.FreeSpaceProvider = _oldFreeSpace;
        RecordingPreflightChecker.EncoderProvider = _oldEncoder;
        SystemQuery.SetWindowProvider(_oldWindows);
        Environment.SetEnvironmentVariable("AGENT_RECORDER_TEST_MODE", _oldTestMode);
        Environment.SetEnvironmentVariable(CaptureBackendSelector.WgcEnvVar, _oldWindowBackend);
    }

    [Fact]
    public void BuildPlan_WgcWindowIsWindowSurface_AndFfmpegWindowIsScreenRectangle()
    {
        var wgc = WithWindowBackend("wgc-continuous", () =>
            CaptureBackendSelector.BuildPlan(WindowConfig(5), new FakeAvailabilityProbe(true)));
        Assert.Equal("wgc-continuous", wgc.PlannedBackend);
        Assert.Equal("window_surface", wgc.CaptureSemantics);
        Assert.Equal("window_4660", wgc.TargetIdentity);
        Assert.False(wgc.FallbackOccurred);

        var ffmpeg = WithWindowBackend(null, () =>
            CaptureBackendSelector.BuildPlan(WindowConfig(5), new FakeAvailabilityProbe(true)));
        Assert.Equal("ffmpeg-window-region", ffmpeg.PlannedBackend);
        Assert.Equal("screen_rectangle", ffmpeg.CaptureSemantics);
        Assert.Equal("window_4660", ffmpeg.TargetIdentity);
    }

    [Theory]
    [InlineData("wgc")]
    [InlineData(" WGC ")]
    public void BuildPlan_LegacyWindowAliasReportsContinuousWindowSurface(string alias)
    {
        var plan = WithWindowBackend(alias, () =>
            CaptureBackendSelector.BuildPlan(WindowConfig(5), new FakeAvailabilityProbe(true)));

        Assert.Equal("wgc-continuous", plan.RequestedBackend);
        Assert.Equal("wgc-continuous", plan.PlannedBackend);
        Assert.Equal("wgc-continuous", plan.Evidence.SelectedBackend);
        Assert.Equal("window_surface", plan.CaptureSemantics);
        Assert.False(plan.FallbackOccurred);
    }

    [Fact]
    public void BuildPlan_ThirtySecondWgcRequestFallsBackBeforeConfirmationAndDisclosesScreenRectangle()
    {
        var probe = new FakeAvailabilityProbe(true);
        var plan = WithWindowBackend("wgc-continuous", () =>
            CaptureBackendSelector.BuildPlan(WindowConfig(30), probe));

        Assert.Equal("ffmpeg-window-region", plan.PlannedBackend);
        Assert.Equal("screen_rectangle", plan.CaptureSemantics);
        Assert.Equal("duration_not_eligible", plan.Evidence.SelectionReasonCode);
        Assert.True(plan.FallbackOccurred);
        Assert.Equal(0, probe.CallCount);

        Environment.SetEnvironmentVariable("AGENT_RECORDER_TEST_MODE", "1");
        var tray = new TestTray { DeferConfirmation = true };
        var audit = new CapturingAudit();
        var engine = new RecordingEngine(audit);
        int backendFactoryCalls = 0;
        engine.BackendFactory = _ =>
        {
            backendFactoryCalls++;
            return (new CountingBackend(), "fake");
        };

        WithWindowBackend("wgc-continuous", () =>
        {
            engine.CreateRecording(WindowJson("window_4660", 30), "test-agent", tray);
            return true;
        });

        Assert.NotNull(tray.Summary);
        using var summary = JsonDocument.Parse(JsonSerializer.Serialize(tray.Summary));
        var root = summary.RootElement;
        Assert.Equal("screen_rectangle", root.GetProperty("capture_semantics").GetString());
        Assert.Equal("ffmpeg-window-region", root.GetProperty("planned_backend").GetString());
        Assert.Equal("window_4660", root.GetProperty("window_id").GetString());
        Assert.Equal("duration_not_eligible", root.GetProperty("selection_reason_code").GetString());
        Assert.Equal("not_run", root.GetProperty("selection_availability_source").GetString());
        Assert.True(root.GetProperty("selection_fallback").GetBoolean());
        Assert.Equal("screen_rectangle", root.GetProperty("preview_semantics").GetString());
        Assert.Equal(0, backendFactoryCalls);
        Assert.Null(engine._recs.Values.Single().Backend);
        Assert.Contains(audit.Events, e => e == "recording.capture_plan_created");
        var planAudit = audit.Payloads.Single(x => x.Event == "recording.capture_plan_created").Payload;
        Assert.Equal("window_4660", planAudit.GetProperty("target_identity").GetString());
        Assert.Equal("ffmpeg-window-region", planAudit.GetProperty("planned_backend").GetString());
        Assert.Equal("screen_rectangle", planAudit.GetProperty("capture_semantics").GetString());
        Assert.Equal("duration_not_eligible", planAudit.GetProperty("selection_reason_code").GetString());
        Assert.True(planAudit.GetProperty("fallback").GetBoolean());
    }

    [Fact]
    public void BuildPlan_IsNonCapturing_AndDoesNotInstantiateOrStartBackend()
    {
        CountingBackend.TotalConstructed = 0;
        CountingBackend.TotalStarts = 0;
        var probe = new FakeAvailabilityProbe(true);
        var plan = WithWindowBackend("wgc-continuous", () =>
            CaptureBackendSelector.BuildPlan(WindowConfig(5), probe));

        Assert.NotNull(plan);
        Assert.Equal(1, probe.CallCount);
        Assert.Equal("window_surface", plan.CaptureSemantics);
        Assert.Equal(0, CountingBackend.TotalConstructed);
        Assert.Equal(0, CountingBackend.TotalStarts);
    }

    [Fact]
    public void UnknownWindowBackendSemantics_FailsClosedWithStableApiError()
    {
        var ex = Assert.Throws<ApiException>(() =>
            CaptureBackendSelector.DetermineSemanticsForTests("window", "future-window-backend"));

        Assert.Equal("CAPTURE_SEMANTICS_UNKNOWN", ex.Code);
    }

    [Fact]
    public void WindowSurfacePreview_UsesOnlyDwm_AndReleasesThumbnailOnce()
    {
        RunSta(() =>
        {
            var screen = new CountingScreenPreviewProvider();
            var dwm = new FakeDwmThumbnailProvider();
            using var form = NewForm(WindowSurfaceSummary(), screen, dwm);

            form.Show();
            form.EnsureDwmThumbnailForTests();

            Assert.True(form.WindowSurfacePreviewForTests);
            Assert.Equal(0, screen.Calls);
            Assert.Equal(1, dwm.RegisterCalls);
            Assert.True(dwm.Thumbnail!.UpdateCalls > 0);
            Assert.True(form.DwmThumbnailActiveForTests);
            Assert.Equal(form.DwmDestinationWindowForTests, dwm.DestinationWindow);
            Assert.NotEqual(form.PreviewPanelHandleForTests, dwm.DestinationWindow);
            Assert.Equal((nint)12345, dwm.SourceWindow);
            Assert.Equal(form.DwmThumbnailDestinationForTests, dwm.Thumbnail.LastDestination);
            Assert.False(dwm.Thumbnail.SourceClientAreaOnly);

            var preview = form.PreviewPanelFormBoundsForTests;
            var destination = form.DwmThumbnailDestinationForTests;
            Assert.True(destination.Left >= preview.Left);
            Assert.True(destination.Top >= preview.Top);
            Assert.True(destination.Right <= preview.Right);
            Assert.True(destination.Bottom <= preview.Bottom);
            Assert.True(form.WindowSurfacePreviewSurfaceIsTransparentForTests);
            Assert.True(form.WindowSurfacePreviewChildrenAreHiddenForTests);

            form.CloseWithoutResult();
            form.Dispose();
            Assert.Equal(1, dwm.Thumbnail.DisposeCalls);
        });
    }

    [Fact]
    public void WindowSurfacePreview_DwmFailureShowsIdentityFallback_AndStillAllowsApproval()
    {
        RunSta(() =>
        {
            var screen = new CountingScreenPreviewProvider();
            var dwm = new FakeDwmThumbnailProvider { RegisterResult = false };
            using var form = NewForm(WindowSurfaceSummary(), screen, dwm);

            form.Show();

            Assert.Equal(0, screen.Calls);
            Assert.False(form.HasPreviewImageForTests);
            Assert.Contains("Notepad", form.PreviewFallbackTextForTests);
            Assert.Contains("窗口", form.PreviewFallbackTextForTests);
            Assert.True(form.ApproveButtonEnabledForTests);
            form.CloseWithoutResult();
        });
    }

    [Fact]
    public void WindowSurfacePreview_QueryFailureReleasesRegisteredThumbnailOnce()
    {
        RunSta(() =>
        {
            var screen = new CountingScreenPreviewProvider();
            var dwm = new FakeDwmThumbnailProvider { QueryResult = false };
            using var form = NewForm(WindowSurfaceSummary(), screen, dwm);

            form.Show();
            form.EnsureDwmThumbnailForTests();

            Assert.Equal(0, screen.Calls);
            Assert.False(form.DwmThumbnailActiveForTests);
            Assert.Equal(1, dwm.Thumbnail!.DisposeCalls);
            form.CloseWithoutResult();
            Assert.Equal(1, dwm.Thumbnail.DisposeCalls);
        });
    }

    [Fact]
    public void WindowSurfacePreview_UpdateFailureShowsIdentityFallback_AndReleasesOnce()
    {
        RunSta(() =>
        {
            var screen = new CountingScreenPreviewProvider();
            var dwm = new FakeDwmThumbnailProvider { UpdateResult = false };
            using var form = NewForm(WindowSurfaceSummary(), screen, dwm);

            form.Show();
            form.EnsureDwmThumbnailForTests();

            Assert.Equal(0, screen.Calls);
            Assert.False(form.DwmThumbnailActiveForTests);
            Assert.Equal(1, dwm.Thumbnail!.DisposeCalls);
            Assert.Contains("Notepad", form.PreviewFallbackTextForTests);
            form.CloseWithoutResult();
            form.Dispose();
            Assert.Equal(1, dwm.Thumbnail.DisposeCalls);
        });
    }

    [Fact]
    public void WindowSurfacePreview_HandleRecreation_ReRegistersAgainstNewTopLevelHwnd()
    {
        RunSta(() =>
        {
            var screen = new CountingScreenPreviewProvider();
            var dwm = new FakeDwmThumbnailProvider();
            using var form = NewForm(WindowSurfaceSummary(), screen, dwm);

            form.Show();
            System.Windows.Forms.Application.DoEvents();
            Assert.Equal(1, dwm.RegisterCalls);
            var oldHandle = form.DwmDestinationWindowForTests;
            var oldThumbnail = dwm.Thumbnails[0];

            // This invokes the real protected Control.RecreateHandle path.
            // The test deliberately does not call EnsureDwmThumbnailForTests.
            form.RecreateHandleForTests();
            System.Windows.Forms.Application.DoEvents();

            Assert.Equal(2, dwm.RegisterCalls);
            Assert.NotEqual(oldHandle, form.DwmDestinationWindowForTests);
            Assert.Equal(1, oldThumbnail.DisposeCalls);
            Assert.Equal(form.DwmDestinationWindowForTests, dwm.Thumbnails[1].DestinationWindow);
            Assert.Equal(oldThumbnail.SourceWindow, dwm.Thumbnails[1].SourceWindow);
            Assert.True(form.DwmThumbnailActiveForTests);
            Assert.True(dwm.Thumbnails[1].UpdateCalls > 0);
            Assert.False(dwm.Thumbnails[1].SourceClientAreaOnly);

            var preview = form.PreviewPanelFormBoundsForTests;
            var destination = form.DwmThumbnailDestinationForTests;
            Assert.True(destination.Left >= preview.Left);
            Assert.True(destination.Top >= preview.Top);
            Assert.True(destination.Right <= preview.Right);
            Assert.True(destination.Bottom <= preview.Bottom);
            Assert.Equal(destination, dwm.Thumbnails[1].LastDestination);
            Assert.Equal(0, screen.Calls);

            form.CloseWithoutResult();
            form.Dispose();
            Assert.Equal(1, oldThumbnail.DisposeCalls);
            Assert.Equal(1, dwm.Thumbnails[1].DisposeCalls);
        });
    }

    [Theory]
    [InlineData("register")]
    [InlineData("update")]
    public void WindowSurfacePreview_HandleRecreationFailure_ShowsIdentityCardWithoutGdi(string failure)
    {
        RunSta(() =>
        {
            var screen = new CountingScreenPreviewProvider();
            var dwm = new FakeDwmThumbnailProvider();
            if (failure == "register")
                dwm.RegisterResultAfterFirst = false;
            else
                dwm.UpdateResultAfterFirst = false;

            using var form = NewForm(WindowSurfaceSummary(), screen, dwm);
            form.Show();
            System.Windows.Forms.Application.DoEvents();
            var oldThumbnail = dwm.Thumbnails[0];

            form.RecreateHandleForTests();
            System.Windows.Forms.Application.DoEvents();

            Assert.Equal(2, dwm.RegisterCalls);
            Assert.Equal(1, oldThumbnail.DisposeCalls);
            Assert.False(form.DwmThumbnailActiveForTests);
            Assert.Contains("Notepad", form.PreviewFallbackTextForTests);
            Assert.Equal(0, screen.Calls);
            Assert.All(dwm.Thumbnails, thumbnail => Assert.Equal(1, thumbnail.DisposeCalls));

            form.CloseWithoutResult();
            form.Dispose();
            Assert.All(dwm.Thumbnails, thumbnail => Assert.Equal(1, thumbnail.DisposeCalls));
        });
    }

    [Theory]
    [InlineData("screen_rectangle", "window")]
    [InlineData("display_surface", "display")]
    [InlineData("region_rectangle", "region")]
    public void ComposedPixelSemantics_RetainGdiPreview(string semantics, string sourceType)
    {
        RunSta(() =>
        {
            var screen = new CountingScreenPreviewProvider();
            var dwm = new FakeDwmThumbnailProvider();
            using var form = NewForm(ComposedSummary(semantics, sourceType), screen, dwm);

            Assert.Equal(1, screen.Calls);
            Assert.True(form.HasPreviewImageForTests);
            Assert.Equal(0, dwm.RegisterCalls);
            form.CloseWithoutResult();
        });
    }

    [Theory]
    [InlineData(0, 0, 400, 200, 1600, 900, 22, 0, 356, 200)]
    [InlineData(10, -20, 600, 300, 1600, 900, 43, -20, 533, 300)]
    [InlineData(5, 7, 800, 400, 1600, 900, 49, 7, 711, 400)]
    [InlineData(-100, -50, 400, 200, 1600, 900, -78, -50, 356, 200)]
    public void DwmGeometry_UsesDevicePixels_IsCenteredAspectPreservingAndContained(
        int x, int y, int width, int height, int sourceWidth, int sourceHeight,
        int expectedX, int expectedY, int expectedWidth, int expectedHeight)
    {
        var panel = new Rectangle(x, y, width, height);
        var actual = DwmThumbnailGeometry.Fit(
            panel, new Size(sourceWidth, sourceHeight));

        Assert.Equal(new Rectangle(expectedX, expectedY, expectedWidth, expectedHeight), actual);
        Assert.Equal((panel.Width - actual.Width) / 2, actual.Left - panel.Left);
        Assert.Equal((panel.Height - actual.Height) / 2, actual.Top - panel.Top);
        Assert.True(actual.Left >= panel.Left);
        Assert.True(actual.Top >= panel.Top);
        Assert.True(actual.Right <= panel.Right);
        Assert.True(actual.Bottom <= panel.Bottom);
        Assert.InRange(
            Math.Abs((long)actual.Width * sourceHeight - (long)actual.Height * sourceWidth),
            0L,
            (long)Math.Max(sourceWidth, sourceHeight));
    }

    [Fact]
    public void ApprovedWindowSurfaceSemanticDrift_FailsBeforeCountdownUiBackendOrOutput()
    {
        Environment.SetEnvironmentVariable("AGENT_RECORDER_TEST_MODE", "1");
        InstallWindowProvider();

        var approved = Plan("wgc-continuous", "window_surface", "window_backend_selected");
        var changed = Plan("ffmpeg-window-region", "screen_rectangle", "duration_not_eligible", fallback: true);
        int planCalls = 0;
        var backend = new CountingBackend();
        var tray = new TestTray { DeferConfirmation = true };
        var audit = new CapturingAudit();
        var engine = new RecordingEngine(audit)
        {
            CapturePlanFactoryForTests = _ => ++planCalls == 1 ? approved : changed
        };
        engine.BackendFactory = _ => (backend, "fake");

        var outputDirectory = Path.Combine(Path.GetTempPath(), "agent-recorder-task-197", Guid.NewGuid().ToString("N"));
        var outputPath = Path.Combine(outputDirectory, "drift.mp4");
        var cfg = WindowJson("window_12345", 5, outputPath);
        engine.CreateRecording(cfg, "test-agent", tray);
        var rec = engine._recs.Values.Single();

        tray.Approve();

        Assert.Equal(2, planCalls);
        Assert.Equal(RecState.failed, rec.State);
        Assert.Equal("capture_semantics_changed", rec.Error);
        Assert.Null(rec.Backend);
        Assert.Equal(0, backend.StartCalls);
        Assert.Equal(0, tray.PreparingCalls);
        Assert.Equal(0, tray.CountdownCalls);
        Assert.Equal(0, tray.RecordingCalls);
        Assert.Equal(0, tray.FinalizingCalls);
        Assert.Equal("capture_semantics_changed", tray.LastFailureReason);
        Assert.False(File.Exists(outputPath));
        Assert.Contains(audit.Events, e => e == "recording.capture_plan_revalidated");
        Assert.Contains(audit.Events, e => e == "recording.capture_semantics_changed");
    }

    [Fact]
    public void SemanticLock_TargetIdentityChangeFailsClosedBeforeStart()
    {
        AssertRevalidationFailsBeforeStart(
            Plan("wgc-continuous", "window_surface", "window_backend_selected"),
            Plan("wgc-continuous", "window_surface", "window_backend_selected",
                targetIdentity: "window_99999", windowHandle: (nint)99999));
    }

    [Fact]
    public void SemanticLock_HwndChangeFailsClosedEvenWhenIdentityTextIsUnchanged()
    {
        AssertRevalidationFailsBeforeStart(
            Plan("wgc-continuous", "window_surface", "window_backend_selected"),
            Plan("wgc-continuous", "window_surface", "window_backend_selected",
                windowHandle: (nint)99999));
    }

    [Fact]
    public void SemanticLock_CoordinateSpaceChangeFailsClosedBeforeBackendStart()
    {
        AssertRevalidationFailsBeforeStart(
            Plan("wgc-continuous", "window_surface", "window_backend_selected", coordinateSpace: "virtual_screen"),
            Plan("wgc-continuous", "window_surface", "window_backend_selected", coordinateSpace: "screen_pixels"));
    }

    [Fact]
    public void SemanticLock_SourceKindChangeFailsClosedBeforeStart()
    {
        AssertRevalidationFailsBeforeStart(
            Plan("wgc-continuous", "window_surface", "window_backend_selected"),
            Plan("wgc-continuous", "display", "default_backend",
                sourceKind: "display", targetIdentity: null, windowHandle: nint.Zero));
    }

    [Fact]
    public void SemanticLock_SameBackendDifferentSemanticsFailsClosedBeforeStart()
    {
        AssertRevalidationFailsBeforeStart(
            Plan("wgc-continuous", "window_surface", "window_backend_selected"),
            Plan("wgc-continuous", "screen_rectangle", "window_backend_selected"));
    }

    [Fact]
    public void SemanticLock_RegionDisplayIdentityChangeFailsClosedBeforeStart()
    {
        AssertRevalidationFailsBeforeStart(
            Plan("wgc-continuous", "region_rectangle", "wgc_probe_success",
                sourceKind: "region", targetIdentity: null, windowHandle: nint.Zero,
                targetDisplayIdentity: "display-left"),
            Plan("wgc-continuous", "region_rectangle", "wgc_probe_success",
                sourceKind: "region", targetIdentity: null, windowHandle: nint.Zero,
                targetDisplayIdentity: "display-right"));
    }

    [Fact]
    public void SemanticLock_RegionDisplayOrCropBoundsChangeFailsClosedBeforeStart()
    {
        var approved = Plan("wgc-continuous", "region_rectangle", "wgc_probe_success",
            sourceKind: "region", targetIdentity: null, windowHandle: nint.Zero,
            targetDisplayIdentity: "display-left");
        var changed = new CapturePlan(
            "wgc-continuous",
            "wgc-continuous",
            approved.Evidence,
            "region_rectangle",
            "region",
            null,
            nint.Zero,
            new CapturePlanBounds(-1798, -100, 640, 480),
            "synthetic-test-display:display-left",
            new CapturePlanBounds(-1920, -200, 1920, 1080),
            "display-left",
            DisplayIdentityResolutionStatus.Resolved);

        AssertRevalidationFailsBeforeStart(approved, changed);
    }

    [Fact]
    public void StableWindowRevalidation_ReusesApprovedPlan_AndStartsExactlyOnce()
    {
        Environment.SetEnvironmentVariable("AGENT_RECORDER_TEST_MODE", "1");
        InstallWindowProvider();

        var approved = Plan("wgc-continuous", "window_surface", "wgc_probe_success");
        int planCalls = 0;
        var backend = new CountingBackend();
        var tray = new TestTray { DeferConfirmation = true };
        var engine = new RecordingEngine(new CapturingAudit())
        {
            CapturePlanFactoryForTests = _ =>
            {
                planCalls++;
                return new CapturePlan(
                    approved.RequestedBackend,
                    approved.PlannedBackend,
                    approved.Evidence,
                    approved.CaptureSemantics,
                    approved.SourceKind,
                    approved.TargetIdentity,
                    approved.WindowHandle,
                    approved.Bounds);
            }
        };
        engine.BackendFactory = _ => (backend, "fake");

        engine.CreateRecording(WindowJson("window_12345", 5), "test-agent", tray);
        var rec = engine._recs.Values.Single();
        tray.Approve();

        Assert.Equal(2, planCalls);
        Assert.Equal(RecState.recording, rec.State);
        Assert.Same(approved.Evidence, rec.ApprovedCapturePlan!.Evidence);
        Assert.Equal("window_surface", rec.ApprovedCapturePlan.CaptureSemantics);
        Assert.Equal(1, backend.StartCalls);
    }

    [Fact]
    public void LocalizationAndFailureReasonExposeTheSemanticLock()
    {
        var zh = new UiTextProvider(UiLanguage.ZhCn);
        var en = new UiTextProvider(UiLanguage.EnUs);

        Assert.Contains("遮挡窗口", zh.Get("Confirmation_CaptureSemantics_WindowSurface"));
        Assert.Contains("covering", en.Get("Confirmation_CaptureSemantics_ScreenRectangle"), StringComparison.OrdinalIgnoreCase);
        Assert.Contains("retry", en.Get("Tray_RecordingFailure_CaptureSemanticsChangedBody"), StringComparison.OrdinalIgnoreCase);
        Assert.True(RecordingFailureNotificationManager.IsSupportedReason("capture_semantics_changed"));
    }

    private static JsonNode WindowJson(string windowId, int duration, string? outputPath = null) =>
        new JsonObject
        {
            ["source"] = new JsonObject
            {
                ["type"] = "window",
                ["window_id"] = windowId
            },
            ["video"] = new JsonObject { ["fps"] = 30 },
            ["stop_condition"] = new JsonObject { ["type"] = "duration", ["seconds"] = duration },
            ["output"] = new JsonObject
            {
                ["filename"] = outputPath ?? Path.Combine(Path.GetTempPath(), "agent-recorder-task-197.mp4")
            }
        };

    private static CaptureConfig WindowConfig(int duration) => new()
    {
        SourceKind = "window",
        WindowHandle = (nint)0x1234,
        Bounds = (75, 125, 1280, 720),
        DurationSeconds = duration,
        Fps = 30,
        OutputPath = "C:\\temp\\task-197.mp4"
    };

    private static CapturePlan Plan(
        string backend,
        string semantics,
        string reason,
        bool fallback = false,
        string sourceKind = "window",
        string? targetIdentity = "window_12345",
        nint windowHandle = (nint)12345,
        string? targetDisplayIdentity = null,
        CapturePlanBounds? sourceBounds = null,
        CapturePlanBounds? displayBounds = null,
        string coordinateSpace = "virtual_screen") =>
        new(
            backend == "wgc-continuous" ? "wgc-continuous" : "wgc-continuous",
            backend,
            new CaptureBackendSelectionEvidence(
                "wgc-continuous", backend, reason, fallback ? "not_run" : "fresh_probe", null, fallback),
            semantics,
            sourceKind,
            targetIdentity,
            windowHandle,
            sourceBounds ?? (sourceKind == "window" ? new CapturePlanBounds(0, 0, 1280, 720) :
                sourceKind == "region" ? new CapturePlanBounds(-1800, -100, 640, 480) : null),
            sourceKind == "region" && targetDisplayIdentity != null
                ? $"synthetic-test-display:{targetDisplayIdentity}"
                : null,
            displayBounds ?? (sourceKind == "region" ? new CapturePlanBounds(-1920, -200, 1920, 1080) : null),
            sourceKind == "region" ? targetDisplayIdentity : null,
            sourceKind == "region"
                ? DisplayIdentityResolutionStatus.Resolved
                : DisplayIdentityResolutionStatus.Unresolved,
            coordinateSpace: coordinateSpace);

    private static object WindowSurfaceSummary() => new
    {
        source = "window: Notepad",
        source_type = "window",
        source_title = "Notepad",
        source_application = "notepad.exe",
        window_id = "window_12345",
        capture_semantics = "window_surface",
        preview_semantics = "window_surface",
        capture_bounds = new { x = 20, y = -10, width = 1280, height = 720 },
        duration = "5s",
        output = "out.mp4",
        recording_id = "rec_1",
        confirmation_id = "conf_1",
        timeout_seconds = 60,
        expires_at = "2026-01-01T00:00:00Z"
    };

    private static object ComposedSummary(string semantics, string sourceType) => new
    {
        source = sourceType,
        source_type = sourceType,
        source_title = "Desktop",
        source_application = "",
        capture_semantics = semantics,
        preview_semantics = semantics,
        capture_bounds = new { x = 0, y = 0, width = 1280, height = 720 },
        duration = "5s",
        output = "out.mp4",
        recording_id = "rec_1",
        confirmation_id = "conf_1",
        timeout_seconds = 60,
        expires_at = "2026-01-01T00:00:00Z"
    };

    private static ConfirmationForm NewForm(object summary, CountingScreenPreviewProvider screen, FakeDwmThumbnailProvider dwm) =>
        new(
            new PendingConfirmationItem("conf_1", "rec_1", summary, _ => { }, 60),
            1,
            1,
            previewProvider: screen,
            dwmThumbnailProvider: dwm,
            textProvider: new UiTextProvider(UiLanguage.ZhCn))
        {
            EnableDelayedForegroundVerification = false
        };

    private static void InstallWindowProvider() =>
        SystemQuery.SetWindowProvider((_, _) => new()
        {
            new SystemQuery.WindowInfo(
                "window_12345", "Notepad", "notepad.exe", 42, false, false,
                new SystemQuery.Bounds(0, 0, 1280, 720))
        });

    private static void AssertRevalidationFailsBeforeStart(CapturePlan approved, CapturePlan changed)
    {
        Environment.SetEnvironmentVariable("AGENT_RECORDER_TEST_MODE", "1");
        InstallWindowProvider();

        int planCalls = 0;
        var backend = new CountingBackend();
        var tray = new TestTray { DeferConfirmation = true };
        var audit = new CapturingAudit();
        var topology = approved.SourceKind == "region"
            ? new FixedDisplayTopologyProvider(new[]
            {
                new DisplayTopologySnapshot(
                    approved.TargetDisplayId!,
                    approved.TargetDisplayIdentity!,
                    DisplayIdentityResolutionStatus.Resolved,
                    approved.DisplayBounds!)
            })
            : null;
        var engine = new RecordingEngine(audit, displayTopologyProvider: topology)
        {
            CapturePlanFactoryForTests = _ => ++planCalls == 1 ? approved : changed,
            BackendFactory = _ => (backend, "fake")
        };

        var outputPath = Path.Combine(Path.GetTempPath(), "agent-recorder-task-197b-drift",
            Guid.NewGuid().ToString("N"), "drift.mp4");
        engine.CreateRecording(WindowJson("window_12345", 5, outputPath), "test-agent", tray);
        var rec = engine._recs.Values.Single();
        if (approved.SourceKind == "region")
        {
            rec.Config.SourceKind = "region";
            rec.Config.DisplayId = approved.TargetDisplayId;
            rec.Config.DisplayStableIdentity = approved.TargetDisplayIdentity;
            rec.Config.DisplayIdentityStatus = DisplayIdentityResolutionStatus.Resolved;
            rec.Config.DisplayBounds = (
                approved.DisplayBounds!.X,
                approved.DisplayBounds.Y,
                approved.DisplayBounds.Width,
                approved.DisplayBounds.Height);
            rec.Config.Bounds = (
                approved.Bounds!.X,
                approved.Bounds.Y,
                approved.Bounds.Width,
                approved.Bounds.Height);
        }
        tray.Approve();

        Assert.Equal(2, planCalls);
        Assert.Equal(RecState.failed, rec.State);
        Assert.Equal("capture_semantics_changed", rec.Error);
        Assert.Null(rec.Backend);
        Assert.Equal(0, backend.StartCalls);
        Assert.Equal(0, tray.CountdownCalls);
        Assert.Equal(0, tray.RecordingCalls);
        Assert.False(File.Exists(outputPath));
    }

    private static Func<bool, bool, List<SystemQuery.WindowInfo>>? GetWindowProvider()
    {
        var field = typeof(SystemQuery).GetField("_windowProvider",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        var scoped = field?.GetValue(null);
        return scoped?.GetType().GetProperty("Value")?.GetValue(scoped)
            as Func<bool, bool, List<SystemQuery.WindowInfo>>;
    }

    private static T WithWindowBackend<T>(string? value, Func<T> action)
    {
        var previous = Environment.GetEnvironmentVariable(CaptureBackendSelector.WgcEnvVar);
        try
        {
            Environment.SetEnvironmentVariable(CaptureBackendSelector.WgcEnvVar, value);
            return action();
        }
        finally
        {
            Environment.SetEnvironmentVariable(CaptureBackendSelector.WgcEnvVar, previous);
        }
    }

    private static void RunSta(Action action)
    {
        Exception? error = null;
        var thread = new Thread(() =>
        {
            try { action(); }
            catch (Exception ex) { error = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (error != null)
            throw new System.Reflection.TargetInvocationException(error);
    }

    private sealed class FakeAvailabilityProbe : IWgcContinuousAvailabilityProbe
    {
        private readonly bool _available;
        public FakeAvailabilityProbe(bool available) => _available = available;
        public int CallCount { get; private set; }
        public WgcContinuousAvailabilityResult Check(CaptureConfig config)
        {
            CallCount++;
            return new WgcContinuousAvailabilityResult(_available, "probe_success", "fresh_probe", 4);
        }
    }

    private sealed class FixedDisplayTopologyProvider : IDisplayTopologyProvider
    {
        private readonly IReadOnlyList<DisplayTopologySnapshot> _displays;

        public FixedDisplayTopologyProvider(IReadOnlyList<DisplayTopologySnapshot> displays)
            => _displays = displays;

        public IReadOnlyList<DisplayTopologySnapshot> GetCurrentDisplays() => _displays;
    }

    private sealed class CountingBackend : ICaptureBackend
    {
        public CountingBackend() => TotalConstructed++;
        public static int TotalStarts;
        public static int TotalConstructed;
        public int StartCalls { get; private set; }
        public void Start(CaptureConfig cfg)
        {
            StartCalls++;
            TotalStarts++;
            cfg.CommandArgs = "fake";
        }
        public OutputMeta Stop() => new();
        public void OnNaturalExit(Action<int, OutputMeta> callback) { }
        public int ExitCode => 0;
        public void Dispose() { }
    }

    private sealed class CountingScreenPreviewProvider : IScreenPreviewProvider
    {
        public int Calls { get; private set; }
        public Bitmap Capture(CaptureBounds bounds, Size maxSize)
        {
            Calls++;
            return new Bitmap(Math.Max(1, maxSize.Width), Math.Max(1, maxSize.Height));
        }
    }

    private sealed class FakeDwmThumbnailProvider : IDwmThumbnailProvider
    {
        public bool RegisterResult { get; set; } = true;
        public bool RegisterResultAfterFirst { get; set; } = true;
        public bool QueryResult { get; set; } = true;
        public bool QueryResultAfterFirst { get; set; } = true;
        public bool UpdateResult { get; set; } = true;
        public bool UpdateResultAfterFirst { get; set; } = true;
        public int RegisterCalls { get; private set; }
        public nint DestinationWindow { get; private set; }
        public nint SourceWindow { get; private set; }
        private readonly List<FakeDwmThumbnail> _thumbnails = new();
        public IReadOnlyList<FakeDwmThumbnail> Thumbnails => _thumbnails;
        public FakeDwmThumbnail? Thumbnail => _thumbnails.LastOrDefault();
        public bool TryRegister(nint destinationWindow, nint sourceWindow, out IDwmThumbnail thumbnail)
        {
            RegisterCalls++;
            DestinationWindow = destinationWindow;
            SourceWindow = sourceWindow;
            bool registerResult = RegisterCalls == 1 ? RegisterResult : RegisterResultAfterFirst;
            if (!registerResult)
            {
                thumbnail = null!;
                return false;
            }

            var created = new FakeDwmThumbnail
            {
                DestinationWindow = destinationWindow,
                SourceWindow = sourceWindow,
                QueryResult = RegisterCalls == 1 ? QueryResult : QueryResultAfterFirst,
                UpdateResult = RegisterCalls == 1 ? UpdateResult : UpdateResultAfterFirst
            };
            _thumbnails.Add(created);
            thumbnail = created;
            return true;
        }
    }

    private sealed class FakeDwmThumbnail : IDwmThumbnail
    {
        public bool QueryResult { get; set; } = true;
        public bool UpdateResult { get; set; } = true;
        public int UpdateCalls { get; private set; }
        public int DisposeCalls { get; private set; }
        public nint DestinationWindow { get; set; }
        public nint SourceWindow { get; set; }
        public Rectangle LastDestination { get; private set; }
        public bool SourceClientAreaOnly { get; private set; }
        public bool TryQuerySourceSize(out Size sourceSize)
        {
            sourceSize = new Size(1280, 720);
            return QueryResult;
        }
        public bool TryUpdateDestination(Rectangle destination, bool sourceClientAreaOnly)
        {
            UpdateCalls++;
            LastDestination = destination;
            SourceClientAreaOnly = sourceClientAreaOnly;
            return UpdateResult && destination.Width > 0 && destination.Height > 0;
        }
        public void Dispose() => DisposeCalls++;
    }

    private sealed class TestTray : ITrayContext, IRecordingFailureNotifier
    {
        private Action<ConfirmationDecision>? _callback;
        public string HostMode => "headless";
        public bool SupportsRegionSelectionUi => false;
        public bool DeferConfirmation { get; set; }
        public object? Summary { get; private set; }
        public int PreparingCalls { get; private set; }
        public int CountdownCalls { get; private set; }
        public int RecordingCalls { get; private set; }
        public int FinalizingCalls { get; private set; }
        public string? LastError { get; private set; }
        public string? LastFailureReason { get; private set; }

        public void RequestConfirmation(object summary, Action<ConfirmationDecision> callback)
        {
            Summary = summary;
            if (DeferConfirmation)
                _callback = callback;
            else
                callback(ConfirmationDecision.Reject());
        }
        public void Approve() => _callback?.Invoke(ConfirmationDecision.Approve());
        public void RequestRegionSelection(int timeoutSeconds, Action<string, int, int, int, int, string, string> callback) { }
        public void SetRecording(object rec) => RecordingCalls++;
        public void SetIdle(object rec) { }
        public void SetAllIdle() { }
        public void ShowError(string text) => LastError = text;
        public void SetPreparing(object rec) => PreparingCalls++;
        public void SetCountdown(object rec, int? remainingSeconds) => CountdownCalls++;
        public void SetFinalizing(object rec) => FinalizingCalls++;
        public void ShowRecordingFailure(string recordingId, string reasonCode) => LastFailureReason = reasonCode;
    }

    private sealed class CapturingAudit : AuditLogger
    {
        public List<string> Events { get; } = new();
        public List<(string Event, JsonElement Payload)> Payloads { get; } = new();
        public override void Log(string evt, object payload)
        {
            Events.Add(evt);
            using var document = JsonDocument.Parse(JsonSerializer.Serialize(payload));
            Payloads.Add((evt, document.RootElement.Clone()));
        }
    }
}
