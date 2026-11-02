using System.Collections.Concurrent;
using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using AgentRecorder.Capture;
using AgentRecorder.Core;
using Xunit;

namespace AgentRecorder.Tests;

public sealed partial class WindowStorageEngineTests
{
    // Use the engine's lifecycle signal and its one secured snapshot. A terminal
    // start is different from a still-pending deadline; neither is recording.
    private static async Task<string> StorageStartupWait(RecordingEngine engine, Recording rec, int waitMs)
    {
        var result = await Task.Run(() => engine.GetCreationRecordingWait(rec.Id, waitMs));
        var wait = JsonSerializer.SerializeToElement(result).GetProperty("Wait");
        return wait.GetProperty("GoalReached").GetBoolean() ? "recording"
            : wait.GetProperty("Terminal").GetBoolean() ? "terminal" : "timeout";
    }

    [Theory]
    [InlineData("read-deadline")]
    [InlineData("cleanup-deadline")]
    public async Task StorageStartupDeadlineControlReportsTerminalAndRetainsExactOwner(string phase)
    {
        var cfg = Config(false);
        Directory.CreateDirectory(Path.GetDirectoryName(cfg.OutputPath)!);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var evidence = new StorageStartupEvidence(phase);
        Task Hold() { entered.TrySetResult(); return release.Task; }
        var provider = evidence.CreateProvider(() => false,
            beforeRead: phase == "read-deadline" ? Hold : null,
            beforeDispose: phase == "cleanup-deadline" ? Hold : null);
        cfg.WritePaths = CaptureWritePaths.Freeze(cfg);
        cfg.StorageSafety = new WindowStorageSafety(cfg.WritePaths, 60, provider)
            { QueryTimeout = TimeSpan.FromMilliseconds(500) };
        var session = new StorageWgcSession();
        using var backend = new WgcContinuousCaptureBackend(_ => session, StagingToFinalPublisher.Instance,
            _ => new OutputMeta(), () => "fake.exe", _temp.Path);
        backend.FirstFrameObserved += _ => evidence.FirstFrame();
        var tray = new Tray(); var audit = new Audit();
        using var engine = Engine(backend, cfg, tray, audit);
        var rec = Recording(cfg);
        try
        {
            engine.StartCaptureForTests(rec, tray);
            // If cold launch itself fails first, the test fails and preserves that
            // evidence. It cannot count as the intended controlled gate outcome.
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var startup = await StorageStartupWait(engine, rec, 10_000);
            var snapshot = evidence.Save("controlled-" + startup, rec, cfg.StorageSafety, session, audit);
            Assert.True(startup == "terminal", snapshot);
            Assert.Equal(RecState.failed, rec.State);
            Assert.Equal(WindowStorageSafety.UnavailableCode, rec.StopReason);
            Assert.Equal(WindowStorageSafety.UnavailableCode, cfg.StorageSafety.FailureCode);
            Assert.False(cfg.StorageSafety.ProbeRetirementCompletion.IsCompleted);
            var life = evidence.SingleStartupLifetime();
            Assert.True(life.Started);
            Assert.False(life.RetirementCompletion.IsCompleted);
            if (phase == "cleanup-deadline") Assert.True(life.ExitCompletion.IsCompletedSuccessfully);
            Assert.Equal(0, session.Authorizations);
            Assert.False(rec.BackendStartAttempted);
            Assert.False(File.Exists(cfg.OutputPath));
            Assert.Single(engine._recs);
            release.TrySetResult();
            await cfg.StorageSafety.ProbeRetirementCompletion.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(life.ExitCompletion.IsCompletedSuccessfully);
            Assert.True(life.RetirementCompletion.IsCompletedSuccessfully);
            Assert.Null(life.CleanupFailure);
            Assert.Null(cfg.StorageSafety.ProbeRetirementFailure);
            if (phase == "read-deadline")
            {
                var cancellation = Assert.IsAssignableFrom<OperationCanceledException>(
                    Assert.Single(cfg.StorageSafety.ProbeQueryFailure!.Flatten().InnerExceptions));
                evidence.AssertExactCancelledQuery(provider, cancellation, life);
            }
            else Assert.Null(cfg.StorageSafety.ProbeQueryFailure); // Query succeeded after its caller's deadline.
            evidence.Save("passed-before-cleanup", rec, cfg.StorageSafety, session, audit);
        }
        catch
        {
            evidence.Save("failure-before-cleanup", rec, cfg.StorageSafety, session, audit);
            throw;
        }
        finally
        {
            release.TrySetResult(); cfg.StorageSafety.Dispose();
            await cfg.StorageSafety.MonitorCompletion.WaitAsync(TimeSpan.FromSeconds(5));
            await cfg.StorageSafety.ProbeRetirementCompletion.WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    [Fact]
    public async Task StorageStartupWaitReportsUnresolvedWithoutInventingRecording()
    {
        var cfg = Config(false); var tray = new Tray(); var audit = new Audit();
        using var backend = new ControlledBackend();
        using var engine = Engine(backend, cfg, tray, audit);
        var rec = Recording(cfg); rec.State = RecState.pending_confirmation;
        engine._recs[rec.Id] = rec;
        // Zero deadline explicitly requests a snapshot; no sleeps or fake failure.
        Assert.Equal("timeout", await StorageStartupWait(engine, rec, 0));
        Assert.Equal(RecState.pending_confirmation, rec.State);
        Assert.Equal(0, backend.Starts);
    }

    private sealed class StorageStartupEvidence(string mode)
    {
        private readonly Stopwatch _elapsed = Stopwatch.StartNew();
        private readonly ConcurrentQueue<ProbeTrace> _queries = new();
        private int _firstFrames;
        private ProcessStorageCapacityProvider? _provider;
        private ProbeTrace? _current;
        internal void FirstFrame() => Interlocked.Increment(ref _firstFrames);

        internal ProcessStorageCapacityProvider CreateProvider(Func<bool> runtime,
            Func<Task>? beforeRead = null, Func<Task>? beforeDispose = null)
        {
            _provider = new ProcessStorageCapacityProvider
            {
                HooksForTests = new StorageProbeHooks
                {
                    BeforeStart = _ =>
                    {
                        // WindowStorageSafety's existing sampling gate serializes
                        // calls. Bind LastProbe once here to this invocation, then
                        // verify the exact Process in Started; never re-read it to
                        // prove an earlier query's retirement.
                        var trace = new ProbeTrace(_queries.Count + 1, runtime(), _provider!.LastProbe!);
                        _current = trace; _queries.Enqueue(trace); Mark("before-start");
                    },
                    Started = process =>
                    {
                        _current!.IdentityMatches = ReferenceEquals(process, _current.Lifetime.Process);
                        Mark("started");
                    },
                    BeforeRead = async () => { Mark("before-read"); if (beforeRead is not null) await beforeRead(); Mark("read-released"); },
                    BeforeCleanup = () => { Mark("before-cleanup"); return Task.CompletedTask; },
                    BeforeDispose = async () =>
                    {
                        Mark("before-dispose");
                        if (beforeDispose is not null) await beforeDispose();
                        Mark("dispose-released");
                    },
                    CleanupWarning = error => Mark("cleanup-warning:" + error.GetType().Name)
                }
            };
            return _provider;
        }

        private void Mark(string stage) => _current?.Stages.Enqueue(new { stage, ms = _elapsed.Elapsed.TotalMilliseconds });
        internal StorageProbeLifetime SingleRuntimeLifetime() => _queries.Single(q => q.Runtime).Lifetime;
        internal StorageProbeLifetime SingleStartupLifetime() => _queries.Single(q => !q.Runtime).Lifetime;

        // The formal retired-error certificate is the only existing hook that
        // exposes the original returned query Task. Reflect that test-only,
        // preserving both provider and lifetime identity. Pending/successful or
        // aggregate-cleanup queries have no certificate: report task as unknown,
        // never manufacture one from LastProbe or a copied task.
        private static (object? Owner, Task? Query, StorageProbeLifetime? Life) Certificate(Exception error)
        {
            var flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            var evidence = error.GetType().GetProperty("Evidence", flags)?.GetValue(error);
            return (evidence?.GetType().GetProperty("Owner", flags)?.GetValue(evidence),
                evidence?.GetType().GetProperty("Query", flags)?.GetValue(evidence) as Task,
                error.GetType().GetProperty("Lifetime", flags)?.GetValue(error) as StorageProbeLifetime);
        }
        internal void AssertExactCancelledQuery(ProcessStorageCapacityProvider provider, Exception error, StorageProbeLifetime life)
        {
            var certificate = Certificate(error);
            Assert.Same(provider, certificate.Owner); Assert.Same(life, certificate.Life);
            var query = Assert.IsAssignableFrom<Task<IReadOnlyList<StorageVolumeSample>>>(certificate.Query);
            Assert.True(query.IsCanceled);
            Assert.True(provider.HasCleanRetirementEvidence(error, query));
        }

        internal string Save(string outcome, Recording rec, WindowStorageSafety guard, StorageWgcSession session, Audit audit)
        {
            // Observation failure must not replace the exception being diagnosed.
            try
            {
                var error = guard.ProbeQueryFailure;
                var certificates = error?.Flatten().InnerExceptions.Select(Certificate).ToArray()
                    ?? Array.Empty<(object? Owner, Task? Query, StorageProbeLifetime? Life)>();
                var snapshot = JsonSerializer.Serialize(new
                {
                    mode, outcome, elapsedMs = _elapsed.Elapsed.TotalMilliseconds,
                    recording = new { rec.Id, state = rec.State.ToString(), rec.Error, rec.StopReason, rec.IsFinalized,
                        rec.BackendStartAttempted, rec.BackendStartAtUtc, rec.StartedAtUtc },
                    backend = new { session.Authorizations, session.Stops, firstFrames = Volatile.Read(ref _firstFrames) },
                    guard = new { queryTimeoutMs = guard.QueryTimeout.TotalMilliseconds, guard.FailureCode,
                        queryFailure = error?.ToString(), retirementFailure = guard.ProbeRetirementFailure?.ToString(),
                        monitor = guard.MonitorCompletion.Status.ToString(), retirement = guard.ProbeRetirementCompletion.Status.ToString() },
                    auditStages = audit.Events.ToArray(),
                    queries = _queries.Select(q =>
                    {
                        var certificate = certificates.FirstOrDefault(c => ReferenceEquals(c.Life, q.Lifetime)
                            && ReferenceEquals(c.Owner, _provider));
                        return new
                    {
                        q.Ordinal, q.Runtime, q.IdentityMatches, q.Lifetime.Started, q.Lifetime.ProcessId, q.Lifetime.StartTime,
                        phases = q.Stages.ToArray(), originalProcess = Observe(q.Lifetime.Process),
                        exit = q.Lifetime.ExitCompletion.Status.ToString(), retirement = q.Lifetime.RetirementCompletion.Status.ToString(),
                        cleanupFailure = q.Lifetime.CleanupFailure?.ToString(),
                        originalQueryTask = ReferenceEquals(certificate.Life, q.Lifetime) && ReferenceEquals(certificate.Owner, _provider)
                            ? new { id = certificate.Query?.Id, state = certificate.Query?.Status.ToString() } : null,
                        taskObservation = ReferenceEquals(certificate.Life, q.Lifetime) ? "exact-error-certificate" : "unknown-no-task-hook"
                    }; }).ToArray()
                }, new JsonSerializerOptions { WriteIndented = true });
                var directory = Environment.GetEnvironmentVariable("TASK378R_EVIDENCE_DIR")
                    ?? Path.Combine(TestHelper.ProjectRoot, ".local-data", "task378r", "diagnostics", "unscoped");
                if (!string.IsNullOrWhiteSpace(directory))
                {
                    Directory.CreateDirectory(directory);
                    File.WriteAllText(Path.Combine(directory, $"{mode}-{outcome}-{Guid.NewGuid():N}.json"), snapshot);
                }
                return snapshot;
            }
            catch (Exception error) { return "diagnostic-observation-failed:" + error.GetType().Name; }
        }
        private static object Observe(Process process)
        {
            try { return new { state = "observed-original-instance", exited = process.HasExited, id = process.Id }; }
            catch (Exception error) { return new { state = "unknown", observationError = error.GetType().Name }; }
        }
        private sealed class ProbeTrace(int ordinal, bool runtime, StorageProbeLifetime lifetime)
        {
            internal int Ordinal { get; } = ordinal;
            internal bool Runtime { get; } = runtime;
            internal StorageProbeLifetime Lifetime { get; } = lifetime;
            internal bool IdentityMatches;
            internal ConcurrentQueue<object> Stages { get; } = new();
        }
    }
}
