using System.Reflection;
using System.Text.Json;
using AgentRecorder.Capture;
using AgentRecorder.Core;
using AgentRecorder.Infrastructure;
using Xunit;
using static AgentRecorder.Tests.RuntimeOutputTestEngine;

namespace AgentRecorder.Tests;

public sealed partial class WgcContinuousCaptureBackendTests
{
    [Theory]
    [InlineData("success")] [InlineData("fault")] [InlineData("cancel")]
    public async Task OutputClaimWaitsForFormalWgcCompletionOwner(string sessionOutcome)
    {
        using var engine = new RecordingEngine(new CaptureAuditLogger());
        var path = Path.Combine(_finalDir, "claim.mp4");
        FakeSession? session = null;
        string? ownedStaging = null;
        var backend = CreateBackend(options =>
        {
            CreatePlaceholderMp4(options.OutputPath, 10000);
            ownedStaging = Path.GetDirectoryName(options.OutputPath)!;
            File.WriteAllText(Path.Combine(ownedStaging, "384r-marker"), "owned synthetic resource");
            session = new FakeSession(options, autoRetireResources: false); session.StartTcs.TrySetResult(null); session.AuthorizeTcs.TrySetResult(true);
            return session;
        }, out _, out _);
        File.WriteAllText(Path.Combine(_tempDir, "fake-helper.exe"), "synthetic-not-executed");
        var cfg = CreateValidConfig(path); cfg.CountdownSeconds = 0;
        var rec = new Recording(false) { IsOrdinaryLocalRequest = true, SourceType = "display", OutputPath = path, Config = cfg, CountdownSeconds = 0 };
        var tray = new ClaimNoUi(); engine.SetTray(tray); engine.BackendFactory = _ => (backend, "wgc-continuous");
        using var ownerEntered = new ManualResetEventSlim(); using var ownerRelease = new ManualResetEventSlim();
        var unrelated = Path.Combine(_finalDir, "unrelated.mp4");
        try
        {
            Claim(engine, rec, path); engine.StartCaptureForTests(rec, tray);
            Assert.NotNull(session); session!.FireFirstFrame(new() { FrameNumber = 1, TotalSizeBytes = 1 });
            Assert.Equal(RecState.recording, rec.State);
            var callback = (Action<int, OutputMeta>)typeof(WgcContinuousCaptureBackend).GetField("_onNaturalExit", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(backend)!;
            backend.OnNaturalExit((code, meta) =>
            {
                callback(code, meta); // real engine finalization, not synthesized terminal state
                ownerEntered.Set(); Assert.True(ownerRelease.Wait(TimeSpan.FromSeconds(10)));
            });
            if (sessionOutcome == "success") session.CompletionTcs.TrySetResult(session.DefaultResult);
            if (sessionOutcome == "fault") session.CompletionTcs.TrySetException(new IOException("synthetic session failure"));
            if (sessionOutcome == "cancel") session.CompletionTcs.TrySetCanceled();
            Assert.True(ownerEntered.Wait(TimeSpan.FromSeconds(5))); Assert.True(rec.IsFinalized);
            Assert.Equal(sessionOutcome == "success" ? RecState.completed : RecState.failed, rec.State);
            var staging = (TaskCompletionSource<object?>)typeof(WgcContinuousCaptureBackend).GetField("_stagingReleased", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(backend)!;
            TraceClaimRetirement384R(backend, session, ownedStaging!, "callback_entered_resources_pending");
            Assert.False(session.ResourceRetirement.IsCompleted);
            Assert.False(staging.Task.IsCompleted);
            Assert.True(Directory.Exists(ownedStaging));
            Assert.True(File.Exists(Path.Combine(ownedStaging!, "384r-marker")));
            Assert.False(backend.OutputRetirement.IsCompleted);
            Assert.True(Read(engine, path)!.Paths.Single().InUse);

            // Resource retirement and result handling have independent continuations.
            // Callback entry alone cannot establish successful staging cleanup.
            session.ConfirmResourceRetirementForTests();
            try { await staging.Task.WaitAsync(TimeSpan.FromSeconds(5)); }
            catch
            {
                TraceClaimRetirement384R(backend, session, ownedStaging!, "cleanup_wait_failed");
                throw;
            }
            var owners = TraceClaimRetirement384R(backend, session, ownedStaging!, "staging_retired_callback_still_blocked");
            Assert.True(session.ResourceRetirement.IsCompletedSuccessfully);
            Assert.True(staging.Task.IsCompletedSuccessfully);
            Assert.False(Directory.Exists(ownedStaging));
            Assert.False(File.Exists(Path.Combine(ownedStaging!, "384r-marker")));
            Assert.True(owners.SetupFinished);
            Assert.Equal(0, owners.FileOwners);
            Assert.Equal(1, owners.ResultHosts);
            Assert.False(owners.HostRetired);
            Assert.False(backend.OutputRetirement.IsCompleted);
            Assert.True(Read(engine, path)!.Paths.Single().InUse);
            Claim(engine, new Recording(false) { IsOrdinaryLocalRequest = true, OutputPath = unrelated }, unrelated);
            Assert.True(Read(engine, path, unrelated)!.Paths.All(p => p.InUse));
        }
        finally
        {
            session?.ConfirmResourceRetirementForTests();
            ownerRelease.Set();
            try
            {
                // Also retire a session created before an early setup/assertion failure.
                if (session is null || !session.CompletionTask.IsCompleted) backend.Dispose();
                if (session is not null) await session.ResourceRetirement.WaitAsync(TimeSpan.FromSeconds(5));
                await backend.OutputRetirement.WaitAsync(TimeSpan.FromSeconds(5));
                Assert.False(Directory.Exists(ownedStaging));
            }
            finally { TraceClaimRetirement384R(backend, session, ownedStaging, "finally_retirement"); }
        }
        Assert.True(backend.OutputRetirement.IsCompletedSuccessfully);
        var next = Path.Combine(_finalDir, "next.mp4");
        Claim(engine, new Recording(false) { IsOrdinaryLocalRequest = true, OutputPath = next }, next);
        var final = Read(engine, path, unrelated, next)!;
        Assert.False(final.Paths[0].InUse); Assert.True(final.Paths[1].InUse); Assert.True(final.Paths[2].InUse);
        Assert.Equal(1, session.StartCallCount); // all inputs are fake; no screen/audio process
    }
    private (bool SetupFinished, int FileOwners, int ResultHosts, bool HostRetired) TraceClaimRetirement384R(
        WgcContinuousCaptureBackend backend, FakeSession? session, string? dir, string phase)
    {
        object Field(string name) => typeof(WgcContinuousCaptureBackend).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(backend)!;
        Task setup, staging, host;
        int fileOwners, resultHosts;
        bool cleanupRunning;
        lock (Field("_lifecycleLock"))
        {
            setup = ((TaskCompletionSource)Field("_setupFinished")).Task;
            staging = ((TaskCompletionSource<object?>)Field("_stagingReleased")).Task;
            host = ((TaskCompletionSource)Field("_resultHostRetired")).Task;
            fileOwners = (int)Field("_resultOwners");
            resultHosts = (int)Field("_resultHosts");
            cleanupRunning = (bool)Field("_cleanupRunning");
        }
        // Keep filesystem observations and test output outside the production lock.
        _output.WriteLine(JsonSerializer.Serialize(new
        {
            phase, resource = session?.ResourceRetirement.Status.ToString() ?? "no_session", setup = setup.Status.ToString(),
            fileOwners, resultHosts, cleanupRunning, cleanupSignal = staging.Status.ToString(),
            hostRetired = host.Status.ToString(), directoryExists = Directory.Exists(dir),
            markerExists = dir is not null && File.Exists(Path.Combine(dir, "384r-marker")),
            cleanupFailure = "No production error signal is exposed; pending cleanup alone does not identify an IO failure"
        }));
        return (setup.IsCompletedSuccessfully, fileOwners, resultHosts, host.IsCompletedSuccessfully);
    }
    private sealed class ClaimNoUi : ITrayContext
    {
        public string HostMode => "headless"; public bool SupportsRegionSelectionUi => false;
        public void RequestConfirmation(RecordingConfirmationPresentation p, Action<ConfirmationDecision> c) { }
        public void RequestRegionSelection(int t, Action<string, int, int, int, int, string, string> c) { }
        public void SetRecording(RecordingUiPresentation p) { } public void SetIdle(RecordingUiPresentation p) { }
        public void SetAllIdle() { } public void ShowError(string text) { }
    }
}
