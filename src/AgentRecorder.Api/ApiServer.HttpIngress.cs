using System.Globalization;
using System.Net.Sockets;
using System.Text;
using AgentRecorder.Infrastructure;

namespace AgentRecorder.Api;

public sealed partial class ApiServer
{
    internal const int MaximumHeaderBytes = 64 * 1024; // Includes CRLF CRLF.
    internal const int MaximumBodyBytes = 1024 * 1024;
    internal const int MaximumAdmittedClients = 32;
    internal const int MaximumLongWaitClients = 24;
    private int _activeLongWaitClients;
    internal int ActiveLongWaitCountForTests => Volatile.Read(ref _activeLongWaitClients);
    internal static readonly TimeSpan RequestReadTimeout = TimeSpan.FromSeconds(5);
    internal static readonly TimeSpan ResponseWriteTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan StrictBodyReadTimeout = TimeSpan.FromSeconds(3);
    private const int ReadChunkBytes = 8192;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    // Instance-only seams can shorten, but cannot extend, the production I/O budgets.
    internal TimeSpan ReadTimeoutForTests { get; set; } = RequestReadTimeout;
    internal TimeSpan WriteTimeoutForTests { get; set; } = ResponseWriteTimeout;
    internal Action<int>? BeforeBodyAllocationForTests { get; set; }
    internal Func<Stream, Stream>? ClientStreamForTests { get; set; }
    internal int ActiveClientCountForTests { get { lock (_clientTaskGate) return _activeClients.Count; } }
    internal int ClientTaskCountForTests { get { lock (_clientTaskGate) return _clientTasks.Count; } }

    // Only restrict routes which can synchronously hold a request. This is not
    // a privileged control lane: every connection still uses the global quota,
    // parser, authentication, host admission, and normal business validation.
    private LongWaitLease? AdmitLongWait(HttpRequest request)
    {
        if (!MayHoldLongWait(request)) return null;
        while (true)
        {
            var count = Volatile.Read(ref _activeLongWaitClients);
            if (count >= MaximumLongWaitClients)
                throw new ApiException(429, "WAIT_CAPACITY_EXCEEDED", "Concurrent wait capacity is exhausted; no operation was started.",
                    new { maximum_wait_requests = MaximumLongWaitClients });
            if (Interlocked.CompareExchange(ref _activeLongWaitClients, count + 1, count) == count)
                return new LongWaitLease(this);
        }
    }

    private static bool MayHoldLongWait(HttpRequest request)
    {
        if (!request.Path.StartsWith(Prefix, StringComparison.Ordinal)) return false;
        var sub = request.Path[Prefix.Length..];
        if (request.Method == "POST")
        {
            // Creation may wait for interactive selection even without wait_for.
            // Reserve before any creation/selection side effects, not in ApplyCreationWait.
            if (sub is "/recordings" or "/recordings/quick")
            {
                _ = ParseCreationWaitMs(request.Query);
                return true;
            }
            return sub is "/region-selections" or "/regions/select";
        }
        if (request.Method != "GET") return false;
        var segments = sub.Trim('/').Split('/'); // Same route shape as Route.
        if ((segments.Length >= 2 && segments[0] == "confirmations") ||
            (segments.Length == 2 && segments[0] == "recordings"))
            return ParseWaitMs(request.Query.GetValueOrDefault("wait_ms")) > 0 &&
                !string.IsNullOrEmpty(request.Query.GetValueOrDefault("since_status"));
        if (segments.Length == 2 && segments[0] == "plan-setups")
        {
            var id = segments[1];
            var recurring = id.StartsWith("recurring-setup-", StringComparison.Ordinal);
            var required = id.StartsWith("required-once-setup-", StringComparison.Ordinal);
            if (!recurring && !required && !id.StartsWith("standing-setup-", StringComparison.Ordinal)) return false;
            var wait = ParsePlanSetupWaitMs(request.Query.GetValueOrDefault("wait_ms"));
            var version = ParsePlanSetupSinceVersion(request.Query.GetValueOrDefault("since_status_version") ??
                request.Query.GetValueOrDefault("since_version"), recurring, required);
            return wait > 0 && (request.Query.GetValueOrDefault("since_status") is not null || version is not null);
        }
        return false;
    }

