using System;
using AgentRecorder.Capture;
using AgentRecorder.Core.Automation;

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

    /// <summary>
    /// Validates the already-consumed standing proof at the final engine
    /// boundary. A standing proof is consumed by the durable start-gate path;
    /// this method deliberately never consumes it a second time and never
    /// consults an interactive confirmation.
    /// </summary>
    internal static bool TryValidateStanding(
        CaptureAuthorizationProof? proof,
        Recording recording,
        CapturePlan? currentPlan,
        AuthorizedFixedRegionScope scope,
        DateTimeOffset nowUtc,
        out string failureReason)
    {
        failureReason = "standing_proof_missing";
        if (proof is not StandingLeaseUseProof standing ||
            recording is null ||
            currentPlan is null ||
            scope is null)
            return false;

        if (nowUtc.Offset != TimeSpan.Zero ||
            standing.IssuedAtUtc.Offset != TimeSpan.Zero ||
            standing.ExpiresAtUtc.Offset != TimeSpan.Zero ||
            standing.ExpiresAtUtc <= standing.IssuedAtUtc)
        {
            failureReason = "standing_proof_time_invalid";
            return false;
        }

        if (nowUtc < standing.IssuedAtUtc || nowUtc >= standing.ExpiresAtUtc)
        {
            failureReason = "standing_proof_expired";
            return false;
        }

        if (!standing.IsConsumed)
        {
            failureReason = "standing_proof_not_consumed";
            return false;
        }

        if (!recording.IsStandingLeaseExecution ||
            standing.Kind != CaptureAuthorizationProofKind.StandingLeaseUse ||
            !string.Equals(standing.RecordingId, recording.Id, StringComparison.Ordinal) ||
            !string.Equals(standing.RunId, recording.Id, StringComparison.Ordinal))
        {
            failureReason = "standing_proof_run_mismatch";
            return false;
        }

        if (!string.Equals(standing.LeaseId, scope.LeaseId, StringComparison.Ordinal) ||
            !string.Equals(standing.LeaseUseId, recording.StandingLeaseUseId, StringComparison.Ordinal))
        {
            // The use id is checked against the ticket/specification by the
            // standing host. Keep this gate strict without accepting a caller
            // supplied use id as a substitute for the process-local ticket.
            failureReason = "standing_proof_scope_mismatch";
            return false;
        }

        if (!string.Equals(standing.ScopeDigest, scope.ScopeDigest, StringComparison.Ordinal))
        {
            failureReason = "standing_proof_scope_mismatch";
            return false;
        }

        if (!string.Equals(
                standing.UserSessionBinding,
                standing.CurrentUserSid + "|" + standing.SessionBinding,
                StringComparison.Ordinal))
        {
            failureReason = "standing_proof_session_binding_invalid";
            return false;
        }

        if (!string.Equals(standing.SessionBinding, CaptureAuthorizationSessionBinding.Current, StringComparison.Ordinal))
        {
            failureReason = "standing_proof_session_binding_invalid";
            return false;
        }

        var durationSeconds = recording.DurationSeconds ?? recording.Config.DurationSeconds ?? 0;
        if (durationSeconds <= 0 ||
            recording.CountdownSeconds != 0 ||
            scope.CountdownSeconds != 0 ||
            standing.MaxDuration != scope.ReservedDuration ||
            standing.MaxDuration != TimeSpan.FromSeconds(durationSeconds))
        {
            failureReason = "standing_authorization_limits_mismatch";
            return false;
        }

        try
        {
            var endTicks = checked(nowUtc.UtcDateTime.Ticks + standing.MaxDuration.Ticks);
            if (endTicks > standing.ExpiresAtUtc.UtcDateTime.Ticks)
            {
                failureReason = "standing_proof_duration_exceeds_window";
                return false;
            }
        }
        catch (OverflowException)
        {
            failureReason = "standing_proof_duration_exceeds_window";
            return false;
        }

        failureReason = "";
        return true;
    }
}
