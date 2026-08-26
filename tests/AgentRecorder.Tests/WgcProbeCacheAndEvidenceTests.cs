using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.ExceptionServices;
using System.Text.Json;
using AgentRecorder.App;
using AgentRecorder.Capture;
using AgentRecorder.Core;
using AgentRecorder.Infrastructure;
using AgentRecorder.Logging;
using Xunit;

namespace AgentRecorder.Tests;

[Collection("NonParallel-WindowBackend")]
public sealed class WgcContinuousProbeCacheAndEvidenceTests
{
    private static readonly (int x, int y, int w, int h) PrimaryBounds = (10, -20, 1920, 1080);
    private static readonly (int x, int y, int w, int h) SecondaryBounds = (1930, 0, 1280, 720);

    [Fact]
    public void SuccessfulProbe_UsesTtlCache_AndReevaluatesBoundsWithoutRerun()
    {
        var state = new ProbeState();
        var runner = new QueueProcessRunner(
            HealthyResults(PrimaryBounds),
            HealthyResults(PrimaryBounds));
        var probe = CreateProbe(state, runner, TimeSpan.FromMilliseconds(30));

        var first = probe.Check(Config(PrimaryBounds));
        var mismatch = probe.Check(Config(SecondaryBounds));
        var second = probe.Check(Config(PrimaryBounds));

        Assert.True(first.Available);
        Assert.Equal("fresh_probe", first.AvailabilitySource);
        Assert.False(mismatch.Available);
        Assert.Equal("probe_bounds_mismatch", mismatch.ReasonCode);
        Assert.Equal("cache_hit", mismatch.AvailabilitySource);
        Assert.True(second.Available);
        Assert.Equal("cache_hit", second.AvailabilitySource);
        Assert.Equal(2, runner.CallCount);

        state.Now += 31;
        var afterTtl = probe.Check(Config(PrimaryBounds));

        Assert.True(afterTtl.Available);
        Assert.Equal("fresh_probe", afterTtl.AvailabilitySource);
        Assert.Equal(4, runner.CallCount);
    }

    [Fact]
    public void HelperPathAndFileIdentityChanges_ForceFreshProbe()
    {
        var state = new ProbeState();
        var runner = new QueueProcessRunner(
            HealthyResults(PrimaryBounds),
            HealthyResults(PrimaryBounds),
            HealthyResults(PrimaryBounds));
        var probe = CreateProbe(state, runner, TimeSpan.FromMinutes(1));

        Assert.True(probe.Check(Config(PrimaryBounds)).Available);

        state.HelperPath = "second-helper.exe";
        state.FullPath = "C:\\wgc-probe-tests\\second-helper.exe";
        Assert.True(probe.Check(Config(PrimaryBounds)).Available);

        state.Length++;
        Assert.True(probe.Check(Config(PrimaryBounds)).Available);

        Assert.Equal(6, runner.CallCount);
    }

    [Fact]
    public void HelperPathCaseOnlyChange_ReusesWindowsIdentityCache()
    {
        var state = new ProbeState();
        var runner = new QueueProcessRunner(HealthyResults(PrimaryBounds));
        var probe = CreateProbe(state, runner, TimeSpan.FromMinutes(1));

        var first = probe.Check(Config(PrimaryBounds));
        state.HelperPath = state.HelperPath.ToUpperInvariant();
        state.FullPath = state.FullPath.ToUpperInvariant();
        var second = probe.Check(Config(PrimaryBounds));

        Assert.True(first.Available);
        Assert.True(second.Available);
        Assert.Equal("cache_hit", second.AvailabilitySource);
        Assert.Equal(2, runner.CallCount);
    }

