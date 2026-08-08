using System;
using AgentRecorder.Capture;
using AgentRecorder.Infrastructure;
using ApiException = AgentRecorder.Infrastructure.ApiException;

namespace AgentRecorder.Core;

/// <summary>
/// Selects capture backends and produces privacy-safe selection evidence.
/// </summary>
public static class CaptureBackendSelector
{
    public const string WgcEnvVar = "AGENT_RECORDER_WINDOW_BACKEND";
    public const string DisplayBackendEnvVar = "AGENT_RECORDER_DISPLAY_BACKEND";

    private static readonly IWgcContinuousAvailabilityProbe DefaultDisplayProbe =
        new WgcContinuousAvailabilityProbe();

    /// <summary>
    /// The stable production probe shared by selector calls and App warmup.
    /// It is exposed read-only so composition roots cannot replace global state.
    /// </summary>
    internal static IWgcContinuousAvailabilityProbe ProductionDisplayProbe => DefaultDisplayProbe;

    public static (ICaptureBackend Backend, string BackendType) Select(CaptureConfig cfg) =>
        SelectWithEvidence(cfg).AsTuple();

    public static (ICaptureBackend Backend, string BackendType) Select(
        CaptureConfig cfg,
        IWgcContinuousAvailabilityProbe displayProbe) =>
        SelectWithEvidence(cfg, displayProbe).AsTuple();

    public static CaptureBackendSelection SelectWithEvidence(CaptureConfig cfg) =>
        SelectWithEvidence(cfg, DefaultDisplayProbe);

    public static CaptureBackendSelection SelectWithEvidence(
        CaptureConfig cfg,
        IWgcContinuousAvailabilityProbe displayProbe)
    {
        if (cfg == null) throw new ArgumentNullException(nameof(cfg));
        if (displayProbe == null) throw new ArgumentNullException(nameof(displayProbe));
        if (!IsKnownSourceKind(cfg.SourceKind))
        {
            throw new ApiException(400, "INVALID_ARGUMENT",
                $"Unsupported source type: '{cfg.SourceKind}'. Expected 'display', 'window', or 'region'.");
        }

        var decision = Determine(cfg, displayProbe);
        return new CaptureBackendSelection(
            CreateBackend(decision.BackendType),
            decision.BackendType,
            decision.Evidence);
    }

    public static string SelectBackendType(CaptureConfig cfg) =>
        SelectWithEvidence(cfg).BackendType;

    public static string SelectBackendType(
        CaptureConfig cfg,
        IWgcContinuousAvailabilityProbe displayProbe) =>
        SelectWithEvidence(cfg, displayProbe).BackendType;

    public static bool IsFfmpegMp4Backend(string backendType)
    {
        return string.Equals(backendType, "ffmpeg", StringComparison.OrdinalIgnoreCase)
            || string.Equals(backendType, "ffmpeg-region", StringComparison.OrdinalIgnoreCase)
            || string.Equals(backendType, "ffmpeg-window-region", StringComparison.OrdinalIgnoreCase)
            || string.Equals(backendType, "ffmpeg-av-split", StringComparison.OrdinalIgnoreCase)
            || string.Equals(backendType, "ffmpeg-region-av-split", StringComparison.OrdinalIgnoreCase)
            || string.Equals(backendType, "ffmpeg-window-region-av-split", StringComparison.OrdinalIgnoreCase);
    }

    private static BackendDecision Determine(
        CaptureConfig cfg,
        IWgcContinuousAvailabilityProbe displayProbe)
    {
        if (string.Equals(cfg.SourceKind, "display", StringComparison.Ordinal))
            return DetermineDisplay(cfg, displayProbe);

        if (string.Equals(cfg.SourceKind, "window", StringComparison.Ordinal))
            return DetermineWindow(cfg, displayProbe);

        string regionBackend = cfg.Microphone ? "ffmpeg-region-av-split" : "ffmpeg-region";
        return DefaultDecision(regionBackend, "default_backend");
    }

