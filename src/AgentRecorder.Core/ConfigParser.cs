using System;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using AgentRecorder.Capture;
using AgentRecorder.Infrastructure;
using AgentRecorder.Logging;
using AgentRecorder.Security;
using AgentRecorder.Windows;
using ApiException = AgentRecorder.Infrastructure.ApiException;
namespace AgentRecorder.Core;

public static class ConfigParser
{
    private static readonly int[] AllowedFps = { 15, 24, 30, 60 };
    private static readonly TimeSpan DeviceEnumerationTimeout = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Safe fallback provider used when no provider is explicitly supplied.
    /// It is immutable and returns no devices, keeping tests without audio
    /// fixtures deterministic and preventing accidental shared mutable state.
    /// </summary>
    private static readonly IMicrophoneDeviceProvider EmptyProvider = new EmptyMicrophoneProvider();
    private static readonly IMicrophoneStatusProvider NullStatusProvider = NullMicrophoneStatusProvider.Instance;
    private static readonly ISystemAudioEndpointProvider EmptySystemAudioProvider = new EmptySystemAudioEndpointProvider();

    public static Recording Build(JsonNode cfg, string agent, out object summary,
        IMicrophoneDeviceProvider? microphoneProvider = null,
        IMicrophoneStatusProvider? microphoneStatusProvider = null,
        ISystemAudioEndpointProvider? systemAudioEndpointProvider = null,
        ISystemAudioExperimentFlag? systemAudioExperimentFlag = null,
        SystemAudioEndpointInfo? preResolvedSystemAudioEndpoint = null)
    {
        RejectUnsupportedContinuousFeatures(cfg);

        // =====================================================================
        // Step 0: audio intent and device selection MUST come before source
        // enumeration, region-selection UI, or output path construction.
        // This ensures microphone failures (unknown device, no devices,
        // enumeration unavailable, system audio requested, muted device) fail
        // fast and cheap.
        // =====================================================================
        var resolvedAudio = ResolveAudioIntentDetails(
            cfg,
            microphoneProvider,
            microphoneStatusProvider,
            systemAudioEndpointProvider,
            systemAudioExperimentFlag,
            preResolvedSystemAudioEndpoint);
        var resolvedMic = resolvedAudio.Microphone;

        // =====================================================================
        // Step 1: nested.role validation MUST come before source enumeration.
        // This ensures invalid role is rejected even when displays/windows
        // are unavailable or source is malformed.
        // =====================================================================
        var nested = cfg["nested"];
        if (nested != null)
        {
            var role = Str(nested["role"]);
            if (role != null && role != "outer" && role != "inner")
                throw new ApiException(400, "INVALID_ARGUMENT",
                    $"nested.role '{role}' is not valid; must be 'outer' or 'inner'");
        }

        // TEST_MODE: If set, skip expensive source enumeration (displays/windows).
        // This allows Phase-4 concurrency guard testing without real displays.
        bool testMode = Environment.GetEnvironmentVariable("AGENT_RECORDER_TEST_MODE") == "1";

        var rec = new Recording { Agent = agent };
        var src = cfg["source"] ?? throw Inv("source is required");
        var type = Str(src["type"]) ?? throw Inv("source.type is required");
        var cap = new CaptureConfig();

        if (type == "display")
        {
            var did = Str(src["display_id"]);
            if (did == null)
                throw Inv("display_id required for display source");

            if (testMode)
            {
                rec.SourceType = "display";
                rec.SourceTitle = $"Test Display ({did})";
                cap.SourceKind = "display";
                cap.Bounds = (0, 0, 1920, 1080);
                cap.DisplayId = did;
                cap.DisplayBounds = cap.Bounds;
                cap.DisplayStableIdentity = $"synthetic-test-display:{did}";
                cap.DisplayIdentityStatus = DisplayIdentityResolutionStatus.Resolved;
            }
            else
            {
                var d = SystemQuery.EnumDisplayTopology().FirstOrDefault(x => x.id == did)
                        ?? throw new ApiException(404, "SOURCE_NOT_FOUND",
                            $"Display {did} not found", new { suggested_action = "list_displays" });
                rec.SourceType = "display";
                rec.SourceTitle = d.name;
                cap.SourceKind = "display";
                cap.Bounds = (d.bounds.x, d.bounds.y, d.bounds.width, d.bounds.height);
                cap.DisplayId = d.id;
                cap.DisplayBounds = (d.bounds.x, d.bounds.y, d.bounds.width, d.bounds.height);
                cap.DisplayStableIdentity = d.stable_identity;
                cap.DisplayIdentityStatus = d.identity_status;
            }
        }
        else if (type == "window")
        {
            var wid = Str(src["window_id"]) ?? throw Inv("window_id required for window source");

            if (testMode)
            {
                rec.SourceType = "window";
                rec.SourceTitle = $"Test Window ({wid})";
                rec.SourceApplication = "test-window";
                cap.SourceKind = "window";
                cap.WindowTitle = wid;
                cap.Bounds = (0, 0, 1280, 720);
                // In test mode we still want a stable HWND so preflight can look it up
                // via the injectable SystemQuery.SetWindowProvider seam.
                if (WindowIdParser.TryParse(wid, out var hwnd))
                    cap.WindowHandle = hwnd;
            }
            else
            {
                var w = SystemQuery.EnumWindows(true, false).FirstOrDefault(x => x.id == wid)
                        ?? throw new ApiException(404, "SOURCE_NOT_FOUND",
                            "The selected window no longer exists. Call GET /api/v1/windows to choose another.",
                            new { suggested_action = "list_windows" });
                PolicyEngine.CheckDenylist(w.title);
                PolicyEngine.CheckDenylistByProcessName(w.app_name);
                WindowIdParser.RejectMinimized(w.is_minimized, w.title);

                var capBounds = ClampWindowBoundsToVirtualScreen(w.bounds);

                if (capBounds.width <= 0 || capBounds.height <= 0)
                    throw new ApiException(400, "SOURCE_UNAVAILABLE",
                        "Window is outside the capturable desktop area.",
                        new { suggested_action = "restore_or_move_window_then_retry" });

                const int MinSize = 32;
                if (capBounds.width < MinSize || capBounds.height < MinSize)
                    throw new ApiException(400, "INVALID_ARGUMENT",
                        $"Window is too small ({capBounds.width}x{capBounds.height}). Minimum recording size is {MinSize}x{MinSize}.",
                        new { suggested_action = "enlarge_the_window_or_select_a_different_window" });

                var normalizedBw = NormalizeDimension(capBounds.width);
                var normalizedBh = NormalizeDimension(capBounds.height);

                rec.SourceType = "window";
                rec.SourceTitle = w.title;
                rec.SourceApplication = w.app_name;
                cap.SourceKind = "window";
                cap.WindowTitle = w.title;
                cap.WindowHandle = WindowIdParser.Parse(wid);
                cap.Bounds = (capBounds.x, capBounds.y, normalizedBw, normalizedBh);
            }
        }
        else if (type == "region")
        {
            var did = Str(src["display_id"]) ?? throw Inv("display_id required for region source");

            var coordSpace = Str(src["coordinate_space"]) ?? "virtual_screen";
            if (coordSpace != "virtual_screen")
                throw new ApiException(400, "INVALID_ARGUMENT", $"coordinate_space '{coordSpace}' not supported; only 'virtual_screen' is supported");

            var bnode = src["bounds"]
                ?? throw Inv("bounds required for region source");

            var bx = bnode["x"]?.GetValue<int?>() ?? throw Inv("bounds.x required");
            var by = bnode["y"]?.GetValue<int?>() ?? throw Inv("bounds.y required");
            var bw = bnode["width"]?.GetValue<int?>() ?? throw Inv("bounds.width required");
            var bh = bnode["height"]?.GetValue<int?>() ?? throw Inv("bounds.height required");

            // Normalize odd dimensions to even (required by x264/yuv420p)
            var normalizedBw = NormalizeDimension(bw);
            var normalizedBh = NormalizeDimension(bh);
            var wasNormalized = (normalizedBw != bw || normalizedBh != bh);

            if (bw < 0) throw Inv("bounds.width must be non-negative");
            if (bh < 0) throw Inv("bounds.height must be non-negative");
            if (bw == 0 || bh == 0) throw Inv("bounds.width and bounds.height must be at least 1");

            const int MinSize = 32;
            if (bw < MinSize || bh < MinSize)
                throw Inv($"bounds.width and bounds.height must be at least {MinSize}x{MinSize}");

            if (!testMode)
            {
                // Resolve public ID, bounds, and the internal identity from one
                // active topology snapshot. A public ordinal is never used as
                // a fallback identity when Windows parsing is unavailable.
                var d = SystemQuery.EnumDisplayTopology().FirstOrDefault(x => x.id == did)
                        ?? throw new ApiException(404, "SOURCE_NOT_FOUND",
                            $"Display {did} not found", new { suggested_action = "list_displays" });

                if (d.identity_status != DisplayIdentityResolutionStatus.Resolved ||
                    !DisplayIdentityDeriver.IsFixedFormat(d.stable_identity))
                {
                    throw new ApiException(503, "DISPLAY_IDENTITY_UNAVAILABLE",
                        "The selected display identity could not be resolved safely. Retry after the display topology is stable.",
                        new { suggested_action = "list_displays_and_retry" });
                }

                // Check bounds are within display
                var db = d.bounds;
                long regionRight = (long)bx + bw;
                long regionBottom = (long)by + bh;
                long displayRight = (long)db.x + db.width;
                long displayBottom = (long)db.y + db.height;
                if (bx < db.x || by < db.y
                    || regionRight > displayRight || regionBottom > displayBottom)
                {
                    throw new ApiException(400, "INVALID_ARGUMENT",
                        $"Region bounds (x={bx},y={by},w={bw},h={bh}) exceeds display bounds (x={db.x},y={db.y},w={db.width},h={db.height})",
                        new { display_bounds = new { x = db.x, y = db.y, width = db.width, height = db.height } });
                }

                rec.SourceType = "region";
                rec.SourceTitle = $"region:{d.name}";
                cap.SourceKind = "region";
                cap.Bounds = (bx, by, normalizedBw, normalizedBh);
                cap.DisplayId = d.id;
                cap.DisplayBounds = (db.x, db.y, db.width, db.height);
                cap.DisplayStableIdentity = d.stable_identity;
                cap.DisplayIdentityStatus = d.identity_status;
                cap.RegionNormalizedBounds = wasNormalized ? (normalizedBw, normalizedBh) : null;
            }
            else
            {
                // Test mode: use placeholder bounds without display validation
                rec.SourceType = "region";
                rec.SourceTitle = $"Test Region ({bx},{by},{bw},{bh})";
                cap.SourceKind = "region";
                cap.Bounds = (bx, by, normalizedBw, normalizedBh);
                cap.DisplayId = did;
                cap.DisplayBounds = (0, 0, 1920, 1080);
                cap.DisplayStableIdentity = $"synthetic-test-display:{did}";
                cap.DisplayIdentityStatus = DisplayIdentityResolutionStatus.Resolved;
            }
        }
        else throw new ApiException(400, "UNSUPPORTED_FEATURE",
            $"source.type '{type}' not supported in MVP");

        var micNode = cfg["audio"]?["microphone"];
        rec.Microphone = micNode?["enabled"]?.GetValue<bool>() ?? false;
        cap.Microphone = rec.Microphone;

        if (rec.Microphone)
        {
            if (resolvedMic == null)
                throw new ApiException(503, "AUDIO_DEVICE_NOT_AVAILABLE",
                    "No microphone input device is available.",
                    new { suggested_action = "list_audio_devices" });
            rec.MicrophoneDeviceId = resolvedMic.Id;
            rec.MicrophoneDeviceName = resolvedMic.Name;
            cap.MicDevice = resolvedMic.Id;
            cap.MicDeviceName = resolvedMic.Name;
        }

        if (resolvedAudio.SystemAudioEndpoint != null)
        {
            rec.AudioSourceKind = AudioCaptureSourceKind.SystemLoopback;
            rec.SystemAudioEndpointId = resolvedAudio.SystemAudioEndpoint.Id;
            rec.SystemAudioEndpointName = resolvedAudio.SystemAudioEndpoint.Name;
            rec.SystemAudioEndpointIsDefault = resolvedAudio.SystemAudioEndpoint.IsDefaultMultimedia;
            cap.AudioSourceKind = AudioCaptureSourceKind.SystemLoopback;
            cap.SystemLoopbackEndpoint = resolvedAudio.SystemAudioEndpoint.Id;
            cap.SystemLoopbackEndpointName = resolvedAudio.SystemAudioEndpoint.Name;
            cap.SystemLoopbackEndpointIsDefault = resolvedAudio.SystemAudioEndpoint.IsDefaultMultimedia;
        }
        else if (rec.Microphone)
        {
            rec.AudioSourceKind = AudioCaptureSourceKind.Microphone;
            cap.AudioSourceKind = AudioCaptureSourceKind.Microphone;
        }

        var v = cfg["video"];
        int fps = v?["fps"]?.GetValue<int>() ?? 30;
        if (!AllowedFps.Contains(fps)) throw Inv("fps must be one of 15, 24, 30, 60");
        cap.Fps = fps;
        cap.Quality = Str(v?["quality"]) ?? "medium";

        var stop = cfg["stop_condition"];
        var stype = Str(stop?["type"]) ?? "manual";
        if (stype == "duration")
        {
            int secs = stop?["seconds"]?.GetValue<int>() ?? 60;
            if (secs <= 0 || secs > 7200) throw Inv("duration seconds must be 1..7200");
            rec.DurationSeconds = secs;
            cap.DurationSeconds = secs;
        }

        rec.OutputPath = OutputPathResolver.BuildOutputPath(cfg["output"], rec);
        cap.OutputPath = rec.OutputPath;
        rec.Config = cap;

        // Assign nested metadata. Role validity already validated in Step 0.
        var nestedVal = cfg["nested"];
        if (nestedVal != null)
        {
            var role = Str(nestedVal["role"]);
            if (role == "outer" || role == "inner")
            {
                rec.NestedRole = role;
                rec.NestedSessionId = Str(nestedVal["session_id"]);
                if (role == "inner")
                {
                    rec.ParentRecordingId = Str(nestedVal["parent_recording_id"]);
                    // Note: parent state (must be 'recording') is validated by
                    // RecordingEngine.CreateRecording Phase 4 after Build.
                }
                else
                {
                    rec.IsNestedParent = true;
                }
            }
        }

        summary = new
        {
            source = $"{rec.SourceType}: {rec.SourceTitle}",
            audio = rec.AudioSourceKind == AudioCaptureSourceKind.SystemLoopback
                ? rec.SystemAudioEndpointIsDefault == true
                    ? $"System audio: On (Default output: {rec.SystemAudioEndpointName})"
                    : $"System audio: On (Selected output: {rec.SystemAudioEndpointName})"
                : rec.Microphone ? $"Microphone: {rec.MicrophoneDeviceName}" : "No audio",
            audio_source_kind = rec.AudioSourceKind switch
            {
                AudioCaptureSourceKind.Microphone => "microphone",
                AudioCaptureSourceKind.SystemLoopback => "system-loopback",
                _ => "none"
            },
            audio_system_enabled = rec.AudioSourceKind == AudioCaptureSourceKind.SystemLoopback,
            audio_system_default_output = rec.SystemAudioEndpointIsDefault == true
                ? rec.SystemAudioEndpointName
                : null,
            audio_system_output_name = rec.SystemAudioEndpointName,
            audio_system_output_is_default = rec.SystemAudioEndpointIsDefault,
            audio_system_output_selection = rec.SystemAudioEndpointIsDefault == true ? "default" : "selected",
            audio_device = rec.Microphone ? rec.MicrophoneDeviceName : null,
            audio_volume_percent = rec.Microphone ? resolvedMic?.VolumePercent : null,
            duration = rec.DurationSeconds is int s ? $"{s}s" : "Manual stop",
            output = rec.OutputPath,
            nested_role = rec.NestedRole ?? "none"
        };
        return rec;
    }