    [Fact]
    public void FailureIsNotCached_AndDoesNotReplaceSuccessForAnotherIdentity()
    {
        var state = new ProbeState();
        var runner = new QueueProcessRunner(
            HealthyResults(PrimaryBounds),
            new[] { new WgcHelperProcessResult { ExitCode = 1 } },
            HealthyResults(PrimaryBounds));
        var probe = CreateProbe(state, runner, TimeSpan.FromMilliseconds(1));

        var success = probe.Check(Config(PrimaryBounds));
        Assert.True(success.Available);

        state.HelperPath = "failure-helper.exe";
        state.FullPath = "C:\\wgc-probe-tests\\failure-helper.exe";
        var failure = probe.Check(Config(PrimaryBounds));
        Assert.False(failure.Available);
        Assert.Equal("version_nonzero_exit", failure.ReasonCode);

        state.HelperPath = "fake-helper.exe";
        state.FullPath = "C:\\wgc-probe-tests\\fake-helper.exe";
        var cachedSuccess = probe.Check(Config(PrimaryBounds));

        Assert.True(cachedSuccess.Available);
        Assert.Equal("cache_hit", cachedSuccess.AvailabilitySource);
        Assert.Equal(3, runner.CallCount);

        var retry = new QueueProcessRunner(
            new[] { new WgcHelperProcessResult { ExitCode = 1 } },
            HealthyResults(PrimaryBounds));
        var retryProbe = CreateProbe(new ProbeState(), retry, TimeSpan.FromMinutes(1));
        Assert.False(retryProbe.Check(Config(PrimaryBounds)).Available);
        Assert.True(retryProbe.Check(Config(PrimaryBounds)).Available);
        Assert.Equal(3, retry.CallCount);
    }

    [Fact]
    public async Task ConcurrentChecks_ShareOneFreshProbe_AndMarkWaitersSingleFlight()
    {
        const int count = 16;
        var state = new ProbeState { IdentityTarget = count };
        var runner = new BlockingProcessRunner(HealthyResults(PrimaryBounds));
        var probe = CreateProbe(state, runner, TimeSpan.FromMinutes(1));
        var start = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        int ready = 0;

        var tasks = Enumerable.Range(0, count)
            .Select(_ => Task.Run(async () =>
            {
                if (Interlocked.Increment(ref ready) == count)
                    start.TrySetResult(true);
                await start.Task.ConfigureAwait(false);
                return probe.Check(Config(PrimaryBounds));
            }))
            .ToArray();

        await state.IdentityTargetReached.Task.WaitAsync(TimeSpan.FromSeconds(3));
        await runner.FirstCallEntered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal(1, runner.CallCount);

        runner.Release.TrySetResult(true);
        var results = await Task.WhenAll(tasks).WaitAsync(TimeSpan.FromSeconds(3));

        Assert.All(results, result => Assert.True(result.Available));
        Assert.Equal(1, results.Count(result => result.AvailabilitySource == "fresh_probe"));
        Assert.Equal(count - 1, results.Count(result => result.AvailabilitySource == "single_flight"));
        Assert.Equal(2, runner.CallCount);
    }

    [Fact]
    public async Task CancelledWarmupWaiter_DoesNotCancelOwner_AndOwnerPublishesCache()
    {
        var state = new ProbeState { IdentityTarget = 2 };
        var runner = new BlockingProcessRunner(HealthyResults(PrimaryBounds));
        var probe = CreateProbe(state, runner, TimeSpan.FromMinutes(1));
        var ownerTask = probe.WarmupAsync();

        await runner.FirstCallEntered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        using var waiterCts = new CancellationTokenSource();
        var waiterTask = probe.WarmupAsync(waiterCts.Token);
        await state.IdentityTargetReached.Task.WaitAsync(TimeSpan.FromSeconds(3));

        waiterCts.Cancel();
        var waiter = await waiterTask.WaitAsync(TimeSpan.FromSeconds(3));

        Assert.False(waiter.Available);
        Assert.Equal("probe_cancelled", waiter.ReasonCode);
        Assert.Equal("single_flight", waiter.AvailabilitySource);
        Assert.False(runner.FirstCallToken.IsCancellationRequested);

        runner.Release.TrySetResult(true);
        var owner = await ownerTask.WaitAsync(TimeSpan.FromSeconds(3));
        var cached = probe.Check(Config(PrimaryBounds));

        Assert.True(owner.Available);
        Assert.Equal("fresh_probe", owner.AvailabilitySource);
        Assert.True(cached.Available);
        Assert.Equal("cache_hit", cached.AvailabilitySource);
        Assert.Equal(2, runner.CallCount);
    }

    [Theory]
    [InlineData("exception")]
    [InlineData("timeout")]
    public async Task ConcurrentFailure_ReleasesSingleFlight_AndNextCallRetries(string failureKind)
    {
        for (int iteration = 0; iteration < 100; iteration++)
            await AssertJoinedFailureIterationAsync(failureKind, iteration);

        for (int iteration = 0; iteration < 50; iteration++)
            AssertLateCallerCanBecomeFreshOwner(failureKind, iteration);

        // Keep the forced-cleanup regression inside this existing theory so the
        // exact project test count remains stable while exercising both failure
        // kinds across 20 deterministic harness failures.
        if (failureKind == "exception")
        {
            for (int iteration = 0; iteration < 20; iteration++)
            {
                string forcedFailureKind = iteration % 2 == 0 ? "exception" : "timeout";
                var failure = await Assert.ThrowsAsync<InvalidOperationException>(
                    () => AssertJoinedFailureIterationAsync(
                        forcedFailureKind,
                        iteration,
                        forceHarnessFailure: true));

                Assert.Contains("forced harness failure after all callers joined", failure.Message);
            }
        }
    }

