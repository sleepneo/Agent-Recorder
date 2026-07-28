using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using AgentRecorder.App;
using Xunit;

namespace AgentRecorder.Tests;

[Collection("NonParallel-DwmCompositionBarrier")]
public class DwmCompositionBarrierTests : IDisposable
{
    public DwmCompositionBarrierTests()
    {
        // Ensure no stale test seam from a previous run.
        DwmCompositionBarrier.TestFlushOverride = null;
    }

    public void Dispose()
    {
        DwmCompositionBarrier.TestFlushOverride = null;
    }

    [Fact]
    public void Wait_FlushCompletesQuickly_ReturnsFlushedWithoutFallback()
    {
        DwmCompositionBarrier.TestFlushOverride = _ => Task.CompletedTask;

        var result = DwmCompositionBarrier.Wait(TimeSpan.FromMilliseconds(200));

        Assert.True(result.Flushed);
        Assert.False(result.UsedFallback);
        Assert.True(result.Elapsed < TimeSpan.FromMilliseconds(50));
    }

    [Fact]
    public void Wait_FlushTimesOut_UsesFallbackAndNotFlushed()
    {
        DwmCompositionBarrier.TestFlushOverride = async ct =>
        {
            await Task.Delay(TimeSpan.FromSeconds(10), ct);
        };

        var result = DwmCompositionBarrier.Wait(TimeSpan.FromMilliseconds(20));

        Assert.False(result.Flushed);
        Assert.True(result.UsedFallback);
    }

    [Fact]
    public void Wait_FlushThrowsDllNotFound_UsesFallbackAndNotFlushed()
    {
        DwmCompositionBarrier.TestFlushOverride = _ =>
            Task.FromException(new DllNotFoundException("dwmapi.dll"));

        var result = DwmCompositionBarrier.Wait(TimeSpan.FromMilliseconds(50));

        Assert.False(result.Flushed);
        Assert.True(result.UsedFallback);
    }

    [Fact]
    public void Wait_FlushThrowsWin32Exception_UsesFallbackAndNotFlushed()
    {
        DwmCompositionBarrier.TestFlushOverride = _ =>
            Task.FromException(new Win32Exception(unchecked((int)0x80070715)));

        var result = DwmCompositionBarrier.Wait(TimeSpan.FromMilliseconds(50));

        Assert.False(result.Flushed);
        Assert.True(result.UsedFallback);
    }

    [Fact]
    public void Wait_FlushThrowsGenericException_UsesFallbackAndNotFlushed()
    {
        DwmCompositionBarrier.TestFlushOverride = _ =>
            Task.FromException(new InvalidOperationException("boom"));

        var result = DwmCompositionBarrier.Wait(TimeSpan.FromMilliseconds(50));

        Assert.False(result.Flushed);
        Assert.True(result.UsedFallback);
    }

    [Fact]
    public void Wait_FlushCanceledQuickly_DoesNotThrowAndUsesFallback()
    {
        DwmCompositionBarrier.TestFlushOverride = ct =>
            Task.FromCanceled(ct);

        var result = DwmCompositionBarrier.Wait(TimeSpan.FromMilliseconds(50));

        Assert.False(result.Flushed);
        Assert.True(result.UsedFallback);
    }

    [Fact]
    public void Wait_WithTestSeam_NeverCallsRealDwmFlush()
    {
        var called = false;
        DwmCompositionBarrier.TestFlushOverride = _ =>
        {
            called = true;
            return Task.CompletedTask;
        };

        var result = DwmCompositionBarrier.Wait(TimeSpan.FromMilliseconds(50));

        Assert.True(called);
        Assert.True(result.Flushed);
    }
}
