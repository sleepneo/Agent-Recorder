using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Text.Json;
using AgentRecorder.Api;
using AgentRecorder.Core;
using AgentRecorder.Infrastructure;
using AgentRecorder.Logging;
using Xunit;

namespace AgentRecorder.Tests;

[Collection("HeadlessHostIntegration")]
public sealed class HttpIngressBoundaryTests
{
    private static byte[] Request(string headers = "Content-Length: 0\r\n", string body = "", string path = "/api/v1/recordings") =>
        Encoding.UTF8.GetBytes($"POST {path} HTTP/1.1\r\nHost: localhost\r\n{headers}\r\n{body}");

    [Theory]
    [InlineData("/api/v1/recordings")]
    [InlineData("/api/v1/plans")]
    public async Task OriginalLargeDeclarationWithImmediateEofNeverParsesOrPrompts(string path)
    {
        using var f = new Fixture();
        using var input = new Input(Request("Content-Length: 262144\r\n", path: path));
        var error = await Assert.ThrowsAnyAsync<ApiException>(() => f.Server.ReadRequest(input));
        Assert.Equal(400, error.Status);
        Assert.Equal("BAD_REQUEST", error.Code);
        f.Server.Start();
        var reply = await f.Raw(Request("Content-Length: 262144\r\n", path: path));
        Assert.StartsWith("HTTP/1.1 400 Bad Request\r\n", reply);
        Assert.Contains("BAD_REQUEST", reply);
        Assert.Equal(0, f.Tray.Prompts);
        Assert.Equal(0, f.Tray.Selections);
        Assert.Empty(f.Engine._recs);
    }

    [Theory]
    [InlineData("1048577", 413)]
    [InlineData("2147483648", 413)]
    [InlineData("999999999999999999999999", 413)]
    [InlineData("-1", 400)]
    [InlineData("+1", 400)]
    [InlineData("", 400)]
    [InlineData("1,1", 400)]
    [InlineData("1 0", 400)]
    [InlineData("0x10", 400)]
    public async Task LengthIsRejectedBeforeBodyAllocationWithoutCredentials(string length, int status)
    {
        using var f = new Fixture();
        int allocations = 0;
        f.Server.BeforeBodyAllocationForTests = _ => allocations++;
        var bytes = Request($"Content-Length: {length}\r\n");
        using var input = new Input(bytes);
        var error = await Assert.ThrowsAnyAsync<ApiException>(() => f.Server.ReadRequest(input));
        Assert.Equal(status, error.Status);
        Assert.Equal(0, allocations);
        Assert.Equal(bytes.Length, input.BytesRead);
    }

    [Theory]
    [InlineData("Content-Length: 0\r\nContent-Length: 0\r\n")]
    [InlineData("Content-Length: 0\r\ncontent-length: 1\r\n")]
    [InlineData("Transfer-Encoding: chunked\r\n")]
    [InlineData("Transfer-Encoding: identity\r\nContent-Length: 0\r\n")]
    public async Task AmbiguousFramingIsRejectedForEveryRoute(string headers)
    {
        using var f = new Fixture();
        foreach (var path in new[] { "/api/v1/recordings", "/api/v1/plans", "/api/v1/retention/evaluations",
            "/api/v1/retention/deletion-requests", "/api/v1/retention/batch-deletion-requests", "/api/v1/retention/usage" })
        {
            using var input = new Input(Request(headers, path: path));
            var error = await Assert.ThrowsAnyAsync<ApiException>(() => f.Server.ReadRequest(input));
            Assert.Equal(400, error.Status);
            Assert.Equal(path.Contains("retention") ? "INVALID_ARGUMENT" : "BAD_REQUEST", error.Code);
        }
    }

    [Theory]
    [InlineData(-1, true)]
    [InlineData(0, true)]
    [InlineData(1, false)]
    public async Task HeaderLimitIncludesDelimiterEvenInLastChunk(int delta, bool valid)
    {
        using var f = new Fixture();
        const string prefix = "GET /api/v1/capabilities HTTP/1.1\r\nX-Pad: ";
        var text = prefix + new string('x', ApiServer.MaximumHeaderBytes + delta - prefix.Length - 4) + "\r\n\r\n";
        using var input = new Input(Encoding.ASCII.GetBytes(text));
        if (valid) Assert.Equal("/api/v1/capabilities", (await f.Server.ReadRequest(input)).Path);
        else Assert.Equal(413, (await Assert.ThrowsAnyAsync<ApiException>(() => f.Server.ReadRequest(input))).Status);
        Assert.InRange(input.BytesRead, 1, ApiServer.MaximumHeaderBytes);
    }

