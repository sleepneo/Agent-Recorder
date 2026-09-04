using System;
using System.Collections.Concurrent;
using System.Globalization;
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

[Collection("NonParallel-SystemQueryProviders")]
public sealed class CaptureAuthorizationProofTests
{
    [Fact]
    public void Proof_IsBoundedAndCanBeConsumedOnlyOnceEvenConcurrently()
    {
        var issued = DateTimeOffset.UtcNow;
        var proof = CreateProof(
            recordingId: "rec_proof_test",
            runId: "rec_proof_test",
            sourceId: "confirm_proof_test",
            issuedAt: issued,
            expiresAt: issued.AddMinutes(1));

        Assert.Equal(CaptureAuthorizationProofKind.InteractiveConfirmation, proof.Kind);
        Assert.Equal(CaptureAuthorizationProofState.Available, proof.State);
        Assert.False(proof.IsConsumed);

        var successes = new ConcurrentBag<bool>();
        Parallel.For(0, 64, iteration => successes.Add(Consume(proof)));

        Assert.Equal(1, successes.Count(success => success));
        Assert.Equal(63, successes.Count(success => !success));
        Assert.Equal(CaptureAuthorizationProofState.Consumed, proof.State);
        Assert.False(proof.TryConsume(DateTimeOffset.UtcNow, out var reason));
        Assert.Equal("proof_already_consumed", reason);
    }

    [Fact]
    public void Proof_ExpiredBeforeConsumption_FailsClosed()
    {
        var now = DateTimeOffset.UtcNow;
        var proof = CreateProof(
            recordingId: "rec_expired_test",
            runId: "rec_expired_test",
            sourceId: "confirm_expired_test",
            issuedAt: now.AddMinutes(-2),
            expiresAt: now.AddMinutes(-1));

        Assert.False(proof.TryConsume(now, out var reason));
        Assert.Equal("proof_expired", reason);
        Assert.Equal(CaptureAuthorizationProofState.Expired, proof.State);
    }

    [Fact]
    public void ProofBearingCaptureAndDeferredEntrances_RejectUnconsumedProof()
    {
        var proof = CreateProof(
            "rec_interface_test",
            "rec_interface_test",
            "confirm_interface_test",
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow.AddMinutes(1));
        var backend = new CountingBackend();
        ICaptureBackend captureInterface = backend;

        Assert.Throws<InvalidOperationException>(() => captureInterface.Start(new CaptureConfig(), proof));
        Assert.True(proof.TryConsume(DateTimeOffset.UtcNow, out _));
        captureInterface.Start(new CaptureConfig(), proof);
        Assert.Equal(1, backend.StartCalls);

        var deferredProof = CreateProof(
            "rec_deferred_interface_test",
            "rec_deferred_interface_test",
            "confirm_deferred_interface_test",
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow.AddMinutes(1));
        var deferred = new CountingDeferredBackend();
        IDeferredCaptureStartBackend deferredInterface = deferred;
        Assert.Throws<InvalidOperationException>(() => deferredInterface.StartCapture(deferredProof));
        Assert.True(deferredProof.TryConsume(DateTimeOffset.UtcNow, out _));
        deferredInterface.StartCapture(deferredProof);
        Assert.Equal(1, deferred.StartCaptureCalls);
    }

    [Fact]
    public void CaptureEntrances_ExposeOnlyProofBearingSignatures()
    {
        var backendStart = typeof(ICaptureBackend).GetMethods()
            .Where(method => method.Name == nameof(ICaptureBackend.Start))
            .Single();
        Assert.True(backendStart.IsAbstract);
        Assert.Equal(
            new[] { typeof(CaptureConfig), typeof(CaptureAuthorizationProof) },
            backendStart.GetParameters().Select(parameter => parameter.ParameterType));

        var deferredStart = typeof(IDeferredCaptureStartBackend).GetMethods()
            .Where(method => method.Name == nameof(IDeferredCaptureStartBackend.StartCapture))
            .Single();
        Assert.True(deferredStart.IsAbstract);
        Assert.Equal(
            new[] { typeof(CaptureAuthorizationProof) },
            deferredStart.GetParameters().Select(parameter => parameter.ParameterType));

        var screenshotCapture = typeof(IScreenshotFrameRunner).GetMethods()
            .Where(method => method.Name == nameof(IScreenshotFrameRunner.CaptureAsync))
            .Single();
        Assert.True(screenshotCapture.IsAbstract);
        Assert.Equal(
            new[]
            {
                typeof(ScreenshotFrameRequest),
                typeof(CaptureAuthorizationProof),
                typeof(System.Threading.CancellationToken)
            },
            screenshotCapture.GetParameters().Select(parameter => parameter.ParameterType));
    }

