using AgentRecorder.Capture;
using AgentRecorder.Windows;

namespace AgentRecorder.Core;

/// <summary>
/// Immutable persisted authorization scope for one future top-level window.
/// This is deliberately separate from fixed-region Lease specifications.
/// </summary>
internal sealed record FutureWindowAuthorizationScope(
    string AuthorizationId,
    string IdempotencyKey,
    string RequestDigest,
    string CurrentUserSid,
    string SessionBinding,
    FutureWindowExecutableIdentity ExecutableIdentity,
    string? SystemAudioEndpointId,
    string? SystemAudioEndpointName,
    int MaximumDurationSeconds,
    int ValiditySeconds,
    string OutputDirectory,
    string OutputFileName,
    string StatusCode,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset? ApprovedAtUtc,
    DateTimeOffset? ExpiresAtUtc,
    string? ApprovalId,
    string? RunId,
    long Version)
{
    internal string OutputFilePath => Path.GetFullPath(Path.Combine(OutputDirectory, OutputFileName));
    internal bool HasSystemAudio => !string.IsNullOrWhiteSpace(SystemAudioEndpointId);
    internal string AuthorizationDigest => CaptureAuthorizationProofIssuer.ComputeFutureWindowAuthorizationDigest(this);
}

/// <summary>
/// Post-transaction evidence returned only after the one-run authorization was
/// atomically consumed. The process-local proof issuer revalidates this receipt
/// before making a capture proof.
/// </summary>
internal sealed class FutureWindowStartCommitReceipt
{
    internal FutureWindowStartCommitReceipt(
        FutureWindowAuthorizationScope authorization,
        FutureWindowProcessSnapshot process,
        string runId,
        string proofId,
        string proofNonce,
        DateTimeOffset committedAtUtc)
    {
        Authorization = authorization ?? throw new ArgumentNullException(nameof(authorization));
        Process = process ?? throw new ArgumentNullException(nameof(process));
        RunId = Required(runId, nameof(runId));
        ProofId = Required(proofId, nameof(proofId));
        ProofNonce = Required(proofNonce, nameof(proofNonce));
        CommittedAtUtc = committedAtUtc;
    }

    internal FutureWindowAuthorizationScope Authorization { get; }
    internal FutureWindowProcessSnapshot Process { get; }
    internal string RunId { get; }
    internal string ProofId { get; }
    internal string ProofNonce { get; }
    internal DateTimeOffset CommittedAtUtc { get; }

    private static string Required(string value, string parameterName) =>
        string.IsNullOrWhiteSpace(value) ? throw new ArgumentException("A committed field is required.", parameterName) : value;
}

/// <summary>
/// Process-local one-use handoff between the persisted start gate and the
/// existing RecordingEngine/WGC/AvSplit lifecycle.
/// </summary>
internal sealed class FutureWindowOneShotExecutionTicket
{
    private int _claimed;

    internal FutureWindowOneShotExecutionTicket(
        FutureWindowStartCommitReceipt receipt,
        FutureWindowOneShotProof proof)
    {
        Receipt = receipt ?? throw new ArgumentNullException(nameof(receipt));
        Proof = proof ?? throw new ArgumentNullException(nameof(proof));
    }

    internal FutureWindowStartCommitReceipt Receipt { get; }
    internal FutureWindowAuthorizationScope Authorization => Receipt.Authorization;
    internal FutureWindowProcessSnapshot Process => Receipt.Process;
    internal FutureWindowOneShotProof Proof { get; }
    internal string RunId => Receipt.RunId;
    internal DateTimeOffset CommittedAtUtc => Receipt.CommittedAtUtc;

    internal bool TryClaim() => Interlocked.CompareExchange(ref _claimed, 1, 0) == 0;
}

internal sealed record FutureWindowCaptureStartResult(
    bool Accepted,
    string? RunId,
    string? ReasonCode);
