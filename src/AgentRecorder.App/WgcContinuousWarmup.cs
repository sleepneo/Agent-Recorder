using System.Diagnostics;
using AgentRecorder.Capture;
using AgentRecorder.Core;

namespace AgentRecorder.App;

internal static class WgcContinuousWarmup
{
    public static Task StartIfEnabled(
        IWgcContinuousAvailabilityProbe probe,
        Action<string>? diagnostic,
        CancellationToken cancellationToken = default)
    {
        if (!IsEnabled())
            return Task.CompletedTask;
        if (probe is not IWgcContinuousAvailabilityWarmupProbe warmup)
            return Task.CompletedTask;

        // Keep all helper work off the UI/readiness path. The shared probe owns
        // cache and single-flight state used later by selector requests.
        return Task.Run(async () =>
        {
            WgcContinuousAvailabilityResult result;
            try
            {
                result = await warmup.WarmupAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch
            {
                TryDiagnostic(diagnostic, "wgc_probe_warmup_exception");
                return;
            }

            if (!result.Available)
                TryDiagnostic(diagnostic, "wgc_probe_warmup_unavailable:" + result.ReasonCode);
        }, CancellationToken.None);
    }

    private static bool IsEnabled()
    {
        var displayFlag = Environment.GetEnvironmentVariable(CaptureBackendSelector.DisplayBackendEnvVar)?.Trim() ?? "";
        var windowFlag = Environment.GetEnvironmentVariable(CaptureBackendSelector.WgcEnvVar)?.Trim() ?? "";
        return string.Equals(displayFlag, "wgc-continuous", StringComparison.OrdinalIgnoreCase)
            || string.Equals(windowFlag, "wgc-continuous", StringComparison.OrdinalIgnoreCase);
    }

    private static void TryDiagnostic(Action<string>? diagnostic, string message)
    {
        try
        {
            if (diagnostic != null)
                diagnostic(message);
            else
                Debug.WriteLine(message);
        }
        catch
        {
            // Warmup diagnostics are best effort and never affect startup.
        }
    }
}
