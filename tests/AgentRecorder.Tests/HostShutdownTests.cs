using System.Net;
using System.Text.Json;
using AgentRecorder.Api;
using AgentRecorder.Capture;
using AgentRecorder.Core;
using AgentRecorder.Infrastructure;
using AgentRecorder.Logging;
using Xunit;

namespace AgentRecorder.Tests;

[Collection("NonParallel-AgentRecorderDataDir")]
public sealed class HostShutdownTests
{
    [Theory]
    [InlineData(null, "POST", 401)]
    [InlineData("wrong", "POST", 401)]
    [InlineData("valid", "GET", 405)]
    [InlineData("valid", "DELETE", 405)]
    public async Task AuthenticationAndMethodNeverExit(string? key, string method, int expected)
    {
        using var host = new Host();
        host.Http.DefaultRequestHeaders.Clear();
        using var request = new HttpRequestMessage(new HttpMethod(method), "api/v1/host/shutdown");
        if (key is not null) request.Headers.Add("X-Agent-Recorder-Key", key == "valid" ? ApiKeyAuth.CurrentApiKey : key);
        using var response = await host.Http.SendAsync(request);
        Assert.Equal(expected, (int)response.StatusCode);
        Assert.Equal(0, host.Exits);
        Assert.False(host.Engine.HostAdmission.IsClosed);
    }

    [Fact]
    public async Task CapabilitiesAdvertiseIdleOnlyAndStaleInstanceHeaderCannotExit()
    {
        using var host = new Host();
        using var caps = await host.Http.GetAsync("api/v1/capabilities");
        var contract = JsonDocument.Parse(await caps.Content.ReadAsStringAsync()).RootElement.GetProperty("data").GetProperty("host_shutdown");
        Assert.True(contract.GetProperty("supported").GetBoolean());
        Assert.True(contract.GetProperty("idle_only").GetBoolean());
        Assert.False(contract.GetProperty("force").GetBoolean());
        Assert.Equal("POST", contract.GetProperty("method").GetString());
        using var request = new HttpRequestMessage(HttpMethod.Post, "api/v1/host/shutdown");
        request.Headers.Add("X-Agent-Recorder-Instance", Guid.NewGuid().ToString("D"));
        using var response = await host.Http.SendAsync(request);
        Assert.Equal(409, (int)response.StatusCode);
        Assert.Contains("HOST_IDENTITY_MISMATCH", await response.Content.ReadAsStringAsync());
        Assert.Equal(0, host.Exits);
        Assert.False(host.Engine.HostAdmission.IsClosed);
    }

    [Fact]
    public async Task PendingResponseCannotPostExitUntilWriteIsReleased()
    {
        using var host = new Host();
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        host.Server.BeforeHostShutdownResponseWriteForTests = () => { entered.Set(); release.Wait(TimeSpan.FromSeconds(3)); };
        var request = host.Post();
        try
        {
            Assert.True(await Task.Run(() => entered.Wait(TimeSpan.FromSeconds(3))));
            Assert.Equal(0, host.Exits);
            Assert.False(request.IsCompleted);
        }
        finally { release.Set(); }
        using var response = await request;
        Assert.Equal(202, (int)response.StatusCode);
        Assert.True(await Task.Run(() => SpinWait.SpinUntil(() => host.Exits == 1, TimeSpan.FromSeconds(3))));
        using var repeat = await host.Post();
        Assert.Equal(1, host.Exits);
    }

