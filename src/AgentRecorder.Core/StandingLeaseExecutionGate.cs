using AgentRecorder.Capture;
using AgentRecorder.Core.Automation;

namespace AgentRecorder.Core;

/// <summary>
/// The dedicated standing-proof execution gate. It is separate from the
/// interactive confirmation gate and is the only Core seam that may consume a
/// StandingLeaseUseProof for future unattended execution.
/// </summary>
internal static class StandingLeaseExecutionGate
{
    // Persistence must call this only while its write transaction remains open.
    // The method intentionally returns no execution context: the context is
    // constructed by Persistence only after that transaction commits.
    internal static bool TryAuthorizeAndConsumeStandingLeaseUseInTransaction(
        CaptureAuthorizationProof? proof,
        StandingLeaseExecutionSnapshot? snapshot,
        StandingLeaseExecutionEnvironment? environment,
        out string failureReason)
    {
        failureReason = "proof_missing";

        // 1. Establish the expected proof kind before considering any
        // snapshot or environment supplied by a caller.
        if (proof is null)
        {
            return false;
        }

        if (proof.Kind != CaptureAuthorizationProofKind.StandingLeaseUse)
        {
            failureReason = "proof_kind_not_allowed";
            return false;
        }

        if (proof is not StandingLeaseUseProof standingProof)
        {
            failureReason = "proof_kind_not_allowed";
            return false;
        }

        if (snapshot is null)
        {
            failureReason = "execution_snapshot_missing";
            return false;
        }

        if (environment is null)
        {
            failureReason = "execution_environment_missing";
            return false;
        }

        var nowUtc = environment.NowUtc;

        // 2. Read the one-time state without changing it. TryConsume is
        // intentionally deferred until every other check has passed.
        if (!standingProof.CheckAvailableAt(nowUtc, out failureReason))
        {
            return false;
        }

        // 3. Bind all proof identities to the rehydrated snapshot.
        if (!string.Equals(standingProof.RunId, snapshot.Run.Id, StringComparison.Ordinal) ||
            !string.Equals(standingProof.RecordingId, snapshot.Run.Id, StringComparison.Ordinal) ||
            !string.Equals(standingProof.LeaseId, snapshot.Lease.Id, StringComparison.Ordinal) ||
            !string.Equals(standingProof.LeaseUseId, snapshot.Use.Id, StringComparison.Ordinal) ||
            !string.Equals(standingProof.ScopeDigest, snapshot.Scope.ScopeDigest, StringComparison.Ordinal))
        {
            failureReason = "proof_identity_mismatch";
            return false;
        }

        // 4. Validate source, standing plan digest, session binding and exact
        // millisecond duration against the trusted snapshot.
        if (!string.Equals(
                standingProof.AuthorizationSourceId,
                "standing-lease-use:" + snapshot.Use.Id,
                StringComparison.Ordinal))
        {
            failureReason = "proof_source_mismatch";
            return false;
        }

        if (!string.Equals(
                standingProof.CapturePlanDigest,
                CaptureAuthorizationProofIssuer.ComputeStandingLeaseUsePlanDigest(snapshot.Plan),
                StringComparison.Ordinal))
        {
            failureReason = "proof_plan_digest_mismatch";
            return false;
        }

        var scope = snapshot.Scope;
        if (!string.Equals(standingProof.CurrentUserSid, scope.CurrentUserSid, StringComparison.Ordinal) ||
            !string.Equals(standingProof.SessionBinding, scope.SessionBinding, StringComparison.Ordinal) ||
            !string.Equals(
                standingProof.UserSessionBinding,
                standingProof.CurrentUserSid + "|" + standingProof.SessionBinding,
                StringComparison.Ordinal) ||
            standingProof.MaxDuration != scope.ReservedDuration ||
            standingProof.MaxDurationMilliseconds != scope.ReservedDuration.Ticks / TimeSpan.TicksPerMillisecond ||
            standingProof.MaxDurationSeconds is not null ||
            standingProof.MaxFrameCount is not null)
        {
            failureReason = "proof_binding_mismatch";
            return false;
        }

        // 5. Require UTC proof timestamps and the half-open execution interval.
        if (nowUtc.Offset != TimeSpan.Zero ||
            standingProof.IssuedAtUtc.Offset != TimeSpan.Zero ||
            standingProof.ExpiresAtUtc.Offset != TimeSpan.Zero)
        {
            failureReason = "execution_time_not_utc";
            return false;
        }

        if (nowUtc < standingProof.IssuedAtUtc)
        {
            failureReason = "proof_not_yet_valid";
            return false;
        }

        if (nowUtc >= standingProof.ExpiresAtUtc)
        {
            failureReason = "proof_expired";
            return false;
        }

        // 6. The complete reserved duration must fit without intersecting or
        // shortening any authorization boundary.
        if (scope.ReservedDuration <= TimeSpan.Zero ||
            scope.ReservedDuration > AuthorizedFixedRegionScope.MaximumReservedDuration)
        {
            failureReason = "execution_duration_out_of_scope";
            return false;
        }

        long endTicks;
        try
        {
            endTicks = checked(nowUtc.UtcDateTime.Ticks + scope.ReservedDuration.Ticks);
        }
        catch (OverflowException)
        {
            failureReason = "execution_duration_out_of_scope";
            return false;
        }

        if (nowUtc < snapshot.Occurrence.WindowStartUtc ||
            nowUtc >= snapshot.Occurrence.WindowEndUtc)
        {
            failureReason = "occurrence_not_executable";
            return false;
        }

        if (nowUtc < snapshot.Lease.ValidFromUtc ||
            nowUtc >= snapshot.Lease.ValidUntilUtc)
        {
            failureReason = "lease_not_executable";
            return false;
        }

        if (endTicks > snapshot.Occurrence.WindowEndUtc.UtcDateTime.Ticks ||
            endTicks > snapshot.Lease.ValidUntilUtc.UtcDateTime.Ticks ||
            endTicks > standingProof.ExpiresAtUtc.UtcDateTime.Ticks)
        {
            failureReason = "execution_duration_out_of_scope";
            return false;
        }

        // 7. Rehydration already enforces the scope constructor boundary; the
        // gate repeats the immutable policy and digest check before consume so
        // a manually supplied internal snapshot cannot widen the scope.
        if (snapshot.Plan.Status != PlanDefinitionStatus.Enabled ||
            !snapshot.Plan.IsOneTime ||
            scope.AuthorizationVersion != AuthorizedFixedRegionScope.CurrentAuthorizationVersion ||
            scope.TargetType != AuthorizedScopeTargetType.FixedRegion ||
            scope.CaptureSemantics != AuthorizedCaptureSemantics.DesktopRegion ||
            scope.CoordinateSpace != AuthorizedCoordinateSpace.PhysicalVirtualScreen ||
            scope.DisplayIdentityStatus != AuthorizedDisplayIdentityStatus.Resolved ||
            scope.Backend != AuthorizedCaptureBackend.FfmpegRegion ||
            scope.AudioMode != AuthorizedAudioMode.None ||
            scope.WakePolicy != AuthorizedWakePolicy.NaturalWakeOnly ||
            scope.DesktopRequirement != AuthorizedDesktopRequirement.InteractiveDesktopRequired ||
            !string.Equals(
                AuthorizedFixedRegionScopeDigest.Compute(scope),
                scope.ScopeDigest,
                StringComparison.Ordinal))
        {
            failureReason = "execution_scope_policy_mismatch";
            return false;
        }

        try
        {
            _ = scope.VirtualScreenRegion;
            _ = scope.OutputFilePath;
        }
        catch
        {
            failureReason = "execution_scope_projection_invalid";
            return false;
        }

        // 8. Revalidate the complete current environment before consumption.
        // This includes identity, desktop, topology, fixed-region projection,
        // and frozen output filesystem state. No caller string is trusted.
        if (!StandingLeaseExecutionEnvironmentValidator.TryValidate(
                environment,
                scope,
                out failureReason))
        {
            return false;
        }

        // 9. Re-run the Task 229 start-committed evidence invariants on the
        // same complete snapshot, including relation and timeline checks.
        try
        {
            CaptureAuthorizationProofIssuer.ValidateStandingReceipt(
                new StandingLeaseUseProofIssuanceReceipt(
                    snapshot.Plan,
                    snapshot.Occurrence,
                    snapshot.Lease,
                    snapshot.Scope,
                    snapshot.Run,
                    snapshot.Use,
                    snapshot.Run.CreatedAtUtc));
        }
        catch
        {
            failureReason = "execution_snapshot_invalid";
            return false;
        }

        if (snapshot.Occurrence.Status != PlanOccurrenceStatus.RunCreated ||
            snapshot.Run.Status != RecordingRunStatus.StartCommitted ||
            !snapshot.Run.HasCrossedStartCommit ||
            !snapshot.Run.IsNonRetryable ||
            snapshot.Run.MediaArtifactId is not null ||
            snapshot.Run.BundleId is not null ||
            snapshot.Use.Status != LeaseUseStatus.StartCommitted ||
            !snapshot.Use.IsQuotaConsumed ||
            snapshot.Use.ReservedUseCount != 1 ||
            snapshot.Use.ActualSettledDuration is not null ||
            snapshot.Lease.Status != ConsentLeaseStatus.Exhausted)
        {
            failureReason = "execution_state_not_start_committed";
            return false;
        }

        // 10. This is the sole state-changing operation. The proof's lock
        // provides the at-most-once linearization point for concurrent calls.
        if (!standingProof.TryConsume(nowUtc, out failureReason))
        {
            return false;
        }

        failureReason = "";
        return true;
    }
}