    private static BackendDecision DetermineWindow(
        CaptureConfig cfg,
        IWgcContinuousAvailabilityProbe probe)
    {
        var flag = Environment.GetEnvironmentVariable(WgcEnvVar)?.Trim() ?? "";
        if (string.Equals(flag, "wgc", StringComparison.OrdinalIgnoreCase))
        {
            // Preserve the legacy prototype exactly. Its microphone behavior
            // is intentionally outside the continuous-window experiment.
            return DefaultDecision("wgc", "window_backend_selected");
        }

        if (!string.Equals(flag, "wgc-continuous", StringComparison.OrdinalIgnoreCase))
        {
            string backendType = cfg.Microphone ? "ffmpeg-window-region-av-split" : "ffmpeg-window-region";
            return DefaultDecision(backendType, "window_backend_selected");
        }

        const string requestedBackend = "wgc-continuous";
        string fallbackBackend = cfg.Microphone ? "ffmpeg-window-region-av-split" : "ffmpeg-window-region";
        if (cfg.Microphone)
            return FallbackDecision(fallbackBackend, requestedBackend, "microphone_not_eligible");
        if (cfg.WindowHandle == nint.Zero)
            return FallbackDecision(fallbackBackend, requestedBackend, "window_handle_not_eligible");
        if (!cfg.DurationSeconds.HasValue || cfg.DurationSeconds.Value is < 1 or > 10)
            return FallbackDecision(fallbackBackend, requestedBackend, "duration_not_eligible");
        if (cfg.Fps is < 1 or > 60)
            return FallbackDecision(fallbackBackend, requestedBackend, "fps_not_eligible");
        if (cfg.Bounds.w <= 0 || cfg.Bounds.h <= 0)
            return FallbackDecision(fallbackBackend, requestedBackend, "bounds_not_eligible");

        WgcContinuousAvailabilityResult availability;
        try
        {
            availability = probe.Check(cfg);
        }
        catch
        {
            availability = new WgcContinuousAvailabilityResult(false, "probe_exception", "fresh_probe", 0);
        }

        string source = NormalizeAvailabilitySource(availability.AvailabilitySource);
        string reason = availability.Available
            ? source switch
            {
                "cache_hit" => "wgc_cache_hit",
                "single_flight" => "wgc_single_flight",
                _ => "wgc_probe_success"
            }
            : NormalizeProbeReason(availability.ReasonCode);

        if (availability.Available)
        {
            return new BackendDecision(
                "wgc-continuous",
                new CaptureBackendSelectionEvidence(
                    requestedBackend,
                    "wgc-continuous",
                    reason,
                    source,
                    availability.ElapsedMs,
                    false));
        }

        return new BackendDecision(
            fallbackBackend,
            new CaptureBackendSelectionEvidence(
                requestedBackend,
                fallbackBackend,
                reason,
                source,
                availability.ElapsedMs,
                true));
    }

