using System;
using AgentRecorder.Capture;
using AgentRecorder.Infrastructure;

namespace AgentRecorder.Core;

/// <summary>
/// Selects the appropriate ICaptureBackend implementation based on
/// source type, microphone configuration, and feature flag environment variables.
/// </summary>
public static class CaptureBackendSelector
{
    public const string WgcEnvVar = "AGENT_RECORDER_WINDOW_BACKEND";

    /// <summary>
    /// Selects a backend and returns both the backend instance and its type string.
    /// When a microphone is requested for an FFmpeg source, the audio/video split
    /// backend is used so dshow initialization cannot block gdigrab.
    /// </summary>
    public static (ICaptureBackend Backend, string BackendType) Select(CaptureConfig cfg)
    {
        if (string.Equals(cfg.SourceKind, "display", StringComparison.Ordinal))
        {
            if (cfg.Microphone)
                return (new AvSplitCaptureBackend(), "ffmpeg-av-split");
            return (new FfmpegCaptureBackend(), "ffmpeg");
        }

        if (string.Equals(cfg.SourceKind, "window", StringComparison.Ordinal))
        {
            var flag = Environment.GetEnvironmentVariable(WgcEnvVar)?.Trim() ?? "";
            if (string.Equals(flag, "wgc", StringComparison.OrdinalIgnoreCase))
            {
                return (new WgcWindowCaptureBackend(), "wgc");
            }
            if (cfg.Microphone)
                return (new AvSplitCaptureBackend(), "ffmpeg-window-region-av-split");
            return (new FfmpegCaptureBackend(), "ffmpeg-window-region");
        }

        if (string.Equals(cfg.SourceKind, "region", StringComparison.Ordinal))
        {
            if (cfg.Microphone)
                return (new AvSplitCaptureBackend(), "ffmpeg-region-av-split");
            // Region uses FFmpeg gdigrab with desktop source and offset parameters
            return (new FfmpegCaptureBackend(), "ffmpeg-region");
        }

        throw new ApiException(400, "INVALID_ARGUMENT",
            $"Unsupported source type: '{cfg.SourceKind}'. Expected 'display', 'window', or 'region'.");
    }

    /// <summary>
    /// Returns just the backend type string that would be selected,
    /// useful for logging and testing without creating backend instances.
    /// </summary>
    public static string SelectBackendType(CaptureConfig cfg)
    {
        if (string.Equals(cfg.SourceKind, "display", StringComparison.Ordinal))
        {
            if (cfg.Microphone)
                return "ffmpeg-av-split";
            return "ffmpeg";
        }

        if (string.Equals(cfg.SourceKind, "window", StringComparison.Ordinal))
        {
            var flag = Environment.GetEnvironmentVariable(WgcEnvVar)?.Trim() ?? "";
            if (string.Equals(flag, "wgc", StringComparison.OrdinalIgnoreCase))
                return "wgc";
            if (cfg.Microphone)
                return "ffmpeg-window-region-av-split";
            return "ffmpeg-window-region";
        }

        if (string.Equals(cfg.SourceKind, "region", StringComparison.Ordinal))
        {
            if (cfg.Microphone)
                return "ffmpeg-region-av-split";
            return "ffmpeg-region";
        }

        return "ffmpeg";
    }

    /// <summary>
    /// Returns true if the given backend type is one of the known FFmpeg MP4
    /// capture backends that should produce a recording bundle.
    /// </summary>
    public static bool IsFfmpegMp4Backend(string backendType)
    {
        return string.Equals(backendType, "ffmpeg", StringComparison.OrdinalIgnoreCase)
            || string.Equals(backendType, "ffmpeg-region", StringComparison.OrdinalIgnoreCase)
            || string.Equals(backendType, "ffmpeg-window-region", StringComparison.OrdinalIgnoreCase)
            || string.Equals(backendType, "ffmpeg-av-split", StringComparison.OrdinalIgnoreCase)
            || string.Equals(backendType, "ffmpeg-region-av-split", StringComparison.OrdinalIgnoreCase)
            || string.Equals(backendType, "ffmpeg-window-region-av-split", StringComparison.OrdinalIgnoreCase);
    }
}
