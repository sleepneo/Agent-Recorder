using AgentRecorder.Api;
using AgentRecorder.Core;
using AgentRecorder.Infrastructure;
using Xunit;
using CliProgram = AgentRecorder.Cli.Program;

namespace AgentRecorder.Tests;

public sealed partial class OrdinaryWgcBundleTests
{
    [Theory]
    [InlineData("completed")]
    [InlineData("selection_cancelled")]
    [InlineData("expired")]
    public async Task CliQuickRecording423ExercisesRealQuickRouteWithSyntheticDesktopAndPublication(string outcome)
    {
        using var f = RuntimeFixture();
        if (outcome == "completed")
        {
            await AvMuxOwnershipTests.Generate(f.Clip, "color=c=blue:s=64x64:r=15", false, "10");
            f.SyntheticProcessCreatedForTests = process => process.DurationMsOverride = 10_000;
        }
        if (outcome == "expired") f.Engine.ConfirmationTimeout = TimeSpan.FromMilliseconds(150);
        if (outcome == "selection_cancelled") f.Tray.SelectionOutcome = outcome;
        f.StartApi(artifactCheck: true);
        int port = f.Api!.BoundPort;
        var run = CliProgram.QuickRecordAsync(15_000, _ => Task.FromResult(CliQuickRecordingTests.Input),
            _ => Task.FromResult(new CliProgram.QuickHost(
                CliProgram.CreateQuickClient(port, Environment.ProcessId, () => true),
                CliProgram.CreateQuickClient(port, Environment.ProcessId, () => true, singleConnection: true),
                Guid.NewGuid().ToString("D"), ApiKeyAuth.CurrentApiKey, null)));

        if (outcome == "completed")
        {
            await Until(() => f.Tray.Confirmations.Count == 1 || run.IsCompleted);
            Assert.False(run.IsCompleted, run.IsCompletedSuccessfully ? run.Result.ToString() : "");
            var rec = Assert.Single(f.Engine._recs.Values);
            using var allowCommit = new ManualResetEventSlim();
            var pendingCommit = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            f.Writer.BeforePersistForTests = snapshot =>
            {
                if (snapshot.RecordingId == rec.Id && snapshot.Status == "completed")
                {
                    pendingCommit.TrySetResult();
                    if (!allowCommit.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException("owned test gate");
                }
            };
            try
            {
                f.Tray.Confirmations[rec.Id](ConfirmationDecision.Approve());
                await Until(() => rec.State == RecState.recording || rec.IsFinalized);
                Assert.Equal(RecState.recording, rec.State);
                f.Process!.Finish.TrySetResult();
                await pendingCommit.Task.WaitAsync(TimeSpan.FromSeconds(10));
                Assert.False(run.IsCompleted);
            }
            finally { allowCommit.Set(); }

            var result = await run;
            Assert.True(result.Ok, result.ToString());
            Assert.Equal(rec.Id, result.RecordingId);
            Assert.Equal(rec.OutputPath, result.OutputPath);
            Assert.True(File.Exists(result.OutputPath));
            Assert.Equal(new FileInfo(result.OutputPath!).Length, result.OutputSizeBytes);
            Assert.InRange(rec.LastMeta!.DurationSeconds, 9.95, 10.05);
            Assert.Equal(1, f.FactoryCalls);
        }
        else
        {
            var result = await run;
            Assert.False(result.Ok);
            Assert.Equal(outcome, outcome == "expired" ? result.RecordingStatus : result.ReasonCode);
            Assert.Equal(outcome == "expired" ? 1 : 0, f.Tray.Confirmations.Count);
            Assert.Equal(0, f.FactoryCalls);
        }

        Assert.Equal(1, f.Tray.SelectionRequests);
        Assert.Equal(outcome == "selection_cancelled" ? 0 : 1, f.Tray.Confirmations.Count);
        Assert.True(f.Api.BoundPort > 0);
    }
}