    private static async Task AssertJoinedFailureIterationAsync(
        string failureKind,
        int iteration,
        bool forceHarnessFailure = false)
    {
        const int count = 12;
        var state = new ProbeState { IdentityTarget = count };
        var runner = new BlockingFailureProcessRunner(failureKind, HealthyResults(PrimaryBounds));
        var probe = CreateProbe(state, runner, TimeSpan.FromMinutes(1));
        var start = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var allJoined = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseJoined = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        int ready = 0;
        int joined = 0;
        var tasks = new List<Task<WgcContinuousAvailabilityResult>>(count);
        Exception? primaryFailure = null;
        var cleanupFailures = new List<Exception>();

        probe.AfterInflightJoinForTests = () =>
        {
            if (Interlocked.Increment(ref joined) == count)
                allJoined.TrySetResult(true);
            releaseJoined.Task.GetAwaiter().GetResult();
        };

        try
        {
            // Cleanup is armed before the first caller can enter the seam. Every
            // task is recorded as it is created so a partial setup failure still
            // joins all callers that were actually started.
            for (int caller = 0; caller < count; caller++)
            {
                tasks.Add(Task.Run(async () =>
                {
                    if (Interlocked.Increment(ref ready) == count)
                        start.TrySetResult(true);
                    await start.Task.ConfigureAwait(false);
                    return probe.Check(Config(PrimaryBounds));
                }));
            }

            await state.IdentityTargetReached.Task.WaitAsync(TimeSpan.FromSeconds(3));
            await allJoined.Task.WaitAsync(TimeSpan.FromSeconds(3));
            Assert.Equal(count, Volatile.Read(ref joined));
            Assert.Equal(0, runner.CallCount);

            if (forceHarnessFailure)
            {
                // Leave every caller blocked at the join seam. The outer
                // finally must release both the membership seam and runner,
                // then observe all 12 tasks before this primary failure returns.
                throw new InvalidOperationException(
                    $"Iteration {iteration} forced harness failure after all callers joined ({failureKind}).");
            }

            // Every intended caller has already selected the same local Lazy.
            // The owner may now run and fail; no delayed caller can become a new owner.
            releaseJoined.TrySetResult(true);
            await runner.FirstCallEntered.Task.WaitAsync(TimeSpan.FromSeconds(3));
            runner.Release.TrySetResult(true);

            var failures = await Task.WhenAll(tasks).WaitAsync(TimeSpan.FromSeconds(3));
            string expectedReason = failureKind == "exception" ? "version_start_failed" : "version_timeout";
            Assert.All(failures, result =>
            {
                Assert.False(result.Available);
                Assert.Equal(expectedReason, result.ReasonCode);
            });
            Assert.Equal(1, failures.Count(result => result.AvailabilitySource == "fresh_probe"));
            Assert.Equal(count - 1, failures.Count(result => result.AvailabilitySource == "single_flight"));
            Assert.Equal(1, runner.CallCount);

            var retry = probe.Check(Config(PrimaryBounds));
            Assert.True(retry.Available);
            Assert.Equal("fresh_probe", retry.AvailabilitySource);
            Assert.Equal(3, runner.CallCount);
        }
        catch (Exception ex)
        {
            primaryFailure = ex;
        }
        finally
        {
            // Detach first so a late continuation cannot re-arm the seam, then
            // release every gate unconditionally on both success and failure.
            probe.AfterInflightJoinForTests = null;
            releaseJoined.TrySetResult(true);
            runner.Release.TrySetResult(true);

            for (int caller = 0; caller < tasks.Count; caller++)
            {
                try
                {
                    await tasks[caller].WaitAsync(TimeSpan.FromSeconds(3));
                }
                catch (Exception ex)
                {
                    cleanupFailures.Add(new InvalidOperationException(
                        $"Iteration {iteration} ({failureKind}) caller {caller} did not terminate during harness cleanup.",
                        ex));
                }
            }

            if (tasks.Any(task => !task.IsCompleted))
            {
                cleanupFailures.Add(new InvalidOperationException(
                    $"Iteration {iteration} ({failureKind}) left a probe caller incomplete after cleanup."));
            }
        }

        if (primaryFailure is not null && cleanupFailures.Count > 0)
        {
            throw new AggregateException(
                $"Probe harness iteration {iteration} ({failureKind}) failed and cleanup also failed.",
                new[] { primaryFailure }.Concat(cleanupFailures));
        }

        if (primaryFailure is not null)
            ExceptionDispatchInfo.Capture(primaryFailure).Throw();

        if (cleanupFailures.Count > 0)
        {
            throw new AggregateException(
                $"Probe harness cleanup failed for iteration {iteration} ({failureKind}).",
                cleanupFailures);
        }
    }

