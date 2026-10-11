using System.Net;
using System.Net.Http;
using AgentRecorder.Api;
using AgentRecorder.Core;
using AgentRecorder.Infrastructure;
using AgentRecorder.Logging;
using Xunit;

namespace AgentRecorder.Tests;

[Collection("HeadlessHostIntegration")]
public sealed class ApiServerShutdownDrainTests
{
    [Fact]
    public async Task RepeatedFiniteAndUnboundedStopWaitForTheRealBlockedRequest()
    {
        using var context = new BlockedReadContext();
        var request = context.StartRequest();
        Assert.True(context.History.Entered.Wait(TimeSpan.FromSeconds(5)), "HTTP detail request did not enter Read gate.");

        Assert.False(context.Server.Stop(TimeSpan.FromMilliseconds(25)));
        Assert.False(context.Server.Stop(TimeSpan.FromMilliseconds(25)));

        var unboundedStop = Task.Run(context.Server.Stop);
        Assert.True(context.WaitForStopWaitCount(3), "Unbounded Stop did not reach client drain wait.");
        Assert.False(unboundedStop.IsCompleted, "Stop() returned while the HTTP route was still blocked.");

        context.History.Release.Set();
        Assert.True(context.History.Exited.Wait(TimeSpan.FromSeconds(5)), "HTTP route did not exit after releasing Read gate.");
        await unboundedStop.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(context.Server.Stop(TimeSpan.FromSeconds(1)));
        context.Server.Stop();
        await ObserveRequestAsync(request);
    }

