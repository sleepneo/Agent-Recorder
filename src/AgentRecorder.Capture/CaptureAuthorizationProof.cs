using System;
using System.Diagnostics;

namespace AgentRecorder.Capture;

/// <summary>
/// The kind of trusted, in-process authorization that may cross the main
/// process capture boundary. Standing lease use is named here only as a
/// future extension point; this version never issues one.
/// </summary>
public enum CaptureAuthorizationProofKind
{
    InteractiveConfirmation = 0,
    StandingLeaseUse = 1
}

/// <summary>
/// The lifecycle state of a capture authorization proof.
/// </summary>
public enum CaptureAuthorizationProofState
{
    Available = 0,
    Consumed = 1,
    Expired = 2,
    Revoked = 3
}

/// <summary>
/// Immutable, main-process-only authorization evidence for one capture run.
/// The constructor is intentionally internal: API payloads, JSON settings,
/// environment variables and helper command lines cannot manufacture a
/// proof. The object is never passed across the helper process boundary.
/// </summary>
public abstract class CaptureAuthorizationProof
{
    private readonly object _stateLock = new();
    private CaptureAuthorizationProofState _state;

    internal CaptureAuthorizationProof(
        string proofId,
        string recordingId,
        string runId,
        CaptureAuthorizationProofKind kind,
        string authorizationSourceId,
        string capturePlanDigest,
        string scopeDigest,
        DateTimeOffset issuedAtUtc,
        DateTimeOffset expiresAtUtc,
        string userSessionBinding,
        int? maxDurationSeconds,
        int? maxFrameCount)
    {
        ProofId = RequireValue(proofId, nameof(proofId));
        RecordingId = RequireValue(recordingId, nameof(recordingId));
        RunId = RequireValue(runId, nameof(runId));
        AuthorizationSourceId = RequireValue(authorizationSourceId, nameof(authorizationSourceId));
        CapturePlanDigest = RequireValue(capturePlanDigest, nameof(capturePlanDigest));
        ScopeDigest = RequireValue(scopeDigest, nameof(scopeDigest));
        UserSessionBinding = RequireValue(userSessionBinding, nameof(userSessionBinding));
        if (expiresAtUtc <= issuedAtUtc)
            throw new ArgumentException("Authorization proof expiry must be after issuance.", nameof(expiresAtUtc));
        if (maxDurationSeconds is < 0 || maxFrameCount is < 0)
            throw new ArgumentOutOfRangeException(nameof(maxDurationSeconds));

        Kind = kind;
        IssuedAtUtc = issuedAtUtc;
        ExpiresAtUtc = expiresAtUtc;
        MaxDurationSeconds = maxDurationSeconds;
        MaxFrameCount = maxFrameCount;
        _state = CaptureAuthorizationProofState.Available;
    }

    public string ProofId { get; }
    public string RecordingId { get; }
    public string RunId { get; }
    public CaptureAuthorizationProofKind Kind { get; }

    /// <summary>
    /// The confirmation ID for interactive proof. A future standing lease
    /// proof will use this same field for its single-use source reference.
    /// </summary>
    public string AuthorizationSourceId { get; }

    public string CapturePlanDigest { get; }
    public string ScopeDigest { get; }
    public DateTimeOffset IssuedAtUtc { get; }
    public DateTimeOffset ExpiresAtUtc { get; }
    public string UserSessionBinding { get; }
    public int? MaxDurationSeconds { get; }
    public int? MaxFrameCount { get; }

    public CaptureAuthorizationProofState State
    {
        get
        {
            lock (_stateLock)
            {
                MarkExpiredIfNeeded(DateTimeOffset.UtcNow);
                return _state;
            }
        }
    }

    public bool IsConsumed => State == CaptureAuthorizationProofState.Consumed;

    /// <summary>
    /// Claims the proof exactly once after the Core authorization gate has
    /// validated its run, plan, scope, confirmation and session bindings.
    /// </summary>
    internal bool TryConsume(DateTimeOffset nowUtc, out string failureReason)
    {
        lock (_stateLock)
        {
            MarkExpiredIfNeeded(nowUtc);
            if (_state == CaptureAuthorizationProofState.Expired)
            {
                failureReason = "proof_expired";
                return false;
            }

            if (_state == CaptureAuthorizationProofState.Consumed)
            {
                failureReason = "proof_already_consumed";
                return false;
            }

            if (_state == CaptureAuthorizationProofState.Revoked)
            {
                failureReason = "proof_revoked";
                return false;
            }

            _state = CaptureAuthorizationProofState.Consumed;
            failureReason = "";
            return true;
        }
    }

    /// <summary>
    /// Reads the one-time state without changing it. The standing execution
    /// gate uses this only after it has a UTC execution timestamp and before
    /// it has validated the rest of the trusted snapshot.
    /// </summary>
    internal bool CheckAvailableAt(DateTimeOffset nowUtc, out string failureReason)
    {
        if (nowUtc.Offset != TimeSpan.Zero)
        {
            failureReason = "execution_time_not_utc";
            return false;
        }

        lock (_stateLock)
        {
            if (_state == CaptureAuthorizationProofState.Consumed)
            {
                failureReason = "proof_already_consumed";
                return false;
            }

            if (_state == CaptureAuthorizationProofState.Expired || nowUtc >= ExpiresAtUtc)
            {
                failureReason = "proof_expired";
                return false;
            }

            if (_state == CaptureAuthorizationProofState.Revoked)
            {
                failureReason = "proof_revoked";
                return false;
            }

            failureReason = "";
            return _state == CaptureAuthorizationProofState.Available;
        }
    }

