using System.Text.Json.Nodes;
using AgentRecorder.Capture;
using AgentRecorder.Core;
using AgentRecorder.Api;
using AgentRecorder.Infrastructure;
using AgentRecorder.Logging;
using AgentRecorder.Persistence;
using AgentRecorder.Windows;
using Xunit;

namespace AgentRecorder.Tests;

[Collection("NonParallel-AgentRecorderDataDir")]
public sealed class OrdinaryRecordingHistoryLifecycleRaceTests : IDisposable
{
    private readonly string _root = Path.GetFullPath(Path.Combine(
        AppContext.BaseDirectory, "..", "..", "..", "..", "..", ".local-data", "temp", "task309s", "engine", Guid.NewGuid().ToString("N")));
    private readonly string? _priorTestMode = Environment.GetEnvironmentVariable("AGENT_RECORDER_TEST_MODE");

    public OrdinaryRecordingHistoryLifecycleRaceTests()
    {
        Directory.CreateDirectory(_root);
        Environment.SetEnvironmentVariable("AGENT_RECORDER_TEST_MODE", "1");
        DataDirResolver.SetOverride(_root);
        SystemQuery.SetDisplayProvider(() => new List<SystemQuery.DisplayInfo>
        {
            new("display_1", "Display 1", true, new SystemQuery.Bounds(0, 0, 1920, 1080), 1.0)
        });
    }

    [Theory]
    [InlineData("stop")]
    [InlineData("stop_all")]
    [InlineData("dispose")]
    public async Task StopOrCloseWinningDuringRegisterIsPersistedAndNeverRequestsConfirmation(string winner)
    {
        var database = new SqliteOperationalStore(Path.Combine(_root, winner, "agent-recorder.db"));
        database.Initialize();
        using var sqliteHistory = new SqliteOrdinaryRecordingHistoryStore(database);
        var gatedHistory = new RegisterGateHistory(sqliteHistory);
        var tray = new HoldingConfirmationTray();
        var engine = new RecordingEngine(new AuditLogger());
        engine.SetTray(tray);
        engine.BackendFactory = _ => (new NeverStartedBackend(), "fake");
        engine.ConfigureOrdinaryRecordingHistory(gatedHistory);

        var create = Task.Run(() => engine.CreateRecording(Request(), "task309r-test", tray));
        Assert.True(gatedHistory.RegisterEntered.Wait(TimeSpan.FromSeconds(5)), "Register gate was not entered.");
        var recording = Assert.Single(engine._recs.Values);
        Task? disposal = null;

        switch (winner)
        {
            case "stop":
                engine.Stop(recording.Id, "user");
                break;
            case "stop_all":
                engine.StopAllSync("user");
                break;
            case "dispose":
                disposal = Task.Run(engine.Dispose);
                Assert.True(SpinWait.SpinUntil(() => recording.State == RecState.cancelled, TimeSpan.FromSeconds(5)),
                    "Dispose did not cancel the pending registration.");
                break;
        }
        Assert.Equal(RecState.cancelled, recording.State);

        gatedHistory.ReleaseRegister.Set();
        var createResult = await create.WaitAsync(TimeSpan.FromSeconds(5));
        if (disposal is not null) await disposal.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("cancelled", ReadStatus(createResult));
        Assert.Equal(0, tray.ConfirmationRequests);

        var persisted = sqliteHistory.Read(recording.Id);
        Assert.NotNull(persisted);
        Assert.Equal("cancelled", persisted!.Status);
        Assert.False(string.IsNullOrWhiteSpace(persisted.ReasonCode));
        Assert.NotNull(persisted.CompletedAtUtc);
        if (disposal is null) engine.Dispose();
    }