    [Fact]
    public async Task IdleResponseIsCompleteBeforeOnePostedExitAndDoesNotWaitForDispatcher()
    {
        using var host = new Host();
        var dispatched = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim();
        host.Server.ConfigureHostShutdown(() => null, () =>
        {
            dispatched.SetResult();
            release.Wait(TimeSpan.FromSeconds(5)); // Fake UI dispatcher blocked; HTTP must still finish.
            Interlocked.Increment(ref host.Exits);
        });
        try
        {
            using var response = await host.Post();
            Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
            var text = await response.Content.ReadAsStringAsync();
            Assert.Equal(response.Content.Headers.ContentLength, System.Text.Encoding.UTF8.GetByteCount(text));
            Assert.Equal("accepted", JsonDocument.Parse(text).RootElement.GetProperty("data").GetProperty("status").GetString());
            await dispatched.Task.WaitAsync(TimeSpan.FromSeconds(3));
            Assert.Equal(0, host.Exits);
            using var duplicate = await host.Post();
            Assert.Equal(HttpStatusCode.Accepted, duplicate.StatusCode);
            Assert.True(host.Engine.HostAdmission.IsClosed);
            using var denied = await host.Http.PostAsync("api/v1/recordings", new StringContent("{}"));
            Assert.Equal(503, (int)denied.StatusCode);
        }
        finally { release.Set(); }
    }

    [Theory]
    [InlineData(RecState.pending_confirmation)]
    [InlineData(RecState.created)]
    [InlineData(RecState.preparing)]
    [InlineData(RecState.countdown)]
    [InlineData(RecState.recording)]
    [InlineData(RecState.stopping)]
    [InlineData(RecState.finalizing)]
    public async Task EveryActiveStateRejectsWithoutClosingOrCancelling(RecState state)
    {
        using var host = new Host();
        var rec = new Recording("rec_busy") { State = state };
        host.Engine._recs.TryAdd(rec.Id, rec);
        using var response = await host.Post();
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Contains("recording_" + state, await response.Content.ReadAsStringAsync());
        Assert.Equal(state, rec.State);
        Assert.False(host.Engine.HostAdmission.IsClosed);
        using var available = await host.Http.GetAsync("api/v1/permissions");
        Assert.Equal(HttpStatusCode.OK, available.StatusCode);
        host.Engine._recs.TryRemove(rec.Id, out _);
        using var retry = await host.Post();
        Assert.Equal(HttpStatusCode.Accepted, retry.StatusCode);
    }

    [Theory]
    [InlineData("selection_ui_in_flight")]
    [InlineData("plan_execution_in_flight")]
    [InlineData("request_in_flight")]
    public async Task ActiveOwnerWinsAndBusyRetryLeavesServiceUsable(string reason)
    {
        using var host = new Host();
        using (host.Engine.HostAdmission.TryEnter(reason))
        {
            using var busy = await host.Post();
            Assert.Equal(409, (int)busy.StatusCode);
            Assert.Contains(reason, await busy.Content.ReadAsStringAsync());
            Assert.False(host.Engine.HostAdmission.IsClosed);
        }
        using var accepted = await host.Post();
        Assert.Equal(202, (int)accepted.StatusCode);
    }

    [Theory]
    [InlineData("interaction_in_flight")]
    [InlineData("deletion_in_flight")]
    [InlineData("deletion_retirement_unknown")]
    [InlineData("owner_state_unknown")]
    public async Task OtherOwnerOrUnknownRejectsThenCanRetry(string reason)
    {
        using var host = new Host();
        host.Busy = reason;
        using var busy = await host.Post();
        Assert.Equal(409, (int)busy.StatusCode);
        Assert.Contains(reason, await busy.Content.ReadAsStringAsync());
        Assert.False(host.Engine.HostAdmission.IsClosed);
        host.Busy = null;
        using var accepted = await host.Post();
        Assert.Equal(202, (int)accepted.StatusCode);
    }

    [Fact]
    public async Task TerminalStatusWithUnknownWorkerIsBusyAndDoesNotCallStop()
    {
        using var host = new Host();
        var worker = new UnknownBackend();
        host.Engine._recs.TryAdd("rec_terminal", new Recording("rec_terminal") { State = RecState.failed, Backend = worker });
        using var busy = await host.Post();
        Assert.Equal(409, (int)busy.StatusCode);
        Assert.Contains("worker_retirement_unknown", await busy.Content.ReadAsStringAsync());
        Assert.Equal(0, worker.Stops);
        Assert.False(host.Engine.HostAdmission.IsClosed);
        host.Engine._recs.Clear();
    }