    private static string? Str(JsonNode? n) => n?.GetValue<string>();
    private static ApiException Inv(string m) => new(400, "INVALID_ARGUMENT", m);

    /// <summary>
    /// Resolves the requested microphone device from an already-enriched device
    /// list. <paramref name="deviceId"/> selects explicitly; omitted device_id
    /// first prefers the single fresh CoreAudio multimedia default, then a single
    /// active device, otherwise requires an explicit choice. No audio stream is
    /// opened; only device enumeration is performed.
    /// </summary>
    private static MicrophoneDeviceInfo ResolveMicrophoneDevice(JsonNode? micNode, IReadOnlyList<MicrophoneDeviceInfo> devices)
    {
        var requestedId = Str(micNode?["device_id"]);
        if (!string.IsNullOrEmpty(requestedId))
        {
            var match = devices.FirstOrDefault(d => string.Equals(d.Id, requestedId, StringComparison.Ordinal));
            if (match == null)
                throw new ApiException(404, "AUDIO_DEVICE_NOT_FOUND",
                    "The requested microphone device was not found.",
                    new { suggested_action = "list_audio_devices" });
            return match;
        }

        // A device is viable unless it is known to be inactive. Unknown state
        // is treated as viable so transient COM failures do not block recording.
        var viable = devices.Where(d => !string.Equals(d.State, "inactive", StringComparison.OrdinalIgnoreCase)).ToList();
        var defaultViable = viable.Where(d => d.IsDefault == true).ToList();

        // Prefer the single reliably-identified CoreAudio multimedia default.
        if (defaultViable.Count == 1)
            return defaultViable[0];

        if (viable.Count == 1)
            return viable[0];

        if (viable.Count == 0)
            throw new ApiException(503, "AUDIO_DEVICE_NOT_AVAILABLE",
                "No microphone input device is available.",
                new { suggested_action = "list_audio_devices" });

        throw new ApiException(400, "AUDIO_DEVICE_REQUIRED",
            "Multiple microphone devices are available. Specify audio.microphone.device_id from GET /api/v1/audio/devices.",
            new { suggested_action = "list_audio_devices" });
    }

