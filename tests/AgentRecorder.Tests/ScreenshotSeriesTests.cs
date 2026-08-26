using System.IO.Compression;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.Json;
using AgentRecorder.Capture;
using AgentRecorder.Core;
using AgentRecorder.Infrastructure;
using AgentRecorder.Logging;
using Xunit;

namespace AgentRecorder.Tests;

[Collection("NonParallel-SystemQueryProviders")]
public sealed class ScreenshotSeriesTests : IDisposable
{
    private readonly string _dataDir = Path.Combine(Path.GetTempPath(), "agent-recorder-series-" + Guid.NewGuid().ToString("N"));
    private readonly string? _oldTestMode;
    private readonly string? _oldDataDir;

    public ScreenshotSeriesTests()
    {
        _oldTestMode = Environment.GetEnvironmentVariable("AGENT_RECORDER_TEST_MODE");
        _oldDataDir = Environment.GetEnvironmentVariable("AGENT_RECORDER_DATA_DIR");
        Environment.SetEnvironmentVariable("AGENT_RECORDER_TEST_MODE", "1");
        Environment.SetEnvironmentVariable("AGENT_RECORDER_DATA_DIR", _dataDir);
        Directory.CreateDirectory(_dataDir);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("AGENT_RECORDER_TEST_MODE", _oldTestMode);
        Environment.SetEnvironmentVariable("AGENT_RECORDER_DATA_DIR", _oldDataDir);
        try { if (Directory.Exists(_dataDir)) Directory.Delete(_dataDir, true); } catch { }
    }

    [Fact]
    public void NormalizeSeries_CountAndDurationContractsAreStrictAndBounded()
    {
        var byCount = ConfigParser.NormalizeModeAndSeries(JsonNode.Parse(
            "{\"mode\":\"screenshot_series\",\"interval_ms\":1000,\"max_count\":3}")!);
        Assert.NotNull(byCount);
        Assert.Equal(3, byCount!.PlannedFrameCount);
        Assert.Null(byCount.MaxDurationSeconds);

        var byDuration = ConfigParser.NormalizeModeAndSeries(JsonNode.Parse(
            "{\"mode\":\"screenshot_series\",\"interval_ms\":1500,\"max_duration_seconds\":4}")!);
        Assert.NotNull(byDuration);
        Assert.Equal(3, byDuration!.PlannedFrameCount);

        var ex = Assert.Throws<ApiException>(() => ConfigParser.NormalizeModeAndSeries(JsonNode.Parse(
            "{\"mode\":\"screenshot_series\",\"interval_ms\":1000,\"max_count\":3.0}")!));
        Assert.Equal("INVALID_ARGUMENT", ex.Code);
    }

    [Fact]
    public void NormalizeSeries_DurationPlanPointsAreStrictlyBeforeDeadline()
    {
        var config = ConfigParser.NormalizeModeAndSeries(JsonNode.Parse(
            "{\"mode\":\"screenshot_series\",\"interval_ms\":1500,\"max_duration_seconds\":4}")!);

        Assert.NotNull(config);
        Assert.Equal(3, config!.PlannedFrameCount);
        Assert.All(Enumerable.Range(0, config.PlannedFrameCount), index =>
            Assert.True((long)index * config.IntervalMs < config.MaxDurationSeconds!.Value * 1000L));
    }

    [Fact]
    public void NormalizeSeries_AudioIsRejectedBeforeAudioProviderRuns()
    {
        var providerCalls = 0;
        var ex = Assert.Throws<ApiException>(() => ConfigParser.Build(JsonNode.Parse(
            "{\"mode\":\"screenshot_series\",\"interval_ms\":1000,\"max_count\":1,\"audio\":{\"microphone\":{\"enabled\":true}},\"source\":{\"type\":\"display\",\"display_id\":\"missing\"}}")!,
            "test", out _, new CountingMicrophoneProvider(() => providerCalls++)));

        Assert.Equal("INVALID_ARGUMENT", ex.Code);
        Assert.Equal(0, providerCalls);
    }

    [Fact]
    public void NormalizeSeries_StopConditionIsRejectedBeforeAudioOrSourceResolution()
    {
        var providerCalls = 0;
        var ex = Assert.Throws<ApiException>(() => ConfigParser.Build(JsonNode.Parse(
            "{\"mode\":\"screenshot_series\",\"interval_ms\":1000,\"max_count\":2,\"stop_condition\":{\"type\":\"manual\"},\"source\":{\"type\":\"display\",\"display_id\":\"display_1\"}}")!,
            "test", out _, new CountingMicrophoneProvider(() => providerCalls++)));

        Assert.Equal(400, ex.Status);
        Assert.Equal("INVALID_ARGUMENT", ex.Code);
        Assert.Contains("stop_condition", ex.Message, StringComparison.Ordinal);
        Assert.Contains("remove_stop_condition", ex.Details?.ToString() ?? "", StringComparison.Ordinal);
        Assert.Equal(0, providerCalls);
    }

    [Fact]
    public void ScreenshotPlan_AllTargetsUseSingleFrameAndHonestSemantics()
    {
        var cases = new[]
        {
            (source: "display", semantics: "display_surface"),
            (source: "region", semantics: "region_rectangle"),
            (source: "window", semantics: "screen_rectangle")
        };

        foreach (var item in cases)
        {
            var cfg = new CaptureConfig
            {
                Mode = ScreenshotSeriesConfig.ModeName,
                SourceKind = item.source,
                Bounds = (0, 0, 32, 32),
                ScreenshotSeries = new ScreenshotSeriesConfig { IntervalMs = 1000, MaxCount = 1, PlannedFrameCount = 1 }
            };
            var plan = CaptureBackendSelector.BuildScreenshotSeriesPlan(cfg);

            Assert.Equal("ffmpeg-single-frame", plan.PlannedBackend);
            Assert.Equal(item.semantics, plan.CaptureSemantics);
            Assert.Equal(item.semantics, plan.PreviewSemantics);
            Assert.False(plan.IsWindowSurface);
        }
    }

    [Fact]
    public void ScreenshotPlan_WindowSurfaceIsRejectedBeforeWorkerStarts()
    {
        var rec = CreateSeriesRecording(1, "window");
        rec.ApprovedCapturePlan = new CapturePlan(
            "wgc-continuous",
            "wgc-continuous",
            new CaptureBackendSelectionEvidence("wgc-continuous", "wgc-continuous", "test", "not_run", null, false),
            "window_surface",
            "window",
            "window_test",
            nint.Zero,
            new CapturePlanBounds(0, 0, 32, 32));
        var runner = new FakePngRunner(32, 32);
        using var engine = new RecordingEngine(new AuditLogger())
        {
            ScreenshotFrameRunnerFactoryForTests = _ => runner
        };

        var ex = Assert.Throws<ApiException>(() => engine.StartCaptureForTests(rec, new TestTray()));

        Assert.Equal("UNSUPPORTED_FEATURE", ex.Code);
        Assert.Equal(0, runner.Calls);
        Assert.True(SpinWait.SpinUntil(
            () => engine.ActiveScreenshotSeriesOperationCountForTests == 0,
            TimeSpan.FromSeconds(1)));
        Assert.Equal(0, engine.ActiveScreenshotSeriesOperationCountForTests);
    }

