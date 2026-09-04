using System;
using AgentRecorder.Capture;

namespace AgentRecorder.Core;

/// <summary>
/// The single Core-side validation and one-time consumption gate for all
/// capture-producing paths.
/// </summary>
internal static class CaptureAuthorizationGate
{
    internal static bool TryConsumeInteractive(
        CaptureAuthorizationProof? proof,
        Recording recording,
        CapturePlan? currentPlan,
        Confirmation? confirmation,
        bool allowSyntheticTestAuthorization,
        out string failureReason)
        => TryConsumeInteractive(
            proof,
            recording,
            currentPlan,
            confirmation,
            allowSyntheticTestAuthorization,
            DateTimeOffset.UtcNow,
            out failureReason);

    internal static bool TryConsumeInteractive(
        CaptureAuthorizationProof? proof,
        Recording recording,
        CapturePlan? currentPlan,
        Confirmation? confirmation,
        bool allowSyntheticTestAuthorization,
        DateTimeOffset nowUtc,
        out string failureReason)
    {
        failureReason = "proof_missing";
        if (proof == null || recording == null || currentPlan == null)
            return false;

        if (recording.State is RecState.stopping or RecState.finalizing or RecState.completed or
            RecState.failed or RecState.cancelled or RecState.rejected or RecState.expired)
        {
            failureReason = "recording_not_startable";
            return false;
        }

        if (proof.Kind != CaptureAuthorizationProofKind.InteractiveConfirmation)
        {
            failureReason = "proof_kind_not_allowed";
            return false;
        }

        if (!string.Equals(proof.RecordingId, recording.Id, StringComparison.Ordinal) ||
            !string.Equals(proof.RunId, recording.Id, StringComparison.Ordinal))
        {
            failureReason = "recording_or_run_mismatch";
            return false;
        }

        if (!allowSyntheticTestAuthorization)
        {
            if (confirmation == null ||
                !string.Equals(confirmation.RecordingId, recording.Id, StringComparison.Ordinal) ||
                !string.Equals(confirmation.Id, recording.ConfirmationId, StringComparison.Ordinal) ||
                !string.Equals(confirmation.Id, proof.AuthorizationSourceId, StringComparison.Ordinal) ||
                !string.Equals(confirmation.Status, "approved", StringComparison.Ordinal))
            {
                failureReason = "confirmation_not_approved_or_mismatched";
                return false;
            }
        }
        else if (recording.ConfirmationId != null &&
                 !string.Equals(recording.ConfirmationId, proof.AuthorizationSourceId, StringComparison.Ordinal))
        {
            failureReason = "confirmation_mismatch";
            return false;
        }

        if (!string.Equals(
                proof.CapturePlanDigest,
                CaptureAuthorizationProofIssuer.ComputeCapturePlanDigest(currentPlan),
                StringComparison.Ordinal))
        {
            failureReason = "capture_plan_mismatch";
            return false;
        }

        if (!string.Equals(
                proof.ScopeDigest,
                CaptureAuthorizationProofIssuer.ComputeCaptureScopeDigest(recording, currentPlan),
                StringComparison.Ordinal))
        {
            failureReason = "capture_scope_mismatch";
            return false;
        }

        if (!string.Equals(
                proof.UserSessionBinding,
                CaptureAuthorizationSessionBinding.Current,
                StringComparison.Ordinal))
        {
            failureReason = "user_session_mismatch";
            return false;
        }

        if (proof.MaxDurationSeconds != (recording.DurationSeconds ?? recording.Config.DurationSeconds) ||
            proof.MaxFrameCount != recording.Config.ScreenshotSeries?.PlannedFrameCount)
        {
            failureReason = "authorization_limits_mismatch";
            return false;
        }

        return proof.TryConsume(nowUtc.ToUniversalTime(), out failureReason);
    }
}