    private static void AssertLateCallerCanBecomeFreshOwner(string failureKind, int iteration)
    {
        var runner = new BlockingFailureProcessRunner(failureKind, HealthyResults(PrimaryBounds));
        var probe = CreateProbe(new ProbeState(), runner, TimeSpan.FromMinutes(1));
        runner.Release.TrySetResult(true);

        var failure = probe.Check(Config(PrimaryBounds));
        Assert.False(failure.Available);
        Assert.Equal(failureKind == "exception" ? "version_start_failed" : "version_timeout", failure.ReasonCode);

        // This call arrives after the failed Lazy has been removed. It must be
        // allowed to become a fresh owner; failed results must not be cached.
        var retry = probe.Check(Config(PrimaryBounds));
        Assert.True(retry.Available);
        Assert.Equal("fresh_probe", retry.AvailabilitySource);
        Assert.Equal(3, runner.CallCount);
    }

    [Fact]
    public async Task Warmup_UsesOnlyVersionAndProbe_AndPopulatesSelectorCache()
    {
        var state = new ProbeState();
        var runner = new QueueProcessRunner(HealthyResults(PrimaryBounds));
        var probe = CreateProbe(state, runner, TimeSpan.FromMinutes(1));

        var warmup = await probe.WarmupAsync();
        var selected = WithDisplayFlag("wgc-continuous", () =>
            CaptureBackendSelector.SelectWithEvidence(Config(PrimaryBounds), probe));

        Assert.True(warmup.Available);
        Assert.Equal("fresh_probe", warmup.AvailabilitySource);
        Assert.Equal("wgc-continuous", selected.BackendType);
        Assert.Equal("cache_hit", selected.Evidence.AvailabilitySource);
        Assert.Equal(2, runner.CallCount);
        Assert.Equal(new[] { "--version", "--probe" }, runner.Calls.SelectMany(call => call.Arguments).ToArray());
        Assert.All(runner.Calls, call => Assert.DoesNotContain("--start", call.Arguments));
        Assert.All(runner.Calls, call => Assert.DoesNotContain("--stop", call.Arguments));
    }