    private sealed class LongWaitLease(ApiServer owner) : IDisposable
    {
        private ApiServer? _owner = owner;
        public void Dispose()
        {
            var server = Interlocked.Exchange(ref _owner, null);
            if (server is not null) Interlocked.Decrement(ref server._activeLongWaitClients);
        }
    }

    private static TimeSpan IoBudget(TimeSpan requested, TimeSpan maximum) =>
        requested > TimeSpan.Zero && requested < maximum ? requested : maximum;

    internal async Task<HttpRequest> ReadRequest(Stream stream)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
        deadline.CancelAfter(IoBudget(ReadTimeoutForTests, RequestReadTimeout));
        Func<ApiException>? strictError = null;
        ApiException Invalid() => strictError?.Invoke() ??
            new ApiException(400, "BAD_REQUEST", "Malformed or incomplete HTTP request.");
        ApiException TooLarge() => strictError?.Invoke() ??
            new ApiException(413, "PAYLOAD_TOO_LARGE", "HTTP request exceeds the byte limit.");
        try
        {
            var buffer = new byte[MaximumHeaderBytes];
            int count = 0, headerEnd = -1;
            while (headerEnd < 0)
            {
                if (count == buffer.Length) throw TooLarge();
                int read = await stream.ReadAsync(buffer.AsMemory(count, Math.Min(ReadChunkBytes, buffer.Length - count)), deadline.Token);
                if (read == 0) throw Invalid();
                var scanFrom = Math.Max(0, count - 3);
                count += read;
                headerEnd = FindHeaderEnd(buffer.AsSpan(0, count), scanFrom);
            }

            // Identify the existing strict route contract before rejecting invalid UTF-8.
            var lines = Encoding.UTF8.GetString(buffer, 0, headerEnd - 4).Split("\r\n");
            var requestLine = lines[0].Split(' ');
            if (requestLine.Length != 3 || requestLine[2] is not ("HTTP/1.1" or "HTTP/1.0") ||
                requestLine[0].Length == 0 || !requestLine[1].StartsWith('/')) throw Invalid();
            var method = requestLine[0].ToUpperInvariant();
            var path = requestLine[1];
            bool evaluation = IsRetentionEvaluationPath(path), batch = IsBatchDeletionPath(path), deletion = IsDeletionPath(path);
            bool staging = IsRetentionStagingPath(path), usage = IsRetentionUsagePath(path), reconciliation = IsRetentionReconciliationPath(path);
            bool noBody = staging || usage || reconciliation || IsRetentionStoragePath(path);
            int bodyLimit = MaximumBodyBytes;
            if (noBody)
            {
                bodyLimit = 0;
                strictError = () => reconciliation ? new RetentionReconciliationRequestException() :
                    usage ? new RetentionUsageRequestException() : staging ? new RetentionStagingRequestException() : new RetentionStorageRequestException();
            }
            else if (batch) { bodyLimit = BatchDeletionBodyLimit; strictError = InvalidBatchDeletion; }
            else if (evaluation) { bodyLimit = EvaluationBodyLimit; strictError = InvalidEvaluation; }
            else if (deletion) { bodyLimit = 2048; strictError = InvalidDeletion; }
            _ = StrictUtf8.GetString(buffer, 0, headerEnd);

            var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var duplicates = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var line in lines.Skip(1))
            {
                var colon = line.IndexOf(':');
                if (colon <= 0) throw Invalid();
                var name = line[..colon];
                if (name.Any(c => c <= ' ' || c >= 127)) throw Invalid();
                var value = line[(colon + 1)..].Trim(' ', '\t');
                if (!headers.TryAdd(name, value))
                {
                    duplicates.Add(name);
                    headers[name] = value;
                }
            }
            if (headers.ContainsKey("Transfer-Encoding") || duplicates.Contains("Content-Length")) throw Invalid();
            int length = 0;
            if (headers.TryGetValue("Content-Length", out var declared))
            {
                if (declared.Length == 0 || declared.Any(c => !char.IsAsciiDigit(c))) throw Invalid();
                if (!int.TryParse(declared, NumberStyles.None, CultureInfo.InvariantCulture, out length) || length > bodyLimit)
                    throw TooLarge();
            }
            int bufferedBodyBytes = count - headerEnd;
            if (bufferedBodyBytes > length) throw Invalid();
            byte[] bodyBytes = Array.Empty<byte>();
            if (length > 0)
            {
                BeforeBodyAllocationForTests?.Invoke(length);
                bodyBytes = new byte[length];
                buffer.AsSpan(headerEnd, bufferedBodyBytes).CopyTo(bodyBytes);
                using var bodyDeadline = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token);
                if (deletion || evaluation || batch) bodyDeadline.CancelAfter(StrictBodyReadTimeout);
                int offset = bufferedBodyBytes;
                while (offset < length)
                {
                    // Read a chunk so already available surplus cannot be silently truncated.
                    int read = await stream.ReadAsync(buffer.AsMemory(0, ReadChunkBytes), bodyDeadline.Token);
                    if (read == 0 || read > length - offset) throw Invalid();
                    buffer.AsSpan(0, read).CopyTo(bodyBytes.AsSpan(offset));
                    offset += read;
                }
                bodyDeadline.Token.ThrowIfCancellationRequested();
            }
            deadline.Token.ThrowIfCancellationRequested();
            var body = StrictUtf8.GetString(bodyBytes);
            if (strictError is null) body = StripBom(body);
            return new HttpRequest(method, path, headers, body, duplicates);
        }
        catch (DecoderFallbackException) { throw Invalid(); }
        catch (OperationCanceledException) when (!_cts.IsCancellationRequested)
        {
            throw strictError?.Invoke() ?? new ApiException(408, "REQUEST_TIMEOUT", "HTTP request read deadline exceeded.");
        }
        catch (IOException) when (!_cts.IsCancellationRequested) { throw Invalid(); }
        catch (SocketException) when (!_cts.IsCancellationRequested) { throw Invalid(); }
    }

    internal async Task WriteJson(Stream stream, int status, string body, IReadOnlyDictionary<string, string>? responseHeaders = null)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
        deadline.CancelAfter(IoBudget(WriteTimeoutForTests, ResponseWriteTimeout));
        var bytes = Encoding.UTF8.GetBytes(body);
        var extra = responseHeaders is null ? "" : string.Concat(responseHeaders.Select(h => $"{h.Key}: {h.Value}\r\n"));
        var header = Encoding.UTF8.GetBytes($"HTTP/1.1 {status} {StatusText(status)}\r\nContent-Type: application/json; charset=utf-8\r\nContent-Length: {bytes.Length}\r\nConnection: close\r\n{extra}\r\n");
        await stream.WriteAsync(header, deadline.Token);
        await stream.WriteAsync(bytes, deadline.Token);
        await stream.FlushAsync(deadline.Token);
    }

    private async Task<bool> TryWriteJson(Stream stream, int status, string body, IReadOnlyDictionary<string, string>? headers = null)
    {
        try { await WriteJson(stream, status, body, headers); return true; }
        catch { return false; } // A failed/partial response is never followed by another response.
    }

    private static int FindHeaderEnd(ReadOnlySpan<byte> bytes, int start)
    {
        for (int i = start; i <= bytes.Length - 4; i++)
            if (bytes[i] == '\r' && bytes[i + 1] == '\n' && bytes[i + 2] == '\r' && bytes[i + 3] == '\n') return i + 4;
        return -1;
    }

    private static string StripBom(string text) => text.StartsWith('\uFEFF') ? text[1..] : text;

    private static string StatusText(int status) => status switch
    {
        200 => "OK", 201 => "Created", 202 => "Accepted", 400 => "Bad Request", 401 => "Unauthorized",
        403 => "Forbidden", 404 => "Not Found", 405 => "Method Not Allowed", 408 => "Request Timeout",
        409 => "Conflict", 412 => "Precondition Failed", 413 => "Payload Too Large", 428 => "Precondition Required",
        422 => "Unprocessable Content", 429 => "Too Many Requests", 500 => "Internal Server Error",
        501 => "Not Implemented", 503 => "Service Unavailable", 504 => "Gateway Timeout", _ => "Unknown"
    };
}