    [Fact]
    public void ApprovedProof_UsesPostApprovalStartupDeadlineAndFailsClosedAfterIt()
    {
        var approvalTime = new DateTimeOffset(2030, 1, 2, 3, 4, 5, TimeSpan.Zero);
        var recording = CreateRecording();
        var confirmation = new Confirmation
        {
            RecordingId = recording.Id,
            CreatedAtUtc = approvalTime.UtcDateTime.AddSeconds(-4),
            TimeoutSeconds = 5
        };
        Assert.True(confirmation.TryDecide("approved"));
        recording.ConfirmationId = confirmation.Id;
        var plan = CreatePlan(recording.Config);

        var proof = CaptureAuthorizationProofIssuer.IssueInteractiveConfirmation(
            recording, confirmation, plan, approvalTime);

        Assert.Equal(
            approvalTime.AddSeconds(CaptureAuthorizationProofIssuer.InteractivePostApprovalStartupDeadlineSeconds),
            proof.ExpiresAtUtc);
        Assert.True(CaptureAuthorizationGate.TryConsumeInteractive(
            proof,
            recording,
            plan,
            confirmation,
            allowSyntheticTestAuthorization: false,
            nowUtc: approvalTime.AddSeconds(29),
            out var startupFailure));
        Assert.Equal("", startupFailure);

        var expiredProof = CaptureAuthorizationProofIssuer.IssueInteractiveConfirmation(
            recording, confirmation, plan, approvalTime);
        Assert.False(CaptureAuthorizationGate.TryConsumeInteractive(
            expiredProof,
            recording,
            plan,
            confirmation,
            allowSyntheticTestAuthorization: false,
            nowUtc: approvalTime.AddSeconds(30),
            out var expiryFailure));
        Assert.Equal("proof_expired", expiryFailure);
    }

