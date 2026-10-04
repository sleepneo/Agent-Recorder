namespace AgentRecorder.Api;

public sealed record FutureWindowOneShotCreateRequest(
    string IdempotencyKey,
    string ExecutablePath,
    string? SystemAudioEndpointId,
    int MaximumDurationSeconds,
    int ValiditySeconds,
    string OutputDirectory);

public sealed record FutureWindowAuthorizationState(
    string AuthorizationId,
    string Status,
    string? ReasonCode,
    string ExecutablePath,
    string ExecutableFileIdentity,
    string ExecutableSha256,
    string? PublisherSubject,
    string? PublisherCertificateSha256,
    string AudioMode,
    string? SystemAudioEndpointId,
    string? SystemAudioEndpointName,
    int MaximumDurationSeconds,
    int ValiditySeconds,
    string OutputPath,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset? ApprovedAtUtc,
    DateTimeOffset? ExpiresAtUtc,
    string? RunId,
    string? RunStatus,
    string? WindowId,
    int? ProcessId,
    string? OutputEvidencePath,
    long? OutputSizeBytes,
    long? ActualDurationMs,
    long Version,
    DateTimeOffset? LatestPermissibleStartAtUtc = null);

public sealed record FutureWindowAuthorizationCreateResponse(
    string Result,
    FutureWindowAuthorizationState Authorization);

public sealed record FutureWindowAuthorizationRunResponse(
    string Result,
    string RunId,
    string StatusUrl);

public sealed record FutureWindowAuthorizationRevokeResponse(
    bool RevocationAccepted,
    FutureWindowAuthorizationState Authorization);

public sealed record FutureWindowAuthorizationStartResult(bool Accepted, string? RunId, string? ReasonCode);

public interface IFutureWindowOneShotGateway
{
    bool IsSetupSupported { get; }
    bool IsExecutionSupported { get; }
    bool IsInteractiveDesktopAvailable { get; }

    FutureWindowAuthorizationCreateResponse CreateOrGet(FutureWindowOneShotCreateRequest request);
    FutureWindowAuthorizationState? Get(string authorizationId);
    FutureWindowAuthorizationStartResult Start(string authorizationId, string windowId);
    FutureWindowAuthorizationState? Revoke(string authorizationId);
    IReadOnlyList<FutureWindowAuthorizationState> ListForSafetyCenter();
}