    [Fact]
    public async Task FailedRegisterDuringStopRemovesMemoryEntryAndDoesNotRequestConfirmation()
    {
        var database = new SqliteOperationalStore(Path.Combine(_root, "register-failure", "agent-recorder.db"));
        database.Initialize();
        using var sqliteHistory = new SqliteOrdinaryRecordingHistoryStore(database);
        var gatedHistory = new RegisterGateHistory(sqliteHistory, failAfterRelease: true);
        var tray = new HoldingConfirmationTray();
        var engine = new RecordingEngine(new AuditLogger());
        engine.SetTray(tray);
        engine.BackendFactory = _ => (new NeverStartedBackend(), "fake");
        engine.ConfigureOrdinaryRecordingHistory(gatedHistory);

        var create = Task.Run(() => Record.Exception(() => engine.CreateRecording(Request(), "task309r-test", tray)));
        Assert.True(gatedHistory.RegisterEntered.Wait(TimeSpan.FromSeconds(5)), "Register gate was not entered.");
        var recording = Assert.Single(engine._recs.Values);
        engine.Stop(recording.Id, "user");
        gatedHistory.ReleaseRegister.Set();

        var createError = await create.WaitAsync(TimeSpan.FromSeconds(5));
        var error = Assert.IsType<ApiException>(createError);
        Assert.Equal(503, error.Status);
        Assert.Empty(engine._recs);
        Assert.Equal(0, tray.ConfirmationRequests);
        Assert.Null(sqliteHistory.Read(recording.Id));
        engine.Dispose();
    }

    [Theory]
    [InlineData("app")]
    [InlineData("headless")]
    public async Task ProductionHostShutdownPathCancelsAndSettlesBlockedRegistration(string host)
    {
        var database = new SqliteOperationalStore(Path.Combine(_root, host, "agent-recorder.db"));
        database.Initialize();
        using var sqliteHistory = new SqliteOrdinaryRecordingHistoryStore(database);
        var gatedHistory = new RegisterGateHistory(sqliteHistory);
        var tray = new HoldingConfirmationTray();
        var audit = new AuditLogger();
        var engine = new RecordingEngine(audit);
        engine.SetTray(tray);
        engine.BackendFactory = _ => (new NeverStartedBackend(), "fake");
        engine.ConfigureOrdinaryRecordingHistory(gatedHistory);
        var server = new ApiServer(engine, audit, tray, null, null, null, null, null, null, null, null, null, 0);
        server.Start();

        var create = Task.Run(() => engine.CreateRecording(Request($"{host}.mp4"), "task309s-host-shutdown", tray));
        Assert.True(gatedHistory.RegisterEntered.Wait(TimeSpan.FromSeconds(5)), "Register gate was not entered.");
        var recording = Assert.Single(engine._recs.Values);
        var shutdown = Task.Run(() => HostRecordingShutdown.Stop(server, engine, sqliteHistory,
            $"{host}_host_shutdown"));
        Assert.True(SpinWait.SpinUntil(() => recording.State == RecState.cancelled, TimeSpan.FromSeconds(5)),
            "Host shutdown did not cancel pending confirmation.");
        gatedHistory.ReleaseRegister.Set();

        Assert.True(await shutdown.WaitAsync(TimeSpan.FromSeconds(5)));
        var createResult = await create.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("cancelled", ReadStatus(createResult));
        Assert.Equal(0, tray.ConfirmationRequests);
        var persisted = sqliteHistory.Read(recording.Id);
        Assert.NotNull(persisted);
        Assert.Equal("cancelled", persisted!.Status);
        Assert.Equal($"{host}_host_shutdown", persisted.ReasonCode);
        Assert.NotNull(persisted.CompletedAtUtc);
        Assert.Equal(recording.CompletedAtUtc, persisted.CompletedAtUtc);
        var afterShutdown = Record.Exception(() => engine.CreateRecording(Request("after-shutdown.mp4"), "task309s-after-shutdown", tray));
        Assert.Equal(503, Assert.IsType<ApiException>(afterShutdown).Status);
    }