    [Fact]
    public void CaptureDigests_AreCultureIndependentAndExcludeDisplayText()
    {
        var recording = CreateRecording();
        recording.Config.DisplayStableIdentity = "display-stable-2";
        recording.Config.DisplayIdentityStatus = DisplayIdentityResolutionStatus.Resolved;
        recording.Config.DisplayBounds = (-1920, 0, 1920, 1080);
        recording.Config.WindowTitle = "初始窗口";
        recording.Config.WindowHandle = (nint)123456789;
        recording.Config.Microphone = true;
        recording.Config.MicDevice = "mic-device-2";
        recording.Config.MicDeviceName = "麦克风";
        recording.Config.AudioSourceKind = AudioCaptureSourceKind.SystemLoopback;
        recording.Config.SystemLoopbackEndpoint = "endpoint-2";
        recording.Config.SystemLoopbackEndpointName = "扬声器";
        recording.Config.SystemLoopbackEndpointIsDefault = true;
        recording.Config.Fps = 59;
        recording.Config.CountdownSeconds = 10;
        recording.Config.RegionNormalizedBounds = (640, 480);
        recording.Config.ScreenshotSeries = new ScreenshotSeriesConfig
        {
            IntervalMs = 1250,
            MaxCount = 7,
            MaxDurationSeconds = 19,
            PlannedFrameCount = 7
        };
        var plan = new CapturePlan(
            "wgc-continuous",
            "wgc-continuous",
            new CaptureBackendSelectionEvidence("wgc-continuous", "wgc-continuous", "test", "cached", null, false),
            "display_surface",
            "display",
            "display-stable-2",
            (nint)123456789,
            new CapturePlanBounds(-1920, 0, 1920, 1080),
            targetDisplayIdentity: "display-stable-2",
            displayBounds: new CapturePlanBounds(-1920, 0, 1920, 1080),
            targetDisplayId: "display_2",
            targetDisplayIdentityStatus: DisplayIdentityResolutionStatus.Resolved,
            audioSourceKind: AudioCaptureSourceKind.SystemLoopback,
            audioEndpointId: "endpoint-2",
            audioEndpointName: "扬声器",
            audioEndpointIsDefault: true);

        var oldCulture = CultureInfo.CurrentCulture;
        var oldUiCulture = CultureInfo.CurrentUICulture;
        try
        {
            string? expectedPlan = null;
            string? expectedScope = null;
            foreach (var cultureName in new[] { "en-US", "zh-CN", "ar-SA" })
            {
                var culture = CultureInfo.GetCultureInfo(cultureName);
                CultureInfo.CurrentCulture = culture;
                CultureInfo.CurrentUICulture = culture;

                var planDigest = CaptureAuthorizationProofIssuer.ComputeCapturePlanDigest(plan);
                var scopeDigest = CaptureAuthorizationProofIssuer.ComputeCaptureScopeDigest(recording, plan);
                expectedPlan ??= planDigest;
                expectedScope ??= scopeDigest;
                Assert.Equal(expectedPlan, planDigest);
                Assert.Equal(expectedScope, scopeDigest);
            }

            var renamedPlan = new CapturePlan(
                "wgc-continuous",
                "wgc-continuous",
                plan.Evidence,
                "display_surface",
                "display",
                "display-stable-2",
                (nint)123456789,
                new CapturePlanBounds(-1920, 0, 1920, 1080),
                targetDisplayIdentity: "display-stable-2",
                displayBounds: new CapturePlanBounds(-1920, 0, 1920, 1080),
                targetDisplayId: "display_2",
                targetDisplayIdentityStatus: DisplayIdentityResolutionStatus.Resolved,
                audioSourceKind: AudioCaptureSourceKind.SystemLoopback,
                audioEndpointId: "endpoint-2",
                audioEndpointName: "Different localized speaker name",
                audioEndpointIsDefault: true);
            Assert.Equal(
                expectedPlan,
                CaptureAuthorizationProofIssuer.ComputeCapturePlanDigest(renamedPlan));

            recording.Config.WindowTitle = "Different localized title";
            recording.Config.MicDeviceName = "Different localized microphone name";
            recording.Config.SystemLoopbackEndpointName = "Different localized endpoint name";
            Assert.Equal(
                expectedScope,
                CaptureAuthorizationProofIssuer.ComputeCaptureScopeDigest(recording, plan));

            recording.Config.DurationSeconds = 6;
            Assert.NotEqual(
                expectedScope,
                CaptureAuthorizationProofIssuer.ComputeCaptureScopeDigest(recording, plan));
        }
        finally
        {
            CultureInfo.CurrentCulture = oldCulture;
            CultureInfo.CurrentUICulture = oldUiCulture;
        }
    }

    [Fact]
    public void Gate_RejectsPlanAndRunMismatchesBeforeConsumption()
    {
        var recording = CreateRecording();
        var plan = CreatePlan(recording.Config);
        var proof = CaptureAuthorizationProofIssuer.IssueForTests(recording, plan);
        recording.AuthorizationProof = proof;
        recording.SyntheticAuthorizationForTests = true;

        recording.Config.Bounds = (1, 0, 32, 32);
        Assert.False(CaptureAuthorizationGate.TryConsumeInteractive(
            proof,
            recording,
            plan,
            confirmation: null,
            allowSyntheticTestAuthorization: true,
            out var scopeReason));
        Assert.Equal("capture_scope_mismatch", scopeReason);
        Assert.Equal(CaptureAuthorizationProofState.Available, proof.State);

        var mismatchedRunProof = CreateProof(
            recordingId: recording.Id,
            runId: "run_other",
            sourceId: "test_confirmation",
            issuedAt: DateTimeOffset.UtcNow,
            expiresAt: DateTimeOffset.UtcNow.AddMinutes(1),
            planDigest: CaptureAuthorizationProofIssuer.ComputeCapturePlanDigest(plan),
            scopeDigest: CaptureAuthorizationProofIssuer.ComputeCaptureScopeDigest(recording, plan));
        Assert.False(CaptureAuthorizationGate.TryConsumeInteractive(
            mismatchedRunProof,
            recording,
            plan,
            confirmation: null,
            allowSyntheticTestAuthorization: true,
            out var runReason));
        Assert.Equal("recording_or_run_mismatch", runReason);
        Assert.Equal(CaptureAuthorizationProofState.Available, mismatchedRunProof.State);
    }

