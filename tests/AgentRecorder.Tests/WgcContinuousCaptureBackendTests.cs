using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.ExceptionServices;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using AgentRecorder.Capture;
using AgentRecorder.Core;
using AgentRecorder.Infrastructure;
using Xunit;
using Xunit.Abstractions;

namespace AgentRecorder.Tests;

/// <summary>
/// Tests for <see cref="WgcContinuousCaptureBackend"/>.
/// All tests use injectable fakes for the managed session, publisher and probe
/// so no real WGC capture or GUI is exercised. A dedicated real-process fixture
/// in a separate non-parallel collection verifies Dispose cleans up a live
/// subprocess tree.
/// </summary>
[Collection("NonParallel-WindowBackend")]
public sealed class WgcContinuousCaptureBackendTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _finalDir;
    private readonly ITestOutputHelper _output;
    private readonly List<IDisposable> _disposables = new();
    private SessionHarness? _lastHarness;

    public WgcContinuousCaptureBackendTests(ITestOutputHelper output)
    {
        _output = output;
        _tempDir = Path.Combine(Path.GetTempPath(), "AgentRecorderTests", $"wgc-cbe-{Guid.NewGuid():N}");
        _finalDir = Path.Combine(_tempDir, "final");
        Directory.CreateDirectory(_tempDir);
        Directory.CreateDirectory(_finalDir);
    }

    public void Dispose()
    {
        var disposeExceptions = new List<Exception>();
        foreach (var d in _disposables)
        {
            try { d.Dispose(); }
            catch (Exception ex) { disposeExceptions.Add(ex); }
        }

        // Give async continuations (process exit, completion handler) a moment
        // to release file handles before we assert zero residue.
        Thread.Sleep(50);

        Exception? lastCleanupException = null;
        for (int attempt = 0; attempt < 20; attempt++)
        {
            try
            {
                if (Directory.Exists(_tempDir))
                    Directory.Delete(_tempDir, recursive: true);
                if (!Directory.Exists(_tempDir))
                    break;
            }
            catch (Exception ex)
            {
                lastCleanupException = ex;
                Thread.Sleep(50);
            }
        }

        Assert.False(Directory.Exists(_tempDir),
            $"Test temp directory was not cleaned up: {_tempDir}. Last error: {lastCleanupException?.Message}");

        if (disposeExceptions.Count > 0)
            throw new AggregateException("Disposing test resources failed.", disposeExceptions);
    }

    // -----------------------------------------------------------------
    // Test helpers
    // -----------------------------------------------------------------

    private CaptureConfig CreateValidConfig(
        string? outputPath = null,
        int durationSeconds = 5,
        int fps = 30,
        (int x, int y, int w, int h)? bounds = null)
    {
        return new CaptureConfig
        {
            SourceKind = "display",
            Bounds = bounds ?? (-100, 0, 1920, 1080),
            DurationSeconds = durationSeconds,
            Fps = fps,
            OutputPath = outputPath ?? Path.Combine(_finalDir, "out.mp4")
        };
    }

    private CaptureConfig CreateRegionConfig(string outputPath, bool deferCaptureStart = true) => new()
    {
        SourceKind = "region",
        DisplayId = "display-left",
        DisplayBounds = (-1920, -200, 1920, 1080),
        Bounds = (-1800, -100, 640, 480),
        DurationSeconds = 5,
        Fps = 30,
        OutputPath = outputPath,
        DeferCaptureStart = deferCaptureStart
    };

    private WgcContinuousCaptureBackend CreateBackend(
        FakeWgcContinuousProcess process,
        out FakePublisher publisher,
        out FakeProbe probe,
        Action<WgcContinuousSessionOptions>? configureOptions = null)
    {
        publisher = new FakePublisher();
        probe = new FakeProbe();
        _lastHarness = null;

        var backend = new WgcContinuousCaptureBackend(
            options =>
            {
                configureOptions?.Invoke(options);
                File.WriteAllText(options.HelperExePath, "fake");
                process.WaitForBeginSignalPath = options.BeginSignalPath;
                process.AutoContinueOnStopSignalPath = options.StopSignalPath;
                process.OutputFilePath = options.OutputPath;
                var harness = new SessionHarness(options, process);
                _lastHarness = harness;
                // The backend owns the session lifecycle; do not add the harness
                // to the disposables list so that tests cannot get a false pass
                // from a separate Dispose.
                return harness.Session;
            },
            publisher,
            probe.Probe,
            () => Path.Combine(_tempDir, "fake-helper.exe"),
            _tempDir);

        _disposables.Add(backend);
        return backend;
    }

    private WgcContinuousCaptureBackend CreateBackend(
        Func<WgcContinuousSessionOptions, IWgcContinuousBackendSession> sessionFactory,
        out FakePublisher publisher,
        out FakeProbe probe,
        Func<string>? helperPathResolver = null)
    {
        publisher = new FakePublisher();
        probe = new FakeProbe();
        _lastHarness = null;

        var backend = new WgcContinuousCaptureBackend(
            sessionFactory,
            publisher,
            probe.Probe,
            helperPathResolver ?? (() => Path.Combine(_tempDir, "fake-helper.exe")),
            _tempDir);

        _disposables.Add(backend);
        return backend;
    }

    private static void CreatePlaceholderMp4(string path, long size)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);
        using var fs = new FileStream(path, FileMode.Create, FileAccess.Write);
        fs.SetLength(size);
    }

    private static async Task WaitForConditionAsync(Func<bool> predicate, TimeSpan timeout, string? message = null)
    {
        var sw = Stopwatch.StartNew();
        while (!predicate() && sw.Elapsed < timeout)
            await Task.Delay(10);

        if (!predicate())
            throw new TimeoutException(message ?? "Condition was not met within the allotted timeout.");
    }

    private string[] Started(
        string recordingId,
        string outputPath,
        string captureMethod = "WGC_D3D11_FRAME_STREAM") => new[]
    {
        "RESULT: STARTED",
        $"RecordingId: {recordingId}",
        $"Output: {outputPath}",
        "Container: mp4",
        "Codec: h264",
        "Fps: 30",
        "Width: 1920",
        "Height: 1080",
        $"CaptureMethod: {captureMethod}",
        ""
    };

    private static string[] Progress(long frames, long elapsedMs, long bytesWritten = 0) => new[]
    {
        "RESULT: PROGRESS",
        $"FramesCaptured: {frames}",
        "FramesDropped: 0",
        $"ElapsedMs: {elapsedMs}",
        $"BytesWritten: {bytesWritten}",
        ""
    };

    private static string[] Ok(long frames = 300, long durationMs = 5000, long fileSize = 15000000) => new[]
    {
        "RESULT: OK",
        $"FramesCaptured: {frames}",
        "FramesDropped: 0",
        $"DurationMs: {durationMs}",
        $"FileSize: {fileSize} bytes",
        "Width: 1920",
        "Height: 1080",
        ""
    };

    private static string[] RegionStarted(
        string recordingId,
        string outputPath,
        int width = 640,
        int height = 480) => new[]
    {
        "RESULT: STARTED",
        $"RecordingId: {recordingId}",
        $"Output: {outputPath}",
        "Container: mp4",
        "Codec: h264",
        "Fps: 30",
        $"Width: {width}",
        $"Height: {height}",
        "CaptureMethod: WGC_D3D11_REGION_FRAME_STREAM",
        ""
    };

    private static string[] RegionOk(
        int width,
        int height,
        long fileSize = 1024) => new[]
    {
        "RESULT: OK",
        "FramesCaptured: 300",
        "FramesDropped: 0",
        "DurationMs: 5000",
        $"FileSize: {fileSize} bytes",
        $"Width: {width}",
        $"Height: {height}",
        ""
    };

    private static string[] Stopped(long frames = 150, long durationMs = 2500, long fileSize = 7500000) => new[]
    {
        "RESULT: STOPPED",
        "StopReason: user_requested",
        $"FramesCaptured: {frames}",
        "FramesDropped: 0",
        $"DurationMs: {durationMs}",
        $"FileSize: {fileSize} bytes",
        "Width: 1920",
        "Height: 1080",
        ""
    };

    private static string[] Fail(string reason, string errorCode) => new[]
    {
        "RESULT: FAIL",
        $"ErrorCode: {errorCode}",
        $"Reason: {reason}",
        "FramesCaptured: 0",
        "BytesWritten: 0",
        ""
    };

    private static string[] Malformed() => new[]
    {
        "RESULT: STARTED",
        "RecordingId: r",
        "This is not a valid event line and never terminates",
        ""
    };

    // -----------------------------------------------------------------
    // 1. Start 对合法 display 配置快速返回并只创建/授权一个 session
    // -----------------------------------------------------------------

    [Fact]
    public async Task Start_ValidDisplay_QuickStart_SingleSessionAndAuthorization()
    {
        string outputPath = Path.Combine(_finalDir, "quickstart.mp4");
        CreatePlaceholderMp4(outputPath, 1024);
        string recordingId = "r-quick";
        var process = new FakeWgcContinuousProcess(
            initialStdout: Started(recordingId, outputPath).Concat(Ok(fileSize: 1024)).ToArray(),
            createOutputFile: true,
            outputFilePath: outputPath,
            outputFileSize: 1024);

        var backend = CreateBackend(process, out var publisher, out _);
        var cfg = CreateValidConfig(outputPath);

        var sw = Stopwatch.StartNew();
        backend.Start(cfg);
        sw.Stop();

        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(2), "Start must return quickly.");
        Assert.NotNull(_lastHarness);
        Assert.Equal(1, _lastHarness!.Process.StartInvocationCount);

        // Authorization writes the begin signal. Use the fake helper's persistent
        // observation evidence rather than polling the transient signal file, which
        // can be deleted by completion cleanup before the assertion runs.
        await process.BeginSignalObservedTask.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(1, process.BeginSignalObservationCount);
        Assert.Equal(_lastHarness.Options.BeginToken, process.ObservedBeginToken);

        _output.WriteLine("Session state after Start: " + _lastHarness.Session.State);
        await Task.Delay(100);
        _output.WriteLine("Session state after 100ms: " + _lastHarness.Session.State);
        if (_lastHarness.Session.IsCompleted)
        {
            var r = await _lastHarness.Session.CompletionTask;
            _output.WriteLine("Result state: " + r.State);
            _output.WriteLine("Failure phase: " + r.FailurePhase);
            _output.WriteLine("Failure category: " + r.FailureCategory);
            _output.WriteLine("Stderr tail: " + r.StderrTail);
        }

        var meta = backend.Stop();
        Assert.NotNull(meta);

        _output.WriteLine("Session state after Stop: " + _lastHarness.Session.State);
        Assert.Equal(1, publisher.CallCount);
    }

    // -----------------------------------------------------------------
    // 2. display 负坐标正确映射
    // -----------------------------------------------------------------

    [Fact]
    public void Start_DisplayNegativeBounds_MapsCorrectly()
    {
        string outputPath = Path.Combine(_finalDir, "negative.mp4");
        CreatePlaceholderMp4(outputPath, 1024);
        var process = new FakeWgcContinuousProcess(
            initialStdout: Started("r-neg", outputPath).Concat(Ok()).ToArray(),
            createOutputFile: true,
            outputFilePath: outputPath,
            outputFileSize: 1024);

        var backend = CreateBackend(process, out _, out _);
        backend.Start(CreateValidConfig(outputPath, bounds: (-256, -128, 1920, 1080)));

        Assert.NotNull(_lastHarness);
        var args = _lastHarness!.Process.CapturedArguments;
        Assert.NotNull(args);
        int idx = -1;
        for (int i = 0; i < args!.Count; i++)
        {
            if (args[i] == "--display-bounds") { idx = i; break; }
        }

        Assert.True(idx >= 0 && idx + 1 < args.Count, "--display-bounds argument missing.");
        Assert.Equal("-256,-128,1920,1080", args[idx + 1]);

        backend.Dispose();
    }

    // -----------------------------------------------------------------
    // 3. window/region、无 duration、>10 秒、microphone、非法输出在启动前拒绝
    // -----------------------------------------------------------------

    [Fact]
    public async Task Start_ValidWindow_UsesWindowTargetAndArguments()
    {
        string outputPath = Path.Combine(_finalDir, "window.mp4");
        CreatePlaceholderMp4(outputPath, 1024);
        var process = new FakeWgcContinuousProcess(
            Started("r-window", outputPath, "WGC_D3D11_WINDOW_FRAME_STREAM")
                .Concat(Ok(fileSize: 1024)).ToArray(),
            createOutputFile: true,
            outputFilePath: outputPath,
            outputFileSize: 1024);
        var backend = CreateBackend(process, out _, out _);

        var cfg = CreateValidConfig(outputPath);
        cfg.SourceKind = "window";
        cfg.WindowHandle = (nint)0x1234;

        backend.Start(cfg);

        Assert.NotNull(_lastHarness);
        Assert.Equal(WgcContinuousTargetKind.Window, _lastHarness!.Options.TargetKind);
        Assert.Equal((nint)0x1234, _lastHarness.Options.WindowHandle);
        var args = _lastHarness.Process.CapturedArguments!;
        Assert.Equal("--capture-continuous-window", args[0]);
        Assert.Equal("--window-hwnd", args[1]);
        Assert.Equal("0x1234", args[2]);
        Assert.DoesNotContain("--display-bounds", args);

        await process.BeginSignalObservedTask.WaitAsync(TimeSpan.FromSeconds(5));
        backend.Dispose();
    }

    [Fact]
    public void Start_Region_PassesDisplayAndRegionBoundsInCanonicalOrder()
    {
        FakeSession? fake = null;
        var backend = CreateBackend(options => fake = new FakeSession(options), out _, out _);
        var cfg = CreateRegionConfig(Path.Combine(_finalDir, "region-args.mp4"));

        backend.Start(cfg);

        Assert.NotNull(fake);
        Assert.StartsWith("--capture-continuous-region", cfg.CommandArgs, StringComparison.Ordinal);
        Assert.Contains("--display-bounds", cfg.CommandArgs, StringComparison.Ordinal);
        Assert.Contains("-1920,-200,1920,1080", cfg.CommandArgs, StringComparison.Ordinal);
        Assert.Contains("--region-bounds", cfg.CommandArgs, StringComparison.Ordinal);
        Assert.Contains("-1800,-100,640,480", cfg.CommandArgs, StringComparison.Ordinal);
        Assert.Equal(WgcContinuousTargetKind.Region, fake!.Options.TargetKind);
        Assert.Equal((-1920, -200, 1920, 1080),
            (fake.Options.DisplayX, fake.Options.DisplayY, fake.Options.DisplayWidth, fake.Options.DisplayHeight));
        Assert.Equal((-1800, -100, 640, 480),
            (fake.Options.RegionX, fake.Options.RegionY, fake.Options.RegionWidth, fake.Options.RegionHeight));

        backend.Dispose();
    }

    [Fact]
    public void Start_RegionInvalidOddBounds_FailsBeforeHelperAndLeavesNoOutput()
    {
        string outputPath = Path.Combine(_finalDir, "region-invalid.mp4");
        var process = new FakeWgcContinuousProcess(Array.Empty<string>());
        var backend = CreateBackend(process, out _, out _);
        var cfg = CreateRegionConfig(outputPath);
        cfg.Bounds = (-1800, -100, 641, 480);

        Assert.Throws<ApiException>(() => backend.Start(cfg));
        Assert.Equal(0, process.StartInvocationCount);
        Assert.False(File.Exists(outputPath));
    }

    [Fact]
    public async Task RegionTerminalDimensionMismatch_FailsWithoutPublishingFinalMp4()
    {
        string outputPath = Path.Combine(_finalDir, "region-terminal-mismatch.mp4");
        var process = new FakeWgcContinuousProcess(
            RegionStarted("r-region-mismatch", "ignored")
                .Concat(RegionOk(800, 600))
                .ToArray(),
            createOutputFile: true,
            outputFileSize: 1024);
        var backend = CreateBackend(process, out var publisher, out _);
        var cfg = CreateRegionConfig(outputPath, deferCaptureStart: false);

        backend.Start(cfg);
        await WaitForConditionAsync(
            () => _lastHarness?.Session.IsCompleted == true,
            TimeSpan.FromSeconds(5),
            "Region helper mismatch did not reach a terminal state.");
        backend.Stop();

        Assert.Equal(0, publisher.CallCount);
        Assert.False(File.Exists(outputPath));
    }

    [Fact]
    public void Start_WindowWithoutHwnd_RejectedBeforeHelperStart()
    {
        var process = new FakeWgcContinuousProcess(Array.Empty<string>());
        var backend = CreateBackend(process, out _, out _);

        var cfg = CreateValidConfig();
        cfg.SourceKind = "window";

        var ex = Assert.Throws<ApiException>(() => backend.Start(cfg));
        Assert.Equal(400, ex.Status);
        Assert.Equal("INVALID_ARGUMENT", ex.Code);
        Assert.Contains("HWND", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, process.StartInvocationCount);
    }

    [Fact]
    public void Start_InvalidSourceKind_Region_RejectedBeforeHelperStart()
    {
        var process = new FakeWgcContinuousProcess(Array.Empty<string>());
        var backend = CreateBackend(process, out _, out _);

        var cfg = CreateValidConfig();
        cfg.SourceKind = "region";

        var ex = Assert.Throws<ApiException>(() => backend.Start(cfg));
        Assert.Equal(400, ex.Status);
        Assert.Equal("INVALID_ARGUMENT", ex.Code);
        Assert.Equal(0, process.StartInvocationCount);
    }

    [Fact]
    public void Start_MissingDuration_RejectedBeforeHelperStart()
    {
        var process = new FakeWgcContinuousProcess(Array.Empty<string>());
        var backend = CreateBackend(process, out _, out _);

        var cfg = CreateValidConfig();
        cfg.DurationSeconds = null;

        var ex = Assert.Throws<ApiException>(() => backend.Start(cfg));
        Assert.Equal(400, ex.Status);
        Assert.Equal("INVALID_ARGUMENT", ex.Code);
        Assert.Contains("DurationSeconds", ex.Message);
        Assert.Equal(0, process.StartInvocationCount);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(11)]
    [InlineData(-1)]
    public void Start_DurationOutOfRange_RejectedBeforeHelperStart(int duration)
    {
        var process = new FakeWgcContinuousProcess(Array.Empty<string>());
        var backend = CreateBackend(process, out _, out _);

        var cfg = CreateValidConfig(durationSeconds: duration);

        var ex = Assert.Throws<ApiException>(() => backend.Start(cfg));
        Assert.Equal(400, ex.Status);
        Assert.Equal("INVALID_ARGUMENT", ex.Code);
        Assert.Contains("1 and 10", ex.Message);
        Assert.Equal(0, process.StartInvocationCount);
    }

    [Fact]
    public void Start_MicrophoneRequested_RejectedBeforeHelperStart()
    {
        var process = new FakeWgcContinuousProcess(Array.Empty<string>());
        var backend = CreateBackend(process, out _, out _);

        var cfg = CreateValidConfig();
        cfg.Microphone = true;

        var ex = Assert.Throws<ApiException>(() => backend.Start(cfg));
        Assert.Equal(400, ex.Status);
        Assert.Equal("UNSUPPORTED_FEATURE", ex.Code);
        Assert.Contains("microphone", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, process.StartInvocationCount);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("relative.mp4")]
    [InlineData(@"C:\some\output.avi")]
    public void Start_InvalidOutputPath_RejectedBeforeHelperStart(string? outputPath)
    {
        var process = new FakeWgcContinuousProcess(Array.Empty<string>());
        var backend = CreateBackend(process, out _, out _);

        var cfg = CreateValidConfig();
        cfg.OutputPath = outputPath ?? string.Empty;

        var ex = Assert.Throws<ApiException>(() => backend.Start(cfg));
        Assert.Equal(400, ex.Status);
        Assert.Equal("INVALID_ARGUMENT", ex.Code);
        Assert.Equal(0, process.StartInvocationCount);
    }

    // -----------------------------------------------------------------
    // 4. cfg.CommandArgs 不包含真实 begin token
    // -----------------------------------------------------------------

    [Fact]
    public void Start_CommandArgs_RedactsBeginToken()
    {
        string outputPath = Path.Combine(_finalDir, "redact.mp4");
        CreatePlaceholderMp4(outputPath, 1024);
        var process = new FakeWgcContinuousProcess(
            initialStdout: Started("r-redact", outputPath).Concat(Ok()).ToArray(),
            createOutputFile: true,
            outputFilePath: outputPath,
            outputFileSize: 1024);

        var backend = CreateBackend(process, out _, out _);
        var cfg = CreateValidConfig(outputPath);
        backend.Start(cfg);

        Assert.NotNull(_lastHarness);
        string token = _lastHarness!.Options.BeginToken;
        Assert.False(string.IsNullOrEmpty(token), "Begin token should be generated.");

        string commandArgs = cfg.CommandArgs;
        _output.WriteLine("CommandArgs: " + commandArgs);

        Assert.DoesNotContain(token, commandArgs, StringComparison.Ordinal);
        Assert.Contains("<redacted>", commandArgs, StringComparison.Ordinal);
        Assert.Contains("--begin-token", commandArgs, StringComparison.Ordinal);

        backend.Dispose();
    }

    // -----------------------------------------------------------------
    // 5. first-frame exactly once 转发，observer 异常隔离
    // -----------------------------------------------------------------

    [Fact]
    public async Task FirstFrame_ForwardedExactlyOnce_AndObserverExceptionIsolated()
    {
        string outputPath = Path.Combine(_finalDir, "firstframe.mp4");
        CreatePlaceholderMp4(outputPath, 1024);
        var process = new FakeWgcContinuousProcess(
            initialStdout: Started("r-ff", outputPath)
                .Concat(Progress(1, 33, 1024))
                .Concat(Ok(fileSize: 1024))
                .ToArray(),
            createOutputFile: true,
            outputFilePath: outputPath,
            outputFileSize: 1024);

        var backend = CreateBackend(process, out _, out _);

        int observedCount = 0;
        int throwingCount = 0;
        backend.FirstFrameObserved += _ => Interlocked.Increment(ref observedCount);
        backend.FirstFrameObserved += _ =>
        {
            Interlocked.Increment(ref throwingCount);
            throw new InvalidOperationException("Observer must not affect flow.");
        };

        var tcs = new TaskCompletionSource();
        backend.OnNaturalExit((_, _) => tcs.TrySetResult());

        backend.Start(CreateValidConfig(outputPath));
        await tcs.Task.WaitAsync(TimeSpan.FromSeconds(15));

        Assert.Equal(1, observedCount);
        Assert.Equal(1, throwingCount);

        var meta = backend.Stop();
        Assert.True(meta.OutputFileExists);
    }

    // -----------------------------------------------------------------
    // 6. Success staging 文件原子发布到最终路径，并由 probe 生成正确 OutputMeta
    // -----------------------------------------------------------------

    [Fact]
    public async Task Success_AtomicPublishAndProbe_OutputMetaCorrect()
    {
        string outputPath = Path.Combine(_finalDir, "success.mp4");
        string recordingId = "r-success";
        long stagingSize = 10000;
        CreatePlaceholderMp4(outputPath, 1); // final dir exists; will be replaced
        string stagingPath;

        var process = new FakeWgcContinuousProcess(
            initialStdout: Started(recordingId, outputPath).Concat(Ok(fileSize: stagingSize)).ToArray(),
            createOutputFile: true,
            outputFilePath: outputPath, // ignored: publisher writes real file
            outputFileSize: stagingSize);

        var backend = CreateBackend(process, out var publisher, out var probe);
        var cfg = CreateValidConfig(outputPath);

        var tcs = new TaskCompletionSource();
        backend.OnNaturalExit((_, _) => tcs.TrySetResult());

        backend.Start(cfg);
        await tcs.Task.WaitAsync(TimeSpan.FromSeconds(15));

        Assert.NotNull(_lastHarness);
        stagingPath = _lastHarness!.Options.OutputPath;

        Assert.Equal(1, publisher.CallCount);
        Assert.Equal(stagingPath, publisher.Calls[0].Staging);
        Assert.Equal(outputPath, publisher.Calls[0].Final);

        // Authenticity checks run against the staging file before publishing.
        Assert.Equal(1, probe.CallCount);
        Assert.Equal(stagingPath, probe.Calls[0]);

        var meta = backend.Stop();
        Assert.Equal(outputPath, meta.OutputPath);
        Assert.Equal("mp4", meta.Container);
        Assert.Equal("h264", meta.Codec);
        Assert.Equal("WGC_D3D11_FRAME_STREAM", meta.CaptureMethod);
        Assert.Equal("Success", meta.Stage);
        Assert.Equal("not_requested", meta.AudioStatus);
        Assert.True(meta.OutputFileExists);
        Assert.True(meta.SizeBytes > 512);
        Assert.Equal(0, backend.ExitCode);
    }

    // -----------------------------------------------------------------
    // 7. graceful Stopped 也发布完整合法 MP4
    // -----------------------------------------------------------------

    [Fact]
    public async Task Stopped_AtomicPublishAndProbe_OutputMetaCorrect()
    {
        string outputPath = Path.Combine(_finalDir, "stopped.mp4");
        long stagingSize = 10000;
        string recordingId = "r-stopped";

        var process = new FakeWgcContinuousProcess(
            initialStdout: Started(recordingId, outputPath).ToArray(),
            finalStdout: Stopped(durationMs: 5000, fileSize: stagingSize),
            createOutputFile: true,
            outputFilePath: outputPath,
            outputFileSize: stagingSize);

        var backend = CreateBackend(process, out var publisher, out var probe);
        var cfg = CreateValidConfig(outputPath);

        backend.Start(cfg);

        // Wait for the process to be waiting on the stop signal, then stop.
        await WaitForConditionAsync(() => File.Exists(_lastHarness!.Options.BeginSignalPath), TimeSpan.FromSeconds(5));

        var meta = backend.Stop();

        Assert.Equal(1, publisher.CallCount);
        Assert.Equal(1, probe.CallCount);
        Assert.Equal(_lastHarness!.Options.OutputPath, probe.Calls[0]);
        Assert.Equal(outputPath, meta.OutputPath);
        Assert.Equal("mp4", meta.Container);
        Assert.Equal("h264", meta.Codec);
        Assert.Equal("Stopped", meta.Stage);
        Assert.True(meta.OutputFileExists);
        Assert.True(meta.SizeBytes > 512);
    }

    // -----------------------------------------------------------------
    // 8. copy/flush/move/probe 失败均形成 Failed，清理 tmp/staging，旧 final 不被破坏
    // -----------------------------------------------------------------

    [Theory]
    [InlineData("publish_failed")]
    [InlineData("probe_exception")]
    public void PublishOrProbeFailure_Failed_CleansStaging_PreservesExistingFinal(string failureMode)
    {
        string outputPath = Path.Combine(_finalDir, "preserve.mp4");
        byte[] existingContent = Encoding.UTF8.GetBytes("existing-final-content");
        File.WriteAllBytes(outputPath, existingContent);

        long stagingSize = 10000;
        var process = new FakeWgcContinuousProcess(
            initialStdout: Started("r-fail", outputPath).Concat(Ok(fileSize: stagingSize)).ToArray(),
            createOutputFile: true,
            outputFilePath: outputPath,
            outputFileSize: stagingSize);

        var backend = CreateBackend(process, out var publisher, out var probe);

        if (failureMode == "publish_failed")
        {
            publisher.OnPublish = (_, _, _) => new PublishResult
            {
                Success = false,
                FailureCategory = "size_mismatch"
            };
        }
        else
        {
            // Probe runs before publish, so the publisher must not be invoked.
            probe.OnProbe = _ => throw new InvalidOperationException("probe failure");
        }

        var cfg = CreateValidConfig(outputPath);
        backend.Start(cfg);

        var meta = backend.Stop();

        Assert.False(meta.OutputFileExists);
        Assert.NotEmpty(meta.Warnings);
        Assert.Contains(failureMode == "publish_failed" ? "publish_failed" : "probe_exception",
            string.Join(" ", meta.Warnings), StringComparison.Ordinal);

        // Existing final file must be untouched.
        Assert.True(File.Exists(outputPath));
        Assert.Equal(existingContent, File.ReadAllBytes(outputPath));

        if (failureMode == "probe_exception")
        {
            // Probe is the first authenticity gate; publisher is never called.
            Assert.Equal(0, publisher.CallCount);
        }

        // Staging directory must be removed.
        string stagingDir = Directory.GetParent(_lastHarness!.Options.OutputPath)!.FullName;
        Assert.False(Directory.Exists(stagingDir), "Staging directory should be cleaned up.");

        // No publish tmp residue in final dir.
        Assert.DoesNotContain(Directory.GetFiles(_finalDir), f => Path.GetFileName(f).Contains(".publish-tmp-"));

        Assert.NotEqual(0, backend.ExitCode);
    }

    // -----------------------------------------------------------------
    // 9. helper Failed/Cancelled/malformed 不发布最终文件
    // -----------------------------------------------------------------

    [Theory]
    [InlineData("fail")]
    [InlineData("cancel")]
    [InlineData("malformed")]
    public void Helper_Failed_Cancelled_Malformed_DoesNotPublish(string mode)
    {
        string outputPath = Path.Combine(_finalDir, $"{mode}.mp4");
        FakeWgcContinuousProcess process;

        if (mode == "fail")
        {
            process = new FakeWgcContinuousProcess(
                initialStdout: Fail("encoding_error", "0x80004005"),
                exitCode: 1);
        }
        else if (mode == "malformed")
        {
            process = new FakeWgcContinuousProcess(
                initialStdout: Malformed(),
                exitCode: 0);
        }
        else
        {
            process = new FakeWgcContinuousProcess(
                initialStdout: Started("r-cancel", outputPath).ToArray(),
                ignoreStopSignal: true);
        }

        var backend = CreateBackend(process, out var publisher, out _);
        var cfg = CreateValidConfig(outputPath);
        backend.Start(cfg);

        if (mode == "cancel")
        {
            // Give authorization a moment, then dispose.
            Thread.Sleep(100);
            backend.Dispose();
        }

        var meta = backend.Stop();

        Assert.False(File.Exists(outputPath));
        Assert.Equal(0, publisher.CallCount);
        Assert.False(meta.OutputFileExists);
        Assert.Contains("wgc_continuous", string.Join(" ", meta.Warnings), StringComparison.Ordinal);
        Assert.NotEqual(0, backend.ExitCode);
    }

    [Theory]
    [InlineData("window_closed")]
    [InlineData("window_minimized")]
    [InlineData("size_changed")]
    public void HelperLifecycleFailure_OutputMetaKeepsSpecificStopReason(string reason)
    {
        string outputPath = Path.Combine(_finalDir, $"{reason}.mp4");
        var process = new FakeWgcContinuousProcess(
            initialStdout: Fail(reason, reason),
            exitCode: 1);

        var backend = CreateBackend(process, out var publisher, out _);
        backend.Start(CreateValidConfig(outputPath));
        var meta = backend.Stop();

        Assert.Equal(reason, meta.StopReason);
        Assert.Contains($"wgc_continuous_{reason}", string.Join(" ", meta.Warnings));
        Assert.False(meta.OutputFileExists);
        Assert.Equal(0, publisher.CallCount);
        Assert.False(File.Exists(outputPath));
    }

    // -----------------------------------------------------------------
    // 10. Stop 与自然退出竞态：同一 meta、一次 publish、一次 callback
    // -----------------------------------------------------------------

    [Fact]
    public async Task Stop_NaturalExitRace_SameMeta_OnePublish_OneCallback()
    {
        string outputPath = Path.Combine(_finalDir, "race.mp4");
        long stagingSize = 10000;
        var process = new FakeWgcContinuousProcess(
            initialStdout: Started("r-race", outputPath).Concat(Ok(fileSize: stagingSize)).ToArray(),
            createOutputFile: true,
            outputFilePath: outputPath,
            outputFileSize: stagingSize,
            initialDelay: TimeSpan.FromMilliseconds(50));

        var backend = CreateBackend(process, out var publisher, out _);
        var cfg = CreateValidConfig(outputPath);

        int callbackCount = 0;
        OutputMeta? callbackMeta = null;
        backend.OnNaturalExit((ec, m) =>
        {
            Interlocked.Increment(ref callbackCount);
            callbackMeta = m;
        });

        backend.Start(cfg);

        // Race Stop against the natural completion.
        var stopTask = Task.Run(() => backend.Stop());
        var stopMeta = await stopTask.WaitAsync(TimeSpan.FromSeconds(15));

        Assert.Equal(1, publisher.CallCount);
        Assert.True(callbackCount <= 1, "Callback must fire at most once.");
        if (callbackMeta != null)
        {
            Assert.Equal(stopMeta.OutputPath, callbackMeta.OutputPath);
            Assert.Equal(stopMeta.SizeBytes, callbackMeta.SizeBytes);
        }
    }

    // -----------------------------------------------------------------
    // 11. 多次 Stop/Dispose 幂等且不残留资源
    // -----------------------------------------------------------------

    [Fact]
    public void MultipleStop_Dispose_Idempotent_NoResidue()
    {
        string outputPath = Path.Combine(_finalDir, "idempotent.mp4");
        long stagingSize = 10000;
        var process = new FakeWgcContinuousProcess(
            initialStdout: Started("r-idem", outputPath).Concat(Ok(fileSize: stagingSize)).ToArray(),
            createOutputFile: true,
            outputFilePath: outputPath,
            outputFileSize: stagingSize);

        var backend = CreateBackend(process, out var publisher, out var probe);
        var cfg = CreateValidConfig(outputPath);

        int callbackCount = 0;
        backend.OnNaturalExit((_, _) => Interlocked.Increment(ref callbackCount));

        backend.Start(cfg);

        var meta1 = backend.Stop();
        var meta2 = backend.Stop();
        var meta3 = backend.Stop();

        backend.Dispose();
        backend.Dispose();

        Assert.Same(meta1, meta2);
        Assert.Same(meta2, meta3);
        Assert.Equal(1, publisher.CallCount);
        Assert.Equal(1, probe.CallCount);
        Assert.True(callbackCount <= 1, "Natural-exit callback must fire at most once.");

        // No staging residue.
        string stagingDir = Directory.GetParent(_lastHarness!.Options.OutputPath)!.FullName;
        Assert.False(Directory.Exists(stagingDir), "Staging directory should be removed.");

        // No signal files.
        Assert.False(File.Exists(_lastHarness.Options.BeginSignalPath));
        Assert.False(File.Exists(_lastHarness.Options.StopSignalPath));

        // No publish tmp in final dir.
        Assert.DoesNotContain(Directory.GetFiles(_finalDir), f => Path.GetFileName(f).Contains(".publish-tmp-"));
    }

    // -----------------------------------------------------------------
    // 12. callback/first-frame observer 抛异常不影响终态
    // -----------------------------------------------------------------

    [Fact]
    public void CallbackObserverException_DoesNotCorruptState()
    {
        string outputPath = Path.Combine(_finalDir, "cb-exception.mp4");
        long stagingSize = 10000;
        var process = new FakeWgcContinuousProcess(
            initialStdout: Started("r-cbex", outputPath).Concat(Ok(fileSize: stagingSize)).ToArray(),
            createOutputFile: true,
            outputFilePath: outputPath,
            outputFileSize: stagingSize);

        var backend = CreateBackend(process, out _, out _);
        backend.OnNaturalExit((_, _) => throw new InvalidOperationException("callback exception"));

        var cfg = CreateValidConfig(outputPath);
        backend.Start(cfg);

        var meta = backend.Stop();

        Assert.True(meta.OutputFileExists);
        Assert.True(meta.SizeBytes > 512);
        Assert.Equal(0, backend.ExitCode);
    }

    // -----------------------------------------------------------------
    // 13. begin token 不出现在 command args、warnings、stderr、异常和测试输出
    // -----------------------------------------------------------------

    [Fact]
    public void BeginToken_NotLeakedAnywhere()
    {
        string outputPath = Path.Combine(_finalDir, "token-leak.mp4");
        long stagingSize = 10000;
        string stderrToken = "stderr-line-with-token";
        var process = new FakeWgcContinuousProcess(
            initialStdout: Started("r-tok", outputPath).Concat(Ok(fileSize: stagingSize)).ToArray(),
            stderr: new[] { stderrToken },
            createOutputFile: true,
            outputFilePath: outputPath,
            outputFileSize: stagingSize);

        var backend = CreateBackend(process, out _, out _);
        var cfg = CreateValidConfig(outputPath);
        backend.Start(cfg);

        var meta = backend.Stop();
        Assert.NotNull(_lastHarness);
        string token = _lastHarness!.Options.BeginToken;

        Assert.DoesNotContain(token, cfg.CommandArgs, StringComparison.Ordinal);
        Assert.DoesNotContain(token, string.Join(" ", meta.Warnings ?? Array.Empty<string>()), StringComparison.Ordinal);
        Assert.DoesNotContain(token, meta.StderrLog ?? string.Empty, StringComparison.Ordinal);

        // The helper stderr we injected deliberately does not contain the token;
        // assert it is bounded and present but not equal to token.
        Assert.Equal(stderrToken, meta.StderrLog?.TrimEnd());
    }

    // -----------------------------------------------------------------
    // 14. CaptureBackendSelector.Select(new CaptureConfig { SourceKind = "display" }) 仍返回 FFmpeg
    // -----------------------------------------------------------------

    [Fact]
    public void CaptureBackendSelector_Display_StillReturnsFfmpeg()
    {
        Environment.SetEnvironmentVariable("AGENT_RECORDER_WINDOW_BACKEND", null);
        var (backend, type) = CaptureBackendSelector.Select(new CaptureConfig { SourceKind = "display" });
        Assert.Equal("ffmpeg", type);
        Assert.IsType<FfmpegCaptureBackend>(backend);
    }

    // -----------------------------------------------------------------
    // Task 178B: deterministic Start/Completion/Dispose race tests
    // -----------------------------------------------------------------

    [Fact]
    public async Task StartAsync_CompletesSynchronously_BeforeReturn_ProcessesExactlyOnce()
    {
        string outputPath = Path.Combine(_finalDir, "sync-complete.mp4");
        FakeSession? session = null;
        var backend = CreateBackend(options =>
        {
            Directory.CreateDirectory(Path.GetDirectoryName(options.OutputPath)!);
            CreatePlaceholderMp4(options.OutputPath, 10000);
            session = new FakeSession(options) { StartMode = FakeSessionStartMode.SynchronousCompletion };
            return session;
        }, out var publisher, out var probe);

        var cfg = CreateValidConfig(outputPath);
        var tcs = new TaskCompletionSource();
        backend.OnNaturalExit((_, _) => tcs.TrySetResult());

        backend.Start(cfg);

        Assert.NotNull(session);
        Assert.Equal(1, session!.StartCallCount);
        await tcs.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var meta = backend.Stop();
        Assert.True(meta.OutputFileExists);
        Assert.Equal(1, publisher.CallCount);
        Assert.Equal(1, probe.CallCount);
        Assert.Equal(0, backend.ExitCode);
    }

    [Fact]
    public void StartAsync_ThrowsSynchronously_RollbacksAndFails()
    {
        string outputPath = Path.Combine(_finalDir, "sync-throw.mp4");
        FakeSession? session = null;
        var backend = CreateBackend(options =>
        {
            session = new FakeSession(options) { StartMode = FakeSessionStartMode.SynchronousThrow };
            return session;
        }, out var publisher, out _);

        var cfg = CreateValidConfig(outputPath);
        var ex = Assert.Throws<InvalidOperationException>(() => backend.Start(cfg));
        Assert.Contains("StartAsync synchronous failure", ex.Message);

        Assert.NotNull(session);
        Assert.True(session!.Disposed);
        Assert.Equal(0, publisher.CallCount);

        var meta = backend.Stop();
        Assert.False(meta.OutputFileExists);
        Assert.Contains("wgc_continuous_start_failed", string.Join(" ", meta.Warnings));
        Assert.False(Directory.Exists(Path.GetDirectoryName(session.Options.OutputPath)!));
    }

    [Fact]
    public void StartAsync_ReturnsFaultedTask_RollbacksAndFails()
    {
        string outputPath = Path.Combine(_finalDir, "faulted-task.mp4");
        FakeSession? session = null;
        var backend = CreateBackend(options =>
        {
            session = new FakeSession(options) { StartMode = FakeSessionStartMode.FaultedTask };
            return session;
        }, out var publisher, out _);

        var cfg = CreateValidConfig(outputPath);
        var ex = Assert.Throws<InvalidOperationException>(() => backend.Start(cfg));
        Assert.Contains("StartAsync faulted task", ex.Message);

        Assert.NotNull(session);
        Assert.True(session!.Disposed);
        Assert.Equal(0, publisher.CallCount);

        var meta = backend.Stop();
        Assert.False(meta.OutputFileExists);
        Assert.Contains("wgc_continuous_start_failed", string.Join(" ", meta.Warnings));
        Assert.False(Directory.Exists(Path.GetDirectoryName(session.Options.OutputPath)!));
    }

    [Fact]
    public void SecondStart_DoesNotInvokeResolverFactoryOrChangeCommandArgs()
    {
        string outputPath = Path.Combine(_finalDir, "second-start.mp4");
        CreatePlaceholderMp4(outputPath, 1024);
        var process = new FakeWgcContinuousProcess(
            initialStdout: Started("r-second", outputPath).Concat(Ok(fileSize: 1024)).ToArray(),
            createOutputFile: true,
            outputFilePath: outputPath,
            outputFileSize: 1024);

        int resolverCalls = 0;
        int factoryCalls = 0;
        var publisher = new FakePublisher();
        var probe = new FakeProbe();
        var backend = new WgcContinuousCaptureBackend(
            options =>
            {
                factoryCalls++;
                File.WriteAllText(options.HelperExePath, "fake");
                process.WaitForBeginSignalPath = options.BeginSignalPath;
                process.AutoContinueOnStopSignalPath = options.StopSignalPath;
                process.OutputFilePath = options.OutputPath;
                var harness = new SessionHarness(options, process);
                _lastHarness = harness;
                // The backend owns the session lifecycle; do not add the harness
                // to the disposables list so that tests cannot get a false pass
                // from a separate Dispose.
                return harness.Session;
            },
            publisher,
            probe.Probe,
            () => { resolverCalls++; return Path.Combine(_tempDir, "fake-helper.exe"); },
            _tempDir);
        _disposables.Add(backend);

        var cfg = CreateValidConfig(outputPath);
        backend.Start(cfg);
        string originalArgs = cfg.CommandArgs;
        backend.Stop();

        Assert.Equal(1, resolverCalls);
        Assert.Equal(1, factoryCalls);

        Assert.Throws<ObjectDisposedException>(() => backend.Start(cfg));
        Assert.Equal(1, resolverCalls);
        Assert.Equal(1, factoryCalls);
        Assert.Equal(originalArgs, cfg.CommandArgs);
    }

    [Fact]
    public void DisposeBeforeStart_NoSideEffects()
    {
        int resolverCalls = 0;
        int factoryCalls = 0;
        var backend = CreateBackend(
            _ => { factoryCalls++; return new FakeSession(_) { StartMode = FakeSessionStartMode.SynchronousCompletion }; },
            out _,
            out _,
            () => { resolverCalls++; return Path.Combine(_tempDir, "helper.exe"); });

        backend.Dispose();

        Assert.Equal(0, resolverCalls);
        Assert.Equal(0, factoryCalls);

        var meta = backend.Stop();
        Assert.False(meta.OutputFileExists);
        Assert.DoesNotContain("wgc-continuous", Directory.GetDirectories(_tempDir).Select(Path.GetFileName));
    }

    [Fact]
    public void ResolverThrows_Rollbacks_NoStaging()
    {
        int resolverCalls = 0;
        var backend = CreateBackend(
            options => new FakeSession(options),
            out _,
            out _,
            () => { resolverCalls++; throw new IOException("resolver failed"); });

        var cfg = CreateValidConfig();
        var ex = Assert.Throws<IOException>(() => backend.Start(cfg));
        Assert.Equal(1, resolverCalls);
        Assert.Contains("resolver failed", ex.Message);

        var meta = backend.Stop();
        Assert.False(meta.OutputFileExists);
        Assert.Contains("wgc_continuous_start_failed", string.Join(" ", meta.Warnings));
        Assert.DoesNotContain("wgc-continuous", Directory.GetDirectories(_tempDir).Select(Path.GetFileName));
    }

    [Fact]
    public void SessionFactoryThrows_Rollbacks_CleansStaging()
    {
        int factoryCalls = 0;
        string? capturedStagingDir = null;
        var backend = CreateBackend(
            options =>
            {
                factoryCalls++;
                capturedStagingDir = Path.GetDirectoryName(options.OutputPath);
                throw new InvalidOperationException("factory failed");
            },
            out _,
            out _);

        var cfg = CreateValidConfig();
        var ex = Assert.Throws<InvalidOperationException>(() => backend.Start(cfg));
        Assert.Equal(1, factoryCalls);
        Assert.Contains("factory failed", ex.Message);

        Assert.NotNull(capturedStagingDir);
        Assert.False(Directory.Exists(capturedStagingDir!));

        var meta = backend.Stop();
        Assert.False(meta.OutputFileExists);
        Assert.Contains("wgc_continuous_start_failed", string.Join(" ", meta.Warnings));
    }

    [Fact]
    public async Task DisposeDuringStarting_ResolverBlocked_RollbacksWithoutPublishing()
    {
        var startBlocked = new ManualResetEventSlim(false);
        var disposeStarting = new ManualResetEventSlim(false);
        FakeSession? session = null;
        int resolverCalls = 0;
        int factoryCalls = 0;

        var backend = CreateBackend(
            options =>
            {
                factoryCalls++;
                session = new FakeSession(options);
                return session;
            },
            out var publisher,
            out _,
            () =>
            {
                resolverCalls++;
                startBlocked.Set();
                disposeStarting.Wait();
                return Path.Combine(_tempDir, "helper.exe");
            });
        backend.OnDisposeStartingWaitForTests = () => disposeStarting.Set();

        var cfg = CreateValidConfig();
        var startTask = Task.Run(() => backend.Start(cfg));
        Assert.True(startBlocked.Wait(TimeSpan.FromSeconds(5)), "Resolver should enter.");

        var disposeTask = Task.Run(() => backend.Dispose());
        await disposeTask.WaitAsync(TimeSpan.FromSeconds(5));
        var startEx = await Assert.ThrowsAsync<ObjectDisposedException>(() => startTask);
        Assert.NotNull(startEx);

        Assert.Equal(1, resolverCalls);
        Assert.Equal(1, factoryCalls);
        Assert.NotNull(session);
        Assert.True(session!.Disposed);
        Assert.Equal(0, session.StartCallCount);
        Assert.Equal(0, publisher.CallCount);
        Assert.DoesNotContain("wgc-continuous", Directory.GetDirectories(_tempDir).Select(Path.GetFileName));
    }

    [Fact]
    public async Task DisposeDuringStarting_FactoryBlocked_RollbacksAndDisposesSession()
    {
        var factoryBlocked = new ManualResetEventSlim(false);
        var disposeStarting = new ManualResetEventSlim(false);
        FakeSession? session = null;
        int factoryCalls = 0;

        var backend = CreateBackend(
            options =>
            {
                factoryCalls++;
                factoryBlocked.Set();
                disposeStarting.Wait();
                session = new FakeSession(options);
                return session;
            },
            out var publisher,
            out _);
        backend.OnDisposeStartingWaitForTests = () => disposeStarting.Set();

        var cfg = CreateValidConfig();
        var startTask = Task.Run(() => backend.Start(cfg));
        Assert.True(factoryBlocked.Wait(TimeSpan.FromSeconds(5)), "Factory should enter.");

        var disposeTask = Task.Run(() => backend.Dispose());
        await disposeTask.WaitAsync(TimeSpan.FromSeconds(5));
        var startEx = await Assert.ThrowsAsync<ObjectDisposedException>(() => startTask);
        Assert.NotNull(startEx);

        Assert.Equal(1, factoryCalls);
        Assert.NotNull(session);
        Assert.True(session!.Disposed);
        Assert.Equal(0, session.StartCallCount);
        Assert.Equal(0, publisher.CallCount);
        Assert.DoesNotContain("wgc-continuous", Directory.GetDirectories(_tempDir).Select(Path.GetFileName));
    }

    [Fact]
    public void DisposeDuringRunning_WinsRace_CompletesWithoutPublishingOrCallback()
    {
        string outputPath = Path.Combine(_finalDir, "dispose-running.mp4");
        FakeSession? session = null;
        var backend = CreateBackend(options =>
        {
            Directory.CreateDirectory(Path.GetDirectoryName(options.OutputPath)!);
            CreatePlaceholderMp4(options.OutputPath, 10000);
            session = new FakeSession(options);
            return session;
        }, out var publisher, out _);

        int callbackCount = 0;
        backend.OnNaturalExit((_, _) => Interlocked.Increment(ref callbackCount));

        var cfg = CreateValidConfig(outputPath);
        backend.Start(cfg);

        Assert.NotNull(session);
        backend.Dispose();

        var meta = backend.Stop();
        Assert.False(meta.OutputFileExists);
        Assert.Equal(0, publisher.CallCount);
        Assert.Equal(0, callbackCount);
        Assert.True(session!.Disposed);
        Assert.False(Directory.Exists(Path.GetDirectoryName(session.Options.OutputPath)!));
    }

    [Fact]
    public async Task DisposeDuringCompleting_WhileProbeBlocked_WaitsForProbeThenCleans()
    {
        string outputPath = Path.Combine(_finalDir, "dispose-probe.mp4");
        var probeEntered = new ManualResetEventSlim(false);
        var probeRelease = new ManualResetEventSlim(false);
        var completingEntered = new ManualResetEventSlim(false);
        var disposeEntered = new ManualResetEventSlim(false);
        FakeSession? session = null;

        var backend = CreateBackend(options =>
        {
            Directory.CreateDirectory(Path.GetDirectoryName(options.OutputPath)!);
            CreatePlaceholderMp4(options.OutputPath, 10000);
            session = new FakeSession(options);
            return session;
        }, out var publisher, out var probe);

        probe.OnProbe = _ =>
        {
            probeEntered.Set();
            probeRelease.Wait();
            return new OutputMeta
            {
                Container = "mp4",
                Codec = "h264",
                Width = 1920,
                Height = 1080,
                Fps = 30,
                DurationSeconds = 5,
                SizeBytes = 10000
            };
        };
        backend.OnCompletingForTests = () => completingEntered.Set();
        backend.OnDisposeCompletingWaitForTests = () => disposeEntered.Set();

        var cfg = CreateValidConfig(outputPath);
        backend.Start(cfg);
        Assert.NotNull(session);

        _ = Task.Run(() => session!.CompletionTcs.TrySetResult(session.DefaultResult));

        Assert.True(completingEntered.Wait(TimeSpan.FromSeconds(5)), "Should enter Completing.");
        Assert.True(probeEntered.Wait(TimeSpan.FromSeconds(5)), "Probe should be entered.");

        var disposeTask = Task.Run(() => backend.Dispose());
        Assert.True(disposeEntered.Wait(TimeSpan.FromSeconds(5)), "Dispose should observe Completing.");

        probeRelease.Set();
        await disposeTask.WaitAsync(TimeSpan.FromSeconds(5));

        var meta = backend.Stop();
        Assert.True(meta.OutputFileExists);
        Assert.Equal(1, publisher.CallCount);
        Assert.False(Directory.Exists(Path.GetDirectoryName(session!.Options.OutputPath)!));
    }

    [Fact]
    public async Task DisposeDuringCompleting_WhilePublisherCopyBlocked_WaitsForCopyThenCleans()
    {
        string outputPath = Path.Combine(_finalDir, "dispose-publish.mp4");
        var copyEntered = new ManualResetEventSlim(false);
        var copyRelease = new ManualResetEventSlim(false);
        var completingEntered = new ManualResetEventSlim(false);
        var disposeEntered = new ManualResetEventSlim(false);
        FakeSession? session = null;

        var backend = CreateBackend(options =>
        {
            Directory.CreateDirectory(Path.GetDirectoryName(options.OutputPath)!);
            CreatePlaceholderMp4(options.OutputPath, 10000);
            session = new FakeSession(options);
            return session;
        }, out var publisher, out _);

        publisher.OnPublish = (staging, final, ct) =>
        {
            copyEntered.Set();
            copyRelease.Wait(ct);
            Directory.CreateDirectory(Path.GetDirectoryName(final)!);
            File.Copy(staging, final, overwrite: true);
            return new PublishResult { Success = true, FinalSizeBytes = new FileInfo(final).Length };
        };
        backend.OnCompletingForTests = () => completingEntered.Set();
        backend.OnDisposeCompletingWaitForTests = () => disposeEntered.Set();

        var cfg = CreateValidConfig(outputPath);
        backend.Start(cfg);
        Assert.NotNull(session);

        _ = Task.Run(() => session!.CompletionTcs.TrySetResult(session.DefaultResult));

        Assert.True(completingEntered.Wait(TimeSpan.FromSeconds(5)), "Should enter Completing.");
        Assert.True(copyEntered.Wait(TimeSpan.FromSeconds(5)), "Publisher copy should be entered.");

        var disposeTask = Task.Run(() => backend.Dispose());
        Assert.True(disposeEntered.Wait(TimeSpan.FromSeconds(5)), "Dispose should observe Completing.");

        copyRelease.Set();
        await disposeTask.WaitAsync(TimeSpan.FromSeconds(5));

        var meta = backend.Stop();
        Assert.True(meta.OutputFileExists);
        Assert.Equal(1, publisher.CallCount);
        Assert.False(Directory.Exists(Path.GetDirectoryName(session!.Options.OutputPath)!));
    }

    [Theory]
    [InlineData("wrong_container")]
    [InlineData("wrong_codec")]
    [InlineData("zero_size")]
    [InlineData("zero_duration")]
    [InlineData("too_small")]
    public void ProbeValidation_Failure_DoesNotPublish(string mode)
    {
        string outputPath = Path.Combine(_finalDir, $"probe-{mode}.mp4");
        long stagingSize = mode == "too_small" ? 100 : 10000;
        CreatePlaceholderMp4(outputPath, 1);
        var process = new FakeWgcContinuousProcess(
            initialStdout: Started("r-probe", outputPath).Concat(Ok(fileSize: stagingSize)).ToArray(),
            createOutputFile: true,
            outputFilePath: outputPath,
            outputFileSize: stagingSize);

        var backend = CreateBackend(process, out var publisher, out var probe);
        probe.OnProbe = _ => mode switch
        {
            "wrong_container" => new OutputMeta { Container = "avi", Codec = "h264", Width = 1920, Height = 1080, Fps = 30, DurationSeconds = 5, SizeBytes = stagingSize },
            "wrong_codec" => new OutputMeta { Container = "mp4", Codec = "hevc", Width = 1920, Height = 1080, Fps = 30, DurationSeconds = 5, SizeBytes = stagingSize },
            "zero_size" => new OutputMeta { Container = "mp4", Codec = "h264", Width = 1920, Height = 1080, Fps = 30, DurationSeconds = 5, SizeBytes = 0 },
            "zero_duration" => new OutputMeta { Container = "mp4", Codec = "h264", Width = 1920, Height = 1080, Fps = 30, DurationSeconds = 0, SizeBytes = stagingSize },
            "too_small" => new OutputMeta { Container = "mp4", Codec = "h264", Width = 1920, Height = 1080, Fps = 30, DurationSeconds = 5, SizeBytes = stagingSize },
            _ => throw new InvalidOperationException()
        };

        var cfg = CreateValidConfig(outputPath);
        backend.Start(cfg);

        var meta = backend.Stop();
        Assert.False(meta.OutputFileExists);
        Assert.Equal(0, publisher.CallCount);
        Assert.Contains("wgc_continuous_output_validation_failed", string.Join(" ", meta.Warnings));
        Assert.False(Directory.Exists(Path.GetDirectoryName(_lastHarness!.Options.OutputPath)!));
    }

    [Fact]
    public void CorrectedNearTenSecondMedia_PassesStrictValidationAndPublishes()
    {
        string outputPath = Path.Combine(_finalDir, "corrected-near-ten-second.mp4");
        const long stagingSize = 1340075;
        var process = new FakeWgcContinuousProcess(
            initialStdout: Started("r-corrected-near-ten", outputPath).ToArray(),
            finalStdout: Ok(frames: 300, durationMs: 10008, fileSize: stagingSize).ToArray(),
            createOutputFile: true,
            outputFilePath: outputPath,
            outputFileSize: stagingSize,
            exitCode: 0);

        var backend = CreateBackend(process, out var publisher, out var probe);
        probe.OnProbe = _ => new OutputMeta
        {
            Container = "mp4",
            Codec = "h264",
            Width = 1920,
            Height = 1080,
            Fps = 30,
            DurationSeconds = 10.008,
            SizeBytes = stagingSize
        };

        backend.Start(CreateValidConfig(outputPath, durationSeconds: 10));
        var meta = backend.Stop();

        Assert.True(meta.OutputFileExists);
        Assert.Equal(outputPath, meta.OutputPath);
        Assert.Equal(1, publisher.CallCount);
        Assert.Equal(0, backend.ExitCode);
        Assert.Empty(meta.Warnings);
        Assert.True(File.Exists(outputPath));
    }

    [Fact]
    public void OutputValidationFailure_PreservesHelperExitCodeAndStructuredCategory()
    {
        string outputPath = Path.Combine(_finalDir, "probe-duration-mismatch.mp4");
        const long stagingSize = 1340075;
        var process = new FakeWgcContinuousProcess(
            initialStdout: Started("r-duration-mismatch", outputPath).ToArray(),
            finalStdout: Ok(frames: 300, durationMs: 10008, fileSize: stagingSize).ToArray(),
            createOutputFile: true,
            outputFilePath: outputPath,
            outputFileSize: stagingSize,
            exitCode: 0);

        var backend = CreateBackend(process, out var publisher, out var probe);
        probe.OnProbe = _ => new OutputMeta
        {
            Container = "mp4",
            Codec = "h264",
            Width = 1920,
            Height = 1080,
            Fps = 30,
            DurationSeconds = 10.833,
            SizeBytes = stagingSize
        };

        backend.Start(CreateValidConfig(outputPath, durationSeconds: 10));
        var meta = backend.Stop();

        Assert.False(meta.OutputFileExists);
        Assert.Equal(0, publisher.CallCount);
        Assert.Equal(0, backend.ExitCode);
        Assert.Equal("output_validation_failed", meta.StopReason);
        Assert.Contains("duration_mismatch: probe=10833ms summary=10008ms", meta.Warnings);
        Assert.Contains("wgc_continuous_output_validation_failed", meta.Warnings);
        string warnings = string.Join(" ", meta.Warnings);
        Assert.DoesNotContain("wgc_continuous_unexpected_terminal_state", warnings);
        Assert.DoesNotContain("unexpected_exit", warnings);
        Assert.DoesNotContain("non_zero_exit", warnings);
        Assert.False(File.Exists(outputPath));
    }

    [Theory]
    [InlineData("width")]
    [InlineData("height")]
    [InlineData("size")]
    [InlineData("duration")]
    public async Task SummaryMismatch_Failure_DoesNotPublish(string mode)
    {
        string outputPath = Path.Combine(_finalDir, $"summary-{mode}.mp4");
        long stagingSize = 10000;
        CreatePlaceholderMp4(outputPath, 1);

        var finalLines = Ok(fileSize: stagingSize).ToList();
        switch (mode)
        {
            case "width":
                finalLines[5] = "Width: 1919";
                break;
            case "height":
                finalLines[6] = "Height: 1079";
                break;
            case "size":
                finalLines[4] = "FileSize: 9999 bytes";
                break;
            case "duration":
                finalLines[3] = "DurationMs: 10000";
                break;
        }

        var process = new FakeWgcContinuousProcess(
            initialStdout: Started("r-summary", outputPath).ToArray(),
            finalStdout: finalLines.ToArray(),
            createOutputFile: true,
            outputFilePath: outputPath,
            outputFileSize: stagingSize);

        var backend = CreateBackend(process, out var publisher, out _);
        var cfg = CreateValidConfig(outputPath);
        backend.Start(cfg);
        await WaitForConditionAsync(() => File.Exists(_lastHarness!.Options.BeginSignalPath), TimeSpan.FromSeconds(5));

        var meta = backend.Stop();
        Assert.False(meta.OutputFileExists);
        Assert.Equal(0, publisher.CallCount);
        Assert.Contains($"{mode}_mismatch", string.Join(" ", meta.Warnings));
        Assert.False(Directory.Exists(Path.GetDirectoryName(_lastHarness!.Options.OutputPath)!));
    }

    // -----------------------------------------------------------------
    // Dispose timeout / commit gate / Completed-session disposal tests
    // -----------------------------------------------------------------

    [Fact]
    public async Task Dispose_ProbeBlocksBeyondGraceTimeout_NoLateCommit()
    {
        string outputPath = Path.Combine(_finalDir, "probe-block.mp4");
        CreatePlaceholderMp4(outputPath, 1024);
        byte[] existingBytes = File.ReadAllBytes(outputPath);
        long stagingSize = 10000;

        var process = new FakeWgcContinuousProcess(
            initialStdout: Started("r-probe-block", outputPath).Concat(Ok(fileSize: stagingSize)).ToArray(),
            createOutputFile: true,
            outputFilePath: outputPath,
            outputFileSize: stagingSize);

        var backend = CreateBackend(process, out _, out var probe);
        backend.DisposeGraceTimeoutForTests = TimeSpan.FromMilliseconds(50);
        backend.DisposeDrainTimeoutForTests = TimeSpan.FromMilliseconds(50);

        var probeBarrier = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        probe.OnProbe = path =>
        {
            // Block the completion owner until Dispose has returned.
            probeBarrier.Task.Wait(TimeSpan.FromSeconds(10));
            return new OutputMeta
            {
                Container = "mp4",
                Codec = "h264",
                Width = 1920,
                Height = 1080,
                Fps = 30,
                DurationSeconds = 5,
                SizeBytes = new FileInfo(path).Length
            };
        };

        backend.Start(CreateValidConfig(outputPath));

        await WaitForConditionAsync(() => probe.CallCount > 0, TimeSpan.FromSeconds(5),
            "Completion owner should have reached the probe.");

        backend.Dispose();

        // Release the late probe after Dispose has closed the gate.
        probeBarrier.TrySetResult();

        // Let the late completion owner finish its finally block.
        await WaitForConditionAsync(
            () => backend.LifecycleStateNameForTests == "Disposed",
            TimeSpan.FromSeconds(5),
            "Lifecycle should settle to Disposed.");

        // The existing final file must remain unchanged.
        Assert.Equal(existingBytes, File.ReadAllBytes(outputPath));
        Assert.Equal("Disposed", backend.LifecycleStateNameForTests);
    }

    [Fact]
    public async Task Dispose_PublisherBlocksBeyondGraceTimeout_NoLateCommit()
    {
        string outputPath = Path.Combine(_finalDir, "publisher-block.mp4");
        CreatePlaceholderMp4(outputPath, 1024);
        byte[] existingBytes = File.ReadAllBytes(outputPath);
        long stagingSize = 10000;

        var process = new FakeWgcContinuousProcess(
            initialStdout: Started("r-pub-block", outputPath).Concat(Ok(fileSize: stagingSize)).ToArray(),
            createOutputFile: true,
            outputFilePath: outputPath,
            outputFileSize: stagingSize);

        var backend = CreateBackend(process, out var publisher, out _);
        backend.DisposeGraceTimeoutForTests = TimeSpan.FromMilliseconds(50);
        backend.DisposeDrainTimeoutForTests = TimeSpan.FromMilliseconds(50);

        var publishBarrier = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        publisher.OnPublishAsync = async (_, _, ct, _) =>
        {
            // Stall the publisher until the cancellation gate closes.
            await Task.Delay(Timeout.Infinite, ct);
            publishBarrier.TrySetResult();
            return new PublishResult { Success = false, FailureCategory = "should_not_reach" };
        };

        backend.Start(CreateValidConfig(outputPath));

        await WaitForConditionAsync(() => publisher.CallCount > 0, TimeSpan.FromSeconds(5),
            "Completion owner should have reached the publisher.");

        backend.Dispose();

        // Give the cancelled publisher continuation time to observe cancellation.
        await Task.Delay(100);

        // The existing final file must remain unchanged.
        Assert.Equal(existingBytes, File.ReadAllBytes(outputPath));
        Assert.Equal("Disposed", backend.LifecycleStateNameForTests);
    }

    [Fact]
    public async Task Dispose_PublisherStallsBeforeMove_TimeoutClosesGate_NoLateCommit()
    {
        string outputPath = Path.Combine(_finalDir, "publisher-move-block.mp4");
        CreatePlaceholderMp4(outputPath, 1024);
        byte[] existingBytes = File.ReadAllBytes(outputPath);
        long stagingSize = 10000;

        var process = new FakeWgcContinuousProcess(
            initialStdout: Started("r-move-block", outputPath).Concat(Ok(fileSize: stagingSize)).ToArray(),
            createOutputFile: true,
            outputFilePath: outputPath,
            outputFileSize: stagingSize);

        var backend = CreateBackend(process, out var publisher, out _);
        backend.DisposeGraceTimeoutForTests = TimeSpan.FromMilliseconds(50);
        backend.DisposeDrainTimeoutForTests = TimeSpan.FromMilliseconds(50);

        publisher.OnPublishAsync = async (staging, final, ct, gate) =>
        {
            // Simulate successful copy/flush/size-check, then stop at the move boundary.
            var dir = Path.GetDirectoryName(final);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);
            string tmpPath = final + ".publish-tmp-test.mp4";
            File.Copy(staging, tmpPath, overwrite: true);

            // Wait until Dispose cancels the gate.
            await Task.Delay(Timeout.Infinite, ct);

            return new PublishResult { Success = false, FailureCategory = "should_not_reach" };
        };

        backend.Start(CreateValidConfig(outputPath));

        await WaitForConditionAsync(() => publisher.CallCount > 0, TimeSpan.FromSeconds(5),
            "Completion owner should have reached the publisher.");

        backend.Dispose();

        await Task.Delay(100);

        Assert.Equal(existingBytes, File.ReadAllBytes(outputPath));
        Assert.Equal("Disposed", backend.LifecycleStateNameForTests);
    }

    [Fact]
    public async Task Dispose_LateCompletionDoesNotRevertDisposedState()
    {
        string outputPath = Path.Combine(_finalDir, "late-state.mp4");
        CreatePlaceholderMp4(outputPath, 1024);
        long stagingSize = 10000;

        var process = new FakeWgcContinuousProcess(
            initialStdout: Started("r-late-state", outputPath).Concat(Ok(fileSize: stagingSize)).ToArray(),
            createOutputFile: true,
            outputFilePath: outputPath,
            outputFileSize: stagingSize);

        var backend = CreateBackend(process, out _, out var probe);
        backend.DisposeGraceTimeoutForTests = TimeSpan.FromMilliseconds(50);
        backend.DisposeDrainTimeoutForTests = TimeSpan.FromMilliseconds(50);

        var probeBarrier = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        probe.OnProbe = path =>
        {
            probeBarrier.Task.Wait(TimeSpan.FromSeconds(10));
            return new OutputMeta
            {
                Container = "mp4",
                Codec = "h264",
                Width = 1920,
                Height = 1080,
                Fps = 30,
                DurationSeconds = 5,
                SizeBytes = new FileInfo(path).Length
            };
        };

        backend.Start(CreateValidConfig(outputPath));
        await WaitForConditionAsync(() => probe.CallCount > 0, TimeSpan.FromSeconds(5));

        backend.Dispose();
        Assert.Equal("Disposed", backend.LifecycleStateNameForTests);

        probeBarrier.TrySetResult();
        await Task.Delay(100);

        // The late completion owner must not be able to revert Disposed.
        Assert.Equal("Disposed", backend.LifecycleStateNameForTests);
    }

    [Fact]
    public async Task Dispose_Timeout_FinallyCleansStagingAndSignals()
    {
        string outputPath = Path.Combine(_finalDir, "timeout-cleanup.mp4");
        CreatePlaceholderMp4(outputPath, 1024);
        long stagingSize = 10000;

        var process = new FakeWgcContinuousProcess(
            initialStdout: Started("r-timeout-cleanup", outputPath).Concat(Ok(fileSize: stagingSize)).ToArray(),
            createOutputFile: true,
            outputFilePath: outputPath,
            outputFileSize: stagingSize);

        var backend = CreateBackend(process, out _, out var probe);
        backend.DisposeGraceTimeoutForTests = TimeSpan.FromMilliseconds(50);
        backend.DisposeDrainTimeoutForTests = TimeSpan.FromMilliseconds(50);

        var probeBarrier = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        probe.OnProbe = path =>
        {
            probeBarrier.Task.Wait(TimeSpan.FromSeconds(10));
            return new OutputMeta
            {
                Container = "mp4",
                Codec = "h264",
                Width = 1920,
                Height = 1080,
                Fps = 30,
                DurationSeconds = 5,
                SizeBytes = new FileInfo(path).Length
            };
        };

        backend.Start(CreateValidConfig(outputPath));
        await WaitForConditionAsync(() => probe.CallCount > 0, TimeSpan.FromSeconds(5));

        backend.Dispose();
        probeBarrier.TrySetResult();

        // Wait for the late completion owner to finish its finally block.
        await Task.Delay(200);

        string? stagingDir = Path.GetDirectoryName(_lastHarness!.Options.OutputPath);
        Assert.False(Directory.Exists(stagingDir), "Staging directory should be cleaned by the completion owner.");
    }

    [Fact]
    public void Dispose_CompletionOwnerException_StillReleasesSignalsAndStaging()
    {
        var session = new FakeSession(CreateOptionsWithHelperPath());
        session.CompletionTcs.TrySetResult(session.DefaultResult);

        var backend = CreateBackend(_ => session, out _, out _);
        backend.OnCompletingForTests = () => throw new InvalidOperationException("boom");

        backend.Start(CreateValidConfig());

        Assert.Equal("Completed", backend.LifecycleStateNameForTests);
        backend.Dispose();
        Assert.Equal("Disposed", backend.LifecycleStateNameForTests);
    }

    [Fact]
    public void Dispose_AfterNaturalSuccess_SessionDisposedExactlyOnce()
    {
        var session = new FakeSession(CreateOptionsWithHelperPath());
        session.CompletionTcs.TrySetResult(session.DefaultResult);

        var backend = CreateBackend(_ => session, out _, out _);
        backend.Start(CreateValidConfig());

        Assert.Equal("Completed", backend.LifecycleStateNameForTests);
        Assert.Equal(0, session.DisposeCount);

        backend.Dispose();
        Assert.Equal("Disposed", backend.LifecycleStateNameForTests);
        Assert.Equal(1, session.DisposeCount);
    }

    [Fact]
    public void Dispose_AfterNaturalFailure_SessionDisposedExactlyOnce()
    {
        var session = new FakeSession(CreateOptionsWithHelperPath());
        session.CompletionTcs.TrySetResult(new WgcContinuousSessionResult
        {
            State = WgcContinuousManagedSessionState.Failed,
            ExitCode = -1,
            FailureCategory = "helper_failed"
        });

        var backend = CreateBackend(_ => session, out _, out _);
        backend.Start(CreateValidConfig());

        Assert.Equal("Completed", backend.LifecycleStateNameForTests);
        Assert.Equal(0, session.DisposeCount);

        backend.Dispose();
        Assert.Equal("Disposed", backend.LifecycleStateNameForTests);
        Assert.Equal(1, session.DisposeCount);
    }

    [Fact]
    public void Dispose_MultipleTimes_SessionDisposedExactlyOnce()
    {
        var session = new FakeSession(CreateOptionsWithHelperPath());
        session.CompletionTcs.TrySetResult(session.DefaultResult);

        var backend = CreateBackend(_ => session, out _, out _);
        backend.Start(CreateValidConfig());

        backend.Dispose();
        backend.Dispose();
        backend.Dispose();

        Assert.Equal("Disposed", backend.LifecycleStateNameForTests);
        Assert.Equal(1, session.DisposeCount);
    }

    [Fact]
    public void Dispose_FixtureDoesNotSeparatelyDisposeBackendSession()
    {
        var session = new FakeSession(CreateOptionsWithHelperPath());
        session.CompletionTcs.TrySetResult(session.DefaultResult);

        var backend = CreateBackend(_ => session, out _, out _);
        backend.Start(CreateValidConfig());

        // The test fixture removed the SessionHarness from _disposables; only
        // the backend should dispose the session it owns.
        backend.Dispose();

        Assert.Equal(1, session.DisposeCount);
    }

    [Fact]
    public async Task Dispose_CommitGateClosesBeforeMove_NoMoveOccurs()
    {
        string outputPath = Path.Combine(_finalDir, "gate-before-move.mp4");
        CreatePlaceholderMp4(outputPath, 1024);
        byte[] existingBytes = File.ReadAllBytes(outputPath);
        long stagingSize = 10000;

        var process = new FakeWgcContinuousProcess(
            initialStdout: Started("r-gate-before", outputPath).Concat(Ok(fileSize: stagingSize)).ToArray(),
            createOutputFile: true,
            outputFilePath: outputPath,
            outputFileSize: stagingSize);

        var backend = CreateBackend(process, out var publisher, out _);
        backend.DisposeGraceTimeoutForTests = TimeSpan.FromMilliseconds(50);
        backend.DisposeDrainTimeoutForTests = TimeSpan.FromMilliseconds(50);

        var moveBarrier = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        publisher.OnPublishAsync = async (staging, final, ct, gate) =>
        {
            // Pause before entering the commit gate so Dispose can close it first.
            await moveBarrier.Task.WaitAsync(TimeSpan.FromSeconds(10));

            bool moved = gate!.TryCommit(() => File.Copy(staging, final, overwrite: true));
            return moved
                ? new PublishResult { Success = true }
                : new PublishResult { Success = false, FailureCategory = "commit_closed" };
        };

        backend.Start(CreateValidConfig(outputPath));
        await WaitForConditionAsync(() => publisher.CallCount > 0, TimeSpan.FromSeconds(5),
            "Completion owner should have reached the publisher.");

        backend.Dispose();
        Assert.Equal("Disposed", backend.LifecycleStateNameForTests);

        // Now release the publisher continuation.
        moveBarrier.TrySetResult();
        await Task.Delay(100);

        Assert.Equal(0, publisher.MoveCount);
        Assert.Equal(0, backend.NaturalExitCallbackCountForTests);
        Assert.Equal(existingBytes, File.ReadAllBytes(outputPath));
        Assert.Equal("Disposed", backend.LifecycleStateNameForTests);
    }

    [Fact]
    public async Task Dispose_CommitGateWaitsForInFlightMove_MoveCompletesBeforeCloseReturns()
    {
        string outputPath = Path.Combine(_finalDir, "gate-inflight-move.mp4");
        CreatePlaceholderMp4(outputPath, 1024);
        long stagingSize = 10000;

        var process = new FakeWgcContinuousProcess(
            initialStdout: Started("r-gate-inflight", outputPath).Concat(Ok(fileSize: stagingSize)).ToArray(),
            createOutputFile: true,
            outputFilePath: outputPath,
            outputFileSize: stagingSize);

        var backend = CreateBackend(process, out var publisher, out _);
        backend.DisposeGraceTimeoutForTests = TimeSpan.FromMilliseconds(50);
        backend.DisposeDrainTimeoutForTests = TimeSpan.FromMilliseconds(50);

        var moveStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var moveBarrier = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        publisher.OnPublishAsync = (staging, final, ct, gate) =>
        {
            bool moved = gate!.TryCommit(() =>
            {
                moveStarted.TrySetResult();
                moveBarrier.Task.Wait(TimeSpan.FromSeconds(10));
                var dir = Path.GetDirectoryName(final);
                if (!string.IsNullOrEmpty(dir))
                    Directory.CreateDirectory(dir);
                File.Copy(staging, final, overwrite: true);
                publisher.RecordMove();
            });

            return Task.FromResult(moved
                ? new PublishResult { Success = true }
                : new PublishResult { Success = false, FailureCategory = "commit_closed" });
        };

        backend.Start(CreateValidConfig(outputPath));
        await WaitForConditionAsync(() => publisher.CallCount > 0, TimeSpan.FromSeconds(5),
            "Completion owner should have reached the publisher.");

        await moveStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        // Dispose Close must block until the in-flight move completes.
        var disposeTask = Task.Run(() => backend.Dispose());
        await Task.Delay(100);
        Assert.False(disposeTask.IsCompleted, "Dispose Close should wait for the in-flight move.");

        moveBarrier.TrySetResult();
        await disposeTask.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(1, publisher.MoveCount);
        Assert.Equal("Disposed", backend.LifecycleStateNameForTests);
    }

    [Fact]
    public async Task CommitGate_ConcurrentCloseAndTryCommit_CloseReturnsBeforeAnyLateMove()
    {
        var gate = new FileCommitGate();
        int successCount = 0;
        int rejectedCount = 0;
        var closeTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var startTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var tasks = new List<Task>();
        for (int i = 0; i < 20; i++)
        {
            int id = i;
            tasks.Add(Task.Run(() =>
            {
                startTcs.Task.Wait();
                bool moved = gate.TryCommit(() =>
                {
                    // Simulate a tiny amount of work so races are exercised.
                    Thread.Sleep(1);
                    Interlocked.Increment(ref successCount);
                });
                if (!moved)
                    Interlocked.Increment(ref rejectedCount);
            }));
        }

        tasks.Add(Task.Run(() =>
        {
            startTcs.Task.Wait();
            Thread.Sleep(2);
            gate.Close();
            closeTcs.TrySetResult();
        }));

        startTcs.TrySetResult();
        await closeTcs.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await Task.WhenAll(tasks).WaitAsync(TimeSpan.FromSeconds(5));

        // After Close returns, no further successful commits may appear.
        int successAfterClose = successCount;
        int rejectedAfterClose = rejectedCount;

        // Try a few more commits after Close to confirm they are rejected.
        for (int i = 0; i < 10; i++)
        {
            bool moved = gate.TryCommit(() => Interlocked.Increment(ref successCount));
            if (!moved)
                Interlocked.Increment(ref rejectedCount);
        }

        Assert.Equal(successAfterClose, successCount);
        Assert.True(rejectedCount > rejectedAfterClose, "Post-Close commits should be rejected.");
    }

    [Fact]
    public async Task Dispose_ProbeTimeout_SuppressesNaturalExitCallback()
    {
        string outputPath = Path.Combine(_finalDir, "probe-callback-suppress.mp4");
        CreatePlaceholderMp4(outputPath, 1024);
        long stagingSize = 10000;

        var process = new FakeWgcContinuousProcess(
            initialStdout: Started("r-probe-cb", outputPath).Concat(Ok(fileSize: stagingSize)).ToArray(),
            createOutputFile: true,
            outputFilePath: outputPath,
            outputFileSize: stagingSize);

        var backend = CreateBackend(process, out _, out var probe);
        backend.DisposeGraceTimeoutForTests = TimeSpan.FromMilliseconds(50);
        backend.DisposeDrainTimeoutForTests = TimeSpan.FromMilliseconds(50);

        var probeBarrier = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        probe.OnProbe = path =>
        {
            probeBarrier.Task.Wait(TimeSpan.FromSeconds(10));
            return new OutputMeta
            {
                Container = "mp4",
                Codec = "h264",
                Width = 1920,
                Height = 1080,
                Fps = 30,
                DurationSeconds = 5,
                SizeBytes = new FileInfo(path).Length
            };
        };

        backend.Start(CreateValidConfig(outputPath));
        await WaitForConditionAsync(() => probe.CallCount > 0, TimeSpan.FromSeconds(5));

        backend.Dispose();
        Assert.Equal("Disposed", backend.LifecycleStateNameForTests);

        probeBarrier.TrySetResult();
        await Task.Delay(100);

        Assert.Equal(0, backend.NaturalExitCallbackCountForTests);
        Assert.Equal("Disposed", backend.LifecycleStateNameForTests);
    }

    [Fact]
    public async Task Dispose_PublisherTimeout_SuppressesNaturalExitCallback()
    {
        string outputPath = Path.Combine(_finalDir, "publisher-callback-suppress.mp4");
        CreatePlaceholderMp4(outputPath, 1024);
        byte[] existingBytes = File.ReadAllBytes(outputPath);
        long stagingSize = 10000;

        var process = new FakeWgcContinuousProcess(
            initialStdout: Started("r-pub-cb", outputPath).Concat(Ok(fileSize: stagingSize)).ToArray(),
            createOutputFile: true,
            outputFilePath: outputPath,
            outputFileSize: stagingSize);

        var backend = CreateBackend(process, out var publisher, out _);
        backend.DisposeGraceTimeoutForTests = TimeSpan.FromMilliseconds(50);
        backend.DisposeDrainTimeoutForTests = TimeSpan.FromMilliseconds(50);

        publisher.OnPublishAsync = async (_, _, ct, _) =>
        {
            await Task.Delay(Timeout.Infinite, ct);
            return new PublishResult { Success = false, FailureCategory = "should_not_reach" };
        };

        backend.Start(CreateValidConfig(outputPath));
        await WaitForConditionAsync(() => publisher.CallCount > 0, TimeSpan.FromSeconds(5));

        backend.Dispose();
        Assert.Equal("Disposed", backend.LifecycleStateNameForTests);

        await Task.Delay(100);

        Assert.Equal(0, backend.NaturalExitCallbackCountForTests);
        Assert.Equal(existingBytes, File.ReadAllBytes(outputPath));
        Assert.Equal("Disposed", backend.LifecycleStateNameForTests);
    }

    [Fact]
    public async Task Dispose_DuringCompleting_BeforeCallbackArbitration_SuppressesCallback()
    {
        string outputPath = Path.Combine(_finalDir, "completing-callback-race.mp4");
        CreatePlaceholderMp4(outputPath, 1024);
        byte[] existingBytes = File.ReadAllBytes(outputPath);

        var session = new FakeSession(CreateOptionsWithHelperPath());
        session.AuthorizeTcs.TrySetResult(true);

        var backend = CreateBackend(_ => session, out var publisher, out _);
        backend.DisposeGraceTimeoutForTests = TimeSpan.FromMilliseconds(50);
        backend.DisposeDrainTimeoutForTests = TimeSpan.FromMilliseconds(50);

        var completingEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var callbackArbitrationBarrier = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var allowPublish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var disposeStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        // Delay publish so Dispose can claim the single atomic notification
        // arbiter before ProcessResult reaches the callback arbitration point.
        publisher.OnPublishAsync = (staging, final, ct, gate) =>
        {
            allowPublish.Task.Wait(TimeSpan.FromSeconds(10));
            long size = 0;
            try
            {
                gate?.TryCommit(() => File.Move(staging, final, overwrite: true));
                size = new FileInfo(final).Length;
            }
            catch { }
            return Task.FromResult(new PublishResult { Success = true, FinalSizeBytes = size });
        };

        backend.OnCompletingForTests = () =>
        {
            completingEntered.TrySetResult();
            // Start Dispose exactly after the completion owner has claimed the
            // Completing state. This exercises the Completing-branch arbitration
            // and guarantees we do not rely on a grace-window race.
            _ = Task.Run(() =>
            {
                backend.Dispose();
                disposeStarted.TrySetResult();
            });
        };
        backend.OnFireNaturalExitForTests = () =>
        {
            // Hold the callback dispatch after it has won the single atomic
            // arbiter. Dispose should be able to claim DisposeClaimed while we
            // are paused here.
            callbackArbitrationBarrier.Task.Wait(TimeSpan.FromSeconds(10));
        };

        backend.Start(CreateValidConfig(outputPath));
        File.WriteAllBytes(session.Options.OutputPath, new byte[1024]);

        // Trigger completion after Start has returned and the backend is Running.
        _ = Task.Run(() => session.CompletionTcs.TrySetResult(session.DefaultResult));

        await completingEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        // Wait until Dispose has claimed the single atomic notification token.
        await WaitForConditionAsync(
            () => backend.NotificationStateNameForTests == "DisposeClaimed",
            TimeSpan.FromSeconds(5),
            "Dispose should claim the notification arbiter while the callback is arbitrated.");

        // Release the callback arbitration barrier after Dispose has won.
        callbackArbitrationBarrier.TrySetResult();

        // Now allow ProcessResult to continue; its callback CAS must fail.
        allowPublish.TrySetResult();

        await disposeStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(0, backend.NaturalExitCallbackCountForTests);
        Assert.Equal("Disposed", backend.LifecycleStateNameForTests);
        Assert.NotEqual("Completed", backend.LifecycleStateNameForTests);
        Assert.Equal("DisposeClaimed", backend.NotificationStateNameForTests);
        Assert.Equal(existingBytes, File.ReadAllBytes(outputPath));
        Assert.Equal(1, session.DisposeCount);
    }

    [Fact]
    public async Task NaturalCompletion_WinsArbitration_CallbackExactlyOnce_DisposeDoesNotCallbackAgain()
    {
        string outputPath = Path.Combine(_finalDir, "natural-callback-once.mp4");
        CreatePlaceholderMp4(outputPath, 1024);

        var session = new FakeSession(CreateOptionsWithHelperPath());
        session.AuthorizeTcs.TrySetResult(true);

        var backend = CreateBackend(_ => session, out _, out _);
        var callbackCompleted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        backend.OnFireNaturalExitForTests = () => callbackCompleted.TrySetResult();

        backend.Start(CreateValidConfig(outputPath));
        session.CompletionTcs.TrySetResult(session.DefaultResult);

        await callbackCompleted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(1, backend.NaturalExitCallbackCountForTests);
        Assert.Equal("Completed", backend.LifecycleStateNameForTests);

        backend.Dispose();

        Assert.Equal("Disposed", backend.LifecycleStateNameForTests);
        Assert.Equal(1, backend.NaturalExitCallbackCountForTests);
        Assert.Equal(1, session.DisposeCount);
    }

    [Fact]
    public async Task Concurrent_DisposeAndCompletion_Race_NoLateCallbackOrStateRegression()
    {
        for (int i = 0; i < 20; i++)
        {
            string outputPath = Path.Combine(_finalDir, $"concurrent-race-{i}.mp4");
            CreatePlaceholderMp4(outputPath, 1024);

            // Use a synchronous-completion fake so the backend completion continuation
            // runs directly on the dedicated LongRunning thread that calls
            // TrySetResult, instead of being reposted to the thread pool where xUnit
            // parallel tests can delay it. Together with OnBeforeCallbackArbiterForTests
            // this makes the arbiter-CAS ordering fully deterministic.
            var session = new FakeSession(
                CreateOptionsWithHelperPath(),
                runCompletionContinuationsAsynchronously: false);
            session.AuthorizeTcs.TrySetResult(true);

            var backend = CreateBackend(_ => session, out _, out _);
            backend.DisposeGraceTimeoutForTests = TimeSpan.FromMilliseconds(20);
            backend.DisposeDrainTimeoutForTests = TimeSpan.FromMilliseconds(20);

            var completingEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var callbackBarrier = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            backend.OnCompletingForTests = () => completingEntered.TrySetResult();
            backend.OnFireNaturalExitForTests = () => callbackBarrier.Task.Wait(TimeSpan.FromSeconds(10));

            backend.Start(CreateValidConfig(outputPath));

            Task? completionOwnerTask = null;
            Task? disposeOwnerTask = null;
            TaskCompletionSource? releaseBeforeCallbackArbiter = null;

            // Wrap each iteration so any early-exit exception still releases the
            // completion-owner thread's wait at the callback barrier. Without this,
            // a timeout on WaitForConditionAsync would leave a thread pool worker
            // blocked for up to 10s, and accumulated blocked workers across 20
            // iterations starve the xUnit parallel scheduler in full-suite runs.
            try
            {
                if (i % 2 == 0)
                {
                    // Race 1: Dispose must claim the single atomic notification
                    // arbiter BEFORE the completion owner runs its CAS in the
                    // callback-claim path.
                    //
                    // We pause the completion owner after publish/probe at the
                    // OnBeforeCallbackArbiterForTests seam (the exact code point
                    // just before it calls Interlocked.CompareExchange to claim
                    // CallbackClaimed on the shared arbiter word). Dispose is
                    // then launched with the arbiter still "Open", so its
                    // ClaimDisposeNotification CAS wins deterministically.
                    var beforeCallbackArbiterReached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                    releaseBeforeCallbackArbiter = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

                    // Overwrite seams set earlier for this iteration's path.
                    backend.OnCompletingForTests = () => completingEntered.TrySetResult();
                    backend.OnBeforeCallbackArbiterForTests = () =>
                    {
                        beforeCallbackArbiterReached.TrySetResult();
                        releaseBeforeCallbackArbiter.Task.Wait(TimeSpan.FromSeconds(10));
                    };

                    // Launch the completion owner on a dedicated LongRunning thread
                    // so the synchronous continuation runs there and blocks at the
                    // callback-arbiter seam (no thread-pool repost).
                    completionOwnerTask = Task.Factory.StartNew(
                        () => session.CompletionTcs.TrySetResult(session.DefaultResult),
                        CancellationToken.None,
                        TaskCreationOptions.LongRunning,
                        TaskScheduler.Default);

                    // Wait for the completion owner to:
                    //   (a) win the lifecycle CAS to Completing (completingEntered)
                    //   (b) finish publish/probe and block at the pre-arbiter seam
                    //       (beforeCallbackArbiterReached)
                    // At this point the shared notification arbiter is still "Open"
                    // because the completion thread is paused one instruction before
                    // its CallbackClaimed CAS.
                    await completingEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
                    await beforeCallbackArbiterReached.Task.WaitAsync(TimeSpan.FromSeconds(5));

                    // Now Dispose on the thread pool; ClaimDisposeNotification sees
                    // "Open" and sets it to DisposeClaimed before the completion
                    // owner can resume.
                    disposeOwnerTask = Task.Factory.StartNew(
                        () =>
                        {
                            backend.Dispose();
                        },
                        CancellationToken.None,
                        TaskCreationOptions.LongRunning,
                        TaskScheduler.Default);

                    await WaitForConditionAsync(
                        () => backend.NotificationStateNameForTests == "DisposeClaimed",
                        TimeSpan.FromSeconds(5),
                        $"Iteration {i}: Dispose should claim the notification arbiter.");

                    // Only after Dispose owns the arbiter do we release the
                    // completion owner to attempt its (now doomed) CallbackClaimed
                    // CAS and reach the callback-dispatch wait.
                    releaseBeforeCallbackArbiter.TrySetResult();
                    callbackBarrier.TrySetResult();
                    await disposeOwnerTask.WaitAsync(TimeSpan.FromSeconds(5));
                }
                else
                {
                    // Race 2: Natural completion wins the notification arbiter
                    // before Dispose is ever invoked. Determinism here comes from
                    // waiting for NaturalExitCallbackCount==1 (evidence of
                    // CallbackClaimed + dispatch start) before calling Dispose.
                    backend.OnCompletingForTests = () => completingEntered.TrySetResult();
                    completionOwnerTask = Task.Factory.StartNew(
                        () => session.CompletionTcs.TrySetResult(session.DefaultResult),
                        CancellationToken.None,
                        TaskCreationOptions.LongRunning,
                        TaskScheduler.Default);
                    await completingEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
                    callbackBarrier.TrySetResult();
                    await WaitForConditionAsync(
                        () => backend.NaturalExitCallbackCountForTests == 1,
                        TimeSpan.FromSeconds(5),
                        $"Iteration {i}: Natural callback should fire exactly once.");
                    backend.Dispose();
                }
            }
            finally
            {
                // Release both Race 1 seams before joining either owner. This must
                // run on every path so a failed assertion cannot strand a thread.
                releaseBeforeCallbackArbiter?.TrySetResult();
                callbackBarrier.TrySetResult();

                // Observe both owned tasks and continue joining after the first
                // failure so no owner leaks into the next iteration.
                Exception? cleanupFailure = null;
                if (completionOwnerTask is not null)
                {
                    try
                    {
                        await completionOwnerTask.WaitAsync(TimeSpan.FromSeconds(5));
                    }
                    catch (Exception ex)
                    {
                        cleanupFailure ??= new InvalidOperationException(
                            $"Iteration {i}: completion owner did not exit cleanly.", ex);
                    }
                }

                if (disposeOwnerTask is not null)
                {
                    try
                    {
                        await disposeOwnerTask.WaitAsync(TimeSpan.FromSeconds(5));
                    }
                    catch (Exception ex)
                    {
                        cleanupFailure ??= new InvalidOperationException(
                            $"Iteration {i}: Dispose owner did not exit cleanly.", ex);
                    }
                }

                if (cleanupFailure is not null)
                    throw cleanupFailure;
            }

            Assert.True(
                backend.LifecycleStateNameForTests == "Disposed" || backend.LifecycleStateNameForTests == "Completed",
                $"Iteration {i}: unexpected lifecycle state {backend.LifecycleStateNameForTests}.");
            Assert.InRange(backend.NaturalExitCallbackCountForTests, 0, 1);

            // After Dispose returns, the state must be Disposed and never revert.
            Assert.Equal("Disposed", backend.LifecycleStateNameForTests);
            Assert.True(backend.NaturalExitCallbackCountForTests <= 1, $"Iteration {i}: callback fired more than once.");
            Assert.Equal(1, session.DisposeCount);
        }

        for (int cleanupIteration = 0; cleanupIteration < 20; cleanupIteration++)
        {
            await RunEarlyCancellationCleanupRegressionAsync(cleanupIteration, forceSetupFailure: false);
            await RunEarlyCancellationCleanupRegressionAsync(cleanupIteration, forceSetupFailure: true);
        }
    }

    private async Task RunEarlyCancellationCleanupRegressionAsync(int iteration, bool forceSetupFailure)
    {
        string pathKind = forceSetupFailure ? "setup-failure" : "early-cancellation";
        string outputPath = Path.Combine(_finalDir, $"concurrent-race-{pathKind}-{iteration}.mp4");
        CreatePlaceholderMp4(outputPath, 1024);

        var session = new FakeSession(
            CreateOptionsWithHelperPath(),
            runCompletionContinuationsAsynchronously: false);
        session.AuthorizeTcs.TrySetResult(true);

        var backend = CreateBackend(_ => session, out _, out _);
        var beforeCallbackArbiter = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseBeforeCallbackArbiter = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var disposeEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseDispose = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var forcedSetupFailure = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        backend.OnBeforeCallbackArbiterForTests = () =>
        {
            beforeCallbackArbiter.TrySetResult();
            releaseBeforeCallbackArbiter.Task.Wait(TimeSpan.FromSeconds(10));
        };
        backend.OnDisposeAfterCompletingCasForTests = () =>
        {
            if (forceSetupFailure)
            {
                forcedSetupFailure.TrySetException(new InvalidOperationException(
                    $"Iteration {iteration}: synthetic setup failure before Dispose seam notification."));
            }
            else
            {
                disposeEntered.TrySetResult();
            }
            releaseDispose.Task.Wait(TimeSpan.FromSeconds(10));
        };

        Task? completionOwner = null;
        Task? disposeOwner = null;
        Exception? primaryFailure = null;
        bool cancellationObserved = false;
        var cleanupFailures = new List<Exception>();
        try
        {
            // The outer try begins before Start and before either setup wait, so
            // setup failures use the same release-and-join path as body failures.
            backend.Start(CreateValidConfig(outputPath));
            File.WriteAllBytes(session.Options.OutputPath, new byte[1024]);

            completionOwner = Task.Factory.StartNew(
                () => session.CompletionTcs.TrySetResult(session.DefaultResult),
                CancellationToken.None,
                TaskCreationOptions.LongRunning,
                TaskScheduler.Default);
            await beforeCallbackArbiter.Task.WaitAsync(TimeSpan.FromSeconds(5));

            disposeOwner = Task.Factory.StartNew(
                backend.Dispose,
                CancellationToken.None,
                TaskCreationOptions.LongRunning,
                TaskScheduler.Default);

            if (forceSetupFailure)
            {
                // The callback deliberately does not complete disposeEntered;
                // this fails the setup wait immediately while Dispose is paused.
                await forcedSetupFailure.Task.WaitAsync(TimeSpan.FromSeconds(5));
            }
            else
            {
                await disposeEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
                using var cancellation = new CancellationTokenSource();
                cancellation.Cancel();
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellation.Token);
            }
        }
        catch (OperationCanceledException) when (!forceSetupFailure)
        {
            cancellationObserved = true;
            try
            {
                releaseDispose.TrySetResult();
                await WaitForConditionAsync(
                    () => backend.NotificationStateNameForTests == "DisposeClaimed",
                    TimeSpan.FromSeconds(5),
                    "Dispose owner should claim the arbiter during early cleanup.");
                releaseBeforeCallbackArbiter.TrySetResult();
            }
            catch (Exception ex)
            {
                primaryFailure = ex;
            }
        }
        catch (Exception ex)
        {
            primaryFailure = ex;
        }
        finally
        {
            // Release all armed seams even when Start or either setup wait fails.
            releaseDispose.TrySetResult();
            releaseBeforeCallbackArbiter.TrySetResult();

            foreach ((Task? task, string label) in new[]
            {
                (completionOwner, "completion owner"),
                (disposeOwner, "Dispose owner")
            })
            {
                if (task is null)
                    continue;

                try
                {
                    await task.WaitAsync(TimeSpan.FromSeconds(5));
                }
                catch (Exception ex)
                {
                    cleanupFailures.Add(new InvalidOperationException(
                        $"Iteration {iteration} ({pathKind}): {label} did not exit cleanly.", ex));
                }
            }
        }

        if (primaryFailure is not null && cleanupFailures.Count > 0)
        {
            throw new AggregateException(
                $"Iteration {iteration} ({pathKind}) failed during setup/body and cleanup.",
                new[] { primaryFailure }.Concat(cleanupFailures));
        }

        if (cleanupFailures.Count > 0)
            throw new AggregateException(
                $"Iteration {iteration} ({pathKind}) owned-task cleanup failed.",
                cleanupFailures);

        if (forceSetupFailure)
        {
            Assert.NotNull(primaryFailure);
            Assert.IsType<InvalidOperationException>(primaryFailure);
            Assert.False(disposeEntered.Task.IsCompleted);
            Assert.True(completionOwner?.IsCompleted == true);
            Assert.True(disposeOwner?.IsCompleted == true);
            return;
        }

        if (primaryFailure is not null)
            ExceptionDispatchInfo.Capture(primaryFailure).Throw();

        Assert.True(cancellationObserved);
        Assert.Equal("Disposed", backend.LifecycleStateNameForTests);
        Assert.Equal(0, backend.NaturalExitCallbackCountForTests);
        Assert.Equal(1, session.DisposeCount);
    }

    [Fact]
    public void PublicApi_IFileCommitGate_IsNotExported()
    {
        var assembly = typeof(WgcContinuousCaptureBackend).Assembly;
        var exportedTypes = assembly.GetExportedTypes();

        Assert.DoesNotContain(exportedTypes, t => t.Name == "IFileCommitGate");
        Assert.DoesNotContain(exportedTypes, t => t.Name == "FileCommitGate");
        Assert.DoesNotContain(exportedTypes, t => t.Name == "IStagingToFinalPublisher");
        Assert.DoesNotContain(exportedTypes, t => t.Name == "StagingToFinalPublisher");
        Assert.DoesNotContain(exportedTypes, t => t.Name == "PublishResult");
    }

    [Fact]
    public void PublicSelectorBoundary_Unchanged()
    {
        // Compile-time assertion that the public surface used by RecordingEngine
        // remains intact and no real capture is performed.
        ICaptureBackend backend = new WgcContinuousCaptureBackend();
        Assert.NotNull(backend);
        backend.Dispose();
    }

    // -----------------------------------------------------------------
    // 178F: single atomic notification arbiter tests
    // -----------------------------------------------------------------

    [Fact]
    public async Task Dispose_AfterCompletingLifecycleCas_SecondDisposeWaits_ArbiterSingleWinner_DisposeWins()
    {
        string outputPath = Path.Combine(_finalDir, "arbiter-dispose-wins.mp4");
        CreatePlaceholderMp4(outputPath, 1024);
        byte[] existingBytes = File.ReadAllBytes(outputPath);

        // Run the completion continuation on the owned LongRunning task below so
        // the test has a handle for the completion owner it deliberately parks.
        // Construct the session from the backend-supplied options so the staging
        // file written below is the same path the completion owner will publish.
        FakeSession? session = null;
        var backend = CreateBackend(options =>
        {
            session = new FakeSession(options, runCompletionContinuationsAsynchronously: false);
            session.AuthorizeTcs.TrySetResult(true);
            return session;
        }, out var publisher, out _);
        backend.DisposeGraceTimeoutForTests = TimeSpan.FromMilliseconds(50);
        backend.DisposeDrainTimeoutForTests = TimeSpan.FromMilliseconds(50);

        var lifecycleCasObserved = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondDisposeEnteredDisposing = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirstDispose = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var allowPublish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var publishEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var firstDisposeStarted = new TaskCompletionSource<Task>(TaskCreationOptions.RunContinuationsAsynchronously);
        var ownedTasks = new List<(Task Task, string Label)>();
        var taskGate = new object();
        var cleanupFailures = new List<Exception>();
        Exception? primaryFailure = null;

        void Track(Task task, string label)
        {
            lock (taskGate)
                ownedTasks.Add((task, label));
        }

        async Task JoinOwnedTasksAsync()
        {
            // The completion owner can create the first Dispose owner from its
            // callback. Join in bounded passes so that owner is observed after
            // the completion task has been released, even on a setup failure.
            for (int pass = 0; pass < 3; pass++)
            {
                (Task Task, string Label)[] snapshot;
                lock (taskGate)
                    snapshot = ownedTasks.ToArray();

                foreach (var entry in snapshot)
                {
                    try
                    {
                        await entry.Task.WaitAsync(TimeSpan.FromSeconds(5));
                    }
                    catch (Exception ex)
                    {
                        cleanupFailures.Add(new InvalidOperationException(
                            $"Dispose-wins cleanup could not join {entry.Label}.", ex));
                    }
                }

                lock (taskGate)
                {
                    if (ownedTasks.All(entry => entry.Task.IsCompleted))
                        return;
                }
            }

            lock (taskGate)
            {
                foreach (var entry in ownedTasks.Where(entry => !entry.Task.IsCompleted))
                {
                    cleanupFailures.Add(new TimeoutException(
                        $"Dispose-wins cleanup left {entry.Label} incomplete."));
                }
            }
        }

        publisher.OnPublishAsync = (staging, final, ct, gate) =>
        {
            // This event proves the completion owner is parked before any
            // notification callback can win the arbiter.
            publishEntered.TrySetResult();
            allowPublish.Task.GetAwaiter().GetResult();
            long size = 0;
            try
            {
                gate?.TryCommit(() => File.Move(staging, final, overwrite: true));
                size = new FileInfo(final).Length;
            }
            catch { }
            return Task.FromResult(new PublishResult { Success = true, FinalSizeBytes = size });
        };

        backend.OnCompletingForTests = () =>
        {
            var firstDisposeTask = Task.Factory.StartNew(
                () => backend.Dispose(),
                CancellationToken.None,
                TaskCreationOptions.LongRunning,
                TaskScheduler.Default);
            Track(firstDisposeTask, "first Dispose owner");
            firstDisposeStarted.TrySetResult(firstDisposeTask);
        };

        backend.OnDisposeAfterCompletingCasForTests = () =>
        {
            lifecycleCasObserved.TrySetResult();
            releaseFirstDispose.Task.GetAwaiter().GetResult();
        };

        backend.OnDisposeDisposingWaitForTests = () =>
            secondDisposeEnteredDisposing.TrySetResult(backend.NotificationStateNameForTests);

        try
        {
            backend.Start(CreateValidConfig(outputPath));
            // Match FakeSession.DefaultResult.Summary.FileSize and FakeProbe so
            // validation reaches the publisher gate instead of bypassing it.
            File.WriteAllBytes(session!.Options.OutputPath, new byte[10000]);

            // With synchronous fake continuations this task is the completion
            // owner and remains joinable while the publisher gate is held.
            var completionOwnerTask = Task.Factory.StartNew(
                () => session!.CompletionTcs.TrySetResult(session.DefaultResult),
                CancellationToken.None,
                TaskCreationOptions.LongRunning,
                TaskScheduler.Default);
            Track(completionOwnerTask, "completion owner");

            await lifecycleCasObserved.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await publishEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));

            // The existing post-claim seam is the ordering proof: once this
            // event fires, the second Dispose has already CASed DisposeClaimed.
            var secondDisposeTask = Task.Factory.StartNew(
                () => backend.Dispose(),
                CancellationToken.None,
                TaskCreationOptions.LongRunning,
                TaskScheduler.Default);
            Track(secondDisposeTask, "second Dispose owner");

            string secondDisposeState = await secondDisposeEnteredDisposing.Task
                .WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal("DisposeClaimed", secondDisposeState);

            // Release the first Dispose only after the second Dispose has
            // directly proved ownership of the notification arbiter.
            releaseFirstDispose.TrySetResult();
            var firstDisposeTask = await firstDisposeStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await firstDisposeTask.WaitAsync(TimeSpan.FromSeconds(5));

            // First Dispose has now closed the commit gate; only then release
            // the parked publisher so it can prove no final-file replacement.
            allowPublish.TrySetResult();
            await completionOwnerTask.WaitAsync(TimeSpan.FromSeconds(5));
            await secondDisposeTask.WaitAsync(TimeSpan.FromSeconds(5));

            Assert.Equal("Disposed", backend.LifecycleStateNameForTests);
            Assert.Equal("DisposeClaimed", backend.NotificationStateNameForTests);
            Assert.Equal(0, backend.NaturalExitCallbackCountForTests);
            Assert.Equal(1, session!.DisposeCount);
            Assert.Equal(existingBytes, File.ReadAllBytes(outputPath));
        }
        catch (Exception ex)
        {
            primaryFailure = ex;
        }
        finally
        {
            // Detach all test seams first, then release every gate regardless of
            // which setup/assertion step failed.
            backend.OnCompletingForTests = null;
            backend.OnDisposeAfterCompletingCasForTests = null;
            backend.OnDisposeDisposingWaitForTests = null;
            releaseFirstDispose.TrySetResult();
            allowPublish.TrySetResult();
            await JoinOwnedTasksAsync();
        }

        if (primaryFailure is not null && cleanupFailures.Count > 0)
        {
            throw new AggregateException(
                "Dispose-wins race failed and worker cleanup also failed.",
                new[] { primaryFailure }.Concat(cleanupFailures));
        }

        if (primaryFailure is not null)
            ExceptionDispatchInfo.Capture(primaryFailure).Throw();

        if (cleanupFailures.Count > 0)
            throw new AggregateException("Dispose-wins worker cleanup failed.", cleanupFailures);
    }

    [Fact]
    public async Task Dispose_AfterCompletingLifecycleCas_CallbackWins_ArbiterSingleWinner()
    {
        string outputPath = Path.Combine(_finalDir, "arbiter-callback-wins.mp4");
        CreatePlaceholderMp4(outputPath, 1024);

        var session = new FakeSession(CreateOptionsWithHelperPath());
        session.AuthorizeTcs.TrySetResult(true);

        var backend = CreateBackend(_ => session, out var publisher, out _);
        backend.DisposeGraceTimeoutForTests = TimeSpan.FromMilliseconds(50);
        backend.DisposeDrainTimeoutForTests = TimeSpan.FromMilliseconds(50);

        var lifecycleCasObserved = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondDisposeEnteredDisposing = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirstDispose = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var callbackBarrier = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var firstDisposeCompleted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var callbackCompleted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        publisher.OnPublishAsync = (staging, final, ct, gate) =>
        {
            long size = 0;
            try
            {
                gate?.TryCommit(() => File.Move(staging, final, overwrite: true));
                size = new FileInfo(final).Length;
            }
            catch { }
            return Task.FromResult(new PublishResult { Success = true, FinalSizeBytes = size });
        };

        backend.OnNaturalExit((code, meta) => callbackCompleted.TrySetResult());

        backend.OnCompletingForTests = () =>
        {
            _ = Task.Factory.StartNew(() =>
            {
                backend.Dispose();
                firstDisposeCompleted.TrySetResult();
            }, TaskCreationOptions.LongRunning);
        };

        backend.OnDisposeAfterCompletingCasForTests = () =>
        {
            lifecycleCasObserved.TrySetResult();
            releaseFirstDispose.Task.Wait(TimeSpan.FromSeconds(10));
        };

        backend.OnDisposeDisposingWaitForTests = () => secondDisposeEnteredDisposing.TrySetResult();

        backend.OnFireNaturalExitForTests = () =>
        {
            callbackBarrier.Task.Wait(TimeSpan.FromSeconds(10));
        };

        backend.Start(CreateValidConfig(outputPath));
        File.WriteAllBytes(session.Options.OutputPath, new byte[1024]);

        // Use a long-running task for the synchronous completion continuation so
        // the dedicated thread blocks at the publisher boundary instead of
        // starving the thread pool that runs the Dispose tasks.
        _ = Task.Factory.StartNew(() => session.CompletionTcs.TrySetResult(session.DefaultResult), TaskCreationOptions.LongRunning);

        await lifecycleCasObserved.Task.WaitAsync(TimeSpan.FromSeconds(5));

        // Wait until the callback has won the arbiter and dispatch has started.
        // Only then introduce the second Dispose; otherwise a later Dispose could
        // legitimately claim the arbiter first and suppress the callback.
        await WaitForConditionAsync(
            () => backend.NotificationStateNameForTests == "CallbackClaimed" && backend.NaturalExitCallbackCountForTests == 1,
            TimeSpan.FromSeconds(5),
            "Callback should win the single atomic notification arbiter.");

        var secondDisposeTask = Task.Factory.StartNew(() => backend.Dispose(), TaskCreationOptions.LongRunning);
        await secondDisposeEnteredDisposing.Task.WaitAsync(TimeSpan.FromSeconds(5));

        // Release the first Dispose only after the callback has started dispatch;
        // it must wait for that evidence and not return before the callback begins.
        releaseFirstDispose.TrySetResult();

        await firstDisposeCompleted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await secondDisposeTask.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal("Disposed", backend.LifecycleStateNameForTests);
        Assert.Equal("CallbackClaimed", backend.NotificationStateNameForTests);
        Assert.Equal(1, backend.NaturalExitCallbackCountForTests);
        Assert.Equal(1, session.DisposeCount);

        callbackBarrier.TrySetResult();
        await callbackCompleted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(1, backend.NaturalExitCallbackCountForTests);
    }

    [Fact]
    public async Task Dispose_WaitsForCallbackDispatchStart_WhenCallbackWinsArbiter()
    {
        string outputPath = Path.Combine(_finalDir, "dispose-waits-for-dispatch-start.mp4");
        CreatePlaceholderMp4(outputPath, 1024);

        var session = new FakeSession(CreateOptionsWithHelperPath());
        session.AuthorizeTcs.TrySetResult(true);

        var backend = CreateBackend(_ => session, out var publisher, out _);
        backend.DisposeGraceTimeoutForTests = TimeSpan.FromMilliseconds(50);
        backend.DisposeDrainTimeoutForTests = TimeSpan.FromMilliseconds(50);

        var callbackBarrier = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var disposeReturned = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var callbackCompleted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        publisher.OnPublishAsync = (staging, final, ct, gate) =>
        {
            long size = 0;
            try
            {
                gate?.TryCommit(() => File.Move(staging, final, overwrite: true));
                size = new FileInfo(final).Length;
            }
            catch { }
            return Task.FromResult(new PublishResult { Success = true, FinalSizeBytes = size });
        };

        backend.OnNaturalExit((code, meta) => callbackCompleted.TrySetResult());

        backend.OnFireNaturalExitForTests = () => callbackBarrier.Task.Wait(TimeSpan.FromSeconds(10));

        backend.Start(CreateValidConfig(outputPath));
        File.WriteAllBytes(session.Options.OutputPath, new byte[1024]);

        // Use a long-running task for the synchronous completion continuation so
        // the dedicated thread blocks at the callback boundary instead of
        // starving the thread pool that runs the Dispose task.
        _ = Task.Factory.StartNew(() => session.CompletionTcs.TrySetResult(session.DefaultResult), TaskCreationOptions.LongRunning);

        // Wait until the callback has won and is paused right before the handler.
        await WaitForConditionAsync(
            () => backend.NotificationStateNameForTests == "CallbackClaimed" && backend.NaturalExitCallbackCountForTests == 1,
            TimeSpan.FromSeconds(5),
            "Callback should win the arbiter and begin dispatch.");

        _ = Task.Factory.StartNew(() =>
        {
            backend.Dispose();
            disposeReturned.TrySetResult();
        }, TaskCreationOptions.LongRunning);

        // Give the Dispose task a moment to reach its wait; it must not have
        // returned while the callback is still paused before the handler.
        await Task.Delay(100);
        Assert.False(disposeReturned.Task.IsCompleted, "Dispose must not return before callback dispatch has started.");

        // Release the callback. Dispose should observe dispatch-started and return.
        callbackBarrier.TrySetResult();
        await disposeReturned.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await callbackCompleted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal("Disposed", backend.LifecycleStateNameForTests);
        Assert.Equal("CallbackClaimed", backend.NotificationStateNameForTests);
        Assert.Equal(1, backend.NaturalExitCallbackCountForTests);
        Assert.Equal(1, session.DisposeCount);
    }

    [Fact]
    public async Task Concurrent_TwoDispose_SecondIndependentlyClosesArbiter_20Rounds()
    {
        for (int i = 0; i < 20; i++)
        {
            string outputPath = Path.Combine(_finalDir, $"second-dispose-closes-arbiter-{i}.mp4");
            CreatePlaceholderMp4(outputPath, 1024);

            // Use a synchronous-completion fake so the backend completion continuation
            // runs on the same dedicated LongRunning thread that calls TrySetResult,
            // instead of being reposted to the thread pool where xUnit parallel tests
            // can delay it.
            var session = new FakeSession(CreateOptionsWithHelperPath(), runCompletionContinuationsAsynchronously: false);
            session.AuthorizeTcs.TrySetResult(true);

            var backend = CreateBackend(_ => session, out var publisher, out _);
            backend.DisposeGraceTimeoutForTests = TimeSpan.FromMilliseconds(20);
            backend.DisposeDrainTimeoutForTests = TimeSpan.FromMilliseconds(20);

            var lifecycleCasObserved = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var releaseFirstDispose = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var beforeCallbackArbiterObserved = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var releaseBeforeCallbackArbiter = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var firstDisposeCompleted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var secondDisposeCompleted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

            publisher.OnPublishAsync = (staging, final, ct, gate) =>
            {
                long size = 0;
                try
                {
                    gate?.TryCommit(() => File.Move(staging, final, overwrite: true));
                    size = new FileInfo(final).Length;
                }
                catch { }
                return Task.FromResult(new PublishResult { Success = true, FinalSizeBytes = size });
            };

            backend.OnCompletingForTests = () =>
            {
                _ = Task.Factory.StartNew(
                    () =>
                    {
                        backend.Dispose();
                        firstDisposeCompleted.TrySetResult();
                    },
                    CancellationToken.None,
                    TaskCreationOptions.LongRunning,
                    TaskScheduler.Default);
            };

            backend.OnDisposeAfterCompletingCasForTests = () =>
            {
                lifecycleCasObserved.TrySetResult();
                releaseFirstDispose.Task.Wait();
            };

            // Pause the completion owner after publish but before it claims the
            // single atomic notification arbiter. This makes the race deterministic:
            // the test-thread Dispose is guaranteed to observe an unclaimed arbiter.
            backend.OnBeforeCallbackArbiterForTests = () =>
            {
                beforeCallbackArbiterObserved.TrySetResult();
                releaseBeforeCallbackArbiter.Task.Wait();
            };

            backend.Start(CreateValidConfig(outputPath));
            File.WriteAllBytes(session.Options.OutputPath, new byte[1024]);

            // Completion continuation is ExecuteSynchronously; with a non-async TCS it
            // runs directly on this LongRunning thread, deterministically invoking
            // OnCompletingForTests and then reaching OnBeforeCallbackArbiterForTests.
            _ = Task.Factory.StartNew(
                () => session.CompletionTcs.TrySetResult(session.DefaultResult),
                CancellationToken.None,
                TaskCreationOptions.LongRunning,
                TaskScheduler.Default);

            await lifecycleCasObserved.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await beforeCallbackArbiterObserved.Task.WaitAsync(TimeSpan.FromSeconds(5));

            // Both the first Dispose and the completion owner are paused before
            // claiming the notification arbiter. Call Dispose directly from the test
            // thread; it must close the arbiter independently.
            backend.Dispose();
            secondDisposeCompleted.TrySetResult();

            Assert.Equal("DisposeClaimed", backend.NotificationStateNameForTests);

            // Release the completion owner first; its callback CAS must lose.
            releaseBeforeCallbackArbiter.TrySetResult();

            // Release the first Dispose only after the arbiter is closed.
            releaseFirstDispose.TrySetResult();

            await firstDisposeCompleted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await secondDisposeCompleted.Task.WaitAsync(TimeSpan.FromSeconds(5));

            Assert.Equal("Disposed", backend.LifecycleStateNameForTests);
            Assert.Equal("DisposeClaimed", backend.NotificationStateNameForTests);
            Assert.Equal(0, backend.NaturalExitCallbackCountForTests);
            Assert.Equal(1, session.DisposeCount);
        }
    }

    [Fact]
    public async Task Dispose_WaitsForCallbackDispatchStart_BeforeReturning_CallbackHandlerReentersDispose()
    {
        string outputPath = Path.Combine(_finalDir, "dispatch-start-wait-reentrant.mp4");
        CreatePlaceholderMp4(outputPath, 1024);

        var session = new FakeSession(CreateOptionsWithHelperPath());
        session.AuthorizeTcs.TrySetResult(true);

        var backend = CreateBackend(_ => session, out var publisher, out _);
        backend.DisposeGraceTimeoutForTests = TimeSpan.FromMilliseconds(50);
        backend.DisposeDrainTimeoutForTests = TimeSpan.FromMilliseconds(50);

        var callbackClaimed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var callbackBarrier = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var disposeReturned = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var callbackCompleted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        publisher.OnPublishAsync = (staging, final, ct, gate) =>
        {
            long size = 0;
            try
            {
                gate?.TryCommit(() => File.Move(staging, final, overwrite: true));
                size = new FileInfo(final).Length;
            }
            catch { }
            return Task.FromResult(new PublishResult { Success = true, FinalSizeBytes = size });
        };

        backend.OnCallbackClaimedForTests = () =>
        {
            callbackClaimed.TrySetResult();
            callbackBarrier.Task.Wait(TimeSpan.FromSeconds(10));
        };

        backend.OnNaturalExit((_, _) =>
        {
            // Reentrant Dispose from inside the callback handler must not deadlock.
            backend.Dispose();
            callbackCompleted.TrySetResult();
        });

        backend.Start(CreateValidConfig(outputPath));
        File.WriteAllBytes(session.Options.OutputPath, new byte[1024]);

        _ = Task.Factory.StartNew(
            () => session.CompletionTcs.TrySetResult(session.DefaultResult),
            CancellationToken.None,
            TaskCreationOptions.LongRunning,
            TaskScheduler.Default);

        await callbackClaimed.Task.WaitAsync(TimeSpan.FromSeconds(5));

        // Callback has won the arbiter but has not yet signalled dispatch-start.
        Assert.Equal("CallbackClaimed", backend.NotificationStateNameForTests);
        Assert.Equal(0, backend.NaturalExitCallbackCountForTests);

        _ = Task.Factory.StartNew(
            () =>
            {
                backend.Dispose();
                disposeReturned.TrySetResult();
            },
            CancellationToken.None,
            TaskCreationOptions.LongRunning,
            TaskScheduler.Default);

        // Give Dispose time to reach its wait; it must not return before dispatch-start.
        await Task.Delay(100);
        Assert.False(disposeReturned.Task.IsCompleted, "Dispose must not return before callback dispatch has started.");

        // Release the callback. It will set dispatch-start, then invoke the handler
        // which reenters Dispose, and finally complete.
        callbackBarrier.TrySetResult();

        await disposeReturned.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await callbackCompleted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal("Disposed", backend.LifecycleStateNameForTests);
        Assert.Equal("CallbackClaimed", backend.NotificationStateNameForTests);
        Assert.Equal(1, backend.NaturalExitCallbackCountForTests);
        Assert.Equal(1, session.DisposeCount);
    }

    private WgcContinuousSessionOptions CreateOptionsWithHelperPath()
    {
        string helperPath = Path.Combine(_tempDir, "fake-helper.exe");
        Directory.CreateDirectory(_tempDir);
        File.WriteAllText(helperPath, "fake");
        return new WgcContinuousSessionOptions
        {
            HelperExePath = helperPath,
            RecordingId = "r-test",
            OutputPath = Path.Combine(_tempDir, "capture.mp4"),
            DurationMs = 5000,
            Fps = 30,
            BeginSignalPath = Path.Combine(_tempDir, "begin.signal"),
            StopSignalPath = Path.Combine(_tempDir, "stop.signal"),
            BeginTimeoutMs = 30000,
            ProcessTimeoutMs = 30000,
            StopWaitTimeoutMs = 10000
        };
    }

    // -----------------------------------------------------------------
    // Fakes
    // -----------------------------------------------------------------

    private enum FakeSessionStartMode
    {
        Default,
        SynchronousCompletion,
        SynchronousThrow,
        FaultedTask
    }

    private sealed class FakeSession : IWgcContinuousBackendSession
    {
        private readonly TaskCompletionSource<object?> _startTcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<bool> _authorizeTcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<bool> _stopTcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<WgcContinuousSessionResult> _completionTcs;

        public FakeSession(WgcContinuousSessionOptions options, bool runCompletionContinuationsAsynchronously = true)
        {
            Options = options;
            _completionTcs = new TaskCompletionSource<WgcContinuousSessionResult>(
                runCompletionContinuationsAsynchronously
                    ? TaskCreationOptions.RunContinuationsAsynchronously
                    : TaskCreationOptions.None);
        }

        public WgcContinuousSessionOptions Options { get; }
        public FakeSessionStartMode StartMode { get; set; }

        public WgcContinuousSessionResult DefaultResult { get; set; } = new()
        {
            State = WgcContinuousManagedSessionState.Success,
            ExitCode = 0,
            Summary = new WgcContinuousSessionSummary
            {
                State = ContinuousSessionState.Success,
                Width = 1920,
                Height = 1080,
                DurationMs = 5000,
                HasFileSize = true,
                FileSize = 10000,
                CaptureMethod = "WGC_D3D11_FRAME_STREAM"
            }
        };

        public int StartCallCount { get; private set; }
        public int AuthorizeCallCount { get; private set; }
        public int StopCallCount { get; private set; }

        private int _disposeCount;
        public int DisposeCount => _disposeCount;
        public bool Disposed => _disposeCount > 0;

        public TaskCompletionSource<object?> StartTcs => _startTcs;
        public TaskCompletionSource<bool> AuthorizeTcs => _authorizeTcs;
        public TaskCompletionSource<bool> StopTcs => _stopTcs;
        public TaskCompletionSource<WgcContinuousSessionResult> CompletionTcs => _completionTcs;

        public Task StartAsync(CancellationToken cancellationToken = default)
        {
            StartCallCount++;
            return StartMode switch
            {
                FakeSessionStartMode.SynchronousCompletion => CompleteAndReturn(),
                FakeSessionStartMode.SynchronousThrow => throw new InvalidOperationException("StartAsync synchronous failure."),
                FakeSessionStartMode.FaultedTask => Task.FromException(new InvalidOperationException("StartAsync faulted task.")),
                _ => _startTcs.Task
            };
        }

        private Task CompleteAndReturn()
        {
            CompletionTcs.TrySetResult(DefaultResult);
            return Task.CompletedTask;
        }

        public Task<bool> AuthorizeCapture(CancellationToken cancellationToken = default)
        {
            AuthorizeCallCount++;
            return _authorizeTcs.Task;
        }

        public Task<bool> RequestStop(CancellationToken cancellationToken = default)
        {
            StopCallCount++;
            return _stopTcs.Task;
        }

        public Task<WgcContinuousSessionResult> CompletionTask => _completionTcs.Task;

        public event Action<FirstFrameObservation>? FirstFrameObserved;

        public void FireFirstFrame(FirstFrameObservation observation) => FirstFrameObserved?.Invoke(observation);

        public void Dispose()
        {
            Interlocked.Increment(ref _disposeCount);
            if (!CompletionTcs.Task.IsCompleted)
            {
                CompletionTcs.TrySetResult(new WgcContinuousSessionResult
                {
                    State = WgcContinuousManagedSessionState.Cancelled,
                    FailureCategory = "disposed"
                });
            }
        }
    }

    private sealed class SessionHarness : IDisposable
    {
        public WgcContinuousManagedSession Session { get; }
        public FakeWgcContinuousProcess Process { get; }
        public WgcContinuousSessionOptions Options { get; }

        public SessionHarness(WgcContinuousSessionOptions options, FakeWgcContinuousProcess process)
        {
            Options = options;
            Process = process;
            Session = new WgcContinuousManagedSession(options, process);
        }

        public void Dispose() => Session.Dispose();
    }

    private sealed class FakePublisher : IStagingToFinalPublisher
    {
        public int CallCount { get; private set; }
        public List<(string Staging, string Final)> Calls { get; } = new();
        public Func<string, string, CancellationToken, PublishResult>? OnPublish { get; set; }
        public Func<string, string, CancellationToken, IFileCommitGate?, Task<PublishResult>>? OnPublishAsync { get; set; }
        private int _moveCount;
        public int MoveCount => _moveCount;

        public void RecordMove() => Interlocked.Increment(ref _moveCount);

        public Task<PublishResult> PublishAsync(
            string stagingPath,
            string finalPath,
            CancellationToken cancellationToken = default,
            IFileCommitGate? commitGate = null)
        {
            CallCount++;
            Calls.Add((stagingPath, finalPath));
            if (OnPublishAsync != null)
                return OnPublishAsync(stagingPath, finalPath, cancellationToken, commitGate);
            if (OnPublish != null)
                return Task.FromResult(OnPublish(stagingPath, finalPath, cancellationToken));

            long size = 0;
            try
            {
                var fi = new FileInfo(stagingPath);
                if (fi.Exists)
                    size = fi.Length;

                var dir = Path.GetDirectoryName(finalPath);
                if (!string.IsNullOrEmpty(dir))
                    Directory.CreateDirectory(dir);

                if (commitGate != null)
                {
                    bool moved = commitGate.TryCommit(() =>
                    {
                        File.Copy(stagingPath, finalPath, overwrite: true);
                        Interlocked.Increment(ref _moveCount);
                    });
                    if (!moved)
                        return Task.FromResult(new PublishResult { Success = false, FailureCategory = "commit_closed" });
                }
                else
                {
                    File.Copy(stagingPath, finalPath, overwrite: true);
                    Interlocked.Increment(ref _moveCount);
                }
            }
            catch
            {
                // Default success path is best-effort; tests that need precise
                // file-system control can set OnPublish.
            }

            return Task.FromResult(new PublishResult
            {
                Success = true,
                FinalSizeBytes = size
            });
        }
    }

    private sealed class FakeProbe
    {
        public int CallCount { get; private set; }
        public List<string> Calls { get; } = new();
        public Func<string, OutputMeta>? OnProbe { get; set; }

        public OutputMeta Probe(string path)
        {
            CallCount++;
            Calls.Add(path);
            if (OnProbe != null)
                return OnProbe(path);

            var fi = new FileInfo(path);
            return new OutputMeta
            {
                Container = "mp4",
                Codec = "h264",
                Width = 1920,
                Height = 1080,
                Fps = 30,
                DurationSeconds = 5,
                SizeBytes = fi.Length,
                OutputFileExists = fi.Exists
            };
        }
    }

    private sealed class FakeWgcContinuousProcess : IWgcContinuousProcess
    {
        private readonly List<string> _initialStdout;
        private readonly List<string>? _finalStdout;
        private readonly List<string> _stderr;
        private readonly TimeSpan? _initialDelay;
        private readonly TimeSpan? _exitDelay;
        private readonly bool _ignoreStopSignal;
        private readonly bool _createOutputFile;
        private readonly long _outputFileSize;
        private string? _outputFilePath;
        private readonly TaskCompletionSource _continueSignal = new();
        private readonly TaskCompletionSource _exitTcs = new();
        private readonly TaskCompletionSource _killSignal = new();
        private readonly TaskCompletionSource _beginSignalObserved = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _started;
        private int _beginSignalObservationCount;

        public int Id { get; set; } = 4242;
        public int ExitCode { get; set; }
        public bool HasExited => _exitTcs.Task.IsCompleted;
        public Stream StandardOutputStream { get; private set; } = Stream.Null;
        public Stream StandardErrorStream { get; private set; } = Stream.Null;
        public string? CapturedFileName { get; private set; }
        public IReadOnlyList<string>? CapturedArguments { get; private set; }
        public bool WasKilled => _killSignal.Task.IsCompleted;
        private int _startInvocationCount;
        public int StartInvocationCount => _startInvocationCount;
        public string? WaitForBeginSignalPath { get; set; }
        public string? AutoContinueOnStopSignalPath { get; set; }
        public Task BeginSignalObservedTask => _beginSignalObserved.Task;
        public int BeginSignalObservationCount => _beginSignalObservationCount;
        public string? ObservedBeginToken { get; private set; }

        /// <summary>
        /// When non-null, forces the file-creation path used by the fake helper.
        /// When null (default), the fake derives the path from the --output
        /// argument supplied by the backend so that the helper reports the same
        /// staging path that the session expects.
        /// </summary>
        public string? OutputFilePath
        {
            get => _outputFilePath;
            set => _outputFilePath = value;
        }

        public FakeWgcContinuousProcess(
            IEnumerable<string> initialStdout,
            IEnumerable<string>? finalStdout = null,
            IEnumerable<string>? stderr = null,
            int exitCode = 0,
            TimeSpan? initialDelay = null,
            TimeSpan? exitDelay = null,
            bool ignoreStopSignal = false,
            bool createOutputFile = false,
            long outputFileSize = 0,
            string? outputFilePath = null)
        {
            _initialStdout = initialStdout.ToList();
            _finalStdout = finalStdout?.ToList();
            _stderr = stderr?.ToList() ?? new List<string>();
            ExitCode = exitCode;
            _initialDelay = initialDelay;
            _exitDelay = exitDelay;
            _ignoreStopSignal = ignoreStopSignal;
            _createOutputFile = createOutputFile;
            _outputFileSize = outputFileSize;
            _outputFilePath = outputFilePath;
        }

        public void Start(string fileName, IReadOnlyList<string> argumentList)
        {
            Interlocked.Increment(ref _startInvocationCount);
            if (Interlocked.Exchange(ref _started, 1) != 0)
                throw new InvalidOperationException("Already started");

            CapturedFileName = fileName;
            CapturedArguments = argumentList.ToList();

            // Resolve the actual output path the backend instructed the helper
            // to write to. If the test did not explicitly override the path,
            // derive it from the --output argument so that the helper stdout
            // and the staged file match the session's expected output path.
            string actualOutputPath = _outputFilePath ?? ExtractOutputPath(argumentList);

            var stdoutChannel = Channel.CreateUnbounded<byte>();
            var stderrChannel = Channel.CreateUnbounded<byte>();

            StandardOutputStream = new ChannelStream(stdoutChannel.Reader);
            StandardErrorStream = new ChannelStream(stderrChannel.Reader);

            _ = Task.Run(async () =>
            {
                try
                {
                    if (_initialDelay.HasValue)
                        await Task.Delay(_initialDelay.Value);

                    if (!string.IsNullOrEmpty(WaitForBeginSignalPath))
                    {
                        while (!File.Exists(WaitForBeginSignalPath) && !_killSignal.Task.IsCompleted)
                            await Task.Delay(10);

                        if (File.Exists(WaitForBeginSignalPath))
                        {
                            Interlocked.Increment(ref _beginSignalObservationCount);
                            try
                            {
                                ObservedBeginToken = File.ReadAllText(WaitForBeginSignalPath);
                            }
                            catch
                            {
                                // Best effort: the token evidence is optional.
                            }
                            _beginSignalObserved.TrySetResult();
                        }
                    }

                    foreach (var line in _initialStdout)
                        await WriteLineAsync(stdoutChannel.Writer, RewriteOutputLine(line, actualOutputPath));

                    if (_finalStdout != null && _finalStdout.Count > 0)
                    {
                        if (!string.IsNullOrEmpty(AutoContinueOnStopSignalPath))
                        {
                            while (!File.Exists(AutoContinueOnStopSignalPath) && !_killSignal.Task.IsCompleted)
                                await Task.Delay(10);
                            _continueSignal.TrySetResult();
                        }

                        await _continueSignal.Task;
                        foreach (var line in _finalStdout)
                            await WriteLineAsync(stdoutChannel.Writer, RewriteOutputLine(line, actualOutputPath));
                    }

                    stdoutChannel.Writer.Complete();

                    foreach (var line in _stderr)
                        await WriteLineAsync(stderrChannel.Writer, line);
                    stderrChannel.Writer.Complete();

                    if (_createOutputFile && !string.IsNullOrEmpty(actualOutputPath))
                    {
                        var dir = Path.GetDirectoryName(actualOutputPath);
                        if (!string.IsNullOrEmpty(dir))
                            Directory.CreateDirectory(dir);
                        using var fs = new FileStream(actualOutputPath, FileMode.Create, FileAccess.Write);
                        fs.SetLength(_outputFileSize);
                    }

                    if (_ignoreStopSignal)
                        await _killSignal.Task;

                    if (_exitDelay.HasValue)
                        await Task.Delay(_exitDelay.Value);

                    _exitTcs.TrySetResult();
                }
                catch (Exception ex)
                {
                    stdoutChannel.Writer.TryComplete(ex);
                    stderrChannel.Writer.TryComplete(ex);
                    _exitTcs.TrySetException(ex);
                }
            });
        }

        public void Continue() => _continueSignal.TrySetResult();

        public void KillEntireTree()
        {
            _killSignal.TrySetResult();
            _exitTcs.TrySetResult();
        }

        public Task WaitForExitAsync(CancellationToken cancellationToken)
            => _exitTcs.Task.WaitAsync(cancellationToken);

        public void Dispose() => _exitTcs.TrySetResult();

        private static string ExtractOutputPath(IReadOnlyList<string> argumentList)
        {
            for (int i = 0; i < argumentList.Count - 1; i++)
            {
                if (argumentList[i] == "--output")
                    return argumentList[i + 1];
            }

            return string.Empty;
        }

        private static string RewriteOutputLine(string line, string actualOutputPath)
        {
            if (line.StartsWith("Output: ", StringComparison.Ordinal))
                return "Output: " + actualOutputPath;
            return line;
        }

        private static async Task WriteLineAsync(ChannelWriter<byte> writer, string line)
        {
            foreach (var b in Encoding.UTF8.GetBytes(line))
                await writer.WriteAsync(b);
            await writer.WriteAsync((byte)'\n');
        }
    }

    private sealed class ChannelStream : Stream
    {
        private readonly ChannelReader<byte> _reader;
        private bool _completed;

        public ChannelStream(ChannelReader<byte> reader) => _reader = reader;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count)
            => ReadAsync(buffer, offset, count, CancellationToken.None).GetAwaiter().GetResult();

        public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            if (_completed || count == 0) return 0;

            int totalRead = 0;
            while (totalRead < count)
            {
                if (_reader.TryRead(out var b))
                {
                    buffer[offset + totalRead] = b;
                    totalRead++;
                }
                else if (_reader.Completion.IsCompleted)
                {
                    _completed = true;
                    break;
                }
                else
                {
                    if (totalRead > 0) break;
                    if (!await _reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
                    {
                        _completed = true;
                        break;
                    }
                }
            }
            return totalRead;
        }

        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}