    [Fact]
    public async Task ShutdownDrainTimeoutLeavesHistoryWriterOpenAndReportsIncomplete()
    {
        var database = new SqliteOperationalStore(Path.Combine(_root, "shutdown-timeout", "agent-recorder.db"));
        database.Initialize();
        using var sqliteHistory = new SqliteOrdinaryRecordingHistoryStore(database);
        var gatedHistory = new RegisterGateHistory(sqliteHistory);
        var tray = new HoldingConfirmationTray();
        var audit = new AuditLogger();
        var engine = new RecordingEngine(audit) { OrdinaryHistoryRegistrationDrainTimeoutForTests = TimeSpan.FromMilliseconds(20) };
        engine.SetTray(tray);
        engine.BackendFactory = _ => (new NeverStartedBackend(), "fake");
        engine.ConfigureOrdinaryRecordingHistory(gatedHistory);
        var server = new ApiServer(engine, audit, tray, null, null, null, null, null, null, null, null, null, 0);
        server.Start();
        var create = Task.Run(() => engine.CreateRecording(Request("timeout.mp4"), "task309s-host-timeout", tray));
        Assert.True(gatedHistory.RegisterEntered.Wait(TimeSpan.FromSeconds(5)), "Register gate was not entered.");
        var recording = Assert.Single(engine._recs.Values);
        var auditEvents = new List<string>();
        var shutdownSettled = HostRecordingShutdown.Stop(server, engine, sqliteHistory, "timeout_shutdown",
            TimeSpan.FromSeconds(1), TimeSpan.FromMilliseconds(20), TimeSpan.FromSeconds(2),
            (name, _) => auditEvents.Add(name));
        Assert.False(shutdownSettled);
        Assert.Contains("service.recording_history_shutdown_incomplete", auditEvents);
        Assert.Equal(RecState.cancelled, recording.State);

        gatedHistory.ReleaseRegister.Set();
        var createResult = await create.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("cancelled", ReadStatus(createResult));
        var persisted = sqliteHistory.Read(recording.Id)!;
        Assert.Equal("cancelled", persisted.Status);
        Assert.Equal("timeout_shutdown", persisted.ReasonCode);
        Assert.True(sqliteHistory.TryPublish(persisted), "Timed-out shutdown must leave the history writer available for the in-flight Register replay.");
    }

    [Fact]
    public void HistoryWriterDrainTimeoutIsNotReportedAsSettledAndRestartRecoveryIsReadable()
    {
        var databasePath = Path.Combine(_root, "writer-timeout", "agent-recorder.db");
        var database = new SqliteOperationalStore(databasePath);
        database.Initialize();
        var history = new SqliteOrdinaryRecordingHistoryStore(database);
        var created = DateTimeOffset.UtcNow;
        var accepted = new OrdinaryRecordingHistorySnapshot("rec_writer_timeout", created, "preparing", "display",
            "display_capture", "Display", 10, "none", null, null, null, null,
            Path.Combine(_root, "writer-timeout.mp4"), null, null, null, null, null, null,
            null, null, null, null, null);
        history.Register(accepted);
        using var writerEntered = new ManualResetEventSlim();
        using var releaseWriter = new ManualResetEventSlim();
        history.BeforePersistForTests = _ =>
        {
            writerEntered.Set();
            if (!releaseWriter.Wait(TimeSpan.FromSeconds(5)))
                throw new TimeoutException("Timed out waiting for the history writer timeout test gate.");
        };
        Assert.True(history.TryPublish(accepted with { Status = "recording" }));
        Assert.True(writerEntered.Wait(TimeSpan.FromSeconds(5)), "History writer did not reach its bounded gate.");

        var auditEvents = new List<string>();
        var audit = new AuditLogger();
        var engine = new RecordingEngine(audit);
        var tray = new HoldingConfirmationTray();
        var server = new ApiServer(engine, audit, tray, null, null, null, null, null, null, null, null, null, 0);
        server.Start();
        var shutdownSettled = HostRecordingShutdown.Stop(server, engine, history, "writer_timeout",
            TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(0), TimeSpan.FromMilliseconds(20),
            (name, _) => auditEvents.Add(name));
        Assert.False(shutdownSettled);
        Assert.Contains("service.recording_history_shutdown_incomplete", auditEvents);
        Assert.False(history.TryPublish(accepted with { Status = "recording" }));

        releaseWriter.Set();
        history.BeforePersistForTests = null;
        Assert.True(history.DrainAndDispose(TimeSpan.FromSeconds(5)));
        var reopened = new SqliteOrdinaryRecordingHistoryStore(database);
        reopened.RecoverIncomplete(DateTimeOffset.UtcNow.AddSeconds(1));
        Assert.Equal("interrupted_unknown", reopened.Read(accepted.RecordingId)!.Status);
        reopened.Dispose();
    }

