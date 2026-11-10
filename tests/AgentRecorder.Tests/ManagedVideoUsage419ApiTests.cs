using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using AgentRecorder.Api;
using AgentRecorder.Core;
using AgentRecorder.Infrastructure;
using AgentRecorder.Persistence;
using Xunit;

namespace AgentRecorder.Tests;

[Collection("NonParallel-AgentRecorderDataDir")]
public sealed class ManagedVideoUsage419ApiTests
{
    private const string Endpoint = "/api/v1/retention/usage";
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(7);

    [Fact]
    public async Task CapabilityAuthenticationAndRequestValidationDoNotStartWorker()
    {
        using var host = new Host();
        using var cap = await host.Send(HttpMethod.Get, "/api/v1/capabilities", HttpStatusCode.OK);
        var usage = cap.RootElement.GetProperty("data").GetProperty("retention_usage");
        Assert.True(usage.GetProperty("supported").GetBoolean());
        Assert.Equal(Endpoint, usage.GetProperty("endpoint").GetString());
        Assert.Equal(1, usage.GetProperty("actual_workers_max").GetInt32());
        Assert.True(usage.GetProperty("read_only").GetBoolean());

        using var noKey = await host.Send(HttpMethod.Get, Endpoint, HttpStatusCode.Unauthorized, auth: false);
        using var wrongKey = await host.Send(HttpMethod.Get, Endpoint, HttpStatusCode.Forbidden, key: "wrong");
        using var method = await host.Send(HttpMethod.Post, Endpoint, HttpStatusCode.MethodNotAllowed);
        using var query = await host.Send(HttpMethod.Get, Endpoint + "?path=Videos", HttpStatusCode.BadRequest);
        using var bareQuery = await host.Send(HttpMethod.Get, Endpoint + "?", HttpStatusCode.BadRequest);
        using var child = await host.Send(HttpMethod.Get, Endpoint + "/Videos", HttpStatusCode.NotFound);
        foreach (var framing in new[] { "Content-Length: 2\r\n", "Content-Length: -0\r\n",
            "Content-Length: 0\r\nContent-Length: 0\r\n", "Transfer-Encoding: chunked\r\n" })
        {
            using var raw = await host.OpenRaw(framing);
            using var reader = new StreamReader(raw.GetStream());
            Assert.Contains("HTTP/1.1 400", await reader.ReadToEndAsync().WaitAsync(Bound));
        }
        Assert.Equal(0, host.Dispatches);

        using var response = await host.Send(HttpMethod.Get, Endpoint, HttpStatusCode.OK);
        var data = response.RootElement.GetProperty("data");
        Assert.Equal(0, data.GetProperty("recorded_logical_bytes").GetInt64());
        Assert.True(data.GetProperty("complete").GetBoolean());
        Assert.Equal(1, host.Dispatches);
    }

    [Fact]
    public async Task ResetOrDeadlineKeepsAppExitProtectedUntilActualMetadataWorkerRetires()
    {
        using var release = new ManualResetEventSlim();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var retired = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var host = new Host(() => { entered.TrySetResult(); release.Wait(Bound); }, () => retired.TrySetResult());
        var client = await host.OpenRaw("");
        try
        {
            await entered.Task.WaitAsync(Bound);
            client.Client.LingerState = new LingerOption(true, 0);
            client.Dispose();
            // Wait for HTTP request retirement while the actual SQLite worker is still blocked.
            await host.RequestRetired.Task.WaitAsync(Bound);
            using var busy = await host.Send(HttpMethod.Get, Endpoint, HttpStatusCode.ServiceUnavailable);
            AssertReason(busy, "RETENTION_USAGE_UNAVAILABLE", "busy");
            using var shutdown = await host.Send(HttpMethod.Post, "/api/v1/host/shutdown", HttpStatusCode.Conflict, "{}");
            AssertReason(shutdown, "HOST_BUSY", "artifact_metadata_worker_pending");
            Assert.Equal(1, host.Dispatches);
        }
        finally
        {
            client.Dispose();
            release.Set();
            await retired.Task.WaitAsync(Bound);
        }
        using var response = await host.Send(HttpMethod.Get, Endpoint, HttpStatusCode.OK);
        Assert.Equal(2, host.Dispatches);
    }

