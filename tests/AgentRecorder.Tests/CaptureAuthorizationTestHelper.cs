using System;
using System.Threading;
using System.Threading.Tasks;
using AgentRecorder.Capture;

namespace AgentRecorder.Tests;

/// <summary>
/// Central test-only seam for low-level capture tests. Production interfaces
/// require a consumed proof; these helpers make that prerequisite explicit
/// without adding a proof-less production overload or test backdoor.
/// </summary>
internal static class CaptureAuthorizationTestHelper
{
    internal static CaptureAuthorizationProof CreateSyntheticConsumedProof()
    {
        var now = DateTimeOffset.UtcNow;
        var proof = new InteractiveConfirmationProof(
            proofId: "auth_test_" + Guid.NewGuid().ToString("N"),
            recordingId: "test_recording",
            runId: "test_recording",
            authorizationSourceId: "test_confirmation",
            capturePlanDigest: "test_plan_digest",
            scopeDigest: "test_scope_digest",
            issuedAtUtc: now,
            expiresAtUtc: now.AddMinutes(1),
            userSessionBinding: CaptureAuthorizationSessionBinding.Current,
            maxDurationSeconds: null,
            maxFrameCount: null);

        if (!proof.TryConsume(now, out var reason))
            throw new InvalidOperationException("Failed to create a consumed synthetic capture proof: " + reason);

        return proof;
    }

    internal static void StartWithSyntheticConsumedProof(
        ICaptureBackend backend,
        CaptureConfig config)
    {
        if (backend == null) throw new ArgumentNullException(nameof(backend));
        if (config == null) throw new ArgumentNullException(nameof(config));
        backend.Start(config, CreateSyntheticConsumedProof());
    }

    internal static void StartDeferredWithSyntheticConsumedProof(
        IDeferredCaptureStartBackend backend)
    {
        if (backend == null) throw new ArgumentNullException(nameof(backend));
        backend.StartCapture(CreateSyntheticConsumedProof());
    }

    internal static Task<ScreenshotFrameResult> CaptureFrameWithSyntheticConsumedProof(
        IScreenshotFrameRunner runner,
        ScreenshotFrameRequest request,
        CancellationToken cancellationToken)
    {
        if (runner == null) throw new ArgumentNullException(nameof(runner));
        if (request == null) throw new ArgumentNullException(nameof(request));
        return runner.CaptureAsync(request, CreateSyntheticConsumedProof(), cancellationToken);
    }
}
