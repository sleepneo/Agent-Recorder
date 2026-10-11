using AgentRecorder.Infrastructure;

namespace AgentRecorder.Persistence;

/// <summary>Validate persistent bindings only; never recheck running targets or expiry against now.</summary>
internal static class FutureHistoryValidation
{
    internal static void Validate(FutureWindowAuthorizationRow row)
    {
        bool Text(string? s, int max = 4096) => !string.IsNullOrWhiteSpace(s) && s == s.Trim() && s.Length <= max && !s.Contains('\0');
        bool Hex(string? s, int length) => s?.Length == length && s.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');
        void Require(bool valid) { if (!valid) throw new InvalidDataException("The persistent authorization is inconsistent."); }
        Require(UnifiedHistoryQueryService.ValidId(row.AuthorizationId) && Text(row.IdempotencyKey, 128) && Hex(row.RequestDigest, 64) &&
            Text(row.CurrentUserSid, 256) && Text(row.SessionBinding, 256) && row.Version >= 0 &&
            row.CreatedAtUtc.ToUnixTimeMilliseconds() > 0 && row.UpdatedAtUtc >= row.CreatedAtUtc &&
            row.MaximumDurationSeconds is >= 1 and <= 1800 && row.ValiditySeconds is >= 1 and <= 3600 &&
            row.MaximumDurationSeconds <= row.ValiditySeconds && Text(row.ExecutableIdentity.FileIdentity) &&
            Hex(row.ExecutableIdentity.Sha256, 64) &&
            (row.ExecutableIdentity.SignerSubject is null || Text(row.ExecutableIdentity.SignerSubject)) &&
            (row.ExecutableIdentity.SignerCertificateSha256 is null || Hex(row.ExecutableIdentity.SignerCertificateSha256, 64)));
        UnifiedHistoryQueryService.ValidatePath(row.ExecutableIdentity.CanonicalPath);
        UnifiedHistoryQueryService.ValidatePath(row.OutputDirectory);
        Require(Text(row.OutputFileName, 180) && Path.GetFileName(row.OutputFileName) == row.OutputFileName &&
            !row.OutputFileName.Contains('/') && !row.OutputFileName.Contains('\\') &&
            (row.SystemAudioEndpointId is null) == (row.SystemAudioEndpointName is null) &&
            (row.SystemAudioEndpointId is null || Text(row.SystemAudioEndpointId) && Text(row.SystemAudioEndpointName)) &&
            (row.ReasonCode is null || Text(row.ReasonCode)));
        var approved = row.ApprovedAtUtc is { } approvalTime && row.ExpiresAtUtc is { } expiryTime && Text(row.ApprovalId) &&
            approvalTime >= row.CreatedAtUtc && expiryTime == approvalTime.AddSeconds(row.ValiditySeconds) && row.UpdatedAtUtc >= approvalTime;
        var noApproval = row.ApprovedAtUtc is null && row.ExpiresAtUtc is null && row.ApprovalId is null;
        Require(approved || noApproval);
        var noRun = row.RunId is null && row.WindowId is null && row.ProcessId is null && row.ProcessCreationFileTimeUtc is null &&
            row.RunStatus is null && row.ProofId is null && row.ProofNonce is null;
        var run = approved && UnifiedHistoryQueryService.ValidId(row.RunId) && Text(row.WindowId) && row.ProcessId is > 0 &&
            row.ProcessCreationFileTimeUtc > row.ApprovedAtUtc!.Value.UtcDateTime.ToFileTimeUtc() && Text(row.ProofId) && Hex(row.ProofNonce, 32) &&
            row.RunStatus is "start_committed" or "recording" or "finalizing" or "completed" or "stopped" or "failed" or "started_unknown";
        Require(noRun || run);
        Require(row.StatusCode switch
        {
            "pending" => noApproval && noRun && row.ReasonCode is null,
            "active" => approved && noRun && row.ReasonCode is null,
            "used" => run && row.RunStatus is "start_committed" or "recording" or "finalizing" or "started_unknown",
            "completed" => run && row.RunStatus == "completed" && row.ReasonCode is null,
            "revoked" => row.ReasonCode is not null && (noRun || row.RunStatus is "start_committed" or "recording" or "finalizing" or "started_unknown" or "stopped"),
            "expired" or "blocked" => noRun && row.ReasonCode is not null,
            "failed" => row.ReasonCode is not null && (noRun || row.RunStatus is "failed" or "stopped"),
            _ => false,
        });
        if (row.RunStatus == "started_unknown") Require(row.ReasonCode is not null);
        if (row.StatusCode == "completed")
        {
            Require(row.OutputPath is not null && row.OutputSizeBytes is > 0 && row.ActualDurationMs is >= 0 &&
                row.ActualDurationMs <= (row.MaximumDurationSeconds + 2L) * 1000);
            UnifiedHistoryQueryService.ValidatePath(row.OutputPath!);
            Require(string.Equals(row.OutputPath, Path.Combine(row.OutputDirectory, row.OutputFileName), StringComparison.OrdinalIgnoreCase));
        }
        else Require(row.OutputPath is null && row.OutputSizeBytes is null && row.ActualDurationMs is null);
    }
}