    private static BackendDecision DetermineDisplay(
        CaptureConfig cfg,
        IWgcContinuousAvailabilityProbe displayProbe)
    {
        bool experimentEnabled = IsDisplayExperimentEnabled();
        string requestedBackend = experimentEnabled ? "wgc-continuous" : "default";

        if (!experimentEnabled)
        {
            string backendType = cfg.Microphone ? "ffmpeg-av-split" : "ffmpeg";
            return new BackendDecision(
                backendType,
                new CaptureBackendSelectionEvidence(
                    requestedBackend,
                    backendType,
                    "experiment_disabled",
                    "not_run",
                    null,
                    false));
        }

        if (cfg.Microphone)
            return FallbackDecision("ffmpeg-av-split", requestedBackend, "microphone_not_eligible");
        if (!cfg.DurationSeconds.HasValue || cfg.DurationSeconds.Value is < 1 or > 10)
            return FallbackDecision("ffmpeg", requestedBackend, "duration_not_eligible");
        if (cfg.Fps is < 1 or > 60)
            return FallbackDecision("ffmpeg", requestedBackend, "fps_not_eligible");
        if (cfg.Bounds.w <= 0 || cfg.Bounds.h <= 0)
            return FallbackDecision("ffmpeg", requestedBackend, "bounds_not_eligible");

        WgcContinuousAvailabilityResult availability;
        try
        {
            availability = displayProbe.Check(cfg);
        }
        catch
        {
            availability = new WgcContinuousAvailabilityResult(
                false,
                "probe_exception",
                "fresh_probe",
                0);
        }

        string source = NormalizeAvailabilitySource(availability.AvailabilitySource);
        string reason = availability.Available
            ? source switch
            {
                "cache_hit" => "wgc_cache_hit",
                "single_flight" => "wgc_single_flight",
                _ => "wgc_probe_success"
            }
            : NormalizeProbeReason(availability.ReasonCode);

        if (availability.Available)
        {
            return new BackendDecision(
                "wgc-continuous",
                new CaptureBackendSelectionEvidence(
                    requestedBackend,
                    "wgc-continuous",
                    reason,
                    source,
                    availability.ElapsedMs,
                    false));
        }

        return new BackendDecision(
            "ffmpeg",
            new CaptureBackendSelectionEvidence(
                requestedBackend,
                "ffmpeg",
                reason,
                source,
                availability.ElapsedMs,
                true));
    }

    private static BackendDecision FallbackDecision(
        string backendType,
        string requestedBackend,
        string reasonCode) =>
        new(
            backendType,
            new CaptureBackendSelectionEvidence(
                requestedBackend,
                backendType,
                reasonCode,
                "not_run",
                null,
                true));

    private static BackendDecision DefaultDecision(string backendType, string reasonCode) =>
        new(
            backendType,
            new CaptureBackendSelectionEvidence(
                "default",
                backendType,
                reasonCode,
                "not_run",
                null,
                false));

    private static ICaptureBackend CreateBackend(string backendType) =>
        backendType switch
        {
            "wgc-continuous" => new WgcContinuousCaptureBackend(),
            "wgc" => new WgcWindowCaptureBackend(),
            "ffmpeg-av-split" or "ffmpeg-window-region-av-split" or "ffmpeg-region-av-split"
                => new AvSplitCaptureBackend(),
            "ffmpeg" or "ffmpeg-window-region" or "ffmpeg-region"
                => new FfmpegCaptureBackend(),
            _ => throw new InvalidOperationException("Unknown capture backend decision.")
        };

    private static bool IsDisplayExperimentEnabled()
    {
        var flag = Environment.GetEnvironmentVariable(DisplayBackendEnvVar)?.Trim() ?? "";
        return string.Equals(flag, "wgc-continuous", StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeAvailabilitySource(string source) =>
        source is "fresh_probe" or "cache_hit" or "single_flight"
            ? source
            : "fresh_probe";

    private static string NormalizeProbeReason(string reason) => reason switch
    {
        "helper_missing" or "helper_resolve_failed" or "helper_identity_failed"
            or "version_start_failed" or "version_timeout" or "version_cancelled"
            or "version_output_invalid" or "version_nonzero_exit" or "version_incompatible"
            or "probe_start_failed" or "probe_timeout" or "probe_cancelled"
            or "probe_output_invalid" or "probe_dpi_mismatch" or "probe_wgc_unsupported"
            or "probe_d3d11_uninitialized" or "probe_encoder_unavailable"
            or "probe_bounds_mismatch" or "probe_window_unsupported" or "probe_exception"
            or "window_handle_not_eligible" or "bounds_not_eligible"
            => reason,
        _ => "probe_unavailable"
    };

    private static bool IsKnownSourceKind(string? sourceKind) =>
        string.Equals(sourceKind, "display", StringComparison.Ordinal)
        || string.Equals(sourceKind, "window", StringComparison.Ordinal)
        || string.Equals(sourceKind, "region", StringComparison.Ordinal);

    private sealed record BackendDecision(
        string BackendType,
        CaptureBackendSelectionEvidence Evidence);
}
