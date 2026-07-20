using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace AgentRecorder.Capture;

/// <summary>
/// Decorator that adds a short TTL cache on top of another microphone provider.
/// Concurrent callers share a single in-flight enumeration; failures are not cached.
/// </summary>
public sealed class CachingMicrophoneDeviceProvider : IMicrophoneDeviceProvider
{
    private readonly IMicrophoneDeviceProvider _inner;
    private readonly TimeSpan _ttl;
    private readonly object _lock = new();
    private IReadOnlyList<MicrophoneDeviceInfo>? _cache;
    private DateTime _cachedAt = DateTime.MinValue;
    private Task<IReadOnlyList<MicrophoneDeviceInfo>>? _inFlight;

    public CachingMicrophoneDeviceProvider(IMicrophoneDeviceProvider inner, TimeSpan? ttl = null)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _ttl = ttl ?? TimeSpan.FromSeconds(5);
    }

    public Task<IReadOnlyList<MicrophoneDeviceInfo>> GetDevicesAsync(CancellationToken cancellationToken = default)
    {
        lock (_lock)
        {
            if (_cache != null && DateTime.UtcNow - _cachedAt < _ttl)
                return Task.FromResult(_cache);

            if (_inFlight != null)
            {
                // The shared enumeration task is not owned by any caller.
                // WaitAsync lets this caller cancel their own await without
                // affecting the shared work or other concurrent callers.
                return _inFlight.WaitAsync(cancellationToken);
            }

            var tcs = new TaskCompletionSource<IReadOnlyList<MicrophoneDeviceInfo>>(TaskCreationOptions.RunContinuationsAsynchronously);
            var inFlight = tcs.Task;
            _inFlight = inFlight;
            // The shared enumeration uses the inner provider's own bounded
            // lifetime, not the first caller's cancellation token.
            _ = EnumerateCoreAsync(tcs);
            return inFlight.WaitAsync(cancellationToken);
        }
    }

    private async Task EnumerateCoreAsync(TaskCompletionSource<IReadOnlyList<MicrophoneDeviceInfo>> tcs)
    {
        try
        {
            var devices = await _inner.GetDevicesAsync(CancellationToken.None);
            lock (_lock)
            {
                _cache = devices;
                _cachedAt = DateTime.UtcNow;
            }
            tcs.SetResult(devices);
        }
        catch (OperationCanceledException)
        {
            tcs.SetCanceled();
        }
        catch (Exception ex)
        {
            tcs.SetException(ex);
        }
        finally
        {
            lock (_lock)
            {
                _inFlight = null;
            }
        }
    }

    /// <summary>
    /// Clears the cache so the next call re-enumerates devices.
    /// </summary>
    public void Refresh()
    {
        lock (_lock)
        {
            _cache = null;
            _cachedAt = DateTime.MinValue;
        }
    }
}
