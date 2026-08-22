using System;
using System.Linq;
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
    public const string RegionBackendEnvVar = "AGENT_RECORDER_REGION_BACKEND";
    public const string WgcContinuousBackend = "wgc-continuous";
    public const string WgcLegacyAlias = "wgc";

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

    /// <summary>
    /// Builds a complete capture decision without constructing or starting a
    /// backend. This is the only selector entry point used before confirmation.
    /// </summary>
    public static CapturePlan BuildPlan(CaptureConfig cfg) =>
        BuildPlan(cfg, DefaultDisplayProbe);

    /// <summary>
    /// Builds the bounded, per-frame plan used by screenshot_series. This is
    /// intentionally separate from the duration-oriented video selector: a
    /// screenshot series never inherits a WGC/window-surface decision.
    /// </summary>
    public static CapturePlan BuildScreenshotSeriesPlan(CaptureConfig cfg)
    {
        if (cfg == null) throw new ArgumentNullException(nameof(cfg));
        cfg.NormalizeAudioSource();
        if (!IsKnownSourceKind(cfg.SourceKind))
        {
            throw new ApiException(400, "INVALID_ARGUMENT",
                $"Unsupported source type: '{cfg.SourceKind}'. Expected 'display', 'window', or 'region'.");
        }

        if (cfg.AudioRequested)
        {
            throw new ApiException(400, "INVALID_ARGUMENT",
                "Audio is not supported for screenshot_series; remove the audio request.");
        }

        if (cfg.Bounds.w <= 0 || cfg.Bounds.h <= 0)
        {
            throw new ApiException(400, "INVALID_ARGUMENT",
                "Screenshot-series bounds must have positive width and height.");
        }

        const string backend = "ffmpeg-single-frame";
        string semantics = string.Equals(cfg.SourceKind, "window", StringComparison.Ordinal)
            ? "screen_rectangle"
            : DetermineSemantics(cfg.SourceKind, backend);
        var evidence = new CaptureBackendSelectionEvidence(
            backend,
            backend,
            "screenshot_series_single_frame",
            "not_run",
            null,
            false);

        return new CapturePlan(
            backend,
            backend,
            evidence,
            semantics,
            cfg.SourceKind,
            cfg.SourceKind == "window" && cfg.WindowHandle != nint.Zero
                ? $"window_{cfg.WindowHandle.ToInt64()}"
                : null,
            cfg.WindowHandle,
            new CapturePlanBounds(cfg.Bounds.x, cfg.Bounds.y, cfg.Bounds.w, cfg.Bounds.h),
            cfg.SourceKind is "region" or "display" ? cfg.DisplayStableIdentity : null,
            cfg.DisplayBounds.HasValue
                ? new CapturePlanBounds(
                    cfg.DisplayBounds.Value.x,
                    cfg.DisplayBounds.Value.y,
                    cfg.DisplayBounds.Value.w,
                    cfg.DisplayBounds.Value.h)
                : null,
            cfg.SourceKind is "region" or "display" ? cfg.DisplayId : null,
            cfg.DisplayIdentityStatus,
            AudioCaptureSourceKind.None,
            previewSemantics: semantics);
    }

    /// <summary>
    /// Builds a complete capture decision without constructing or starting a
    /// backend. The WGC availability probe is capability-only and does not read
    /// screen pixels.
    /// </summary>
    public static CapturePlan BuildPlan(
        CaptureConfig cfg,
        IWgcContinuousAvailabilityProbe displayProbe)
    {
        if (cfg == null) throw new ArgumentNullException(nameof(cfg));
        if (displayProbe == null) throw new ArgumentNullException(nameof(displayProbe));
        cfg.NormalizeAudioSource();
        if (!IsKnownSourceKind(cfg.SourceKind))
        {
            throw new ApiException(400, "INVALID_ARGUMENT",
                $"Unsupported source type: '{cfg.SourceKind}'. Expected 'display', 'window', or 'region'.");
        }

        var decision = Determine(cfg, displayProbe);
        return new CapturePlan(
            decision.Evidence.RequestedBackend,
            decision.BackendType,
            decision.Evidence,
            DetermineSemantics(cfg.SourceKind, decision.BackendType),
            cfg.SourceKind,
            cfg.SourceKind == "window" && cfg.WindowHandle != nint.Zero
                ? $"window_{cfg.WindowHandle.ToInt64()}"
                : null,
            cfg.WindowHandle,
            cfg.Bounds.w > 0 && cfg.Bounds.h > 0
                ? new CapturePlanBounds(cfg.Bounds.x, cfg.Bounds.y, cfg.Bounds.w, cfg.Bounds.h)
                : null,
            cfg.SourceKind == "region" ? cfg.DisplayStableIdentity : null,
            cfg.DisplayBounds.HasValue
                ? new CapturePlanBounds(
                    cfg.DisplayBounds.Value.x,
                    cfg.DisplayBounds.Value.y,
                    cfg.DisplayBounds.Value.w,
                    cfg.DisplayBounds.Value.h)
                : null,
            cfg.SourceKind is "region" or "display" ? cfg.DisplayId : null,
            cfg.DisplayIdentityStatus,
            cfg.AudioSourceKind,
            cfg.SystemLoopbackEndpoint,
            cfg.SystemLoopbackEndpointName,
            cfg.SystemLoopbackEndpointIsDefault);
    }

    public static CaptureBackendSelection SelectWithEvidence(CaptureConfig cfg) =>
        SelectWithEvidence(cfg, DefaultDisplayProbe);

    public static CaptureBackendSelection SelectWithEvidence(
        CaptureConfig cfg,
        IWgcContinuousAvailabilityProbe displayProbe)
    {
        var plan = BuildPlan(cfg, displayProbe);
        return new CaptureBackendSelection(
            CreateBackend(plan.PlannedBackend),
            plan.PlannedBackend,
            plan.Evidence);
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

    /// <summary>
    /// Normalizes the headless/startup argument contract. The empty value keeps
    /// WGC disabled; the historical <c>wgc</c> spelling is accepted only as an
    /// alias and never survives into the process environment or capture plan.
    /// </summary>
    public static string NormalizeWindowBackendArgument(string? value)
    {
        string normalized = value?.Trim() ?? string.Empty;
        if (normalized.Length == 0)
            return string.Empty;

        if (string.Equals(normalized, WgcLegacyAlias, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(normalized, WgcContinuousBackend, StringComparison.OrdinalIgnoreCase))
            return WgcContinuousBackend;

        throw new ArgumentException(
            $"Unsupported window backend '{value}'. Expected empty, '{WgcContinuousBackend}', or legacy alias '{WgcLegacyAlias}'.",
            nameof(value));
    }

    private static BackendDecision Determine(
        CaptureConfig cfg,
        IWgcContinuousAvailabilityProbe displayProbe)
    {
        if (string.Equals(cfg.SourceKind, "display", StringComparison.Ordinal))
            return DetermineDisplay(cfg, displayProbe);

        if (string.Equals(cfg.SourceKind, "window", StringComparison.Ordinal))
            return DetermineWindow(cfg, displayProbe);

        if (string.Equals(cfg.SourceKind, "region", StringComparison.Ordinal))
            return DetermineRegion(cfg, displayProbe);

        string regionBackend = cfg.AudioRequested ? "ffmpeg-region-av-split" : "ffmpeg-region";
        return DefaultDecision(regionBackend, "default_backend");
    }

    private static BackendDecision DetermineRegion(
        CaptureConfig cfg,
        IWgcContinuousAvailabilityProbe probe)
    {
        string fallbackBackend = cfg.AudioRequested ? "ffmpeg-region-av-split" : "ffmpeg-region";
        string? flag = Environment.GetEnvironmentVariable(RegionBackendEnvVar);
        if (!string.Equals(flag, WgcContinuousBackend, StringComparison.Ordinal))
        {
            return new BackendDecision(
                fallbackBackend,
                new CaptureBackendSelectionEvidence(
                    "default",
                    fallbackBackend,
                    "experiment_disabled",
                    "not_run",
                    null,
                    false));
        }

        const string requestedBackend = WgcContinuousBackend;
        if (cfg.AudioRequested)
            return FallbackDecision(
                fallbackBackend,
                requestedBackend,
                cfg.IsMicrophone ? "microphone_not_eligible" : "audio_not_eligible");
        if (!cfg.DurationSeconds.HasValue || cfg.DurationSeconds.Value is < 1 or > 10)
            return FallbackDecision(fallbackBackend, requestedBackend, "duration_not_eligible");
        if (cfg.Fps is < 1 or > 60)
            return FallbackDecision(fallbackBackend, requestedBackend, "fps_not_eligible");
        if (cfg.Bounds.w <= 0 || cfg.Bounds.h <= 0 ||
            !cfg.DisplayBounds.HasValue || string.IsNullOrWhiteSpace(cfg.DisplayId))
            return FallbackDecision(fallbackBackend, requestedBackend, "region_bounds_not_eligible");

        var display = cfg.DisplayBounds.Value;
        if (!WgcRegionGeometry.TryGetCrop(
                new WgcRegionRect(display.x, display.y, display.w, display.h),
                new WgcRegionRect(cfg.Bounds.x, cfg.Bounds.y, cfg.Bounds.w, cfg.Bounds.h),
                out _, out _))
            return FallbackDecision(fallbackBackend, requestedBackend, "region_bounds_not_eligible");

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
        bool evidenceHasTargetDisplay = availability.Evidence?.Monitors.Count(m =>
            cfg.DisplayBounds.HasValue &&
            m.Equals(new WgcMonitorBounds(
                cfg.DisplayBounds.Value.x,
                cfg.DisplayBounds.Value.y,
                cfg.DisplayBounds.Value.w,
                cfg.DisplayBounds.Value.h))) == 1;
        bool available = availability.Available && evidenceHasTargetDisplay;
        string reason = available
            ? source switch
            {
                "cache_hit" => "wgc_cache_hit",
                "single_flight" => "wgc_single_flight",
                _ => "wgc_probe_success"
            }
            : availability.Available && !evidenceHasTargetDisplay
                ? "probe_bounds_mismatch"
                : NormalizeProbeReason(availability.ReasonCode);

        return new BackendDecision(
            available ? WgcContinuousBackend : fallbackBackend,
            new CaptureBackendSelectionEvidence(
                requestedBackend,
                available ? WgcContinuousBackend : fallbackBackend,
                reason,
                source,
                availability.ElapsedMs,
                !available));
    }

    private static BackendDecision DetermineWindow(
        CaptureConfig cfg,
        IWgcContinuousAvailabilityProbe probe)
    {
        var flag = Environment.GetEnvironmentVariable(WgcEnvVar)?.Trim() ?? "";
        if (string.Equals(flag, WgcLegacyAlias, StringComparison.OrdinalIgnoreCase))
            flag = WgcContinuousBackend;

        if (!string.Equals(flag, WgcContinuousBackend, StringComparison.OrdinalIgnoreCase))
        {
            string backendType = cfg.AudioRequested ? "ffmpeg-window-region-av-split" : "ffmpeg-window-region";
            return DefaultDecision(backendType, "window_backend_selected");
        }

        const string requestedBackend = WgcContinuousBackend;
        string fallbackBackend = cfg.AudioRequested ? "ffmpeg-window-region-av-split" : "ffmpeg-window-region";
        if (cfg.AudioRequested)
            return FallbackDecision(
                fallbackBackend,
                requestedBackend,
                cfg.IsMicrophone ? "microphone_not_eligible" : "audio_not_eligible");
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
                WgcContinuousBackend,
                new CaptureBackendSelectionEvidence(
                    requestedBackend,
                    WgcContinuousBackend,
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
            string backendType = cfg.AudioRequested ? "ffmpeg-av-split" : "ffmpeg";
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

        if (cfg.AudioRequested)
            return FallbackDecision(
                "ffmpeg-av-split",
                requestedBackend,
                cfg.IsMicrophone ? "microphone_not_eligible" : "audio_not_eligible");
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

    /// <summary>
    /// Constructs a backend after the caller has completed confirmation and
    /// plan revalidation. This method has no role in plan construction.
    /// </summary>
    public static ICaptureBackend CreateBackend(string backendType) =>
        backendType switch
        {
            "wgc-continuous" => new WgcContinuousCaptureBackend(),
            "ffmpeg-av-split" or "ffmpeg-window-region-av-split" or "ffmpeg-region-av-split"
                => new AvSplitCaptureBackend(),
            "ffmpeg" or "ffmpeg-window-region" or "ffmpeg-region"
                => new FfmpegCaptureBackend(),
            _ => throw new InvalidOperationException("Unknown capture backend decision.")
        };

    internal static string DetermineSemanticsForTests(string sourceKind, string backendType) =>
        DetermineSemantics(sourceKind, backendType);

    private static string DetermineSemantics(string sourceKind, string backendType)
    {
        if (string.Equals(sourceKind, "window", StringComparison.Ordinal))
        {
            return backendType switch
            {
                "wgc-continuous" => "window_surface",
                "ffmpeg-window-region" or "ffmpeg-window-region-av-split" => "screen_rectangle",
                _ => throw new ApiException(
                    500,
                    "CAPTURE_SEMANTICS_UNKNOWN",
                    $"Capture backend '{backendType}' has no declared window capture semantics.")
            };
        }

        if (string.Equals(sourceKind, "display", StringComparison.Ordinal))
            return "display_surface";

        return "region_rectangle";
    }

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
            or "region_bounds_not_eligible" or "display_identity_not_eligible"
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