    [Fact]
    public async Task ForceIsRejectedAndUnconfiguredHostIsUnsupported()
    {
        using var host = new Host();
        using var force = await host.Http.PostAsync("api/v1/host/shutdown", new StringContent("{\"force\":true}"));
        Assert.Equal(400, (int)force.StatusCode);
        Assert.False(host.Engine.HostAdmission.IsClosed);
        using var unsupported = new Host(false);
        using var response = await unsupported.Post();
        Assert.Equal(501, (int)response.StatusCode);
    }

    [Fact]
    public async Task AcceptedShutdownAndNewWorkHaveExactlyOneWinner()
    {
        for (int i = 0; i < 50; i++)
        {
            var gate = new HostWorkAdmission();
            using var start = new ManualResetEventSlim();
            using var release = new ManualResetEventSlim();
            IDisposable? lease = null;
            var work = Task.Run(() => { start.Wait(); lease = gate.TryEnter("deletion_in_flight"); release.Wait(TimeSpan.FromSeconds(3)); });
            var exit = Task.Run(() => { start.Wait(); return gate.TryClose(() => null); });
            start.Set();
            var reason = await exit;
            release.Set();
            await work;
            Assert.Equal(reason is null, lease is null);
            lease?.Dispose();
            Assert.Null(gate.TryClose(() => null));
            Assert.Null(gate.TryEnter("new_recording"));
        }
    }

    internal sealed class QuietTray : ITrayContext
    {
        public string HostMode => "headless";
        public bool SupportsRegionSelectionUi => false;
        public void RequestConfirmation(RecordingConfirmationPresentation p, Action<ConfirmationDecision> callback) => throw new InvalidOperationException();
        public void RequestRegionSelection(int seconds, Action<string, int, int, int, int, string, string> callback) => throw new InvalidOperationException();
        public void SetRecording(RecordingUiPresentation p) { }
        public void SetIdle(RecordingUiPresentation p) { }
        public void SetAllIdle() { }
        public void ShowError(string text) { }
    }
    private sealed class UnknownBackend : ICaptureBackend
    {
        internal int Stops;
        public void Start(CaptureConfig c, CaptureAuthorizationProof p) => throw new InvalidOperationException();
        public OutputMeta Stop() { Stops++; return new(); }
        public void Dispose() { }
    }
    private sealed class Host : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "task365-" + Guid.NewGuid().ToString("N"));
        internal readonly RecordingEngine Engine;
        internal readonly ApiServer Server;
        internal readonly HttpClient Http;
        internal int Exits;
        internal string? Busy;
        internal Host(bool configure = true)
        {
            Directory.CreateDirectory(_root);
            ApiKeyAuth.InitializeForTesting(_root);
            Engine = new RecordingEngine(new AuditLogger());
            Server = new ApiServer(Engine, new AuditLogger(), new QuietTray(), null, null, null, null, null, null, null, null, null, 0);
            if (configure) Server.ConfigureHostShutdown(() => Busy, () => Interlocked.Increment(ref Exits));
            Server.Start();
            Http = new HttpClient(new HttpClientHandler { UseProxy = false }) { BaseAddress = new Uri($"http://127.0.0.1:{Server.BoundPort}/"), Timeout = TimeSpan.FromSeconds(5) };
            Http.DefaultRequestHeaders.Add("X-Agent-Recorder-Key", ApiKeyAuth.CurrentApiKey);
        }
        internal Task<HttpResponseMessage> Post() => Http.PostAsync("api/v1/host/shutdown", new StringContent("{}"));
        public void Dispose()
        {
            Server.Stop(); Engine.Dispose(); Http.Dispose(); ApiKeyAuth.ResetForTesting(null);
        }
    }
}
