using System.Threading;
using System.Threading.Tasks;

namespace AgentRecorder.Capture;

/// <summary>
/// Internal seam implemented by <see cref="WgcContinuousManagedSession"/> and
/// consumed by <see cref="WgcContinuousCaptureBackend"/>. Allows tests to
/// inject synchronous completions and exceptions.
/// </summary>
internal interface IWgcContinuousBackendSession : IDisposable
{
    Task StartAsync(CancellationToken cancellationToken = default);
    Task<bool> AuthorizeCapture(CancellationToken cancellationToken = default);
    Task<bool> RequestStop(CancellationToken cancellationToken = default);
    Task<WgcContinuousSessionResult> CompletionTask { get; }
    event Action<FirstFrameObservation>? FirstFrameObserved;
}

// Implementers must seal future resource creation before publishing this per-session
// resource fact. Completion/status/Dispose return alone are not evidence.
internal interface IWgcContinuousResourceOwner
{
    Task ResourceRetirement { get; }
}
