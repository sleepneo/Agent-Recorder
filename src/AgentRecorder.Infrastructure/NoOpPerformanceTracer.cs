namespace AgentRecorder.Infrastructure;

/// <summary>
/// No-op performance tracer. Keeps tests and hosts that do not need perf
/// diagnostics free of file I/O.
/// </summary>
public sealed class NoOpPerformanceTracer : IPerformanceTracer
{
    public static IPerformanceTracer Instance { get; } = new NoOpPerformanceTracer();

    private NoOpPerformanceTracer() { }

    public void IntentAccepted(string traceId, string endpoint, string? clientSentAtUtc = null) { }
    public void SetEnsureContextAssociation(string traceId, EnsureContextAssociation association) { }
    public void IntentValidated(string traceId, string endpoint, bool success, string? errorCode = null) { }
    public void CorrelationSet(string traceId, string recordingId, string? confirmationId = null, string? sourceType = null) { }
    public bool HasValidationResult(string traceId) => false;
    public void ConfirmationCreated(string traceId, string recordingId, string confirmationId) { }
    public void ConfirmationShown(string traceId, string recordingId, string confirmationId) { }
    public void ConfirmationApproved(string traceId, string recordingId, string confirmationId) { }
    public void ConfirmationRejected(string traceId, string recordingId, string confirmationId) { }
    public void ConfirmationExpired(string traceId, string recordingId, string confirmationId) { }
    public void CaptureStartRequested(string traceId, string recordingId, string backendType) { }
    public void CaptureBackendStartReturned(string traceId, string recordingId, string backendType) { }
    public void CaptureBackendStartFailed(string traceId, string recordingId, string backendType, string errorCode, string errorType) { }
    public void CaptureFirstFrameObserved(string traceId, string recordingId, FirstFrameEvidence evidence) { }
    public void RecordingTerminal(string traceId, string recordingId, string status, string? stopReason = null, string? errorCode = null) { }
    public void LongPollCompleted(string traceId, string kind, int requestedWaitMs, int actualWaitMs, bool changed, string? recordingId = null, string? confirmationId = null) { }
    public void Flush() { }
    public string? ResolveTraceId(string? recordingId = null, string? confirmationId = null) => null;
}