    [Fact]
    public async Task AppWarmup_IsDisabledWithoutExactFlag_AndDoesNotBlockReadiness()
    {
        var probe = new ControlledWarmupProbe();
        var diagnostics = new List<string>();

        var disabled = WithDisplayFlag("ffmpeg", () =>
            WgcContinuousWarmup.StartIfEnabled(probe, diagnostics.Add));
        await disabled;
        Assert.False(probe.Started.Task.IsCompleted);

        var enabled = WithDisplayFlag("  WGC-CONTINUOUS  ", () =>
            WgcContinuousWarmup.StartIfEnabled(probe, diagnostics.Add));
        await probe.Started.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.False(enabled.IsCompleted);

        probe.Release.TrySetResult(true);
        await enabled.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Contains(diagnostics, message => message.Contains("wgc_probe_warmup_unavailable", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AppWarmup_IsEnabledForWindowExperimentFlag()
    {
        var probe = new ControlledWarmupProbe();
        var diagnostics = new List<string>();

        var enabled = WithWindowFlag("wgc-continuous", () =>
            WgcContinuousWarmup.StartIfEnabled(probe, diagnostics.Add));

        await probe.Started.Task.WaitAsync(TimeSpan.FromSeconds(3));
        probe.Release.TrySetResult(true);
        await enabled.WaitAsync(TimeSpan.FromSeconds(3));
    }

    [Theory]
    [InlineData("fresh_probe", "wgc_probe_success")]
    [InlineData("cache_hit", "wgc_cache_hit")]
    [InlineData("single_flight", "wgc_single_flight")]
    public void Selector_EmitsStableEvidenceForSuccessfulAvailability(string source, string reason)
    {
        var probe = new FixedAvailabilityProbe(
            new WgcContinuousAvailabilityResult(true, "available", source, 7));

        var selection = WithDisplayFlag("wgc-continuous", () =>
            CaptureBackendSelector.SelectWithEvidence(Config(PrimaryBounds), probe));

        Assert.Equal("wgc-continuous", selection.Evidence.RequestedBackend);
        Assert.Equal("wgc-continuous", selection.Evidence.SelectedBackend);
        Assert.Equal(reason, selection.Evidence.SelectionReasonCode);
        Assert.Equal(source, selection.Evidence.AvailabilitySource);
        Assert.Equal(7, selection.Evidence.AvailabilityElapsedMs);
        Assert.False(selection.Evidence.Fallback);
    }

    [Fact]
    public void Selector_Evidence_DistinguishesDisabledIneligibleAndStableFallback()
    {
        var healthyProbe = new FixedAvailabilityProbe(
            new WgcContinuousAvailabilityResult(true, "available", "fresh_probe", 4));

        var disabled = WithDisplayFlag(null, () =>
            CaptureBackendSelector.SelectWithEvidence(Config(PrimaryBounds), healthyProbe));
        Assert.Equal("default", disabled.Evidence.RequestedBackend);
        Assert.Equal("experiment_disabled", disabled.Evidence.SelectionReasonCode);
        Assert.Equal("not_run", disabled.Evidence.AvailabilitySource);
        Assert.False(disabled.Evidence.Fallback);
        Assert.Equal(0, healthyProbe.CallCount);

        var microphoneConfig = Config(PrimaryBounds);
        microphoneConfig.Microphone = true;
        var microphone = WithDisplayFlag("wgc-continuous", () =>
            CaptureBackendSelector.SelectWithEvidence(microphoneConfig, healthyProbe));
        Assert.Equal("microphone_not_eligible", microphone.Evidence.SelectionReasonCode);
        Assert.Equal("not_run", microphone.Evidence.AvailabilitySource);
        Assert.True(microphone.Evidence.Fallback);

        var invalid = Config(PrimaryBounds);
        invalid.Fps = 61;
        var invalidSelection = WithDisplayFlag("wgc-continuous", () =>
            CaptureBackendSelector.SelectWithEvidence(invalid, healthyProbe));
        Assert.Equal("fps_not_eligible", invalidSelection.Evidence.SelectionReasonCode);
        Assert.Equal("not_run", invalidSelection.Evidence.AvailabilitySource);

        var fallback = WithDisplayFlag("wgc-continuous", () =>
            CaptureBackendSelector.SelectWithEvidence(
                Config(PrimaryBounds),
                new FixedAvailabilityProbe(new WgcContinuousAvailabilityResult(
                    false, "probe_timeout", "fresh_probe", -9))));
        Assert.Equal("probe_timeout", fallback.Evidence.SelectionReasonCode);
        Assert.Equal("fresh_probe", fallback.Evidence.AvailabilitySource);
        Assert.Equal(0, fallback.Evidence.AvailabilityElapsedMs);
        Assert.True(fallback.Evidence.Fallback);
        Assert.Equal("ffmpeg", fallback.BackendType);
    }

    [Fact]
    public void RecordingEngine_WritesAuditAndTraceEvidenceBeforeBackendStart()
    {
        string temp = Path.Combine(Path.GetTempPath(), "agent-recorder-wgc-evidence-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);
        string auditPath = Path.Combine(temp, "audit.jsonl");
        string tracePath = Path.Combine(temp, "trace.jsonl");

        try
        {
            var audit = new CapturingAuditLogger(auditPath);
            using var writer = new RollingJsonlWriter(tracePath);
            using var tracer = new RecordingPerformanceTracer(writer);
            var engine = new RecordingEngine(audit, tracer);
            var backend = new RecordingTestBackend();
            var evidence = new CaptureBackendSelectionEvidence(
                "wgc-continuous", "ffmpeg", "probe_timeout", "fresh_probe", 3, true);
            engine.BackendSelectionFactoryForTests = _ =>
                new CaptureBackendSelection(backend, "ffmpeg", evidence);

            var recording = new Recording
            {
                SourceType = "display",
                OutputPath = Path.Combine(temp, "clip.mp4"),
                Config = Config(PrimaryBounds)
            };
            engine.StartCaptureForTests(recording, new NoOpTray(), "trace_wgc_evidence");
            tracer.Flush();

            var auditEvent = Assert.Single(audit.Events, item => item.Event == "recording.backend_selected");
            Assert.Equal("wgc-continuous", auditEvent.Payload.GetProperty("requested_backend").GetString());
            Assert.Equal("ffmpeg", auditEvent.Payload.GetProperty("selected_backend").GetString());
            Assert.Equal("probe_timeout", auditEvent.Payload.GetProperty("selection_reason_code").GetString());
            Assert.Equal("fresh_probe", auditEvent.Payload.GetProperty("availability_source").GetString());
            Assert.Equal(3, auditEvent.Payload.GetProperty("availability_elapsed_ms").GetInt32());
            Assert.True(auditEvent.Payload.GetProperty("fallback").GetBoolean());
            Assert.DoesNotContain("C:\\", auditEvent.Payload.GetRawText(), StringComparison.OrdinalIgnoreCase);

            var traceEvents = File.ReadAllLines(tracePath)
                .Where(line => !string.IsNullOrWhiteSpace(line))
                .Select(line => JsonDocument.Parse(line))
                .Select(document => document.RootElement.Clone())
                .ToList();
            var names = traceEvents.Select(eventItem => eventItem.GetProperty("event").GetString()).ToList();
            int selectedIndex = names.IndexOf("capture.backend_selected");
            int startIndex = names.IndexOf("capture.start_requested");
            Assert.True(selectedIndex >= 0);
            Assert.True(startIndex > selectedIndex);

            var traceEvent = traceEvents[selectedIndex];
            var data = traceEvent.GetProperty("data");
            Assert.Equal("wgc-continuous", data.GetProperty("requested_backend").GetString());
            Assert.Equal("ffmpeg", data.GetProperty("selected_backend").GetString());
            Assert.Equal("probe_timeout", data.GetProperty("selection_reason_code").GetString());
            Assert.Equal("fresh_probe", data.GetProperty("availability_source").GetString());
            Assert.Equal(3, data.GetProperty("availability_elapsed_ms").GetInt32());
            Assert.True(data.GetProperty("fallback").GetBoolean());
            Assert.DoesNotContain("C:\\", traceEvents[selectedIndex].GetRawText(), StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            try { Directory.Delete(temp, recursive: true); } catch { }
        }
    }

    [Fact]
    public void BackendSelectionAuditFailure_DoesNotBlockApprovedBackendStart()
    {
        string temp = Path.Combine(Path.GetTempPath(), "agent-recorder-wgc-audit-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);

        try
        {
            var audit = new ThrowingBackendSelectionAuditLogger(Path.Combine(temp, "audit.jsonl"));
            var engine = new RecordingEngine(audit);
            var backend = new RecordingTestBackend();
            engine.BackendFactory = _ => (backend, "fake");

            var recording = new Recording
            {
                SourceType = "display",
                OutputPath = Path.Combine(temp, "clip.mp4"),
                Config = Config(PrimaryBounds)
            };

            var exception = Record.Exception(() =>
                engine.StartCaptureForTests(recording, new NoOpTray(), "trace_wgc_audit_failure"));

            Assert.Null(exception);
            Assert.True(backend.Started);
            Assert.Equal("fake", recording.BackendType);
            Assert.Equal(RecState.recording, recording.State);
        }
        finally
        {
            try { Directory.Delete(temp, recursive: true); } catch { }
        }
    }

    private static WgcContinuousAvailabilityProbe CreateProbe(
        ProbeState state,
        IWgcHelperProcessRunner runner,
        TimeSpan ttl) =>
        new(
            () => state.HelperPath,
            runner,
            state.ResolveIdentity,
            () => state.Now,
            timestampFrequency: 1000,
            cacheTtl: ttl);

    private static WgcHelperProcessResult[] HealthyResults(
        (int x, int y, int w, int h) bounds) =>
        new[]
        {
            new WgcHelperProcessResult
            {
                ExitCode = 0,
                StandardOutput = "wgc-native-helper 0.3.0\n"
            },
            new WgcHelperProcessResult
            {
                ExitCode = 0,
                StandardOutput = HealthyOutput(bounds)
            }
        };

    private static string HealthyOutput((int x, int y, int w, int h) bounds) =>
        $"RESULT: OK\nDpiAwareness: per_monitor_v2\nMonitorCount: 1\n" +
        $"Monitor[0]: x={bounds.x} y={bounds.y} width={bounds.w} height={bounds.h} primary=true\n" +
        "WgcSupported: true\nD3d11Initialized: true\nEncoderCreated: true\nWindowCaptureSupported: true\nHardwareH264Available: false\nHardwareH264CandidateCount: 0\n";

    private static CaptureConfig Config((int x, int y, int w, int h) bounds) => new()
    {
        SourceKind = "display",
        CountdownSeconds = 0,
        Bounds = bounds,
        DurationSeconds = 5,
        Fps = 30,
        OutputPath = "C:\\wgc-probe-tests\\clip.mp4"
    };

    private static T WithDisplayFlag<T>(string? value, Func<T> action)
    {
        string? previous = Environment.GetEnvironmentVariable(CaptureBackendSelector.DisplayBackendEnvVar);
        try
        {
            Environment.SetEnvironmentVariable(CaptureBackendSelector.DisplayBackendEnvVar, value);
            return action();
        }
        finally
        {
            Environment.SetEnvironmentVariable(CaptureBackendSelector.DisplayBackendEnvVar, previous);
        }
    }

    private static T WithWindowFlag<T>(string? value, Func<T> action)
    {
        string? previous = Environment.GetEnvironmentVariable(CaptureBackendSelector.WgcEnvVar);
        try
        {
            Environment.SetEnvironmentVariable(CaptureBackendSelector.WgcEnvVar, value);
            return action();
        }
        finally
        {
            Environment.SetEnvironmentVariable(CaptureBackendSelector.WgcEnvVar, previous);
        }
    }

    private sealed class ProbeState
    {
        private int _identityCalls;

        public string HelperPath { get; set; } = "fake-helper.exe";
        public string FullPath { get; set; } = "C:\\wgc-probe-tests\\fake-helper.exe";
        public long Length { get; set; } = 100;
        public DateTime LastWriteTimeUtc { get; set; } = new(2026, 8, 3, 0, 0, 0, DateTimeKind.Utc);
        public long Now { get; set; } = 1000;
        public int IdentityTarget { get; set; }
        public TaskCompletionSource<bool> IdentityTargetReached { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public WgcContinuousAvailabilityProbe.WgcHelperFileIdentity ResolveIdentity(string _)
        {
            int count = Interlocked.Increment(ref _identityCalls);
            if (IdentityTarget > 0 && count >= IdentityTarget)
                IdentityTargetReached.TrySetResult(true);
            return new WgcContinuousAvailabilityProbe.WgcHelperFileIdentity(
                FullPath, Length, LastWriteTimeUtc);
        }
    }

    private sealed class QueueProcessRunner : IWgcHelperProcessRunner
    {
        private readonly ConcurrentQueue<WgcHelperProcessResult> _results = new();

        public QueueProcessRunner(params WgcHelperProcessResult[][] batches)
        {
            foreach (var batch in batches)
            foreach (var result in batch)
                _results.Enqueue(result);
        }

        public ConcurrentQueue<RunnerCall> Calls { get; } = new();
        public int CallCount => Calls.Count;

        public WgcHelperProcessResult Run(
            string fileName,
            IReadOnlyList<string> argumentList,
            int timeoutMs,
            CancellationToken cancellationToken = default)
        {
            Calls.Enqueue(new RunnerCall(fileName, argumentList.ToArray(), timeoutMs));
            return _results.TryDequeue(out var result)
                ? result
                : new WgcHelperProcessResult { ExitCode = 1 };
        }
    }

    private sealed class BlockingProcessRunner : IWgcHelperProcessRunner
    {
        private readonly ConcurrentQueue<WgcHelperProcessResult> _results = new();

        public BlockingProcessRunner(params WgcHelperProcessResult[][] batches)
        {
            foreach (var batch in batches)
            foreach (var result in batch)
                _results.Enqueue(result);
        }

        public TaskCompletionSource<bool> FirstCallEntered { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<bool> Release { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public CancellationToken FirstCallToken { get; private set; }
        public ConcurrentQueue<RunnerCall> Calls { get; } = new();
        public int CallCount => Calls.Count;

        public WgcHelperProcessResult Run(
            string fileName,
            IReadOnlyList<string> argumentList,
            int timeoutMs,
            CancellationToken cancellationToken = default)
        {
            Calls.Enqueue(new RunnerCall(fileName, argumentList.ToArray(), timeoutMs));
            if (Calls.Count == 1)
            {
                FirstCallToken = cancellationToken;
                FirstCallEntered.TrySetResult(true);
                Release.Task.GetAwaiter().GetResult();
            }

            return _results.TryDequeue(out var result)
                ? result
                : new WgcHelperProcessResult { ExitCode = 1 };
        }
    }

    private sealed class BlockingFailureProcessRunner : IWgcHelperProcessRunner
    {
        private readonly string _failureKind;
        private readonly ConcurrentQueue<WgcHelperProcessResult> _results = new();

        public BlockingFailureProcessRunner(string failureKind, params WgcHelperProcessResult[][] batches)
        {
            _failureKind = failureKind;
            foreach (var batch in batches)
            foreach (var result in batch)
                _results.Enqueue(result);
        }

        public TaskCompletionSource<bool> FirstCallEntered { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<bool> Release { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ConcurrentQueue<RunnerCall> Calls { get; } = new();
        public int CallCount => Calls.Count;

        public WgcHelperProcessResult Run(
            string fileName,
            IReadOnlyList<string> argumentList,
            int timeoutMs,
            CancellationToken cancellationToken = default)
        {
            Calls.Enqueue(new RunnerCall(fileName, argumentList.ToArray(), timeoutMs));
            if (Calls.Count == 1)
            {
                FirstCallEntered.TrySetResult(true);
                Release.Task.GetAwaiter().GetResult();
                if (_failureKind == "exception")
                    throw new InvalidOperationException("synthetic helper failure");
                return new WgcHelperProcessResult { ExitCode = -1, TimedOut = true };
            }

            return _results.TryDequeue(out var result)
                ? result
                : new WgcHelperProcessResult { ExitCode = 1 };
        }
    }

    private sealed record RunnerCall(string FileName, string[] Arguments, int TimeoutMs);

    private sealed class FixedAvailabilityProbe : IWgcContinuousAvailabilityProbe
    {
        private readonly WgcContinuousAvailabilityResult _result;

        public FixedAvailabilityProbe(WgcContinuousAvailabilityResult result) => _result = result;

        public int CallCount { get; private set; }

        public WgcContinuousAvailabilityResult Check(CaptureConfig config)
        {
            CallCount++;
            return _result;
        }
    }

    private sealed class ControlledWarmupProbe : IWgcContinuousAvailabilityProbe, IWgcContinuousAvailabilityWarmupProbe
    {
        public TaskCompletionSource<bool> Started { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<bool> Release { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public WgcContinuousAvailabilityResult Check(CaptureConfig config) =>
            new(false, "probe_timeout", "fresh_probe", 2);

        public async Task<WgcContinuousAvailabilityResult> WarmupAsync(CancellationToken cancellationToken = default)
        {
            Started.TrySetResult(true);
            await Release.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            return new WgcContinuousAvailabilityResult(false, "probe_timeout", "fresh_probe", 2);
        }
    }

    private sealed class CapturingAuditLogger : AuditLogger
    {
        public CapturingAuditLogger(string path) : base(path) { }

        public List<(string Event, JsonElement Payload)> Events { get; } = new();

        public override void Log(string evt, object payload)
        {
            using var document = JsonDocument.Parse(JsonSerializer.Serialize(payload));
            Events.Add((evt, document.RootElement.Clone()));
            base.Log(evt, payload);
        }
    }

    private sealed class ThrowingBackendSelectionAuditLogger : AuditLogger
    {
        public ThrowingBackendSelectionAuditLogger(string path) : base(path) { }

        public override void Log(string evt, object payload)
        {
            if (evt == "recording.backend_selected")
                throw new InvalidOperationException("synthetic backend-selection audit failure");
            base.Log(evt, payload);
        }
    }

    private sealed class RecordingTestBackend : ICaptureBackend
    {
        private Action<int, OutputMeta>? _naturalExit;

        public bool Started { get; private set; }

        public void Start(CaptureConfig cfg)
        {
            Started = true;
            cfg.CommandArgs = "synthetic backend";
        }

        public OutputMeta Stop() => new();
        public void OnNaturalExit(Action<int, OutputMeta> callback) => _naturalExit = callback;
        public int ExitCode => 0;
        public void Dispose() => _naturalExit = null;
    }

    private sealed class NoOpTray : ITrayContext
    {
        public string HostMode => "headless";
        public bool SupportsRegionSelectionUi => false;
        public void RequestConfirmation(RecordingConfirmationPresentation presentation, Action<ConfirmationDecision> callback) { }
        public void RequestRegionSelection(int timeoutSeconds, Action<string, int, int, int, int, string, string> callback) { }
        public void SetRecording(RecordingUiPresentation rec) { }
        public void SetIdle(RecordingUiPresentation rec) { }
        public void SetAllIdle() { }
        public void ShowError(string text) { }
    }
}