    [Fact]
    public async Task Runner_RejectsMismatchedPlanWithoutStartingFfmpeg()
    {
        var cfg = new CaptureConfig
        {
            Mode = ScreenshotSeriesConfig.ModeName,
            SourceKind = "window",
            Bounds = (0, 0, 32, 32),
            ScreenshotSeries = new ScreenshotSeriesConfig { IntervalMs = 1000, MaxCount = 1, PlannedFrameCount = 1 }
        };
        var path = Path.Combine(_dataDir, "runner-no-start.png");
        var result = await new FfmpegScreenshotFrameRunner().CaptureAsync(
            new ScreenshotFrameRequest(cfg, path, TimeSpan.FromSeconds(1), 1,
                "wgc-continuous", "window_surface", "window", "window_test"),
            CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal("unsupported_capture_plan", result.ErrorCode);
        Assert.False(File.Exists(path));
    }

    [Fact]
    public void PngValidation_RequiresChunksCrcAndDecodablePixels()
    {
        var validPath = Path.Combine(_dataDir, "valid.png");
        var valid = FakePngRunner.BuildPng(32, 32);
        File.WriteAllBytes(validPath, valid);
        Assert.True(ScreenshotSeriesArtifacts.TryValidatePng(validPath, out var width, out var height, out var size));
        Assert.Equal(32, width);
        Assert.Equal(32, height);
        Assert.Equal(valid.LongLength, size);

        var pseudoPath = Path.Combine(_dataDir, "pseudo.png");
        var pseudo = new byte[24];
        Array.Copy(valid, pseudo, 8);
        Buffer.BlockCopy(valid, 16, pseudo, 16, 8);
        File.WriteAllBytes(pseudoPath, pseudo);
        Assert.False(ScreenshotSeriesArtifacts.TryValidatePng(pseudoPath, out _, out _, out _));

        var corruptPath = Path.Combine(_dataDir, "corrupt.png");
        var corrupt = (byte[])valid.Clone();
        corrupt[^5] ^= 0x01;
        File.WriteAllBytes(corruptPath, corrupt);
        Assert.False(ScreenshotSeriesArtifacts.TryValidatePng(corruptPath, out _, out _, out _));

        var truncatedPath = Path.Combine(_dataDir, "truncated.png");
        File.WriteAllBytes(truncatedPath, valid[..^4]);
        Assert.False(ScreenshotSeriesArtifacts.TryValidatePng(truncatedPath, out _, out _, out _));
    }

    [Fact]
    public void ScreenshotSeries_UsesFirstCompletedPngAsAnchorAndKeepsPreparingBeforeIt()
    {
        var rec = CreateSeriesRecording(1);
        var runner = new FakePngRunner(32, 32, delayMs: 120);
        RecState? stateBeforeRunner = null;
        long? anchorBeforeRunner = null;
        using var engine = new RecordingEngine(new AuditLogger())
        {
            ScreenshotFrameRunnerFactoryForTests = _ => runner,
            BeforeScreenshotFrameRunnerForTests = (recording, _) =>
            {
                stateBeforeRunner = recording.State;
                anchorBeforeRunner = recording.MarkTimelineAnchorTicks;
            }
        };

        engine.StartCaptureForTests(rec, new TestTray());
        Assert.True(SpinWait.SpinUntil(() => rec.State is RecState.completed or RecState.failed, TimeSpan.FromSeconds(5)));
        Assert.True(SpinWait.SpinUntil(() => engine.ActiveScreenshotSeriesOperationCountForTests == 0, TimeSpan.FromSeconds(5)));

        Assert.Equal(RecState.preparing, stateBeforeRunner);
        Assert.Null(anchorBeforeRunner);
        Assert.Equal(RecState.completed, rec.State);
        Assert.NotEqual(default, rec.StartedAtUtc);
        Assert.NotEqual(0, rec.ScreenshotSeries!.AnchorTicks);
        Assert.Single(rec.ScreenshotSeries.Frames);
        Assert.True(rec.ScreenshotSeries.Frames[0].CaptureStartedAtUtc < rec.ScreenshotSeries.Frames[0].CompletedAtUtc);
    }

    [Fact]
    public void ScreenshotSeries_SchedulesSecondFrameFromFirstCompletionWithoutParallelRunner()
    {
        var rec = CreateSeriesRecording(2);
        var runner = new FakePngRunner(32, 32, delayMs: 150);
        using var engine = new RecordingEngine(new AuditLogger())
        {
            ScreenshotFrameRunnerFactoryForTests = _ => runner
        };

        engine.StartCaptureForTests(rec, new TestTray());
        Assert.True(SpinWait.SpinUntil(() => rec.State is RecState.completed or RecState.failed, TimeSpan.FromSeconds(6)));

        Assert.Equal(RecState.completed, rec.State);
        Assert.Equal(2, rec.ScreenshotSeries!.Frames.Count);
        var first = rec.ScreenshotSeries.Frames[0];
        var second = rec.ScreenshotSeries.Frames[1];
        Assert.Equal(0, first.ScheduledOffsetMs);
        Assert.Equal(1000, second.ScheduledOffsetMs);
        Assert.True(second.CaptureStartedAtUtc - first.CompletedAtUtc >= TimeSpan.FromMilliseconds(650));
        Assert.Equal(1, runner.MaxInFlight);
    }

    [Fact]
    public void ScreenshotSeries_FirstFrameUsesValidSubmitAsZeroAnchorAndReportsCaptureDuration()
    {
        var rec = CreateSeriesRecording(1);
        long ticks = 0;
        var runner = new ClockedPngRunner(32, 32, _ => ticks += 25);
        using var engine = new RecordingEngine(new AuditLogger())
        {
            MonotonicFrequencyForTests = 1000,
            MonotonicTimestampProviderForTests = () => ticks,
            ScreenshotDelaySchedulerForTests = (due, _) =>
            {
                ticks = Math.Max(ticks, due);
                return Task.CompletedTask;
            },
            ScreenshotFrameRunnerFactoryForTests = _ => runner
        };

        engine.StartCaptureForTests(rec, new TestTray());
        Assert.True(SpinWait.SpinUntil(() => rec.State is RecState.completed or RecState.failed, TimeSpan.FromSeconds(5)));

        var frame = Assert.Single(rec.ScreenshotSeries!.Frames);
        Assert.Equal(RecState.completed, rec.State);
        Assert.Equal(0, frame.ScheduledOffsetMs);
        Assert.Equal(0, frame.CapturedOffsetMs);
        Assert.Equal(0, frame.LatenessMs);
        Assert.Equal(25, frame.CaptureDurationMs);
        Assert.Equal(frame.CapturedAtUtc, frame.CompletedAtUtc);
        Assert.Equal(25, rec.ScreenshotSeries.AnchorTicks);
    }

    [Fact]
    public void ScreenshotSeries_OnTimeLaterClaimHasNoLatenessAndKeepsDurationSeparate()
    {
        var rec = CreateSeriesRecording(2);
        long ticks = 0;
        var runner = new ClockedPngRunner(32, 32, _ => ticks += 25);
        using var engine = new RecordingEngine(new AuditLogger())
        {
            MonotonicFrequencyForTests = 1000,
            MonotonicTimestampProviderForTests = () => ticks,
            ScreenshotDelaySchedulerForTests = (due, _) =>
            {
                ticks = Math.Max(ticks, due);
                return Task.CompletedTask;
            },
            ScreenshotFrameRunnerFactoryForTests = _ => runner
        };

        engine.StartCaptureForTests(rec, new TestTray());
        Assert.True(SpinWait.SpinUntil(() => rec.State is RecState.completed or RecState.failed, TimeSpan.FromSeconds(5)));

        Assert.Equal(RecState.completed, rec.State);
        var second = rec.ScreenshotSeries!.Frames[1];
        Assert.Equal(1000, second.ScheduledOffsetMs);
        Assert.Equal(1025, second.CapturedOffsetMs);
        Assert.Equal(0, second.LatenessMs);
        Assert.Equal(25, second.CaptureDurationMs);
    }

    [Fact]
    public void ScreenshotSeries_SlowFrameCrossingNextDueReportsClaimLatenessWithoutParallelLaunch()
    {
        var rec = CreateSeriesRecording(3);
        long ticks = 0;
        var runner = new ClockedPngRunner(32, 32, index => ticks += index == 2 ? 2100 : 25);
        using var engine = new RecordingEngine(new AuditLogger())
        {
            MonotonicFrequencyForTests = 1000,
            MonotonicTimestampProviderForTests = () => ticks,
            ScreenshotDelaySchedulerForTests = (due, _) =>
            {
                ticks = Math.Max(ticks, due);
                return Task.CompletedTask;
            },
            ScreenshotFrameRunnerFactoryForTests = _ => runner
        };

        engine.StartCaptureForTests(rec, new TestTray());
        Assert.True(SpinWait.SpinUntil(() => rec.State is RecState.completed or RecState.failed, TimeSpan.FromSeconds(5)));

        Assert.Equal(RecState.completed, rec.State);
        Assert.Equal(3, runner.Calls);
        var third = rec.ScreenshotSeries!.Frames[2];
        Assert.Equal(2000, third.ScheduledOffsetMs);
        Assert.Equal(1100, third.LatenessMs);
        Assert.Equal(25, third.CaptureDurationMs);
        Assert.Equal(1, runner.MaxInFlight);
        Assert.Null(rec.ScreenshotSeries.NextCaptureDueAtUtc);
    }

    [Fact]
    public void ScreenshotSeries_DurationStopsAtExactDeadlineBeforeRunnerClaim()
    {
        var rec = CreateDurationRecording(2);
        long ticks = 0;
        var runner = new ClockedPngRunner(32, 32, index => { });
        using var engine = new RecordingEngine(new AuditLogger())
        {
            MonotonicFrequencyForTests = 1000,
            MonotonicTimestampProviderForTests = () => ticks,
            ScreenshotDelaySchedulerForTests = (due, _) =>
            {
                ticks = due == 0 ? 0 : 2_000;
                return Task.CompletedTask;
            },
            ScreenshotFrameRunnerFactoryForTests = _ => runner
        };

        engine.StartCaptureForTests(rec, new TestTray());
        Assert.True(SpinWait.SpinUntil(() => rec.State is RecState.completed or RecState.failed, TimeSpan.FromSeconds(5)));

        Assert.Equal(RecState.completed, rec.State);
        Assert.Equal(1, runner.Calls);
        Assert.Single(rec.ScreenshotSeries!.Frames);
        Assert.Null(rec.ScreenshotSeries.NextCaptureDueAtUtc);
        Assert.Equal(2, rec.ScreenshotSeries.PlannedFrameCount);
        Assert.NotNull(rec.ScreenshotSeries.FinalDirectory);
        Assert.Null(rec.ScreenshotSeries.StagingDirectory);
        Assert.True(SpinWait.SpinUntil(
            () => engine.ActiveScreenshotSeriesOperationCountForTests == 0,
            TimeSpan.FromSeconds(1)));
        Assert.Equal(0, engine.ActiveScreenshotSeriesOperationCountForTests);
        var manifest = JsonNode.Parse(File.ReadAllText(Path.Combine(rec.ScreenshotSeries.FinalDirectory!, "series.json")))!;
        Assert.Equal("completed", manifest["status"]!.GetValue<string>());
        Assert.Equal(2, manifest["series"]!["planned_frame_count"]!.GetValue<int>());
        Assert.Equal(1, manifest["series"]!["captured_frame_count"]!.GetValue<int>());
    }

    [Fact]
    public void ScreenshotSeries_SlowFrameCrossingDeadlineDoesNotStartNextRunner()
    {
        var rec = CreateDurationRecording(3);
        long ticks = 0;
        var runner = new ClockedPngRunner(32, 32, index =>
        {
            if (index == 2)
                ticks += 2500;
        });
        using var engine = new RecordingEngine(new AuditLogger())
        {
            MonotonicFrequencyForTests = 1000,
            MonotonicTimestampProviderForTests = () => ticks,
            ScreenshotDelaySchedulerForTests = (due, _) =>
            {
                ticks = Math.Max(ticks, due);
                return Task.CompletedTask;
            },
            ScreenshotFrameRunnerFactoryForTests = _ => runner
        };

        engine.StartCaptureForTests(rec, new TestTray());
        Assert.True(SpinWait.SpinUntil(() => rec.State is RecState.completed or RecState.failed, TimeSpan.FromSeconds(5)));

        Assert.Equal(RecState.completed, rec.State);
        Assert.Equal(2, runner.Calls);
        Assert.Equal(2, rec.ScreenshotSeries!.Frames.Count);
        Assert.Equal(3, rec.ScreenshotSeries.PlannedFrameCount);
        Assert.Equal(0, engine.ActiveScreenshotSeriesOperationCountForTests);
    }

    [Fact]
    public void ScreenshotSeries_MaxCountStillAttemptsCountAfterSlowFrames()
    {
        var rec = CreateSeriesRecording(3);
        long ticks = 0;
        var runner = new ClockedPngRunner(32, 32, index =>
        {
            if (index > 1)
                ticks += 1500;
        });
        using var engine = new RecordingEngine(new AuditLogger())
        {
            MonotonicFrequencyForTests = 1000,
            MonotonicTimestampProviderForTests = () => ticks,
            ScreenshotDelaySchedulerForTests = (due, _) =>
            {
                ticks = Math.Max(ticks, due);
                return Task.CompletedTask;
            },
            ScreenshotFrameRunnerFactoryForTests = _ => runner
        };

        engine.StartCaptureForTests(rec, new TestTray());
        Assert.True(SpinWait.SpinUntil(() => rec.State is RecState.completed or RecState.failed, TimeSpan.FromSeconds(5)));

        Assert.Equal(RecState.completed, rec.State);
        Assert.Equal(3, runner.Calls);
        Assert.Equal(3, rec.ScreenshotSeries!.Frames.Count);
    }

    [Fact]
    public async Task ScreenshotSeries_StopBeforeFrameClaimDoesNotStartRunnerAndRetiresOperation()
    {
        var rec = CreateSeriesRecording(1);
        var entered = new ManualResetEventSlim(false);
        var release = new ManualResetEventSlim(false);
        var runner = new FakePngRunner(32, 32);
        using var engine = new RecordingEngine(new AuditLogger())
        {
            ScreenshotFrameRunnerFactoryForTests = _ => runner,
            BeforeScreenshotFrameStartClaimForTests = (_, _) =>
            {
                entered.Set();
                release.Wait(TimeSpan.FromSeconds(5));
            }
        };

        engine.StartCaptureForTests(rec, new TestTray());
        Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));
        var stopTask = Task.Run(() => engine.Stop(rec.Id, "test_stop_before_claim"));
        Assert.True(SpinWait.SpinUntil(() => rec.State == RecState.stopping, TimeSpan.FromSeconds(5)));
        release.Set();
        await stopTask.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(RecState.cancelled, rec.State);
        Assert.Equal(0, runner.Calls);
        Assert.Equal(0, engine.ActiveScreenshotSeriesOperationCountForTests);
    }

    [Fact]
    public async Task ScreenshotSeries_StopAfterFrameClaimStillFinalizesAndRetiresWorker()
    {
        var rec = CreateSeriesRecording(1);
        var entered = new ManualResetEventSlim(false);
        var release = new ManualResetEventSlim(false);
        var runner = new FakePngRunner(32, 32);
        using var engine = new RecordingEngine(new AuditLogger())
        {
            ScreenshotFrameRunnerFactoryForTests = _ => runner,
            BeforeScreenshotFrameRunnerForTests = (_, _) =>
            {
                entered.Set();
                release.Wait(TimeSpan.FromSeconds(5));
            }
        };

        engine.StartCaptureForTests(rec, new TestTray());
        Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));
        var stopTask = Task.Run(() => engine.Stop(rec.Id, "test_stop_during_frame"));
        Assert.True(SpinWait.SpinUntil(() => rec.State == RecState.stopping, TimeSpan.FromSeconds(5)));
        release.Set();
        await stopTask.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(RecState.cancelled, rec.State);
        Assert.Equal(0, engine.ActiveScreenshotSeriesOperationCountForTests);
    }

    [Fact]
    public async Task ScreenshotSeries_StopDuringRunnerObservesCancellationAndRetiresWorker()
    {
        var rec = CreateSeriesRecording(1);
        var runner = new CancellationAwareRunner();
        using var engine = new RecordingEngine(new AuditLogger())
        {
            ScreenshotFrameRunnerFactoryForTests = _ => runner
        };

        engine.StartCaptureForTests(rec, new TestTray());
        Assert.True(runner.Entered.Wait(TimeSpan.FromSeconds(5)));
        await Task.Run(() => engine.Stop(rec.Id, "stop_during_runner")).WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(runner.CancellationObserved);
        Assert.Equal(RecState.cancelled, rec.State);
        Assert.Equal(0, engine.ActiveScreenshotSeriesOperationCountForTests);
        Assert.Null(rec.ScreenshotSeries!.FinalDirectory);
    }

    [Fact]
    public async Task ScreenshotSeries_StopAfterFirstFramePublishesCancelledPartialSeries()
    {
        var rec = CreateSeriesRecording(2);
        long ticks = 0;
        var entered = new ManualResetEventSlim(false);
        var release = new ManualResetEventSlim(false);
        var runner = new ClockedPngRunner(32, 32, _ => { });
        using var engine = new RecordingEngine(new AuditLogger())
        {
            MonotonicFrequencyForTests = 1000,
            MonotonicTimestampProviderForTests = () => ticks,
            ScreenshotDelaySchedulerForTests = (due, _) =>
            {
                ticks = Math.Max(ticks, due);
                return Task.CompletedTask;
            },
            BeforeScreenshotFrameStartClaimForTests = (recording, index) =>
            {
                if (index == 2)
                {
                    entered.Set();
                    release.Wait(TimeSpan.FromSeconds(5));
                }
            },
            ScreenshotFrameRunnerFactoryForTests = _ => runner
        };

        engine.StartCaptureForTests(rec, new TestTray());
        Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));
        var stopTask = Task.Run(() => engine.Stop(rec.Id, "partial_stop"));
        Assert.True(SpinWait.SpinUntil(() => rec.State == RecState.stopping, TimeSpan.FromSeconds(5)));
        release.Set();
        await stopTask.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(SpinWait.SpinUntil(() => rec.IsFinalized && engine.ActiveScreenshotSeriesOperationCountForTests == 0, TimeSpan.FromSeconds(5)));

        Assert.Equal(RecState.cancelled, rec.State);
        Assert.Equal(1, runner.Calls);
        Assert.Single(rec.ScreenshotSeries!.Frames);
        Assert.NotNull(rec.ScreenshotSeries.FinalDirectory);
        Assert.Null(rec.ScreenshotSeries.StagingDirectory);
        var manifest = JsonNode.Parse(File.ReadAllText(Path.Combine(rec.ScreenshotSeries.FinalDirectory!, "series.json")))!;
        Assert.Equal("cancelled", manifest["status"]!.GetValue<string>());
        Assert.Equal(1, manifest["series"]!["captured_frame_count"]!.GetValue<int>());
        Assert.Equal(0, engine.ActiveScreenshotSeriesOperationCountForTests);
    }

    [Fact]
    public async Task ScreenshotSeries_FrameSuccessAndStopRaceHasOneTerminalNoDoublePublish()
    {
        var rec = CreateSeriesRecording(1);
        var entered = new ManualResetEventSlim(false);
        var release = new ManualResetEventSlim(false);
        using var engine = new RecordingEngine(new AuditLogger())
        {
            BeforeScreenshotFrameRunnerForTests = (_, _) =>
            {
                entered.Set();
                release.Wait(TimeSpan.FromSeconds(5));
            },
            ScreenshotFrameRunnerFactoryForTests = _ => new IgnoringCancellationPngRunner()
        };

        engine.StartCaptureForTests(rec, new TestTray());
        Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));
        var stopTask = Task.Run(() => engine.Stop(rec.Id, "success_stop_race"));
        Assert.True(SpinWait.SpinUntil(() => rec.State == RecState.stopping, TimeSpan.FromSeconds(5)));
        release.Set();
        await stopTask.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(SpinWait.SpinUntil(() => rec.IsFinalized && engine.ActiveScreenshotSeriesOperationCountForTests == 0, TimeSpan.FromSeconds(5)));

        Assert.True(rec.IsFinalized);
        Assert.Equal(RecState.cancelled, rec.State);
        Assert.Null(rec.ScreenshotSeries!.FinalDirectory);
        Assert.Equal(0, engine.ActiveScreenshotSeriesOperationCountForTests);
    }

    [Fact]
    public async Task ScreenshotSeries_FrameFailureAndStopRaceLeavesNoStagingOrOperation()
    {
        var rec = CreateSeriesRecording(1);
        var entered = new ManualResetEventSlim(false);
        var release = new ManualResetEventSlim(false);
        using var engine = new RecordingEngine(new AuditLogger())
        {
            BeforeScreenshotFrameRunnerForTests = (_, _) =>
            {
                entered.Set();
                release.Wait(TimeSpan.FromSeconds(5));
            },
            ScreenshotFrameRunnerFactoryForTests = _ => new ScriptedRunner("exit")
        };

        engine.StartCaptureForTests(rec, new TestTray());
        Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));
        var stopTask = Task.Run(() => engine.Stop(rec.Id, "failure_stop_race"));
        Assert.True(SpinWait.SpinUntil(() => rec.State == RecState.stopping, TimeSpan.FromSeconds(5)));
        release.Set();
        await stopTask.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(SpinWait.SpinUntil(() => rec.IsFinalized && engine.ActiveScreenshotSeriesOperationCountForTests == 0, TimeSpan.FromSeconds(5)));

        Assert.True(rec.IsFinalized);
        Assert.Equal(RecState.failed, rec.State);
        Assert.Null(rec.ScreenshotSeries!.FinalDirectory);
        Assert.Null(rec.ScreenshotSeries.StagingDirectory);
        Assert.Equal(0, engine.ActiveScreenshotSeriesOperationCountForTests);
    }

    [Fact]
    public void ScreenshotSeries_RepeatedStopIsIdempotentAfterTerminalState()
    {
        var rec = CreateSeriesRecording(1);
        using var engine = new RecordingEngine(new AuditLogger())
        {
            ScreenshotFrameRunnerFactoryForTests = _ => new FakePngRunner(32, 32)
        };

        engine.StartCaptureForTests(rec, new TestTray());
        Assert.True(SpinWait.SpinUntil(() => rec.State is RecState.completed or RecState.failed, TimeSpan.FromSeconds(5)));
        Assert.True(SpinWait.SpinUntil(() => engine.ActiveScreenshotSeriesOperationCountForTests == 0, TimeSpan.FromSeconds(5)));
        var first = engine.Stop(rec.Id, "repeat_stop");
        var second = engine.Stop(rec.Id, "repeat_stop_again");

        Assert.Equal(RecState.completed, rec.State);
        Assert.Equal(0, engine.ActiveScreenshotSeriesOperationCountForTests);
        Assert.Equal(JsonSerializer.Serialize(first), JsonSerializer.Serialize(second));
    }

    [Fact]
    public async Task ScreenshotSeries_DisposeDuringCountdownRetiresOperation()
    {
        var rec = CreateSeriesRecording(1);
        rec.CountdownSeconds = 3;
        rec.Config.CountdownSeconds = 3;
        var entered = new ManualResetEventSlim(false);
        var release = new ManualResetEventSlim(false);
        using var engine = new RecordingEngine(new AuditLogger())
        {
            BeforeScreenshotCountdownStepForTests = (_, _) =>
            {
                entered.Set();
                release.Wait(TimeSpan.FromSeconds(5));
            },
            ScreenshotFrameRunnerFactoryForTests = _ => new FakePngRunner(32, 32)
        };

        engine.StartCaptureForTests(rec, new TestTray());
        Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));
        var disposeTask = Task.Run(engine.Dispose);
        Assert.True(SpinWait.SpinUntil(() => rec.State == RecState.stopping, TimeSpan.FromSeconds(5)));
        release.Set();
        await disposeTask.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(0, engine.ActiveScreenshotSeriesOperationCountForTests);
        Assert.True(rec.State is RecState.cancelled or RecState.failed);
    }

    [Fact]
    public void ScreenshotSeries_StagingDirectoriesAreUniqueAndNeverPublishedAsOutput()
    {
        var first = ScreenshotSeriesArtifacts.CreateStagingDirectory("same-recording");
        var second = ScreenshotSeriesArtifacts.CreateStagingDirectory("same-recording");
        try
        {
            Assert.NotEqual(first, second);
            Assert.Contains(Path.Combine("temp", "screenshot-series"), first, StringComparison.OrdinalIgnoreCase);
            Assert.Contains(Path.Combine("temp", "screenshot-series"), second, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            ScreenshotSeriesArtifacts.DeleteStaging(new ScreenshotSeriesRuntime { StagingDirectory = first });
            ScreenshotSeriesArtifacts.DeleteStaging(new ScreenshotSeriesRuntime { StagingDirectory = second });
        }
    }

    [Theory]
    [InlineData("../escape")]
    [InlineData("safe/name")]
    [InlineData("C:\\escape")]
    [InlineData(".")]
    [InlineData("..")]
    public void ScreenshotSeries_OutputTemplateCannotEscapeConfirmedDirectory(string template)
    {
        var node = JsonNode.Parse($"{{\"mode\":\"screenshot_series\",\"interval_ms\":1000,\"max_count\":1,\"source\":{{\"type\":\"display\",\"display_id\":\"display_1\"}},\"output\":{{\"directory\":\"{_dataDir.Replace("\\", "\\\\", StringComparison.Ordinal)}\",\"filename_template\":\"{template.Replace("\\", "\\\\", StringComparison.Ordinal)}\"}}}}")!;

        var ex = Assert.Throws<ApiException>(() => ConfigParser.Build(node, "test", out _, new CountingMicrophoneProvider(() => { })));

        Assert.Equal("INVALID_ARGUMENT", ex.Code);
    }

    [Fact]
    public void ScreenshotSeries_ManifestUsesApprovedPlanAndRealFrameHash()
    {
        var rec = CreateSeriesRecording(1);
        var runner = new FakePngRunner(32, 32);
        using var engine = new RecordingEngine(new AuditLogger())
        {
            ScreenshotFrameRunnerFactoryForTests = _ => runner
        };
        engine.StartCaptureForTests(rec, new TestTray());
        Assert.True(SpinWait.SpinUntil(() => rec.State is RecState.completed or RecState.failed, TimeSpan.FromSeconds(5)));

        Assert.Equal(RecState.completed, rec.State);
        var directory = rec.ScreenshotSeries!.FinalDirectory!;
        var framePath = Path.Combine(directory, "frame-0001.png");
        var manifestText = File.ReadAllText(Path.Combine(directory, "series.json"));
        var manifest = JsonNode.Parse(manifestText)!;
        Assert.Equal("ffmpeg-single-frame", manifest["backend"]!.GetValue<string>());
        Assert.Equal("region_rectangle", manifest["source"]!["capture_semantics"]!.GetValue<string>());
        Assert.Equal("region_rectangle", manifest["source"]!["preview_semantics"]!.GetValue<string>());
        Assert.Equal(Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(framePath))).ToLowerInvariant(),
            manifest["frames"]![0]!["sha256"]!.GetValue<string>());
        Assert.NotNull(manifest["frames"]![0]!["capture_started_at"]);
        Assert.NotNull(manifest["frames"]![0]!["completed_at"]);
        Assert.NotNull(manifest["frames"]![0]!["capture_duration_ms"]);
        Assert.True(manifest["frames"]![0]!["capture_duration_ms"]!.GetValue<long>() >= 0);
        Assert.True(manifestText.IndexOf("\"lateness_ms\"", StringComparison.Ordinal)
            < manifestText.IndexOf("\"capture_duration_ms\"", StringComparison.Ordinal));
        Assert.DoesNotContain(_dataDir, manifestText, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("missing", "invalid_png_frame")]
    [InlineData("wrong_size", "invalid_png_frame")]
    [InlineData("timeout", "frame_timeout")]
    [InlineData("exit", "frame_capture_failed")]
    public void ScreenshotSeries_InvalidFrameOutcomesNeverPublishOutput(string behavior, string expectedError)
    {
        var rec = CreateSeriesRecording(1);
        var runner = new ScriptedRunner(behavior);
        using var engine = new RecordingEngine(new AuditLogger())
        {
            ScreenshotFrameRunnerFactoryForTests = _ => runner
        };

        engine.StartCaptureForTests(rec, new TestTray());
        Assert.True(SpinWait.SpinUntil(() => rec.State is RecState.completed or RecState.failed, TimeSpan.FromSeconds(5)));

        Assert.Equal(RecState.failed, rec.State);
        Assert.Equal(expectedError, rec.ScreenshotSeries!.ErrorCode);
        Assert.Null(rec.ScreenshotSeries.FinalDirectory);
        Assert.False(Directory.Exists(rec.OutputPath));
        Assert.True(SpinWait.SpinUntil(() => engine.ActiveScreenshotSeriesOperationCountForTests == 0, TimeSpan.FromSeconds(5)));
        Assert.Equal(0, engine.ActiveScreenshotSeriesOperationCountForTests);
    }

    [Fact]
    public void ScreenshotSeries_PublishConflictPoliciesCoverRenameFailOverwriteAndFileCollision()
    {
        foreach (var policy in new[] { "rename", "fail", "overwrite", "file" })
        {
            var desired = Path.Combine(_dataDir, "conflict-" + policy);
            var staging = ScreenshotSeriesArtifacts.CreateStagingDirectory("conflict-" + policy);
            var runtime = new ScreenshotSeriesRuntime
            {
                OutputDirectory = desired,
                StagingDirectory = staging
            };
            var rec = CreateSeriesRecording(1);

            try
            {
                if (policy == "file")
                    File.WriteAllText(desired, "existing");
                else
                    Directory.CreateDirectory(desired);

                if (policy == "rename" || policy == "file")
                {
                    var final = ScreenshotSeriesArtifacts.Publish(rec, runtime, "rename");
                    Assert.Equal(desired + "-1", final);
                    Assert.True(Directory.Exists(final));
                }
                else
                {
                    var ex = Assert.Throws<ApiException>(() => ScreenshotSeriesArtifacts.Publish(rec, runtime, policy));
                    Assert.Equal(policy == "fail" ? "OUTPUT_PATH_INVALID" : "PERMISSION_DENIED", ex.Code);
                }
            }
            finally
            {
                ScreenshotSeriesArtifacts.DeleteStaging(runtime);
            }
        }
    }

    [Fact]
    public void ScreenshotSeries_StatusDoesNotExposeStagingPath()
    {
        var rec = CreateSeriesRecording(1);
        var staging = ScreenshotSeriesArtifacts.CreateStagingDirectory(rec.Id);
        rec.ScreenshotSeries = new ScreenshotSeriesRuntime
        {
            OutputDirectory = rec.OutputPath,
            StagingDirectory = staging,
            IntervalMs = 1000,
            MaxCount = 1,
            PlannedFrameCount = 1
        };
        using var engine = new RecordingEngine(new AuditLogger());
        engine._recs[rec.Id] = rec;

        try
        {
            var json = JsonSerializer.Serialize(engine.GetStatus(rec.Id));
            Assert.DoesNotContain(staging, json, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("\"staging\":true", json, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            ScreenshotSeriesArtifacts.DeleteStaging(rec.ScreenshotSeries);
        }
    }

    [Fact]
    public async Task ScreenshotSeries_DisposeDuringWorkerRetiresOperation()
    {
        var rec = CreateSeriesRecording(1);
        var entered = new ManualResetEventSlim(false);
        var release = new ManualResetEventSlim(false);
        using var engine = new RecordingEngine(new AuditLogger())
        {
            ScreenshotFrameRunnerFactoryForTests = _ => new FakePngRunner(32, 32),
            BeforeScreenshotFrameRunnerForTests = (_, _) =>
            {
                entered.Set();
                release.Wait(TimeSpan.FromSeconds(5));
            }
        };

        engine.StartCaptureForTests(rec, new TestTray());
        Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));
        var disposeTask = Task.Run(engine.Dispose);
        Assert.True(SpinWait.SpinUntil(() => rec.State == RecState.stopping, TimeSpan.FromSeconds(5)));
        release.Set();
        await disposeTask.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(0, engine.ActiveScreenshotSeriesOperationCountForTests);
        Assert.True(rec.State is RecState.cancelled or RecState.failed or RecState.completed);
    }

    [Fact]
    public async Task ScreenshotSeries_DisposeBeforeFrameClaimDoesNotLaunchRunner()
    {
        var rec = CreateSeriesRecording(1);
        var entered = new ManualResetEventSlim(false);
        var release = new ManualResetEventSlim(false);
        var runner = new FakePngRunner(32, 32);
        using var engine = new RecordingEngine(new AuditLogger())
        {
            ScreenshotFrameRunnerFactoryForTests = _ => runner,
            BeforeScreenshotFrameStartClaimForTests = (_, _) =>
            {
                entered.Set();
                release.Wait(TimeSpan.FromSeconds(5));
            }
        };

        engine.StartCaptureForTests(rec, new TestTray());
        Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));
        var disposeTask = Task.Run(engine.Dispose);
        Assert.True(SpinWait.SpinUntil(() => rec.State == RecState.stopping, TimeSpan.FromSeconds(5)));
        release.Set();
        await disposeTask.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(0, runner.Calls);
        Assert.Equal(0, engine.ActiveScreenshotSeriesOperationCountForTests);
        Assert.Equal(RecState.cancelled, rec.State);
    }

    private Recording CreateSeriesRecording(int count, string sourceKind = "region")
    {
        var output = Path.Combine(_dataDir, "output-" + Guid.NewGuid().ToString("N"));
        return new Recording
        {
            SourceType = sourceKind,
            SourceTitle = "test " + sourceKind,
            OutputPath = output,
            Config = new CaptureConfig
            {
                Mode = ScreenshotSeriesConfig.ModeName,
                ScreenshotSeries = new ScreenshotSeriesConfig
                {
                    IntervalMs = 1000,
                    MaxCount = count,
                    PlannedFrameCount = count
                },
                SourceKind = sourceKind,
                Bounds = (0, 0, 32, 32),
                OutputPath = output,
                CountdownSeconds = 0
            }
        };
    }

    private Recording CreateDurationRecording(int durationSeconds)
    {
        var intervalMs = ScreenshotSeriesConfig.MinIntervalMs;
        var output = Path.Combine(_dataDir, "duration-output-" + Guid.NewGuid().ToString("N"));
        return new Recording
        {
            SourceType = "region",
            SourceTitle = "test duration region",
            OutputPath = output,
            Config = new CaptureConfig
            {
                Mode = ScreenshotSeriesConfig.ModeName,
                ScreenshotSeries = new ScreenshotSeriesConfig
                {
                    IntervalMs = intervalMs,
                    MaxDurationSeconds = durationSeconds,
                    PlannedFrameCount = ScreenshotSeriesConfig.CountForDuration(durationSeconds, intervalMs)
                },
                SourceKind = "region",
                Bounds = (0, 0, 32, 32),
                OutputPath = output,
                CountdownSeconds = 0
            }
        };
    }

    [Fact]
    public void StartSeries_UsesOneRunnerPerFramePublishesManifestAndNeverUsesVideoBackend()
    {
        var audit = new AuditLogger();
        var runner = new FakePngRunner(32, 32);
        var tray = new TestTray();
        var engine = new RecordingEngine(audit)
        {
            ScreenshotFrameRunnerFactoryForTests = _ => runner,
            CountdownInterval = TimeSpan.FromMilliseconds(1)
        };

        var finalDirectory = Path.Combine(_dataDir, "series-output");
        var series = new ScreenshotSeriesConfig
        {
            IntervalMs = 1_000,
            MaxCount = 2,
            PlannedFrameCount = 2
        };
        var rec = new Recording
        {
            SourceType = "region",
            SourceTitle = "test region",
            OutputPath = finalDirectory,
            Config = new CaptureConfig
            {
                Mode = ScreenshotSeriesConfig.ModeName,
                ScreenshotSeries = series,
                SourceKind = "region",
                Bounds = (0, 0, 32, 32),
                OutputPath = finalDirectory,
                CountdownSeconds = 0
            }
        };

        engine.StartCaptureForTests(rec, tray);
        Assert.True(SpinWait.SpinUntil(() => rec.State == RecState.completed || rec.State == RecState.failed, TimeSpan.FromSeconds(5)));

        Assert.Equal(RecState.completed, rec.State);
        Assert.Equal(2, runner.Calls);
        Assert.NotNull(rec.ScreenshotSeries?.FinalDirectory);
        var published = rec.ScreenshotSeries!.FinalDirectory!;
        Assert.True(File.Exists(Path.Combine(published, "frame-0001.png")));
        Assert.True(File.Exists(Path.Combine(published, "frame-0002.png")));
        var manifestBytes = File.ReadAllBytes(Path.Combine(published, "series.json"));
        Assert.False(manifestBytes.Length >= 3 && manifestBytes[0] == 0xEF && manifestBytes[1] == 0xBB && manifestBytes[2] == 0xBF);
        var manifest = Encoding.UTF8.GetString(manifestBytes);
        Assert.Contains("\"schema_version\": 1", manifest);
        Assert.Contains("\"mode\": \"screenshot_series\"", manifest);
        Assert.Equal(0, engine.ActiveScreenshotSeriesOperationCountForTests);
    }

    [Fact]
    public void AddMark_ScreenshotSeriesIsExplicitlyNotApplicable()
    {
        var rec = new Recording
        {
            State = RecState.recording,
            Config = new CaptureConfig
            {
                Mode = ScreenshotSeriesConfig.ModeName,
                ScreenshotSeries = new ScreenshotSeriesConfig { IntervalMs = 1000, MaxCount = 1, PlannedFrameCount = 1 }
            }
        };
        var engine = new RecordingEngine(new AuditLogger());
        engine._recs[rec.Id] = rec;

        var ex = Assert.Throws<ApiException>(() => engine.AddMark(rec.Id, "not applicable"));
        Assert.Equal("UNSUPPORTED_FEATURE", ex.Code);
    }

    private sealed class FakePngRunner : IScreenshotFrameRunner
    {
        private readonly int _width;
        private readonly int _height;
        private readonly int _delayMs;
        public int Calls;
        public int MaxInFlight;
        private int _inFlight;

        public FakePngRunner(int width, int height, int delayMs = 0)
        {
            _width = width;
            _height = height;
            _delayMs = delayMs;
        }

        public async Task<ScreenshotFrameResult> CaptureAsync(ScreenshotFrameRequest request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref Calls);
            var inFlight = Interlocked.Increment(ref _inFlight);
            while (true)
            {
                var currentMax = MaxInFlight;
                if (inFlight <= currentMax || Interlocked.CompareExchange(ref MaxInFlight, inFlight, currentMax) == currentMax)
                    break;
            }

            var started = DateTime.UtcNow;
            try
            {
                if (_delayMs > 0)
                    await Task.Delay(_delayMs, cancellationToken);
                var bytes = BuildPng(_width, _height);
                File.WriteAllBytes(request.TempPath, bytes);
                return new ScreenshotFrameResult(true, "", _width, _height, bytes.Length, started, DateTime.UtcNow);
            }
            finally
            {
                Interlocked.Decrement(ref _inFlight);
            }
        }

        public static byte[] BuildPng(int width, int height)
        {
            var raw = new byte[checked((width * 4 + 1) * height)];
            for (int row = 0; row < height; row++)
                raw[row * (width * 4 + 1)] = 0;

            byte[] compressed;
            using (var output = new MemoryStream())
            {
                using (var zlib = new ZLibStream(output, CompressionMode.Compress, leaveOpen: true))
                    zlib.Write(raw);
                compressed = output.ToArray();
            }

            using var png = new MemoryStream();
            png.Write(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A });
            var ihdr = new byte[13];
            WriteInt(ihdr, 0, width);
            WriteInt(ihdr, 4, height);
            ihdr[8] = 8;
            ihdr[9] = 6;
            WriteChunk(png, "IHDR", ihdr);
            WriteChunk(png, "IDAT", compressed);
            WriteChunk(png, "IEND", Array.Empty<byte>());
            return png.ToArray();
        }

        private static void WriteChunk(Stream stream, string type, byte[] data)
        {
            var typeBytes = Encoding.ASCII.GetBytes(type);
            WriteInt(stream, data.Length);
            stream.Write(typeBytes);
            stream.Write(data);
            var crcInput = new byte[typeBytes.Length + data.Length];
            Buffer.BlockCopy(typeBytes, 0, crcInput, 0, typeBytes.Length);
            Buffer.BlockCopy(data, 0, crcInput, typeBytes.Length, data.Length);
            WriteInt(stream, unchecked((int)Crc32(crcInput)));
        }

        private static void WriteInt(Stream stream, int value)
        {
            stream.WriteByte((byte)(value >> 24));
            stream.WriteByte((byte)(value >> 16));
            stream.WriteByte((byte)(value >> 8));
            stream.WriteByte((byte)value);
        }

        private static void WriteInt(byte[] bytes, int offset, int value)
        {
            bytes[offset] = (byte)(value >> 24);
            bytes[offset + 1] = (byte)(value >> 16);
            bytes[offset + 2] = (byte)(value >> 8);
            bytes[offset + 3] = (byte)value;
        }

        private static uint Crc32(byte[] bytes)
        {
            uint crc = 0xffffffffu;
            foreach (byte value in bytes)
            {
                crc ^= value;
                for (int bit = 0; bit < 8; bit++)
                    crc = (crc >> 1) ^ (0xedb88320u & (uint)-(int)(crc & 1));
            }
            return crc ^ 0xffffffffu;
        }
    }

    private sealed class ScriptedRunner : IScreenshotFrameRunner
    {
        private readonly string _behavior;
        public ScriptedRunner(string behavior) => _behavior = behavior;

        public Task<ScreenshotFrameResult> CaptureAsync(ScreenshotFrameRequest request, CancellationToken cancellationToken)
        {
            var now = DateTime.UtcNow;
            if (_behavior == "timeout")
                return Task.FromResult(new ScreenshotFrameResult(false, "frame_timeout", 0, 0, 0, now, now, -1));
            if (_behavior == "exit")
                return Task.FromResult(new ScreenshotFrameResult(false, "frame_capture_failed", 0, 0, 0, now, now, 1));
            if (_behavior == "wrong_size")
            {
                var bytes = FakePngRunner.BuildPng(16, 16);
                File.WriteAllBytes(request.TempPath, bytes);
                return Task.FromResult(new ScreenshotFrameResult(true, "", 16, 16, bytes.Length, now, now));
            }
            if (_behavior == "missing")
                return Task.FromResult(new ScreenshotFrameResult(true, "", 32, 32, 0, now, now));
            throw new InvalidOperationException("Unknown scripted runner behavior.");
        }
    }

    private sealed class ClockedPngRunner : IScreenshotFrameRunner
    {
        private readonly int _width;
        private readonly int _height;
        private readonly Action<int> _afterCapture;
        private int _inFlight;
        public int Calls;
        public int MaxInFlight;

        public ClockedPngRunner(int width, int height, Action<int> afterCapture)
        {
            _width = width;
            _height = height;
            _afterCapture = afterCapture;
        }

        public Task<ScreenshotFrameResult> CaptureAsync(ScreenshotFrameRequest request, CancellationToken cancellationToken)
        {
            var index = Interlocked.Increment(ref Calls);
            var inFlight = Interlocked.Increment(ref _inFlight);
            while (true)
            {
                var currentMax = MaxInFlight;
                if (inFlight <= currentMax || Interlocked.CompareExchange(ref MaxInFlight, inFlight, currentMax) == currentMax)
                    break;
            }

            var started = DateTime.UtcNow;
            try
            {
                var bytes = FakePngRunner.BuildPng(_width, _height);
                File.WriteAllBytes(request.TempPath, bytes);
                _afterCapture(index);
                var completed = DateTime.UtcNow;
                return Task.FromResult(new ScreenshotFrameResult(true, "", _width, _height, bytes.Length, started, completed));
            }
            finally
            {
                Interlocked.Decrement(ref _inFlight);
            }
        }
    }

    private sealed class CancellationAwareRunner : IScreenshotFrameRunner
    {
        public ManualResetEventSlim Entered { get; } = new(false);
        public bool CancellationObserved { get; private set; }

        public async Task<ScreenshotFrameResult> CaptureAsync(ScreenshotFrameRequest request, CancellationToken cancellationToken)
        {
            Entered.Set();
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                throw new InvalidOperationException("Cancellation was not observed.");
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                CancellationObserved = true;
                throw;
            }
        }
    }

    private sealed class IgnoringCancellationPngRunner : IScreenshotFrameRunner
    {
        public Task<ScreenshotFrameResult> CaptureAsync(ScreenshotFrameRequest request, CancellationToken cancellationToken)
        {
            var now = DateTime.UtcNow;
            var bytes = FakePngRunner.BuildPng(32, 32);
            File.WriteAllBytes(request.TempPath, bytes);
            return Task.FromResult(new ScreenshotFrameResult(true, "", 32, 32, bytes.Length, now, now));
        }
    }

    private sealed class TestTray : ITrayContext
    {
        public string HostMode => "headless";
        public bool SupportsRegionSelectionUi => false;
        public int CountdownUpdates;
        public int SeriesUpdates;
        public void RequestConfirmation(RecordingConfirmationPresentation presentation, Action<ConfirmationDecision> callback) => callback(ConfirmationDecision.Reject());
        public void RequestRegionSelection(int timeoutSeconds, Action<string, int, int, int, int, string, string> callback) => callback("display_unavailable", 0, 0, 0, 0, "", "virtual_screen");
        public void SetRecording(RecordingUiPresentation rec) { }
        public void SetIdle(RecordingUiPresentation rec) { }
        public void SetAllIdle() { }
        public void ShowError(string text) { }
        public void SetCountdown(RecordingUiPresentation rec) => CountdownUpdates++;
        public void SetSeriesProgress(RecordingUiPresentation rec) => SeriesUpdates++;
    }

    private sealed class CountingMicrophoneProvider : IMicrophoneDeviceProvider
    {
        private readonly Action _onCall;
        public CountingMicrophoneProvider(Action onCall) => _onCall = onCall;
        public Task<IReadOnlyList<MicrophoneDeviceInfo>> GetDevicesAsync(CancellationToken cancellationToken)
        {
            _onCall();
            return Task.FromResult<IReadOnlyList<MicrophoneDeviceInfo>>(Array.Empty<MicrophoneDeviceInfo>());
        }
    }
}
