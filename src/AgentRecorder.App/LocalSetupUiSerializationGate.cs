using System;
using System.Threading;
using System.Threading.Tasks;

namespace AgentRecorder.App;

/// <summary>
/// Serializes the process' unattended setup UI flows across coordinator types.
/// The semaphore is process-owned and intentionally has no disposal surface;
/// callers can only release through the one-shot lease returned by WaitAsync.
/// </summary>
internal sealed class LocalSetupUiSerializationGate
{
    internal static LocalSetupUiSerializationGate Instance { get; } = new();

    private readonly SemaphoreSlim _semaphore = new(1, 1);

    private LocalSetupUiSerializationGate() { }

    internal async Task<Lease> WaitAsync(CancellationToken cancellationToken)
    {
        await _semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
        return new Lease(_semaphore);
    }

    internal sealed class Lease : IDisposable
    {
        private SemaphoreSlim? _semaphore;

        internal Lease(SemaphoreSlim semaphore) => _semaphore = semaphore;

        public void Dispose() => Interlocked.Exchange(ref _semaphore, null)?.Release();
    }
}
