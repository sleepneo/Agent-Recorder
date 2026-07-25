namespace AgentRecorder.Capture;

/// <summary>
/// Runtime and terminal states of a <see cref="WgcContinuousManagedSession"/>.
/// </summary>
public enum WgcContinuousManagedSessionState
{
    /// <summary>Session has not been started.</summary>
    NotStarted,

    /// <summary>Helper is running and waiting for begin authorization.</summary>
    WaitingForAuthorization,

    /// <summary>Authorization write is in progress.</summary>
    Authorizing,

    /// <summary>Begin authorization has been written; helper may now pass the gate.</summary>
    Authorized,

    /// <summary>STARTED event has been received from the helper.</summary>
    Started,

    /// <summary>A stop has been requested and the session is finalizing.</summary>
    Stopping,

    /// <summary>Helper completed naturally with RESULT: OK.</summary>
    Success,

    /// <summary>Helper stopped gracefully with RESULT: STOPPED.</summary>
    Stopped,

    /// <summary>Helper reported FAIL or the session ended without a valid terminal event.</summary>
    Failed,

    /// <summary>The caller cancelled or disposed the session before a natural terminal event.</summary>
    Cancelled
}
