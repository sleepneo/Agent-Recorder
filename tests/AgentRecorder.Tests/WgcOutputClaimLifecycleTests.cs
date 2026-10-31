using System.Reflection;
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
        var backend = CreateBackend(options =>
        {
            CreatePlaceholderMp4(options.OutputPath, 10000);
            session = new FakeSession(options); session.StartTcs.TrySetResult(null); session.AuthorizeTcs.TrySetResult(true);
            return session;
        }, out _, out _);
        File.WriteAllText(Path.Combine(_tempDir, "fake-helper.exe"), "synthetic-not-executed");
        var cfg = CreateValidConfig(path); cfg.CountdownSeconds = 0;
        var rec = new Recording(false) { IsOrdinaryLocalRequest = true, SourceType = "display", OutputPath = path, Config = cfg, CountdownSeconds = 0 };
        var tray = new ClaimNoUi(); engine.SetTray(tray); engine.BackendFactory = _ => (backend, "wgc-continuous");
        Claim(engine, rec, path); engine.StartCaptureForTests(rec, tray);
        Assert.NotNull(session); session!.FireFirstFrame(new() { FrameNumber = 1, TotalSizeBytes = 1 });
        Assert.Equal(RecState.recording, rec.State);
        var callback = (Action<int, OutputMeta>)typeof(WgcContinuousCaptureBackend).GetField("_onNaturalExit", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(backend)!;
        using var ownerEntered = new ManualResetEventSlim(); using var ownerRelease = new ManualResetEventSlim();
        backend.OnNaturalExit((code, meta) =>
        {
            callback(code, meta); // real engine finalization, not synthesized terminal state
            ownerEntered.Set(); Assert.True(ownerRelease.Wait(TimeSpan.FromSeconds(10)));
        });
        if (sessionOutcome == "success") session.CompletionTcs.TrySetResult(session.DefaultResult);
        if (sessionOutcome == "fault") session.CompletionTcs.TrySetException(new IOException("synthetic session failure"));
        if (sessionOutcome == "cancel") session.CompletionTcs.TrySetCanceled();
        var unrelated = Path.Combine(_finalDir, "unrelated.mp4");
        try
        {
            Assert.True(ownerEntered.Wait(TimeSpan.FromSeconds(5))); Assert.True(rec.IsFinalized);
            Assert.Equal(sessionOutcome == "success" ? RecState.completed : RecState.failed, rec.State);
            var staging = (TaskCompletionSource<object?>)typeof(WgcContinuousCaptureBackend).GetField("_stagingReleased", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(backend)!;
            Assert.True(staging.Task.IsCompletedSuccessfully); Assert.False(backend.OutputRetirement.IsCompleted);
            Assert.True(Read(engine, path)!.Paths.Single().InUse);
            Claim(engine, new Recording(false) { IsOrdinaryLocalRequest = true, OutputPath = unrelated }, unrelated);
            Assert.True(Read(engine, path, unrelated)!.Paths.All(p => p.InUse));
        }
        finally { ownerRelease.Set(); await backend.OutputRetirement.WaitAsync(TimeSpan.FromSeconds(5)); }
        Assert.True(backend.OutputRetirement.IsCompletedSuccessfully);
        var next = Path.Combine(_finalDir, "next.mp4");
        Claim(engine, new Recording(false) { IsOrdinaryLocalRequest = true, OutputPath = next }, next);
        var final = Read(engine, path, unrelated, next)!;
        Assert.False(final.Paths[0].InUse); Assert.True(final.Paths[1].InUse); Assert.True(final.Paths[2].InUse);
        Assert.Equal(1, session.StartCallCount); // all inputs are fake; no screen/audio process
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