    [Fact]
    public async Task ProductionHostShutdownSettlesFailedRegisterWithoutInventingHistory()
    {
        var database = new SqliteOperationalStore(Path.Combine(_root, "shutdown-register-failure", "agent-recorder.db"));
        database.Initialize();
        using var sqliteHistory = new SqliteOrdinaryRecordingHistoryStore(database);
        var gatedHistory = new RegisterGateHistory(sqliteHistory, failAfterRelease: true);
        var tray = new HoldingConfirmationTray();
        var audit = new AuditLogger();
        var engine = new RecordingEngine(audit);
        engine.SetTray(tray);
        engine.BackendFactory = _ => (new NeverStartedBackend(), "fake");
        engine.ConfigureOrdinaryRecordingHistory(gatedHistory);
        var server = new ApiServer(engine, audit, tray, null, null, null, null, null, null, null, null, null, 0);
        server.Start();
        var create = Task.Run(() => Record.Exception(() =>
            engine.CreateRecording(Request("failed-register.mp4"), "task309s-register-failure", tray)));
        Assert.True(gatedHistory.RegisterEntered.Wait(TimeSpan.FromSeconds(5)), "Register gate was not entered.");
        var recording = Assert.Single(engine._recs.Values);

        var shutdown = Task.Run(() => HostRecordingShutdown.Stop(server, engine, sqliteHistory,
            "register_failure_shutdown"));
        Assert.True(SpinWait.SpinUntil(() => recording.State == RecState.cancelled, TimeSpan.FromSeconds(5)));
        gatedHistory.ReleaseRegister.Set();
        Assert.True(await shutdown.WaitAsync(TimeSpan.FromSeconds(5)));
        var error = Assert.IsType<ApiException>(await create.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(503, error.Status);
        Assert.Equal(0, tray.ConfirmationRequests);
        Assert.Empty(engine._recs);
        Assert.Null(sqliteHistory.Read(recording.Id));
    }

    [Fact]
    public void EnginePublishesCompleteRejectedAndExpiredTerminalSnapshots()
    {
        var database = new SqliteOperationalStore(Path.Combine(_root, "terminal-engine", "agent-recorder.db"));
        database.Initialize();
        using var history = new SqliteOrdinaryRecordingHistoryStore(database);
        var tray = new HoldingConfirmationTray();
        var engine = new RecordingEngine(new AuditLogger());
        engine.SetTray(tray);
        engine.BackendFactory = _ => (new NeverStartedBackend(), "fake");
        engine.ConfigureOrdinaryRecordingHistory(history);

        engine.CreateRecording(Request("rejected.mp4"), "task309r-test", tray);
        var rejected = engine._recs.Values.Single();
        tray.DecideNext(ConfirmationDecision.Reject());
        Assert.Equal(RecState.rejected, rejected.State);
        var rejectedSnapshot = history.Read(rejected.Id)!;
        Assert.Equal("rejected", rejectedSnapshot.Status);
        Assert.Equal("user_rejected", rejectedSnapshot.ReasonCode);
        Assert.NotNull(rejectedSnapshot.CompletedAtUtc);

        engine.CreateRecording(Request("expired.mp4"), "task309r-test", tray);
        var expired = engine._recs.Values.Single(r => r.Id != rejected.Id);
        engine.TriggerConfirmationExpiryForTests(expired.ConfirmationId!);
        Assert.Equal(RecState.expired, expired.State);
        var expiredSnapshot = history.Read(expired.Id)!;
        Assert.Equal("expired", expiredSnapshot.Status);
        Assert.Equal("confirmation_expired", expiredSnapshot.ReasonCode);
        Assert.NotNull(expiredSnapshot.CompletedAtUtc);
        engine.Dispose();
    }

    public void Dispose()
    {
        DataDirResolver.ClearOverride();
        Environment.SetEnvironmentVariable("AGENT_RECORDER_TEST_MODE", _priorTestMode);
        SystemQuery.SetDisplayProvider(null);
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); } catch { }
    }

    private static string ReadStatus(object value) =>
        value.GetType().GetProperty("status")!.GetValue(value)!.ToString()!;

    private JsonObject Request(string filename = "pending.mp4") => new()
    {
        ["source"] = new JsonObject { ["type"] = "display", ["display_id"] = "display_1" },
        ["video"] = new JsonObject { ["fps"] = 15 },
        ["duration_seconds"] = 10,
        ["output"] = new JsonObject { ["filename"] = filename, ["directory"] = _root, ["conflict_policy"] = "rename" }
    };

    private sealed class RegisterGateHistory(IOrdinaryRecordingHistoryStore inner, bool failAfterRelease = false)
        : IOrdinaryRecordingHistoryStore
    {
        public ManualResetEventSlim RegisterEntered { get; } = new(false);
        public ManualResetEventSlim ReleaseRegister { get; } = new(false);

        public void Register(OrdinaryRecordingHistorySnapshot snapshot)
        {
            RegisterEntered.Set();
            if (!ReleaseRegister.Wait(TimeSpan.FromSeconds(8)))
                throw new TimeoutException("Test Register gate timed out.");
            if (failAfterRelease) throw new IOException("injected register failure");
            inner.Register(snapshot);
        }

        public bool TryPublish(OrdinaryRecordingHistorySnapshot snapshot) => inner.TryPublish(snapshot);
        public OrdinaryRecordingHistoryPage ReadPage(int limit, string? cursor) => inner.ReadPage(limit, cursor);
        public OrdinaryRecordingHistorySnapshot? Read(string recordingId) => inner.Read(recordingId);
        public void RecoverIncomplete(DateTimeOffset recoveredAtUtc) => inner.RecoverIncomplete(recoveredAtUtc);
    }

    private sealed class HoldingConfirmationTray : ITrayContext
    {
        private int _confirmationRequests;
        private readonly Queue<Action<ConfirmationDecision>> _callbacks = new();
        private readonly object _callbackLock = new();
        public int ConfirmationRequests => Volatile.Read(ref _confirmationRequests);
        public string HostMode => "tray";
        public bool SupportsRegionSelectionUi => false;
        public void RequestConfirmation(RecordingConfirmationPresentation presentation, Action<ConfirmationDecision> callback)
        {
            Interlocked.Increment(ref _confirmationRequests);
            lock (_callbackLock) _callbacks.Enqueue(callback);
        }
        public void DecideNext(ConfirmationDecision decision)
        {
            Action<ConfirmationDecision> callback;
            lock (_callbackLock) callback = _callbacks.Dequeue();
            callback(decision);
        }
        public void RequestRegionSelection(int timeoutSeconds, Action<string, int, int, int, int, string, string> callback) { }
        public void SetRecording(RecordingUiPresentation rec) { }
        public void SetIdle(RecordingUiPresentation rec) { }
        public void SetAllIdle() { }
        public void ShowError(string text) { }
    }

    private sealed class NeverStartedBackend : ICaptureBackend
    {
        public bool Started { get; private set; }
        public void Start(CaptureConfig cfg, CaptureAuthorizationProof authorizationProof) => Started = true;
        public OutputMeta Stop() => new();
        public void OnNaturalExit(Action<int, OutputMeta> callback) { }
        public int ExitCode => 0;
        public void Dispose() { }
    }
}
