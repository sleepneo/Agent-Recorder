using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using AgentRecorder.App;
using AgentRecorder.Capture;
using AgentRecorder.Core;
using AgentRecorder.Infrastructure;
using AgentRecorder.Logging;
using AgentRecorder.Persistence;
using AgentRecorder.Windows;
using Microsoft.Win32.SafeHandles;
using Xunit;

namespace AgentRecorder.Tests;

[Collection("NonParallel-SystemQueryProviders")]
public sealed class WindowStorageEngineTests : IDisposable
{
    private readonly TempDirectory _temp = new();
    private readonly IStorageCapacityProvider _original = RecordingPreflightChecker.StorageCapacityProvider;
    private readonly FakeStorageProvider _provider = new();

    public WindowStorageEngineTests()
    {
        DataDirResolver.SetOverride(_temp.Path);
        RecordingPreflightChecker.StorageCapacityProvider = _provider;
        SystemQuery.SetWindowProvider((_, _) => new()
        {
            new("window_42", "Window", "player.exe", 7, true, false, new SystemQuery.Bounds(0, 0, 320, 240))
        });
        SystemQuery.SetDisplayProvider(() => new()
        {
            new("display_1", "Display", true, new SystemQuery.Bounds(0, 0, 1920, 1080), 1)
        });
    }

    public void Dispose()
    {
        RecordingPreflightChecker.StorageCapacityProvider = _original;
        SystemQuery.SetWindowProvider(null);
        SystemQuery.SetDisplayProvider(null);
        DataDirResolver.ClearOverride();
        _temp.Dispose();
    }

    private CaptureConfig Config(bool audio) => new()
    {
        SourceKind = "window", RequireWindowSurface = true,
        WindowHandle = (nint)42, WindowProcessId = 7,
        Bounds = (0, 0, 320, 240), WindowSurfaceBounds = (0, 0, 320, 240),
        CountdownSeconds = 0, DurationSeconds = 60,
        AudioSourceKind = audio ? AudioCaptureSourceKind.SystemLoopback : AudioCaptureSourceKind.None,
        SystemLoopbackEndpoint = audio ? "endpoint-test" : null,
        SystemLoopbackEndpointName = audio ? "Render endpoint" : null,
        OutputPath = Path.Combine(_temp.Path, "output", Guid.NewGuid().ToString("N") + ".mp4")
    };

    private static CapturePlan Plan(CaptureConfig cfg) => new(
        "wgc-window", cfg.IsSystemLoopback ? "wgc-window-av-split" : "wgc-window",
        new CaptureBackendSelectionEvidence("wgc-window", cfg.IsSystemLoopback ? "wgc-window-av-split" : "wgc-window",
            "test_approved", "test", null, false), "window_surface", "window", "window_42", (nint)42,
        new CapturePlanBounds(0, 0, 320, 240), audioSourceKind: cfg.AudioSourceKind,
        audioEndpointId: cfg.SystemLoopbackEndpoint, audioEndpointName: cfg.SystemLoopbackEndpointName,
        targetWindowProcessId: 7, targetWindowSurfaceBounds: new CapturePlanBounds(0, 0, 320, 240));

    private static Recording Recording(CaptureConfig cfg, string? id = null) => new(id ?? "rec_" + Guid.NewGuid().ToString("N")[..12])
    {
        SourceType = "window", Config = cfg, OutputPath = cfg.OutputPath,
        DurationSeconds = cfg.DurationSeconds, CountdownSeconds = 0,
        ApprovedCapturePlan = Plan(cfg), AudioSourceKind = cfg.AudioSourceKind
    };

    private RecordingEngine Engine(ICaptureBackend backend, CaptureConfig cfg, Tray tray, Audit audit, StorageTestClock? clock = null)
    {
        var engine = new RecordingEngine(audit)
        {
            BackendFactory = _ => (backend, Plan(cfg).PlannedBackend),
            CountdownSteps = 0, CountdownInterval = TimeSpan.FromMilliseconds(1),
            DisableDeadlineWatchdogForTests = true
        };
        if (clock is not null) engine.StorageMonitorDelay = clock.Delay;
        engine.SetTray(tray);
        return engine;
    }

