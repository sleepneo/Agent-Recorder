namespace AgentRecorder.Infrastructure;

/// <summary>
/// Performance tracing abstraction. Implementations must be thread-safe and
/// must isolate failures so that tracing problems never change recording or
/// confirmation state.
/// </summary>
public interface IPerformanceTracer
{
    /// <summary>Record that a recording intent HTTP request was accepted.</summary>
    void IntentAccepted(string traceId, string endpoint, string? clientSentAtUtc = null);

    /// <summary>
    /// Associate a one-time ensure-running context with this trace. Must be
    /// called after the server has authenticated the request and successfully
    /// consumed the context. The association is privacy-safe and contains no
    /// raw context ID, file path, or header text.
    /// </summary>
    void SetEnsureContextAssociation(string traceId, EnsureContextAssociation association);

    /// <summary>Record that intent validation succeeded or failed.</summary>
    void IntentValidated(string traceId, string endpoint, bool success, string? errorCode = null);

    /// <summary>Associate a recording (and optional confirmation) with a trace.</summary>
    void CorrelationSet(string traceId, string recordingId, string? confirmationId = null, string? sourceType = null);

    /// <summary>
    /// Returns true if a validation result (intent.validated or intent.failed)
    /// has already been recorded for this trace. Used by catch layers to avoid
    /// duplicate validation events.
    /// </summary>
    bool HasValidationResult(string traceId);

    /// <summary>Record confirmation creation.</summary>
    void ConfirmationCreated(string traceId, string recordingId, string confirmationId);

    /// <summary>Record that the confirmation form really entered OnShown.</summary>
    void ConfirmationShown(string traceId, string recordingId, string confirmationId);

    /// <summary>Record user approval.</summary>
    void ConfirmationApproved(string traceId, string recordingId, string confirmationId);

    /// <summary>Record user rejection.</summary>
    void ConfirmationRejected(string traceId, string recordingId, string confirmationId);

    /// <summary>Record confirmation timeout expiry.</summary>
    void ConfirmationExpired(string traceId, string recordingId, string confirmationId);

    /// <summary>Record that capture backend Start() is about to be called.</summary>
    void CaptureStartRequested(string traceId, string recordingId, string backendType);

    /// <summary>Record that capture backend Start() returned normally.</summary>
    void CaptureBackendStartReturned(string traceId, string recordingId, string backendType);

    /// <summary>Record that capture backend Start() threw.</summary>
    void CaptureBackendStartFailed(string traceId, string recordingId, string backendType, string errorCode, string errorType);

    /// <summary>
    /// Record that the backend observed evidence of the first processed video
    /// frame with positive output bytes. Must be exactly-once per trace and must
    /// be ignored if the recording has already reached a terminal state.
    /// </summary>
    void CaptureFirstFrameObserved(string traceId, string recordingId, FirstFrameEvidence evidence);

    /// <summary>Record that a recording reached a terminal state.</summary>
    void RecordingTerminal(string traceId, string recordingId, string status, string? stopReason = null, string? errorCode = null);

    /// <summary>Record completion of a long-polling wait.</summary>
    void LongPollCompleted(string traceId, string kind, int requestedWaitMs, int actualWaitMs, bool changed, string? recordingId = null, string? confirmationId = null);

    /// <summary>Best-effort flush with bounded wait. Safe to call multiple times.</summary>
    void Flush();

    /// <summary>Resolve trace id from recording or confirmation id, if known.</summary>
    string? ResolveTraceId(string? recordingId = null, string? confirmationId = null);
}
