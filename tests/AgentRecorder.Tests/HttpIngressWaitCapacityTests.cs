using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Collections.Concurrent;
using System.Text.Json;
using System.Reflection;
using AgentRecorder.Api;
using AgentRecorder.Core;
using AgentRecorder.Infrastructure;
using AgentRecorder.Logging;
using Xunit;

namespace AgentRecorder.Tests;

[Collection("HeadlessHostIntegration")]
public sealed class HttpIngressWaitCapacityTests
{
    [Fact]
    public async Task FullOriginalConnectionLoadCannotExcludeTheRealStopRequest()
    {
        using var f = new Fixture();
        var entered24 = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered32 = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var count = 0;
        f.Engine.BeforeStateWaitBlocksForTests = () =>
        {
            var current = Interlocked.Increment(ref count);
            if (current == 24) entered24.TrySetResult();
            if (current == 32) entered32.TrySetResult();
        };
        var requests = Enumerable.Range(0, 24).Select(_ => f.Raw("GET", f.ConfirmationPath)).ToList();
        try
        {
            await entered24.Task.WaitAsync(TimeSpan.FromSeconds(20));
            var extra = Enumerable.Range(0, 8).Select(_ => f.Raw("GET", f.ConfirmationPath)).ToArray(); requests.AddRange(extra);
            var replies = Task.WhenAll(extra);
            var observed = await Task.WhenAny(replies, entered32.Task).WaitAsync(TimeSpan.FromSeconds(15));
            // Before the fix all 32 remain in the engine wait. After the fix the
            // eight non-admitted requests retire with explicit JSON overload.
            if (observed == replies)
                Assert.All(await replies, reply => Assert.Contains("WAIT_CAPACITY_EXCEEDED", reply));
            else Assert.Equal(32, f.Server.ActiveClientCountForTests);
            Assert.StartsWith("HTTP/1.1 200 OK", await f.Raw("POST", f.StopPath).WaitAsync(TimeSpan.FromSeconds(3)));
            Assert.Equal(1, f.StopCalls); Assert.Equal(RecState.cancelled, f.Recording.State);
        }
        finally { f.Release(); await Task.WhenAll(requests).WaitAsync(TimeSpan.FromSeconds(8)); }
    }

