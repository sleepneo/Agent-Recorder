using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AgentRecorder.Cli;

internal static partial class Program
{
    private const int QuickDefaultTimeout = 240_000;
    private const int QuickMaxSeconds = 30;

    internal sealed record QuickResult(bool Ok, string Status, string ReasonCode, string? RecordingId = null,
        string? RecordingStatus = null, string? OutputPath = null, long? OutputSizeBytes = null,
        string? HostOwnership = null, ShutdownResult? HostShutdown = null,
        string? ApiErrorCode = null, string? RecordingStopReason = null);

    private static int RunQuickRecording(string[] args)
    {
        var opts = new CliOptions();
        var timeout = QuickDefaultTimeout;
        string? requestFile = null;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 1; i < args.Length; i++)
        {
            var option = args[i];
            if (option is "--help" or "-h")
            {
                Console.WriteLine("record-quick [--request-file <local JSON file>] [--timeout-ms 240000]");
                Console.WriteLine("             [--data-dir <path>] [--package-root <path>] [--app <path>]");
                Console.WriteLine("Reads quick JSON from stdin by default. Explicit duration 1–30 seconds. Outputs one JSON result.");
                Console.WriteLine("Run on the same unlocked user desktop. Selection and approval remain local.");
                return 0;
            }
            if (option == "--json" && seen.Add(option)) continue;
            if (!seen.Add(option) || i + 1 == args.Length)
                return WriteQuickResult(new(false, "not_submitted", "invalid_option"));
            var value = args[++i];
            switch (option)
            {
                case "--request-file": requestFile = value; break;
                case "--data-dir": opts.DataDir = value; break;
                case "--package-root": opts.PackageRoot = value; break;
                case "--app": opts.AppPath = value; break;
                case "--timeout-ms" when int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out timeout) &&
                    timeout is >= 1000 and <= 300_000: break;
                default: return WriteQuickResult(new(false, "not_submitted", "invalid_option"));
            }
            if (string.IsNullOrWhiteSpace(value)) return WriteQuickResult(new(false, "not_submitted", "invalid_option"));
        }
        if (requestFile is null && !Console.IsInputRedirected)
            return WriteQuickResult(new(false, "not_submitted", "request_input_required"));

        var result = QuickRecordAsync(timeout, async token =>
        {
            if (requestFile is not null) return await ReadQuickTextAsync(requestFile, 64 * 1024, token).ConfigureAwait(false);
            using var stdin = Console.OpenStandardInput();
            return await ReadQuickTextAsync(stdin, 64 * 1024, token).ConfigureAwait(false);
        }, token => AcquireQuickHostAsync(opts, token)).GetAwaiter().GetResult();
        return WriteQuickResult(result);
    }

    internal static int WriteQuickResult(QuickResult result, TextWriter? output = null)
    {
        (output ?? Console.Out).WriteLine(JsonSerializer.Serialize(result, new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        }));
        return result.Ok ? 0 : 1;
    }

    private static async Task<string> ReadQuickTextAsync(string path, int limit, CancellationToken token)
    {
        await using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.Asynchronous);
        return await ReadQuickTextAsync(file, limit, token).ConfigureAwait(false);
    }

    private static async Task<string> ReadQuickTextAsync(Stream stream, int limit, CancellationToken token)
    {
        var bytes = new byte[limit + 1];
        var count = 0;
        while (count < bytes.Length)
        {
            int read = await stream.ReadAsync(bytes.AsMemory(count), token).ConfigureAwait(false);
            if (read == 0) break;
            count += read;
        }
        if (count > limit) throw new InvalidDataException();
        int offset = count >= 3 && bytes[0] == 0xef && bytes[1] == 0xbb && bytes[2] == 0xbf ? 3 : 0;
        return new UTF8Encoding(false, true).GetString(bytes, offset, count - offset);
    }

    private static bool IsShortQuick(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var body = doc.RootElement;
        if (body.ValueKind != JsonValueKind.Object || HasDuplicateProperties(body) ||
            body.TryGetProperty("mode", out var mode) && StringValue(mode) != "video" ||
            body.TryGetProperty("nested", out _) ||
            !body.TryGetProperty("target", out var target) || target.ValueKind != JsonValueKind.Object ||
            Text(target, "type") is not ("primary_display" or "windows_display" or "active_window" or "selected_region" or "last_region" or "profile_region"))
            return false;
        bool duration = body.TryGetProperty("duration_seconds", out var seconds);
        if (body.TryGetProperty("stop_condition", out var stop))
        {
            if (duration || stop.ValueKind != JsonValueKind.Object || Text(stop, "type") != "duration" ||
                !stop.TryGetProperty("seconds", out seconds)) return false;
        }
        return seconds.ValueKind == JsonValueKind.Number && seconds.TryGetInt32(out var value) && value is >= 1 and <= QuickMaxSeconds;
    }

    private static bool HasDuplicateProperties(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Array) return element.EnumerateArray().Any(HasDuplicateProperties);
        if (element.ValueKind != JsonValueKind.Object) return false;
        var names = new HashSet<string>(StringComparer.Ordinal);
        return element.EnumerateObject().Any(property => !names.Add(property.Name) || HasDuplicateProperties(property.Value));
    }

    internal static async Task<QuickResult> QuickRecordAsync(int timeoutMs, Func<CancellationToken, Task<string>> readBody,
        Func<CancellationToken, Task<QuickHost>> acquire)
    {
        var clock = Stopwatch.StartNew();
        int exitReserve = Math.Min(5000, timeoutMs / 5);
        using var work = new CancellationTokenSource(timeoutMs - exitReserve);
        var token = work.Token;
        QuickHost? host = null;
        string? id = null, status = null;
        var stage = "input";
        QuickResult result;
        try
        {
            // Some redirected console streams cannot cancel an in-progress read.
            var body = await Task.Run(() => readBody(token), token).WaitAsync(token).ConfigureAwait(false);
            if (!IsShortQuick(body)) return new(false, "not_submitted", "unsupported_quick_request");
            token.ThrowIfCancellationRequested();
            stage = "startup";
            host = await acquire(token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            stage = "submission";
            result = await SubmitAndObserveAsync(host, body, (recording, state) =>
            {
                id = recording;
                status = state;
                stage = "recording";
            }, token).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            var reason = token.IsCancellationRequested ? "deadline" : ex is QuickFailure failure ? failure.Reason :
                stage == "input" ? "invalid_request_input" : stage == "startup" ? "host_unavailable" : "transport_or_evidence_unavailable";
            result = new(false, stage is "input" or "startup" ? "not_submitted" :
                id is null ? "submission_unknown" : "outcome_unknown", reason, id, status);
        }

        using (host)
        {
            if (host is null)
                return stage == "startup" ? result with { HostOwnership = "unconfirmed" } : result;
            if (host.Exit is null)
                return result with { HostOwnership = "reused" };
            var shutdown = await ExitOwnedHostAsync(host, clock, timeoutMs).ConfigureAwait(false);
            return result with { HostOwnership = "owned", HostShutdown = shutdown };
        }
    }

    private static async Task<ShutdownResult> ExitOwnedHostAsync(QuickHost host, Stopwatch clock, int timeoutMs)
    {
        var until = Math.Min(timeoutMs, clock.ElapsedMilliseconds + 5000);
        ShutdownResult last = new(false, "timeout", "shutdown_budget_unavailable");
        while (until - clock.ElapsedMilliseconds is >= 100 and var remaining)
        {
            try { last = await host.Exit!((int)remaining).ConfigureAwait(false); }
            catch { return new(false, "unknown", "shutdown_unavailable"); }
            // Explicit busy proves the idle request did not authorize exit.
            // Wait for retirement, within the same budget and on the same host.
            if (last.Status != "busy" || until - clock.ElapsedMilliseconds < 250) return last;
            await Task.Delay(150).ConfigureAwait(false);
        }
        return last;
    }

    private static async Task<QuickResult> SubmitAndObserveAsync(QuickHost host, string body, Action<string, string?> observed,
        CancellationToken token)
    {
        using var request = QuickRequest(host, HttpMethod.Post, "recordings/quick?wait_for=recording&wait_ms=1000");
        request.Content = new StringContent(body, Encoding.UTF8, "application/json");
        using var response = await host.Submission.SendAsync(request, token).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            if ((int)response.StatusCode is 400 or 401 or 403 or 404 or 409 or 429)
            {
                using var rejected = JsonDocument.Parse(await response.Content.ReadAsStringAsync(token).ConfigureAwait(false));
                if (rejected.RootElement.TryGetProperty("ok", out var ok) && ok.ValueKind == JsonValueKind.False &&
                    rejected.RootElement.TryGetProperty("error", out var error) && Text(error, "code") is not null)
                    return new(false, "not_submitted", "quick_rejected",
                        ApiErrorCode: ReadQuickDiagnostic(rejected.RootElement, error, "code", IsQuickApiErrorCode));
            }
            return new(false, "submission_unknown", "quick_response_unconfirmed");
        }
        var created = await ReadQuickDataAsync(response, token).ConfigureAwait(false);
        var initial = Text(created, "status");
        var id = Text(created, "recording_id");
        if (id is null && created.TryGetProperty("recording", out var embedded)) id = Text(embedded, "recording_id");
        if (!IsQuickRecordingId(id))
            return id is null && initial is "selection_cancelled" or "selection_timeout" or "selection_failed" or "display_unavailable"
                ? new(false, "no_recording", initial) : new(false, "submission_unknown", "recording_id_unconfirmed");

        observed(id!, KnownStatus(initial) ? initial : null);
        string? since = null;
        while (true)
        {
            string route = "recordings/" + id;
            if (since is not null) route += "?since_status=" + since + "&wait_ms=1000";
            using var poll = await GetQuickAsync(host, route, token).ConfigureAwait(false);
            var state = await ReadQuickDataAsync(poll, token).ConfigureAwait(false);
            var status = Text(state, "status");
            if (Text(state, "recording_id") != id || !KnownStatus(status))
                return new(false, "outcome_unknown", "recording_status_unconfirmed", id, since);
            observed(id!, status);
            if (status == "completed")
                return await ConfirmSavedAsync(host, id!, state, token).ConfigureAwait(false);
            if (status == "interrupted_unknown")
                return new(false, "outcome_unknown", "recording_terminal_failure", id, status);
            if (status is "failed" or "cancelled" or "rejected" or "expired")
                return new(false, "recording_failed", status == "expired" ? "approval_expired" : "recording_terminal_failure",
                    id, status, RecordingStopReason: ReadQuickDiagnostic(state, state, "stop_reason", IsQuickStopReason));
            since = status;
        }
    }

    private static async Task<QuickResult> ConfirmSavedAsync(QuickHost host, string id, JsonElement status, CancellationToken token)
    {
        const string completed = "completed";
        QuickResult Unconfirmed() => new(false, "outcome_unknown", "publication_unconfirmed", id, completed);
        if (!status.TryGetProperty("output", out var output) ||
            Text(output, "path") is not { } path || !Path.IsPathFullyQualified(path) ||
            !path.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase) || PositiveBytes(output, "bytes_written") is not { } bytes)
            return Unconfirmed();

        var route = "recording-history/" + id;
        while (true)
        {
            using var historyReply = await GetQuickAsync(host, route, token).ConfigureAwait(false);
            if (historyReply.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.ServiceUnavailable)
            {
                await Task.Delay(150, token).ConfigureAwait(false);
                continue;
            }
            var history = await ReadQuickDataAsync(historyReply, token).ConfigureAwait(false);
            var row = history.GetProperty("recording");
            var recordedStatus = Text(row, "status");
            if (Text(row, "recording_id") != id || !KnownStatus(recordedStatus)) return Unconfirmed();
            if (recordedStatus != completed)
            {
                if (recordedStatus is "failed" or "rejected" or "cancelled" or "expired" or "interrupted_unknown") return Unconfirmed();
                await Task.Delay(150, token).ConfigureAwait(false);
                continue;
            }
            if (!PathsEqual(Text(row, "output_path") ?? "", path) || PositiveBytes(row, "output_size_bytes") != bytes ||
                !DateTimeOffset.TryParse(Text(row, "completed_at"), CultureInfo.InvariantCulture, DateTimeStyles.None, out _))
                return Unconfirmed();
            break;
        }
        while (true)
        {
            using var reply = await GetQuickAsync(host, route + "/artifacts/check", token).ConfigureAwait(false);
            if (reply.StatusCode == HttpStatusCode.ServiceUnavailable)
            {
                await Task.Delay(150, token).ConfigureAwait(false);
                continue;
            }
            var evidence = await ReadQuickDataAsync(reply, token).ConfigureAwait(false);
            if (Text(evidence, "recording_id") != id) return Unconfirmed();
            if (Text(evidence, "probe_state") is "busy" or "timeout" or "cancelled")
            {
                await Task.Delay(150, token).ConfigureAwait(false);
                continue;
            }
            var media = evidence.GetProperty("media");
            if (Text(evidence, "probe_state") != "complete" || Text(evidence, "media_history_status") != completed ||
                Text(media, "identity_state") != "match" ||
                Text(media, "observation") != "present_size_matches" ||
                PositiveBytes(media, "recorded_size_bytes") != bytes || PositiveBytes(media, "observed_size_bytes") != bytes)
                return Unconfirmed();
            return new(true, completed, "publication_confirmed", id, completed, path, bytes);
        }
    }

    private static HttpRequestMessage QuickRequest(QuickHost host, HttpMethod method, string route)
    {
        var request = new HttpRequestMessage(method, route);
        request.Headers.ConnectionClose = true;
        request.Headers.Add("X-Agent-Recorder-Key", host.Key);
        request.Headers.Add("X-Agent-Recorder-Instance", host.Instance);
        return request;
    }

    private static async Task<HttpResponseMessage> GetQuickAsync(QuickHost host, string route, CancellationToken token)
    {
        using var request = QuickRequest(host, HttpMethod.Get, route);
        return await host.Reads.SendAsync(request, token).ConfigureAwait(false);
    }

    private static async Task<JsonElement> ReadQuickDataAsync(HttpResponseMessage response, CancellationToken token)
    {
        if (!response.IsSuccessStatusCode) throw new QuickFailure("api_evidence_unavailable");
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(token).ConfigureAwait(false));
        if (!document.RootElement.GetProperty("ok").GetBoolean()) throw new QuickFailure("api_evidence_unavailable");
        var data = document.RootElement.GetProperty("data");
        if (data.ValueKind != JsonValueKind.Object) throw new QuickFailure("api_evidence_unavailable");
        return data.Clone();
    }

    private static string? StringValue(JsonElement value) => value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    private static string? Text(JsonElement value, string field) =>
        value.ValueKind == JsonValueKind.Object && value.TryGetProperty(field, out var item) ? StringValue(item) : null;
    private static long? PositiveBytes(JsonElement value, string field) =>
        value.ValueKind == JsonValueKind.Object && value.TryGetProperty(field, out var item) &&
        item.ValueKind == JsonValueKind.Number && item.TryGetInt64(out var number) && number > 0 ? number : null;
    private static bool IsQuickRecordingId(string? id) => id is { Length: 16 } && id.StartsWith("rec_", StringComparison.Ordinal) &&
        id[4..].All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static string? ReadQuickDiagnostic(JsonElement context, JsonElement owner, string field, Func<string, bool> isKnown)
    {
        try
        {
            return Text(owner, field) is { } code && isKnown(code) && !HasDuplicateProperties(context) ? code : null;
        }
        catch (InvalidOperationException)
        {
            // An optional JSON string can fail to decode even after parsing its document.
            return null;
        }
    }

    // Keep the projected diagnostics finite. The API also carries free text in recording state.
    private static bool IsQuickApiErrorCode(string code) => code is
        "INVALID_ARGUMENT" or "SOURCE_NOT_FOUND" or "UNAUTHORIZED" or "FORBIDDEN" or "UNSUPPORTED_FEATURE" or
        "storage_space_low" or "storage_capacity_unavailable" or "OUTPUT_PATH_IN_USE";
    private static bool IsQuickStopReason(string reason) => reason is
        "encoder_start_failed" or "backend_creation_failed" or "unexpected_exit" or "storage_space_low" or
        "storage_capacity_unavailable" or "user_rejected" or "confirmation_expired" or "OUTPUT_PATH_IN_USE";

    private static bool KnownStatus(string? status) => status is "requires_user_confirmation" or "created" or "pending_confirmation"
        or "preparing" or "countdown" or "recording" or "stopping" or "finalizing" or "completed" or "failed" or "rejected"
        or "cancelled" or "expired" or "interrupted_unknown";
}
