using System;
using AgentRecorder.Capture;
using AgentRecorder.Infrastructure;

namespace AgentRecorder.Core;

/// <summary>
/// Selects the appropriate ICaptureBackend implementation based on
/// source type and feature flag environment variables.
/// </summary>
public static class CaptureBackendSelector
{
    public const string WgcEnvVar = "AGENT_RECORDER_WINDOW_BACKEND";

    /// <summary>
    /// Selects a backend and returns both the backend instance and its type string.
    /// </summary>
    public static (ICaptureBackend Backend, string BackendType) Select(string sourceType)
    {
        if (string.Equals(sourceType, "display", StringComparison.Ordinal))
        {
            return (new FfmpegCaptureBackend(), "ffmpeg");
        }

        if (string.Equals(sourceType, "window", StringComparison.Ordinal))
        {
            var flag = Environment.GetEnvironmentVariable(WgcEnvVar)?.Trim() ?? "";
            if (string.Equals(flag, "wgc", StringComparison.OrdinalIgnoreCase))
            {
                return (new WgcWindowCaptureBackend(), "wgc");
            }
            return (new FfmpegCaptureBackend(), "ffmpeg-window-region");
        }

        if (string.Equals(sourceType, "region", StringComparison.Ordinal))
        {
            // Region uses FFmpeg gdigrab with desktop source and offset parameters
            return (new FfmpegCaptureBackend(), "ffmpeg-region");
        }

        throw new ApiException(400, "INVALID_ARGUMENT",
            $"Unsupported source type: '{sourceType}'. Expected 'display', 'window', or 'region'.");
    }

    /// <summary>
    /// Returns just the backend type string that would be selected,
    /// useful for logging and testing without creating backend instances.
    /// </summary>
    public static string SelectBackendType(string sourceType)
    {
        if (string.Equals(sourceType, "display", StringComparison.Ordinal))
            return "ffmpeg";

        if (string.Equals(sourceType, "window", StringComparison.Ordinal))
        {
            var flag = Environment.GetEnvironmentVariable(WgcEnvVar)?.Trim() ?? "";
            if (string.Equals(flag, "wgc", StringComparison.OrdinalIgnoreCase))
                return "wgc";
            return "ffmpeg-window-region";
        }

        if (string.Equals(sourceType, "region", StringComparison.Ordinal))
            return "ffmpeg-region";

        return "ffmpeg";
    }
}
