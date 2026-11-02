using System.Net;
using System.Reflection;
using AgentRecorder.Api;
using AgentRecorder.Capture;
using AgentRecorder.Core;
using AgentRecorder.Infrastructure;
using AgentRecorder.Persistence;
using Xunit;

namespace AgentRecorder.Tests;

public sealed partial class WindowStorageEngineTests
{
    [Theory]
    [InlineData("clean")]
    [InlineData("late-probe")]
    [InlineData("cleanup")]
    [InlineData("unknown-sync")]
    [InlineData("unknown-async")]
    public async Task FailedRecordingHistoryAndNotificationSurviveExactOwnerHttpShutdownClassification(string mode)
    {
        var cfg = Config(false);
        var output = Path.GetDirectoryName(cfg.OutputPath)!;
        Directory.CreateDirectory(output);
        var runtime = false;
        var held = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var evidence = new StorageStartupEvidence(mode);
        var provider = evidence.CreateProvider(() => runtime, beforeDispose: async () =>
            {
                if (!runtime) return;
                if (mode == "cleanup") throw new IOException("owned-cleanup-fault");
                if (mode == "late-probe") { held.TrySetResult(); await release.Task; }
            });
        IStorageCapacityProvider selected = mode.StartsWith("unknown")
            ? new FakeStorageProvider { Query = (paths, _) => !runtime
                ? Task.FromResult<IReadOnlyList<StorageVolumeSample>>(paths.Select(p => new StorageVolumeSample(p, "owned-volume", 1L << 40)).ToArray())
                : mode == "unknown-sync" ? throw new IOException("unknown-owner")
                : Task.FromException<IReadOnlyList<StorageVolumeSample>>(new IOException("unknown-owner")) }
            : provider;
        var unsafeRetirement = mode == "cleanup" || mode.StartsWith("unknown");
        var clock = new StorageTestClock();
        // Freeze exact write paths before the ordinary monitor starts. The session
        // below is synthetic; only the capacity subprocess is a real formal App.
        cfg.WritePaths = CaptureWritePaths.Freeze(cfg);
        cfg.StorageSafety = new WindowStorageSafety(cfg.WritePaths, 60, selected)
            { Delay = clock.Delay, QueryTimeout = TimeSpan.FromMilliseconds(500) };
        var session = new StorageWgcSession();
        using var backend = new WgcContinuousCaptureBackend(_ => session, StagingToFinalPublisher.Instance,
            _ => new OutputMeta(), () => "fake.exe", _temp.Path);
        var tray = new Tray(); var audit = new Audit();
        using var engine = Engine(backend, cfg, tray, audit, clock);
        var store = new SqliteOperationalStore(Path.Combine(_temp.Path, "failure-history.db"));
        store.Initialize();
        using var history = new SqliteOrdinaryRecordingHistoryStore(store);
        engine.ConfigureOrdinaryRecordingHistory(history);
        var rec = Recording(cfg); rec.IsOrdinaryLocalRequest = true; rec.State = RecState.pending_confirmation;
        // Direct start bypasses ordinary admission; bind its same existing
        // terminal publication callback before testing the actual failure chain.
        rec.OrdinaryHistoryPublication = typeof(RecordingEngine).GetMethod("PublishOrdinaryHistorySnapshot",
            BindingFlags.Instance | BindingFlags.NonPublic)!.CreateDelegate<Action<Recording>>(engine);
        var snapshot = (OrdinaryRecordingHistorySnapshot)typeof(RecordingEngine).GetMethod("CreateOrdinaryHistorySnapshot",
            BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, new object[] { rec })!;
        history.Register(snapshot);
        ApiKeyAuth.InitializeForTesting(_temp.Path);
        var server = new ApiServer(engine, audit, tray, null, null, null, null, null, null, null, null, null, 0);
        var exits = 0;
        server.ConfigureHostShutdown(() => null, () => Interlocked.Increment(ref exits));
        server.Start();
        using var http = new HttpClient(new HttpClientHandler { UseProxy = false })
            { BaseAddress = new Uri($"http://127.0.0.1:{server.BoundPort}"), Timeout = TimeSpan.FromSeconds(10) };
        http.DefaultRequestHeaders.Add("X-Agent-Recorder-Key", ApiKeyAuth.CurrentApiKey);
        backend.FirstFrameObserved += _ => evidence.FirstFrame();
        try
        {
            engine.StartCaptureForTests(rec, tray);
            var startup = await StorageStartupWait(engine, rec, 10_000);
            var startupEvidence = evidence.Save("startup-" + startup, rec, cfg.StorageSafety, session, audit);
            Assert.True(startup == "recording", startupEvidence);
            await clock.WaitForDelay();
            Assert.Empty(Directory.EnumerateFileSystemEntries(output));
            Directory.Delete(output, recursive: false);
            File.WriteAllText(output, "owned unavailable directory fixture");
            runtime = true;
            clock.Tick();
            if (mode == "late-probe") await held.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await Until(() => rec.IsFinalized && tray.FailureReasons.Count == 1);
            await cfg.StorageSafety.MonitorCompletion.WaitAsync(TimeSpan.FromSeconds(5));
            await backend.OutputRetirement.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(RecState.failed, rec.State);
            Assert.Equal(WindowStorageSafety.UnavailableCode, rec.StopReason);
            Assert.Equal(new[] { WindowStorageSafety.UnavailableCode }, tray.FailureReasons.ToArray());
            Assert.Equal(1, session.Stops);
            Assert.False(File.Exists(cfg.OutputPath));
            if (mode == "late-probe")
            {
                var exactRuntimeLife = evidence.SingleRuntimeLifetime();
                Assert.True(exactRuntimeLife.ExitCompletion.IsCompletedSuccessfully);
                Assert.False(exactRuntimeLife.RetirementCompletion.IsCompleted);
                using var busy = await http.PostAsync("/api/v1/host/shutdown", new StringContent("{}"));
                Assert.Equal(HttpStatusCode.Conflict, busy.StatusCode);
                Assert.Contains("storage_resources_pending", await busy.Content.ReadAsStringAsync());
                Assert.False(engine.HostAdmission.IsClosed); Assert.Equal(0, exits);
                release.SetResult();
            }
            await cfg.StorageSafety.ProbeRetirementCompletion.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.NotNull(cfg.StorageSafety.ProbeQueryFailure);
            Assert.Equal(unsafeRetirement, cfg.StorageSafety.ProbeRetirementFailure is not null);
            using var response = await http.PostAsync("/api/v1/host/shutdown", new StringContent("{}"));
            Assert.Equal(unsafeRetirement ? HttpStatusCode.Conflict : HttpStatusCode.Accepted, response.StatusCode);
            if (unsafeRetirement)
            {
                Assert.Contains("storage_resources_pending", await response.Content.ReadAsStringAsync());
                Assert.Contains(mode == "cleanup" ? "owned-cleanup-fault" : "unknown-owner", cfg.StorageSafety.ProbeRetirementFailure!.ToString());
                Assert.False(engine.HostAdmission.IsClosed); Assert.Equal(0, exits);
            }
            else await Until(() => Volatile.Read(ref exits) == 1);
            Assert.Single(engine._recs); // Failure remains; no second recording/retry.
            Assert.True(history.DrainAndDispose(TimeSpan.FromSeconds(5)));
            using var reopened = new SqliteOrdinaryRecordingHistoryStore(store);
            var durable = reopened.Read(rec.Id)!;
            Assert.Equal("failed", durable.Status);
            Assert.Equal(WindowStorageSafety.UnavailableCode, durable.ReasonCode);
            Assert.NotNull(durable.CompletedAtUtc);
            evidence.Save("passed-before-cleanup", rec, cfg.StorageSafety, session, audit);
        }
        catch
        {
            evidence.Save("failure-before-cleanup", rec, cfg.StorageSafety, session, audit);
            throw;
        }
        finally
        {
            release.TrySetResult();
            cfg.StorageSafety.Dispose();
            await cfg.StorageSafety.MonitorCompletion.WaitAsync(TimeSpan.FromSeconds(5));
            await cfg.StorageSafety.ProbeRetirementCompletion.WaitAsync(TimeSpan.FromSeconds(5));
            server.Stop(); ApiKeyAuth.ResetForTesting(null);
        }
    }
}
