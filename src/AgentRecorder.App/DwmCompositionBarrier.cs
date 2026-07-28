using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace AgentRecorder.App;

/// <summary>
/// Windows DWM composition barrier used after a confirmation form is closed.
/// Tries to wait until the desktop composition has flushed the last frame
/// that could include the form, falling back to a bounded message pump when
/// DWM is unavailable or too slow.
/// </summary>
internal static class DwmCompositionBarrier
{
    private static readonly TimeSpan FallbackDeadline = TimeSpan.FromMilliseconds(150);

    /// <summary>
    /// Test seam: replaces the real <c>DwmFlush</c> P/Invoke. Production code
    /// leaves this <c>null</c> so the native call is used.
    /// </summary>
    internal static Func<CancellationToken, Task>? TestFlushOverride;

    /// <summary>
    /// Wait for DWM composition to settle after a window has been closed.
    /// Returns whether DwmFlush completed, the elapsed time, and whether the
    /// bounded fallback path was used. Never throws.
    /// </summary>
    public static CompositionFlushResult Wait(TimeSpan timeout)
    {
        var flush = TestFlushOverride;
        if (flush != null)
        {
            return WaitCore(timeout, flush);
        }

        return WaitCore(timeout, RealFlushAsync);
    }

    private static CompositionFlushResult WaitCore(TimeSpan timeout, Func<CancellationToken, Task> flush)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            using var cts = new CancellationTokenSource(timeout);
            var task = flush(cts.Token);
            if (task.Wait(timeout))
            {
                sw.Stop();
                return new CompositionFlushResult(true, sw.Elapsed, false);
            }
        }
        catch (DllNotFoundException)
        {
            // DWM not available (e.g., old Windows, safe mode).
        }
        catch (Win32Exception)
        {
            // DWM composition disabled or call failed.
        }
        catch (TimeoutException)
        {
            // Task.Wait already timed out; fall through.
        }
        catch
        {
            // Any other failure (entry point missing, etc.) is non-fatal.
        }

        sw.Stop();
        var fallbackElapsed = RunBoundedFallback();
        return new CompositionFlushResult(false, sw.Elapsed + fallbackElapsed, true);
    }

    private static Task RealFlushAsync(CancellationToken cancellationToken)
    {
        return Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            DwmFlush();
        }, cancellationToken);
    }

    /// <summary>
    /// Bounded fallback that pumps a few UI messages so any pending close/hide
    /// paint is processed, without relying on a long fixed sleep.
    /// </summary>
    private static TimeSpan RunBoundedFallback()
    {
        var sw = Stopwatch.StartNew();
        var deadline = DateTime.UtcNow + FallbackDeadline;
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                Application.DoEvents();
            }
            catch
            {
                // Message pump failure must not break the barrier.
            }
            Thread.Sleep(5);
        }
        sw.Stop();
        return sw.Elapsed;
    }

    [DllImport("dwmapi.dll", PreserveSig = false)]
    private static extern void DwmFlush();
}

internal readonly record struct CompositionFlushResult(
    bool Flushed,
    TimeSpan Elapsed,
    bool UsedFallback);