    [Fact]
    public async Task ConcurrentStopsReportTheirOwnDeadlineAndLaterObserveTrueDrain()
    {
        using var context = new BlockedReadContext();
        var request = context.StartRequest();
        Assert.True(context.History.Entered.Wait(TimeSpan.FromSeconds(5)), "HTTP detail request did not enter Read gate.");

        var shortStop = Task.Run(() => context.Server.Stop(TimeSpan.FromMilliseconds(300)));
        Assert.True(context.WaitForStopWaitCount(1), "First Stop did not reach client drain wait.");
        var longStop = Task.Run(() => context.Server.Stop(TimeSpan.FromSeconds(5)));
        Assert.True(context.WaitForStopWaitCount(2), "Concurrent longer Stop did not reach client drain wait.");
        Assert.False(await shortStop.WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.False(longStop.IsCompleted, "Concurrent longer Stop returned before the route exited.");

        context.History.Release.Set();
        Assert.True(context.History.Exited.Wait(TimeSpan.FromSeconds(5)), "HTTP route did not exit after releasing Read gate.");
        Assert.True(await longStop.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.True(context.Server.Stop(TimeSpan.FromMilliseconds(25)));
        await ObserveRequestAsync(request);
    }

    [Fact]
    public async Task HostShutdownRetryDoesNotDisposeHistoryUntilRealClientDrain()
    {
        using var context = new BlockedReadContext();
        var request = context.StartRequest();
        Assert.True(context.History.Entered.Wait(TimeSpan.FromSeconds(5)), "HTTP detail request did not enter Read gate.");
        var auditEvents = new List<string>();
        Action<string, object> audit = (name, _) => auditEvents.Add(name);

        Assert.False(HostRecordingShutdown.Stop(context.Server, context.Engine, context.History,
            "test_shutdown", TimeSpan.FromMilliseconds(25), TimeSpan.Zero, TimeSpan.FromMilliseconds(25), audit));
        Assert.Equal(0, context.History.DrainCount);
        Assert.False(HostRecordingShutdown.Stop(context.Server, context.Engine, context.History,
            "test_shutdown_retry", TimeSpan.FromMilliseconds(25), TimeSpan.Zero, TimeSpan.FromMilliseconds(25), audit));
        Assert.Equal(0, context.History.DrainCount);
        Assert.Contains("service.recording_history_shutdown_incomplete", auditEvents);

        context.History.Release.Set();
        Assert.True(context.History.Exited.Wait(TimeSpan.FromSeconds(5)), "HTTP route did not exit after releasing Read gate.");
        Assert.True(HostRecordingShutdown.Stop(context.Server, context.Engine, context.History,
            "test_shutdown_after_drain", TimeSpan.FromSeconds(1), TimeSpan.Zero, TimeSpan.FromSeconds(1), audit));
        Assert.Equal(1, context.History.DrainCount);
        await ObserveRequestAsync(request);
    }

    private static async Task ObserveRequestAsync(Task<HttpResponseMessage> request)
    {
        try
        {
            using var response = await request.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        }
        catch (HttpRequestException)
        {
            // Stop closes the accepted socket. A transport failure is expected;
            // the server-side Read gate is separately asserted to have exited.
        }
    }

    private sealed class BlockedReadContext : IDisposable
    {
        private readonly string _root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,
            "..", "..", "..", "..", "..", ".local-data", "temp", "task309t", Guid.NewGuid().ToString("N")));
        private readonly HttpClient _client;
        private int _stopWaitCount;

        public BlockedReadContext()
        {
            Directory.CreateDirectory(_root);
            ApiKeyAuth.InitializeForTesting(_root);
            var audit = new AuditLogger();
            Engine = new RecordingEngine(audit);
            History = new GateHistory();
            Engine.ConfigureOrdinaryRecordingHistory(History);
            var tray = new QuietTray();
            Server = new ApiServer(Engine, audit, tray, null, null, null, null, null, null, null,
                null, null, 0);
            Server.BeforeStopClientDrainWaitForTests = () =>
            {
                Interlocked.Increment(ref _stopWaitCount);
            };
            Server.Start();
            _client = new HttpClient(new HttpClientHandler { UseProxy = false })
            {
                BaseAddress = new Uri($"http://127.0.0.1:{Server.BoundPort}"),
                Timeout = TimeSpan.FromSeconds(10)
            };
            _client.DefaultRequestHeaders.Add("X-Agent-Recorder-Key", ApiKeyAuth.CurrentApiKey);
        }

        public RecordingEngine Engine { get; }
        public GateHistory History { get; }
        public ApiServer Server { get; }
        public int StopWaitCount => Volatile.Read(ref _stopWaitCount);

        public bool WaitForStopWaitCount(int count) =>
            SpinWait.SpinUntil(() => StopWaitCount >= count, TimeSpan.FromSeconds(5));

        public Task<HttpResponseMessage> StartRequest() =>
            _client.GetAsync("/api/v1/recording-history/rec_blocked");

        public void Dispose()
        {
            History.Release.Set();
            try { Server.Stop(); } catch { }
            try { Engine.Dispose(); } catch { }
            _client.Dispose();
            History.Dispose();
            ApiKeyAuth.ResetForTesting(null);
            try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); } catch { }
        }
    }

    private sealed class GateHistory : IOrdinaryRecordingHistoryStore, IOrdinaryRecordingHistoryDrainable
    {
        private int _drainCount;
        public ManualResetEventSlim Entered { get; } = new(false);
        public ManualResetEventSlim Release { get; } = new(false);
        public ManualResetEventSlim Exited { get; } = new(false);
        public int DrainCount => Volatile.Read(ref _drainCount);

        public void Register(OrdinaryRecordingHistorySnapshot snapshot) => throw new NotSupportedException();
        public bool TryPublish(OrdinaryRecordingHistorySnapshot snapshot) => false;
        public OrdinaryRecordingHistoryPage ReadPage(int limit, string? cursor) => throw new NotSupportedException();

        public OrdinaryRecordingHistorySnapshot? Read(string recordingId)
        {
            Entered.Set();
            try
            {
                if (!Release.Wait(TimeSpan.FromSeconds(10)))
                    throw new TimeoutException("HTTP history Read gate exceeded its bounded test deadline.");
                return null;
            }
            finally { Exited.Set(); }
        }

        public void RecoverIncomplete(DateTimeOffset recoveredAtUtc) { }
        public bool DrainAndDispose(TimeSpan timeout)
        {
            Interlocked.Increment(ref _drainCount);
            Release.Set();
            return true;
        }
        public void Dispose() => Release.Set();
    }

    private sealed class QuietTray : ITrayContext
    {
        public string HostMode => "headless";
        public bool SupportsRegionSelectionUi => false;
        public void RequestConfirmation(RecordingConfirmationPresentation presentation, Action<ConfirmationDecision> callback) =>
            throw new InvalidOperationException("No recording confirmation is expected in this test.");
        public void RequestRegionSelection(int timeoutSeconds, Action<string, int, int, int, int, string, string> callback) =>
            throw new InvalidOperationException("No region selection is expected in this test.");
        public void SetRecording(RecordingUiPresentation rec) { }
        public void SetIdle(RecordingUiPresentation rec) { }
        public void SetAllIdle() { }
        public void ShowError(string text) { }
    }
}
