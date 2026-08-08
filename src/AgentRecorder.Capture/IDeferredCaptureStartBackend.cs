namespace AgentRecorder.Capture;

/// <summary>
/// Optional capability for backends that separate backend preparation from
/// screen-capture authorization. <see cref="ICaptureBackend.Start"/> prepares
/// the backend and its helper into a waiting-for-authorization state without
/// capturing any screen content; <see cref="StartCapture"/> authorizes and
/// starts capture exactly once, typically at the end of an app-owned countdown.
/// </summary>
public interface IDeferredCaptureStartBackend
{
    /// <summary>
    /// True after Start when the backend is prepared but still waiting for an
    /// explicit capture start (deferred mode only).
    /// </summary>
    bool IsAwaitingCaptureStart { get; }

    /// <summary>
    /// Raised once when the deferred authorization attempt completes.
    /// True means the helper was authorized; false means authorization failed
    /// (the terminal failure still flows through the normal completion path).
    /// </summary>
    event Action<bool>? CaptureAuthorizationCompleted;

    /// <summary>
    /// Authorizes and starts screen capture exactly once. Later calls are
    /// no-ops. Must only be invoked after the app-owned countdown has reached
    /// zero; while the countdown digits are visible the backend must remain
    /// unauthorized.
    /// </summary>
    void StartCapture();
}