    [Fact]
    public async Task HeaderWithoutDelimiterStopsAtLimit()
    {
        using var f = new Fixture();
        using var input = new Input(Enumerable.Repeat((byte)'x', ApiServer.MaximumHeaderBytes + 8192).ToArray());
        Assert.Equal(413, (await Assert.ThrowsAnyAsync<ApiException>(() => f.Server.ReadRequest(input))).Status);
        Assert.Equal(ApiServer.MaximumHeaderBytes, input.BytesRead);
    }

    [Fact]
    public async Task ExactBodyLimitAndFragmentedMultibyteBomArePreserved()
    {
        using var f = new Fixture();
        const string chinese = "{\"目标\":\"中文🙂\"}";
        var body = chinese + new string(' ', ApiServer.MaximumBodyBytes - Encoding.UTF8.GetByteCount(chinese));
        using var maximum = new Input(Request($"Content-Length: {ApiServer.MaximumBodyBytes}\r\n", body), chunk: 4093);
        Assert.Equal(body, (await f.Server.ReadRequest(maximum)).Body);
        var bom = "\uFEFF" + chinese;
        using var fragmented = new Input(Request($"Content-Length: {Encoding.UTF8.GetByteCount(bom)}\r\n", bom), chunk: 1);
        Assert.Equal(chinese, (await f.Server.ReadRequest(fragmented)).Body);
    }