    [Fact]
    public void ApprovedLocalConfirmation_IssuesAndConsumesProof_WithoutApiSerialization()
    {
        var oldDataDir = Environment.GetEnvironmentVariable("AGENT_RECORDER_DATA_DIR");
        var dataDir = Path.Combine(Path.GetTempPath(), "authorization-proof-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dataDir);
        Environment.SetEnvironmentVariable("AGENT_RECORDER_DATA_DIR", dataDir);
        SystemQuery.SetDisplayProvider(() => new List<SystemQuery.DisplayInfo>
        {
            new("display_1", "Test Display", true, new SystemQuery.Bounds(0, 0, 1920, 1080), 1.0)
        });

        try
        {
            var backend = new CountingBackend();
            var tray = new ApproveTray();
            using var engine = new RecordingEngine(new AuditLogger())
            {
                BackendFactory = _ => (backend, "fake")
            };
            engine.SetTray(tray);
            engine.CapturePlanFactoryForTests = cfg => CreatePlan(cfg);

            var result = engine.CreateRecording(
                JsonNode.Parse("{\"source\":{\"type\":\"display\",\"display_id\":\"display_1\"},\"stop_condition\":{\"type\":\"duration\",\"seconds\":5}}")!,
                "test-agent",
                tray);

            var rec = engine._recs.Values.Single();
            Assert.Equal(1, backend.StartCalls);
            Assert.Equal(RecState.recording, rec.State);
            Assert.NotNull(rec.AuthorizationProof);
            Assert.IsType<InteractiveConfirmationProof>(rec.AuthorizationProof);
            Assert.Equal(CaptureAuthorizationProofKind.InteractiveConfirmation, rec.AuthorizationProof!.Kind);
            Assert.Equal(CaptureAuthorizationProofState.Consumed, rec.AuthorizationProof.State);
            Assert.Equal(rec.Id, rec.AuthorizationProof.RecordingId);
            Assert.Equal(rec.Id, rec.AuthorizationProof.RunId);
            Assert.Equal(rec.ConfirmationId, rec.AuthorizationProof.AuthorizationSourceId);

            var serialized = JsonSerializer.Serialize(result);
            Assert.DoesNotContain("proof_id", serialized, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("nonce", serialized, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(rec.AuthorizationProof.ProofId, serialized, StringComparison.Ordinal);
        }
        finally
        {
            SystemQuery.SetDisplayProvider(null);
            Environment.SetEnvironmentVariable("AGENT_RECORDER_DATA_DIR", oldDataDir);
            try { if (Directory.Exists(dataDir)) Directory.Delete(dataDir, recursive: true); } catch { }
        }
    }

    [Fact]
    public void PendingOrRejectedConfirmation_NeverIssuesProof()
    {
        var oldDataDir = Environment.GetEnvironmentVariable("AGENT_RECORDER_DATA_DIR");
        var dataDir = Path.Combine(Path.GetTempPath(), "authorization-pending-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dataDir);
        Environment.SetEnvironmentVariable("AGENT_RECORDER_DATA_DIR", dataDir);
        SystemQuery.SetDisplayProvider(() => new List<SystemQuery.DisplayInfo>
        {
            new("display_1", "Test Display", true, new SystemQuery.Bounds(0, 0, 1920, 1080), 1.0)
        });

        try
        {
            foreach (var mode in new[] { DecisionMode.Pending, DecisionMode.Rejected })
            {
                var backend = new CountingBackend();
                var tray = new ApproveTray { Mode = mode };
                using var engine = new RecordingEngine(new AuditLogger())
                {
                    BackendFactory = _ => (backend, "fake")
                };
                engine.SetTray(tray);
                engine.CapturePlanFactoryForTests = cfg => CreatePlan(cfg);
                engine.CreateRecording(
                    JsonNode.Parse("{\"source\":{\"type\":\"display\",\"display_id\":\"display_1\"},\"stop_condition\":{\"type\":\"duration\",\"seconds\":5}}")!,
                    "test-agent",
                    tray);

                var rec = engine._recs.Values.Single();
                Assert.Equal(0, backend.StartCalls);
                Assert.Null(rec.AuthorizationProof);
                Assert.False(rec.State is RecState.preparing or RecState.countdown or RecState.recording);
                if (mode == DecisionMode.Pending)
                {
                    engine.TriggerConfirmationExpiryForTests(rec.ConfirmationId!);
                    Assert.Equal(RecState.expired, rec.State);
                    Assert.Null(rec.AuthorizationProof);
                }
            }
        }
        finally
        {
            SystemQuery.SetDisplayProvider(null);
            Environment.SetEnvironmentVariable("AGENT_RECORDER_DATA_DIR", oldDataDir);
            try { if (Directory.Exists(dataDir)) Directory.Delete(dataDir, recursive: true); } catch { }
        }
    }

    private static Recording CreateRecording()
    {
        var output = Path.Combine(Path.GetTempPath(), "authorization-proof-test.mp4");
        return new Recording
        {
            SourceType = "display",
            OutputPath = output,
            DurationSeconds = 5,
            Config = new CaptureConfig
            {
                SourceKind = "display",
                Bounds = (0, 0, 32, 32),
                OutputPath = output,
                DurationSeconds = 5,
                CountdownSeconds = 0
            }
        };
    }

    private static CapturePlan CreatePlan(CaptureConfig cfg) => new(
        "fake",
        "fake",
        new CaptureBackendSelectionEvidence("fake", "fake", "test", "not_run", null, false),
        "display_surface",
        cfg.SourceKind,
        null,
        nint.Zero,
        new CapturePlanBounds(cfg.Bounds.x, cfg.Bounds.y, cfg.Bounds.w, cfg.Bounds.h),
        coordinateSpace: "virtual_screen");

    private static CaptureAuthorizationProof CreateProof(
        string recordingId,
        string runId,
        string sourceId,
        DateTimeOffset issuedAt,
        DateTimeOffset expiresAt,
        string? planDigest = null,
        string? scopeDigest = null) => new InteractiveConfirmationProof(
        "auth_test_" + Guid.NewGuid().ToString("N"),
        recordingId,
        runId,
        sourceId,
        planDigest ?? "plan_digest",
        scopeDigest ?? "scope_digest",
        issuedAt,
        expiresAt,
        CaptureAuthorizationSessionBinding.Current,
        5,
        null);

    private static bool Consume(CaptureAuthorizationProof proof)
        => proof.TryConsume(DateTimeOffset.UtcNow, out var ignored);

    private enum DecisionMode { Pending, Rejected, Approved }

    private sealed class ApproveTray : ITrayContext
    {
        public DecisionMode Mode { get; set; } = DecisionMode.Approved;
        public string HostMode => "headless";
        public bool SupportsRegionSelectionUi => true;
        public void RequestConfirmation(RecordingConfirmationPresentation presentation, Action<ConfirmationDecision> callback)
        {
            if (Mode == DecisionMode.Pending) return;
            callback(Mode == DecisionMode.Approved
                ? ConfirmationDecision.Approve()
                : ConfirmationDecision.Reject());
        }
        public void RequestRegionSelection(int timeoutSeconds, Action<string, int, int, int, int, string, string> callback)
            => callback("display_unavailable", 0, 0, 0, 0, "", "virtual_screen");
        public void SetRecording(RecordingUiPresentation rec) { }
        public void SetPreparing(RecordingUiPresentation rec) { }
        public void SetCountdown(RecordingUiPresentation rec) { }
        public void SetFinalizing(RecordingUiPresentation rec) { }
        public void SetIdle(RecordingUiPresentation rec) { }
        public void SetAllIdle() { }
        public void ShowError(string text) { }
    }

    private sealed class CountingBackend : ICaptureBackend
    {
        public int StartCalls;
        public void Start(CaptureConfig cfg, CaptureAuthorizationProof authorizationProof)
        {
            authorizationProof.RequireConsumed();
            StartCalls++;
        }
        public OutputMeta Stop() => new();
        public void OnNaturalExit(Action<int, OutputMeta> callback) { }
        public void Dispose() { }
    }

    private sealed class CountingDeferredBackend : IDeferredCaptureStartBackend
    {
        public int StartCaptureCalls;
        public bool IsAwaitingCaptureStart => true;
        public event Action<bool>? CaptureAuthorizationCompleted
        {
            add { }
            remove { }
        }
        public void StartCapture(CaptureAuthorizationProof authorizationProof)
        {
            authorizationProof.RequireConsumed();
            StartCaptureCalls++;
        }
    }
}