    /// <summary>
    /// Revokes an unconsumed proof when its recording becomes terminal before
    /// the start gate. A consumed proof remains consumed for auditability.
    /// </summary>
    internal void Revoke()
    {
        lock (_stateLock)
        {
            if (_state == CaptureAuthorizationProofState.Available)
                _state = CaptureAuthorizationProofState.Revoked;
        }
    }

    /// <summary>
    /// Used by proof-bearing capture implementations to enforce that the
    /// Core-side parent gate already consumed this proof. It is deliberately
    /// not a public construction or serialization mechanism.
    /// </summary>
    internal void RequireConsumed()
    {
        if (!IsConsumed)
            throw new InvalidOperationException("A consumed capture authorization proof is required.");
    }

    private void MarkExpiredIfNeeded(DateTimeOffset nowUtc)
    {
        if (_state == CaptureAuthorizationProofState.Available && nowUtc >= ExpiresAtUtc)
            _state = CaptureAuthorizationProofState.Expired;
    }

    private static string RequireValue(string value, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new ArgumentException("Authorization proof fields cannot be empty.", parameterName);
        return value;
    }
}

/// <summary>
/// Ordinary recording authorization issued after a real local confirmation.
/// The constructor is internal so only trusted in-process orchestration can
/// create this proof. Future standing-lease proofs can use the same base
/// contract without making a lease executable in this phase.
/// </summary>
public sealed class InteractiveConfirmationProof : CaptureAuthorizationProof
{
    internal InteractiveConfirmationProof(
        string proofId,
        string recordingId,
        string runId,
        string authorizationSourceId,
        string capturePlanDigest,
        string scopeDigest,
        DateTimeOffset issuedAtUtc,
        DateTimeOffset expiresAtUtc,
        string userSessionBinding,
        int? maxDurationSeconds,
        int? maxFrameCount)
        : base(
            proofId,
            recordingId,
            runId,
            CaptureAuthorizationProofKind.InteractiveConfirmation,
            authorizationSourceId,
            capturePlanDigest,
            scopeDigest,
            issuedAtUtc,
            expiresAtUtc,
            userSessionBinding,
            maxDurationSeconds,
            maxFrameCount)
    {
    }
}

/// <summary>
/// One-time authorization issued only from a trusted Phase 3 first-commit
/// receipt. Its constructor is internal so public requests, results, JSON,
/// environment variables and helper command lines cannot manufacture it.
/// </summary>
public sealed class StandingLeaseUseProof : CaptureAuthorizationProof
{
    internal StandingLeaseUseProof(
        string proofId,
        string runId,
        string authorizationSourceId,
        string capturePlanDigest,
        string scopeDigest,
        DateTimeOffset issuedAtUtc,
        DateTimeOffset expiresAtUtc,
        string currentUserSid,
        string sessionBinding,
        string leaseId,
        string leaseUseId,
        string oneTimeNonce,
        TimeSpan maxDuration)
        : base(
            proofId,
            recordingId: runId,
            runId,
            CaptureAuthorizationProofKind.StandingLeaseUse,
            authorizationSourceId,
            capturePlanDigest,
            scopeDigest,
            issuedAtUtc,
            expiresAtUtc,
            userSessionBinding: currentUserSid + "|" + sessionBinding,
            maxDurationSeconds: null,
            maxFrameCount: null)
    {
        LeaseId = RequireField(leaseId, nameof(leaseId));
        LeaseUseId = RequireField(leaseUseId, nameof(leaseUseId));
        CurrentUserSid = RequireField(currentUserSid, nameof(currentUserSid));
        SessionBinding = RequireField(sessionBinding, nameof(sessionBinding));
        OneTimeNonce = RequireField(oneTimeNonce, nameof(oneTimeNonce));
        if (OneTimeNonce.Length != 32 || OneTimeNonce.Any(character =>
                character < '0' || (character > '9' && character < 'a') || character > 'f'))
            throw new ArgumentException("The standing proof nonce must be 128-bit lowercase hexadecimal.", nameof(oneTimeNonce));
        if (maxDuration <= TimeSpan.Zero || maxDuration.Ticks % TimeSpan.TicksPerMillisecond != 0)
            throw new ArgumentOutOfRangeException(nameof(maxDuration));

        MaxDuration = maxDuration;
        MaxDurationMilliseconds = maxDuration.Ticks / TimeSpan.TicksPerMillisecond;
    }

    public string LeaseId { get; }

    public string LeaseUseId { get; }

    public string CurrentUserSid { get; }

    public string SessionBinding { get; }

    /// <summary>
    /// Lowercase hexadecimal encoding of 128 bits from a cryptographic random
    /// source. It is not derived from a run ID, use ID, timestamp, or digest.
    /// </summary>
    public string OneTimeNonce { get; }

    public TimeSpan MaxDuration { get; }

    public long MaxDurationMilliseconds { get; }

    private static string RequireField(string value, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new ArgumentException("Standing proof fields cannot be empty.", parameterName);

        return value;
    }
}

/// <summary>
/// The minimum process-local user/session identity used to bind a proof.
/// </summary>
internal static class CaptureAuthorizationSessionBinding
{
    internal static string Current
    {
        get
        {
            string user = string.IsNullOrWhiteSpace(Environment.UserName)
                ? "unknown-user"
                : Environment.UserName;
            string domain = string.IsNullOrWhiteSpace(Environment.UserDomainName)
                ? "unknown-domain"
                : Environment.UserDomainName;
            int sessionId;
            try
            {
                sessionId = Process.GetCurrentProcess().SessionId;
            }
            catch
            {
                sessionId = -1;
            }

            return $"{domain}\\{user}|session:{sessionId}";
        }
    }
}
