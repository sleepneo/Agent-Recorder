using System;

namespace AgentRecorder.Capture;

/// <summary>
/// Optional capability for backends that can report first-frame progress
/// evidence without blocking the recording flow. Implementations must be
/// thread-safe and must not throw from the event callback.
/// </summary>
public interface IFirstFrameObservableCaptureBackend
{
    /// <summary>
    /// Raised exactly once when the backend observes evidence that at least
    /// one frame has been processed and the output stream has positive bytes.
    /// The event may be raised synchronously during <see cref="ICaptureBackend.Start"/>
    /// or asynchronously from a background reader.
    /// </summary>
    event Action<FirstFrameObservation>? FirstFrameObserved;
}