    [Fact]
    public async Task SaturatedConfirmationWaitsRejectExtraAndAuthenticatedStopReachesRealEngine()
    {
        using var f = new Fixture();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var blocks = 0;
        f.Engine.BeforeStateWaitBlocksForTests = () => { if (Interlocked.Increment(ref blocks) == 24) entered.TrySetResult(); };
        var requests = Enumerable.Range(0, 24).Select(_ => f.Raw("GET", f.ConfirmationPath)).ToList();
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(20));
            Assert.Equal(24, f.Server.ActiveLongWaitCountForTests);
            Assert.Equal(32, ApiServer.MaximumAdmittedClients);
            var excess = f.Raw("GET", f.ConfirmationPath); requests.Add(excess);
            Assert.StartsWith("HTTP/1.1 429 Too Many Requests", await excess.WaitAsync(TimeSpan.FromSeconds(2)));
            Assert.Equal(RecState.pending_confirmation, f.Recording.State);
            Assert.StartsWith("HTTP/1.1 403 Forbidden", await f.Raw("POST", f.StopPath, key: "wrong"));
            Assert.Equal(0, f.StopCalls);
            Assert.StartsWith("HTTP/1.1 200 OK", await f.Raw("POST", f.StopPath).WaitAsync(TimeSpan.FromSeconds(3)));
            Assert.Equal(1, f.StopCalls);
            Assert.Equal(RecState.cancelled, f.Recording.State);
        }
        finally
        {
            f.Release();
            await Task.WhenAll(requests).WaitAsync(TimeSpan.FromSeconds(8));
            await Until(() => f.Server.ActiveLongWaitCountForTests == 0);
        }
    }

    [Fact]
    public async Task MixedStatusAndAllSetupWaitsShareBudgetAndCreationIsRejectedBeforeSideEffects()
    {
        using var f = new Fixture();
        var engineEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var setupEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var blocks = 0; var seen = new ConcurrentDictionary<string, byte>();
        f.Engine.BeforeStateWaitBlocksForTests = () => { if (Interlocked.Increment(ref blocks) == 18) engineEntered.TrySetResult(); };
        f.Setup.ReadObserved = id => { seen.TryAdd(id, 0); if (seen.Count == 6) setupEntered.TrySetResult(); };
        var paths = Enumerable.Repeat(f.ConfirmationPath, 9).Concat(Enumerable.Repeat(f.RecordingPath, 9))
            .Concat(new[] { "standing-setup-a", "standing-setup-b", "recurring-setup-a", "recurring-setup-b", "required-once-setup-a", "required-once-setup-b" }
                .Select(id => $"/api/v1/plan-setups/{id}?wait_ms=25000&since_status=setup_pending")).ToArray();
        var requests = paths.Select(path => f.Raw("GET", path)).ToArray();
        try
        {
            await Task.WhenAll(engineEntered.Task, setupEntered.Task).WaitAsync(TimeSpan.FromSeconds(8));
            Assert.Equal(24, f.Server.ActiveLongWaitCountForTests);
            foreach (var path in paths.Distinct())
                Assert.Contains("WAIT_CAPACITY_EXCEEDED", await f.Raw("GET", path).WaitAsync(TimeSpan.FromSeconds(2)));
            foreach (var path in new[] { "/api/v1/recordings?wait_for=recording", "/api/v1/recordings/quick?wait_for=recording&wait_ms=1",
                "/api/v1/recordings", "/api/v1/recordings/quick", "/api/v1/region-selections", "/api/v1/regions/select" })
                Assert.Contains("WAIT_CAPACITY_EXCEEDED", await f.Raw("POST", path, body: "{}").WaitAsync(TimeSpan.FromSeconds(2)));
            Assert.Contains("400 Bad Request", await f.Raw("POST", "/api/v1/recordings?wait_for=invalid", body: "{}"));
            Assert.Contains("400 Bad Request", await f.Raw("GET", "/api/v1/plan-setups/standing-setup-a?wait_ms=bad&since_status=x"));
            foreach (var path in new[] { $"/api/v1/confirmations/{f.Confirmation.Id}", $"/api/v1/recordings/{f.Recording.Id}?wait_ms=25000",
                "/api/v1/plan-setups/standing-setup-a?wait_ms=0&since_status=setup_pending" })
                Assert.StartsWith("HTTP/1.1 200 OK", await f.Raw("GET", path).WaitAsync(TimeSpan.FromSeconds(2)));
            Assert.Equal(24, f.Server.ActiveLongWaitCountForTests);
            Assert.Single(f.Engine._recs); Assert.Single(f.Engine._confs);
            Assert.Equal(0, f.Setup.Creates); Assert.Equal(0, f.Tray.Prompts); Assert.Equal(0, f.Tray.Selections); Assert.Equal(0, f.BackendCalls);
        }
        finally { f.Release(); await Task.WhenAll(requests).WaitAsync(TimeSpan.FromSeconds(8)); }
        await Until(() => f.Server.ActiveLongWaitCountForTests == 0 && f.Server.ActiveClientCountForTests == 0);
    }

    [Fact]
    public async Task CreationWaitConsumesSameBudgetAndNoDuplicateIsCreatedWhenFull()
    {
        using var f = new Fixture(allowCreation: true);
        f.Recording.State = RecState.cancelled; f.Confirmation.Status = "cancelled";
        f.Engine.BeforeStopForTests = null;
        Recording? created = null;
        var creationEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        f.Engine.BeforeCreationWaitBlocksForTests = rec => { created = rec; creationEntered.TrySetResult(); };
        var creation = f.Raw("POST", "/api/v1/recordings?wait_for=recording&wait_ms=25000",
            body: "{\"source\":{\"type\":\"display\",\"display_id\":\"display_1\"},\"stop_condition\":{\"type\":\"duration\",\"seconds\":10}}");
        var waits = new List<Task<string>> { creation };
        try
        {
            var first = await Task.WhenAny(creationEntered.Task, creation).WaitAsync(TimeSpan.FromSeconds(8));
            Assert.True(first == creationEntered.Task, "Creation returned before waiting: " + (creation.IsCompleted ? await creation : "pending"));
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); var count = 0;
            f.Engine.BeforeStateWaitBlocksForTests = () => { if (Interlocked.Increment(ref count) == 23) entered.TrySetResult(); };
            var confirmationPath = $"/api/v1/confirmations/{created!.ConfirmationId}?wait_ms=25000&since_status=pending";
            waits.AddRange(Enumerable.Range(0, 23).Select(_ => f.Raw("GET", confirmationPath)));
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(8));
            Assert.Equal(24, f.Server.ActiveLongWaitCountForTests);
            var recordings = f.Engine._recs.Count; var confirmations = f.Engine._confs.Count;
            Assert.Contains("WAIT_CAPACITY_EXCEEDED", await f.Raw("POST", "/api/v1/recordings/quick?wait_for=recording", body: "{}"));
            Assert.Contains("WAIT_CAPACITY_EXCEEDED", await f.Raw("GET", "/api/v1/plan-setups/standing-setup-a?wait_ms=25000&since_version=0"));
            Assert.Equal(recordings, f.Engine._recs.Count); Assert.Equal(confirmations, f.Engine._confs.Count);
            Assert.Equal(1, f.Tray.Prompts); Assert.Equal(0, f.BackendCalls);
        }
        finally { f.Release(); await Task.WhenAll(waits).WaitAsync(TimeSpan.FromSeconds(8)); }
        Assert.StartsWith("HTTP/1.1 200 OK", await creation);
        await Until(() => f.Server.ActiveLongWaitCountForTests == 0);
    }

    [Fact]
    public async Task SaturateReleaseRepeatAndTimeoutOrExceptionsDoNotLeakWaitSlots()
    {
        using var f = new Fixture();
        for (var cycle = 0; cycle < 2; cycle++)
        {
            f.Recording.State = RecState.pending_confirmation; f.Confirmation.Status = "pending";
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); var count = 0; var poolWorkers = 0;
            f.Engine.BeforeStateWaitBlocksForTests = () =>
            {
                if (Thread.CurrentThread.IsThreadPoolThread) Interlocked.Increment(ref poolWorkers);
                if (Interlocked.Increment(ref count) == 24) entered.TrySetResult();
            };
            var requests = Enumerable.Range(0, 24).Select(_ => f.Raw("GET", f.ConfirmationPath)).ToArray();
            try
            {
                await entered.Task.WaitAsync(TimeSpan.FromSeconds(8));
                Assert.Equal(0, poolWorkers); // No ThreadPool.SetMinThreads or pooled synchronous waits.
                Assert.Equal(24, f.Server.ActiveLongWaitCountForTests);
                Assert.Contains("WAIT_CAPACITY_EXCEEDED", await f.Raw("GET", f.ConfirmationPath));
            }
            finally { f.Release(); await Task.WhenAll(requests).WaitAsync(TimeSpan.FromSeconds(8)); }
            await Until(() => f.Server.ActiveLongWaitCountForTests == 0 && f.Server.ActiveClientCountForTests == 0);
        }
        f.Confirmation.Status = "pending";
        var timedOut = await f.Raw("GET", f.ConfirmationPath.Replace("25000", "30"));
        Assert.StartsWith("HTTP/1.1 200 OK", timedOut); Assert.Contains("\"timed_out\":true", timedOut);
        foreach (var path in new[] { "/api/v1/confirmations/missing?wait_ms=1&since_status=pending", "/api/v1/recordings/missing?wait_ms=1&since_status=x" })
            Assert.StartsWith("HTTP/1.1 404 Not Found", await f.Raw("GET", path));
        await Until(() => f.Server.ActiveLongWaitCountForTests == 0);
        Assert.Equal(0, f.BackendCalls);
    }

    [Fact]
    public async Task StopRetainsQuotaUntilCancellationIgnoringBusinessWaitActuallyReturns()
    {
        using var f = new Fixture(); f.Setup.BlockRead = true;
        var request = f.Raw("GET", "/api/v1/plan-setups/standing-setup-held?wait_ms=25000&since_status=setup_pending");
        try
        {
            await f.Setup.Entered.Task.WaitAsync(TimeSpan.FromSeconds(4));
            Assert.Equal(1, f.Server.ActiveLongWaitCountForTests);
            Assert.False(f.Server.Stop(TimeSpan.FromMilliseconds(25)));
            Assert.False(f.Server.Stop(TimeSpan.FromMilliseconds(25)));
            Assert.Equal(1, f.Server.ActiveLongWaitCountForTests); Assert.Equal(1, f.Server.ActiveClientCountForTests);
        }
        finally { f.Release(); await request.WaitAsync(TimeSpan.FromSeconds(4)); }
        await Until(() => f.Server.ActiveLongWaitCountForTests == 0 && f.Server.ActiveClientCountForTests == 0);
        Assert.True(f.Server.Stop(TimeSpan.FromSeconds(1)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ResponseDeadlineReleasesOnlyAfterActualWriteRetires(bool ignoreCancellation)
    {
        using var f = new Fixture();
        f.Server.WriteTimeoutForTests = TimeSpan.FromMilliseconds(80);
        HeldWrite? output = null;
        f.Server.ClientStreamForTests = stream => output = new HeldWrite(stream, ignoreCancellation);
        var request = f.Raw("GET", f.ConfirmationPath.Replace("since_status=pending", "since_status=changed"));
        try
        {
            await Until(() => output is not null);
            await output!.Entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
            await output.Cancelled.Task.WaitAsync(TimeSpan.FromSeconds(3));
            if (ignoreCancellation)
            {
                Assert.Equal(1, f.Server.ActiveLongWaitCountForTests);
                Assert.False(f.Server.Stop(TimeSpan.FromMilliseconds(25)));
                Assert.Equal(1, f.Server.ActiveLongWaitCountForTests);
            }
        }
        finally { output?.Release.TrySetResult(); await request.WaitAsync(TimeSpan.FromSeconds(4)); }
        await Until(() => f.Server.ActiveLongWaitCountForTests == 0 && f.Server.ActiveClientCountForTests == 0);
        Assert.True(f.Server.Stop(TimeSpan.FromSeconds(1)));
    }

    private static async Task Until(Func<bool> condition)
    { using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(8)); while (!condition()) await Task.Delay(10, deadline.Token); }

    private sealed class Fixture : IDisposable
    {
        private readonly TempDirectory _temp = new();
        private readonly string? _priorDirectory = DataDirResolver.HasOverride ? DataDirResolver.Resolve() : null;
        private readonly Dictionary<FieldInfo, object?> _auth = typeof(ApiKeyAuth).GetFields(BindingFlags.NonPublic | BindingFlags.Static)
            .Where(field => field.Name is "_apiKey" or "_initialized" or "_initializationSource").ToDictionary(field => field, field => field.GetValue(null));
        internal readonly RecordingEngine Engine;
        internal readonly Recording Recording = new() { State = RecState.pending_confirmation, SourceType = "display" };
        internal readonly Confirmation Confirmation = new();
        internal readonly ApiServer Server;
        internal readonly SetupGateway Setup = new();
        internal readonly Tray Tray;
        private readonly bool _allowCreation;
        internal int StopCalls;
        internal int BackendCalls;
        internal string ConfirmationPath => $"/api/v1/confirmations/{Confirmation.Id}?wait_ms=25000&since_status=pending";
        internal string StopPath => $"/api/v1/recordings/{Recording.Id}/stop";
        internal string RecordingPath => $"/api/v1/recordings/{Recording.Id}?wait_ms=25000&since_status=pending_confirmation";
        internal Fixture(bool allowCreation = false)
        {
            _allowCreation = allowCreation;
            ApiKeyAuth.InitializeForTesting(_temp.Path);
            var audit = new AuditLogger(Path.Combine(_temp.Path, "audit.jsonl"));
            Tray = new Tray(allowCreation); Engine = new(audit); Engine.SetTray(Tray);
            Engine.BackendFactory = _ => { Interlocked.Increment(ref BackendCalls); throw new InvalidOperationException("Real capture is forbidden."); };
            if (allowCreation) AgentRecorder.Windows.SystemQuery.SetDisplayProvider(() => new()
                { new("display_1", "Synthetic", true, new(0, 0, 800, 600), 1) });
            Recording.OutputPath = Path.Combine(_temp.Path, "must-not-exist.mp4");
            Confirmation.RecordingId = Recording.Id; Recording.ConfirmationId = Confirmation.Id;
            Engine._recs[Recording.Id] = Recording; Engine._confs[Confirmation.Id] = Confirmation;
            Engine.BeforeStopForTests = rec => { Assert.Same(Recording, rec); Interlocked.Increment(ref StopCalls); };
            Server = new ApiServer(Engine, audit, Tray, null, null, null, null, null, null, Setup, Setup, null, 0,
                requiredOncePlanSetupGateway: Setup);
            Server.Start();
        }
        internal async Task<string> Raw(string method, string path, string? key = null, string body = "")
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(35));
            using var client = new TcpClient(); await client.ConnectAsync(IPAddress.Loopback, Server.BoundPort, deadline.Token);
            var bytes = Encoding.UTF8.GetBytes($"{method} {path} HTTP/1.1\r\nHost: localhost\r\nX-Agent-Recorder-Key: {key ?? ApiKeyAuth.CurrentApiKey}\r\nContent-Length: {Encoding.UTF8.GetByteCount(body)}\r\n\r\n{body}");
            var stream = client.GetStream();
            await stream.WriteAsync(bytes, deadline.Token);
            client.Client.Shutdown(SocketShutdown.Send); // A valid send-half-close still receives the response.
            using var reader = new StreamReader(stream); return await reader.ReadToEndAsync(deadline.Token);
        }
        internal void Release()
        {
            Setup.Completed = true; Setup.Release.Set();
            foreach (var confirmation in Engine._confs.Values) confirmation.Status = "cancelled";
            foreach (var recording in Engine._recs.Values) recording.State = RecState.cancelled;
            Engine.BumpStateVersion();
        }
        public void Dispose()
        {
            Release(); Server.Stop(); Engine.Dispose();
            foreach (var field in _auth) field.Key.SetValue(null, field.Value);
            if (_priorDirectory is null) DataDirResolver.ClearOverride(); else DataDirResolver.SetOverride(_priorDirectory);
            if (_allowCreation) AgentRecorder.Windows.SystemQuery.SetDisplayProvider(null);
            _temp.Dispose();
        }
    }
    private sealed class Tray(bool allowCreation = false) : ITrayContext
    {
        internal int Prompts, Selections;
        public string HostMode => "headless"; public bool SupportsRegionSelectionUi => allowCreation;
        public void RequestConfirmation(RecordingConfirmationPresentation p, Action<ConfirmationDecision> callback)
        { Interlocked.Increment(ref Prompts); if (!allowCreation) throw new InvalidOperationException(); }
        public void RequestRegionSelection(int seconds, Action<string, int, int, int, int, string, string> callback)
        { Interlocked.Increment(ref Selections); throw new InvalidOperationException(); }
        public void SetRecording(RecordingUiPresentation p) { } public void SetIdle(RecordingUiPresentation p) { } public void SetAllIdle() { } public void ShowError(string text) { }
    }
    private sealed class SetupGateway : IStandingPlanSetupGateway, IRecurringPlanSetupGateway, IRequiredOncePlanSetupGateway
    {
        internal Action<string>? ReadObserved;
        internal volatile bool Completed, BlockRead;
        internal readonly ManualResetEventSlim Release = new();
        internal readonly TaskCompletionSource Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal int Creates;
        public bool IsInteractiveDesktopAvailable => true; public bool IsUnattendedEnabled => true;
        public StandingPlanSetupCreateResult CreateOrGet(StandingPlanApiRequest request) { Interlocked.Increment(ref Creates); throw new InvalidOperationException(); }
        public RecurringPlanSetupCreateResult CreateOrGet(RecurringPlanApiRequest request) { Interlocked.Increment(ref Creates); throw new InvalidOperationException(); }
        public RequiredOncePlanSetupCreateResult CreateOrGet(RequiredOncePlanApiRequest request) { Interlocked.Increment(ref Creates); throw new InvalidOperationException(); }
        public StandingPlanSetupState Get(string id)
        {
            ReadObserved?.Invoke(id); Entered.TrySetResult(); if (BlockRead) Release.Wait();
            var required = id.StartsWith("required-once-", StringComparison.Ordinal);
            return new(id, Completed ? "completed" : "setup_pending", 0, null, null, null, false, null, null,
                AuthorizationMode: required ? "required" : "standing_lease", StatusVersionCursor: required ? "required-once-setup/v1:0" : null);
        }
        RecurringPlanSetupState IRecurringPlanSetupGateway.Get(string id)
        {
            ReadObserved?.Invoke(id); Entered.TrySetResult(); if (BlockRead) Release.Wait();
            return new(id, Completed ? "completed" : "setup_pending", 0, "recurring-setup/v1:0", null, null, false, null, null);
        }
    }
    private sealed class HeldWrite(Stream stream, bool ignoreCancellation) : Stream
    {
        internal readonly TaskCompletionSource Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource Cancelled = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override ValueTask<int> ReadAsync(Memory<byte> bytes, CancellationToken ct = default) => stream.ReadAsync(bytes, ct);
        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> bytes, CancellationToken ct = default)
        {
            Entered.TrySetResult(); using var registration = ct.Register(() => Cancelled.TrySetResult());
            if (ignoreCancellation) await Release.Task; else await Task.Delay(Timeout.Infinite, ct);
            throw new IOException("Synthetic retired write.");
        }
        public override bool CanRead => true; public override bool CanSeek => false; public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException(); public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() => stream.Flush(); public override Task FlushAsync(CancellationToken ct) => stream.FlushAsync(ct);
        public override int Read(byte[] b, int o, int c) => stream.Read(b, o, c); public override void Write(byte[] b, int o, int c) => throw new NotSupportedException();
        public override long Seek(long o, SeekOrigin origin) => throw new NotSupportedException(); public override void SetLength(long value) => throw new NotSupportedException();
        protected override void Dispose(bool disposing) { if (disposing) stream.Dispose(); base.Dispose(disposing); }
    }
}
