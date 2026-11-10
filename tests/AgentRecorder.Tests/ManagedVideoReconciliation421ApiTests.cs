using System.Net;
using System.Text;
using System.Text.Json;
using AgentRecorder.Api;
using AgentRecorder.Core;
using AgentRecorder.Infrastructure;
using AgentRecorder.Persistence;
using Xunit;

namespace AgentRecorder.Tests;

public sealed partial class ManagedVideoReconciliation421Tests
{
    private const string Endpoint = "/api/v1/retention/reconciliation";

    [Fact]
    public async Task HttpChecksAuthAndFixedScopeAndOnlyReturnsAggregateData()
    {
        using var f = new Fixture();
        var path = f.File("private-candidate.mp4", 41);
        f.Register("private-id", path);
        int dispatches = 0;
        using var host = new HttpHost(f, new(f.Db.Store, f.Db.Root)
        {
            AfterListingForTests = () => Interlocked.Increment(ref dispatches)
        }, "tray");

        var cap = (await host.Send(HttpMethod.Get, "/api/v1/capabilities", HttpStatusCode.OK)).GetProperty("data")
            .GetProperty("retention_reconciliation");
        Assert.True(cap.GetProperty("supported").GetBoolean());
        Assert.True(cap.GetProperty("read_only").GetBoolean());
        Assert.Equal(Endpoint, cap.GetProperty("endpoint").GetString());
        Assert.Equal(1, cap.GetProperty("actual_workers_max").GetInt32());
        await host.Send(HttpMethod.Get, Endpoint, HttpStatusCode.Unauthorized, authenticate: false);
        await host.Send(HttpMethod.Get, Endpoint, HttpStatusCode.Forbidden, key: "wrong");
        await host.Send(HttpMethod.Post, Endpoint, HttpStatusCode.MethodNotAllowed);
        await host.Send(HttpMethod.Get, Endpoint + "?", HttpStatusCode.BadRequest);
        await host.Send(HttpMethod.Get, Endpoint + "?cursor=unsafe", HttpStatusCode.BadRequest);
        await host.Send(HttpMethod.Get, Endpoint + "/Videos", HttpStatusCode.NotFound);
        await host.Send(HttpMethod.Get, Endpoint, HttpStatusCode.BadRequest, body: "{}");
        Assert.Equal(0, dispatches);

        var response = await host.Send(HttpMethod.Get, Endpoint, HttpStatusCode.OK);
        var data = response.GetProperty("data");
        Assert.Equal(new[] { "complete", "files_without_matching_registration", "matched_files", "missing_registrations",
            "reason_codes", "root_state", "sampled_at_utc", "scope", "uncertain_candidates" },
            data.EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal));
        Assert.True(data.GetProperty("complete").GetBoolean());
        Assert.Equal(1, data.GetProperty("matched_files").GetInt32());
        Assert.DoesNotContain("private-", response.GetRawText(), StringComparison.OrdinalIgnoreCase);
        Assert.Equal(1, dispatches);
    }

    [Fact]
    public async Task HeadlessApiStopWaitsForNativeWorkerThatOutlivedHttpDeadline()
    {
        using var f = new Fixture();
        f.File("owned.mp4", 16);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim();
        var deadline = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var host = new HttpHost(f, new(f.Db.Store, f.Db.Root)
        {
            AfterListingForTests = () => { entered.TrySetResult(); release.Wait(Bound); },
            DeadlineForTests = token => deadline.Task.WaitAsync(token)
        }, "headless");
        var call = host.Send(HttpMethod.Get, Endpoint, HttpStatusCode.ServiceUnavailable);
        try
        {
            await entered.Task.WaitAsync(Bound);
            deadline.TrySetResult();
            var response = await call.WaitAsync(Bound);
            Assert.False(response.TryGetProperty("data", out _));
            Assert.Equal("RETENTION_RECONCILIATION_UNAVAILABLE", response.GetProperty("error").GetProperty("code").GetString());
            Assert.Equal("timeout", response.GetProperty("error").GetProperty("details").GetProperty("reason_code").GetString());
            Assert.False(host.Server.Stop(TimeSpan.FromMilliseconds(250)));
        }
        finally { release.Set(); }
        Assert.True(host.Server.Stop(Bound));
    }

    private sealed class HttpHost : IDisposable
    {
        private readonly RecordingEngine _engine;
        private readonly RuntimeReadiness _readiness;
        private readonly HttpClient _http;
        private readonly string _key;
        internal ApiServer Server { get; }

        internal HttpHost(Fixture f, ManagedVideoReconciliation reconciliation, string mode)
        {
            ApiKeyAuth.InitializeForTesting(f.Db.Root);
            _key = ApiKeyAuth.CurrentApiKey;
            var desktop = new InteractiveDesktopObservation(1, 1, "owned", 1, "owned", "WinSta0", "Default", "Default", "");
            _readiness = new RuntimeReadiness(mode, 0, desktop, @"Local\AgentRecorderTask421-" + Guid.NewGuid().ToString("N"));
            var audit = new RetentionSharedOutputPathTests.QuietAudit();
            _engine = new RecordingEngine(audit);
            var tray = new DeletionRequestFixture.Tray(mode == "headless");
            _engine.SetTray(tray);
            Server = new ApiServer(_engine, audit, tray, _readiness, null, null, null, null, null, null, null, null, 0);
            Server.SetRetentionReconciliation(reconciliation);
            Server.Start();
            _readiness.MarkReady();
            _http = new HttpClient(new HttpClientHandler { UseProxy = false })
            {
                BaseAddress = new Uri($"http://127.0.0.1:{Server.BoundPort}"), Timeout = Bound
            };
        }

        internal async Task<JsonElement> Send(HttpMethod method, string path, HttpStatusCode expected,
            bool authenticate = true, string? key = null, string? body = null)
        {
            using var request = new HttpRequestMessage(method, path);
            if (authenticate) request.Headers.Add("X-Agent-Recorder-Key", key ?? _key);
            if (body is not null) request.Content = new StringContent(body, Encoding.UTF8, "application/json");
            using var response = await _http.SendAsync(request);
            Assert.Equal(expected, response.StatusCode);
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            return json.RootElement.Clone();
        }

        public void Dispose()
        {
            Server.Stop(Bound);
            _http.Dispose();
            _engine.Dispose();
            _readiness.Dispose();
            ApiKeyAuth.ResetForTesting(null);
        }
    }
}
