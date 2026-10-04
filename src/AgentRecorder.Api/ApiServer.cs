using System;
using System.Collections.Generic;
using System.Globalization;
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
using AgentRecorder.Core.Automation;
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

    private readonly TcpListener _listener;
    private readonly object _lifecycleGate = new();
    private readonly object _clientTaskGate = new();
    private readonly HashSet<Task> _clientTasks = new();
    private readonly HashSet<TcpClient> _activeClients = new();
    private readonly RecordingEngine _engine;
    private readonly AuditLogger _audit;
    private readonly ITrayContext _tray;
    private readonly IPerformanceTracer _tracer;
    private readonly IEnsureContextStore? _ensureContextStore;
    private readonly RuntimeReadiness? _readiness;
    private readonly WindowsAutoStartManager? _autoStart;
    private readonly FfmpegPrewarmer? _ffmpegPrewarmer;
    private readonly IPerformanceSummaryProvider _performanceSummaryProvider;
    private readonly IStandingPlanSetupGateway? _standingPlanSetupGateway;
    private readonly IRecurringPlanSetupGateway? _recurringPlanSetupGateway;
    private readonly IPlanExecutionStatusGateway? _planExecutionStatusGateway;
    private readonly IRequiredOncePlanSetupGateway? _requiredOncePlanSetupGateway;
    private readonly IFutureWindowOneShotGateway? _futureWindowOneShotGateway;
    private readonly IFixedRegionProfileManagementGateway? _profileManagementGateway;
    private readonly IFixedRegionProfileDisplayEnvironmentProvider _profileDisplayEnvironmentProvider;
    private readonly FixedRegionProfileSelectionCache _profileSelectionCache = new();
    private CancellationTokenSource _cts = new();
    private Task? _loopTask;
    private int _started;
    private int _stopped;
    private readonly TaskCompletionSource<object?> _stopCompleted =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private SelectedRegionState? _lastSelectedRegion;
    private readonly object _regionLock = new();

    public const string EnsureContextHeaderName = EnsureContextStore.HeaderName;

    public ApiServer(RecordingEngine engine, AuditLogger audit, ITrayContext tray,
        RuntimeReadiness? readiness = null,
        WindowsAutoStartManager? autoStart = null,
        FfmpegPrewarmer? ffmpegPrewarmer = null,
        IPerformanceTracer? tracer = null,
        IEnsureContextStore? ensureContextStore = null,
        IPerformanceSummaryProvider? performanceSummaryProvider = null,
        IStandingPlanSetupGateway? standingPlanSetupGateway = null,
        IRecurringPlanSetupGateway? recurringPlanSetupGateway = null,
        IPlanExecutionStatusGateway? planExecutionStatusGateway = null,
        IRequiredOncePlanSetupGateway? requiredOncePlanSetupGateway = null,
        IFutureWindowOneShotGateway? futureWindowOneShotGateway = null,
        IFixedRegionProfileManagementGateway? profileManagementGateway = null)
        : this(engine, audit, tray, readiness, autoStart, ffmpegPrewarmer, tracer, ensureContextStore,
            performanceSummaryProvider, standingPlanSetupGateway, recurringPlanSetupGateway,
            planExecutionStatusGateway, Port, requiredOncePlanSetupGateway, futureWindowOneShotGateway,
            profileManagementGateway, null)
    {
    }

    internal ApiServer(RecordingEngine engine, AuditLogger audit, ITrayContext tray,
        RuntimeReadiness? readiness,
        WindowsAutoStartManager? autoStart,
        FfmpegPrewarmer? ffmpegPrewarmer,
        IPerformanceTracer? tracer,
        IEnsureContextStore? ensureContextStore,
        IPerformanceSummaryProvider? performanceSummaryProvider,
        IStandingPlanSetupGateway? standingPlanSetupGateway,
        IRecurringPlanSetupGateway? recurringPlanSetupGateway,
        IPlanExecutionStatusGateway? planExecutionStatusGateway,
        int listenPort,
        IRequiredOncePlanSetupGateway? requiredOncePlanSetupGateway = null,
        IFutureWindowOneShotGateway? futureWindowOneShotGateway = null,
        IFixedRegionProfileManagementGateway? profileManagementGateway = null,
        IFixedRegionProfileDisplayEnvironmentProvider? profileDisplayEnvironmentProvider = null)
    {
        if (listenPort is < 0 or > 65535)
            throw new ArgumentOutOfRangeException(nameof(listenPort));
        _listener = new TcpListener(IPAddress.Loopback, listenPort);
        _engine = engine; _audit = audit; _tray = tray;
        _tracer = tracer ?? NoOpPerformanceTracer.Instance;
        _ensureContextStore = ensureContextStore;
        _readiness = readiness;
        _autoStart = autoStart;
        _ffmpegPrewarmer = ffmpegPrewarmer;
        _performanceSummaryProvider = performanceSummaryProvider ?? NoDataPerformanceSummaryProvider.Instance;
        _standingPlanSetupGateway = standingPlanSetupGateway;
        _recurringPlanSetupGateway = recurringPlanSetupGateway;
        _planExecutionStatusGateway = planExecutionStatusGateway;
        _requiredOncePlanSetupGateway = requiredOncePlanSetupGateway;
        _futureWindowOneShotGateway = futureWindowOneShotGateway;
        _profileManagementGateway = profileManagementGateway;
        _profileDisplayEnvironmentProvider = profileDisplayEnvironmentProvider ??
            SystemQueryFixedRegionProfileDisplayEnvironmentProvider.Instance;
        _lastSelectedRegion = RegionSelectionStateStore.Load();
    }

    internal int BoundPort => _started == 0
        ? throw new InvalidOperationException("The API server has not started.")
        : ((IPEndPoint)_listener.LocalEndpoint).Port;

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
        lock (_lifecycleGate)
        {
            if (_started != 0)
                throw new InvalidOperationException("ApiServer has already been started.");
            if (_stopped != 0)
                throw new InvalidOperationException("ApiServer has already been stopped.");
            _listener.Start();
            _loopTask = Task.Run(() => Loop(_cts.Token));
            Volatile.Write(ref _started, 1);
        }
    }

    public void Stop()
    {
        Task? loopTask;
        var ownsStop = false;
        lock (_lifecycleGate)
        {
            if (_stopped == 0)
            {
                Volatile.Write(ref _stopped, 1);
                ownsStop = true;
                try { _cts.Cancel(); } catch { }
                try { _listener.Stop(); } catch { }
            }
            loopTask = _loopTask;
        }
        if (!ownsStop)
        {
            _stopCompleted.Task.GetAwaiter().GetResult();
            return;
        }

        try
        {
            try { loopTask?.GetAwaiter().GetResult(); } catch { }

            Task[] clients;
            TcpClient[] active;
            lock (_clientTaskGate)
            {
                clients = _clientTasks.ToArray();
                active = _activeClients.ToArray();
            }
            foreach (var client in active)
            {
                try { client.Dispose(); } catch { }
            }
            try { Task.WhenAll(clients).GetAwaiter().GetResult(); } catch { }
        }
        finally { _stopCompleted.TrySetResult(null); }
    }

    private async Task Loop(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            TcpClient client;
            try { client = await _listener.AcceptTcpClientAsync(ct); }
            catch { break; }
            var task = Task.Run(() => HandleClient(client));
            lock (_clientTaskGate)
            {
                _activeClients.Add(client);
                _clientTasks.Add(task);
            }
            _ = task.ContinueWith(_ =>
            {
                lock (_clientTaskGate)
                {
                    _activeClients.Remove(client);
                    _clientTasks.Remove(task);
                }
            }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
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

                var responseBody = Route(method, path, request, body, reqId, out int status, out var responseHeaders);
                await WriteJson(stream, status, responseBody, responseHeaders);
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

    private static async Task WriteJson(Stream stream, int status, string body, IReadOnlyDictionary<string, string>? responseHeaders = null)
    {
        var buf = Encoding.UTF8.GetBytes(body);
        var extraHeaders = responseHeaders is null
            ? string.Empty
            : string.Concat(responseHeaders.Select(header => $"{header.Key}: {header.Value}\r\n"));
        var headers = $"HTTP/1.1 {status} {StatusText(status)}\r\nContent-Type: application/json; charset=utf-8\r\nContent-Length: {buf.Length}\r\nConnection: close\r\n{extraHeaders}\r\n";
        var responseBytes = Encoding.UTF8.GetBytes(headers);
        await stream.WriteAsync(responseBytes);
        await stream.WriteAsync(buf);
        try { stream.Flush(); } catch { }
    }

    private static string StatusText(int status) => status switch
    {
        200 => "OK",
        201 => "Created",
        400 => "Bad Request",
        401 => "Unauthorized",
        403 => "Forbidden",
        404 => "Not Found",
        405 => "Method Not Allowed",
        409 => "Conflict",
        412 => "Precondition Failed",
        428 => "Precondition Required",
        202 => "Accepted",
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
        var duplicateHeaders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (int i = 1; i < lines.Length; i++)
        {
            var line = lines[i];
            var idx = line.IndexOf(':');
            if (idx <= 0) continue;
            var name = line[..idx].Trim();
            var value = line[(idx + 1)..].Trim();
            if (!headers.TryAdd(name, value))
            {
                if (name.Equals("If-Match", StringComparison.OrdinalIgnoreCase) ||
                    name.Equals("Idempotency-Key", StringComparison.OrdinalIgnoreCase))
                    duplicateHeaders.Add(name);
                headers[name] = value;
            }
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
        return new HttpRequest(method, rawPath, headers, body, duplicateHeaders);
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
        if (method == "POST" || method == "PUT" || method == "PATCH" || method == "DELETE")
            return true;

        var sensitivePaths = new[] { "/api/v1/recordings", "/api/v1/confirmations", "/api/v1/plan-setups", "/api/v1/plans", "/api/v1/future-window-authorizations", "/api/v1/profiles" };
        return sensitivePaths.Any(p => path.StartsWith(p));
    }

    private string Route(string method, string path, HttpRequest req,
                         string reqBody, string reqId, out int status,
                         out Dictionary<string, string> responseHeaders)
    {
        status = 200;
        responseHeaders = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
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
            case ("POST", "/regions/select"):
                return CreateRegionSelection(req, reqBody, reqId);

            case ("POST", "/plans"):
                return CreatePlan(req, reqBody, reqId, ref status);

            case ("GET", "/recordings"):
                return ApiResponse.Ok(new { recordings = _engine.List() }, reqId);
        }

        if (TryRouteFixedRegionProfiles(sub, method, req, reqBody, reqId, ref status, responseHeaders, out var profileResponse))
            return profileResponse;

        var seg = sub.Trim('/').Split('/');

        if (seg.Length >= 1 && seg[0] == "plan-setups" && method == "GET")
        {
            if (seg.Length != 2) throw PlanSetupNotFound();
            return GetPlanSetup(seg[1], req, reqId);
        }

        if (seg.Length >= 1 && seg[0] == "future-window-authorizations")
        {
            var gateway = _futureWindowOneShotGateway;

            if (seg.Length == 1 && method == "POST")
            {
                var create = FutureWindowOneShotApiRequestParser.ParseCreate(
                    reqBody, req.Headers.GetValueOrDefault("Idempotency-Key"));
                if (gateway is null || !gateway.IsSetupSupported)
                    throw new ApiException(503, "FUTURE_WINDOW_AUTHORIZATION_UNAVAILABLE",
                        "The local future-window authorization host is unavailable.",
                        new { reason_code = "future_window_runtime_unavailable" });
                FutureWindowAuthorizationCreateResponse result;
                try { result = gateway.CreateOrGet(create); }
                catch (ApiException) { throw; }
                catch (Exception exception)
                {
                    _audit.Log("future_window_authorization.api_create_failed", new
                    { reason_code = "setup_persistence_failed", exception_type = exception.GetType().Name });
                    throw new ApiException(500, "FUTURE_WINDOW_SETUP_FAILED", "The authorization setup could not be persisted.");
                }
                if (result.Result == "conflict")
                    throw new ApiException(409, "IDEMPOTENCY_KEY_REUSED",
                        "The Idempotency-Key is already bound to a different normalized request.",
                        new { authorization_id = result.Authorization.AuthorizationId, reason_code = "idempotency_key_reused" });
                status = 202;
                return ApiResponse.Ok(result, reqId);
            }

            if (seg.Length == 2 && method == "GET")
            {
                if (gateway is null || !gateway.IsSetupSupported)
                    throw new ApiException(503, "FUTURE_WINDOW_AUTHORIZATION_UNAVAILABLE",
                        "The local future-window authorization host is unavailable.",
                        new { reason_code = "future_window_runtime_unavailable" });
                var state = gateway.Get(seg[1]);
                if (state is null) throw FutureWindowNotFound();
                return ApiResponse.Ok(state, reqId);
            }

            if (seg.Length == 3 && seg[2] == "runs" && method == "POST")
            {
                var windowId = FutureWindowOneShotApiRequestParser.ParseStartWindowId(reqBody);
                if (gateway is null || !gateway.IsExecutionSupported)
                    throw new ApiException(503, "FUTURE_WINDOW_EXECUTION_UNAVAILABLE",
                        "The future-window run host is unavailable.",
                        new { reason_code = gateway is null ? "future_window_runtime_unavailable" : "future_window_execution_unavailable" });
                var result = gateway.Start(seg[1], windowId);
                if (!result.Accepted || string.IsNullOrWhiteSpace(result.RunId))
                    throw FutureWindowStartRejected(result.ReasonCode ?? "future_window_start_rejected");
                status = 202;
                return ApiResponse.Ok(new FutureWindowAuthorizationRunResponse(
                    "start_committed", result.RunId, $"{Prefix}/recordings/{Uri.EscapeDataString(result.RunId)}"), reqId);
            }

            if (seg.Length == 3 && seg[2] == "revoke" && method == "POST")
            {
                if (reqBody.Length > 0 && reqBody.Trim() != "{}")
                    throw new ApiException(400, "INVALID_ARGUMENT", "Revoke request body must be empty or {}.");
                if (gateway is null || !gateway.IsSetupSupported)
                    throw new ApiException(503, "FUTURE_WINDOW_AUTHORIZATION_UNAVAILABLE",
                        "The local future-window authorization host is unavailable.",
                        new { reason_code = "future_window_runtime_unavailable" });
                var state = gateway.Revoke(seg[1]);
                if (state is null) throw FutureWindowNotFound();
                return ApiResponse.Ok(new FutureWindowAuthorizationRevokeResponse(
                    state.Status == "revoked", state), reqId);
            }

            if (gateway is null || !gateway.IsSetupSupported)
                throw new ApiException(503, "FUTURE_WINDOW_AUTHORIZATION_UNAVAILABLE",
                    "The local future-window authorization host is unavailable.",
                    new { reason_code = "future_window_runtime_unavailable" });
        }

        if (seg.Length >= 1 && seg[0] == "plans" && method == "GET")
        {
            if (seg.Length != 3 || seg[2] != "status")
                throw new ApiException(404, "PLAN_NOT_FOUND", "The requested plan was not found.");
            return GetPlanExecutionStatus(seg[1], reqId);
        }

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

    private static ApiException FutureWindowNotFound() =>
        new(404, "FUTURE_WINDOW_AUTHORIZATION_NOT_FOUND", "The future-window authorization was not found.");

    private static ApiException FutureWindowStartRejected(string reason) => reason switch
    {
        "future_window_not_found" => FutureWindowNotFound(),
        "future_window_not_active" => new ApiException(409, "FUTURE_WINDOW_NOT_ACTIVE", "The authorization is not active.", new { reason_code = reason }),
        "future_window_expired" => new ApiException(409, "FUTURE_WINDOW_EXPIRED", "The authorization has expired.", new { reason_code = reason }),
        "multiple_eligible_windows" => new ApiException(409, "FUTURE_WINDOW_AMBIGUOUS", "More than one eligible window belongs to the approved executable.", new { reason_code = reason }),
        "recording_conflict" => new ApiException(409, "RECORDING_CONFLICT", "Another recording is active.", new { reason_code = reason }),
        _ => new ApiException(409, "FUTURE_WINDOW_START_REJECTED", "The exact future-window target did not pass the start gate.", new { reason_code = reason })
    };

    private string GetPlanExecutionStatus(string planId, string requestId)
    {
        if (string.IsNullOrWhiteSpace(planId) || planId.Length > 128 ||
            planId != planId.Trim() ||
            !planId.All(character => character is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9' or '-' or '_' or '.'))
            throw new ApiException(400, "INVALID_ARGUMENT", "plan_id is not a valid plan identifier.");
        if (_planExecutionStatusGateway is null)
            throw new ApiException(503, "PLAN_STATUS_UNAVAILABLE", "Durable plan execution status is unavailable.");

        PlanExecutionStatusState? state;
        try
        {
            state = _planExecutionStatusGateway.Get(planId);
        }
        catch
        {
            // Keep persistence details, file paths, and identities out of HTTP errors.
            throw new ApiException(503, "PLAN_STATUS_UNAVAILABLE", "Durable plan execution status is unavailable.");
        }
        if (state is null)
            throw new ApiException(404, "PLAN_NOT_FOUND", "The requested plan was not found.");

        return ApiResponse.Ok(new
        {
            plan_id = state.PlanId,
            kind = state.Kind,
            plan_status = state.PlanStatus,
            schedule_exhausted = state.ScheduleExhausted,
            occurrence_count = state.OccurrenceCount,
            next_occurrence = PlanExecutionOccurrenceObject(state.NextOccurrence),
            latest_occurrence = PlanExecutionOccurrenceObject(state.LatestOccurrence)
        }, requestId);
    }

    private static object? PlanExecutionOccurrenceObject(PlanExecutionOccurrenceState? occurrence) =>
        occurrence is null ? null : new
        {
            occurrence_id = occurrence.OccurrenceId,
            window_start_utc = occurrence.WindowStartUtc,
            window_end_utc = occurrence.WindowEndUtc,
            status = occurrence.Status,
            terminal_reason_code = occurrence.TerminalReasonCode,
            run_id = occurrence.RunId,
            run = occurrence.Run is null ? null : new
            {
                run_id = occurrence.Run.RunId,
                status = occurrence.Run.Status,
                terminal_reason_code = occurrence.Run.TerminalReasonCode
            },
            output_path = occurrence.OutputPath,
            output_path_recorded = occurrence.OutputPathRecorded,
            output_file_exists = occurrence.OutputFileExists,
            execution_status_code = occurrence.ExecutionStatusCode
        };

    private string CreatePlan(HttpRequest req, string reqBody, string reqId, ref int status)
    {
        RejectWindowSurfaceIntentOutsideOrdinaryRecording(reqBody);
        var idempotencyKey = StandingPlanApiRequestParser.NormalizeIdempotencyKey(
            req.Headers.GetValueOrDefault("Idempotency-Key"));

        return ReadPlanScheduleKind(reqBody) switch
        {
            "once" => CreateOneTimePlan(reqBody, idempotencyKey, reqId, ref status),
            "daily" or "weekly" => CreateRecurringPlan(reqBody, idempotencyKey, reqId, ref status),
            _ => throw new ApiException(400, "INVALID_ARGUMENT", "schedule.kind must be 'once', 'daily', or 'weekly'."),
        };
    }

    private string CreateOneTimePlan(string reqBody, string idempotencyKey, string reqId, ref int status)
    {
        if (StandingPlanApiRequestParser.IsRequiredAuthorizationMode(reqBody))
        {
            var request = StandingPlanApiRequestParser.ParseRequired(reqBody, idempotencyKey);
            if (_requiredOncePlanSetupGateway is null)
                throw new ApiException(503, "REQUIRED_PLAN_SETUP_UNAVAILABLE", "The local required-plan setup host is unavailable.");
            if (!RequiredOnceInteractiveDesktopAvailable())
                throw new ApiException(409, "INTERACTIVE_DESKTOP_REQUIRED",
                    "A local interactive desktop is required to create and approve a required-mode plan.",
                    new { suggested_action = "run_tray_host" });

            RequiredOncePlanSetupCreateResult result;
            try { result = _requiredOncePlanSetupGateway.CreateOrGet(request); }
            catch (Exception exception)
            {
                _audit.Log("required_once_setup.api_create_failed", new
                {
                    reason_code = "setup_persistence_failed",
                    exception_type = exception.GetType().Name,
                });
                throw new ApiException(500, "SETUP_PERSISTENCE_FAILED", "The required one-time setup could not be persisted.");
            }
            if (result.Status == StandingPlanSetupCreateStatus.Conflict)
                throw new ApiException(409, "IDEMPOTENCY_KEY_REUSED",
                    "The Idempotency-Key is already bound to a different normalized request.",
                    new { setup_intent_id = result.SetupIntentId, reason_code = result.ReasonCode ?? "idempotency_key_reused" });
            if (result.Status == StandingPlanSetupCreateStatus.Rejected || result.SetupIntentId is null)
                throw new ApiException(409,
                    result.ReasonCode == "interactive_desktop_unavailable" ? "INTERACTIVE_DESKTOP_REQUIRED" : "REQUIRED_PLAN_SETUP_REJECTED",
                    "The required one-time setup was not accepted.",
                    new { reason_code = result.ReasonCode ?? "setup_rejected" });

            status = 202;
            return ApiResponse.Ok(RequiredOncePlanSetupResponse(new StandingPlanSetupState(
                result.SetupIntentId, result.StatusCode, result.StatusVersion, result.PlanId, result.OccurrenceId,
                null, result.StatusCode is "region_selection_pending" or "creation_approval_pending",
                NextRequiredOnceAction(result.StatusCode, _requiredOncePlanSetupGateway.IsExecutionSupported), result.ReasonCode,
                StatusVersionCursor: result.StatusVersionCursor,
                AuthorizationMode: "required",
                RequiresExecutionConfirmation: true,
                ExecutionSupported: _requiredOncePlanSetupGateway.IsExecutionSupported)), reqId);
        }

        return CreateStandingPlan(reqBody, idempotencyKey, reqId, ref status);
    }

    private static string ReadPlanScheduleKind(string requestBody)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(requestBody, new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = 16,
            });
        }
        catch (JsonException exception)
        {
            throw new ApiException(400, "INVALID_ARGUMENT", "Invalid JSON body.", exception.Message);
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                throw new ApiException(400, "INVALID_ARGUMENT", "request must be a JSON object.");
            if (root.TryGetProperty("required_capture_semantics", out _))
                throw new ApiException(400, "INVALID_ARGUMENT",
                    "required_capture_semantics is supported only by POST /api/v1/recordings.");
            var scheduleCount = 0;
            JsonElement schedule = default;
            foreach (var property in root.EnumerateObject())
            {
                if (!string.Equals(property.Name, "schedule", StringComparison.Ordinal)) continue;
                scheduleCount++;
                schedule = property.Value;
            }
            if (scheduleCount != 1 || schedule.ValueKind != JsonValueKind.Object)
                throw new ApiException(400, "INVALID_ARGUMENT", "schedule must be present exactly once as a JSON object.");

            var kindCount = 0;
            JsonElement kind = default;
            foreach (var property in schedule.EnumerateObject())
            {
                if (!string.Equals(property.Name, "kind", StringComparison.Ordinal)) continue;
                kindCount++;
                kind = property.Value;
            }
            if (kindCount != 1 || kind.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(kind.GetString()))
                throw new ApiException(400, "INVALID_ARGUMENT", "schedule.kind must be present exactly once as a non-empty string.");
            var value = kind.GetString()!;
            if (value is not ("once" or "daily" or "weekly"))
                throw new ApiException(400, "INVALID_ARGUMENT", "schedule.kind must be 'once', 'daily', or 'weekly'.");
            return value;
        }
    }

    private string CreateStandingPlan(string reqBody, string idempotencyKey, string reqId, ref int status)
    {

        if (_standingPlanSetupGateway is null || !_standingPlanSetupGateway.IsInteractiveDesktopAvailable)
        {
            throw new ApiException(409, "INTERACTIVE_DESKTOP_REQUIRED",
                "A local interactive desktop is required before a standing setup intent can be persisted.",
                new { suggested_action = "run_tray_host" });
        }

        if (!_standingPlanSetupGateway.IsUnattendedEnabled)
        {
            throw new ApiException(409, "UNATTENDED_DISABLED",
                "Unattended lease mode is disabled by the local safety control.",
                new { suggested_action = "enable_unattended_mode_locally" });
        }

        var request = StandingPlanApiRequestParser.Parse(reqBody, idempotencyKey);
        StandingPlanSetupCreateResult result;
        try
        {
            result = _standingPlanSetupGateway.CreateOrGet(request);
        }
        catch (Exception exception)
        {
            _audit.Log("standing_setup.api_create_failed", new
            {
                reason_code = "setup_persistence_failed",
                exception_type = exception.GetType().Name,
            });
            throw new ApiException(500, "SETUP_PERSISTENCE_FAILED",
                "The standing setup intent could not be persisted.");
        }

        if (result.Status == StandingPlanSetupCreateStatus.Conflict)
        {
            throw new ApiException(409, "IDEMPOTENCY_KEY_REUSED",
                "The Idempotency-Key is already bound to a different request.",
                new { setup_intent_id = result.SetupIntentId, reason_code = result.ReasonCode ?? "setup_conflict" });
        }

        if (result.Status is StandingPlanSetupCreateStatus.Rejected or StandingPlanSetupCreateStatus.Expired)
        {
            var code = result.ReasonCode switch
            {
                "unattended_disabled" => "UNATTENDED_DISABLED",
                "interactive_desktop_required" => "INTERACTIVE_DESKTOP_REQUIRED",
                _ => "SETUP_CONFLICT",
            };
            throw new ApiException(409, code,
                "The standing setup request was not accepted.",
                new { reason_code = result.ReasonCode ?? "setup_conflict" });
        }

        status = 202;
        return ApiResponse.Ok(
            StandingPlanResponse(
                result.SetupIntentId,
                result.StatusCode,
                result.StatusVersion,
                result.PlanId,
                result.OccurrenceId,
                result.LeaseId,
                result.StatusCode is "setup_pending" or "pending_lease_approval",
                NextActionFor(result.StatusCode),
                result.ReasonCode,
                $"{Prefix}/plan-setups/{Uri.EscapeDataString(result.SetupIntentId!)}",
                result.StatusVersionCursor,
                runId: null,
                recordingStatusUrl: null,
                startedAtUtc: null,
                completedAtUtc: null),
            reqId);
    }

    private string CreateRecurringPlan(string reqBody, string idempotencyKey, string reqId, ref int status)
    {
        if (_recurringPlanSetupGateway is null || !RecurringInteractiveDesktopAvailable())
            throw new ApiException(409, "INTERACTIVE_DESKTOP_REQUIRED",
                "A local interactive desktop is required before a recurring setup intent can be persisted.",
                new { suggested_action = "run_tray_host" });

        if (!RecurringUnattendedEnabled())
            throw new ApiException(409, "UNATTENDED_DISABLED",
                "Unattended lease mode is disabled by the local safety control.",
                new { suggested_action = "enable_unattended_mode_locally" });

        if (!RecurringExecutionSupported())
            throw new ApiException(409, "RECURRING_EXECUTION_UNAVAILABLE",
                "The recurring natural-wake execution runtime is unavailable.");

        var request = RecurringPlanApiRequestParser.Parse(reqBody, idempotencyKey);
        AgentRecorder.Api.RecurringPlanSetupCreateResult result;
        try
        {
            result = _recurringPlanSetupGateway.CreateOrGet(request);
        }
        catch (Exception exception)
        {
            _audit.Log("recurring_setup.api_create_failed", new
            {
                reason_code = "setup_persistence_failed",
                exception_type = exception.GetType().Name,
            });
            throw new ApiException(500, "SETUP_PERSISTENCE_FAILED",
                "The recurring setup intent could not be persisted.");
        }

        if (result.Status == RecurringPlanSetupCreateStatus.Conflict)
            throw new ApiException(409, "IDEMPOTENCY_KEY_REUSED",
                "The Idempotency-Key is already bound to a different request.",
                new { setup_intent_id = result.State?.SetupIntentId, reason_code = result.ReasonCode ?? "setup_conflict" });

        if (result.Status is RecurringPlanSetupCreateStatus.Rejected or RecurringPlanSetupCreateStatus.Expired)
        {
            var code = result.ReasonCode switch
            {
                "interactive_desktop_required" => "INTERACTIVE_DESKTOP_REQUIRED",
                "unattended_disabled" => "UNATTENDED_DISABLED",
                "recurring_execution_unavailable" => "RECURRING_EXECUTION_UNAVAILABLE",
                _ => "SETUP_CONFLICT",
            };
            throw new ApiException(409, code, "The recurring setup request was not accepted.",
                new { reason_code = result.ReasonCode ?? "setup_conflict" });
        }

        var state = result.State;
        if (state is null)
            throw new ApiException(500, "SETUP_PERSISTENCE_FAILED", "The recurring setup result was incomplete.");

        status = 202;
        return ApiResponse.Ok(RecurringPlanSetupResponse(state), reqId);
    }

    private string GetPlanSetup(string setupIntentId, HttpRequest req, string reqId)
    {
        if (string.IsNullOrWhiteSpace(setupIntentId) || setupIntentId.Contains('/') || setupIntentId.Contains('\\') ||
            setupIntentId.Any(char.IsControl))
        {
            throw new ApiException(400, "INVALID_ARGUMENT", "setup_intent_id is invalid.");
        }

        var recurring = setupIntentId.StartsWith("recurring-setup-", StringComparison.Ordinal);
        var standing = setupIntentId.StartsWith("standing-setup-", StringComparison.Ordinal);
        var requiredOnce = setupIntentId.StartsWith("required-once-setup-", StringComparison.Ordinal);
        if (!recurring && !standing && !requiredOnce)
            throw PlanSetupNotFound();

        var waitMs = ParsePlanSetupWaitMs(req.Query.GetValueOrDefault("wait_ms"));
        var sinceStatus = req.Query.GetValueOrDefault("since_status");
        var sinceVersionText = req.Query.GetValueOrDefault("since_status_version") ??
                               req.Query.GetValueOrDefault("since_version");
        var sinceVersion = ParsePlanSetupSinceVersion(sinceVersionText, recurring, requiredOnce);

        if (recurring)
            return GetRecurringPlanSetup(setupIntentId, reqId, waitMs, sinceStatus, sinceVersion);
        if (requiredOnce)
            return GetRequiredOncePlanSetup(setupIntentId, reqId, waitMs, sinceStatus, sinceVersion);

        var state = ReadStandingPlanSetup(setupIntentId);

        if (waitMs > 0 && (sinceStatus is not null || sinceVersion is not null) &&
            IsSameState(state, sinceStatus, sinceVersion))
        {
            var deadline = Environment.TickCount64 + waitMs;
            while (Environment.TickCount64 < deadline)
            {
                if (_cts.IsCancellationRequested) break;
                Thread.Sleep(Math.Min(100, Math.Max(1, (int)(deadline - Environment.TickCount64))));
                state = ReadStandingPlanSetup(setupIntentId);
                if (!IsSameState(state, sinceStatus, sinceVersion))
                    break;
            }
        }

        return ApiResponse.Ok(
            StandingPlanResponse(
                state.SetupIntentId,
                state.StatusCode,
                state.StatusVersion,
                state.PlanId,
                state.OccurrenceId,
                state.LeaseId,
                state.RequiresLocalAction,
                state.NextAction,
                state.ReasonCode,
                $"{Prefix}/plan-setups/{Uri.EscapeDataString(state.SetupIntentId)}",
                state.StatusVersionCursor,
                state.RunId,
                state.RecordingStatusUrl,
                state.StartedAtUtc,
                state.CompletedAtUtc),
            reqId);
    }

    private string GetRecurringPlanSetup(string setupIntentId, string reqId, int waitMs,
        string? sinceStatus, string? sinceVersion)
    {
        var state = ReadRecurringPlanSetup(setupIntentId);
        if (waitMs > 0 && (sinceStatus is not null || sinceVersion is not null) &&
            IsSameRecurringState(state, sinceStatus, sinceVersion))
        {
            var deadline = Environment.TickCount64 + waitMs;
            while (Environment.TickCount64 < deadline)
            {
                if (_cts.IsCancellationRequested) break;
                Thread.Sleep(Math.Min(100, Math.Max(1, (int)(deadline - Environment.TickCount64))));
                state = ReadRecurringPlanSetup(setupIntentId);
                if (!IsSameRecurringState(state, sinceStatus, sinceVersion)) break;
            }
        }
        return ApiResponse.Ok(RecurringPlanSetupResponse(state), reqId);
    }

    private string GetRequiredOncePlanSetup(string setupIntentId, string reqId, int waitMs,
        string? sinceStatus, string? sinceVersion)
    {
        var state = ReadRequiredOncePlanSetup(setupIntentId);
        if (waitMs > 0 && (sinceStatus is not null || sinceVersion is not null) &&
            IsSameRequiredOnceState(state, sinceStatus, sinceVersion))
        {
            var deadline = Environment.TickCount64 + waitMs;
            while (Environment.TickCount64 < deadline)
            {
                if (_cts.IsCancellationRequested) break;
                Thread.Sleep(Math.Min(100, Math.Max(1, (int)(deadline - Environment.TickCount64))));
                state = ReadRequiredOncePlanSetup(setupIntentId);
                if (!IsSameRequiredOnceState(state, sinceStatus, sinceVersion)) break;
            }
        }
        return ApiResponse.Ok(RequiredOncePlanSetupResponse(state), reqId);
    }

    private StandingPlanSetupState ReadStandingPlanSetup(string id)
    {
        StandingPlanSetupState? state;
        try { state = _standingPlanSetupGateway?.Get(id); }
        catch { throw PlanSetupNotFound(); }
        if (state is null || !string.Equals(state.SetupIntentId, id, StringComparison.Ordinal))
            throw PlanSetupNotFound();
        return state;
    }

    private StandingPlanSetupState ReadRequiredOncePlanSetup(string id)
    {
        StandingPlanSetupState? state;
        try { state = _requiredOncePlanSetupGateway?.Get(id); }
        catch { throw PlanSetupNotFound(); }
        if (state is null || !string.Equals(state.SetupIntentId, id, StringComparison.Ordinal) ||
            !string.Equals(state.AuthorizationMode, "required", StringComparison.Ordinal) ||
            !TryParseRequiredOnceStatusVersionCursor(state.StatusVersionCursor, out var cursorVersion) ||
            cursorVersion != state.StatusVersion)
            throw PlanSetupNotFound();
        return state;
    }

    private AgentRecorder.Api.RecurringPlanSetupState ReadRecurringPlanSetup(string id)
    {
        AgentRecorder.Api.RecurringPlanSetupState? state;
        try { state = _recurringPlanSetupGateway?.Get(id); }
        catch { throw PlanSetupNotFound(); }
        if (state is null || !string.Equals(state.SetupIntentId, id, StringComparison.Ordinal))
            throw PlanSetupNotFound();
        if (!TryParseRecurringStatusVersionCursor(state.StatusVersionCursor, out var cursorVersion) ||
            cursorVersion != state.StatusVersion)
            throw new ApiException(500, "PLAN_SETUP_STATUS_UNAVAILABLE", "The recurring setup status is unavailable.");
        return state;
    }

    private static ApiException PlanSetupNotFound() =>
        new(404, "PLAN_SETUP_NOT_FOUND", "Unknown setup intent.");

    private static bool IsSameRecurringState(AgentRecorder.Api.RecurringPlanSetupState state,
        string? sinceStatus, string? sinceVersion) =>
        (sinceStatus is null || string.Equals(state.Status, sinceStatus, StringComparison.Ordinal)) &&
        (sinceVersion is null ||
         (long.TryParse(sinceVersion, NumberStyles.None, CultureInfo.InvariantCulture, out var numericVersion) &&
          state.StatusVersion <= numericVersion) ||
         (TryParseRecurringStatusVersionCursor(sinceVersion, out var cursorVersion) &&
          TryParseRecurringStatusVersionCursor(state.StatusVersionCursor, out var currentVersion) &&
          currentVersion <= cursorVersion));

    private static bool TryParseRecurringStatusVersionCursor(string? value, out long version)
    {
        const string prefix = "recurring-setup/v1:";
        version = 0;
        if (value is null || !value.StartsWith(prefix, StringComparison.Ordinal)) return false;
        var suffix = value[prefix.Length..];
        return long.TryParse(suffix, NumberStyles.None, CultureInfo.InvariantCulture, out version) &&
            version >= 0 && string.Equals(suffix, version.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal);
    }

    private static bool IsSameRequiredOnceState(StandingPlanSetupState state, string? sinceStatus, string? sinceVersion) =>
        (sinceStatus is null || string.Equals(state.StatusCode, sinceStatus, StringComparison.Ordinal)) &&
        (sinceVersion is null ||
         (long.TryParse(sinceVersion, NumberStyles.None, CultureInfo.InvariantCulture, out var numericVersion) && state.StatusVersion <= numericVersion) ||
         (TryParseRequiredOnceStatusVersionCursor(sinceVersion, out var cursor) && state.StatusVersion <= cursor));

    private static bool TryParseRequiredOnceStatusVersionCursor(string? value, out long version)
    {
        const string prefix = "required-once-setup/v1:";
        version = 0;
        if (value is null || !value.StartsWith(prefix, StringComparison.Ordinal)) return false;
        var suffix = value[prefix.Length..];
        return long.TryParse(suffix, NumberStyles.None, CultureInfo.InvariantCulture, out version) &&
            version >= 0 && string.Equals(suffix, version.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal);
    }

    private static object RecurringPlanSetupResponse(AgentRecorder.Api.RecurringPlanSetupState state) => new
    {
        setup_intent_id = state.SetupIntentId,
        status = state.Status,
        status_version = state.StatusVersion,
        status_version_cursor = state.StatusVersionCursor,
        status_url = $"{Prefix}/plan-setups/{Uri.EscapeDataString(state.SetupIntentId)}",
        plan_id = state.PlanId,
        occurrence_id = (string?)null,
        lease_id = state.LeaseId,
        requires_local_action = state.RequiresLocalAction,
        next_action = state.NextAction,
        reason_code = state.ReasonCode,
        run_id = (string?)null,
        recording_status_url = (string?)null,
    };

    private static object RequiredOncePlanSetupResponse(StandingPlanSetupState state) => new
    {
        setup_intent_id = state.SetupIntentId,
        status = ProjectRequiredOnceStatus(state.StatusCode),
        status_version = state.StatusVersion,
        status_version_cursor = state.StatusVersionCursor,
        status_url = $"{Prefix}/plan-setups/{Uri.EscapeDataString(state.SetupIntentId)}",
        plan_id = state.PlanId,
        occurrence_id = state.OccurrenceId,
        lease_id = (string?)null,
        authorization_mode = "required",
        requires_local_action = state.RequiresLocalAction,
        next_action = state.NextAction,
        reason_code = state.ReasonCode,
        run_id = (string?)null,
        recording_status_url = (string?)null,
        execution_supported = state.ExecutionSupported,
        requires_execution_confirmation = true,
        execution_limitation = state.ExecutionSupported ? null : "execution_runtime_unavailable",
    };

    private static string? NextRequiredOnceAction(string statusCode, bool executionSupported) => statusCode switch
    {
        "region_selection_pending" => "local_region_selection",
        "creation_approval_pending" => "local_plan_creation_approval",
        "scheduled" => executionSupported ? "required_once_natural_wake_execution" : "execution_runtime_unavailable",
        _ => null,
    };

    private static string ProjectRequiredOnceStatus(string statusCode) => statusCode switch
    {
        "region_selection_pending" => "pending_selection",
        "creation_approval_pending" => "pending_creation_approval",
        _ => statusCode,
    };

    private static bool IsSameState(StandingPlanSetupState state, string? sinceStatus, string? sinceVersion) =>
        (sinceStatus is null || string.Equals(state.StatusCode, sinceStatus, StringComparison.Ordinal)) &&
        (sinceVersion is null ||
         (state.StatusVersionCursor is not null &&
          StandingPlanStatusVersionCursor.TryParse(sinceVersion, out _) &&
          StandingPlanStatusVersionCursor.Compare(state.StatusVersionCursor, sinceVersion) <= 0) ||
         (long.TryParse(sinceVersion, NumberStyles.None, CultureInfo.InvariantCulture, out var numericVersion) &&
          state.StatusVersion <= numericVersion));

    private static string? NextActionFor(string statusCode) => statusCode switch
    {
        "setup_pending" => "local_region_selection",
        "pending_lease_approval" => "local_lease_approval",
        "scheduled" => "natural_wake",
        _ => null,
    };

    private static object StandingPlanResponse(
        string? setupIntentId,
        string statusCode,
        long statusVersion,
        string? planId,
        string? occurrenceId,
        string? leaseId,
        bool requiresLocalAction,
        string? nextAction,
        string? reasonCode,
        string statusUrl,
        string? statusVersionCursor = null,
        string? runId = null,
        string? recordingStatusUrl = null,
        DateTimeOffset? startedAtUtc = null,
        DateTimeOffset? completedAtUtc = null) => new
        {
            setup_intent_id = setupIntentId,
            status = statusCode,
            status_version = statusVersion,
            status_version_cursor = statusVersionCursor,
            status_url = statusUrl,
            plan_id = planId,
            occurrence_id = occurrenceId,
            lease_id = leaseId,
            requires_local_action = requiresLocalAction,
            next_action = nextAction,
            reason_code = reasonCode,
            run_id = runId,
            recording_status_url = recordingStatusUrl,
            started_at = startedAtUtc?.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
            completed_at = completedAtUtc?.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
        };

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
        var creationWaitMs = ParseCreationWaitMs(req.Query);
        var agent = req.Headers.GetValueOrDefault("X-Agent-Name") ?? "unknown";
        var traceId = "trace_" + Guid.NewGuid().ToString("N")[..16];
        var clientSentAtUtc = req.Headers.GetValueOrDefault("X-Agent-Sent-At");
        const string endpoint = "recordings";
        ConsumeEnsureContextAndAssociate(req, traceId);
        _tracer.IntentAccepted(traceId, endpoint, clientSentAtUtc);

        bool isProfileRequest = ContainsProfileRefProperty(reqBody);
        JsonNode cfg;
        if (isProfileRequest)
        {
            // The profile parser below uses JsonDocument so duplicate properties
            // remain observable instead of being collapsed by JsonNode.
            cfg = new JsonObject();
        }
        else
        {
            try
            {
                cfg = JsonNode.Parse(string.IsNullOrWhiteSpace(reqBody) ? "{}" : reqBody)
                      ?? throw new ApiException(400, "INVALID_ARGUMENT", "Body required");
            }
            catch
            {
                _tracer.IntentValidated(traceId, endpoint, success: false, errorCode: "INVALID_ARGUMENT");
                throw new ApiException(400, "INVALID_ARGUMENT", "Invalid JSON body");
            }
        }

        object result;
        try
        {
            if (isProfileRequest)
            {
                var profileRef = FixedRegionProfileRecordingRequest.Parse(reqBody);
                var gateway = _profileManagementGateway ?? throw new ApiException(503,
                    "PROFILE_EXECUTION_UNAVAILABLE", "Fixed-region profile execution is unavailable.",
                    new { field = "profile_ref", reason_code = "profile_gateway_unavailable" });
                FixedRegionProfileExactVersionSnapshot? snapshot;
                try { snapshot = gateway.ReadExactVersionSnapshot(profileRef.ProfileId, profileRef.ProfileVersion); }
                catch (Exception ex)
                {
                    _audit.Log("recording.profile_snapshot_read_failed", new
                    {
                        profile_id = profileRef.ProfileId,
                        profile_version = profileRef.ProfileVersion,
                        exception_type = ex.GetType().Name
                    });
                    throw new ApiException(503, "PROFILE_EXECUTION_UNAVAILABLE",
                        "The exact fixed-region profile snapshot could not be read.",
                        new { field = "profile_ref", reason_code = "profile_snapshot_unavailable" });
                }
                if (snapshot?.Version is null)
                    throw new ApiException(404, "PROFILE_VERSION_NOT_FOUND",
                        "The referenced fixed-region profile version does not exist.",
                        new { field = "profile_ref", reason_code = "profile_version_not_found" });
                if (snapshot.Directory.IsDeleted)
                    throw new ApiException(409, "PROFILE_DELETED",
                        "The fixed-region profile has been deleted.",
                        new { field = "profile_ref", reason_code = "profile_deleted" });
                if (!profileRef.Matches(snapshot.Version))
                    throw new ApiException(409, "PROFILE_REF_MISMATCH",
                        "The supplied profile reference does not match the immutable profile version.",
                        new { field = "profile_ref.digest", reason_code = "profile_digest_mismatch" });

                var profile = snapshot.Version;
                ValidateInteractiveProfile(profile);
                string? ValidateEnvironment() => ValidateProfileEnvironment(profile, _profileDisplayEnvironmentProvider);
                var config = BuildInteractiveProfileConfig(profile);
                result = _engine.CreateRecordingFromFixedRegionProfile(
                    config, agent, _tray, profileRef, ValidateEnvironment, traceId, endpoint);
            }
            else
            {
                result = _engine.CreateRecording(cfg, agent, _tray, traceId, endpoint);
            }
        }
        catch (ApiException ex)
        {
            // Engine is the owner of validation events for failures that occur inside
            // CreateRecording. Only record here if the engine did not already do so.
            if (!_tracer.HasValidationResult(traceId))
                _tracer.IntentValidated(traceId, endpoint, success: false, errorCode: ex.Code);
            throw;
        }

        return ApiResponse.Ok(ApplyCreationWait(result, creationWaitMs), reqId);
    }

    private static bool ContainsProfileRefProperty(string body)
    {
        try
        {
            using var document = JsonDocument.Parse(string.IsNullOrWhiteSpace(body) ? "{}" : body);
            return document.RootElement.ValueKind == JsonValueKind.Object &&
                   document.RootElement.EnumerateObject().Any(property => property.Name == "profile_ref");
        }
        catch { return false; }
    }

    private static void ValidateInteractiveProfile(RecurringFixedRegionProfileVersion profile)
    {
        if (Environment.GetEnvironmentVariable("AGENT_RECORDER_TEST_MODE") == "1")
            throw new ApiException(503, "PROFILE_EXECUTION_UNAVAILABLE",
                "Fixed-region profile execution is disabled in test mode.",
                new { field = "profile_ref", reason_code = "test_mode_forbidden" });
        if (profile.TargetType != AuthorizedScopeTargetType.FixedRegion ||
            profile.RebindPolicy != RecurringFixedRegionRebindPolicy.ExactMatchOnly ||
            profile.CaptureSemantics != AuthorizedCaptureSemantics.DesktopRegion ||
            profile.CoordinateSpace != AuthorizedCoordinateSpace.PhysicalVirtualScreen ||
            profile.DisplayIdentityStatus != AuthorizedDisplayIdentityStatus.Resolved ||
            profile.Backend != AuthorizedCaptureBackend.FfmpegRegion ||
            profile.AudioMode != AuthorizedAudioMode.None ||
            profile.OutputConflictPolicy != AuthorizedOutputConflictPolicy.FailIfExists)
            throw new ApiException(422, "PROFILE_NOT_EXECUTABLE",
                "The selected profile contains settings unsupported for interactive recording.",
                new { field = "profile_ref", reason_code = "unsupported_profile_policy" });

        if (profile.Duration.Ticks % TimeSpan.TicksPerSecond != 0 ||
            profile.Duration.TotalSeconds is < 1 or > 600)
            throw new ApiException(422, "PROFILE_NOT_EXECUTABLE",
                "The profile duration must be a whole number of seconds from 1 through 600.",
                new { field = "profile_ref.duration", reason_code = "unsupported_duration_precision" });
        if (profile.CountdownSeconds is < 0 or > 10)
            throw new ApiException(422, "PROFILE_NOT_EXECUTABLE",
                "The profile countdown is outside the supported interactive range.",
                new { field = "profile_ref.countdown_seconds", reason_code = "unsupported_countdown" });
        if ((profile.RegionWithinDisplay.Width & 1) != 0 || (profile.RegionWithinDisplay.Height & 1) != 0 ||
            profile.RegionWithinDisplay.Width < 32 || profile.RegionWithinDisplay.Height < 32)
            throw new ApiException(422, "PROFILE_NOT_EXECUTABLE",
                "The fixed region must have even coordinates and dimensions and be at least 32 by 32 pixels.",
                new { field = "profile_ref.region", reason_code = "unsupported_region_geometry" });
        if (!Path.IsPathFullyQualified(profile.OutputDirectory))
            throw new ApiException(422, "PROFILE_NOT_EXECUTABLE",
                "The profile output directory must be absolute.",
                new { field = "profile_ref.output_directory", reason_code = "absolute_path_required" });
        try { _ = profile.VirtualScreenRegion; }
        catch (Phase3DomainException)
        {
            throw new ApiException(422, "PROFILE_NOT_EXECUTABLE",
                "The fixed-region coordinates overflow the physical desktop coordinate range.",
                new { field = "profile_ref.region", reason_code = "region_coordinate_overflow" });
        }
    }

    private static string? ValidateProfileEnvironment(
        RecurringFixedRegionProfileVersion profile,
        IFixedRegionProfileDisplayEnvironmentProvider provider)
    {
        IReadOnlyList<StandingLeaseDisplayMetadata> displays;
        try { displays = provider.GetExecutionMetadata(); }
        catch { return "display_topology_unavailable"; }
        if (!StandingLeaseDisplayTopologyDigest.TryCompute(displays, out var digest))
            return "display_topology_incomplete";
        if (!string.Equals(digest, profile.TopologyDigest, StringComparison.Ordinal))
            return "display_topology_changed";
        var matches = displays.Where(display => string.Equals(
            display.StableDisplayFingerprint, profile.StableDisplayFingerprint, StringComparison.Ordinal)).ToArray();
        if (matches.Length != 1) return "display_identity_not_unique";
        var current = matches[0];
        if (current.IdentityStatus != DisplayIdentityResolutionStatus.Resolved || current.PhysicalBounds is null ||
            current.DpiX != profile.DpiX || current.DpiY != profile.DpiY ||
            current.PhysicalWidth != profile.PhysicalWidth || current.PhysicalHeight != profile.PhysicalHeight ||
            current.Orientation != profile.Orientation)
            return "display_metadata_changed";
        var expectedBounds = profile.DisplayBounds;
        if (current.PhysicalBounds.Value != expectedBounds) return "display_bounds_changed";
        var region = profile.RegionWithinDisplay;
        long right, bottom, displayRight, displayBottom;
        try
        {
            right = checked((long)region.X + region.Width);
            bottom = checked((long)region.Y + region.Height);
            displayRight = checked((long)expectedBounds.Width);
            displayBottom = checked((long)expectedBounds.Height);
        }
        catch (OverflowException) { return "region_coordinate_overflow"; }
        if (region.X < 0 || region.Y < 0 || right > displayRight || bottom > displayBottom)
            return "region_outside_display";
        try { _ = profile.VirtualScreenRegion; }
        catch (Phase3DomainException) { return "region_coordinate_overflow"; }
        return null;
    }

    private JsonNode BuildInteractiveProfileConfig(RecurringFixedRegionProfileVersion profile)
    {
        var bounds = profile.VirtualScreenRegion;
        DisplayTopologySnapshot display;
        try
        {
            var matches = _profileDisplayEnvironmentProvider.GetTopology()
                .Where(item => string.Equals(item.StableIdentity, profile.StableDisplayFingerprint, StringComparison.Ordinal)).ToArray();
            if (matches.Length != 1) throw new InvalidOperationException("Display identity is not unique.");
            display = matches[0];
        }
        catch
        {
            throw new ApiException(409, "PROFILE_ENVIRONMENT_CHANGED",
                "The profile display is no longer uniquely available.",
                new { field = "profile_ref", reason_code = "display_identity_not_unique" });
        }
        if (string.IsNullOrWhiteSpace(display.PublicId))
            throw new ApiException(409, "PROFILE_ENVIRONMENT_CHANGED",
                "The profile display is no longer uniquely available.",
                new { field = "profile_ref", reason_code = "display_identity_not_unique" });
        return new JsonObject
        {
            ["source"] = new JsonObject
            {
                ["type"] = "region", ["display_id"] = display.PublicId,
                ["coordinate_space"] = "virtual_screen",
                ["bounds"] = new JsonObject
                {
                    ["x"] = bounds.X, ["y"] = bounds.Y,
                    ["width"] = bounds.Width, ["height"] = bounds.Height
                }
            },
            ["countdown_seconds"] = profile.CountdownSeconds,
            ["stop_condition"] = new JsonObject { ["type"] = "duration", ["seconds"] = (int)profile.Duration.TotalSeconds },
            ["output"] = new JsonObject
            {
                ["directory"] = profile.OutputDirectory,
                ["filename_template"] = profile.FilenamePrefix + "-{datetime}-{id}",
                ["conflict_policy"] = "fail"
            },
            ["video"] = new JsonObject { ["fps"] = 30, ["quality"] = "medium" }
        };
    }

    private string CreateRegionSelection(HttpRequest req, string reqBody, string reqId)
    {
        JsonNode body = JsonNode.Parse(string.IsNullOrWhiteSpace(reqBody) ? "{}" : reqBody)
                        ?? throw new ApiException(400, "INVALID_ARGUMENT", "Body required");

        var purpose = body["purpose"]?.GetValue<string>() ?? "recording";
        if (purpose is not ("recording" or "profile"))
            throw new ApiException(400, "INVALID_ARGUMENT", $"purpose '{purpose}' not supported");
        var profilePurpose = purpose == "profile";
        if (profilePurpose && (_profileManagementGateway is not IFixedRegionProfileSelectionCreationGateway ||
                               !_tray.SupportsRegionSelectionUi))
            throw new ApiException(503, "PROFILE_SELECTION_UNAVAILABLE",
                "Interactive profile selection is unavailable on this host.",
                new { reason_code = "interactive_selector_unavailable", suggested_action = "use_an_interactive_desktop" });

        var timeoutSeconds = body["timeout_seconds"]?.GetValue<int?>() ?? 120;
        if (timeoutSeconds < 10 || timeoutSeconds > 600)
            throw new ApiException(400, "INVALID_ARGUMENT",
                "timeout_seconds must be between 10 and 600");

        // 使用 TaskCompletionSource 等待 UI 线程回调
        var tcs = new TaskCompletionSource<(string status, int x, int y, int w, int h, string displayId, string coordSpace)>();

        _tray.RequestRegionSelection(timeoutSeconds, (status, x, y, w, h, displayId, coordSpace) =>
        {
            tcs.TrySetResult((status, x, y, w, h, displayId, coordSpace));
        }, purpose);

        // 等待结果（带整体超时保护）
        var timeoutTask = Task.Delay((timeoutSeconds + 10) * 1000);
        var completed = Task.WaitAny(tcs.Task, timeoutTask);

        if (completed == 1)
            throw new ApiException(504, "SELECTION_TIMEOUT", "Region selection timed out");

        var result = tcs.Task.Result;

        if (result.status == "selected")
        {
            if (profilePurpose)
            {
                if (!TrySnapshotProfileSelection(result.x, result.y, result.w, result.h, result.coordSpace,
                        out var snapshot, out var reason))
                    throw new ApiException(422, "PROFILE_SELECTION_NOT_EXECUTABLE",
                        "The selected region is not a supported fixed-display profile region.",
                        new { reason_code = reason, suggested_action = "reselect_a_single_display_even_sized_region" });
                snapshot = snapshot with { ExpiresAtUtc = DateTimeOffset.UtcNow + FixedRegionProfileSelectionCache.Lifetime };
                if (!_profileSelectionCache.TryAdd(snapshot, out var selectionRef))
                    throw new ApiException(429, "PROFILE_SELECTION_CACHE_FULL",
                        "Too many unexpired profile selections are pending.",
                        new { reason_code = "selection_cache_full", suggested_action = "create_a_profile_or_wait_for_expiry" });
                return ApiResponse.Ok(new
                {
                    status = "selected", display_id = result.displayId, coordinate_space = result.coordSpace,
                    bounds = new { x = result.x, y = result.y, width = result.w, height = result.h },
                    selection_ref = selectionRef, expires_at = snapshot.ExpiresAtUtc
                }, reqId);
            }

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

    private bool TrySnapshotProfileSelection(int x, int y, int width, int height, string coordinateSpace,
        out FixedRegionProfileSelectionSnapshot snapshot, out string reason)
    {
        snapshot = null!;
        reason = "selection_environment_unavailable";
        if (!string.Equals(coordinateSpace, "virtual_screen", StringComparison.Ordinal) ||
            width < 32 || height < 32 || (width & 1) != 0 || (height & 1) != 0)
        {
            reason = "selection_geometry_invalid";
            return false;
        }

        IReadOnlyList<StandingLeaseDisplayMetadata> displays;
        try { displays = _profileDisplayEnvironmentProvider.GetExecutionMetadata(); }
        catch { return false; }
        if (!StandingLeaseDisplayTopologyDigest.TryCompute(displays, out var topologyDigest))
        {
            reason = "display_topology_incomplete";
            return false;
        }

        AuthorizedPhysicalRectangle selected;
        try { selected = new AuthorizedPhysicalRectangle(x, y, width, height); }
        catch (ArgumentException) { reason = "selection_geometry_invalid"; return false; }
        var matches = displays.Where(item => item.PhysicalBounds is { } bounds && Contains(bounds, selected)).ToArray();
        if (matches.Length != 1)
        {
            reason = matches.Length == 0 ? "selection_crosses_display_or_outside" : "display_identity_not_unique";
            return false;
        }
        var display = matches[0];
        if (display.IdentityStatus != DisplayIdentityResolutionStatus.Resolved ||
            string.IsNullOrWhiteSpace(display.StableDisplayFingerprint) || display.PhysicalBounds is null ||
            display.DpiX is not > 0 || display.DpiY is not > 0 || display.PhysicalWidth is not > 0 ||
            display.PhysicalHeight is not > 0 || display.Orientation is null)
        {
            reason = "display_identity_unresolved";
            return false;
        }
        try
        {
            var bounds = display.PhysicalBounds.Value;
            var relativeX = checked((long)x - bounds.X);
            var relativeY = checked((long)y - bounds.Y);
            if (relativeX < 0 || relativeY < 0 || relativeX > int.MaxValue || relativeY > int.MaxValue ||
                relativeX + width > bounds.Width || relativeY + height > bounds.Height)
            {
                reason = "selection_crosses_display_or_outside";
                return false;
            }
            snapshot = new FixedRegionProfileSelectionSnapshot(selected, display.StableDisplayFingerprint,
                bounds, display.DpiX.Value, display.DpiY.Value, display.PhysicalWidth.Value,
                display.PhysicalHeight.Value, display.Orientation.Value, topologyDigest, DateTimeOffset.MinValue);
            return true;
        }
        catch (OverflowException) { reason = "selection_geometry_overflow"; return false; }
    }

    private static bool Contains(AuthorizedPhysicalRectangle outer, AuthorizedPhysicalRectangle inner)
    {
        var right = (long)inner.X + inner.Width;
        var bottom = (long)inner.Y + inner.Height;
        return inner.X >= outer.X && inner.Y >= outer.Y &&
               right <= (long)outer.X + outer.Width && bottom <= (long)outer.Y + outer.Height;
    }

    private string? ValidateSelectionSnapshot(FixedRegionProfileSelectionSnapshot snapshot)
    {
        IReadOnlyList<StandingLeaseDisplayMetadata> displays;
        try { displays = _profileDisplayEnvironmentProvider.GetExecutionMetadata(); }
        catch { return "display_topology_unavailable"; }
        if (!StandingLeaseDisplayTopologyDigest.TryCompute(displays, out var digest)) return "display_topology_incomplete";
        if (!string.Equals(digest, snapshot.TopologyDigest, StringComparison.Ordinal)) return "display_topology_changed";
        var matches = displays.Where(item => string.Equals(item.StableDisplayFingerprint,
            snapshot.StableDisplayFingerprint, StringComparison.Ordinal)).ToArray();
        if (matches.Length != 1) return "display_identity_not_unique";
        var display = matches[0];
        if (display.IdentityStatus != DisplayIdentityResolutionStatus.Resolved ||
            display.PhysicalBounds != snapshot.DisplayBounds || display.DpiX != snapshot.DpiX ||
            display.DpiY != snapshot.DpiY || display.PhysicalWidth != snapshot.PhysicalWidth ||
            display.PhysicalHeight != snapshot.PhysicalHeight || display.Orientation != snapshot.Orientation)
            return "display_metadata_changed";
        return Contains(snapshot.DisplayBounds, snapshot.VirtualBounds) ? null : "selection_outside_display";
    }

    private string CreateQuickRecording(HttpRequest req, string reqBody, string reqId)
    {
        var creationWaitMs = ParseCreationWaitMs(req.Query);
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
            RejectWindowSurfaceIntentOutsideOrdinaryRecording(body);
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

        if (targetNode is not JsonObject targetObject)
        {
            _tracer.IntentValidated(traceId, endpoint, success: false, errorCode: "INVALID_ARGUMENT");
            throw new ApiException(400, "INVALID_ARGUMENT", "target must be a JSON object");
        }

        string? targetType = null;
        try
        {
            if (targetObject["type"] is JsonValue typeValue)
            {
                var typeElement = typeValue.GetValue<JsonElement>();
                if (typeElement.ValueKind == JsonValueKind.String)
                    targetType = typeElement.GetString();
            }
        }
        catch { }
        if (string.IsNullOrWhiteSpace(targetType))
        {
            _tracer.IntentValidated(traceId, endpoint, success: false, errorCode: "INVALID_ARGUMENT");
            throw new ApiException(400, "INVALID_ARGUMENT", "target.type is required");
        }

        int? windowsDisplayNumber = null;
        if (string.Equals(targetType, "windows_display", StringComparison.Ordinal))
        {
            try
            {
                windowsDisplayNumber = ParseWindowsDisplayNumber(targetObject);
            }
            catch (ApiException ex)
            {
                _tracer.IntentValidated(traceId, endpoint, success: false, errorCode: ex.Code);
                throw;
            }
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
                        return ApiResponse.Ok(ApplyCreationWait(data, creationWaitMs), reqId);
                    }

                case "windows_display":
                    {
                        var display = ResolveWindowsDisplay(windowsDisplayNumber!.Value);
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
                            ["display_id"] = display.id,
                            ["windows_display_number"] = display.windows_display_number,
                            ["name"] = display.name,
                            ["is_primary"] = display.is_primary,
                            ["bounds"] = new JsonObject
                            {
                                ["x"] = display.bounds.x,
                                ["y"] = display.bounds.y,
                                ["width"] = display.bounds.width,
                                ["height"] = display.bounds.height
                            }
                        };
                        var data = AddQuickMetadataToObject(result, "windows_display", resolved, true);
                        return ApiResponse.Ok(ApplyCreationWait(data, creationWaitMs), reqId);
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
                        return ApiResponse.Ok(ApplyCreationWait(data, creationWaitMs), reqId);
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
                        return ApiResponse.Ok(ApplyCreationWait(data, creationWaitMs), reqId);
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
                        return ApiResponse.Ok(ApplyCreationWait(data, creationWaitMs), reqId);
                    }

                default:
                    throw new ApiException(400, "INVALID_ARGUMENT",
                        $"target.type '{targetType}' is not supported. Supported: primary_display, windows_display, active_window, selected_region, last_region");
            }
        }
        catch (ApiException ex)
        {
            if (!_tracer.HasValidationResult(traceId))
                _tracer.IntentValidated(traceId, endpoint, success: false, errorCode: ex.Code);
            throw;
        }
    }

    internal static void RejectWindowSurfaceIntentOutsideOrdinaryRecording(string requestBody)
    {
        JsonDocument document;
        try { document = JsonDocument.Parse(requestBody); }
        catch { return; }
        using (document)
        {
            if (document.RootElement.ValueKind == JsonValueKind.Object &&
                document.RootElement.TryGetProperty("required_capture_semantics", out _))
                throw new ApiException(400, "INVALID_ARGUMENT",
                    "required_capture_semantics is supported only by POST /api/v1/recordings.");
        }
    }

    private static void RejectWindowSurfaceIntentOutsideOrdinaryRecording(JsonNode body)
    {
        if (body is JsonObject obj && obj.ContainsKey("required_capture_semantics"))
            throw new ApiException(400, "INVALID_ARGUMENT",
                "required_capture_semantics is supported only by POST /api/v1/recordings.");
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

    private static int ParseWindowsDisplayNumber(JsonObject target)
    {
        if (target["windows_display_number"] is not JsonValue value)
            throw InvalidWindowsDisplayNumber();

        try
        {
            var element = value.GetValue<JsonElement>();
            var raw = element.GetRawText();
            if (element.ValueKind == JsonValueKind.Number
                && raw.Length > 0
                && raw.All(character => character is >= '0' and <= '9')
                && element.TryGetInt32(out var number)
                && number > 0)
            {
                return number;
            }
        }
        catch { }

        throw InvalidWindowsDisplayNumber();
    }

    private static ApiException InvalidWindowsDisplayNumber()
        => new(400, "INVALID_ARGUMENT",
            "target.windows_display_number must be a positive JSON integer.",
            new
            {
                field = "target.windows_display_number",
                reason = "must_be_positive_json_integer",
                suggested_action = "use_the_positive_number_shown_by_windows_identify"
            });

    private static SystemQuery.DisplayInfo ResolveWindowsDisplay(int windowsDisplayNumber)
    {
        List<SystemQuery.DisplayInfo> displays;
        try
        {
            // This is the only display snapshot consumed by the quick resolver.
            // The selected API ID and all public metadata below come from this
            // same list; do not reconstruct a target from another enumeration.
            displays = SystemQuery.EnumDisplays();
        }
        catch
        {
            throw WindowsDisplaySourceNotFound(windowsDisplayNumber);
        }

        var matches = displays
            .Where(display => display.windows_display_number == windowsDisplayNumber)
            .ToArray();
        if (matches.Length != 1)
            throw WindowsDisplaySourceNotFound(windowsDisplayNumber);

        return matches[0];
    }

    private static ApiException WindowsDisplaySourceNotFound(int windowsDisplayNumber)
        => new(400, "SOURCE_NOT_FOUND",
            "The requested Windows display number is not available as a unique current display.",
            new
            {
                requested_windows_display_number = windowsDisplayNumber,
                suggested_action = "refresh_displays_or_ask_user_to_disambiguate"
            });

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

    private static int? ParseCreationWaitMs(IReadOnlyDictionary<string, string> query)
    {
        const int maxWaitMs = 25000;

        if (!query.TryGetValue("wait_for", out var waitFor))
        {
            if (query.ContainsKey("wait_ms"))
            {
                throw new ApiException(400, "INVALID_ARGUMENT",
                    "wait_ms requires wait_for=recording.",
                    new { field = "wait_ms", reason = "requires_wait_for" });
            }

            return null;
        }

        if (!string.Equals(waitFor, "recording", StringComparison.Ordinal))
        {
            throw new ApiException(400, "INVALID_ARGUMENT",
                "wait_for must be 'recording'.",
                new { field = "wait_for", allowed = new[] { "recording" } });
        }

        if (!query.TryGetValue("wait_ms", out var rawWaitMs))
            return maxWaitMs;

        if (!int.TryParse(rawWaitMs, NumberStyles.None, CultureInfo.InvariantCulture, out var waitMs)
            || waitMs < 1 || waitMs > maxWaitMs)
        {
            throw new ApiException(400, "INVALID_ARGUMENT",
                "wait_ms must be an integer between 1 and 25000.",
                new { field = "wait_ms", min = 1, max = maxWaitMs });
        }

        return waitMs;
    }

    private object ApplyCreationWait(object createResult, int? waitMs)
    {
        if (!waitMs.HasValue)
            return createResult;

        var node = JsonSerializer.SerializeToNode(createResult, ApiResponse.Json) as JsonObject;
        if (node == null || !TryGetRecordingId(node, out var recordingId))
            return createResult;

        var waitResult = _engine.GetCreationRecordingWait(recordingId, waitMs.Value);
        var waitNode = JsonSerializer.SerializeToNode(waitResult, ApiResponse.Json) as JsonObject;
        if (waitNode == null)
            return createResult;

        foreach (var property in waitNode)
            node[property.Key] = property.Value?.DeepClone();

        return node;
    }

    private static bool TryGetRecordingId(JsonObject node, out string recordingId)
    {
        recordingId = string.Empty;
        try
        {
            var value = node["recording_id"];
            if (value is not JsonValue jsonValue || !jsonValue.TryGetValue<string>(out var parsed)
                || string.IsNullOrWhiteSpace(parsed))
            {
                return false;
            }

            recordingId = parsed;
            return true;
        }
        catch
        {
            return false;
        }
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

    private static int ParsePlanSetupWaitMs(string? value)
    {
        if (string.IsNullOrEmpty(value)) return 0;
        if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var ms) || ms < 0)
            throw new ApiException(400, "INVALID_ARGUMENT", "wait_ms must be a non-negative integer.");
        return Math.Min(ms, 25000);
    }

    private static string? ParsePlanSetupSinceVersion(string? value, bool recurring, bool requiredOnce = false)
    {
        if (string.IsNullOrEmpty(value)) return null;
        if (long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var version) && version >= 0)
            return version.ToString(CultureInfo.InvariantCulture);
        if (recurring ? TryParseRecurringStatusVersionCursor(value, out _) :
            requiredOnce ? TryParseRequiredOnceStatusVersionCursor(value, out _) : StandingPlanStatusVersionCursor.TryParse(value, out _))
            return value;
        throw new ApiException(400, "INVALID_ARGUMENT",
            recurring
                ? "since_status_version must be a non-negative integer or a canonical recurring setup cursor."
                : requiredOnce
                    ? "since_status_version must be a non-negative integer or a canonical required-once setup cursor."
                    : "since_status_version must be a non-negative integer or a valid standing setup cursor.");
    }

    private bool RecurringInteractiveDesktopAvailable()
    {
        try { return _recurringPlanSetupGateway?.IsInteractiveDesktopAvailable == true; }
        catch { return false; }
    }

    private bool RequiredOnceInteractiveDesktopAvailable()
    {
        try { return _requiredOncePlanSetupGateway?.IsInteractiveDesktopAvailable == true; }
        catch { return false; }
    }

    private bool RecurringUnattendedEnabled()
    {
        try { return _recurringPlanSetupGateway?.IsUnattendedEnabled == true; }
        catch { return false; }
    }

    private bool TryRouteFixedRegionProfiles(
        string sub,
        string method,
        HttpRequest request,
        string body,
        string requestId,
        ref int status,
        Dictionary<string, string> responseHeaders,
        out string response)
    {
        response = string.Empty;
        var gateway = _profileManagementGateway;
        if (sub == "/profiles" && method == "POST")
        {
            if (request.HasDuplicateHeader("Idempotency-Key"))
                throw ProfileInvalid("Exactly one Idempotency-Key header is required.");
            var parsed = FixedRegionProfileApiRequestParser.ParseCreate(body,
                request.Headers.GetValueOrDefault("Idempotency-Key"));
            if (gateway is null) throw ProfileManagementUnavailable();
            try
            {
                FixedRegionProfileCreateResult created;
                if (parsed.SourceRef is { } sourceRef)
                {
                    created = gateway.CreateFromExisting(parsed.IdempotencyKey, parsed.RequestHash,
                        sourceRef, parsed.Name, parsed.Changes, DateTimeOffset.UtcNow);
                }
                else
                {
                    var selectionGateway = gateway as IFixedRegionProfileSelectionCreationGateway;
                    if (selectionGateway is null)
                        throw new ApiException(503, "PROFILE_SELECTION_UNAVAILABLE",
                            "Selection-based profile creation is unavailable.",
                            new { reason_code = "selection_creation_unavailable", suggested_action = "use_an_interactive_desktop" });

                    // Replays are resolved before checking the ephemeral selection or current desktop.
                    var replay = selectionGateway.ReadCreateReplay(parsed.IdempotencyKey, parsed.RequestHash);
                    if (replay is not null)
                        created = replay;
                    else if (parsed.SourceSelectionRef is null ||
                             !_profileSelectionCache.TryGet(parsed.SourceSelectionRef, out var selection))
                        throw new ApiException(409, "PROFILE_SELECTION_INVALID",
                            "The profile selection reference is unknown or expired.",
                            new { reason_code = "selection_ref_invalid_or_expired", suggested_action = "select_the_region_again" });
                    else
                    {
                        var environmentFailure = ValidateSelectionSnapshot(selection);
                        if (environmentFailure is not null)
                            throw new ApiException(409, "PROFILE_SELECTION_ENVIRONMENT_CHANGED",
                                "The display environment changed after selection.",
                                new { reason_code = environmentFailure, suggested_action = "select_the_region_again" });
                        var outputDirectory = parsed.SelectionOutputDirectory ?? OutputSettingsStore.GetEffectiveDefaultOutputDir();
                        outputDirectory = AuthorizedFixedRegionScope.NormalizeOutputDirectoryForAuthorization(outputDirectory);
                        var virtualBounds = selection.VirtualBounds;
                        var relativeX = checked((long)virtualBounds.X - selection.DisplayBounds.X);
                        var relativeY = checked((long)virtualBounds.Y - selection.DisplayBounds.Y);
                        if (relativeX < 0 || relativeY < 0 || relativeX > int.MaxValue || relativeY > int.MaxValue)
                            throw new ApiException(422, "PROFILE_SELECTION_NOT_EXECUTABLE",
                                "The selected region is outside the target display.",
                                new { reason_code = "selection_geometry_invalid", suggested_action = "select_the_region_again" });
                        var specification = new RecurringFixedRegionProfileSpecification(
                            AuthorizedScopeTargetType.FixedRegion,
                            RecurringFixedRegionRebindPolicy.ExactMatchOnly,
                            AuthorizedCaptureSemantics.DesktopRegion,
                            AuthorizedCoordinateSpace.PhysicalVirtualScreen,
                            AuthorizedDisplayIdentityStatus.Resolved,
                            selection.StableDisplayFingerprint,
                            selection.DisplayBounds,
                            new AuthorizedPhysicalRectangle((int)relativeX, (int)relativeY,
                                virtualBounds.Width, virtualBounds.Height),
                            selection.DpiX, selection.DpiY, selection.PhysicalWidth, selection.PhysicalHeight,
                            selection.Orientation, selection.TopologyDigest,
                            AuthorizedCaptureBackend.FfmpegRegion, AuthorizedAudioMode.None,
                            TimeSpan.FromSeconds(parsed.SelectionDurationSeconds!.Value),
                            parsed.SelectionCountdownSeconds ?? 3, outputDirectory,
                            parsed.SelectionFilenamePrefix ?? "recording",
                            AuthorizedOutputConflictPolicy.FailIfExists, AuthorizedWakePolicy.NaturalWakeOnly,
                            AuthorizedDesktopRequirement.InteractiveDesktopRequired);
                        created = selectionGateway.CreateFromSelection(parsed.IdempotencyKey, parsed.RequestHash,
                            parsed.Name, specification, DateTimeOffset.UtcNow);
                    }
                }
                var etag = created.ETag;
                responseHeaders["ETag"] = etag;
                status = 201;
                if (!created.Replayed)
                    _audit.Log("profile.created", new { profile_id = created.Profile.ProfileId,
                        profile_version = created.Profile.CurrentVersion.ProfileVersion,
                        profile_digest = created.Profile.CurrentVersion.ProfileDigest });
                response = ApiResponse.Ok(new
                {
                    profile = ProfileEntry(created.Profile),
                    etag,
                    idempotent_replay = created.Replayed
                }, requestId);
                return true;
            }
            catch (Exception exception) { throw MapProfileException(exception); }
        }

        if (sub == "/profiles" && method == "GET")
        {
            if (gateway is null) throw ProfileManagementUnavailable();
            EnsureOnlyQuery(request, "limit", "cursor", "include_deleted");
            var limit = ParseLimit(request.Query.GetValueOrDefault("limit"));
            var cursor = DecodeProfileCursor(request.Query.GetValueOrDefault("cursor"));
            var includeDeleted = ParseOptionalBoolean(request.Query.GetValueOrDefault("include_deleted"), "include_deleted");
            try
            {
                var page = gateway.List(limit, cursor, includeDeleted);
                response = ApiResponse.Ok(new
                {
                    profiles = page.Items.Select(ProfileEntry).ToArray(),
                    limit,
                    next_cursor = page.NextCursor is null ? null : EncodeCursor("p1", page.NextCursor),
                    include_deleted = includeDeleted
                }, requestId);
                return true;
            }
            catch (Exception exception) { throw MapProfileException(exception); }
        }

        if (!sub.StartsWith("/profiles/", StringComparison.Ordinal)) return false;
        if (gateway is null) throw ProfileManagementUnavailable();
        var segments = sub.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length is < 2 or > 4 || segments[0] != "profiles") return false;
        var profileId = DecodeProfileId(segments[1]);

        if (segments.Length == 2 && method == "GET")
        {
            EnsureOnlyQuery(request);
            try
            {
                var profile = gateway.Get(profileId) ?? throw ProfileNotFound();
                var etag = FixedRegionProfileETag.Compute(profile);
                responseHeaders["ETag"] = etag;
                response = ApiResponse.Ok(new { profile = ProfileEntry(profile), etag }, requestId);
                return true;
            }
            catch (Exception exception) { throw MapProfileException(exception); }
        }

        if (segments.Length == 3 && segments[2] == "versions" && method == "GET")
        {
            EnsureOnlyQuery(request, "limit", "cursor");
            var limit = ParseLimit(request.Query.GetValueOrDefault("limit"));
            var beforeVersion = DecodeVersionCursor(request.Query.GetValueOrDefault("cursor"), profileId);
            try
            {
                var snapshot = gateway.ReadVersionPageSnapshot(profileId, limit, beforeVersion)
                    ?? throw ProfileNotFound();
                var page = snapshot.Page;
                var directory = snapshot.Directory;
                var etag = FixedRegionProfileETag.Compute(directory);
                responseHeaders["ETag"] = etag;
                response = ApiResponse.Ok(new
                {
                    profile_id = profileId,
                    is_deleted = directory.IsDeleted,
                    versions = page.Items.Select(version => VersionEntry(version, directory.Name, directory.IsDeleted,
                        directory.CurrentVersion.ProfileVersion)).ToArray(),
                    limit,
                    next_cursor = page.NextCursor is null ? null : EncodeCursor("v1", profileId, page.NextCursor)
                }, requestId);
                return true;
            }
            catch (Exception exception) { throw MapProfileException(exception); }
        }

        if (segments.Length == 4 && segments[2] == "versions" && method == "GET")
        {
            EnsureOnlyQuery(request);
            if (!long.TryParse(segments[3], NumberStyles.None, CultureInfo.InvariantCulture, out var versionNumber) || versionNumber <= 0)
                throw ProfileInvalid("The exact profile version must be a positive integer.");
            try
            {
                var snapshot = gateway.ReadExactVersionSnapshot(profileId, versionNumber)
                    ?? throw ProfileNotFound();
                var directory = snapshot.Directory;
                var version = snapshot.Version ?? throw ProfileVersionNotFound();
                var etag = FixedRegionProfileETag.Compute(directory);
                responseHeaders["ETag"] = etag;
                response = ApiResponse.Ok(new
                {
                    version = VersionEntry(version, directory.Name, directory.IsDeleted,
                        directory.CurrentVersion.ProfileVersion),
                    etag
                }, requestId);
                return true;
            }
            catch (Exception exception) { throw MapProfileException(exception); }
        }

        if (segments.Length == 2 && method == "PATCH")
        {
            if (request.HasDuplicateHeader("If-Match"))
                throw ProfileInvalid("If-Match must contain exactly one strong entity tag.");
            var ifMatch = ParseIfMatch(request.Headers.GetValueOrDefault("If-Match"));
            var patch = FixedRegionProfileApiRequestParser.ParsePatch(body);
            try
            {
                var updated = gateway.Patch(profileId, ifMatch, patch.Name, patch.Changes, DateTimeOffset.UtcNow);
                var etag = FixedRegionProfileETag.Compute(updated);
                responseHeaders["ETag"] = etag;
                _audit.Log("profile.updated", new { profile_id = updated.ProfileId,
                    profile_version = updated.CurrentVersion.ProfileVersion,
                    profile_digest = updated.CurrentVersion.ProfileDigest });
                response = ApiResponse.Ok(new { profile = ProfileEntry(updated), etag }, requestId);
                return true;
            }
            catch (Exception exception) { throw MapProfileException(exception); }
        }

        if (segments.Length == 2 && method == "DELETE")
        {
            if (request.HasDuplicateHeader("If-Match"))
                throw ProfileInvalid("If-Match must contain exactly one strong entity tag.");
            var ifMatch = ParseIfMatch(request.Headers.GetValueOrDefault("If-Match"));
            try
            {
                gateway.Delete(profileId, ifMatch, DateTimeOffset.UtcNow);
                var deleted = gateway.Get(profileId) ?? throw ProfileNotFound();
                var etag = FixedRegionProfileETag.Compute(deleted);
                responseHeaders["ETag"] = etag;
                _audit.Log("profile.deleted", new { profile_id = deleted.ProfileId,
                    profile_version = deleted.CurrentVersion.ProfileVersion,
                    profile_digest = deleted.CurrentVersion.ProfileDigest });
                response = ApiResponse.Ok(new { profile = ProfileEntry(deleted), etag }, requestId);
                return true;
            }
            catch (Exception exception) { throw MapProfileException(exception); }
        }

        if (method is "GET" or "PATCH" or "DELETE" or "POST")
            throw new ApiException(404, "PROFILE_NOT_FOUND", "Unknown profile endpoint.");
        return false;
    }

    private static object ProfileEntry(FixedRegionProfileDirectoryRecord profile) => new
    {
        name = profile.Name,
        profile_ref = new
        {
            id = profile.ProfileId,
            version = profile.CurrentVersion.ProfileVersion,
            digest = profile.CurrentVersion.ProfileDigest
        },
        specification = ProfileSpecification(profile.CurrentVersion),
        is_deleted = profile.IsDeleted,
        created_at_utc = profile.CreatedAtUtc,
        updated_at_utc = profile.UpdatedAtUtc,
        deleted_at_utc = profile.DeletedAtUtc,
        etag = FixedRegionProfileETag.Compute(profile)
    };

    private static object VersionEntry(RecurringFixedRegionProfileVersion version, string name,
        bool isDeleted, long currentVersion) => new
    {
        name,
        profile_ref = new { id = version.ProfileId, version = version.ProfileVersion, digest = version.ProfileDigest },
        specification = ProfileSpecification(version),
        created_at_utc = version.CreatedAtUtc,
        is_current = version.ProfileVersion == currentVersion,
        is_deleted = isDeleted
    };

    private static object ProfileSpecification(RecurringFixedRegionProfileVersion profile) => new
    {
        target_policy = new
        {
            type = RecurringFixedRegionProfileCode.ToCode(profile.TargetType),
            rebind_policy = RecurringFixedRegionProfileCode.ToCode(profile.RebindPolicy),
            coordinate_space = RecurringFixedRegionProfileCode.ToCode(profile.CoordinateSpace),
            display_identity_status = RecurringFixedRegionProfileCode.ToCode(profile.DisplayIdentityStatus),
            stable_display_fingerprint = profile.StableDisplayFingerprint,
            display_bounds = Rectangle(profile.DisplayBounds),
            region_within_display = Rectangle(profile.RegionWithinDisplay),
            dpi = new { x = profile.DpiX, y = profile.DpiY },
            physical_width = profile.PhysicalWidth,
            physical_height = profile.PhysicalHeight,
            orientation = RecurringFixedRegionProfileCode.ToCode(profile.Orientation),
            topology_digest = profile.TopologyDigest
        },
        capture = new
        {
            semantics = RecurringFixedRegionProfileCode.ToCode(profile.CaptureSemantics),
            backend = RecurringFixedRegionProfileCode.ToCode(profile.Backend)
        },
        audio = new { mode = RecurringFixedRegionProfileCode.ToCode(profile.AudioMode) },
        duration_seconds = DurationSeconds(profile.Duration),
        duration_ms = profile.Duration.Ticks / TimeSpan.TicksPerMillisecond,
        countdown_seconds = profile.CountdownSeconds,
        output = new
        {
            directory = profile.OutputDirectory,
            filename_prefix = profile.FilenamePrefix,
            filename_template = profile.FilenameTemplate,
            conflict_policy = RecurringFixedRegionProfileCode.ToCode(profile.OutputConflictPolicy)
        },
        wake_policy = RecurringFixedRegionProfileCode.ToCode(profile.WakePolicy),
        desktop_requirement = RecurringFixedRegionProfileCode.ToCode(profile.DesktopRequirement)
    };

    private static object DurationSeconds(TimeSpan duration)
    {
        var exactSeconds = duration.Ticks / (decimal)TimeSpan.TicksPerSecond;
        return duration.Ticks % TimeSpan.TicksPerSecond == 0
            ? (object)(long)(duration.Ticks / TimeSpan.TicksPerSecond)
            : exactSeconds;
    }

    private static object Rectangle(AuthorizedPhysicalRectangle rectangle) => new
    {
        x = rectangle.X, y = rectangle.Y, width = rectangle.Width, height = rectangle.Height
    };

    private static string ParseIfMatch(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new ApiException(428, "IF_MATCH_REQUIRED", "A current strong If-Match token is required.",
                new { reason_code = "if_match_required" });
        if (!value.StartsWith("\"profile-v1-", StringComparison.Ordinal) || !value.EndsWith('"') ||
            value.Length != 77 || value[12..^1].Length != 64 || value[12..^1].Any(character => !Uri.IsHexDigit(character)) ||
            value.Contains(',') || value.Contains('*') || value.StartsWith("W/", StringComparison.OrdinalIgnoreCase))
            throw ProfileInvalid("If-Match must contain exactly one supported strong profile ETag.");
        return value;
    }

    private static string DecodeProfileId(string segment)
    {
        string id;
        try { id = Uri.UnescapeDataString(segment); }
        catch (UriFormatException) { throw ProfileInvalid("The profile identifier is malformed."); }
        try { return new ProfileRef(id, 1, RecurringFixedRegionProfileVersion.DigestPrefix + new string('0', 64)).ProfileId; }
        catch (Exception exception) when (exception is ArgumentException or Phase3DomainException)
        { throw ProfileNotFound(); }
    }

    private static int ParseLimit(string? value)
    {
        if (value is null) return 20;
        if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var limit) || limit is < 1 or > 100)
            throw ProfileInvalid("limit must be an integer between 1 and 100.");
        return limit;
    }

    private static bool ParseOptionalBoolean(string? value, string name) => value switch
    {
        null or "false" => false,
        "true" => true,
        _ => throw ProfileInvalid($"{name} must be true or false.")
    };

    private static void EnsureOnlyQuery(HttpRequest request, params string[] allowed)
    {
        var permitted = new HashSet<string>(allowed, StringComparer.OrdinalIgnoreCase);
        if (request.Query.Keys.Any(key => !permitted.Contains(key)))
            throw ProfileInvalid("The request contains an unsupported query parameter.");
    }

    private static string? DecodeProfileCursor(string? cursor)
    {
        if (cursor is null) return null;
        var decoded = DecodeCursor(cursor);
        if (!decoded.StartsWith("p1\n", StringComparison.Ordinal) || decoded.Length <= 3)
            throw ProfileInvalid("The profile cursor is invalid.");
        var id = decoded[3..];
        try { return new ProfileRef(id, 1, RecurringFixedRegionProfileVersion.DigestPrefix + new string('0', 64)).ProfileId; }
        catch (Exception exception) when (exception is ArgumentException or Phase3DomainException)
        { throw ProfileInvalid("The profile cursor is invalid."); }
    }

    private static long? DecodeVersionCursor(string? cursor, string profileId)
    {
        if (cursor is null) return null;
        var decoded = DecodeCursor(cursor);
        var parts = decoded.Split('\n');
        if (parts.Length != 3 || parts[0] != "v1" || !string.Equals(parts[1], profileId, StringComparison.Ordinal) ||
            !long.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out var version) || version <= 0)
            throw ProfileInvalid("The profile version cursor is malformed or belongs to another profile.");
        return version;
    }

    private static string EncodeCursor(params string[] parts) =>
        Convert.ToBase64String(Encoding.UTF8.GetBytes(string.Join('\n', parts)))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static string DecodeCursor(string cursor)
    {
        if (cursor.Length is < 1 or > 512 || cursor.Any(character =>
            !(character is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or >= '0' and <= '9' or '-' or '_')))
            throw ProfileInvalid("The profile cursor is malformed.");
        try
        {
            var base64 = cursor.Replace('-', '+').Replace('_', '/');
            base64 += new string('=', (4 - base64.Length % 4) % 4);
            return new UTF8Encoding(false, true).GetString(Convert.FromBase64String(base64));
        }
        catch (Exception exception) when (exception is FormatException or DecoderFallbackException)
        { throw ProfileInvalid("The profile cursor is malformed."); }
    }

    private static ApiException MapProfileException(Exception exception)
    {
        if (exception is ApiException apiException) return apiException;
        if (exception is ArgumentException or Phase3DomainException)
            return ProfileInvalid("The profile request contains an invalid value.");
        return new ApiException(500, "PROFILE_OPERATION_FAILED", "The profile operation could not be completed.",
            new { reason_code = "profile_operation_failed" });
    }

    private static ApiException ProfileManagementUnavailable() => new(503,
        "PROFILE_MANAGEMENT_UNAVAILABLE", "The durable profile management store is unavailable.",
        new { reason_code = "profile_management_unavailable" });

    private static ApiException ProfileNotFound() => new(404, "PROFILE_NOT_FOUND", "The profile was not found.");
    private static ApiException ProfileVersionNotFound() => new(404, "PROFILE_VERSION_NOT_FOUND", "The exact profile version was not found.");
    private static ApiException ProfileInvalid(string message) => new(400, "INVALID_ARGUMENT", message,
        new { reason_code = "profile_request_invalid" });

    private bool RecurringExecutionSupported()
    {
        try { return _recurringPlanSetupGateway?.IsExecutionSupported == true; }
        catch { return false; }
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
        var recurringExecutionSupported = RecurringExecutionSupported();
        var recurringSetupSupported = _recurringPlanSetupGateway is not null &&
            RecurringInteractiveDesktopAvailable() && recurringExecutionSupported;
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
        var windowSurfaceProbe = CaptureBackendSelector.ProbeWindowSurfaceAvailability();
        var systemAudioAvailability = GetFreshSystemAudioAvailability();
        bool systemAudioHelperChecked = systemAudioAvailability == "ready";
        var systemAudioHelperAvailability = systemAudioHelperChecked
            ? RecordingPreflightChecker.ProbeSystemAudioHelperAvailability()
            : (Available: false, ReasonCode: (string?)"system_audio_endpoint_unavailable");
        var reliableWindowsDisplayNumbers = displaysContext.DisplaySnapshot
            .Where(display => display.windows_display_number is int number && number > 0)
            .Select(display => display.windows_display_number!.Value)
            .Distinct()
            .OrderBy(number => number)
            .ToArray();
        var quickRecipes = BuildQuickRecipes(
            hasPrimaryDisplay,
            hasActiveWindow,
            supportsRegionSelection,
            hasLastRegion,
            displaysContext.DisplaySnapshot);

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
                    system_audio = new { supported = true, status = systemAudioAvailability }
                },
                window_surface = new
                {
                    supported = windowSurfaceProbe.Available,
                    status = windowSurfaceProbe.Available ? "ready" : "unavailable",
                    reason_code = windowSurfaceProbe.Available ? null : windowSurfaceProbe.ReasonCode,
                    source_type = "window",
                    capture_semantics = "window_surface",
                    obscuring_windows_excluded = true,
                    audio = new[] { "none", "system_audio" },
                    system_audio_supported = windowSurfaceProbe.Available && systemAudioAvailability == "ready"
                        && systemAudioHelperAvailability.Available,
                    system_audio_status = systemAudioAvailability,
                    system_audio_helper_status = !systemAudioHelperChecked
                        ? "not_checked"
                        : systemAudioHelperAvailability.Available ? "ready" : "unavailable",
                    system_audio_helper_reason_code = !systemAudioHelperChecked || systemAudioHelperAvailability.Available
                        ? null
                        : systemAudioHelperAvailability.ReasonCode,
                    system_audio_scope = "selected_render_endpoint_loopback_not_window_exclusive",
                    requires_local_confirmation = true,
                    min_duration_seconds = 1,
                    max_duration_seconds = 1800,
                    long_run_readiness = "bounded_duration_only",
                    long_run_stress_tested = false
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
                    targets = new[] { "primary_display", "windows_display", "active_window", "selected_region", "last_region" },
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
                windows_display = new
                {
                    supported = true,
                    available = reliableWindowsDisplayNumbers.Length > 0,
                    available_numbers = reliableWindowsDisplayNumbers,
                    unavailable_reason = reliableWindowsDisplayNumbers.Length > 0
                        ? null
                        : "no_reliable_windows_display_number"
                },
                countdown = new
                {
                    supported = true,
                    min_seconds = ConfigParser.MinCountdownSeconds,
                    max_seconds = ConfigParser.MaxCountdownSeconds,
                    default_seconds = ConfigParser.DefaultCountdownSeconds,
                    capture_during_countdown = false
                },
                creation_wait = new
                {
                    supported = true,
                    endpoints = new[] { "/api/v1/recordings", "/api/v1/recordings/quick" },
                    wait_for = new[] { "recording" },
                    default_wait_ms = 25000,
                    max_wait_ms = 25000,
                    milestone = "trusted_first_frame_or_terminal"
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
                quick_recipes = quickRecipes
            },
            safety = new { requires_confirmation = true, recording_indicator = true, audit_log = true },
            unattended_lease = new
            {
                supported = _standingPlanSetupGateway is not null && _standingPlanSetupGateway.IsInteractiveDesktopAvailable,
                current_enabled = _standingPlanSetupGateway?.IsUnattendedEnabled ?? false,
                default_enabled = false,
                supported_targets = new[] { "fixed_region" },
                audio_allowed = false,
                max_lease_seconds = 3600,
                max_run_seconds = 600,
                max_latest_start_grace_seconds = 300,
                wake_policy = "natural_wake_only",
                requires_interactive_desktop = true,
                revocation_supported = true,
                profile_crud_supported = false,
                create_endpoint = "/api/v1/plans",
                status_endpoint = "/api/v1/plan-setups/{setup_intent_id}",
                execution_status_endpoint = "/api/v1/plans/{plan_id}/status",
                execution_supported = _standingPlanSetupGateway?.IsExecutionSupported ?? false,
                recurring = new
                {
                    supported = recurringSetupSupported,
                    setup_supported = recurringSetupSupported,
                    execution_supported = recurringExecutionSupported && _recurringPlanSetupGateway is not null,
                    schedule_kinds = new[] { "daily", "weekly" },
                    supported_targets = new[] { "fixed_region" },
                    audio_allowed = false,
                    countdown_seconds = 0,
                    wake_policy = "natural_wake_only",
                    requires_interactive_desktop = true,
                    concurrent_runs = 1,
                    nested_recording_allowed = false,
                    profile_crud_supported = false,
                    create_endpoint = "/api/v1/plans",
                    status_endpoint = "/api/v1/plan-setups/{setup_intent_id}",
                    execution_status_endpoint = "/api/v1/plans/{plan_id}/status"
                }
            },
            profile_management = new
            {
                supported = _profileManagementGateway is not null,
                supported_operations = _profileManagementGateway is IFixedRegionProfileSelectionCreationGateway && _tray.SupportsRegionSelectionUi
                    ? new[] { "list", "get", "list_versions", "get_version", "copy_existing_version", "create_from_local_selection", "patch", "delete" }
                    : new[] { "list", "get", "list_versions", "get_version", "copy_existing_version", "patch", "delete" },
                endpoints = new[]
                {
                    "/api/v1/profiles", "/api/v1/profiles/{profile_id}",
                    "/api/v1/profiles/{profile_id}/versions", "/api/v1/profiles/{profile_id}/versions/{version}"
                },
                supported_targets = new[] { "fixed_region" },
                creation_modes = _profileManagementGateway is IFixedRegionProfileSelectionCreationGateway && _tray.SupportsRegionSelectionUi
                    ? new[] { "copy_existing_version", "create_from_local_selection" }
                    : new[] { "copy_existing_version" },
                interactive_selection_supported = _profileManagementGateway is IFixedRegionProfileSelectionCreationGateway &&
                    _tray.SupportsRegionSelectionUi,
                selection_endpoint = "/api/v1/regions/select",
                selection_purpose_profile_supported = _profileManagementGateway is IFixedRegionProfileSelectionCreationGateway &&
                    _tray.SupportsRegionSelectionUi,
                selection_reference_ttl_seconds = (int)FixedRegionProfileSelectionCache.Lifetime.TotalSeconds,
                audio_allowed = false,
                recording_or_plan_profile_ref_supported = false,
                ordinary_recording_profile_ref_supported = _profileManagementGateway is not null,
                plan_profile_ref_supported = false,
                execution_supported = false,
                ordinary_recording_execution_supported = _profileManagementGateway is not null &&
                    !string.Equals(_tray.HostMode, "headless", StringComparison.OrdinalIgnoreCase)
            },
            required_once_plan = new
            {
                setup_supported = _requiredOncePlanSetupGateway is not null && RequiredOnceInteractiveDesktopAvailable(),
                execution_supported = _requiredOncePlanSetupGateway?.IsExecutionSupported == true,
                authorization_mode = "required",
                creation_requires_local_approval = true,
                second_confirmation_required_at_execution = true,
                due_occurrence_behavior = _requiredOncePlanSetupGateway?.IsExecutionSupported == true
                    ? "local_per_run_confirmation_then_fixed_region_capture"
                    : "inert_until_execution_runtime_is_available",
                create_endpoint = "/api/v1/plans",
                status_endpoint = "/api/v1/plan-setups/{setup_intent_id}",
                execution_status_endpoint = "/api/v1/plans/{plan_id}/status",
            },
            future_window_one_shot = new
            {
                setup_supported = _futureWindowOneShotGateway?.IsSetupSupported == true &&
                    _futureWindowOneShotGateway.IsInteractiveDesktopAvailable,
                execution_supported = _futureWindowOneShotGateway?.IsExecutionSupported == true,
                local_approval_required = true,
                authorization_validity_max_seconds = 3600,
                run_duration_max_seconds = 1800,
                one_run_only = true,
                supported_audio_modes = new[] { "none", "system_loopback" },
                supported_target = "one_exact_top_level_window",
                no_fallback = true,
                create_endpoint = "/api/v1/future-window-authorizations",
                status_endpoint = "/api/v1/future-window-authorizations/{authorization_id}",
                run_endpoint = "/api/v1/future-window-authorizations/{authorization_id}/runs",
                revoke_endpoint = "/api/v1/future-window-authorizations/{authorization_id}/revoke",
                unavailable_reason = _futureWindowOneShotGateway is null ? "future_window_runtime_unavailable" :
                    !_futureWindowOneShotGateway.IsInteractiveDesktopAvailable ? "interactive_desktop_required" :
                    !_futureWindowOneShotGateway.IsExecutionSupported ? "future_window_execution_unavailable" : null,
            },
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

    private static object[] BuildQuickRecipes(
        bool hasPrimaryDisplay,
        bool hasActiveWindow,
        bool supportsRegionSelection,
        bool hasLastRegion,
        IReadOnlyList<SystemQuery.DisplayInfo> displaySnapshot)
    {
        var recipes = new List<object>
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
        };

        foreach (var display in displaySnapshot
            .Where(display => display.windows_display_number is int number && number > 0)
            .OrderBy(display => display.windows_display_number))
        {
            var number = display.windows_display_number!.Value;
            recipes.Add(new
            {
                name = $"record_windows_display_{number}",
                target_type = "windows_display",
                description = $"Record Windows display {number} ({display.name}) with local confirmation.",
                endpoint = "/api/v1/recordings/quick",
                method = "POST",
                windows_display_number = number,
                request_template = new
                {
                    target = new { type = "windows_display", windows_display_number = number },
                    duration_seconds = 60,
                    countdown_seconds = ConfigParser.DefaultCountdownSeconds
                },
                available = true,
                unavailable_reason = (string?)null
            });
        }

        return recipes.ToArray();
    }

    private (bool Available, int Count, string? PrimaryDisplayId, object? VirtualBounds, object[] Items, string? Error, SystemQuery.DisplayInfo[] DisplaySnapshot) BuildDisplaysContext()
    {
        try
        {
            var displays = SystemQuery.EnumDisplays();
            // Compute this from the same display list used for items. This
            // keeps one capabilities response internally consistent even if
            // the desktop topology changes between native enumerations.
            var virtualBounds = ComputeVirtualScreenBounds(displays);

            var items = displays.Select(d => new
            {
                id = d.id,
                name = d.name,
                windows_display_number = d.windows_display_number,
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
                Error: null,
                DisplaySnapshot: displays.ToArray()
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
                Error: ex.Message,
                DisplaySnapshot: Array.Empty<SystemQuery.DisplayInfo>()
            );
        }
    }

    private static SystemQuery.Bounds ComputeVirtualScreenBounds(IReadOnlyList<SystemQuery.DisplayInfo> displays)
    {
        if (displays.Count == 0)
            return new SystemQuery.Bounds(0, 0, 0, 0);

        int minX = displays[0].bounds.x;
        int minY = displays[0].bounds.y;
        int maxRight = displays[0].bounds.x + displays[0].bounds.width;
        int maxBottom = displays[0].bounds.y + displays[0].bounds.height;
        foreach (var display in displays.Skip(1))
        {
            minX = Math.Min(minX, display.bounds.x);
            minY = Math.Min(minY, display.bounds.y);
            maxRight = Math.Max(maxRight, display.bounds.x + display.bounds.width);
            maxBottom = Math.Max(maxBottom, display.bounds.y + display.bounds.height);
        }

        return new SystemQuery.Bounds(minX, minY, maxRight - minX, maxBottom - minY);
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
    private readonly HashSet<string> _duplicateHeaders;

    public HttpRequest(string method, string rawPath, Dictionary<string, string> headers, string body,
        HashSet<string>? duplicateHeaders = null)
    {
        Method = method;
        Headers = headers;
        Body = body;
        _duplicateHeaders = duplicateHeaders ?? new HashSet<string>(StringComparer.OrdinalIgnoreCase);

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

    public bool HasDuplicateHeader(string name) => _duplicateHeaders.Contains(name);

    private static Dictionary<string, string> ParseQuery(string query)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var part in query.Split('&'))
        {
            var eq = part.IndexOf('=');
            try
            {
                var key = Uri.UnescapeDataString(eq < 0 ? part : part[..eq]);
                var value = eq < 0 ? "" : Uri.UnescapeDataString(part[(eq + 1)..]);
                if (key.Length == 0 || !result.TryAdd(key, value))
                    throw new ApiException(400, "INVALID_ARGUMENT", "The request contains an empty or duplicate query parameter.");
            }
            catch (UriFormatException)
            {
                throw new ApiException(400, "INVALID_ARGUMENT", "The request contains malformed query encoding.");
            }
        }
        return result;
    }
}