/// <summary>
/// Dedicated non-parallel fixture that verifies <see cref="WgcContinuousCaptureBackend.Dispose"/>
/// kills a real live subprocess tree. The helper executable is generated and
/// cached by <see cref="WgcRealProcessFixture"/> as a controlled WGC helper
/// that launches an independently surviving child process. Both parent and
/// child process IDs are exposed through a structured ready file so the test
/// can verify cleanup by exact PID rather than by process name alone.
/// </summary>
[Collection("NonParallel-RealProcess")]
public sealed class WgcContinuousCaptureBackendRealProcessTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _finalDir;
    private readonly List<IDisposable> _disposables = new();
    private string? _lastUniqueBaseName;
    private string? _lastChildBaseName;

    public WgcContinuousCaptureBackendRealProcessTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "AgentRecorderTests", $"wgc-real-{Guid.NewGuid():N}");
        _finalDir = Path.Combine(_tempDir, "final");
        Directory.CreateDirectory(_tempDir);
        Directory.CreateDirectory(_finalDir);
    }

    public void Dispose()
    {
        var disposeExceptions = new List<Exception>();
        foreach (var d in _disposables)
        {
            try { d.Dispose(); }
            catch (Exception ex) { disposeExceptions.Add(ex); }
        }

        // Last-resort cleanup scoped to the unique names used by the most
        // recent test method. This must not mask product defects: it only runs
        // after the test method's own verification and emergency cleanup.
        if (!string.IsNullOrEmpty(_lastUniqueBaseName))
        {
            foreach (var p in GetProcessesByBaseName(_lastUniqueBaseName))
            {
                try { p.Kill(entireProcessTree: false); }
                catch { }
                p.Dispose();
            }
        }
        if (!string.IsNullOrEmpty(_lastChildBaseName))
        {
            foreach (var p in GetProcessesByBaseName(_lastChildBaseName))
            {
                try { p.Kill(entireProcessTree: false); }
                catch { }
                p.Dispose();
            }
        }

        Thread.Sleep(50);

        Exception? lastCleanupException = null;
        for (int attempt = 0; attempt < 20; attempt++)
        {
            try
            {
                if (Directory.Exists(_tempDir))
                    Directory.Delete(_tempDir, recursive: true);
                if (!Directory.Exists(_tempDir))
                    break;
            }
            catch (Exception ex)
            {
                lastCleanupException = ex;
                Thread.Sleep(50);
            }
        }

        Assert.False(Directory.Exists(_tempDir),
            $"Real-process test temp directory was not cleaned up: {_tempDir}. Last error: {lastCleanupException?.Message}");

        if (disposeExceptions.Count > 0)
            throw new AggregateException("Disposing real-process test resources failed.", disposeExceptions);
    }

    private static Process[] GetProcessesByBaseName(string baseName)
    {
        try
        {
            return Process.GetProcessesByName(baseName);
        }
        catch
        {
            return Array.Empty<Process>();
        }
    }

    private static bool IsProcessAlive(int pid)
    {
        try
        {
            using var p = Process.GetProcessById(pid);
            return !p.HasExited;
        }
        catch
        {
            return false;
        }
    }

    private static bool WaitForProcessExitByPid(int pid, TimeSpan timeout)
    {
        var sw = Stopwatch.StartNew();
        while (IsProcessAlive(pid) && sw.Elapsed < timeout)
            Thread.Sleep(50);
        return !IsProcessAlive(pid);
    }

    private static void TryKillProcessByPid(int pid, string expectedBaseName)
    {
        if (pid <= 0)
            return;

        try
        {
            using var p = Process.GetProcessById(pid);
            if (p.HasExited)
                return;

            // Only kill processes whose name matches the expected helper base
            // name, so emergency cleanup cannot harm unrelated processes.
            if (!string.Equals(p.ProcessName, expectedBaseName, StringComparison.OrdinalIgnoreCase))
                return;

            p.Kill(entireProcessTree: true);
        }
        catch { /* best effort */ }
    }

    private sealed record ReadyEvidence(int ParentPid, int ChildPid);

    private static bool TryParseReadyEvidence(string path, out ReadyEvidence evidence)
    {
        evidence = new ReadyEvidence(0, 0);
        try
        {
            string text = File.ReadAllText(path);
            using var doc = JsonDocument.Parse(text);
            var root = doc.RootElement;
            if (!root.TryGetProperty("parentPid", out var parentProp) ||
                !root.TryGetProperty("childPid", out var childProp))
            {
                return false;
            }

            int parentPid = parentProp.GetInt32();
            int childPid = childProp.GetInt32();
            evidence = new ReadyEvidence(parentPid, childPid);
            return true;
        }
        catch
        {
            return false;
        }
    }

    [Fact]
    public async Task Dispose_AfterRealSubprocessStart_KillsProcessTreeAndCleansStaging()
    {
        string guid = Guid.NewGuid().ToString("N");
        string uniqueBaseName = $"wgc-real-helper-{guid}";
        string childBaseName = uniqueBaseName + "-child";
        _lastUniqueBaseName = uniqueBaseName;
        _lastChildBaseName = childBaseName;

        string helperExe = Path.Combine(_tempDir, uniqueBaseName + ".exe");
        string readyPath = Path.Combine(_tempDir, "wgc-helper-ready.signal");
        string logPath = Path.Combine(_tempDir, "wgc-helper.log");

        string helperSourceExe = await WgcRealProcessFixture.GetHelperExePathAsync();
        File.Copy(helperSourceExe, helperExe, overwrite: true);

        string outputPath = Path.Combine(_finalDir, "real.mp4");
        var publisher = new WgcContinuousCaptureBackendRealProcessTests.FakePublisher();
        var probe = new WgcContinuousCaptureBackendRealProcessTests.FakeProbe();

        WgcContinuousCaptureBackend? backend = null;
        int parentPid = 0;
        int childPid = 0;
        bool parentExitedAfterDispose = false;
        bool childExitedAfterDispose = false;
        bool stagingCleanedAfterDispose = false;
        string? verificationError = null;

        try
        {
            backend = new WgcContinuousCaptureBackend(
                options => new WgcContinuousManagedSession(options),
                publisher,
                probe.Probe,
                () => helperExe,
                _tempDir);
            _disposables.Add(backend);

            var cfg = new CaptureConfig
            {
                SourceKind = "display",
                Bounds = (0, 0, 1920, 1080),
                DurationSeconds = 10,
                Fps = 30,
                OutputPath = outputPath
            };

            backend.Start(cfg);

            // Wait for structured ready evidence that includes both parent and child
            // PIDs. The helper only writes this after it has received the begin
            // signal, emitted the STARTED event, and confirmed the child is alive.
            Assert.True(WaitForCondition(() =>
            {
                if (!File.Exists(readyPath)) return false;
                if (!TryParseReadyEvidence(readyPath, out var evidence)) return false;
                return IsProcessAlive(evidence.ParentPid) && IsProcessAlive(evidence.ChildPid);
            }, TimeSpan.FromSeconds(10)), BuildRealProcessDiagnostic(
                helperExe,
                backend?.LifecycleStateNameForTests ?? "<not created>",
                readyPath,
                logPath,
                uniqueBaseName,
                childBaseName,
                parentPid,
                childPid));

            Assert.True(TryParseReadyEvidence(readyPath, out var ready),
                "Ready file existed before Dispose but could not be parsed.");

            parentPid = ready.ParentPid;
            childPid = ready.ChildPid;

            // Verify exact PIDs are alive before Dispose.
            Assert.True(IsProcessAlive(parentPid),
                $"Parent helper process (PID={parentPid}) is not alive before Dispose.");
            Assert.True(IsProcessAlive(childPid),
                $"Child helper process (PID={childPid}) is not alive before Dispose.");

            backend!.Dispose();

            // Record backend cleanup results *before* any emergency cleanup so a
            // product defect is not masked by the finally block.
            parentExitedAfterDispose = WaitForProcessExitByPid(parentPid, TimeSpan.FromSeconds(10));
            childExitedAfterDispose = WaitForProcessExitByPid(childPid, TimeSpan.FromSeconds(10));
            stagingCleanedAfterDispose = !Directory.Exists(_tempDir) ||
                !Directory.GetDirectories(_tempDir).Any(d => d.Contains("wgc-continuous"));
        }
        catch (Exception ex)
        {
            verificationError = ex.ToString();
        }
        finally
        {
            // Emergency cleanup: dispose the backend, then kill any remaining
            // helper processes by their verified PIDs and unique names. This
            // covers the full test lifecycle, including failures that occur
            // before Dispose is called.
            try { backend?.Dispose(); }
            catch { /* best effort */ }

            TryKillProcessByPid(parentPid, uniqueBaseName);
            TryKillProcessByPid(childPid, childBaseName);
        }

        if (!string.IsNullOrEmpty(verificationError))
            Assert.Fail($"Verification threw before cleanup completed: {verificationError}");

        Assert.True(parentExitedAfterDispose,
            $"Parent helper process (PID={parentPid}) was not terminated by Dispose.");
        Assert.True(childExitedAfterDispose,
            $"Child helper process (PID={childPid}) was not terminated by Dispose.");
        Assert.True(stagingCleanedAfterDispose,
            "Staging directory should be cleaned up.");

        // Process-name enumeration is kept as an additional residue guard, but
        // it does not replace the exact-PID verification above.
        Assert.True(WaitForCondition(() =>
        {
            var procs = GetProcessesByBaseName(uniqueBaseName)
                .Concat(GetProcessesByBaseName(childBaseName))
                .ToArray();
            foreach (var p in procs) p.Dispose();
            return procs.Length == 0;
        }, TimeSpan.FromSeconds(5)), "Residual helper processes were found by process name after Dispose.");
    }

    [Fact]
    public async Task Fixture_ParentOnlyKill_LeavesIndependentChildAlive()
    {
        string guid = Guid.NewGuid().ToString("N");
        string uniqueBaseName = $"wgc-real-helper-{guid}";
        string childBaseName = uniqueBaseName + "-child";
        _lastUniqueBaseName = uniqueBaseName;
        _lastChildBaseName = childBaseName;

        string helperExe = Path.Combine(_tempDir, uniqueBaseName + ".exe");
        string readyPath = Path.Combine(_tempDir, "wgc-helper-ready.signal");
        string beginSignalPath = Path.Combine(_tempDir, "begin.signal");
        string beginToken = guid;
        string stopSignalPath = Path.Combine(_tempDir, "stop.signal");
        string outputPath = Path.Combine(_finalDir, "negative.mp4");

        string helperSourceExe = await WgcRealProcessFixture.GetHelperExePathAsync();
        File.Copy(helperSourceExe, helperExe, overwrite: true);

        int parentPid = 0;
        int childPid = 0;
        Process? parentProcess = null;
        string? verificationError = null;

        try
        {
            parentProcess = Process.Start(new ProcessStartInfo
            {
                FileName = helperExe,
                Arguments = $"--capture-continuous-display --display-bounds 0,0,1920,1080 " +
                            $"--recording-id wgc-c-negative --output \"{outputPath}\" " +
                            $"--duration-ms 10000 --fps 30 " +
                            $"--begin-signal \"{beginSignalPath}\" --begin-token {beginToken} " +
                            $"--begin-timeout-ms 30000 --stop-signal \"{stopSignalPath}\" " +
                            $"--i-understand-this-captures-screen",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden
            });

            Assert.NotNull(parentProcess);

            File.WriteAllText(beginSignalPath, beginToken);

            // Wait for structured ready evidence that includes both parent and child
            // PIDs and confirms both are alive.
            Assert.True(WaitForCondition(() =>
            {
                if (!File.Exists(readyPath)) return false;
                if (!TryParseReadyEvidence(readyPath, out var evidence)) return false;
                return IsProcessAlive(evidence.ParentPid) && IsProcessAlive(evidence.ChildPid);
            }, TimeSpan.FromSeconds(10)), "Negative-control helper did not become ready.");

            Assert.True(TryParseReadyEvidence(readyPath, out var ready),
                "Ready file existed but could not be parsed.");

            parentPid = ready.ParentPid;
            childPid = ready.ChildPid;

            Assert.True(IsProcessAlive(parentPid),
                $"Parent helper process (PID={parentPid}) is not alive before parent-only kill.");
            Assert.True(IsProcessAlive(childPid),
                $"Child helper process (PID={childPid}) is not alive before parent-only kill.");

            // Kill only the parent. If the fixture has any hidden parent->child
            // coupling (Job Object, pipe EOF, etc.), the child will also exit and
            // this assertion will fail.
            using (var p = Process.GetProcessById(parentPid))
            {
                p.Kill(entireProcessTree: false);
            }

            Assert.True(WaitForProcessExitByPid(parentPid, TimeSpan.FromSeconds(10)),
                $"Parent helper process (PID={parentPid}) did not exit after parent-only kill.");

            Assert.True(IsProcessAlive(childPid),
                $"Child helper process (PID={childPid}) should survive a parent-only kill. " +
                "This indicates the test fixture is still cleaning up the child on behalf of the product.");
        }
        catch (Exception ex)
        {
            verificationError = ex.ToString();
        }
        finally
        {
            // Cleanup: the parent process object, then any remaining helper
            // processes by verified PID and unique name. The negative control
            // intentionally leaves the child alive, so we must clean it up here.
            try { parentProcess?.Kill(entireProcessTree: false); } catch { }
            try { parentProcess?.Dispose(); } catch { }
            TryKillProcessByPid(parentPid, uniqueBaseName);
            TryKillProcessByPid(childPid, childBaseName);
        }

        if (!string.IsNullOrEmpty(verificationError))
            Assert.Fail($"Negative control verification threw before cleanup completed: {verificationError}");
    }

    private static string BuildRealProcessDiagnostic(
        string helperExe,
        string lifecycleState,
        string readyPath,
        string logPath,
        string uniqueBaseName,
        string childBaseName,
        int knownParentPid,
        int knownChildPid)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Real helper process did not become ready.");
        sb.AppendLine($"Helper path: {helperExe}");
        sb.AppendLine($"Lifecycle state: {lifecycleState}");
        sb.AppendLine($"Ready path: {readyPath}");
        sb.AppendLine($"Log path: {logPath}");
        sb.AppendLine($"Known parent PID: {knownParentPid}");
        sb.AppendLine($"Known child PID: {knownChildPid}");

        if (File.Exists(readyPath))
        {
            try
            {
                sb.AppendLine($"Ready content: {File.ReadAllText(readyPath)}");
            }
            catch (Exception ex)
            {
                sb.AppendLine($"Failed to read ready file: {ex.Message}");
            }
        }
        else
        {
            sb.AppendLine("Ready file not found.");
        }

        if (knownParentPid > 0)
            sb.AppendLine($"Parent alive (PID={knownParentPid}): {IsProcessAlive(knownParentPid)}");
        if (knownChildPid > 0)
            sb.AppendLine($"Child alive (PID={knownChildPid}): {IsProcessAlive(knownChildPid)}");

        if (File.Exists(logPath))
        {
            try
            {
                var lines = File.ReadAllLines(logPath);
                int tail = Math.Min(lines.Length, 30);
                sb.AppendLine("Helper log tail:");
                for (int i = lines.Length - tail; i < lines.Length; i++)
                {
                    if (i >= 0)
                        sb.AppendLine(lines[i]);
                }
            }
            catch (Exception ex)
            {
                sb.AppendLine($"Failed to read helper log: {ex.Message}");
            }
        }
        else
        {
            sb.AppendLine("Helper log file not found.");
        }

        try
        {
            var mainProcs = GetProcessesByBaseName(uniqueBaseName);
            var childProcs = GetProcessesByBaseName(childBaseName);
            sb.AppendLine($"Live main processes by name: {mainProcs.Length}");
            foreach (var p in mainProcs)
            {
                sb.AppendLine($"  PID={p.Id}");
                p.Dispose();
            }
            sb.AppendLine($"Live child processes by name: {childProcs.Length}");
            foreach (var p in childProcs)
            {
                sb.AppendLine($"  PID={p.Id}");
                p.Dispose();
            }
        }
        catch (Exception ex)
        {
            sb.AppendLine($"Failed to enumerate live processes: {ex.Message}");
        }

        return sb.ToString();
    }

    private static bool WaitForCondition(Func<bool> predicate, TimeSpan timeout)
    {
        var sw = Stopwatch.StartNew();
        while (!predicate() && sw.Elapsed < timeout)
            Thread.Sleep(50);
        return predicate();
    }

    private sealed class FakePublisher : IStagingToFinalPublisher
    {
        public Task<PublishResult> PublishAsync(
            string stagingPath,
            string finalPath,
            CancellationToken cancellationToken = default,
            IFileCommitGate? commitGate = null)
            => Task.FromResult(new PublishResult { Success = false, FailureCategory = "test_publisher" });
    }

    private sealed class FakeProbe
    {
        public OutputMeta Probe(string path) => new() { Container = "mp4", Codec = "h264" };
    }
}
