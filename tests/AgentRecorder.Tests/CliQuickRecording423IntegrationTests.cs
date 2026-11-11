using System.Net;
using System.Text.Json;
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
    [InlineData("invalid_countdown")]
    [InlineData("backend_creation_failed")]
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
        if (outcome == "backend_creation_failed") f.Engine.BackendFactory = _ =>
        {
            Interlocked.Increment(ref f.FactoryCalls);
            throw new IOException("private synthetic factory fault");
        };
        f.StartApi(artifactCheck: true);
        int port = f.Api!.BoundPort;
        var input = outcome == "invalid_countdown"
            ? """{"target":{"type":"selected_region"},"duration_seconds":10,"countdown_seconds":11}"""
            : CliQuickRecordingTests.Input;
        var run = CliProgram.QuickRecordAsync(15_000, _ => Task.FromResult(input),
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
            Assert.Null(result.ApiErrorCode);
            Assert.Null(result.RecordingStopReason);
        }
        else if (outcome == "backend_creation_failed")
        {
            await Until(() => f.Tray.Confirmations.Count == 1 || run.IsCompleted);
            var rec = Assert.Single(f.Engine._recs.Values);
            f.Tray.Confirmations[rec.Id](ConfirmationDecision.Approve());
            var result = await run;
            Assert.False(result.Ok);
            Assert.Equal("recording_failed", result.Status);
            Assert.Equal("recording_terminal_failure", result.ReasonCode);
            Assert.Equal("failed", result.RecordingStatus);
            Assert.Equal(rec.Id, result.RecordingId);
            Assert.Equal(outcome, result.RecordingStopReason);
            Assert.Null(result.ApiErrorCode);
            Assert.Null(result.OutputPath);
            Assert.Equal(1, f.FactoryCalls);
        }
        else if (outcome == "invalid_countdown")
        {
            var result = await run;
            Assert.False(result.Ok);
            Assert.Equal("not_submitted", result.Status);
            Assert.Equal("quick_rejected", result.ReasonCode);
            Assert.Equal("INVALID_ARGUMENT", result.ApiErrorCode);
            Assert.Null(result.RecordingId);
            Assert.Null(result.RecordingStopReason);
            Assert.Empty(f.Engine._recs);
            Assert.Equal(0, f.FactoryCalls);
        }
        else
        {
            var result = await run;
            Assert.False(result.Ok);
            Assert.Equal(outcome, outcome == "expired" ? result.RecordingStatus : result.ReasonCode);
            Assert.Equal(outcome == "expired" ? "confirmation_expired" : null, result.RecordingStopReason);
            Assert.Null(result.ApiErrorCode);
            Assert.Equal(0, f.FactoryCalls);
        }

        Assert.Equal(outcome == "invalid_countdown" ? 0 : 1, f.Tray.SelectionRequests);
        Assert.Equal(outcome is "selection_cancelled" or "invalid_countdown" ? 0 : 1, f.Tray.Confirmations.Count);
        Assert.True(f.Api.BoundPort > 0);
    }

    [Fact]
    public async Task CliQuickRecording424RProjectsRealPreConfirmationStorageRejection()
    {
        using var f = RuntimeFixture();
        var capacity = new FakeStorageProvider { Free = _ => 0 };
        RecordingPreflightChecker.StorageCapacityProvider = capacity;
        f.StartApi();
        int port = f.Api!.BoundPort;
        var observer = new QuickSubmissionObserver();
        var result = await CliProgram.QuickRecordAsync(15_000, _ => Task.FromResult(CliQuickRecordingTests.Input),
            _ => Task.FromResult(new CliProgram.QuickHost(
                CliProgram.CreateQuickClient(port, Environment.ProcessId, () => true),
                new HttpClient(observer) { BaseAddress = new Uri($"http://127.0.0.1:{port}/api/v1/") },
                Guid.NewGuid().ToString("D"), ApiKeyAuth.CurrentApiKey, null)));

        Assert.Equal(HttpStatusCode.BadRequest, observer.Status);
        Assert.Equal("storage_space_low", observer.ErrorCode);
        Assert.Equal(1, observer.Posts);
        var json = CliQuickRecordingTests.Output(result);
        Assert.False(result.Ok);
        Assert.Equal("not_submitted", result.Status);
        Assert.Equal("quick_rejected", result.ReasonCode);
        Assert.Equal("storage_space_low", json.GetProperty("api_error_code").GetString());
        Assert.False(json.TryGetProperty("recording_id", out _));
        Assert.False(json.TryGetProperty("recording_stop_reason", out _));
        Assert.True(capacity.Calls > 0);
        Assert.Equal(1, f.Tray.SelectionRequests);
        Assert.Empty(f.Engine._recs);
        Assert.Empty(f.Engine._confs);
        Assert.Empty(f.Tray.Confirmations);
        Assert.Equal(0, f.FactoryCalls);
    }

    private sealed class QuickSubmissionObserver() : DelegatingHandler(new HttpClientHandler { UseProxy = false, AllowAutoRedirect = false })
    {
        internal int Posts { get; private set; }
        internal HttpStatusCode? Status { get; private set; }
        internal string? ErrorCode { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal("/api/v1/recordings/quick", request.RequestUri!.AbsolutePath);
            Posts++;
            var response = await base.SendAsync(request, token);
            Status = response.StatusCode;
            using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(token));
            ErrorCode = body.RootElement.GetProperty("error").GetProperty("code").GetString();
            return response;
        }
    }
}