    [Fact]
    public void CodexConfirmedOutputDirectoryChangeMustPassStartPreflight()
    {
        var originalEncoder = RecordingPreflightChecker.EncoderProvider;
        RecordingPreflightChecker.EncoderProvider = (out string? ffmpeg, out string? ffprobe) =>
        {
            ffmpeg = ffprobe = typeof(RecordingEngine).Assembly.Location;
            return true;
        };
        try
        {
            var cfg = Config(false);
            var rec = Recording(cfg);
            Assert.True(RecordingPreflightChecker.CheckBeforeConfirmation(rec).Passed);
            var selectedDirectory = Path.Combine(_temp.Path, "user-selected-output");
            using var engine = new RecordingEngine(new Audit());
            var apply = typeof(RecordingEngine).GetMethod("ApplyConfirmationOutputDirectory",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
            Assert.True((bool)apply.Invoke(engine, new object[]
            {
                rec, ConfirmationDecision.Approve(selectedDirectory), "codex-confirmation"
            })!);
            Assert.Equal(selectedDirectory, Path.GetDirectoryName(cfg.OutputPath));
            var result = RecordingPreflightChecker.CheckBeforeStart(rec);
            Assert.True(result.Passed, $"Confirmed directory rejected: {result.ErrorCode}");
        }
        finally { RecordingPreflightChecker.EncoderProvider = originalEncoder; }
    }

    [Theory]
    [InlineData(false, "same")]
    [InlineData(false, "other")]
    [InlineData(false, "low")]
    [InlineData(false, "unavailable")]
    [InlineData(true, "same")]
    [InlineData(true, "other")]
    [InlineData(true, "low")]
    [InlineData(true, "unavailable")]
    public async Task ConfirmedDirectoryReplacesTentativePathsAndChecksOnlyApprovedOutput(bool audioEnabled, string outcome)
    {
        using var preflight = new StoragePreflightOverrides();
        var cfg = Config(audioEnabled); var rec = Recording(cfg);
        var oldDirectory = Path.GetDirectoryName(cfg.OutputPath)!;
        var selected = Path.Combine(_temp.Path, "locally-approved");
        _provider.Volume = p => p == selected && outcome != "same" ? "selected-volume" : "original-volume";
        var queries = new ConcurrentQueue<string[]>();
        _provider.Query = (paths, _) =>
        {
            queries.Enqueue(paths.ToArray());
            if (outcome == "unavailable" && paths.Contains(selected)) throw new IOException("selected volume unavailable");
            return Task.FromResult<IReadOnlyList<StorageVolumeSample>>(paths.Select(p => new StorageVolumeSample(p, _provider.Volume(p), _provider.Free(p))).ToArray());
        };
        Assert.True(RecordingPreflightChecker.CheckBeforeConfirmation(rec).Passed);
        var tentative = cfg.WritePaths!;
        var oldClock = new StorageTestClock();
        using var oldGuard = new WindowStorageSafety(tentative, 60, _provider) { Delay = oldClock.Delay };
        oldGuard.EnsureAdmission(); cfg.StorageSafety = oldGuard;
        int oldFailures = 0;
        oldGuard.Start(_ => Interlocked.Increment(ref oldFailures));
        await oldClock.WaitForDelay();
        var audio = new FakeAudioCaptureWorker(raiseAudioReadyOnStart: true);
        var video = new FakeVideoCaptureWorker();
        var factory = new FakeAvWorkerFactory { AudioWorker = audio, VideoWorker = video };
        int sessions = 0;
        using ICaptureBackend backend = audioEnabled
            ? new AvSplitCaptureBackend(factory, new FakeExternalProcessRunner(), new TempRetentionPolicy(_temp.Path))
            : new WgcContinuousCaptureBackend(_ => { sessions++; return new StorageWgcSession(); },
                StagingToFinalPublisher.Instance, _ => new OutputMeta(), () => "fake.exe", _temp.Path);
        var tray = new Tray(); using var engine = Engine(backend, cfg, tray, new Audit());
        Assert.True(ApplyLocalOutput(engine, rec, ConfirmationDecision.Approve(selected, rememberOutputDirectory: true)));
        Assert.Equal(selected, Path.GetDirectoryName(cfg.OutputPath));
        Assert.Equal(cfg.OutputPath, rec.OutputPath);
        Assert.NotSame(tentative, cfg.WritePaths); Assert.Null(cfg.StorageSafety);
        Assert.Equal(selected, cfg.WritePaths!.OutputDirectory);
        Assert.Equal(selected, OutputSettingsStore.GetEffectiveDefaultOutputDir());
        await oldGuard.MonitorCompletion.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(oldGuard.TryAbort(WindowStorageSafety.LowSpaceCode)); Assert.Equal(0, oldFailures);
        queries.Clear();
        _provider.Free = p => p == oldDirectory || p == selected && outcome == "low" ? 0 : 100L * 1024 * 1024 * 1024;
        var result = RecordingPreflightChecker.CheckBeforeStart(rec);
        Assert.DoesNotContain(queries, paths => paths.Contains(oldDirectory));
        Assert.Contains(queries, paths => paths.Contains(selected) && paths.Contains(cfg.WritePaths.WgcTempRoot) &&
            (!audioEnabled || paths.Contains(cfg.WritePaths.AvTempDirectory!)));
        var healthy = outcome is "same" or "other";
        Assert.Equal(healthy, result.Passed);
        engine.StartCaptureForTests(rec, tray);
        if (healthy)
        {
            await Until(() => rec.State == RecState.recording);
            Assert.Same(cfg.WritePaths, cfg.StorageSafety!.Paths);
            Assert.Equal(audioEnabled ? 1 : 0, factory.CreateAudioWorkerCount);
            Assert.Equal(audioEnabled ? 1 : 0, factory.CreateVideoWorkerCount);
            Assert.Equal(audioEnabled ? 0 : 1, sessions);
        }
        else
        {
            var expected = outcome == "low" ? WindowStorageSafety.LowSpaceCode : WindowStorageSafety.UnavailableCode;
            Assert.Equal(expected, result.ErrorCode); Assert.Equal(expected, rec.Error);
            Assert.Equal(0, factory.CreateAudioWorkerCount); Assert.Equal(0, factory.CreateVideoWorkerCount); Assert.Equal(0, sessions);
            Assert.False(rec.BackendStartAttempted);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void UnapprovedDirectoryMutationStillFailsClosed(bool audio)
    {
        using var preflight = new StoragePreflightOverrides();
        var cfg = Config(audio); var rec = Recording(cfg);
        Assert.True(RecordingPreflightChecker.CheckBeforeConfirmation(rec).Passed);
        var frozen = cfg.WritePaths;
        cfg.OutputPath = rec.OutputPath = Path.Combine(_temp.Path, "unapproved", "tampered.mp4");
        Assert.Equal(WindowStorageSafety.UnavailableCode, RecordingPreflightChecker.CheckBeforeStart(rec).ErrorCode);
        var backend = new ControlledBackend(); var tray = new Tray();
        using var engine = Engine(backend, cfg, tray, new Audit());
        engine.StartCaptureForTests(rec, tray);
        Assert.Equal(0, backend.Starts); Assert.Same(frozen, cfg.WritePaths);
    }

    [Theory]
    [InlineData("future")]
    [InlineData("standing")]
    [InlineData("recurring")]
    [InlineData("required")]
    [InlineData("proof")]
    [InlineData("started")]
    [InlineData("recording")]
    [InlineData("terminal")]
    [InlineData("rejected")]
    public void LocalOutputEntryCannotRewriteExecutionScopesOrUnapprovedRuns(string boundary)
    {
        var cfg = Config(false); var rec = Recording(cfg);
        cfg.WritePaths = CaptureWritePaths.Freeze(cfg); var original = cfg.OutputPath; var paths = cfg.WritePaths;
        if (boundary == "future") rec.IsFutureWindowOneShotExecution = true;
        if (boundary == "standing") rec.IsStandingLeaseExecution = true;
        if (boundary == "recurring") rec.IsRecurringLeaseExecution = true;
        if (boundary == "required") rec.IsRequiredOnceExecution = true;
        if (boundary == "proof") rec.AuthorizationProof = CaptureAuthorizationProofIssuer.IssueForTests(rec, Plan(cfg));
        if (boundary == "started") rec.BackendStartAttempted = true;
        if (boundary == "recording") rec.State = RecState.recording;
        if (boundary == "terminal")
            lock (rec) { rec.State = RecState.completed; Assert.True(rec.PublishFinalized()); }
        var selected = Path.Combine(_temp.Path, "must-not-create");
        using var engine = new RecordingEngine(new Audit());
        Assert.False(ApplyLocalOutput(engine, rec, new ConfirmationDecision(boundary != "rejected", selected, true)));
        Assert.Equal(original, rec.OutputPath); Assert.Equal(original, cfg.OutputPath); Assert.Same(paths, cfg.WritePaths);
        Assert.False(Directory.Exists(selected));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void FutureWindowConsumedReceiptStillRejectsOutputDirectoryOrFilenameTampering(bool audio, bool changeFilename)
    {
        var cfg = Config(audio); cfg.OutputConflictPolicy = "fail_if_exists";
        var originalPath = cfg.OutputPath;
        Directory.CreateDirectory(Path.GetDirectoryName(originalPath)!);
        cfg.WritePaths = CaptureWritePaths.Freeze(cfg);
        var frozen = cfg.WritePaths;
        var store = new SqliteOperationalStore(Path.Combine(_temp.Path, "future-scope-test.db"));
        store.Initialize();
        var service = new StandingLeaseSafetyControlService(store, utcNowForTest: null, auditForTest: (_, _) => { });
        Assert.True(service.EnableUnattended("test-enable").DurableOperationCommitted);
        var repository = new SqliteFutureWindowAuthorizationRepository(store);
        var now = DateTimeOffset.UtcNow;
        const string sid = "S-1-5-21-100-200-300-1001";
        const string session = "storage-test-session";
        var executable = new FutureWindowExecutableIdentity(1, @"C:\Player\player.exe", "1A2B3C4D:0000000000000010", new string('a', 64), null, null);
        var scope = new FutureWindowAuthorizationScope("fwa_" + Guid.NewGuid().ToString("N"), "key-test", new string('b', 64),
            sid, session, executable, cfg.SystemLoopbackEndpoint, cfg.SystemLoopbackEndpointName, 60, 300,
            Path.GetDirectoryName(originalPath)!, Path.GetFileName(originalPath), "pending", now.AddSeconds(-3), null, null, null, null, 0);
        repository.CreateOrGet(scope);
        repository.Approve(scope.AuthorizationId, sid, session, "approval-test", now.AddSeconds(-2));
        var snapshot = new FutureWindowProcessSnapshot("window_42", (nint)42, 7, now.AddSeconds(-1).UtcDateTime.ToFileTimeUtc(),
            sid, 1, executable.CanonicalPath, executable);
        var receipt = repository.TryCommitStart(scope.AuthorizationId, sid, session, snapshot, now, out var reason);
        Assert.NotNull(receipt); Assert.Equal("", reason);
        cfg.OutputPath = changeFilename ? Path.Combine(scope.OutputDirectory, "unapproved.mp4")
            : Path.Combine(_temp.Path, "unapproved", scope.OutputFileName);
        var backend = new ControlledBackend(); var tray = new Tray();
        using var engine = Engine(backend, cfg, tray, new Audit());
        using var hold = new FutureWindowIdentityHold(snapshot, new SafeProcessHandle(IntPtr.Zero, false), new SafeFileHandle(IntPtr.Zero, false));
        var result = engine.StartFutureWindowCapture(receipt!, cfg, Plan(cfg), hold, tray);
        Assert.False(result.Accepted); Assert.Equal("future_window_proof_issue_failed", result.ReasonCode);
        Assert.Equal(0, backend.Starts); Assert.Equal(0, _provider.Calls); Assert.Same(frozen, cfg.WritePaths);
        Assert.Equal(originalPath, receipt!.Authorization.OutputFilePath);
        Assert.Equal(originalPath, repository.Get(scope.AuthorizationId, sid, session)!.ToScope().OutputFilePath);
        Assert.Null(repository.TryCommitStart(scope.AuthorizationId, sid, session, snapshot, now.AddSeconds(1), out _));
    }

    private static bool ApplyLocalOutput(RecordingEngine engine, Recording rec, ConfirmationDecision decision)
        => (bool)typeof(RecordingEngine).GetMethod("ApplyConfirmationOutputDirectory",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .Invoke(engine, new object[] { rec, decision, "local-confirmation-test" })!;

    private sealed class StoragePreflightOverrides : IDisposable
    {
        private readonly RecordingPreflightChecker.TryGetEncoderPaths _encoder = RecordingPreflightChecker.EncoderProvider;
        private readonly RecordingPreflightChecker.TryResolveAudioHelper _helper = RecordingPreflightChecker.AudioHelperPathResolver;
        private readonly RecordingPreflightChecker.RunAudioHelperProbe _probe = RecordingPreflightChecker.AudioHelperProbeRunner;
        internal StoragePreflightOverrides()
        {
            RecordingPreflightChecker.EncoderProvider = (out string? ffmpeg, out string? ffprobe) =>
            { ffmpeg = ffprobe = typeof(RecordingEngine).Assembly.Location; return true; };
            RecordingPreflightChecker.AudioHelperPathResolver = () => "fake-version-only.exe";
            RecordingPreflightChecker.AudioHelperProbeRunner = (_, _) => new AudioHelperProbeResult
                { Success = true, ProtocolVersion = "audio-helper-v1", TimestampFrequency = Stopwatch.Frequency };
        }
        public void Dispose()
        { RecordingPreflightChecker.EncoderProvider = _encoder; RecordingPreflightChecker.AudioHelperPathResolver = _helper; RecordingPreflightChecker.AudioHelperProbeRunner = _probe; }
    }

    [Theory]
    [InlineData(false, "output")]
    [InlineData(false, "wgc")]
    [InlineData(true, "output")]
    [InlineData(true, "av")]
    [InlineData(true, "wgc")]
    [InlineData(true, "exception")]
    public void OrdinaryFinalStartBoundaryRejectsBeforeAnyCaptureWorkerStarts(bool audio, string low)
    {
        var cfg = Config(audio);
        cfg.WritePaths = CaptureWritePaths.Freeze(cfg);
        _provider.Free = p => low == "output" && p == cfg.WritePaths.OutputDirectory ||
            low == "av" && p == cfg.WritePaths.AvTempDirectory || low == "wgc" && p == cfg.WritePaths.WgcTempRoot
            ? 1 : 100L * 1024 * 1024 * 1024;
        if (low == "exception") _provider.Query = (_, _) => throw new IOException();
        var factory = new FakeAvWorkerFactory();
        using var backend = new AvSplitCaptureBackend(factory, new FakeExternalProcessRunner(), new TempRetentionPolicy(_temp.Path));
        var tray = new Tray(); var audit = new Audit();
        using var engine = Engine(backend, cfg, tray, audit);
        var rec = Recording(cfg);
        engine.StartCaptureForTests(rec, tray);
        Assert.Equal(RecState.failed, rec.State);
        Assert.Equal(low == "exception" ? WindowStorageSafety.UnavailableCode : WindowStorageSafety.LowSpaceCode, rec.Error);
        Assert.Equal(rec.Error, rec.StopReason);
        Assert.Equal(0, factory.CreateAudioWorkerCount);
        Assert.Equal(0, factory.CreateVideoWorkerCount);
        Assert.False(rec.BackendStartAttempted);
        Assert.Equal(new[] { rec.Error }, tray.FailureReasons.ToArray());
    }

    [Theory]
    [InlineData(false, "output")]
    [InlineData(false, "wgc")]
    [InlineData(false, "unavailable")]
    [InlineData(true, "output")]
    [InlineData(true, "av")]
    [InlineData(true, "wgc")]
    [InlineData(true, "unavailable")]
    public void FutureWindowConsumedReceiptRechecksStorageWithoutTouchingRealAuthorizationDatabase(bool audio, string low)
    {
        var cfg = Config(audio);
        Directory.CreateDirectory(Path.GetDirectoryName(cfg.OutputPath)!);
        cfg.OutputConflictPolicy = "fail_if_exists";
        var store = new SqliteOperationalStore(Path.Combine(_temp.Path, "future-test.db"));
        store.Initialize();
        var service = new StandingLeaseSafetyControlService(store, utcNowForTest: null, auditForTest: (_, _) => { });
        Assert.True(service.EnableUnattended("test-enable").DurableOperationCommitted);
        var repository = new SqliteFutureWindowAuthorizationRepository(store);
        var now = DateTimeOffset.UtcNow;
        const string sid = "S-1-5-21-100-200-300-1001";
        const string session = "storage-test-session";
        var executable = new FutureWindowExecutableIdentity(1, @"C:\Player\player.exe", "1A2B3C4D:0000000000000010", new string('a', 64), null, null);
        var scope = new FutureWindowAuthorizationScope("fwa_" + Guid.NewGuid().ToString("N"), "key-test", new string('b', 64),
            sid, session, executable, cfg.SystemLoopbackEndpoint, cfg.SystemLoopbackEndpointName, 60, 300,
            Path.GetDirectoryName(cfg.OutputPath)!, Path.GetFileName(cfg.OutputPath), "pending", now.AddSeconds(-3), null, null, null, null, 0);
        repository.CreateOrGet(scope);
        repository.Approve(scope.AuthorizationId, sid, session, "approval-test", now.AddSeconds(-2));
        var snapshot = new FutureWindowProcessSnapshot("window_42", (nint)42, 7, now.AddSeconds(-1).UtcDateTime.ToFileTimeUtc(),
            sid, 1, executable.CanonicalPath, executable);
        var receipt = repository.TryCommitStart(scope.AuthorizationId, sid, session, snapshot, now, out var failure);
        Assert.NotNull(receipt); Assert.Equal("", failure);
        var paths = CaptureWritePaths.Freeze(cfg);
        _provider.Free = p => low == "output" && p == paths.OutputDirectory ||
            low == "av" && p == paths.AvTempDirectory || low == "wgc" && p == paths.WgcTempRoot
            ? 1 : 100L * 1024 * 1024 * 1024;
        if (low == "unavailable") _provider.Query = (_, _) => throw new IOException();
        var expected = low == "unavailable" ? WindowStorageSafety.UnavailableCode : WindowStorageSafety.LowSpaceCode;
        var backend = new ControlledBackend();
        var tray = new Tray(); var audit = new Audit();
        using var engine = new RecordingEngine(audit, null, null, null, null, null, null,
            futureWindowStartSafetyInterlock: new StandingLeaseStartSafetyInterlock(),
            futureWindowStartSafetyValidator: (ticket, utc) => repository.ValidateAtBackendStart(ticket, utc))
        {
            BackendFactory = _ => (backend, Plan(cfg).PlannedBackend),
            FutureWindowIdentityForTests = () => (sid, session, 1)
        };
        engine.SetTray(tray);
        using var hold = new FutureWindowIdentityHold(snapshot, new SafeProcessHandle(IntPtr.Zero, false), new SafeFileHandle(IntPtr.Zero, false));
        var result = engine.StartFutureWindowCapture(receipt!, cfg, Plan(cfg), hold, tray);
        Assert.False(result.Accepted);
        Assert.Equal(expected, result.ReasonCode);
        Assert.Equal(0, backend.Starts);
        repository.CompleteRun(scope.AuthorizationId, receipt!.RunId, false, result.ReasonCode!, null, null, null, now.AddSeconds(1));
        var terminal = repository.Get(scope.AuthorizationId, sid, session)!;
        repository.CompleteRun(scope.AuthorizationId, receipt.RunId, false, result.ReasonCode!, null, null, null, now.AddSeconds(2));
        Assert.Equal(terminal.Version, repository.Get(scope.AuthorizationId, sid, session)!.Version);
        Assert.Equal("failed", terminal.StatusCode);
        Assert.Equal(expected, terminal.ReasonCode);
        Assert.Null(repository.TryCommitStart(scope.AuthorizationId, sid, session, snapshot, now.AddSeconds(3), out _));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task FutureWindowRuntimeTrackerSettlesTrustedStorageFailureOnce(bool audioEnabled, bool unavailable)
    {
        var cfg = Config(audioEnabled);
        Directory.CreateDirectory(Path.GetDirectoryName(cfg.OutputPath)!);
        var store = new SqliteOperationalStore(Path.Combine(_temp.Path, "future-runtime-test.db"));
        store.Initialize();
        var service = new StandingLeaseSafetyControlService(store, utcNowForTest: null, auditForTest: (_, _) => { });
        Assert.True(service.EnableUnattended("test-enable").DurableOperationCommitted);
        var repository = new SqliteFutureWindowAuthorizationRepository(store);
        var now = DateTimeOffset.UtcNow;
        const string sid = "S-1-5-21-100-200-300-1001";
        const string session = "storage-test-session";
        var executable = new FutureWindowExecutableIdentity(1, @"C:\Player\player.exe", "1A2B3C4D:0000000000000010", new string('a', 64), null, null);
        var scope = new FutureWindowAuthorizationScope("fwa_" + Guid.NewGuid().ToString("N"), "key-test", new string('b', 64),
            sid, session, executable, cfg.SystemLoopbackEndpoint, cfg.SystemLoopbackEndpointName, 60, 300,
            Path.GetDirectoryName(cfg.OutputPath)!, Path.GetFileName(cfg.OutputPath), "pending", now.AddSeconds(-3), null, null, null, null, 0);
        repository.CreateOrGet(scope);
        repository.Approve(scope.AuthorizationId, sid, session, "approval-test", now.AddSeconds(-2));
        var snapshot = new FutureWindowProcessSnapshot("window_42", (nint)42, 7, now.AddSeconds(-1).UtcDateTime.ToFileTimeUtc(),
            sid, 1, executable.CanonicalPath, executable);
        var receipt = repository.TryCommitStart(scope.AuthorizationId, sid, session, snapshot, now, out var failure);
        Assert.NotNull(receipt); Assert.Equal("", failure);
        var audio = new FakeAudioCaptureWorker(raiseAudioReadyOnStart: true);
        var video = new FakeVideoCaptureWorker();
        var runner = new FakeExternalProcessRunner();
        var sessionWithoutAudio = new StorageWgcSession();
        using ICaptureBackend backend = audioEnabled
            ? new AvSplitCaptureBackend(new FakeAvWorkerFactory { AudioWorker = audio, VideoWorker = video },
                runner, new TempRetentionPolicy(_temp.Path)) { ApplyContinuityCheck = false }
            : new WgcContinuousCaptureBackend(_ => sessionWithoutAudio,
                StagingToFinalPublisher.Instance, _ => new OutputMeta(), () => "fake.exe", _temp.Path);
        var tray = new Tray(); var audit = new Audit(); var clock = new StorageTestClock();
        using var engine = Engine(backend, cfg, tray, audit, clock);
        using var coordinator = new FutureWindowOneShotAuthorizationCoordinator(store, engine, tray, audit,
            new StandingLeaseStartSafetyInterlock(), recoverPending: false);
        // Only physical capture/identity acquisition is faked here. The durable
        // receipt, run status, production tracker and SQLite settlement are real.
        // The separate consumed-receipt matrix covers the genuine start gate.
        var rec = Recording(cfg, receipt!.RunId);
        engine.StartCaptureForTests(rec, tray);
        await Until(() => rec.State == RecState.recording);
        await clock.WaitForDelay();
        var tracking = coordinator.TrackRunForTests(receipt);
        _provider.Free = _ => WindowStorageSafety.LowWaterBytes - 1;
        if (unavailable) _provider.Query = (_, _) => throw new IOException("test removed volume");
        clock.Tick();
        await tracking.WaitAsync(TimeSpan.FromSeconds(10));
        var terminal = repository.Get(scope.AuthorizationId, sid, session)!;
        var expected = unavailable ? WindowStorageSafety.UnavailableCode : WindowStorageSafety.LowSpaceCode;
        Assert.Equal("failed", terminal.StatusCode);
        Assert.Equal("failed", terminal.RunStatus);
        Assert.Equal(expected, terminal.ReasonCode);
        Assert.Null(terminal.OutputPath); Assert.Null(terminal.OutputSizeBytes);
        await coordinator.TrackRunForTests(receipt).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(terminal.Version, repository.Get(scope.AuthorizationId, sid, session)!.Version);
        Assert.Null(repository.TryCommitStart(scope.AuthorizationId, sid, session, snapshot, now.AddSeconds(3), out _));
        if (audioEnabled) { Assert.True(video.HasExited); Assert.True(audio.HasExited); }
        else Assert.Equal(1, sessionWithoutAudio.Stops);
        Assert.Equal(0, runner.RunCallCount);
        Assert.False(File.Exists(cfg.OutputPath));
        await Until(() => tray.FailureReasons.Count == 1);
        Assert.True(Assert.Single(tray.IdleBeforeFailure));
        Assert.Single(audit.Events, e => e == "recording.failed");
        await cfg.StorageSafety!.MonitorCompletion.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Theory]
    [InlineData("output", false)]
    [InlineData("av", false)]
    [InlineData("wgc", false)]
    [InlineData("output", true)]
    [InlineData("av", true)]
    [InlineData("wgc", true)]
    public async Task RuntimeFailureStopsBothSplitWorkersSkipsMuxAndExposesOneTrustedTerminal(string pathKind, bool unavailable)
    {
        var cfg = Config(true);
        var audio = new FakeAudioCaptureWorker(raiseAudioReadyOnStart: true);
        var video = new FakeVideoCaptureWorker();
        var runner = new FakeExternalProcessRunner();
        var factory = new FakeAvWorkerFactory { AudioWorker = audio, VideoWorker = video };
        using var backend = new AvSplitCaptureBackend(factory, runner, new TempRetentionPolicy(_temp.Path)) { ApplyContinuityCheck = false };
        var tray = new Tray(); var audit = new Audit(); var clock = new StorageTestClock();
        using var engine = Engine(backend, cfg, tray, audit, clock);
        var rec = Recording(cfg);
        engine.StartCaptureForTests(rec, tray);
        await Until(() => rec.State == RecState.recording);
        await clock.WaitForDelay();
        var paths = cfg.WritePaths!;
        string selected = pathKind == "output" ? paths.OutputDirectory : pathKind == "av" ? paths.AvTempDirectory! : paths.WgcTempRoot;
        _provider.Free = p => p == selected ? WindowStorageSafety.LowWaterBytes - 1 : 100L * 1024 * 1024 * 1024;
        if (unavailable) _provider.Query = (_, _) => throw new IOException("unavailable " + selected);
        clock.Tick();
        await Until(() => rec.IsFinalized && tray.FailureReasons.Count == 1);
        var expected = unavailable ? WindowStorageSafety.UnavailableCode : WindowStorageSafety.LowSpaceCode;
        Assert.Equal(RecState.failed, rec.State);
        Assert.Equal(expected, rec.StopReason); Assert.Equal(expected, rec.Error);
        Assert.True(audio.HasExited); Assert.True(video.HasExited);
        Assert.True(audio.StopCalled); Assert.True(video.StopCalled);
        Assert.Equal(0, runner.RunCallCount);
        Assert.False(File.Exists(cfg.OutputPath)); Assert.False(rec.LastMeta!.OutputFileExists);
        Assert.Equal(0, rec.LastMeta.SizeBytes);
        var status = JsonSerializer.SerializeToElement(engine.GetStatus(rec.Id));
        Assert.Equal("failed", status.GetProperty("status").GetString());
        Assert.Equal(expected, status.GetProperty("stop_reason").GetString());
        Assert.Single(audit.Events, e => e == "recording.failed");
        Assert.Single(tray.FailureReasons);
        Assert.True(Assert.Single(tray.IdleBeforeFailure));
        await cfg.StorageSafety!.MonitorCompletion.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LateStorageAbortCannotOverrideSuccessfulNaturalOrUserStop(bool manualStop)
    {
        var cfg = Config(false);
        var backend = new ControlledBackend(); var tray = new Tray(); var audit = new Audit();
        var clock = new StorageTestClock();
        using var engine = Engine(backend, cfg, tray, audit, clock);
        var rec = Recording(cfg);
        engine.StartCaptureForTests(rec, tray);
        await Until(() => rec.State == RecState.recording);
        if (manualStop) engine.Stop(rec.Id, "user_requested"); else backend.Complete();
        await Until(() => rec.IsFinalized);
        Assert.Equal(RecState.completed, rec.State);
        Assert.Equal(manualStop ? "user_requested" : "duration_reached", rec.StopReason);
        Assert.False(cfg.StorageSafety!.TryAbort(WindowStorageSafety.LowSpaceCode));
        Assert.Empty(tray.FailureReasons);
        await cfg.StorageSafety.MonitorCompletion.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Theory]
    [InlineData("display")]
    [InlineData("region")]
    public void NonWindowRunsDoNotQueryOrCreateStorageGuard(string kind)
    {
        var cfg = Config(false); cfg.SourceKind = kind; cfg.RequireWindowSurface = false;
        var backend = new ControlledBackend(); var tray = new Tray(); var audit = new Audit();
        using var engine = Engine(backend, cfg, tray, audit);
        var rec = new Recording { SourceType = kind, Config = cfg, OutputPath = cfg.OutputPath, DurationSeconds = 60 };
        engine.StartCaptureForTests(rec, tray);
        Assert.Null(cfg.StorageSafety);
        Assert.Equal(0, _provider.Calls);
    }

    [Theory]
    [InlineData("mux")]
    [InlineData("publish-preparation")]
    [InlineData("publish-copy")]
    [InlineData("user-stop")]
    [InlineData("dispose")]
    [InlineData("authorization_revoked")]
    public async Task StorageProtectionContinuesThroughNaturalEndMuxAndPublication(string phase)
    {
        var cfg = Config(true); cfg.DurationSeconds = 2;
        Directory.CreateDirectory(Path.GetDirectoryName(cfg.OutputPath)!);
        var (videoPath, audioPath, muxPath) = CreateMediaFixtures();
        var audio = new FakeAudioCaptureWorker(raiseAudioReadyOnStart: true, holdFileOpen: true, holdFileOpenCopyFrom: audioPath);
        var video = new FakeVideoCaptureWorker();
        var runner = new GatedMuxRunner(muxPath, phase is "mux" or "user-stop" or "dispose" or "authorization_revoked");
        var copy = new PausedCopy();
        using var backend = new AvSplitCaptureBackend(new FakeAvWorkerFactory { AudioWorker = audio, VideoWorker = video },
            runner, new TempRetentionPolicy(_temp.Path)) { ApplyContinuityCheck = false };
        if (phase == "publish-copy") backend.FinalOutputPublisher = new StagingToFinalPublisher(copy);
        var tray = new Tray(); var audit = new Audit(); var clock = new StorageTestClock();
        using var engine = Engine(backend, cfg, tray, audit, clock);
        var rec = Recording(cfg);
        engine.StartCaptureForTests(rec, tray);
        await Until(() => rec.State == RecState.recording);
        File.Copy(videoPath, video.OutputPath!, true);
        await clock.WaitForDelay();
        if (phase == "publish-preparation") runner.AfterMux = () => _provider.Free = _ => 0;
        var ending = Task.Run(() => video.EmitNaturalExit(0, ""));
        await runner.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        if (phase == "publish-copy") await copy.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        if (phase != "publish-preparation")
        {
            _provider.Free = _ => 0;
            clock.Tick();
            await Until(() => cfg.StorageSafety!.FailureCode == WindowStorageSafety.LowSpaceCode);
        }
        Task? competing = phase == "user-stop" ? Task.Run(() => engine.Stop(rec.Id, "user_requested"))
            : phase == "authorization_revoked" ? Task.Run(() => engine.Stop(rec.Id, "authorization_revoked"))
            : phase == "dispose" ? Task.Run(engine.Dispose) : null;
        await ending.WaitAsync(TimeSpan.FromSeconds(10));
        if (competing is not null) await competing.WaitAsync(TimeSpan.FromSeconds(10));
        await Until(() => rec.IsFinalized);
        Assert.Equal(RecState.failed, rec.State);
        Assert.Equal(WindowStorageSafety.LowSpaceCode, rec.StopReason);
        Assert.Equal(WindowStorageSafety.LowSpaceCode, rec.Error);
        Assert.False(rec.LastMeta!.OutputFileExists);
        Assert.False(File.Exists(cfg.OutputPath));
        Assert.False(File.Exists(cfg.OutputPath + ".muxing.partial.mp4"));
        Assert.True(Directory.Exists(backend.FailedArtifactsDirectory));
        Assert.True(File.Exists(Path.Combine(backend.FailedArtifactsDirectory!, "video.mp4")));
        Assert.True(File.Exists(Path.Combine(backend.FailedArtifactsDirectory!, "audio.wav")));
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(cfg.OutputPath)!, "*.publish-tmp-*.mp4"));
        Assert.True(audio.HasExited); Assert.True(video.HasExited);
        Assert.Single(audit.Events, e => e == "recording.failed");
        await cfg.StorageSafety!.MonitorCompletion.WaitAsync(TimeSpan.FromSeconds(5));
        await Until(() => tray.FailureReasons.Count == 1);
        Assert.True(Assert.Single(tray.IdleBeforeFailure));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task HealthySplitStillPublishesValidAudioVideoAndKeepsStopSemantics(bool manual)
    {
        var cfg = Config(true); cfg.DurationSeconds = 2;
        Directory.CreateDirectory(Path.GetDirectoryName(cfg.OutputPath)!);
        var (videoPath, audioPath, muxPath) = CreateMediaFixtures();
        var audio = new FakeAudioCaptureWorker(raiseAudioReadyOnStart: true, holdFileOpen: true, holdFileOpenCopyFrom: audioPath);
        var video = new FakeVideoCaptureWorker();
        using var backend = new AvSplitCaptureBackend(new FakeAvWorkerFactory { AudioWorker = audio, VideoWorker = video },
            new FakeExternalProcessRunner(outputFileToCopy: muxPath), new TempRetentionPolicy(_temp.Path)) { ApplyContinuityCheck = false };
        var tray = new Tray(); var audit = new Audit(); var clock = new StorageTestClock();
        using var engine = Engine(backend, cfg, tray, audit, clock);
        var rec = Recording(cfg);
        engine.StartCaptureForTests(rec, tray);
        await Until(() => rec.State == RecState.recording);
        File.Copy(videoPath, video.OutputPath!, true);
        if (manual) engine.Stop(rec.Id, "user_requested"); else video.EmitNaturalExit(0, "");
        await Until(() => rec.IsFinalized);
        Assert.Equal(RecState.completed, rec.State);
        Assert.Equal(manual ? "user_requested" : "duration_reached", rec.StopReason);
        Assert.True(File.Exists(cfg.OutputPath));
        var media = FfmpegCaptureBackend.Probe(cfg.OutputPath);
        Assert.Equal("h264", media.Codec); Assert.Equal("aac", media.AudioCodec);
        Assert.True(cfg.StorageSafety!.Committed);
        Assert.False(cfg.StorageSafety.TryAbort(WindowStorageSafety.LowSpaceCode));
        Assert.Empty(tray.FailureReasons);
        await cfg.StorageSafety.MonitorCompletion.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task ApiStopTextCannotManufactureTrustedStorageFailure()
    {
        var cfg = Config(false); var backend = new ControlledBackend(); var tray = new Tray(); var audit = new Audit();
        using var engine = Engine(backend, cfg, tray, audit);
        var rec = Recording(cfg);
        engine.StartCaptureForTests(rec, tray);
        await Until(() => rec.State == RecState.recording);
        engine.Stop(rec.Id, WindowStorageSafety.LowSpaceCode);
        Assert.Equal(RecState.completed, rec.State);
        Assert.Null(rec.TrustedLifecycleAbortReason);
        Assert.Null(engine.GetTrustedStorageFailureReason(rec.Id));
        Assert.Empty(tray.FailureReasons);
    }

    [Theory]
    [InlineData("user_requested")]
    [InlineData("authorization_revoked")]
    public async Task AlreadyClaimedStorageAbortWinsManualTerminalOwner(string stopReason)
    {
        var cfg = Config(false); var backend = new ControlledBackend(); var tray = new Tray(); var audit = new Audit();
        using var engine = Engine(backend, cfg, tray, audit);
        var rec = Recording(cfg);
        engine.StartCaptureForTests(rec, tray);
        await Until(() => rec.State == RecState.recording);
        lock (rec)
        {
            Assert.True(cfg.StorageSafety!.TryAbort(WindowStorageSafety.LowSpaceCode));
            engine.Stop(rec.Id, stopReason);
        }
        await Until(() => rec.IsFinalized && tray.FailureReasons.Count == 1);
        Assert.Equal(RecState.failed, rec.State);
        Assert.Equal(WindowStorageSafety.LowSpaceCode, rec.Error);
        Assert.Equal(WindowStorageSafety.LowSpaceCode, rec.StopReason);
        Assert.Single(audit.Events, e => e == "recording.failed");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void StrictPreflightFailsClosedAtBothStagesBeforeAudioProbe(bool unavailable)
    {
        var cfg = Config(true); var rec = Recording(cfg);
        _provider.Free = _ => 0;
        if (unavailable) _provider.Query = (_, _) => throw new IOException();
        var original = RecordingPreflightChecker.AudioHelperProbeRunner;
        int calls = 0;
        RecordingPreflightChecker.AudioHelperProbeRunner = (_, _) => { calls++; throw new Exception(); };
        try
        {
            var beforeConfirmation = RecordingPreflightChecker.CheckBeforeConfirmation(rec);
            var beforeStart = RecordingPreflightChecker.CheckBeforeStart(rec);
            Assert.False(beforeConfirmation.Passed); Assert.False(beforeStart.Passed);
            Assert.Equal(unavailable ? WindowStorageSafety.UnavailableCode : WindowStorageSafety.LowSpaceCode, beforeStart.ErrorCode);
            Assert.Equal(beforeStart.ErrorCode, beforeConfirmation.ErrorCode);
            Assert.Equal(0, calls);
            Assert.DoesNotContain("continuing", beforeStart.Message!);
        }
        finally { RecordingPreflightChecker.AudioHelperProbeRunner = original; }
    }

    [Fact]
    public void NoAudioWgcStorageRejectionPrecedesSessionAndCaptureEntry()
    {
        var cfg = Config(false); cfg.WritePaths = CaptureWritePaths.Freeze(cfg);
        cfg.StorageSafety = new WindowStorageSafety(cfg.WritePaths, 60, _provider);
        _provider.Free = _ => 0;
        int sessions = 0;
        using var backend = new WgcContinuousCaptureBackend(_ => { sessions++; throw new Exception(); },
            StagingToFinalPublisher.Instance, _ => new OutputMeta(), () => "fake.exe", _temp.Path);
        Assert.Throws<StorageSafetyException>(() => CaptureAuthorizationTestHelper.StartWithSyntheticConsumedProof(backend, cfg));
        Assert.Equal(0, sessions);
        cfg.StorageSafety.Dispose();
    }

    [Fact]
    public async Task NoAudioWgcMonitorUsesFrozenEffectiveStagingRootAndStopsSessionWithoutPublishing()
    {
        var cfg = Config(false);
        cfg.WritePaths = new CaptureWritePaths(Path.GetDirectoryName(cfg.OutputPath)!, null, Path.Combine(_temp.Path, "frozen-wgc"));
        var session = new StorageWgcSession();
        string? actualStaging = null;
        using var backend = new WgcContinuousCaptureBackend(options => { actualStaging = options.OutputPath; return session; },
            StagingToFinalPublisher.Instance, _ => new OutputMeta(), () => "fake.exe", Path.Combine(_temp.Path, "unused-root"));
        var tray = new Tray(); var audit = new Audit(); var clock = new StorageTestClock();
        using var engine = Engine(backend, cfg, tray, audit, clock);
        var rec = Recording(cfg);
        engine.StartCaptureForTests(rec, tray);
        await Until(() => rec.State == RecState.recording);
        Assert.StartsWith(cfg.WritePaths.WgcTempRoot, actualStaging!);
        Assert.False(Directory.Exists(Path.Combine(_temp.Path, "unused-root")));
        await clock.WaitForDelay();
        _provider.Free = p => p == cfg.WritePaths.WgcTempRoot ? 0 : 100L * 1024 * 1024 * 1024;
        clock.Tick();
        await Until(() => rec.IsFinalized && tray.FailureReasons.Count == 1);
        Assert.Equal(1, session.Stops);
        Assert.Equal(RecState.failed, rec.State);
        Assert.Equal(WindowStorageSafety.LowSpaceCode, rec.StopReason);
        Assert.False(File.Exists(cfg.OutputPath));
        await cfg.StorageSafety!.MonitorCompletion.WaitAsync(TimeSpan.FromSeconds(5));
    }

    private sealed class StorageWgcSession : IWgcContinuousBackendSession
    {
        private readonly TaskCompletionSource<WgcContinuousSessionResult> _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal int Stops;
        internal int Authorizations;
        public Task<WgcContinuousSessionResult> CompletionTask => _completion.Task;
        public event Action<FirstFrameObservation>? FirstFrameObserved;
        public Task StartAsync(CancellationToken token = default) => Task.CompletedTask;
        public Task<bool> AuthorizeCapture(CancellationToken token = default)
        {
            Interlocked.Increment(ref Authorizations);
            FirstFrameObserved?.Invoke(new FirstFrameObservation { FrameNumber = 1, TotalSizeBytes = 2048 });
            return Task.FromResult(true);
        }
        public Task<bool> RequestStop(CancellationToken token = default)
        {
            Interlocked.Increment(ref Stops);
            _completion.TrySetResult(new WgcContinuousSessionResult
            {
                State = WgcContinuousManagedSessionState.Failed, ExitCode = -1,
                StopRequestedByCaller = true, FailureCategory = "stopped"
            });
            return Task.FromResult(true);
        }
        public void Dispose() => _completion.TrySetResult(new WgcContinuousSessionResult
            { State = WgcContinuousManagedSessionState.Failed, ExitCode = -1 });
    }

    [Fact]
    public async Task ProductionProbeTimeoutKillsOnlyItsHungTestChildAndReleasesDeployment()
    {
        using var helper = new FakeAudioHelperDeployment(_temp.Path);
        var saved = Environment.GetEnvironmentVariable("AGENT_RECORDER_FAKE_HANG");
        Environment.SetEnvironmentVariable("AGENT_RECORDER_FAKE_HANG", "1");
        try
        {
            var provider = new ProcessStorageCapacityProvider { ExecutablePath = () => helper.ExecutablePath };
            using var guard = new WindowStorageSafety(CaptureWritePaths.Freeze(Config(false)), 60, provider)
                { QueryTimeout = TimeSpan.FromMilliseconds(100) };
            var watch = Stopwatch.StartNew();
            Assert.Equal(WindowStorageSafety.UnavailableCode, await guard.CheckAsync(false));
            Assert.True(watch.Elapsed < TimeSpan.FromSeconds(3));
            Assert.True(provider.LastProbeProcessId > 0);
            Assert.Throws<ArgumentException>(() => Process.GetProcessById(provider.LastProbeProcessId));
        }
        finally { Environment.SetEnvironmentVariable("AGENT_RECORDER_FAKE_HANG", saved); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SpaceLostBeforeDeferredVideoEntryStopsPreparedWorkerWithoutStartingCapture(bool audioRequested)
    {
        var cfg = Config(audioRequested);
        var audio = new FakeAudioCaptureWorker(raiseAudioReadyOnStart: true);
        var factory = new FakeAvWorkerFactory { AudioWorker = audio, VideoWorker = new FakeVideoCaptureWorker() };
        var session = new StorageWgcSession();
        using ICaptureBackend backend = audioRequested
            ? new AvSplitCaptureBackend(factory, new FakeExternalProcessRunner(), new TempRetentionPolicy(_temp.Path))
            : new WgcContinuousCaptureBackend(_ => session, StagingToFinalPublisher.Instance,
                _ => new OutputMeta(), () => "fake.exe", _temp.Path);
        var tray = new Tray(); var audit = new Audit(); var clock = new StorageTestClock();
        using var engine = Engine(backend, cfg, tray, audit, clock);
        engine.BeforeStartActionForTests = (_, action) =>
        {
            if (action == (audioRequested ? "start_video" : "start_capture")) _provider.Free = _ => 0;
        };
        var rec = Recording(cfg);
        engine.StartCaptureForTests(rec, tray);
        await Until(() => rec.IsFinalized && tray.FailureReasons.Count == 1);
        Assert.Equal(RecState.failed, rec.State);
        Assert.Equal(WindowStorageSafety.LowSpaceCode, rec.StopReason);
        if (audioRequested)
        {
            Assert.True(audio.HasExited);
            Assert.Equal(1, factory.CreateAudioWorkerCount);
            Assert.Equal(0, factory.CreateVideoWorkerCount);
        }
        else
        {
            Assert.Equal(1, session.Stops);
            Assert.Equal(0, session.Authorizations);
        }
        await cfg.StorageSafety!.MonitorCompletion.WaitAsync(TimeSpan.FromSeconds(5));
    }

    private (string Video, string Audio, string Mux) CreateMediaFixtures()
    {
        var video = Path.Combine(_temp.Path, "fixture.mp4");
        var audio = Path.Combine(_temp.Path, "fixture.wav");
        var mux = Path.Combine(_temp.Path, "fixture-av.mp4");
        RunFfmpeg($"-y -v error -f lavfi -i testsrc=duration=2:size=320x240:rate=10 -pix_fmt yuv420p -c:v libx264 \"{video}\"");
        RunFfmpeg($"-y -v error -f lavfi -i sine=frequency=1000:duration=3 -acodec pcm_s16le -ar 48000 -ac 2 \"{audio}\"");
        RunFfmpeg($"-y -v error -i \"{video}\" -i \"{audio}\" -filter_complex \"[1:a]atrim=duration=2,asetpts=PTS-STARTPTS[a]\" -c:v copy -c:a aac -map 0:v:0 -map \"[a]\" \"{mux}\"");
        return (video, audio, mux);
    }

    private static void RunFfmpeg(string args)
    {
        using var process = Process.Start(new ProcessStartInfo(FfmpegLocator.FfmpegPath, args)
            { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true })!;
        Assert.True(process.WaitForExit(10000));
        Assert.True(process.ExitCode == 0, process.StandardError.ReadToEnd());
    }

    private sealed class GatedMuxRunner(string fixture, bool block) : IExternalProcessRunner
    {
        internal TaskCompletionSource Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal Action? AfterMux;
        public async Task<ExternalProcessResult> RunAsync(string fileName, IReadOnlyList<string> args, TimeSpan timeout,
            bool captureStderr = true, Encoding? stderrEncoding = null, CancellationToken cancellationToken = default)
        {
            Entered.TrySetResult();
            if (block) await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            File.Copy(fixture, args[^1], true);
            AfterMux?.Invoke();
            return new ExternalProcessResult(0, false, "");
        }
    }

    private sealed class PausedCopy : IFileSystemOperations
    {
        private readonly PhysicalFileSystemOperations _files = new();
        internal TaskCompletionSource Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public void CreateDirectory(string path) => _files.CreateDirectory(path);
        public long GetFileSize(string path) => _files.GetFileSize(path);
        public async Task CopyFileAsync(string source, string destination, CancellationToken token)
        {
            await _files.CopyFileAsync(source, destination, token);
            Entered.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
        }
        public void FlushFileToDisk(string path) => _files.FlushFileToDisk(path);
        public void MoveFile(string source, string destination) => _files.MoveFile(source, destination);
        public void DeleteFile(string path) => _files.DeleteFile(path);
    }

    private static async Task Until(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (!condition()) await Task.Delay(10, timeout.Token);
    }

    private sealed class Audit : AuditLogger
    {
        internal ConcurrentQueue<string> Events { get; } = new();
        public override void Log(string evt, object payload) => Events.Enqueue(evt);
    }

    private sealed class Tray : ITrayContext, IRecordingFailureNotifier
    {
        private int _idleCount;
        internal ConcurrentQueue<string> FailureReasons { get; } = new();
        internal ConcurrentQueue<bool> IdleBeforeFailure { get; } = new();
        public string HostMode => "tray";
        public bool SupportsRegionSelectionUi => false;
        public void RequestConfirmation(RecordingConfirmationPresentation presentation, Action<ConfirmationDecision> callback) { }
        public void RequestRegionSelection(int timeoutSeconds, Action<string, int, int, int, int, string, string> callback) { }
        public void SetRecording(RecordingUiPresentation presentation) { }
        public void SetIdle(RecordingUiPresentation presentation) => Interlocked.Increment(ref _idleCount);
        public void SetAllIdle() { }
        public void ShowError(string text) { }
        public void ShowRecordingFailure(string recordingId, string reasonCode)
        {
            IdleBeforeFailure.Enqueue(Volatile.Read(ref _idleCount) > 0);
            FailureReasons.Enqueue(reasonCode);
        }
    }

    private sealed class ControlledBackend : ICaptureBackend, IFirstFrameObservableCaptureBackend
    {
        private Action<int, OutputMeta>? _callback;
        private CaptureConfig? _cfg;
        internal int Starts;
        public event Action<FirstFrameObservation>? FirstFrameObserved;
        public void Start(CaptureConfig cfg, CaptureAuthorizationProof proof)
        {
            _cfg = cfg; Starts++;
            FirstFrameObserved?.Invoke(new FirstFrameObservation { FrameNumber = 1, OutTimeUs = 0 });
        }
        public OutputMeta Stop()
        {
            _cfg?.StorageSafety?.TryCommit(() => { }, true);
            return new OutputMeta { SizeBytes = 2048, DurationSeconds = 60, OutputFileExists = true };
        }
        public int ExitCode => 0;
        public void OnNaturalExit(Action<int, OutputMeta> callback) => _callback = callback;
        internal void Complete() => _callback?.Invoke(0, Stop());
        public void Dispose() { }
    }
}
