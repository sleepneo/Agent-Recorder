using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using AgentRecorder.Capture;
using AgentRecorder.Core;
using AgentRecorder.Infrastructure;
using AgentRecorder.Logging;
using AgentRecorder.Windows;
using ApiException = AgentRecorder.Infrastructure.ApiException;

namespace AgentRecorder.Api;

public sealed class ApiServer
{
    public const int Port = 37891;
    private const string Prefix = "/api/v1";
    private static readonly string ProductVersion = ResolveProductVersion();

    private readonly TcpListener _listener = new(IPAddress.Loopback, Port);
    private readonly RecordingEngine _engine;
    private readonly AuditLogger _audit;
    private readonly ITrayContext _tray;
    private readonly RuntimeReadiness? _readiness;
    private readonly WindowsAutoStartManager? _autoStart;
    private readonly FfmpegPrewarmer? _ffmpegPrewarmer;
    private CancellationTokenSource _cts = new();

    private SelectedRegionState? _lastSelectedRegion;
    private readonly object _regionLock = new();

    public ApiServer(RecordingEngine engine, AuditLogger audit, ITrayContext tray,
        RuntimeReadiness? readiness = null,
        WindowsAutoStartManager? autoStart = null,
        FfmpegPrewarmer? ffmpegPrewarmer = null)
    {
        _engine = engine; _audit = audit; _tray = tray;
        _readiness = readiness;
        _autoStart = autoStart;
        _ffmpegPrewarmer = ffmpegPrewarmer;
        _lastSelectedRegion = RegionSelectionStateStore.Load();
    }

    public void Start()
    {
        _listener.Start();
        _ = Task.Run(() => Loop(_cts.Token));
    }

    public void Stop()
    {
        _cts.Cancel();
        try { _listener.Stop(); } catch { }
    }