    private static void AssertReason(JsonDocument json, string code, string reason)
    {
        Assert.False(json.RootElement.TryGetProperty("data", out _));
        var error = json.RootElement.GetProperty("error");
        Assert.Equal(code, error.GetProperty("code").GetString());
        Assert.Equal(reason, error.GetProperty("details").GetProperty("reason_code").GetString());
    }

    private sealed class Host : IDisposable
    {
        private readonly UnifiedHistoryTestDatabase _database = new(Path.Combine(Path.GetTempPath(), "task419-api-" + Guid.NewGuid().ToString("N")));
        private readonly RecordingEngine _engine;
        private readonly RuntimeReadiness _readiness;
        private readonly ApiServer _server;
        private readonly HttpClient _http;
        private readonly string _key;
        internal readonly TaskCompletionSource RequestRetired = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal int Dispatches;

        internal Host(Action? entered = null, Action? retired = null)
        {
            ApiKeyAuth.InitializeForTesting(_database.Root);
            _key = ApiKeyAuth.CurrentApiKey;
            var desktop = new InteractiveDesktopObservation(1, 1, "owned", 1, "owned", "WinSta0", "Default", "Default", "");
            _readiness = new RuntimeReadiness("tray", 0, desktop, @"Local\AgentRecorderTask419-" + Guid.NewGuid().ToString("N"));
            var audit = new RetentionSharedOutputPathTests.QuietAudit();
            _engine = new RecordingEngine(audit);
            var tray = new DeletionRequestFixture.Tray(false);
            _engine.SetTray(tray);
            _server = new ApiServer(_engine, audit, tray, _readiness, null, null, null, null, null, null, null, null, 0);
            _server.SetRetentionUsage(new(_database.Store, _database.Root)
            {
                BeforeReadForTests = () => { Interlocked.Increment(ref Dispatches); entered?.Invoke(); },
                WorkerRetiredForTests = retired
            });
            _server.ConfigureHostShutdown(() => HostReadOnlyWorkerState.BusyReason, () => { });
            _server.Start();
            _readiness.MarkReady();
            _http = new HttpClient(new HttpClientHandler { UseProxy = false })
            {
                BaseAddress = new Uri($"http://127.0.0.1:{_server.BoundPort}"), Timeout = Bound
            };
        }

        internal async Task<JsonDocument> Send(HttpMethod method, string path, HttpStatusCode status,
            string? body = null, bool auth = true, string? key = null)
        {
            using var request = new HttpRequestMessage(method, path);
            if (auth) request.Headers.Add("X-Agent-Recorder-Key", key ?? _key);
            if (body is not null) request.Content = new StringContent(body, Encoding.UTF8, "application/json");
            using var response = await _http.SendAsync(request);
            Assert.Equal(status, response.StatusCode);
            return JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        }

        internal async Task<TcpClient> OpenRaw(string headers)
        {
            var client = new TcpClient();
            try
            {
                await client.ConnectAsync(IPAddress.Loopback, _server.BoundPort);
                var request = $"GET {Endpoint} HTTP/1.1\r\nHost: localhost\r\nX-Agent-Recorder-Key: {_key}\r\n{headers}Connection: close\r\n\r\n";
                await client.GetStream().WriteAsync(Encoding.UTF8.GetBytes(request));
                if (headers.Length == 0) _ = ObserveRequestRetirement();
                return client;
            }
            catch { client.Dispose(); throw; }
        }

        private async Task ObserveRequestRetirement()
        {
            try
            {
                using var timeout = new CancellationTokenSource(Bound);
                while (Volatile.Read(ref Dispatches) == 0) await Task.Delay(15, timeout.Token);
                while (_engine.HostAdmission.TryClose(() => "test_idle") == "request_in_flight")
                    await Task.Delay(15, timeout.Token);
                RequestRetired.TrySetResult();
            }
            catch (Exception ex) { RequestRetired.TrySetException(ex); }
        }

        public void Dispose()
        {
            _server.Stop(Bound);
            _http.Dispose();
            _engine.Dispose();
            _readiness.Dispose();
            ApiKeyAuth.ResetForTesting(null);
            _database.Dispose();
        }
    }
}
