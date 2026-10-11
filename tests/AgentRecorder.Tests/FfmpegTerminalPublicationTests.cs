using System.Collections.Concurrent;
using System.Reflection;
using AgentRecorder.Capture;
using AgentRecorder.Infrastructure;
using Xunit;

namespace AgentRecorder.Tests;

[Collection("NonParallel-AgentRecorderDataDir")]
public sealed class FfmpegTerminalPublicationTests
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    private static readonly Type RunType = typeof(FfmpegCaptureBackend).GetNestedType("VideoOnlyRun", Private)!;
    private static object NewRun(FfmpegCaptureBackend backend, CaptureConfig? config = null)
    {
        var root = Path.GetFullPath(Path.Combine(TestHelper.ProjectRoot, ".local-data/temp/task338/synthetic-never-created"));
        config ??= new CaptureConfig { OutputPath = Path.Combine(root, "final.mp4") };
        var run = RunType.GetConstructors(Private).Single().Invoke(new object[] { config, config.OutputPath,
            root, Path.Combine(root, "missing.mp4"), "synthetic", new StagingToFinalPublisher(new PhysicalFileSystemOperations()) });
        ((TaskCompletionSource)Field(run, "StderrDrained")!).SetResult();
        typeof(FfmpegCaptureBackend).GetField("_videoOnlyRun", Private)!.SetValue(backend, run);
        return run;
    }
    private static object? Field(object run, string name) => RunType.GetField(name, Private)!.GetValue(run);
    private static Task<OutputMeta> Completion(object run) => ((TaskCompletionSource<OutputMeta>)Field(run, "Completion")!).Task;
    private static OutputMeta Call(FfmpegCaptureBackend backend, string method, params object?[] args)
    {
        try { return (OutputMeta)typeof(FfmpegCaptureBackend).GetMethod(method, Private)!.Invoke(backend, args)!; }
        catch (TargetInvocationException ex) when (ex.InnerException is not null)
        { System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(ex.InnerException).Throw(); throw; }
    }
    private static OutputMeta Converge(FfmpegCaptureBackend b, object run, bool natural = true) => Call(b, "ConvergeVideoOnly", run, natural, 1);
    private static OutputMeta Publish(FfmpegCaptureBackend b, object run, OutputMeta candidate, int code) => Call(b, "PublishVideoOnlyTerminal", run, candidate, code);
    private static void Detach(FfmpegCaptureBackend b) => typeof(FfmpegCaptureBackend).GetField("_videoOnlyRun", Private)!.SetValue(b, null);
    private static void Consistent(FfmpegCaptureBackend b, OutputMeta expected, int code)
    { Assert.Same(expected, b.LastMeta); Assert.Equal(code, b.ExitCode); }
    private static void Release(ManualResetEventSlim gate) => Assert.True(gate.Wait(TimeSpan.FromSeconds(10)), "Controlled schedule did not release.");

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task CompletionReleasesObserversOnlyAfterCanonicalFieldsAreInstalled(bool success)
    {
        using var b = new FfmpegCaptureBackend(); var run = NewRun(b);
        using var before = new ManualResetEventSlim(); using var allow = new ManualResetEventSlim();
        using var after = new ManualResetEventSlim(); using var finish = new ManualResetEventSlim();
        var candidate = new OutputMeta { OutputFileExists = success, SizeBytes = success ? 13 : 0 };
        b.BeforeVideoOnlyTerminalForTests = () => { before.Set(); Release(allow); };
        b.AfterVideoOnlyTerminalForTests = () => { after.Set(); Release(finish); };
        var owner = Task.Run(() => Publish(b, run, candidate, success ? 0 : -1));
        try
        {
            Release(before); Assert.False(Completion(run).IsCompleted); Assert.Null(b.LastMeta);
            allow.Set(); Release(after);
            // Owner cannot execute any code after signalling until this observer checks.
            var observed = await Completion(run).WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Same(candidate, observed); Consistent(b, observed, success ? 0 : -1);
            Assert.False(b.IsVideoOnlyOutputSettled); // The publication helper cannot imply I/O settled.
        }
        finally { allow.Set(); finish.Set(); b.BeforeVideoOnlyTerminalForTests = null; b.AfterVideoOnlyTerminalForTests = null; }
        Assert.Same(candidate, await owner.WaitAsync(TimeSpan.FromSeconds(5))); Detach(b);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task NaturalOrStopOwnerAndMultipleStopWaitersShareOneFailureAndCallbackRule(bool natural)
    {
        using var b = new FfmpegCaptureBackend(); var run = NewRun(b);
        using var entered = new ManualResetEventSlim(); using var release = new ManualResetEventSlim();
        using var waitersEntered = new CountdownEvent(4); var callbacks = 0; OutputMeta? callbackMeta = null; int callbackCode = 42;
        b.BeforeVideoOnlyFinalizeForTests = _ => { entered.Set(); Release(release); };
        b.WaitForVideoOnlyOwnerForTests = () => waitersEntered.Signal();
        b.OnNaturalExit((code, meta) => { callbackCode = code; callbackMeta = meta; Interlocked.Increment(ref callbacks); });
        var owner = Task.Run(() => Converge(b, run, natural)); Task<OutputMeta>[] waiters = Array.Empty<Task<OutputMeta>>();
        try
        {
            Release(entered); waiters = Enumerable.Range(0, 4).Select(_ => Task.Run(b.Stop)).ToArray();
            Assert.True(waitersEntered.Wait(TimeSpan.FromSeconds(5))); release.Set();
            var canonical = await Completion(run).WaitAsync(TimeSpan.FromSeconds(5)); Consistent(b, canonical, -1);
            Assert.Same(canonical, await owner.WaitAsync(TimeSpan.FromSeconds(5)));
            foreach (var result in await Task.WhenAll(waiters).WaitAsync(TimeSpan.FromSeconds(5))) Assert.Same(canonical, result);
            Assert.Equal(natural ? 1 : 0, callbacks);
            if (natural) { Assert.Same(canonical, callbackMeta); Assert.Equal(-1, callbackCode); }
            Assert.True(b.IsVideoOnlyOutputSettled);
        }
        finally { release.Set(); b.WaitForVideoOnlyOwnerForTests = null; }
        var again = b.Stop(); Assert.Same(await Completion(run), again); Assert.Equal(natural ? 1 : 0, callbacks);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task CompetingSuccessAndFailureCandidatesSelectOneObjectAndCode(bool successFirst)
    {
        using var b = new FfmpegCaptureBackend(); var run = NewRun(b);
        var success = new OutputMeta { OutputFileExists = true, SizeBytes = 13 }; var failure = new OutputMeta { StopReason = "failed" };
        using var ready = new CountdownEvent(2); using var successGate = new ManualResetEventSlim(); using var failureGate = new ManualResetEventSlim();
        var s = Task.Run(() => { ready.Signal(); Release(successGate); return Publish(b, run, success, 0); });
        var f = Task.Run(() => { ready.Signal(); Release(failureGate); return Publish(b, run, failure, -1); });
        try
        {
            Assert.True(ready.Wait(TimeSpan.FromSeconds(5))); (successFirst ? successGate : failureGate).Set();
            var canonical = await Completion(run).WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Same(successFirst ? success : failure, canonical); Consistent(b, canonical, successFirst ? 0 : -1);
        }
        finally { successGate.Set(); failureGate.Set(); }
        Assert.Same(await s, await f); Assert.Same(await Completion(run), Publish(b, run, new OutputMeta(), 27)); Detach(b);
    }

    private sealed class Capacity : IStorageCapacityProvider
    {
        public Task<IReadOnlyList<StorageVolumeSample>> QueryAsync(IReadOnlyList<string> paths, CancellationToken token) =>
            Task.FromResult<IReadOnlyList<StorageVolumeSample>>(paths.Select(p => new StorageVolumeSample(p, "same-volume", 4L * 1024 * 1024 * 1024)).ToArray());
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task TimeoutWinsWithoutReleasingOwnerOutputOrCopyCapacity(bool natural)
    {
        using var b = new FfmpegCaptureBackend();
        var root = Path.GetFullPath(Path.Combine(TestHelper.ProjectRoot, ".local-data/temp/task338/synthetic-never-created"));
        using var safety = new WindowStorageSafety(new(root, null, null), 1, new Capacity());
        safety.ReserveFfmpegVideoOnlyPublication(); var reserved = safety.RemainingPublicationCopyBytes;
        var cfg = new CaptureConfig { SourceKind = "region", Bounds = (0, 0, 64, 64), OutputPath = Path.Combine(root, "final.mp4"), StorageSafety = safety };
        var run = NewRun(b, cfg); using var entered = new ManualResetEventSlim(); using var release = new ManualResetEventSlim();
        var callbacks = 0; OutputMeta? callbackMeta = null;
        b.OnNaturalExit((code, m) => { Assert.Equal(-1, code); callbackMeta = m; callbacks++; });
        b.BeforeVideoOnlyFinalizeForTests = _ => { entered.Set(); Release(release); };
        var owner = Task.Run(() => Converge(b, run, natural));
        OutputMeta timeout;
        try
        {
            Release(entered); b.WaitForVideoOnlyOwnerForTests = () => throw new TimeoutException();
            timeout = b.Stop(); Assert.Equal("convergence_owner_timeout", timeout.StopReason);
            Assert.Same(timeout, await Completion(run)); Consistent(b, timeout, -1); Assert.Null(timeout.PublicationReceipt);
            Assert.True(((FileCommitGate)Field(run, "Gate")!).IsClosed);
            Assert.True(((CancellationToken)Field(run, "Token")!).IsCancellationRequested);
            Assert.False(b.IsVideoOnlyOutputSettled); Assert.Equal(reserved, safety.RemainingPublicationCopyBytes);
            // Start must refuse reuse while the old owner's I/O and reservations still exist.
            Assert.Throws<InvalidOperationException>(() => CaptureAuthorizationTestHelper.StartWithSyntheticConsumedProof(b, cfg));
        }
        finally { release.Set(); b.WaitForVideoOnlyOwnerForTests = null; }
        Assert.Same(timeout, await owner.WaitAsync(TimeSpan.FromSeconds(5))); Consistent(b, timeout, -1);
        Assert.True(b.IsVideoOnlyOutputSettled); Assert.Equal(0, safety.RemainingPublicationCopyBytes);
        Assert.Equal(natural ? 1 : 0, callbacks); if (natural) Assert.Same(timeout, callbackMeta);
        // A later success cannot replace the timeout or issue its metadata to a caller.
        var lateSuccess = new OutputMeta { OutputFileExists = true, SizeBytes = 13 };
        Assert.Same(timeout, Publish(b, run, lateSuccess, 0)); Consistent(b, timeout, -1);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task OldRunTailCannotOverwriteNewStartFailureOrCallback(bool timeoutFirst)
    {
        using var b = new FfmpegCaptureBackend(); var oldRun = NewRun(b);
        using var entered = new ManualResetEventSlim(); using var release = new ManualResetEventSlim();
        using var finalizing = new ManualResetEventSlim(); using var allowFinalize = new ManualResetEventSlim();
        var callbacks = 0; b.OnNaturalExit((_, _) => callbacks++);
        b.BeforeVideoOnlyFinalizeForTests = _ => { finalizing.Set(); Release(allowFinalize); };
        var oldOwner = Task.Run(() => Converge(b, oldRun));
        var root = Path.GetFullPath(Path.Combine(TestHelper.ProjectRoot, ".local-data/temp/task338/reuse-" + Guid.NewGuid().ToString("N")));
        OutputMeta current;
        try
        {
            Release(finalizing);
            if (timeoutFirst)
            {
                b.WaitForVideoOnlyOwnerForTests = () => throw new TimeoutException();
                var timeout = b.Stop(); Assert.Equal("convergence_owner_timeout", timeout.StopReason);
                b.WaitForVideoOnlyOwnerForTests = null;
            }
            // Pause only the late owner's attempt, after the timeout was signalled.
            b.BeforeVideoOnlyTerminalForTests = () => { entered.Set(); Release(release); };
            allowFinalize.Set();
            Release(entered); Assert.True(b.IsVideoOnlyOutputSettled);
            if (!timeoutFirst)
            {
                // Publish old completion after cleanup while the tail is paused.
                b.BeforeVideoOnlyTerminalForTests = null; Publish(b, oldRun, new OutputMeta(), -1);
            }
            b.BeforeVideoOnlyTerminalForTests = null;
            b.ConfigureProcessForTests = _ => throw new IOException("Synthetic start failure.");
            var cfg = new CaptureConfig { SourceKind = "region", Bounds = (0, 0, 64, 64), OutputPath = Path.Combine(root, "new.mp4") };
            Assert.Throws<ApiException>(() => CaptureAuthorizationTestHelper.StartWithSyntheticConsumedProof(b, cfg));
            current = b.LastMeta!; Assert.Equal("encoder_start_failed", current.StopReason);
            Assert.Same(current, await b.VideoOnlyCompletion!); Consistent(b, current, -1);
        }
        finally { allowFinalize.Set(); release.Set(); b.BeforeVideoOnlyTerminalForTests = null; b.WaitForVideoOnlyOwnerForTests = null; }
        await oldOwner.WaitAsync(TimeSpan.FromSeconds(5)); Consistent(b, current, -1);
        Assert.Same(current, b.Stop()); Assert.Same(await Completion(oldRun), Converge(b, oldRun)); Consistent(b, current, -1);
        Assert.Equal(0, callbacks);
    }

    [Fact]
    public async Task InitialStartFailureRepeatedStopAndSupportedReuseHaveCanonicalMetadata()
    {
        using var b = new FfmpegCaptureBackend(); b.ConfigureProcessForTests = _ => throw new IOException("Synthetic start failure.");
        OutputMeta? previous = null;
        for (int i = 0; i < 2; i++)
        {
            var root = Path.GetFullPath(Path.Combine(TestHelper.ProjectRoot, ".local-data/temp/task338/start-" + Guid.NewGuid().ToString("N")));
            var cfg = new CaptureConfig { SourceKind = "region", Bounds = (0, 0, 64, 64), OutputPath = Path.Combine(root, "final.mp4") };
            Assert.Throws<ApiException>(() => CaptureAuthorizationTestHelper.StartWithSyntheticConsumedProof(b, cfg));
            var result = await b.VideoOnlyCompletion!; Consistent(b, result, -1);
            Assert.True(b.HasExited);
            Assert.NotSame(previous, result); Assert.Equal("encoder_start_failed", result.StopReason);
            Assert.Same(result, b.Stop()); Assert.Same(result, b.Stop()); Assert.True(b.IsVideoOnlyOutputSettled); previous = result;
        }
    }

    [Fact]
    public void ReviewMetaCounterexampleScheduleNeverSeesCompletedTaskWithNullOrWrongMetadata()
    {
        using var queue = new BlockingCollection<(FfmpegCaptureBackend B, Task<OutputMeta> Task, ManualResetEventSlim Ready, ManualResetEventSlim Done)>();
        int mismatches = 0;
        var reader = new Thread(() => {
            foreach (var item in queue.GetConsumingEnumerable()) {
                item.Ready.Set(); while (!item.Task.IsCompleted) Thread.SpinWait(1);
                var canonical = item.Task.GetAwaiter().GetResult();
                if (!ReferenceEquals(canonical, item.B.LastMeta) || item.B.ExitCode != -1) Interlocked.Increment(ref mismatches);
                item.Done.Set();
            }
        });
        reader.Start();
        try
        {
            for (int i = 0; i < 10000; i++)
            {
                using var b = new FfmpegCaptureBackend(); var run = NewRun(b);
                using var ready = new ManualResetEventSlim(); using var done = new ManualResetEventSlim();
                queue.Add((b, Completion(run), ready, done)); Release(ready);
                var result = Converge(b, run); Release(done); Consistent(b, result, -1);
            }
        }
        finally { queue.CompleteAdding(); reader.Join(); }
        Assert.Equal(0, mismatches);
    }

    private sealed class Prober(bool failFinal) : IOutputProber
    {
        private int _calls;
        public OutputMeta Probe(string path)
        {
            var meta = FfmpegCaptureBackend.Probe(path);
            if (++_calls == 2 && failFinal) meta.DurationSeconds = 0;
            return meta;
        }
    }
    private static CaptureConfig RealConfig(string root) => new()
    { SourceKind = "region", Bounds = (0, 0, 64, 64), Fps = 15, DurationSeconds = 1, OutputPath = Path.Combine(root, "final.mp4") };
    private static void SyntheticInput(FfmpegCaptureBackend b) => b.VideoInputArgumentsForTests = _ =>
        new[] { "-f", "lavfi", "-t", "1", "-i", "color=c=black:s=64x64:r=15" };

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task RealSuccessAndPostCommitProbeFailurePublishCoherentMetadata(bool failure)
    {
        var root = Path.GetFullPath(Path.Combine(TestHelper.ProjectRoot, ".local-data/temp/task338/real-" + Guid.NewGuid().ToString("N")));
        var previous = Environment.GetEnvironmentVariable("AGENT_RECORDER_DATA_DIR");
        DataDirResolver.SetOverride(root); Environment.SetEnvironmentVariable("AGENT_RECORDER_DATA_DIR", root);
        using var b = new FfmpegCaptureBackend(); SyntheticInput(b); b.VideoOnlyProberForTests = new Prober(failure);
        using var before = new ManualResetEventSlim(); using var allow = new ManualResetEventSlim();
        using var after = new ManualResetEventSlim(); using var finish = new ManualResetEventSlim();
        b.BeforeVideoOnlyTerminalForTests = () => { before.Set(); Release(allow); };
        b.AfterVideoOnlyTerminalForTests = () => { after.Set(); Release(finish); };
        var callback = new TaskCompletionSource<OutputMeta>(TaskCreationOptions.RunContinuationsAsynchronously);
        b.OnNaturalExit((code, m) => { Assert.Equal(failure ? -1 : 0, code); callback.SetResult(m); });
        try
        {
            var cfg = RealConfig(root); CaptureAuthorizationTestHelper.StartWithSyntheticConsumedProof(b, cfg);
            Release(before); Assert.False(b.VideoOnlyCompletion!.IsCompleted); Assert.Null(b.LastMeta); allow.Set(); Release(after);
            var result = await b.VideoOnlyCompletion.WaitAsync(TimeSpan.FromSeconds(5)); Consistent(b, result, failure ? -1 : 0);
            Assert.Equal(!failure, result.OutputFileExists);
            if (failure) Assert.Null(result.PublicationReceipt); else Assert.NotNull(result.PublicationReceipt);
            Assert.True(File.Exists(cfg.OutputPath)); Assert.True(FfmpegCaptureBackend.Probe(cfg.OutputPath).DurationSeconds > 0);
            finish.Set(); Assert.Same(result, await callback.Task.WaitAsync(TimeSpan.FromSeconds(5))); Assert.Same(result, b.Stop());
        }
        finally
        {
            allow.Set(); finish.Set(); b.BeforeVideoOnlyTerminalForTests = null; b.AfterVideoOnlyTerminalForTests = null;
            b.Dispose(); DataDirResolver.ClearOverride(); Environment.SetEnvironmentVariable("AGENT_RECORDER_DATA_DIR", previous);
        }
    }

    [Fact]
    public async Task TimeoutAfterRealCommitKeepsFinalUntilOwnerCleansUpWithoutPublishingLateSuccess()
    {
        var root = Path.GetFullPath(Path.Combine(TestHelper.ProjectRoot, ".local-data/temp/task338/late-success-" + Guid.NewGuid().ToString("N")));
        var previous = Environment.GetEnvironmentVariable("AGENT_RECORDER_DATA_DIR");
        DataDirResolver.SetOverride(root); Environment.SetEnvironmentVariable("AGENT_RECORDER_DATA_DIR", root);
        using var b = new FfmpegCaptureBackend(); SyntheticInput(b);
        using var safety = new WindowStorageSafety(new(root, null, null), 1, new Capacity());
        using var entered = new ManualResetEventSlim(); using var release = new ManualResetEventSlim();
        b.BeforeVideoOnlyCleanupForTests = () => { entered.Set(); Release(release); };
        var callback = new TaskCompletionSource<OutputMeta>(TaskCreationOptions.RunContinuationsAsynchronously); var callbacks = 0;
        b.OnNaturalExit((code, m) => { Assert.Equal(-1, code); callbacks++; callback.SetResult(m); });
        try
        {
            var cfg = RealConfig(root); cfg.StorageSafety = safety;
            CaptureAuthorizationTestHelper.StartWithSyntheticConsumedProof(b, cfg); Release(entered);
            Assert.True(File.Exists(cfg.OutputPath)); var committed = File.ReadAllBytes(cfg.OutputPath);
            Assert.True(File.Exists(b.VideoOnlyStagingPath)); Assert.Equal(0, safety.RemainingPublicationCopyBytes); // Physical copy already finished.
            Assert.False(b.IsVideoOnlyOutputSettled); Assert.False(b.VideoOnlyCompletion!.IsCompleted);
            b.WaitForVideoOnlyOwnerForTests = () => throw new TimeoutException();
            var timeout = b.Stop(); Consistent(b, timeout, -1); Assert.Equal("convergence_owner_timeout", timeout.StopReason);
            Assert.Null(timeout.PublicationReceipt); Assert.False(timeout.OutputFileExists);
            Assert.False(b.IsVideoOnlyOutputSettled); Assert.Equal(0, safety.RemainingPublicationCopyBytes);
            Assert.True(File.Exists(b.VideoOnlyStagingPath)); Assert.Equal(committed, File.ReadAllBytes(cfg.OutputPath));
            Assert.Throws<InvalidOperationException>(() => CaptureAuthorizationTestHelper.StartWithSyntheticConsumedProof(b, cfg));
            b.WaitForVideoOnlyOwnerForTests = null; release.Set();
            Assert.Same(timeout, await callback.Task.WaitAsync(TimeSpan.FromSeconds(5))); Assert.Equal(1, callbacks);
            Assert.True(b.IsVideoOnlyOutputSettled); Assert.Equal(0, safety.RemainingPublicationCopyBytes);
            Assert.Same(timeout, b.Stop()); Consistent(b, timeout, -1); Assert.Null(b.LastMeta!.PublicationReceipt);
            Assert.Equal(committed, File.ReadAllBytes(cfg.OutputPath));
        }
        finally
        {
            release.Set(); b.BeforeVideoOnlyCleanupForTests = null; b.WaitForVideoOnlyOwnerForTests = null;
            b.Dispose(); DataDirResolver.ClearOverride(); Environment.SetEnvironmentVariable("AGENT_RECORDER_DATA_DIR", previous);
        }
    }

    [Fact]
    public Task TimeoutDuringPhysicalCopyKeepsReservationsAndStageUntilWorkerExits() => TimeoutDuringCopy(dispose: false);

    [Fact]
    public Task TimeoutDisposeDefersResourceClosureUntilActualOwnerFinishes() => TimeoutDuringCopy(dispose: true);

    [Fact]
    public async Task PublishedTimeoutDoesNotPretendAnActualWaitingProcessHasExited()
    {
        using var b = new FfmpegCaptureBackend(); var run = NewRun(b);
        using var process = new System.Diagnostics.Process { StartInfo = new System.Diagnostics.ProcessStartInfo(
            Path.Combine(AppContext.BaseDirectory, "AgentRecorder.Headless.exe"), "--internal-storage-capacity-probe")
            { UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardOutput = true } };
        Assert.True(process.Start());
        typeof(FfmpegCaptureBackend).GetField("_proc", Private)!.SetValue(b, process);
        RunType.GetField("OwnerClaimed", Private)!.SetValue(run, true);
        b.WaitForVideoOnlyOwnerForTests = () => throw new TimeoutException();
        try
        {
            var timeout = b.Stop(); Assert.Same(timeout, await Completion(run)); Consistent(b, timeout, -1);
            Assert.False(process.HasExited); Assert.False(b.HasExited); Assert.False(b.IsVideoOnlyOutputSettled);
            var root = Path.GetFullPath(Path.Combine(TestHelper.ProjectRoot, ".local-data/temp/task338"));
            await process.StandardInput.WriteLineAsync(System.Text.Json.JsonSerializer.Serialize(new[] { root })); process.StandardInput.Close();
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5)); Assert.Equal(0, process.ExitCode);
            Assert.True(b.HasExited); Assert.False(b.IsVideoOnlyOutputSettled); Consistent(b, timeout, -1);
        }
        finally
        {
            if (!process.HasExited) { process.Kill(); await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5)); }
            b.WaitForVideoOnlyOwnerForTests = null; Detach(b);
        }
    }

    private static async Task TimeoutDuringCopy(bool dispose)
    {
        var root = Path.GetFullPath(Path.Combine(TestHelper.ProjectRoot, ".local-data/temp/task338/inflight-" + Guid.NewGuid().ToString("N")));
        var previous = Environment.GetEnvironmentVariable("AGENT_RECORDER_DATA_DIR");
        DataDirResolver.SetOverride(root); Environment.SetEnvironmentVariable("AGENT_RECORDER_DATA_DIR", root);
        using var b = new FfmpegCaptureBackend(); SyntheticInput(b);
        using var safety = new WindowStorageSafety(new(root, null, null), 1, new Capacity());
        using var entered = new ManualResetEventSlim(); using var release = new ManualResetEventSlim();
        safety.AfterPhysicalCopyWriteForTests = () => { entered.Set(); Release(release); };
        var callback = new TaskCompletionSource<OutputMeta>(TaskCreationOptions.RunContinuationsAsynchronously);
        b.OnNaturalExit((code, m) => { Assert.Equal(-1, code); callback.SetResult(m); });
        try
        {
            var cfg = RealConfig(root); cfg.StorageSafety = safety;
            CaptureAuthorizationTestHelper.StartWithSyntheticConsumedProof(b, cfg); Release(entered);
            var reserved = safety.RemainingPublicationCopyBytes; Assert.True(reserved > 0);
            Assert.True(File.Exists(b.VideoOnlyStagingPath)); Assert.False(b.IsVideoOnlyOutputSettled);
            b.WaitForVideoOnlyOwnerForTests = () => throw new TimeoutException();
            var process = (System.Diagnostics.Process)typeof(FfmpegCaptureBackend).GetField("_proc", Private)!.GetValue(b)!;
            OutputMeta timeout;
            if (dispose) { b.Dispose(); timeout = b.LastMeta!; Assert.True(process.HasExited); }
            else timeout = b.Stop();
            Consistent(b, timeout, -1); Assert.Null(timeout.PublicationReceipt);
            Assert.Equal(reserved, safety.RemainingPublicationCopyBytes); Assert.False(b.IsVideoOnlyOutputSettled);
            Assert.True(File.Exists(b.VideoOnlyStagingPath));
            Assert.Throws<InvalidOperationException>(() => CaptureAuthorizationTestHelper.StartWithSyntheticConsumedProof(b, cfg));
            b.WaitForVideoOnlyOwnerForTests = null; release.Set();
            Assert.Same(timeout, await callback.Task.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.True(b.IsVideoOnlyOutputSettled); Assert.Equal(0, safety.RemainingPublicationCopyBytes);
            Assert.False(File.Exists(cfg.OutputPath)); Consistent(b, timeout, -1);
            if (dispose) Assert.Throws<InvalidOperationException>(() => process.HasExited);
        }
        finally
        {
            release.Set(); safety.AfterPhysicalCopyWriteForTests = null; b.WaitForVideoOnlyOwnerForTests = null;
            b.Dispose(); DataDirResolver.ClearOverride(); Environment.SetEnvironmentVariable("AGENT_RECORDER_DATA_DIR", previous);
        }
    }
}