    /// <summary>
    /// Validates audio intent and resolves the microphone device before any
    /// display/window/region enumeration. Returns the resolved device when
    /// microphone is enabled, or <c>null</c> when it is disabled/absent.
    /// Throws <see cref="ApiException"/> for system audio or microphone failures,
    /// including <c>AUDIO_DEVICE_MUTED</c> when the selected device is muted.
    /// </summary>
    public static MicrophoneDeviceInfo? ResolveAudioIntent(JsonNode cfg,
        IMicrophoneDeviceProvider? provider = null,
        IMicrophoneStatusProvider? statusProvider = null,
        ISystemAudioEndpointProvider? systemAudioEndpointProvider = null,
        ISystemAudioExperimentFlag? systemAudioExperimentFlag = null,
        SystemAudioEndpointInfo? preResolvedSystemAudioEndpoint = null)
        => ResolveAudioIntentDetails(
            cfg,
            provider,
            statusProvider,
            systemAudioEndpointProvider,
            systemAudioExperimentFlag,
            preResolvedSystemAudioEndpoint).Microphone;

    /// <summary>
    /// Resolves the complete audio intent before target enumeration or UI.
    /// The returned endpoint is a stable snapshot that callers may bind into
    /// the request before the existing region-selection/confirmation flow.
    /// </summary>
    public static AudioIntentResolution ResolveAudioIntentDetails(JsonNode cfg,
        IMicrophoneDeviceProvider? provider = null,
        IMicrophoneStatusProvider? statusProvider = null,
        ISystemAudioEndpointProvider? systemAudioEndpointProvider = null,
        ISystemAudioExperimentFlag? systemAudioExperimentFlag = null,
        SystemAudioEndpointInfo? preResolvedSystemAudioEndpoint = null)
    {
        var flag = systemAudioExperimentFlag ?? DisabledSystemAudioExperimentFlag.Instance;
        RejectUnsupportedAudioFeatures(cfg, flag);

        var micNode = cfg["audio"]?["microphone"];
        var systemNode = cfg["audio"]?["system_audio"];
        bool microphoneEnabled = micNode?["enabled"]?.GetValue<bool>() ?? false;
        bool systemEnabled = systemNode?["enabled"]?.GetValue<bool>() ?? false;

        if (microphoneEnabled && systemEnabled)
            throw new ApiException(400, "UNSUPPORTED_FEATURE",
                "Microphone and system audio cannot be enabled together in this controlled flow.",
                new { suggested_action = "choose_one_audio_source" });

        SystemAudioEndpointInfo? resolvedSystem = null;

        if (systemEnabled)
        {
            resolvedSystem = preResolvedSystemAudioEndpoint == null
                ? ResolveSystemAudioEndpoint(systemNode, systemAudioEndpointProvider)
                : ValidatePreResolvedSystemAudioEndpoint(systemNode, preResolvedSystemAudioEndpoint);
        }

        if (!microphoneEnabled)
            return new AudioIntentResolution(null, resolvedSystem);

        var actualProvider = provider ?? EmptyProvider;
        var actualStatusProvider = statusProvider ?? NullStatusProvider;

        // Enumerate once, then attach fresh CoreAudio status to every device.
        // Fresh status drives default selection and active-state checks; it is
        // never read from the 10-second dshow enumeration cache.
        var devices = EnumerateMicrophoneDevices(actualProvider);
        var enriched = EnrichWithFreshStatus(devices, actualStatusProvider);

        var device = ResolveMicrophoneDevice(micNode, enriched);

        // If the selected endpoint is known to be non-active, fail before UI.
        // "not_present" is fresh CoreAudio evidence that the endpoint id no
        // longer exists (e.g. a stale enumeration cache entry); it is rejected
        // the same way as an inactive endpoint. Unknown state is treated as
        // active so a transient COM failure does not block recording.
        if (string.Equals(device.State, "inactive", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(device.State, "not_present", StringComparison.OrdinalIgnoreCase))
        {
            throw new ApiException(503, "AUDIO_DEVICE_NOT_AVAILABLE",
                "The selected microphone is currently unavailable. Please check the device and try again.",
                new { suggested_action = "check_audio_device", device_id = device.Id });
        }

        if (device.IsMuted == true)
        {
            throw new ApiException(409, "AUDIO_DEVICE_MUTED",
                "The selected microphone is muted. Please unmute it in Windows sound settings and try again.",
                new { suggested_action = "unmute_microphone_in_windows_settings", device_id = device.Id });
        }

        return new AudioIntentResolution(device, resolvedSystem);
    }

    private static IReadOnlyList<MicrophoneDeviceInfo> EnumerateMicrophoneDevices(IMicrophoneDeviceProvider provider)
    {
        try
        {
            using var cts = new System.Threading.CancellationTokenSource(DeviceEnumerationTimeout);
            return provider.GetDevicesAsync(cts.Token)
                .WaitAsync(DeviceEnumerationTimeout).GetAwaiter().GetResult();
        }
        catch (MicrophoneEnumerationException ex)
        {
            throw new ApiException(503, ex.ErrorCode, "Microphone device enumeration is currently unavailable.",
                new { suggested_action = "retry_or_check_audio_devices" });
        }
        catch (System.OperationCanceledException)
        {
            throw new ApiException(503, "device_enumeration_timeout", "Microphone device enumeration timed out.",
                new { suggested_action = "retry_or_check_audio_devices" });
        }
        catch (Exception)
        {
            throw new ApiException(503, "device_enumeration_unavailable", "Microphone device enumeration is currently unavailable.",
                new { suggested_action = "retry_or_check_audio_devices" });
        }
    }

    private static IReadOnlyList<MicrophoneDeviceInfo> EnrichWithFreshStatus(IReadOnlyList<MicrophoneDeviceInfo> devices, IMicrophoneStatusProvider statusProvider)
    {
        if (devices.Count == 0)
            return devices;

        var enriched = new MicrophoneDeviceInfo[devices.Count];
        for (int i = 0; i < devices.Count; i++)
        {
            var d = devices[i];
            var status = QueryMicrophoneStatus(d.Id, statusProvider);
            enriched[i] = d with
            {
                IsMuted = status.IsMuted,
                VolumePercent = status.VolumePercent,
                IsDefault = status.IsDefault ?? d.IsDefault,
                State = status.State ?? d.State
            };
        }
        return enriched;
    }

    private static MicrophoneStatus QueryMicrophoneStatus(string deviceId, IMicrophoneStatusProvider provider)
    {
        try
        {
            using var cts = new System.Threading.CancellationTokenSource(DeviceEnumerationTimeout);
            return provider.GetStatusAsync(deviceId, cts.Token)
                .WaitAsync(DeviceEnumerationTimeout).GetAwaiter().GetResult();
        }
        catch
        {
            return new MicrophoneStatus(null, null, null, null);
        }
    }


    /// <summary>
    /// Rejects explicit requests for audio capabilities that are not yet
    /// implemented. Must run before any display/window enumeration or output
    /// path construction so that the failure is fast and cheap.
    /// Microphone is implemented; system audio remains blocked.
    /// </summary>
    public static void RejectUnsupportedAudioFeatures(
        JsonNode cfg,
        ISystemAudioExperimentFlag? systemAudioExperimentFlag = null)
    {
        var sys = cfg["audio"]?["system_audio"];
        if (sys?["enabled"]?.GetValue<bool>() == true &&
            !(systemAudioExperimentFlag ?? DisabledSystemAudioExperimentFlag.Instance).IsEnabled)
            throw new ApiException(400, "CAPABILITY_NOT_IMPLEMENTED",
                "System audio recording is disabled for the controlled experiment.",
                new { capability = "system_audio", suggested_action = "retry_without_audio" });
    }

    private static SystemAudioEndpointInfo ResolveSystemAudioEndpoint(
        JsonNode? systemNode,
        ISystemAudioEndpointProvider? provider)
    {
        if (systemNode == null)
            throw new ApiException(400, "INVALID_ARGUMENT", "audio.system_audio is required when system audio is enabled.");

        var endpointProvider = provider ?? EmptySystemAudioProvider;
        var requestedId = Str(systemNode["device_id"]);
        bool explicitId = systemNode["device_id"] != null;
        if (explicitId && string.IsNullOrWhiteSpace(requestedId))
            throw new ApiException(400, "INVALID_ARGUMENT",
                "audio.system_audio.device_id must be a non-empty exact render endpoint id.",
                new { suggested_action = "use_the_current_default_output_or_a_valid_render_endpoint_id" });

        SystemAudioEndpointInfo? endpoint;
        try
        {
            using var cts = new System.Threading.CancellationTokenSource(DeviceEnumerationTimeout);
            endpoint = (explicitId
                    ? endpointProvider.GetEndpointAsync(requestedId!, cts.Token)
                    : endpointProvider.GetDefaultMultimediaRenderEndpointAsync(cts.Token))
                .WaitAsync(DeviceEnumerationTimeout).GetAwaiter().GetResult();
        }
        catch (SystemAudioEndpointEnumerationException ex)
        {
            throw new ApiException(503, ex.ErrorCode,
                "The system-audio output endpoint could not be enumerated.",
                new { suggested_action = "retry_after_checking_the_default_output_device" });
        }
        catch (OperationCanceledException)
        {
            throw new ApiException(503, "system_audio_endpoint_enumeration_timeout",
                "Timed out while resolving the system-audio output endpoint.",
                new { suggested_action = "retry_after_checking_the_default_output_device" });
        }
        catch (Exception)
        {
            throw new ApiException(503, "system_audio_endpoint_enumeration_unavailable",
                "The system-audio output endpoint could not be enumerated.",
                new { suggested_action = "retry_after_checking_the_default_output_device" });
        }

        if (endpoint == null)
            throw new ApiException(explicitId ? 404 : 503,
                explicitId ? "SYSTEM_AUDIO_ENDPOINT_NOT_FOUND" : "SYSTEM_AUDIO_DEFAULT_ENDPOINT_NOT_FOUND",
                explicitId
                    ? "The requested system-audio render endpoint was not found."
                    : "The current default multimedia render endpoint is unavailable.",
                new { suggested_action = "refresh_audio_output_devices" });

        if (!string.Equals(endpoint.Direction, "render", StringComparison.OrdinalIgnoreCase))
            throw new ApiException(400, "SYSTEM_AUDIO_ENDPOINT_WRONG_DIRECTION",
                "System audio requires an output render endpoint.",
                new { suggested_action = "select_a_render_endpoint" });

        if (!string.Equals(endpoint.State, "active", StringComparison.OrdinalIgnoreCase))
            throw new ApiException(503, "SYSTEM_AUDIO_ENDPOINT_INACTIVE",
                "The selected system-audio output endpoint is not active.",
                new { suggested_action = "activate_or_reconnect_the_output_device", device_id = endpoint.Id });

        if (!explicitId && !endpoint.IsDefaultMultimedia)
            throw new ApiException(503, "SYSTEM_AUDIO_DEFAULT_ENDPOINT_NOT_FOUND",
                "The endpoint provider did not return the current multimedia default output.",
                new { suggested_action = "retry_after_checking_the_default_output_device" });

        if (string.IsNullOrWhiteSpace(endpoint.Id) || string.IsNullOrWhiteSpace(endpoint.Name))
            throw new ApiException(503, "SYSTEM_AUDIO_ENDPOINT_METADATA_UNAVAILABLE",
                "The system-audio output endpoint did not provide safe display metadata.",
                new { suggested_action = "retry_after_checking_the_default_output_device" });

        return endpoint;
    }

    private static SystemAudioEndpointInfo ValidatePreResolvedSystemAudioEndpoint(
        JsonNode? systemNode,
        SystemAudioEndpointInfo endpoint)
    {
        var requestedId = Str(systemNode?["device_id"]);
        if (!string.IsNullOrWhiteSpace(requestedId) &&
            !string.Equals(requestedId, endpoint.Id, StringComparison.Ordinal))
        {
            throw new ApiException(409, "SYSTEM_AUDIO_ENDPOINT_CHANGED",
                "The approved system-audio endpoint no longer matches the request.",
                new { suggested_action = "retry_after_refreshing_audio_output_devices" });
        }

        if (string.IsNullOrWhiteSpace(endpoint.Id) || string.IsNullOrWhiteSpace(endpoint.Name))
            throw new ApiException(503, "SYSTEM_AUDIO_ENDPOINT_METADATA_UNAVAILABLE",
                "The approved system-audio output endpoint did not provide safe display metadata.",
                new { suggested_action = "retry_after_checking_the_default_output_device" });

        if (!string.Equals(endpoint.Direction, "render", StringComparison.OrdinalIgnoreCase))
            throw new ApiException(400, "SYSTEM_AUDIO_ENDPOINT_WRONG_DIRECTION",
                "System audio requires an output render endpoint.",
                new { suggested_action = "select_a_render_endpoint" });

        if (!string.Equals(endpoint.State, "active", StringComparison.OrdinalIgnoreCase))
            throw new ApiException(503, "SYSTEM_AUDIO_ENDPOINT_INACTIVE",
                "The selected system-audio output endpoint is not active.",
                new { suggested_action = "activate_or_reconnect_the_output_device", device_id = endpoint.Id });

        return endpoint;
    }

    public sealed record AudioIntentResolution(
        MicrophoneDeviceInfo? Microphone,
        SystemAudioEndpointInfo? SystemAudioEndpoint);

    /// <summary>
    /// Binds the already validated endpoint snapshot into the private request
    /// copy used by quick-recording target resolution. The subsequent Build()
    /// therefore validates and plans the exact same endpoint, even if the
    /// Windows default output changes while the region selector is visible.
    /// </summary>
    public static void BindResolvedSystemAudioEndpoint(JsonNode cfg, SystemAudioEndpointInfo endpoint)
    {
        if (cfg["audio"] is not JsonObject audio ||
            audio["system_audio"] is not JsonObject systemAudio)
            throw new ApiException(400, "INVALID_ARGUMENT",
                "audio.system_audio is required when system audio is enabled.");

        systemAudio["device_id"] = endpoint.Id;
    }

    private sealed class EmptySystemAudioEndpointProvider : ISystemAudioEndpointProvider
    {
        public Task<SystemAudioEndpointInfo?> GetDefaultMultimediaRenderEndpointAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<SystemAudioEndpointInfo?>(null);

        public Task<SystemAudioEndpointInfo?> GetEndpointAsync(string endpointId, CancellationToken cancellationToken = default)
            => Task.FromResult<SystemAudioEndpointInfo?>(null);
    }

    /// <summary>
    /// Task 64: reject explicit continuous-recording markers before any
    /// source/window/display enumeration happens. This keeps the public API
    /// boundary frozen: WGC continuous recording remains an internal controlled
    /// experiment and is not a public request capability.
    /// </summary>
    private static void RejectUnsupportedContinuousFeatures(JsonNode cfg)
    {
        if (MatchesAny(cfg["capture_kind"], "continuous"))
            throw ContinuousUnsupported("capture_kind", "continuous");

        if (MatchesAny(cfg["recording_mode"], "continuous"))
            throw ContinuousUnsupported("recording_mode", "continuous");

        if (MatchesAny(cfg["capture_method"], "WGC_D3D11_FRAME_STREAM", "WGC_D3D11_WINDOW_FRAME_STREAM"))
            throw ContinuousUnsupported("capture_method", Str(cfg["capture_method"]) ?? "WGC_D3D11_FRAME_STREAM");

        if (MatchesAny(cfg["backend"], "wgc_continuous", "wgc-continuous"))
            throw ContinuousUnsupported("backend", Str(cfg["backend"]) ?? "wgc_continuous");
    }

    private static bool MatchesAny(JsonNode? node, params string[] values)
    {
        var s = Str(node);
        if (string.IsNullOrEmpty(s)) return false;
        foreach (var v in values)
            if (string.Equals(s, v, StringComparison.OrdinalIgnoreCase))
                return true;
        return false;
    }

    private static ApiException ContinuousUnsupported(string field, string value) =>
        new(400, "UNSUPPORTED_FEATURE",
            $"WGC continuous recording is not implemented. '{field}'='{value}' is not supported. " +
            "Current public API capabilities remain standard FFmpeg recording; WGC continuous is selected only through controlled internal experiment entry points.");

    /// <summary>
    /// Clamps window bounds to the virtual screen bounds so that FFmpeg gdigrab
    /// capture region never extends outside the capturable desktop area.
    /// </summary>
    private static SystemQuery.Bounds ClampWindowBoundsToVirtualScreen(SystemQuery.Bounds window)
    {
        var screen = SystemQuery.VirtualScreenBounds();

        int screenLeft = screen.x;
        int screenTop = screen.y;
        int screenRight = screen.x + screen.width;
        int screenBottom = screen.y + screen.height;

        int winLeft = window.x;
        int winTop = window.y;
        int winRight = window.x + window.width;
        int winBottom = window.y + window.height;

        int clampedLeft = Math.Max(winLeft, screenLeft);
        int clampedTop = Math.Max(winTop, screenTop);
        int clampedRight = Math.Min(winRight, screenRight);
        int clampedBottom = Math.Min(winBottom, screenBottom);

        int clampedW = clampedRight - clampedLeft;
        int clampedH = clampedBottom - clampedTop;

        return new SystemQuery.Bounds(clampedLeft, clampedTop, clampedW, clampedH);
    }

    /// <summary>
    /// Normalize dimension to even number (required by x264/yuv420p).
    /// </summary>
    private static int NormalizeDimension(int dim)
    {
        return (dim % 2 == 0) ? dim : dim - 1;
    }
}
