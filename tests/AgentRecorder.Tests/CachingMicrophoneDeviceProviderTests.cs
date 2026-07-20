using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using AgentRecorder.Capture;
using Xunit;

namespace AgentRecorder.Tests;

public class CachingMicrophoneDeviceProviderTests
{
    [Fact]
    public async Task SecondCallWithinTtl_ReturnsCachedListWithoutEnumerating()
    {
        var inner = new CountingProvider(new MicrophoneDeviceInfo("mic_1", "Test Mic", true, "active"));
        var cache = new CachingMicrophoneDeviceProvider(inner, TimeSpan.FromSeconds(10));

        var first = await cache.GetDevicesAsync();
        var second = await cache.GetDevicesAsync();

        Assert.Single(first);
        Assert.Same(first, second);
        Assert.Equal(1, inner.CallCount);
    }

    [Fact]
    public async Task CacheExpires_AfterTtl_EnumeratesAgain()
    {
        var inner = new CountingProvider(new MicrophoneDeviceInfo("mic_1", "Test Mic", true, "active"));
        var cache = new CachingMicrophoneDeviceProvider(inner, TimeSpan.FromMilliseconds(1));

        var first = await cache.GetDevicesAsync();
        await Task.Delay(50);
        var second = await cache.GetDevicesAsync();

        Assert.Equal(2, inner.CallCount);
        Assert.NotSame(first, second);
    }

    [Fact]
    public async Task Refresh_ClearsCacheSoNextCallEnumerates()
    {
        var inner = new CountingProvider(new MicrophoneDeviceInfo("mic_1", "Test Mic", true, "active"));
        var cache = new CachingMicrophoneDeviceProvider(inner, TimeSpan.FromSeconds(10));

        await cache.GetDevicesAsync();
        cache.Refresh();
        await cache.GetDevicesAsync();

        Assert.Equal(2, inner.CallCount);
    }

    [Fact]
    public async Task Failure_IsNotCached_SubsequentCallRetries()
    {
        var inner = new FailingProvider(new MicrophoneEnumerationException("device_enumeration_unavailable", "fail"));
        var cache = new CachingMicrophoneDeviceProvider(inner, TimeSpan.FromSeconds(10));

        await Assert.ThrowsAsync<MicrophoneEnumerationException>(() => cache.GetDevicesAsync());
        inner.NextResult = new[] { new MicrophoneDeviceInfo("mic_1", "Test Mic", true, "active") };

        var devices = await cache.GetDevicesAsync();

        Assert.Single(devices);
        Assert.Equal(2, inner.CallCount);
    }

    [Fact]
    public async Task ConcurrentCalls_ShareSingleInFlightEnumeration()
    {
        var gate = new TaskCompletionSource<IReadOnlyList<MicrophoneDeviceInfo>>();
        var inner = new DelayedProvider(gate.Task);
        var cache = new CachingMicrophoneDeviceProvider(inner, TimeSpan.FromSeconds(10));

        var t1 = cache.GetDevicesAsync();
        var t2 = cache.GetDevicesAsync();
        var t3 = cache.GetDevicesAsync();

        Assert.Equal(1, inner.CallCount);

        var expected = new List<MicrophoneDeviceInfo> { new MicrophoneDeviceInfo("mic_1", "Test Mic", true, "active") };
        gate.SetResult(expected);

        var results = await Task.WhenAll(t1, t2, t3);
        Assert.All(results, r => Assert.Same(expected, r));
        Assert.Equal(1, inner.CallCount);
    }

    [Fact]
    public async Task OneCallerCancels_OtherCallersStillReceiveResult()
    {
        var gate = new TaskCompletionSource<IReadOnlyList<MicrophoneDeviceInfo>>();
        var inner = new DelayedProvider(gate.Task);
        var cache = new CachingMicrophoneDeviceProvider(inner, TimeSpan.FromSeconds(10));

        using var cts = new CancellationTokenSource();
        var waitingTask = cache.GetDevicesAsync(cts.Token);
        var otherTask = cache.GetDevicesAsync();

        Assert.Equal(1, inner.CallCount);

        // Cancel the first caller's await. The shared enumeration must continue.
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waitingTask);

        var expected = new List<MicrophoneDeviceInfo> { new MicrophoneDeviceInfo("mic_1", "Test Mic", true, "active") };
        gate.SetResult(expected);

        var result = await otherTask;
        Assert.Same(expected, result);
        Assert.Equal(1, inner.CallCount);
    }

    private sealed class CountingProvider : IMicrophoneDeviceProvider
    {
        private readonly IReadOnlyList<MicrophoneDeviceInfo> _devices;
        public int CallCount { get; private set; }

        public CountingProvider(params MicrophoneDeviceInfo[] devices)
        {
            _devices = devices;
        }

        public Task<IReadOnlyList<MicrophoneDeviceInfo>> GetDevicesAsync(CancellationToken cancellationToken = default)
        {
            CallCount++;
            return Task.FromResult<IReadOnlyList<MicrophoneDeviceInfo>>(new List<MicrophoneDeviceInfo>(_devices));
        }
    }

    private sealed class FailingProvider : IMicrophoneDeviceProvider
    {
        private Exception _exception;
        private IReadOnlyList<MicrophoneDeviceInfo>? _next;
        public int CallCount { get; private set; }

        public FailingProvider(Exception exception)
        {
            _exception = exception;
        }

        public IReadOnlyList<MicrophoneDeviceInfo>? NextResult
        {
            set
            {
                _next = value;
                _exception = null!;
            }
        }

        public Task<IReadOnlyList<MicrophoneDeviceInfo>> GetDevicesAsync(CancellationToken cancellationToken = default)
        {
            CallCount++;
            if (_exception != null)
                return Task.FromException<IReadOnlyList<MicrophoneDeviceInfo>>(_exception);
            return Task.FromResult(_next ?? new List<MicrophoneDeviceInfo>());
        }
    }

    private sealed class DelayedProvider : IMicrophoneDeviceProvider
    {
        private readonly Task<IReadOnlyList<MicrophoneDeviceInfo>> _result;
        public int CallCount { get; private set; }

        public DelayedProvider(Task<IReadOnlyList<MicrophoneDeviceInfo>> result)
        {
            _result = result;
        }

        public Task<IReadOnlyList<MicrophoneDeviceInfo>> GetDevicesAsync(CancellationToken cancellationToken = default)
        {
            CallCount++;
            return _result;
        }
    }
}
