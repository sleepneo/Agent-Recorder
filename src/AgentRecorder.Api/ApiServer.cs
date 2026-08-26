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
    private readonly IPerformanceTracer _tracer;
    private readonly IEnsureContextStore? _ensureContextStore;
    private readonly RuntimeReadiness? _readiness;
    private readonly WindowsAutoStartManager? _autoStart;
    private readonly FfmpegPrewarmer? _ffmpegPrewarmer;
    private readonly IPerformanceSummaryProvider _performanceSummaryProvider;
    private CancellationTokenSource _cts = new();

    private SelectedRegionState? _lastSelectedRegion;
    private readonly object _regionLock = new();

    public const string EnsureContextHeaderName = EnsureContextStore.HeaderName;

    public ApiServer(RecordingEngine engine, AuditLogger audit, ITrayContext tray,
        RuntimeReadiness? readiness = null,
        WindowsAutoStartManager? autoStart = null,
        FfmpegPrewarmer? ffmpegPrewarmer = null,
        IPerformanceTracer? tracer = null,
        IEnsureContextStore? ensureContextStore = null,
        IPerformanceSummaryProvider? performanceSummaryProvider = null)
    {
        _engine = engine; _audit = audit; _tray = tray;
        _tracer = tracer ?? NoOpPerformanceTracer.Instance;
        _ensureContextStore = ensureContextStore;
        _readiness = readiness;
        _autoStart = autoStart;
        _ffmpegPrewarmer = ffmpegPrewarmer;
        _performanceSummaryProvider = performanceSummaryProvider ?? NoDataPerformanceSummaryProvider.Instance;
        _lastSelectedRegion = RegionSelectionStateStore.Load();
    }

    /// <summary>
    /// Returns the single microphone provider used for request parsing and public
    /// device list endpoints. The engine always owns the provider instance, so
    /// there is no separate ApiServer-level injection and no static fallback.
    /// </summary>
    private IMicrophoneDeviceProvider EffectiveMicrophoneProvider => _engine.MicrophoneProvider;

    /// <summary>
    /// Returns the microphone status provider used for fresh mute/volume checks.
    /// This is intentionally separate from the device enumeration provider so
    /// device caching does not stale dynamic mute/volume state.
    /// </summary>
    private IMicrophoneStatusProvider EffectiveMicrophoneStatusProvider => _engine.MicrophoneStatusProvider;

    private ISystemAudioEndpointProvider EffectiveSystemAudioEndpointProvider => _engine.SystemAudioEndpointProvider;

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
        409 => "Conflict",
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
                return ApiResponse.Ok(BuildAudioDevicesResponse(), reqId);

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
            if (seg.Length == 3 && method == "POST" && seg[2] == "marks")
                return AddMark(id, reqBody, reqId);
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

    private string AddMark(string recordingId, string reqBody, string reqId)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(reqBody);
        }
        catch (JsonException)
        {
            throw new ApiException(400, "INVALID_ARGUMENT", "Invalid JSON body.");
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                throw new ApiException(400, "INVALID_ARGUMENT", "Mark body must be a JSON object.");

            if (!root.TryGetProperty("label", out var labelElement))
            {
                throw new ApiException(400, "INVALID_ARGUMENT", "Invalid mark label.",
                    new { field = "label", reason = "required" });
            }

            if (labelElement.ValueKind != JsonValueKind.String)
            {
                throw new ApiException(400, "INVALID_ARGUMENT", "Invalid mark label.",
                    new { field = "label", reason = "must_be_string" });
            }

            var label = labelElement.GetString();
            string source = "agent";
            if (root.TryGetProperty("source", out var sourceElement))
            {
                if (sourceElement.ValueKind != JsonValueKind.String)
                {
                    throw new ApiException(400, "INVALID_ARGUMENT", "Invalid mark source.",
                        new { field = "source", reason = "must_be_agent" });
                }
                source = sourceElement.GetString() ?? "";
            }

            // The domain operation also supports the local hotkey, but
            // this authenticated remote endpoint must not let an agent claim
            // that source.
            if (!string.Equals(source, "agent", StringComparison.Ordinal))
            {
                throw new ApiException(400, "INVALID_ARGUMENT", "Invalid mark source.",
                    new { field = "source", allowed = new[] { "agent" } });
            }

            var mark = _engine.AddMark(recordingId, label!, source);
            return ApiResponse.Ok(new
            {
                recording_id = recordingId,
                mark = new
                {
                    t_ms = mark.TMs,
                    label = mark.Label,
                    source = mark.Source
                }
            }, reqId);
        }
    }

    private string CreateRecording(HttpRequest req, string reqBody, string reqId)
    {
        var agent = req.Headers.GetValueOrDefault("X-Agent-Name") ?? "unknown";
        var traceId = "trace_" + Guid.NewGuid().ToString("N")[..16];
        var clientSentAtUtc = req.Headers.GetValueOrDefault("X-Agent-Sent-At");
        const string endpoint = "recordings";
        ConsumeEnsureContextAndAssociate(req, traceId);
        _tracer.IntentAccepted(traceId, endpoint, clientSentAtUtc);

        JsonNode cfg;
        try
        {
            cfg = JsonNode.Parse(string.IsNullOrWhiteSpace(reqBody) ? "{}" : reqBody)
                  ?? throw new ApiException(400, "INVALID_ARGUMENT", "Body required");
        }
        catch
        {
            // Entry-level failure: no recording was created. Record the intent-level
            // validation failure and surface a stable 400 without leaking raw input.
            _tracer.IntentValidated(traceId, endpoint, success: false, errorCode: "INVALID_ARGUMENT");
            throw new ApiException(400, "INVALID_ARGUMENT", "Invalid JSON body");
        }

        object result;
        try
        {
            result = _engine.CreateRecording(cfg, agent, _tray, traceId, endpoint);
        }
        catch (ApiException ex)
        {
            // Engine is the owner of validation events for failures that occur inside
            // CreateRecording. Only record here if the engine did not already do so.
            if (!_tracer.HasValidationResult(traceId))
                _tracer.IntentValidated(traceId, endpoint, success: false, errorCode: ex.Code);
            throw;
        }

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
        var traceId = "trace_" + Guid.NewGuid().ToString("N")[..16];
        var clientSentAtUtc = req.Headers.GetValueOrDefault("X-Agent-Sent-At");
        const string endpoint = "recordings.quick";
        ConsumeEnsureContextAndAssociate(req, traceId);
        _tracer.IntentAccepted(traceId, endpoint, clientSentAtUtc);

        JsonNode body;
        try
        {
            body = JsonNode.Parse(string.IsNullOrWhiteSpace(reqBody) ? "{}" : reqBody)
                   ?? throw new ApiException(400, "INVALID_ARGUMENT", "Body required");
        }
        catch
        {
            _tracer.IntentValidated(traceId, endpoint, success: false, errorCode: "INVALID_ARGUMENT");
            throw new ApiException(400, "INVALID_ARGUMENT", "Invalid JSON body");
        }

        // Normalize the shared mode/series contract before target resolution,
        // audio enumeration, or the region-selection UI. Screenshot-series
        // audio is rejected here, before any target side effect.
        try
        {
            ConfigParser.RejectQuickScreenshotSeriesStopFields(body);
            ConfigParser.NormalizeModeAndSeries(body);
        }
        catch (ApiException ex)
        {
            _tracer.IntentValidated(traceId, endpoint, success: false, errorCode: ex.Code);
            throw;
        }

        var targetNode = body["target"];
        if (targetNode == null)
        {
            _tracer.IntentValidated(traceId, endpoint, success: false, errorCode: "INVALID_ARGUMENT");
            throw new ApiException(400, "INVALID_ARGUMENT", "target is required");
        }

        var targetType = targetNode["type"]?.GetValue<string>();
        if (string.IsNullOrWhiteSpace(targetType))
        {
            _tracer.IntentValidated(traceId, endpoint, success: false, errorCode: "INVALID_ARGUMENT");
            throw new ApiException(400, "INVALID_ARGUMENT", "target.type is required");
        }

        // Validate the shared top-level countdown contract before any quick
        // target resolution, audio enumeration, or region-selection UI.
        int countdownSeconds;
        try
        {
            countdownSeconds = ConfigParser.NormalizeCountdownSeconds(body);
        }
        catch (ApiException ex)
        {
            _tracer.IntentValidated(traceId, endpoint, success: false, errorCode: ex.Code);
            throw;
        }

        JsonObject cfg = BuildQuickRecordingConfig(body);
        cfg["countdown_seconds"] = countdownSeconds;
        SystemAudioEndpointInfo? preResolvedSystemAudioEndpoint = null;

        // Resolve audio intent before any target resolution so microphone failures
        // (system audio, unknown device, no devices, enumeration unavailable) fail
        // fast without display/window enumeration or opening the region-selection UI.
        try
        {
            var audioIntent = ConfigParser.ResolveAudioIntentDetails(
                cfg,
                EffectiveMicrophoneProvider,
                EffectiveMicrophoneStatusProvider,
                EffectiveSystemAudioEndpointProvider);
            preResolvedSystemAudioEndpoint = audioIntent.SystemAudioEndpoint;
            if (audioIntent.SystemAudioEndpoint != null)
                ConfigParser.BindResolvedSystemAudioEndpoint(cfg, audioIntent.SystemAudioEndpoint);
        }
        catch (ApiException ex)
        {
            _tracer.IntentValidated(traceId, endpoint, success: false, errorCode: ex.Code);
            throw;
        }

        try
        {
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
                        var result = _engine.CreateRecording(
                            cfg, agent, _tray, traceId, endpoint, preResolvedSystemAudioEndpoint);
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
                        // Pre-build to get the clamped capture bounds for the response.
                        // Use the engine's providers so active-window pre-build cannot
                        // diverge from the device list endpoint or the real recording path.
                        var preBuilt = ConfigParser.Build(
                            cfg,
                            agent,
                            out _,
                            EffectiveMicrophoneProvider,
                            EffectiveMicrophoneStatusProvider,
                            EffectiveSystemAudioEndpointProvider,
                            preResolvedSystemAudioEndpoint);
                        var capBounds = preBuilt.Config.Bounds;
                        var result = _engine.CreateRecording(
                            cfg, agent, _tray, traceId, endpoint, preResolvedSystemAudioEndpoint);
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
                            // No recording was created. Record an intent-level failure
                            // with a stable, non-sensitive code describing the outcome.
                            var noRecordingCode = sel.status switch
                            {
                                "selection_cancelled" => "selection_cancelled",
                                "selection_timeout" => "selection_timeout",
                                "display_unavailable" => "display_unavailable",
                                _ => "selection_failed"
                            };
                            _tracer.IntentValidated(traceId, endpoint, success: false, errorCode: noRecordingCode);
                            return ApiResponse.Ok(new
                            {
                                status = sel.status,
                                quick = new
                                {
                                    target_type = "selected_region",
                                    recording_created = false
                                },
                                performance_trace_id = traceId
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

                        var result = _engine.CreateRecording(
                            cfg, agent, _tray, traceId, endpoint, preResolvedSystemAudioEndpoint);
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

                        var result = _engine.CreateRecording(
                            cfg, agent, _tray, traceId, endpoint, preResolvedSystemAudioEndpoint);
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
        catch (ApiException ex)
        {
            if (!_tracer.HasValidationResult(traceId))
                _tracer.IntentValidated(traceId, endpoint, success: false, errorCode: ex.Code);
            throw;
        }
    }

    private void ConsumeEnsureContextAndAssociate(HttpRequest req, string traceId)
    {
        try
        {
            var contextId = req.Headers.GetValueOrDefault(EnsureContextHeaderName);
            if (string.IsNullOrWhiteSpace(contextId) || _ensureContextStore == null)
                return;

            var result = _ensureContextStore.TryConsume(contextId);
            _tracer.SetEnsureContextAssociation(traceId, EnsureContextAssociation.FromResult(result));
        }
        catch
        {
            // Context consumption is diagnostic only and must never change
            // recording state, confirmation, or API response status.
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

        if (body["countdown_seconds"] != null)
            cfg["countdown_seconds"] = body["countdown_seconds"]!.DeepClone();

        if (body["mode"] != null)
            cfg["mode"] = body["mode"]!.DeepClone();
        if (body["interval_ms"] != null)
            cfg["interval_ms"] = body["interval_ms"]!.DeepClone();
        if (body["max_count"] != null)
            cfg["max_count"] = body["max_count"]!.DeepClone();
        if (body["max_duration_seconds"] != null)
            cfg["max_duration_seconds"] = body["max_duration_seconds"]!.DeepClone();

        var stopConditionNode = body["stop_condition"];
        if (stopConditionNode != null)
        {
            cfg["stop_condition"] = stopConditionNode.DeepClone();
        }
        else
        {
            var durationSec = body["duration_seconds"]?.GetValue<int?>();
            if (durationSec.HasValue)
            {
                cfg["stop_condition"] = new JsonObject
                {
                    ["type"] = "duration",
                    ["seconds"] = durationSec.Value
                };
            }
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
                modes = new[] { "video", ScreenshotSeriesConfig.ModeName },
                sources = new[] { "display", "window", "region" },
                audio = new[] { "microphone", "system_audio" },
                audio_capabilities = new
                {
                    microphone = new { supported = true, status = GetFreshMicrophoneAvailability() },
                    system_audio = new { supported = true, status = GetFreshSystemAudioAvailability() }
                },
                containers = new[] { "mp4" },
                screenshot_series = new
                {
                    supported = true,
                    mode = ScreenshotSeriesConfig.ModeName,
                    output_format = "png_sequence",
                    audio_supported = false,
                    interval_ms = new { min = ScreenshotSeriesConfig.MinIntervalMs, max = ScreenshotSeriesConfig.MaxIntervalMs },
                    max_count = ScreenshotSeriesConfig.MaxFrameCount,
                    max_duration_seconds = ScreenshotSeriesConfig.MaxDurationSecondsLimit,
                    min_count = ScreenshotSeriesConfig.MinCount,
                    min_duration_seconds = ScreenshotSeriesConfig.MinDurationSeconds,
                    max_planned_frames = ScreenshotSeriesConfig.MaxFrameCount,
                    targets = new[] { "primary_display", "active_window", "selected_region", "last_region" },
                    capture_semantics = "one single-frame capture per anchored schedule point; no continuous video extraction"
                },
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
            chapter_marks = new
            {
                supported = true,
                endpoint = "/api/v1/recordings/{recording_id}/marks",
                local_hotkey = new
                {
                    supported = _tray.SupportsChapterMarksLocalHotkey,
                    registered = _tray.SupportsChapterMarksLocalHotkey && _tray.IsChapterMarksHotkeyRegistered,
                    gesture = _tray.SupportsChapterMarksLocalHotkey ? _tray.ChapterMarksHotkeyGesture : null,
                    registration_policy = _tray.ChapterMarksHotkeyRegistrationPolicy
                }
            },
            interaction = new
            {
                region_selection_endpoint = true,
                region_selection_requires_local_user = true,
                region_selection_may_block_in_headless = !_tray.SupportsRegionSelectionUi,
                quick_recording_endpoint = "/api/v1/recordings/quick",
                quick_recording_supported = true,
                countdown = new
                {
                    supported = true,
                    min_seconds = ConfigParser.MinCountdownSeconds,
                    max_seconds = ConfigParser.MaxCountdownSeconds,
                    default_seconds = ConfigParser.DefaultCountdownSeconds,
                    capture_during_countdown = false
                },
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
                quick_recipes = new object[]
                {
                    new
                    {
                        name = "record_primary_display",
                        target_type = "primary_display",
                        description = "Record the primary display with local confirmation.",
                        endpoint = "/api/v1/recordings/quick",
                        method = "POST",
                        request_template = new { target = new { type = "primary_display" }, duration_seconds = 60, countdown_seconds = ConfigParser.DefaultCountdownSeconds },
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
                        request_template = new { target = new { type = "active_window" }, duration_seconds = 60, countdown_seconds = ConfigParser.DefaultCountdownSeconds },
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
                        request_template = new { target = new { type = "selected_region" }, duration_seconds = 60, countdown_seconds = ConfigParser.DefaultCountdownSeconds },
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
                        request_template = new { target = new { type = "last_region" }, duration_seconds = 60, countdown_seconds = ConfigParser.DefaultCountdownSeconds },
                        available = hasLastRegion,
                        unavailable_reason = hasLastRegion ? null : "no_last_selected_region"
                    },
                    new
                    {
                        name = "screenshot_selected_region",
                        target_type = "selected_region",
                        mode = ScreenshotSeriesConfig.ModeName,
                        description = "Ask the local user to select a region, then capture a bounded PNG screenshot series with local confirmation.",
                        endpoint = "/api/v1/recordings/quick",
                        method = "POST",
                        request_template = new
                        {
                            target = new { type = "selected_region" },
                            mode = ScreenshotSeriesConfig.ModeName,
                            interval_ms = 5000,
                            max_count = 12,
                            countdown_seconds = ConfigParser.DefaultCountdownSeconds
                        },
                        available = supportsRegionSelection,
                        unavailable_reason = supportsRegionSelection ? null : "headless_host"
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
            },
            perf_summary = GetPerfSummarySafe()
        };
    }

    private object GetPerfSummarySafe()
    {
        try
        {
            return _performanceSummaryProvider.GetSummary();
        }
        catch
        {
            // Final reliability boundary: even a misbehaving provider must not
            // break /capabilities. Return a complete, privacy-safe degraded
            // summary without exception text, types, paths, or IDs.
            var degraded = PerformanceSummary.NoData(DateTime.UtcNow,
                RollingJsonlPerformanceSummaryProviderConstants.DefaultMaxTracesPerGroup,
                new PerformanceSummaryQuality { ReasonCode = "provider_error" });
            degraded.Status = PerformanceSummaryStatus.Degraded;
            return degraded;
        }
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

    private object BuildAudioDevicesResponse()
    {
        var devices = GetFreshMicrophoneDevices(out var enumerationAvailable);
        var availability = AvailabilityFromDevices(devices, enumerationAvailable);
        var outputDevices = GetFreshSystemAudioDevices(out var outputEnumerationAvailable);
        var outputAvailability = AvailabilityFromDevices(outputDevices, outputEnumerationAvailable);
        return new
        {
            status = availability,
            microphone_status = availability,
            system_audio_status = outputAvailability,
            microphone_supported = true,
            system_audio_supported = true,
            input_devices = devices.Select(d => new
            {
                id = d.Id,
                name = d.Name,
                is_default = d.IsDefault,
                state = d.State,
                is_muted = d.IsMuted,
                volume_percent = d.VolumePercent
            }).ToArray(),
            output_devices = outputDevices.Select(d => new
            {
                id = d.Id,
                name = d.Name,
                is_default = d.IsDefaultMultimedia,
                state = d.State,
                direction = "render"
            }).ToArray()
        };
    }

    /// <summary>
    /// Enumerates microphones and merges each entry with fresh CoreAudio status.
    /// Entries the fresh lookup definitively proves are gone (stale enumeration
    /// cache entries) are removed, and the enumeration cache is invalidated so
    /// the next call re-enumerates. Inconclusive status lookups never remove a
    /// device. The returned id values are the provider's own identifiers and
    /// round-trip unchanged into recording requests.
    /// </summary>
    private IReadOnlyList<MicrophoneDeviceInfo> GetFreshMicrophoneDevices(out bool enumerationAvailable)
    {
        IReadOnlyList<MicrophoneDeviceInfo> devices;
        try
        {
            devices = EffectiveMicrophoneProvider.GetDevicesAsync().GetAwaiter().GetResult();
        }
        catch
        {
            enumerationAvailable = false;
            return Array.Empty<MicrophoneDeviceInfo>();
        }

        enumerationAvailable = true;
        var assembly = AudioDeviceListAssembler.Assemble(devices, QueryMicrophoneStatusSafe);
        if (assembly.RemovedStaleDevices && EffectiveMicrophoneProvider is CachingMicrophoneDeviceProvider caching)
            caching.Refresh();
        return assembly.Devices;
    }

    private string GetFreshMicrophoneAvailability()
    {
        var devices = GetFreshMicrophoneDevices(out var enumerationAvailable);
        return AvailabilityFromDevices(devices, enumerationAvailable);
    }

    private IReadOnlyList<SystemAudioEndpointInfo> GetFreshSystemAudioDevices(
        out bool enumerationAvailable)
    {
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var devices = EffectiveSystemAudioEndpointProvider
                .GetRenderEndpointsAsync(cts.Token)
                .WaitAsync(TimeSpan.FromSeconds(5))
                .GetAwaiter()
                .GetResult();
            enumerationAvailable = true;
            return devices ?? Array.Empty<SystemAudioEndpointInfo>();
        }
        catch
        {
            enumerationAvailable = false;
            return Array.Empty<SystemAudioEndpointInfo>();
        }
    }

    private string GetFreshSystemAudioAvailability()
    {
        var devices = GetFreshSystemAudioDevices(out var enumerationAvailable);
        return AvailabilityFromDevices(devices, enumerationAvailable);
    }

    /// <summary>
    /// Maps a fresh device list to the stable availability status: "ready" when
    /// devices are present, "no_devices" when the enumeration succeeded but
    /// returned nothing, and "unavailable" when enumeration failed.
    /// </summary>
    private static string AvailabilityFromDevices(IReadOnlyList<MicrophoneDeviceInfo> devices, bool enumerationAvailable)
        => !enumerationAvailable ? "unavailable" : devices.Count > 0 ? "ready" : "no_devices";

    private static string AvailabilityFromDevices(IReadOnlyList<SystemAudioEndpointInfo> devices, bool enumerationAvailable)
        => !enumerationAvailable ? "unavailable" : devices.Count > 0 ? "ready" : "no_devices";

    private MicrophoneStatus QueryMicrophoneStatusSafe(string deviceId)
    {
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            return EffectiveMicrophoneStatusProvider.GetStatusAsync(deviceId, cts.Token)
                .WaitAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
        }
        catch
        {
            return new MicrophoneStatus(null, null, null, null);
        }
    }

    private object Permissions()
    {
        var devices = GetFreshMicrophoneDevices(out var enumerationAvailable);
        var availability = AvailabilityFromDevices(devices, enumerationAvailable);
        // Permissions distinguishes "device availability" from "OS permission granted".
        // The honest values are available / no_devices / unavailable; "granted" is not
        // reported because this version does not probe the real Windows microphone ACL.
        var permissionStatus = availability switch
        {
            "ready" => "available",
            _ => availability
        };

        return new
        {
            screen_capture = new { status = "granted" },
            microphone = new { supported = true, status = permissionStatus },
            system_audio = new
            {
                supported = true,
                status = PermissionStatusFromAvailability(GetFreshSystemAudioAvailability())
            },
            output_directory = new { status = "granted", default_path = Paths.DefaultOutputDir, selection_ui = true }
        };
    }

    private static string PermissionStatusFromAvailability(string availability)
        => availability == "ready" ? "available" : availability;
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