    private async Task Loop(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            TcpClient client;
            try { client = await _listener.AcceptTcpClientAsync(ct); }
            catch { break; }
            _ = Task.Run(() => HandleClient(client), ct);
        }
    }

    private async Task HandleClient(TcpClient client)
    {
        var reqId = "req_" + Guid.NewGuid().ToString("N")[..12];
        try
        {
            var stream = client.GetStream();
            stream.ReadTimeout = 5000;
            stream.WriteTimeout = 5000;

            try
            {
                var request = await ReadRequest(stream);
                if (request == null)
                {
                    await WriteJson(stream, 400, ApiResponse.Err("BAD_REQUEST", "Malformed HTTP request", null, reqId));
                    return;
                }

                var body = request.Body;
                var method = request.Method;
                var path = request.Path;

                if (RequiresAuth(method, path))
                {
                    ApiKeyAuth.ValidateHeader(request.Headers.GetValueOrDefault("x-agent-recorder-key"));
                }

                var responseBody = Route(method, path, request, body, reqId, out int status);
                await WriteJson(stream, status, responseBody);
            }
            catch (ApiException ex)
            {
                await WriteJson(stream, ex.Status, ApiResponse.Err(ex.Code, ex.Message, ex.Details, reqId));
            }
            catch (Exception ex)
            {
                await WriteJson(stream, 500, ApiResponse.Err("INTERNAL_ERROR", ex.Message, null, reqId));
            }
        }
        finally
        {
            try { client.Dispose(); } catch { }
        }
    }

    private static async Task WriteJson(Stream stream, int status, string body)
    {
        var buf = Encoding.UTF8.GetBytes(body);
        var headers = $"HTTP/1.1 {status} {StatusText(status)}\r\nContent-Type: application/json; charset=utf-8\r\nContent-Length: {buf.Length}\r\nConnection: close\r\n\r\n";
        var responseBytes = Encoding.UTF8.GetBytes(headers);
        await stream.WriteAsync(responseBytes);
        await stream.WriteAsync(buf);
        try { stream.Flush(); } catch { }
    }

    private static string StatusText(int status) => status switch
    {
        200 => "OK",
        400 => "Bad Request",
        401 => "Unauthorized",
        403 => "Forbidden",
        404 => "Not Found",
        405 => "Method Not Allowed",
        500 => "Internal Server Error",
        _ => "Unknown"
    };

    private static async Task<HttpRequest?> ReadRequest(Stream stream)
    {
        using var ms = new MemoryStream();
        var buffer = new byte[8192];
        int headerEnd = -1;

        while (true)
        {
            int read;
            try { read = await stream.ReadAsync(buffer.AsMemory(0, buffer.Length)); }
            catch { return null; }
            if (read == 0) return null;

            ms.Write(buffer, 0, read);
            var bytes = ms.ToArray();
            headerEnd = FindHeaderEnd(bytes);
            if (headerEnd >= 0) break;
            if (bytes.Length > 65536) return null; // too large
        }

        var headerBytes = ms.ToArray();
        var headerText = Encoding.UTF8.GetString(headerBytes, 0, headerEnd);
        var lines = headerText.Split("\r\n");
        if (lines.Length < 1) return null;

        var requestLine = lines[0].Split(' ');
        if (requestLine.Length < 2) return null;
        var method = requestLine[0].ToUpperInvariant();
        var rawPath = requestLine[1];

        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (int i = 1; i < lines.Length; i++)
        {
            var line = lines[i];
            var idx = line.IndexOf(':');
            if (idx <= 0) continue;
            var name = line[..idx].Trim();
            var value = line[(idx + 1)..].Trim();
            headers[name] = value;
        }

        int contentLength = 0;
        if (headers.TryGetValue("Content-Length", out var clValue) && int.TryParse(clValue, out var parsed))
            contentLength = parsed;

        byte[] bodyBytes = Array.Empty<byte>();
        var bodyStart = headerEnd;
        var alreadyRead = headerBytes.Length - bodyStart;
        var remaining = contentLength - alreadyRead;
        if (remaining < 0) remaining = 0;

        if (contentLength > 0)
        {
            bodyBytes = new byte[contentLength];
            Array.Copy(headerBytes, bodyStart, bodyBytes, 0, alreadyRead);
            var offset = alreadyRead;
            while (remaining > 0)
            {
                int r;
                try { r = await stream.ReadAsync(bodyBytes.AsMemory(offset, remaining)); }
                catch { break; }
                if (r == 0) break;
                offset += r;
                remaining -= r;
            }
        }

        var body = StripBom(Encoding.UTF8.GetString(bodyBytes));
        return new HttpRequest(method, rawPath, headers, body);
    }

    private static string StripBom(string s)
    {
        if (s.Length > 0 && s[0] == '\uFEFF')
            return s[1..];
        return s;
    }

    private static int FindHeaderEnd(byte[] bytes)
    {
        for (int i = 0; i < bytes.Length - 3; i++)
        {
            if (bytes[i] == '\r' && bytes[i + 1] == '\n' && bytes[i + 2] == '\r' && bytes[i + 3] == '\n')
                return i + 4;
        }
        return -1;
    }

    private static bool RequiresAuth(string method, string path)
    {
        if (method == "POST" || method == "PUT" || method == "DELETE")
            return true;

        var sensitivePaths = new[] { "/api/v1/recordings", "/api/v1/confirmations" };
        return sensitivePaths.Any(p => path.StartsWith(p));
    }

    private string Route(string method, string path, HttpRequest req,
                         string reqBody, string reqId, out int status)
    {
        status = 200;
        if (!path.StartsWith(Prefix))
            throw new ApiException(404, "RECORDING_NOT_FOUND", "Unknown endpoint");
        var sub = path[Prefix.Length..];

        switch (method, sub)
        {
            case ("GET", "/capabilities"):
                return ApiResponse.Ok(Capabilities(), reqId);

            case ("GET", "/permissions"):
                return ApiResponse.Ok(Permissions(), reqId);

            case ("GET", "/displays"):
                return ApiResponse.Ok(new { displays = SystemQuery.EnumDisplays() }, reqId);

            case ("GET", "/windows"):
                bool incMin = req.Query.GetValueOrDefault("include_minimized") == "true";
                bool incSys = req.Query.GetValueOrDefault("include_system_windows") == "true";
                return ApiResponse.Ok(new { windows = SystemQuery.EnumWindows(incMin, incSys) }, reqId);

            case ("GET", "/windows/active"):
                return ApiResponse.Ok(new { window = SystemQuery.ActiveWindow() }, reqId);

            case ("GET", "/audio/devices"):
                return ApiResponse.Ok(new
                {
                    input_devices = SystemQuery.AudioInputs(),
                    system_audio_supported = false
                }, reqId);

            case ("POST", "/recordings"):
                return CreateRecording(req, reqBody, reqId);

            case ("POST", "/recordings/quick"):
                return CreateQuickRecording(req, reqBody, reqId);

            case ("POST", "/region-selections"):
                return CreateRegionSelection(req, reqBody, reqId);

            case ("GET", "/recordings"):
                return ApiResponse.Ok(new { recordings = _engine.List() }, reqId);
        }

        var seg = sub.Trim('/').Split('/');

        if (seg.Length >= 2 && seg[0] == "confirmations" && method == "GET")
        {
            var confId = seg[1];
            // Long-polling: wait_ms + since_status
            var waitMs = ParseWaitMs(req.Query.GetValueOrDefault("wait_ms"));
            var sinceStatus = req.Query.GetValueOrDefault("since_status");
            if (waitMs > 0 && !string.IsNullOrEmpty(sinceStatus))
                return ApiResponse.Ok(_engine.GetConfirmationWait(confId, sinceStatus, waitMs), reqId);
            return ApiResponse.Ok(_engine.GetConfirmation(confId), reqId);
        }

        if (seg.Length >= 3 && seg[0] == "confirmations" && method == "POST"
            && (seg[2] == "approve" || seg[2] == "reject"))
        {
            throw new ApiException(405, "METHOD_NOT_ALLOWED",
                "Recording confirmation cannot be approved or rejected via HTTP API. " +
                "A local user must interact with the system tray menu or the confirmation pop-up instead.",
                new { suggested_action = "click_tray_confirmation_or_popup" });
        }

        if (seg.Length >= 2 && seg[0] == "recordings")
        {
            var id = seg[1];
            if (seg.Length == 2 && method == "GET")
            {
                // Long-polling: wait_ms + since_status
                var waitMs = ParseWaitMs(req.Query.GetValueOrDefault("wait_ms"));
                var sinceStatus = req.Query.GetValueOrDefault("since_status");
                if (waitMs > 0 && !string.IsNullOrEmpty(sinceStatus))
                    return ApiResponse.Ok(_engine.GetStatusWait(id, sinceStatus, waitMs), reqId);
                return ApiResponse.Ok(_engine.GetStatus(id), reqId);
            }
            if (seg.Length == 3 && method == "POST" && seg[2] == "stop")
                return ApiResponse.Ok(_engine.Stop(id, ReasonFrom(reqBody)), reqId);
            if (seg.Length == 3 && method == "GET" && seg[2] == "output")
                return ApiResponse.Ok(_engine.GetOutput(id), reqId);
        }

        throw new ApiException(404, "RECORDING_NOT_FOUND", "Unknown endpoint: " + sub);
    }

    private string CreateRecording(HttpRequest req, string reqBody, string reqId)
    {
        var agent = req.Headers.GetValueOrDefault("X-Agent-Name") ?? "unknown";
        JsonNode cfg = JsonNode.Parse(string.IsNullOrWhiteSpace(reqBody) ? "{}" : reqBody)
                       ?? throw new ApiException(400, "INVALID_ARGUMENT", "Body required");

        var result = _engine.CreateRecording(cfg, agent, _tray);
        return ApiResponse.Ok(result, reqId);
    }

    private string CreateRegionSelection(HttpRequest req, string reqBody, string reqId)
    {
        JsonNode body = JsonNode.Parse(string.IsNullOrWhiteSpace(reqBody) ? "{}" : reqBody)
                        ?? throw new ApiException(400, "INVALID_ARGUMENT", "Body required");

        var purpose = body["purpose"]?.GetValue<string>() ?? "recording";
        if (purpose != "recording")
            throw new ApiException(400, "INVALID_ARGUMENT", $"purpose '{purpose}' not supported");

        var timeoutSeconds = body["timeout_seconds"]?.GetValue<int?>() ?? 120;
        if (timeoutSeconds < 10 || timeoutSeconds > 600)
            throw new ApiException(400, "INVALID_ARGUMENT",
                "timeout_seconds must be between 10 and 600");

        // 使用 TaskCompletionSource 等待 UI 线程回调
        var tcs = new TaskCompletionSource<(string status, int x, int y, int w, int h, string displayId, string coordSpace)>();

        _tray.RequestRegionSelection(timeoutSeconds, (status, x, y, w, h, displayId, coordSpace) =>
        {
            tcs.TrySetResult((status, x, y, w, h, displayId, coordSpace));
        });

        // 等待结果（带整体超时保护）
        var timeoutTask = Task.Delay((timeoutSeconds + 10) * 1000);
        var completed = Task.WaitAny(tcs.Task, timeoutTask);

        if (completed == 1)
            throw new ApiException(504, "SELECTION_TIMEOUT", "Region selection timed out");

        var result = tcs.Task.Result;

        if (result.status == "selected")
        {
            var state = new SelectedRegionState(
                Available: true,
                DisplayId: result.displayId,
                CoordinateSpace: result.coordSpace,
                X: result.x,
                Y: result.y,
                Width: result.w,
                Height: result.h,
                UpdatedAt: DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ"),
                Source: "region_selection");

            RegionSelectionStateStore.Save(state);
            lock (_regionLock) { _lastSelectedRegion = state; }
        }

        object response = result.status switch
        {
            "selected" => new
            {
                status = "selected",
                display_id = result.displayId,
                coordinate_space = result.coordSpace,
                bounds = new { x = result.x, y = result.y, width = result.w, height = result.h }
            },
            "selection_cancelled" => new
            {
                status = "selection_cancelled",
                reason = "user_cancelled"
            },
            "selection_timeout" => new
            {
                status = "selection_timeout",
                reason = "timeout"
            },
            "display_unavailable" => new
            {
                status = "display_unavailable",
                reason = "no_displays_enumerated",
                detail = "API host could not enumerate displays in its current session"
            },
            _ => new
            {
                status = "selection_failed",
                reason = "unknown_error"
            }
        };

        return ApiResponse.Ok(response, reqId);
    }

    private string CreateQuickRecording(HttpRequest req, string reqBody, string reqId)
    {
        var agent = req.Headers.GetValueOrDefault("X-Agent-Name") ?? "unknown";
        JsonNode body = JsonNode.Parse(string.IsNullOrWhiteSpace(reqBody) ? "{}" : reqBody)
                        ?? throw new ApiException(400, "INVALID_ARGUMENT", "Body required");

        var targetNode = body["target"];
        if (targetNode == null)
            throw new ApiException(400, "INVALID_ARGUMENT", "target is required");

        var targetType = targetNode["type"]?.GetValue<string>();
        if (string.IsNullOrWhiteSpace(targetType))
            throw new ApiException(400, "INVALID_ARGUMENT", "target.type is required");

        JsonObject cfg = BuildQuickRecordingConfig(body);

        switch (targetType)
        {
            case "primary_display":
                {
                    var display = ResolvePrimaryDisplay();
                    cfg["source"] = new JsonObject
                    {
                        ["type"] = "display",
                        ["display_id"] = display.id
                    };
                    var result = _engine.CreateRecording(cfg, agent, _tray);
                    var resolved = new JsonObject
                    {
                        ["type"] = "display",
                        ["display_id"] = display.id
                    };
                    var data = AddQuickMetadataToObject(result, "primary_display", resolved, true);
                    return ApiResponse.Ok(data, reqId);
                }

            case "active_window":
                {
                    var window = ResolveActiveWindow();
                    cfg["source"] = new JsonObject
                    {
                        ["type"] = "window",
                        ["window_id"] = window.id
                    };
                    // Pre-build to get the clamped capture bounds for the response
                    var preBuilt = ConfigParser.Build(cfg, agent, out _);
                    var capBounds = preBuilt.Config.Bounds;
                    var result = _engine.CreateRecording(cfg, agent, _tray);
                    var resolved = new JsonObject
                    {
                        ["type"] = "window",
                        ["window_id"] = window.id,
                        ["title"] = window.title,
                        ["bounds"] = new JsonObject
                        {
                            ["x"] = window.bounds.x,
                            ["y"] = window.bounds.y,
                            ["width"] = window.bounds.width,
                            ["height"] = window.bounds.height
                        },
                        ["capture_bounds"] = new JsonObject
                        {
                            ["x"] = capBounds.x,
                            ["y"] = capBounds.y,
                            ["width"] = capBounds.w,
                            ["height"] = capBounds.h
                        }
                    };
                    var data = AddQuickMetadataToObject(result, "active_window", resolved, true);
                    return ApiResponse.Ok(data, reqId);
                }

            case "selected_region":
                {
                    var timeoutSec = targetNode["selection_timeout_seconds"]?.GetValue<int?>() ?? 120;
                    if (timeoutSec < 10 || timeoutSec > 600)
                        throw new ApiException(400, "INVALID_ARGUMENT",
                            "target.selection_timeout_seconds must be between 10 and 600");

                    var sel = WaitForRegionSelection(timeoutSec);

                    if (sel.status != "selected")
                    {
                        return ApiResponse.Ok(new
                        {
                            status = sel.status,
                            quick = new
                            {
                                target_type = "selected_region",
                                recording_created = false
                            }
                        }, reqId);
                    }

                    cfg["source"] = new JsonObject
                    {
                        ["type"] = "region",
                        ["display_id"] = sel.displayId,
                        ["coordinate_space"] = sel.coordSpace,
                        ["bounds"] = new JsonObject
                        {
                            ["x"] = sel.x,
                            ["y"] = sel.y,
                            ["width"] = sel.w,
                            ["height"] = sel.h
                        }
                    };

                    var state = new SelectedRegionState(
                        Available: true,
                        DisplayId: sel.displayId,
                        CoordinateSpace: sel.coordSpace,
                        X: sel.x,
                        Y: sel.y,
                        Width: sel.w,
                        Height: sel.h,
                        UpdatedAt: DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ"),
                        Source: "quick_selected_region");

                    RegionSelectionStateStore.Save(state);
                    lock (_regionLock) { _lastSelectedRegion = state; }

                    var result = _engine.CreateRecording(cfg, agent, _tray);
                    var resolved = new JsonObject
                    {
                        ["type"] = "region",
                        ["display_id"] = sel.displayId,
                        ["coordinate_space"] = sel.coordSpace,
                        ["bounds"] = new JsonObject
                        {
                            ["x"] = sel.x,
                            ["y"] = sel.y,
                            ["width"] = sel.w,
                            ["height"] = sel.h
                        }
                    };
                    var data = AddQuickMetadataToObject(result, "selected_region", resolved, true);
                    return ApiResponse.Ok(data, reqId);
                }

            case "last_region":
                {
                    SelectedRegionState? last;
                    lock (_regionLock) { last = _lastSelectedRegion; }

                    if (last == null)
                    {
                        throw new ApiException(404, "SOURCE_NOT_FOUND",
                            "No last selected region is available.",
                            new { suggested_action = "use_selected_region_first" });
                    }

                    cfg["source"] = new JsonObject
                    {
                        ["type"] = "region",
                        ["display_id"] = last.DisplayId,
                        ["coordinate_space"] = last.CoordinateSpace,
                        ["bounds"] = new JsonObject
                        {
                            ["x"] = last.X,
                            ["y"] = last.Y,
                            ["width"] = last.Width,
                            ["height"] = last.Height
                        }
                    };

                    var result = _engine.CreateRecording(cfg, agent, _tray);
                    var resolved = new JsonObject
                    {
                        ["type"] = "region",
                        ["display_id"] = last.DisplayId,
                        ["coordinate_space"] = last.CoordinateSpace,
                        ["bounds"] = new JsonObject
                        {
                            ["x"] = last.X,
                            ["y"] = last.Y,
                            ["width"] = last.Width,
                            ["height"] = last.Height
                        },
                        ["source"] = "last_selected_region"
                    };
                    var data = AddQuickMetadataToObject(result, "last_region", resolved, true);
                    return ApiResponse.Ok(data, reqId);
                }

            default:
                throw new ApiException(400, "INVALID_ARGUMENT",
                    $"target.type '{targetType}' is not supported. Supported: primary_display, active_window, selected_region, last_region");
        }
    }

    private static JsonObject BuildQuickRecordingConfig(JsonNode body)
    {
        var cfg = new JsonObject();

        var videoNode = body["video"];
        if (videoNode != null)
            cfg["video"] = videoNode.DeepClone();

        var audioNode = body["audio"];
        if (audioNode != null)
            cfg["audio"] = audioNode.DeepClone();

        var outputNode = body["output"];
        if (outputNode != null)
            cfg["output"] = outputNode.DeepClone();

        var nestedNode = body["nested"];
        if (nestedNode != null)
            cfg["nested"] = nestedNode.DeepClone();

        var durationSec = body["duration_seconds"]?.GetValue<int?>();
        if (durationSec.HasValue)
        {
            cfg["stop_condition"] = new JsonObject
            {
                ["type"] = "duration",
                ["seconds"] = durationSec.Value
            };
        }

        return cfg;
    }

    private static SystemQuery.DisplayInfo ResolvePrimaryDisplay()
    {
        var displays = SystemQuery.EnumDisplays();
        if (displays.Count == 0)
            throw new ApiException(400, "SOURCE_NOT_FOUND",
                "No display is available for quick recording.",
                new { suggested_action = "use_selected_region_or_check_desktop_session" });

        var primary = displays.FirstOrDefault(d => d.is_primary) ?? displays[0];
        return primary;
    }

    private static SystemQuery.WindowInfo ResolveActiveWindow()
    {
        var window = SystemQuery.ActiveWindow();
        if (window == null)
            throw new ApiException(400, "SOURCE_NOT_FOUND",
                "No active recordable window is available.",
                new { suggested_action = "ask_user_to_focus_a_window_or_use_selected_region" });
        return window;
    }

    private (string status, int x, int y, int w, int h, string displayId, string coordSpace) WaitForRegionSelection(int timeoutSeconds)
    {
        var tcs = new TaskCompletionSource<(string status, int x, int y, int w, int h, string displayId, string coordSpace)>();

        _tray.RequestRegionSelection(timeoutSeconds, (status, x, y, w, h, displayId, coordSpace) =>
        {
            tcs.TrySetResult((status, x, y, w, h, displayId, coordSpace));
        });

        var timeoutTask = Task.Delay((timeoutSeconds + 10) * 1000);
        var completed = Task.WaitAny(tcs.Task, timeoutTask);

        if (completed == 1)
            return ("selection_timeout", 0, 0, 0, 0, "", "virtual_screen");

        return tcs.Task.Result;
    }

    private static JsonObject AddQuickMetadataToObject(object createResult, string targetType, JsonObject resolvedSource, bool requiresConfirmation)
    {
        var resultJson = JsonSerializer.Serialize(createResult, new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower
        });
        var node = JsonNode.Parse(resultJson) as JsonObject ?? new JsonObject();
        node["quick"] = new JsonObject
        {
            ["target_type"] = targetType,
            ["recording_created"] = true,
            ["resolved_source"] = resolvedSource,
            ["requires_user_confirmation"] = requiresConfirmation
        };
        return node;
    }

    private static string ReasonFrom(string body)
    {
        try { return JsonNode.Parse(body)?["reason"]?.GetValue<string>() ?? "user_requested"; }
        catch { return "user_requested"; }
    }

    private static int ParseWaitMs(string? value)
    {
        if (string.IsNullOrEmpty(value)) return 0;
        if (int.TryParse(value, out var ms) && ms > 0)
            return Math.Min(ms, 25000);
        return 0;
    }

    private static string PrewarmStatusToString(PrewarmStatus status) => status switch
    {
        PrewarmStatus.NotStarted => "not_started",
        PrewarmStatus.Running => "running",
        PrewarmStatus.Completed => "completed",
        PrewarmStatus.Failed => "failed",
        PrewarmStatus.Skipped => "skipped",
        _ => "unknown"
    };

    private object Capabilities()
    {
        var autoStartInfo = _autoStart?.GetStatus();
        var ffmpegPrewarm = _ffmpegPrewarmer?.CurrentResult;
        string? ffmpegSource = null;
        bool ffmpegResolved = false;
        try
        {
            ffmpegSource = FfmpegLocator.Source;
            ffmpegResolved = !string.IsNullOrEmpty(ffmpegSource)
                && File.Exists(FfmpegLocator.FfmpegPath)
                && File.Exists(FfmpegLocator.FfprobePath);
        }
        catch { }

        var displaysContext = BuildDisplaysContext();
        var windowsContext = BuildWindowsContext();
        var hasPrimaryDisplay = displaysContext.Available && displaysContext.PrimaryDisplayId != null;
        var hasActiveWindow = windowsContext.Active != null;
        var supportsRegionSelection = _tray.SupportsRegionSelectionUi;

        SelectedRegionState? lastRegion;
        lock (_regionLock) { lastRegion = _lastSelectedRegion; }
        bool hasLastRegion = lastRegion != null;

        return new
        {
            app = new { name = "Agent Recorder", version = ProductVersion, platform = "windows" },
            host = new
            {
                mode = _tray.HostMode,
                supports_region_selection_ui = _tray.SupportsRegionSelectionUi,
                region_selection_blocker = _tray.SupportsRegionSelectionUi ? null : "headless_host",
                autostart = new
                {
                    supported = true,
                    enabled = autoStartInfo?.Enabled ?? false,
                    matches_current_app = autoStartInfo?.MatchesCurrentApp ?? false,
                    value_name = autoStartInfo?.ValueName ?? WindowsAutoStartManager.DefaultValueName
                }
            },
            ffmpeg = new
            {
                resolved = ffmpegResolved,
                source = ffmpegSource,
                prewarm = new
                {
                    status = ffmpegPrewarm != null ? PrewarmStatusToString(ffmpegPrewarm.Status) : "not_started",
                    elapsed_ms = ffmpegPrewarm?.ElapsedMs > 0 ? ffmpegPrewarm.ElapsedMs : (long?)null
                }
            },
            recording = new
            {
                sources = new[] { "display", "window", "region" },
                audio = new[] { "microphone" },
                containers = new[] { "mp4" },
                codecs = new[] { "h264" },
                fps = new[] { 15, 24, 30, 60 },
                stop_conditions = new[] { "duration", "manual" },
                max_duration_seconds = 7200,
                max_concurrent_recordings = 2,
                default_concurrency_policy = "single_unless_explicit_nested",
                pause_resume = false,
                nested_recording_mvp = new
                {
                    supported = true,
                    max_concurrent = 2,
                    roles = new[] { "outer", "inner" }
                }
            },
            interaction = new
            {
                region_selection_endpoint = true,
                region_selection_requires_local_user = true,
                region_selection_may_block_in_headless = !_tray.SupportsRegionSelectionUi,
                quick_recording_endpoint = "/api/v1/recordings/quick",
                quick_recording_supported = true,
                stop_controls = new
                {
                    floating_button = _tray.SupportsFloatingStopButton,
                    tray_stop = _tray.SupportsTrayStop,
                    global_hotkey = new
                    {
                        supported = _tray.SupportsGlobalStopHotkey,
                        registered = _tray.IsGlobalStopHotkeyRegistered,
                        gesture = _tray.GlobalStopHotkeyGesture,
                        behavior = "stop_all_active_recordings"
                    }
                },
                quick_recipes = new[]
                {
                    new
                    {
                        name = "record_primary_display",
                        target_type = "primary_display",
                        description = "Record the primary display with local confirmation.",
                        endpoint = "/api/v1/recordings/quick",
                        method = "POST",
                        request_template = new { target = new { type = "primary_display" }, duration_seconds = 60 },
                        available = hasPrimaryDisplay,
                        unavailable_reason = hasPrimaryDisplay ? null : "no_primary_display"
                    },
                    new
                    {
                        name = "record_active_window",
                        target_type = "active_window",
                        description = "Record the current active window with local confirmation.",
                        endpoint = "/api/v1/recordings/quick",
                        method = "POST",
                        request_template = new { target = new { type = "active_window" }, duration_seconds = 60 },
                        available = hasActiveWindow,
                        unavailable_reason = hasActiveWindow ? null : "no_active_window"
                    },
                    new
                    {
                        name = "record_selected_region",
                        target_type = "selected_region",
                        description = "Ask the local user to select a region, then create a recording with local confirmation.",
                        endpoint = "/api/v1/recordings/quick",
                        method = "POST",
                        request_template = new { target = new { type = "selected_region" }, duration_seconds = 60 },
                        available = supportsRegionSelection,
                        unavailable_reason = supportsRegionSelection ? null : "headless_host"
                    },
                    new
                    {
                        name = "record_last_region",
                        target_type = "last_region",
                        description = "Record the last selected region with local confirmation.",
                        endpoint = "/api/v1/recordings/quick",
                        method = "POST",
                        request_template = new { target = new { type = "last_region" }, duration_seconds = 60 },
                        available = hasLastRegion,
                        unavailable_reason = hasLastRegion ? null : "no_last_selected_region"
                    }
                }
            },
            safety = new { requires_confirmation = true, recording_indicator = true, audit_log = true },
            auth = new { required = true, header = "X-Agent-Recorder-Key" },
            readiness = _readiness?.ToCapabilitiesObject(),
            context = new
            {
                snapshot_at = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ"),
                displays = new
                {
                    available = displaysContext.Available,
                    count = displaysContext.Count,
                    primary_display_id = displaysContext.PrimaryDisplayId,
                    virtual_bounds = displaysContext.VirtualBounds,
                    items = displaysContext.Items,
                    error = displaysContext.Error
                },
                windows = new
                {
                    available = windowsContext.Available,
                    active = windowsContext.Active,
                    visible_count = windowsContext.VisibleCount,
                    items_sample = windowsContext.ItemsSample,
                    sample_limit = 10,
                    error = windowsContext.Error
                },
                last_selected_region = lastRegion == null ? null : LastRegionToCapabilitiesObject(lastRegion)
            }
        };
    }

    private static string ResolveProductVersion()
    {
        var informationalVersion = typeof(ApiServer).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion;

        if (!string.IsNullOrWhiteSpace(informationalVersion))
            return informationalVersion.Split('+', 2)[0];

        return typeof(ApiServer).Assembly.GetName().Version?.ToString(3) ?? "unknown";
    }

    private static object LastRegionToCapabilitiesObject(SelectedRegionState state) => new
    {
        available = true,
        display_id = state.DisplayId,
        coordinate_space = state.CoordinateSpace,
        bounds = new
        {
            x = state.X,
            y = state.Y,
            width = state.Width,
            height = state.Height
        },
        updated_at = state.UpdatedAt,
        source = state.Source
    };

    private (bool Available, int Count, string? PrimaryDisplayId, object? VirtualBounds, object[] Items, string? Error) BuildDisplaysContext()
    {
        try
        {
            var displays = SystemQuery.EnumDisplays();
            var virtualBounds = SystemQuery.VirtualScreenBounds();

            var items = displays.Select(d => new
            {
                id = d.id,
                name = d.name,
                is_primary = d.is_primary,
                bounds = new { x = d.bounds.x, y = d.bounds.y, width = d.bounds.width, height = d.bounds.height },
                scale_factor = d.scale_factor
            }).ToArray();

            return (
                Available: displays.Count > 0,
                Count: displays.Count,
                PrimaryDisplayId: displays.FirstOrDefault(d => d.is_primary)?.id,
                VirtualBounds: new { x = virtualBounds.x, y = virtualBounds.y, width = virtualBounds.width, height = virtualBounds.height },
                Items: items,
                Error: null
            );
        }
        catch (Exception ex)
        {
            return (
                Available: false,
                Count: 0,
                PrimaryDisplayId: null,
                VirtualBounds: null,
                Items: Array.Empty<object>(),
                Error: ex.Message
            );
        }
    }

    private (bool Available, object? Active, int VisibleCount, object[] ItemsSample, string? Error) BuildWindowsContext()
    {
        SystemQuery.WindowInfo? activeWindow = null;
        string? activeError = null;
        try
        {
            activeWindow = SystemQuery.ActiveWindow();
        }
        catch (Exception ex)
        {
            activeError = "Failed to query active window: " + ex.Message;
        }

        List<SystemQuery.WindowInfo> windows = new();
        string? enumError = null;
        try
        {
            windows = SystemQuery.EnumWindows(includeMinimized: false, includeSystem: false);
        }
        catch (Exception ex)
        {
            enumError = "Failed to enumerate windows: " + ex.Message;
        }

        object? activeObj = null;
        if (activeWindow != null)
        {
            activeObj = new
            {
                id = activeWindow.id,
                title = activeWindow.title,
                app_name = activeWindow.app_name,
                process_id = activeWindow.process_id,
                is_minimized = activeWindow.is_minimized,
                bounds = new { x = activeWindow.bounds.x, y = activeWindow.bounds.y, width = activeWindow.bounds.width, height = activeWindow.bounds.height }
            };
        }

        List<object> sample = new();
        if (activeWindow != null)
        {
            sample.Add(new
            {
                id = activeWindow.id,
                title = activeWindow.title,
                app_name = activeWindow.app_name,
                process_id = activeWindow.process_id,
                is_active = true,
                is_minimized = activeWindow.is_minimized,
                bounds = new { x = activeWindow.bounds.x, y = activeWindow.bounds.y, width = activeWindow.bounds.width, height = activeWindow.bounds.height }
            });
        }

        var activeId = activeWindow?.id;
        int remaining = 10 - sample.Count;
        if (remaining > 0 && windows.Count > 0)
        {
            sample.AddRange(windows
                .Where(w => w.id != activeId)
                .Take(remaining)
                .Select(w => new
                {
                    id = w.id,
                    title = w.title,
                    app_name = w.app_name,
                    process_id = w.process_id,
                    is_active = w.is_active,
                    is_minimized = w.is_minimized,
                    bounds = new { x = w.bounds.x, y = w.bounds.y, width = w.bounds.width, height = w.bounds.height }
                }));
        }

        string? combinedError = null;
        if (activeError != null || enumError != null)
        {
            var parts = new List<string>();
            if (activeError != null) parts.Add(activeError);
            if (enumError != null) parts.Add(enumError);
            combinedError = string.Join("; ", parts);
        }

        bool available = activeWindow != null || windows.Count > 0;

        return (
            Available: available,
            Active: activeObj,
            VisibleCount: windows.Count,
            ItemsSample: sample.ToArray(),
            Error: combinedError
        );
    }

    private static object Permissions() => new
    {
        screen_capture = new { status = "granted" },
        microphone = new { status = "granted" },
        output_directory = new { status = "granted", default_path = Paths.DefaultOutputDir, selection_ui = true }
    };
}

internal sealed class HttpRequest
{
    public string Method { get; }
    public string Path { get; }
    public Dictionary<string, string> Query { get; }
    public Dictionary<string, string> Headers { get; }
    public string Body { get; }

    public HttpRequest(string method, string rawPath, Dictionary<string, string> headers, string body)
    {
        Method = method;
        Headers = headers;
        Body = body;

        var qidx = rawPath.IndexOf('?');
        if (qidx >= 0)
        {
            Path = rawPath[..qidx];
            Query = ParseQuery(rawPath[(qidx + 1)..]);
        }
        else
        {
            Path = rawPath;
            Query = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }
    }

    private static Dictionary<string, string> ParseQuery(string query)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var part in query.Split('&'))
        {
            var eq = part.IndexOf('=');
            if (eq < 0)
            {
                result[Uri.UnescapeDataString(part)] = "";
            }
            else
            {
                result[Uri.UnescapeDataString(part[..eq])] = Uri.UnescapeDataString(part[(eq + 1)..]);
            }
        }
        return result;
    }
}