    [Theory]
    [InlineData("/api/v1/retention/deletion-requests", 2049)]
    [InlineData("/api/v1/retention/evaluations", 16385)]
    [InlineData("/api/v1/retention/batch-deletion-requests", 4097)]
    [InlineData("/api/v1/retention/storage", 1)]
    [InlineData("/api/v1/retention/staging", 1)]
    [InlineData("/api/v1/retention/usage", 1)]
    [InlineData("/api/v1/retention/reconciliation", 1)]
    public async Task StrictRoutesKeepTheirSmallerLimitAndError(string path, int length)
    {
        using var f = new Fixture();
        int allocations = 0;
        f.Server.BeforeBodyAllocationForTests = _ => allocations++;
        using var input = new Input(Request($"Content-Length: {length}\r\n", path: path));
        var error = await Assert.ThrowsAnyAsync<ApiException>(() => f.Server.ReadRequest(input));
        Assert.Equal(400, error.Status);
        Assert.Equal("INVALID_ARGUMENT", error.Code);
        Assert.Equal(0, allocations);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SurplusBufferedBytesAreRejectedIncludingAfterHeaderRead(bool splitHeader)
    {
        using var f = new Fixture();
        var bytes = Request("Content-Length: 3\r\n", "abcX");
        using var input = new Input(bytes, chunk: splitHeader ? bytes.Length - 4 : int.MaxValue);
        Assert.Equal(400, (await Assert.ThrowsAnyAsync<ApiException>(() => f.Server.ReadRequest(input))).Status);
    }

    [Fact]
    public async Task InvalidUtf8AndBodyReadFailureCannotBecomeAValidRequest()
    {
        using var f = new Fixture();
        var head = Request("Content-Length: 2\r\n");
        using var badUtf8 = new Input(head.Concat(new byte[] { 0xc3, 0x28 }).ToArray());
        Assert.Equal(400, (await Assert.ThrowsAnyAsync<ApiException>(() => f.Server.ReadRequest(badUtf8))).Status);
        using var broken = new Input(head, failAtEnd: true);
        Assert.Equal(400, (await Assert.ThrowsAnyAsync<ApiException>(() => f.Server.ReadRequest(broken))).Status);
        using var duplicates = new Input(Request("If-Match: a\r\nif-match: a\r\nIdempotency-Key: b\r\nIdempotency-Key: b\r\n"));
        var parsed = await f.Server.ReadRequest(duplicates);
        Assert.True(parsed.HasDuplicateHeader("If-Match"));
        Assert.True(parsed.HasDuplicateHeader("Idempotency-Key"));
    }

    [Theory]
    [InlineData("/api/v1/retention/deletion-requests", 2048)]
    [InlineData("/api/v1/retention/evaluations", 16384)]
    [InlineData("/api/v1/retention/batch-deletion-requests", 4096)]
    public async Task StrictRoutesAcceptExactByteLimit(string path, int length)
    {
        using var f = new Fixture();
        var body = new string(' ', length);
        using var input = new Input(Request($"Content-Length: {length}\r\n", body, path));
        Assert.Equal(length, (await f.Server.ReadRequest(input)).Body.Length);
    }

    [Theory]
    [InlineData("/api/v1/recordings", "BAD_REQUEST")]
    [InlineData("/api/v1/retention/evaluations", "INVALID_ARGUMENT")]
    public async Task InvalidHeaderUtf8UsesTheRoutesErrorContract(string path, string code)
    {
        using var f = new Fixture();
        var prefix = Encoding.ASCII.GetBytes($"GET {path} HTTP/1.1\r\nX-Note: ");
        using var input = new Input(prefix.Concat(new byte[] { 0xff }).Concat(Encoding.ASCII.GetBytes("\r\n\r\n")).ToArray());
        Assert.Equal(code, (await Assert.ThrowsAnyAsync<ApiException>(() => f.Server.ReadRequest(input))).Code);
    }

    [Fact]
    public async Task PipelinedBytesAreNotDispatchedAsASecondRequest()
    {
        using var f = new Fixture();
        using var input = new Input(Encoding.ASCII.GetBytes("GET /api/v1/capabilities HTTP/1.1\r\n\r\nGET /api/v1/recordings HTTP/1.1\r\n\r\n"));
        Assert.Equal(400, (await Assert.ThrowsAnyAsync<ApiException>(() => f.Server.ReadRequest(input))).Status);
    }

    [Fact]
    public async Task StrictBodyDeadlineKeepsItsStructured400()
    {
        using var f = new Fixture();
        f.Server.ReadTimeoutForTests = TimeSpan.FromMilliseconds(100);
        using var input = new Input(Request("Content-Length: 10\r\n", "x", "/api/v1/retention/evaluations"), waitAtEnd: true);
        var error = await Assert.ThrowsAnyAsync<ApiException>(() => f.Server.ReadRequest(input));
        Assert.Equal(400, error.Status);
        Assert.Equal("INVALID_ARGUMENT", error.Code);
        Assert.True(input.CancellationObserved);
        Assert.Equal(0, input.PendingReads);
    }

    [Fact]
    public async Task ParseAuthAndTimeoutFailuresReleaseTheSameConnectionSlot()
    {
        using var f = new Fixture();
        f.Server.ReadTimeoutForTests = TimeSpan.FromMilliseconds(120);
        int allocations = 0;
        f.Server.BeforeBodyAllocationForTests = _ => allocations++;
        f.Server.Start();
        Assert.StartsWith("HTTP/1.1 413 Payload Too Large", await f.Raw(Request("Content-Length: 2147483648\r\n")));
        Assert.Equal(0, allocations);
        Assert.StartsWith("HTTP/1.1 401 Unauthorized", await f.Raw(Request()));
        Assert.StartsWith("HTTP/1.1 403 Forbidden", await f.Raw(Request("Content-Length: 0\r\nX-Agent-Recorder-Key: wrong\r\n")));
        Assert.StartsWith("HTTP/1.1 400 Bad Request", await f.Raw(Encoding.ASCII.GetBytes("broken\r\n\r\n")));
        using (var silent = await f.Connect())
        {
            using var reader = new StreamReader(silent.GetStream());
            Assert.StartsWith("HTTP/1.1 408 Request Timeout", await reader.ReadToEndAsync().WaitAsync(TimeSpan.FromSeconds(2)));
        }
        for (int i = 0; i < 5; i++)
        {
            Assert.StartsWith("HTTP/1.1 200 OK", await f.Raw(Encoding.ASCII.GetBytes("GET /api/v1/capabilities HTTP/1.1\r\n\r\n")));
            await Until(() => f.Server.ActiveClientCountForTests == 0 && f.Server.ClientTaskCountForTests == 0);
        }
    }

    [Fact]
    public async Task WriteFailureCannotRetryAnErrorOrDispatchAcceptedShutdown()
    {
        using var f = new Fixture();
        int exits = 0;
        f.Server.ConfigureHostShutdown(() => null, () => Interlocked.Increment(ref exits));
        var outputs = new List<Output>();
        f.Server.ClientStreamForTests = input =>
        {
            var output = new Output("fail", input);
            lock (outputs) outputs.Add(output);
            return output;
        };
        f.Server.Start();
        foreach (var bytes in new[] { Encoding.ASCII.GetBytes("broken\r\n\r\n"),
            Request($"Content-Length: 2\r\nX-Agent-Recorder-Key: {ApiKeyAuth.CurrentApiKey}\r\n", "{}", "/api/v1/host/shutdown") })
        {
            Assert.Equal("", await f.Raw(bytes));
            await Until(() => f.Server.ActiveClientCountForTests == 0 && f.Server.ClientTaskCountForTests == 0);
        }
        Assert.Equal(0, exits);
        Assert.Equal(2, outputs.Count);
        Assert.All(outputs, output => Assert.Equal(1, output.Writes));
    }

    [Theory]
    [InlineData("silent")]
    [InlineData("slow_header")]
    [InlineData("paused_body")]
    [InlineData("dripping_body")]
    public async Task TotalReadDeadlineCancelsUnderlyingIo(string kind)
    {
        using var f = new Fixture();
        f.Server.ReadTimeoutForTests = TimeSpan.FromMilliseconds(120);
        var bytes = kind == "silent" ? [] : Request("Content-Length: 100\r\n", kind == "paused_body" ? "a" : "");
        using var input = new Input(bytes, chunk: kind == "slow_header" ? 1 : int.MaxValue,
            waitAtEnd: true, delayEveryRead: kind == "slow_header", dripAtEnd: kind == "dripping_body");
        var watch = Stopwatch.StartNew();
        Assert.Equal(408, (await Assert.ThrowsAnyAsync<ApiException>(() => f.Server.ReadRequest(input))).Status);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(3));
        Assert.True(input.CancellationObserved);
        Assert.Equal(0, input.PendingReads);
        int reads = input.BytesRead;
        await Task.Delay(50);
        Assert.Equal(reads, input.BytesRead);
        Assert.Equal(0, f.Tray.Prompts);
    }

