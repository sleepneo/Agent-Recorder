using System;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
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

    public static Recording Build(JsonNode cfg, string agent, out object summary)
    {
        RejectUnsupportedContinuousFeatures(cfg);

        // =====================================================================
        // Step 0: nested.role validation MUST come before source enumeration.
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
            }
            else
            {
                var d = SystemQuery.EnumDisplays().FirstOrDefault(x => x.id == did)
                        ?? throw new ApiException(404, "SOURCE_NOT_FOUND",
                            $"Display {did} not found", new { suggested_action = "list_displays" });
                rec.SourceType = "display";
                rec.SourceTitle = d.name;
                cap.SourceKind = "display";
                cap.Bounds = (d.bounds.x, d.bounds.y, d.bounds.width, d.bounds.height);
            }
        }
        else if (type == "window")
        {
            var wid = Str(src["window_id"]) ?? throw Inv("window_id required for window source");

            if (testMode)
            {
                rec.SourceType = "window";
                rec.SourceTitle = $"Test Window ({wid})";
                cap.SourceKind = "window";
                cap.WindowTitle = wid;
                cap.Bounds = (0, 0, 1280, 720);
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
                var d = SystemQuery.EnumDisplays().FirstOrDefault(x => x.id == did)
                        ?? throw new ApiException(404, "SOURCE_NOT_FOUND",
                            $"Display {did} not found", new { suggested_action = "list_displays" });

                // Check bounds are within display
                var db = d.bounds;
                if (bx < db.x || by < db.y
                    || bx + bw > db.x + db.width || by + bh > db.y + db.height)
                {
                    throw new ApiException(400, "INVALID_ARGUMENT",
                        $"Region bounds (x={bx},y={by},w={bw},h={bh}) exceeds display bounds (x={db.x},y={db.y},w={db.width},h={db.height})",
                        new { display_bounds = new { x = db.x, y = db.y, width = db.width, height = db.height } });
                }

                rec.SourceType = "region";
                rec.SourceTitle = $"region:{d.name}";
                cap.SourceKind = "region";
                cap.Bounds = (bx, by, normalizedBw, normalizedBh);
                cap.RegionNormalizedBounds = wasNormalized ? (normalizedBw, normalizedBh) : null;
            }
            else
            {
                // Test mode: use placeholder bounds without display validation
                rec.SourceType = "region";
                rec.SourceTitle = $"Test Region ({bx},{by},{bw},{bh})";
                cap.SourceKind = "region";
                cap.Bounds = (bx, by, normalizedBw, normalizedBh);
            }
        }
        else throw new ApiException(400, "UNSUPPORTED_FEATURE",
            $"source.type '{type}' not supported in MVP");

        var mic = cfg["audio"]?["microphone"];
        rec.Microphone = mic?["enabled"]?.GetValue<bool>() ?? false;
        cap.Microphone = rec.Microphone;
        cap.MicDevice = Str(mic?["device_id"]);

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

        rec.OutputPath = BuildOutputPath(cfg["output"], rec);
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
            audio = rec.Microphone ? "Microphone enabled" : "No audio",
            duration = rec.DurationSeconds is int s ? $"{s}s" : "Manual stop",
            output = rec.OutputPath,
            nested_role = rec.NestedRole ?? "none"
        };
        return rec;
    }

    private static string BuildOutputPath(JsonNode? output, Recording rec)
    {
        string dir = Str(output?["directory"]) is { } d && d != "default"
            ? d : Paths.DefaultOutputDir;
        PolicyEngine.ValidateDirectory(dir);
        Directory.CreateDirectory(dir);

        string name;
        if (Str(output?["filename"]) is { } fn)
            name = fn.EndsWith(".mp4") ? fn : fn + ".mp4";
        else
        {
            var tmpl = Str(output?["filename_template"]) ?? "recording-{datetime}";
            name = ApplyTemplate(tmpl, rec) + ".mp4";
        }

        var policy = Str(output?["conflict_policy"]) ?? "rename";
        var full = Path.Combine(dir, name);
        return ResolveConflict(full, policy);
    }

    private static string ApplyTemplate(string t, Recording rec)
    {
        var now = DateTime.Now;
        return t.Replace("{date}", now.ToString("yyyy-MM-dd"))
                .Replace("{time}", now.ToString("HHmmss"))
                .Replace("{datetime}", now.ToString("yyyy-MM-dd-HHmmss"))
                .Replace("{source}", Sanitize(rec.SourceTitle))
                .Replace("{id}", rec.Id);
    }

    private static string ResolveConflict(string full, string policy)
    {
        if (!File.Exists(full)) return full;
        switch (policy)
        {
            case "fail": throw new ApiException(409, "OUTPUT_PATH_INVALID", "Output file already exists");
            case "overwrite": throw new ApiException(403, "PERMISSION_DENIED", "Overwrite requires explicit confirmation");
            default:
                var dir = Path.GetDirectoryName(full)!;
                var stem = Path.GetFileNameWithoutExtension(full);
                var ext = Path.GetExtension(full);
                for (int i = 1; ; i++)
                {
                    var cand = Path.Combine(dir, $"{stem}-{i}{ext}");
                    if (!File.Exists(cand)) return cand;
                }
        }
    }

    private static string Sanitize(string s) =>
        new string(s.Where(c => !Path.GetInvalidFileNameChars().Contains(c)).ToArray());
    private static string? Str(JsonNode? n) => n?.GetValue<string>();
    private static ApiException Inv(string m) => new(400, "INVALID_ARGUMENT", m);

    /// <summary>
    /// Task 64: reject explicit continuous-recording markers before any
    /// source/window/display enumeration happens. This keeps the public API
    /// boundary frozen: WGC continuous recording is not implemented yet.
    /// </summary>
    private static void RejectUnsupportedContinuousFeatures(JsonNode cfg)
    {
        if (MatchesAny(cfg["capture_kind"], "continuous"))
            throw ContinuousUnsupported("capture_kind", "continuous");

        if (MatchesAny(cfg["recording_mode"], "continuous"))
            throw ContinuousUnsupported("recording_mode", "continuous");

        if (MatchesAny(cfg["capture_method"], "WGC_D3D11_FRAME_STREAM"))
            throw ContinuousUnsupported("capture_method", "WGC_D3D11_FRAME_STREAM");

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
            "Current supported capabilities: standard FFmpeg recording and WGC still-frame PNG when enabled via feature flag.");

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