    [Theory]
    [InlineData("write")]
    [InlineData("flush")]
    [InlineData("total")]
    public async Task ResponseDeadlineCancelsBlockedWriteOrFlush(string mode)
    {
        using var f = new Fixture();
        f.Server.WriteTimeoutForTests = TimeSpan.FromMilliseconds(120);
        using var output = new Output(mode);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => f.Server.WriteJson(output, 200, "{}"));
        Assert.True(output.CancellationObserved);
        Assert.Equal(0, output.PendingIo);
    }

    [Fact]
    public async Task StopCancelsReadAndWriteIoRatherThanOnlyAbandoningWaiters()
    {
        using var f = new Fixture();
        using var input = new Input([], waitAtEnd: true);
        using var output = new Output("write");
        var read = f.Server.ReadRequest(input);
        var write = f.Server.WriteJson(output, 200, "{}");
        await output.Entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.True(f.Server.Stop(TimeSpan.FromSeconds(1)));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => read);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => write);
        Assert.True(input.CancellationObserved);
        Assert.True(output.CancellationObserved);
        Assert.Equal(0, input.PendingReads);
        Assert.Equal(0, output.PendingIo);
    }

    [Fact]
    public async Task ConnectionQuotaIsBoundedRecyclesAndDrainsDuringConcurrentStop()
    {
        using var f = new Fixture();
        f.Server.Start();
        var clients = new List<TcpClient>();
        try
        {
            for (int i = 0; i < ApiServer.MaximumAdmittedClients; i++) clients.Add(await f.Connect());
            await Until(() => f.Server.ActiveClientCountForTests == ApiServer.MaximumAdmittedClients);
            Assert.Equal(ApiServer.MaximumAdmittedClients, f.Server.ClientTaskCountForTests);
            using var extra = await f.Connect();
            var buffer = new byte[1];
            try { Assert.Equal(0, await extra.GetStream().ReadAsync(buffer).AsTask().WaitAsync(TimeSpan.FromSeconds(2))); }
            catch (IOException) { }
            Assert.Equal(ApiServer.MaximumAdmittedClients, f.Server.ActiveClientCountForTests);
            clients[0].Dispose();
            await Until(() => f.Server.ActiveClientCountForTests == ApiServer.MaximumAdmittedClients - 1);
            Assert.StartsWith("HTTP/1.1 200 OK", await f.Raw(Encoding.ASCII.GetBytes("GET /api/v1/capabilities HTTP/1.1\r\n\r\n")));
            var stops = await Task.WhenAll(Task.Run(() => f.Server.Stop(TimeSpan.FromSeconds(3))), Task.Run(() => f.Server.Stop(TimeSpan.FromSeconds(3))));
            Assert.All(stops, Assert.True);
            Assert.Equal(0, f.Server.ActiveClientCountForTests);
            Assert.Equal(0, f.Server.ClientTaskCountForTests);
        }
        finally { foreach (var client in clients) client.Dispose(); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BlockedSocketResponseReleasesSlotOnDeadlineOrStopWithoutRetry(bool stop)
    {
        using var f = new Fixture();
        f.Server.WriteTimeoutForTests = TimeSpan.FromMilliseconds(200);
        Output? output = null;
        f.Server.ClientStreamForTests = stream => output = new Output("write", stream);
        f.Server.Start();
        using var tcp = await f.Connect();
        await tcp.GetStream().WriteAsync(Encoding.ASCII.GetBytes("GET /api/v1/capabilities HTTP/1.1\r\n\r\n"));
        await Until(() => output is not null);
        await output!.Entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(1, f.Server.ActiveClientCountForTests);
        if (stop) Assert.True(f.Server.Stop(TimeSpan.FromSeconds(2)));
        await Until(() => f.Server.ActiveClientCountForTests == 0 && f.Server.ClientTaskCountForTests == 0);
        Assert.True(output.CancellationObserved);
        Assert.Equal(1, output.Writes);
        Assert.Equal(0, output.PendingIo);
    }

    [Fact]
    public async Task QuickSelectionCanOutliveReadBudgetAndHalfCloseStillReceivesResponse()
    {
        using var f = new Fixture();
        f.Server.ReadTimeoutForTests = TimeSpan.FromMilliseconds(80);
        f.Tray.DeferSelection = true;
        f.Server.Start();
        var body = "{\"target\":{\"type\":\"selected_region\"},\"duration_seconds\":10}";
        var response = f.Raw(Request($"Content-Length: {Encoding.UTF8.GetByteCount(body)}\r\nX-Agent-Recorder-Key: {ApiKeyAuth.CurrentApiKey}\r\n", body, "/api/v1/recordings/quick"));
        await f.Tray.SelectionEntered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        try
        {
            await Task.Delay(250);
            Assert.False(response.IsCompleted);
        }
        finally { f.Tray.CancelSelection(); }
        var text = await response;
        Assert.StartsWith("HTTP/1.1 200 OK", text);
        Assert.Contains("selection_cancelled", text);
        Assert.Equal(1, f.Tray.Selections);
        Assert.Equal(0, f.Tray.Prompts);
    }

    private static async Task Until(Func<bool> predicate)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!predicate()) await Task.Delay(10, deadline.Token);
    }

    private sealed class Fixture : IDisposable
    {
        private readonly TempDirectory _temp = new();
        private readonly string? _priorDirectory = DataDirResolver.HasOverride ? DataDirResolver.Resolve() : null;
        private readonly Dictionary<FieldInfo, object?> _auth = typeof(ApiKeyAuth).GetFields(BindingFlags.NonPublic | BindingFlags.Static)
            .Where(f => f.Name is "_apiKey" or "_initialized" or "_initializationSource").ToDictionary(f => f, f => f.GetValue(null));
        internal readonly Tray Tray = new();
        internal readonly RecordingEngine Engine;
        internal readonly ApiServer Server;
        internal Fixture()
        {
            try
            {
                ApiKeyAuth.InitializeForTesting(_temp.Path);
                var audit = new AuditLogger(Path.Combine(_temp.Path, "audit.jsonl"));
                Engine = new RecordingEngine(audit);
                Engine.SetTray(Tray);
                Engine.BackendFactory = _ => throw new InvalidOperationException("Capture is forbidden in ingress tests.");
                Server = new ApiServer(Engine, audit, Tray, null, null, null, null, null, null, null, null, null, 0);
            }
            catch { Dispose(); throw; }
        }
        internal async Task<TcpClient> Connect()
        {
            var client = new TcpClient();
            await client.ConnectAsync(IPAddress.Loopback, Server.BoundPort);
            return client;
        }
        internal async Task<string> Raw(byte[] request)
        {
            using var client = await Connect();
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            var stream = client.GetStream();
            await stream.WriteAsync(request, deadline.Token);
            client.Client.Shutdown(SocketShutdown.Send);
            using var reader = new StreamReader(stream, Encoding.UTF8);
            return await reader.ReadToEndAsync(deadline.Token);
        }
        public void Dispose()
        {
            Tray.CancelSelection();
            try { Server?.Stop(); Engine?.Dispose(); }
            finally
            {
                foreach (var pair in _auth) pair.Key.SetValue(null, pair.Value);
                if (_priorDirectory is null) DataDirResolver.ClearOverride(); else DataDirResolver.SetOverride(_priorDirectory);
                _temp.Dispose();
            }
        }
    }

    private sealed class Tray : ITrayContext
    {
        internal int Prompts, Selections;
        internal bool DeferSelection;
        internal TaskCompletionSource SelectionEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private Action<string, int, int, int, int, string, string>? _selection;
        public string HostMode => "headless";
        public bool SupportsRegionSelectionUi => true;
        public void RequestConfirmation(RecordingConfirmationPresentation presentation, Action<ConfirmationDecision> callback) => Interlocked.Increment(ref Prompts);
        public void RequestRegionSelection(int timeoutSeconds, Action<string, int, int, int, int, string, string> callback)
        {
            Interlocked.Increment(ref Selections);
            _selection = callback;
            SelectionEntered.TrySetResult();
            if (!DeferSelection) CancelSelection();
        }
        internal void CancelSelection() => Interlocked.Exchange(ref _selection, null)?.Invoke("selection_cancelled", 0, 0, 0, 0, "", "virtual_screen");
        public void SetRecording(RecordingUiPresentation recording) { }
        public void SetIdle(RecordingUiPresentation recording) { }
        public void SetAllIdle() { }
        public void ShowError(string text) { }
    }

    private sealed class Input(byte[] bytes, int chunk = int.MaxValue, bool waitAtEnd = false,
        bool delayEveryRead = false, bool dripAtEnd = false, bool failAtEnd = false) : MemoryStream
    {
        internal int BytesRead, PendingReads;
        internal bool CancellationObserved;
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token = default)
        {
            Interlocked.Increment(ref PendingReads);
            try
            {
                if (delayEveryRead) await Task.Delay(25, token);
                if (BytesRead < bytes.Length)
                {
                    int count = Math.Min(Math.Min(buffer.Length, chunk), bytes.Length - BytesRead);
                    bytes.AsMemory(BytesRead, count).CopyTo(buffer);
                    BytesRead += count;
                    return count;
                }
                if (failAtEnd) throw new IOException("private transport failure");
                if (dripAtEnd) { await Task.Delay(25, token); buffer.Span[0] = (byte)'x'; return 1; }
                if (waitAtEnd) await Task.Delay(Timeout.Infinite, token);
                return 0;
            }
            catch (OperationCanceledException) { CancellationObserved = true; throw; }
            finally { Interlocked.Decrement(ref PendingReads); }
        }
    }

    private sealed class Output(string mode, Stream? input = null) : MemoryStream
    {
        internal int Writes, PendingIo;
        internal bool CancellationObserved;
        internal readonly TaskCompletionSource Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token = default) => input!.ReadAsync(buffer, token);
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken token = default)
        {
            Interlocked.Increment(ref Writes);
            if (mode == "fail") throw new IOException("synthetic write failure");
            return new ValueTask(Wait(mode == "write", mode == "total", token));
        }
        public override Task FlushAsync(CancellationToken token) => Wait(mode == "flush", false, token);
        private async Task Wait(bool block, bool delay, CancellationToken token)
        {
            Interlocked.Increment(ref PendingIo);
            Entered.TrySetResult();
            try
            {
                if (block) await Task.Delay(Timeout.Infinite, token);
                if (delay) await Task.Delay(80, token);
            }
            catch (OperationCanceledException) { CancellationObserved = true; throw; }
            finally { Interlocked.Decrement(ref PendingIo); }
        }
    }
}
