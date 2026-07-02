using System;
using System.Text.Json;
using System.Text.Json.Nodes;
using Xunit;
using AgentRecorder.Capture;
using AgentRecorder.Core;
using AgentRecorder.Infrastructure;
using ApiException = AgentRecorder.Infrastructure.ApiException;

namespace AgentRecorder.Tests;

/// <summary>
/// Tests for the CaptureBackendSelector - verifies backend selection logic
/// based on source type and AGENT_RECORDER_WINDOW_BACKEND feature flag.
/// </summary>
public class CaptureBackendSelectorTests
{
    [Fact]
    public void Select_DisplaySource_ReturnsFfmpeg()
    {
        Environment.SetEnvironmentVariable("AGENT_RECORDER_WINDOW_BACKEND", null);
        var (backend, type) = CaptureBackendSelector.Select("display");
        Assert.NotNull(backend);
        Assert.Equal("ffmpeg", type);
        Assert.IsType<FfmpegCaptureBackend>(backend);
    }

    [Fact]
    public void Select_DisplaySource_WithWgcFlag_ReturnsFfmpeg()
    {
        // Display always uses FFmpeg; WGC flag is only for window sources
        Environment.SetEnvironmentVariable("AGENT_RECORDER_WINDOW_BACKEND", "wgc");
        try
        {
            var (backend, type) = CaptureBackendSelector.Select("display");
            Assert.Equal("ffmpeg", type);
            Assert.IsType<FfmpegCaptureBackend>(backend);
        }
        finally
        {
            Environment.SetEnvironmentVariable("AGENT_RECORDER_WINDOW_BACKEND", null);
        }
    }

    [Fact]
    public void Select_WindowSource_NoFlag_ReturnsFfmpegGdigrab()
    {
        Environment.SetEnvironmentVariable("AGENT_RECORDER_WINDOW_BACKEND", null);
        var (backend, type) = CaptureBackendSelector.Select("window");
        Assert.NotNull(backend);
        Assert.Equal("ffmpeg-gdigrab", type);
        Assert.IsType<FfmpegCaptureBackend>(backend);
    }

    [Fact]
    public void Select_WindowSource_EmptyFlag_ReturnsFfmpegGdigrab()
    {
        Environment.SetEnvironmentVariable("AGENT_RECORDER_WINDOW_BACKEND", "");
        try
        {
            var (backend, type) = CaptureBackendSelector.Select("window");
            Assert.Equal("ffmpeg-gdigrab", type);
            Assert.IsType<FfmpegCaptureBackend>(backend);
        }
        finally
        {
            Environment.SetEnvironmentVariable("AGENT_RECORDER_WINDOW_BACKEND", null);
        }
    }

    [Fact]
    public void Select_WindowSource_WithWgcFlag_ReturnsWgc()
    {
        Environment.SetEnvironmentVariable("AGENT_RECORDER_WINDOW_BACKEND", "wgc");
        try
        {
            var (backend, type) = CaptureBackendSelector.Select("window");
            Assert.NotNull(backend);
            Assert.Equal("wgc", type);
            Assert.IsType<WgcWindowCaptureBackend>(backend);
        }
        finally
        {
            Environment.SetEnvironmentVariable("AGENT_RECORDER_WINDOW_BACKEND", null);
        }
    }

    [Fact]
    public void Select_WindowSource_WithWhitespaceWgcFlag_ReturnsWgc()
    {
        Environment.SetEnvironmentVariable("AGENT_RECORDER_WINDOW_BACKEND", "  wgc  ");
        try
        {
            var (backend, type) = CaptureBackendSelector.Select("window");
            Assert.Equal("wgc", type);
            Assert.IsType<WgcWindowCaptureBackend>(backend);
        }
        finally
        {
            Environment.SetEnvironmentVariable("AGENT_RECORDER_WINDOW_BACKEND", null);
        }
    }

    [Fact]
    public void Select_WindowSource_WithUppercaseWgcFlag_ReturnsWgc()
    {
        Environment.SetEnvironmentVariable("AGENT_RECORDER_WINDOW_BACKEND", "WGC");
        try
        {
            var (backend, type) = CaptureBackendSelector.Select("window");
            Assert.Equal("wgc", type);
            Assert.IsType<WgcWindowCaptureBackend>(backend);
        }
        finally
        {
            Environment.SetEnvironmentVariable("AGENT_RECORDER_WINDOW_BACKEND", null);
        }
    }

    [Fact]
    public void Select_WindowSource_WithOtherValue_ReturnsFfmpegGdigrab()
    {
        Environment.SetEnvironmentVariable("AGENT_RECORDER_WINDOW_BACKEND", "something-else");
        try
        {
            var (backend, type) = CaptureBackendSelector.Select("window");
            Assert.Equal("ffmpeg-gdigrab", type);
            Assert.IsType<FfmpegCaptureBackend>(backend);
        }
        finally
        {
            Environment.SetEnvironmentVariable("AGENT_RECORDER_WINDOW_BACKEND", null);
        }
    }

    [Fact]
    public void Select_InvalidSourceType_Throws()
    {
        var ex = Assert.Throws<ApiException>(() =>
            CaptureBackendSelector.Select("unknown-type"));
        Assert.Equal(400, ex.Status);
        Assert.Equal("INVALID_ARGUMENT", ex.Code);
    }

    [Fact]
    public void SelectBackendType_Display_ReturnsFfmpeg()
    {
        Assert.Equal("ffmpeg", CaptureBackendSelector.SelectBackendType("display"));
    }

    [Fact]
    public void SelectBackendType_WindowNoFlag_ReturnsGdigrab()
    {
        Environment.SetEnvironmentVariable("AGENT_RECORDER_WINDOW_BACKEND", null);
        Assert.Equal("ffmpeg-gdigrab", CaptureBackendSelector.SelectBackendType("window"));
    }

    [Fact]
    public void SelectBackendType_WindowWgcFlag_ReturnsWgc()
    {
        Environment.SetEnvironmentVariable("AGENT_RECORDER_WINDOW_BACKEND", "wgc");
        try
        {
            Assert.Equal("wgc", CaptureBackendSelector.SelectBackendType("window"));
        }
        finally
        {
            Environment.SetEnvironmentVariable("AGENT_RECORDER_WINDOW_BACKEND", null);
        }
    }

    [Fact]
    public void Select_RegionSource_ReturnsFfmpegRegion()
    {
        Environment.SetEnvironmentVariable("AGENT_RECORDER_WINDOW_BACKEND", null);
        var (backend, type) = CaptureBackendSelector.Select("region");
        Assert.NotNull(backend);
        Assert.Equal("ffmpeg-region", type);
        Assert.IsType<FfmpegCaptureBackend>(backend);
    }

    [Fact]
    public void Select_RegionSource_WithWgcFlag_StillReturnsFfmpegRegion()
    {
        // Region should always use FFmpeg, even if WGC flag is set
        Environment.SetEnvironmentVariable("AGENT_RECORDER_WINDOW_BACKEND", "wgc");
        try
        {
            var (backend, type) = CaptureBackendSelector.Select("region");
            Assert.Equal("ffmpeg-region", type);
            Assert.IsType<FfmpegCaptureBackend>(backend);
        }
        finally
        {
            Environment.SetEnvironmentVariable("AGENT_RECORDER_WINDOW_BACKEND", null);
        }
    }

    [Fact]
    public void SelectBackendType_Region_ReturnsFfmpegRegion()
    {
        Assert.Equal("ffmpeg-region", CaptureBackendSelector.SelectBackendType("region"));
    }
}

/// <summary>
/// Tests for WindowIdParser - verifies HWND parsing from window_id format
/// and rejection of minimized windows.
/// </summary>
public class WindowIdParserTests
{
    [Fact]
    public void TryParse_ValidWindowId_ReturnsTrueAndHwnd()
    {
        Assert.True(WindowIdParser.TryParse("window_123456", out var hwnd));
        Assert.Equal(123456, hwnd.ToInt64());
    }

    [Fact]
    public void TryParse_ValidLargeHwnd_ReturnsTrue()
    {
        Assert.True(WindowIdParser.TryParse("window_9999999999", out var hwnd));
        Assert.Equal(9999999999L, hwnd.ToInt64());
    }

    [Fact]
    public void TryParse_NullString_ReturnsFalse()
    {
        Assert.False(WindowIdParser.TryParse(null, out var hwnd));
        Assert.Equal(default, hwnd);
    }

    [Fact]
    public void TryParse_EmptyString_ReturnsFalse()
    {
        Assert.False(WindowIdParser.TryParse("", out var hwnd));
        Assert.Equal(default, hwnd);
    }

    [Fact]
    public void TryParse_Whitespace_ReturnsFalse()
    {
        Assert.False(WindowIdParser.TryParse("   ", out var hwnd));
        Assert.Equal(default, hwnd);
    }

    [Fact]
    public void TryParse_MissingPrefix_ReturnsFalse()
    {
        Assert.False(WindowIdParser.TryParse("123456", out var hwnd));
        Assert.Equal(default, hwnd);
    }

    [Fact]
    public void TryParse_WrongPrefix_ReturnsFalse()
    {
        Assert.False(WindowIdParser.TryParse("display_123456", out var hwnd));
        Assert.Equal(default, hwnd);
    }

    [Fact]
    public void TryParse_NonNumericSuffix_ReturnsFalse()
    {
        Assert.False(WindowIdParser.TryParse("window_abc", out var hwnd));
        Assert.Equal(default, hwnd);
    }

    [Fact]
    public void TryParse_ZeroHwnd_ReturnsTrue()
    {
        // TryParse is lenient; Parse is stricter
        Assert.True(WindowIdParser.TryParse("window_0", out var hwnd));
        Assert.Equal(0, hwnd.ToInt64());
    }

    [Fact]
    public void Parse_ValidWindowId_ReturnsHwnd()
    {
        var hwnd = WindowIdParser.Parse("window_12345");
        Assert.Equal(12345, hwnd.ToInt64());
    }

    [Fact]
    public void Parse_InvalidFormat_ThrowsInvalidArgument()
    {
        var ex = Assert.Throws<ApiException>(() => WindowIdParser.Parse("window_abc"));
        Assert.Equal(400, ex.Status);
        Assert.Equal("INVALID_ARGUMENT", ex.Code);
    }

    [Fact]
    public void Parse_ZeroHwnd_ThrowsInvalidArgument()
    {
        var ex = Assert.Throws<ApiException>(() => WindowIdParser.Parse("window_0"));
        Assert.Equal(400, ex.Status);
        Assert.Equal("INVALID_ARGUMENT", ex.Code);
    }

    [Fact]
    public void Parse_MissingPrefix_ThrowsInvalidArgument()
    {
        var ex = Assert.Throws<ApiException>(() => WindowIdParser.Parse("123456"));
        Assert.Equal(400, ex.Status);
        Assert.Equal("INVALID_ARGUMENT", ex.Code);
    }

    [Fact]
    public void RejectMinimized_IsMinimized_ThrowsSourceUnavailable()
    {
        var ex = Assert.Throws<ApiException>(() =>
            WindowIdParser.RejectMinimized(isMinimized: true, "Notepad"));
        Assert.Equal(403, ex.Status);
        Assert.Equal("SOURCE_UNAVAILABLE", ex.Code);
        Assert.Contains("minimized", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void RejectMinimized_NotMinimized_DoesNotThrow()
    {
        // Should not throw
        WindowIdParser.RejectMinimized(isMinimized: false, "Notepad");
    }
}

/// <summary>
/// Tests for WgcWindowCaptureBackend - tests use an injectable fake
/// IWgcHelperProcessRunner so no real helper process is launched,
/// no real capture is performed, no PNG is written to disk.
/// </summary>
public class WgcWindowCaptureBackendTests
{
    private static IWgcHelperProcessRunner MakeFakeRunner(WgcHelperProcessResult ret) =>
        new FakeWgcProcessRunner(ret);

    private sealed class FakeWgcProcessRunner : IWgcHelperProcessRunner
    {
        private readonly WgcHelperProcessResult _ret;
        public IReadOnlyList<string>? LastArgs { get; private set; }
        public string? LastFileName { get; private set; }
        public int LastTimeoutMs { get; private set; }

        public FakeWgcProcessRunner(WgcHelperProcessResult ret) => _ret = ret;

        public WgcHelperProcessResult Run(string fileName, IReadOnlyList<string> args, int timeoutMs, CancellationToken ct = default)
        {
            LastFileName = fileName;
            LastArgs = new List<string>(args);
            LastTimeoutMs = timeoutMs;
            return _ret;
        }
    }

    [Fact]
    public void Start_TestCtor_InjectedRunnerAndExePath()
    {
        var fake = new FakeWgcProcessRunner(new WgcHelperProcessResult
        { ExitCode = 0, StandardOutput = "RESULT: OK\nStage: Complete\nWidth: 1920\nHeight: 1080\nFileSize: 102400\nCaptureMethod: WGC_D3D11_FRAME_SURFACE\nOutput: /tmp/wgc.png\n", StandardError = "" });
        var backend = new WgcWindowCaptureBackend(fake, "fake.exe");

        backend.Start(new CaptureConfig
        {
            SourceKind = "window",
            WindowHandle = new nint(0x12345),
            OutputPath = "out.mp4"
        });

        // Runner was called with the fake exe path
        Assert.Equal("fake.exe", fake.LastFileName);
    }

    [Fact]
    public void Start_ValidWindowConfig_RunsHelperAndMapsMeta()
    {
        var fake = new FakeWgcProcessRunner(new WgcHelperProcessResult
        {
            ExitCode = 0,
            StandardOutput = "RESULT: OK\nStage: Complete\nWidth: 1920\nHeight: 1080\nFileSize: 102400\nCaptureMethod: WGC_D3D11_FRAME_SURFACE\nOutput: .local-data/wgc-tests/out.png\n",
            StandardError = ""
        });
        var backend = new WgcWindowCaptureBackend(fake, "fake.exe");

        // Start should not throw (Success=true because exit==0 and RESULT: OK)
        backend.Start(new CaptureConfig
        {
            SourceKind = "window",
            WindowHandle = new nint(0x12345),
            OutputPath = "out.mp4"
        });

        var meta = backend.Stop();
        Assert.NotNull(meta);
        Assert.Equal("png", meta.Container);
        Assert.Equal("still-frame", meta.Codec);
        Assert.Equal("WGC_D3D11_FRAME_SURFACE", meta.CaptureMethod);
        Assert.Contains(".png", meta.OutputPath, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(1920, meta.Width);
        Assert.Equal(1080, meta.Height);
        Assert.Equal(102400, meta.SizeBytes);
        Assert.Contains("wgc_still_frame_only", string.Join(" ", meta.Warnings ?? []), StringComparison.Ordinal);
        Assert.Equal(0, backend.ExitCode);
    }

    [Fact]
    public void Start_InvalidHwnd_FromHelperOutput_Throws()
    {
        var fake = new FakeWgcProcessRunner(new WgcHelperProcessResult
        {
            ExitCode = 1,
            StandardOutput = "RESULT: FAIL\nStage: IsWindow(HWND)\nHRESULT: 0x80070057\nReason: HWND does not refer to a valid window.\n",
            StandardError = ""
        });
        var backend = new WgcWindowCaptureBackend(fake, "fake.exe");

        var ex = Assert.Throws<InvalidOperationException>(() =>
            backend.Start(new CaptureConfig { SourceKind = "window", WindowHandle = new nint(0xFF), OutputPath = "out.mp4" }));

        Assert.Contains("Stage=IsWindow", ex.Message, StringComparison.Ordinal);
        Assert.Contains("0x80070057", ex.Message, StringComparison.Ordinal);
        Assert.Contains("WGC helper failed", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Start_Timeout_FromHelperOutput_ThrowsWithStage()
    {
        var fake = new FakeWgcProcessRunner(new WgcHelperProcessResult
        {
            ExitCode = 1,
            StandardOutput = "RESULT: FAIL\nStage: FrameArrived(timeout)\nHRESULT: 0x800705B4\nReason: Timed out waiting for first WGC frame.\n",
            StandardError = ""
        });
        var backend = new WgcWindowCaptureBackend(fake, "fake.exe");

        var ex = Assert.Throws<InvalidOperationException>(() =>
            backend.Start(new CaptureConfig { SourceKind = "window", WindowHandle = new nint(0x12345), OutputPath = "out.mp4" }));

        Assert.Contains("FrameArrived(timeout)", ex.Message, StringComparison.Ordinal);
        Assert.Contains("0x800705B4", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Start_NonWindowSourceKind_ThrowsBeforeInvokingHelper()
    {
        var fake = new FakeWgcProcessRunner(new WgcHelperProcessResult());
        var backend = new WgcWindowCaptureBackend(fake, "fake.exe");

        var ex = Assert.Throws<InvalidOperationException>(() =>
            backend.Start(new CaptureConfig { SourceKind = "display", WindowHandle = new nint(1), OutputPath = "out.mp4" }));

        // The fake runner should NOT have been called because source validation runs first.
        Assert.Contains("window", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Null(fake.LastArgs);
        Assert.Null(fake.LastFileName);
    }

    [Fact]
    public void Start_ZeroWindowHandle_ThrowsBeforeInvokingHelper()
    {
        var fake = new FakeWgcProcessRunner(new WgcHelperProcessResult());
        var backend = new WgcWindowCaptureBackend(fake, "fake.exe");

        var ex = Assert.Throws<InvalidOperationException>(() =>
            backend.Start(new CaptureConfig { SourceKind = "window", WindowHandle = 0, OutputPath = "out.mp4" }));

        Assert.Contains("WindowHandle", ex.Message, StringComparison.Ordinal);
        Assert.Null(fake.LastArgs);
    }

    [Fact]
    public void Start_BuildsArgumentListWith_Hwnd_And_PngOutput()
    {
        var fake = new FakeWgcProcessRunner(new WgcHelperProcessResult
        {
            ExitCode = 0,
            StandardOutput = "RESULT: OK\nStage: Complete\nWidth: 1024\nHeight: 768\nFileSize: 4096\nCaptureMethod: WGC_D3D11_FRAME_SURFACE\nOutput: .local-data/wgc-tests/x.png\n",
            StandardError = ""
        });
        var backend = new WgcWindowCaptureBackend(fake, "fake.exe");

        backend.Start(new CaptureConfig { SourceKind = "window", WindowHandle = new nint(0x0001ABCD), OutputPath = "captures/rec.mp4" });

        Assert.NotNull(fake.LastArgs);
        var args = fake.LastArgs;
        // --capture-one-frame-window exists
        Assert.Contains(args, a => a == "--capture-one-frame-window");
        // --i-understand-this-captures-screen exists
        Assert.Contains(args, a => a == "--i-understand-this-captures-screen");

        // --output exists (value ends in .png)
        int outputIdx = -1;
        for (int i = 0; i < args.Count; i++) if (args[i] == "--output") { outputIdx = i; break; }
        Assert.True(outputIdx >= 0 && outputIdx + 1 < args.Count, "--output should have a value");
        Assert.EndsWith(".png", args[outputIdx + 1]);

        // --timeout-ms exists and is clamped to [100, 30000]
        int timeoutIdx = -1;
        for (int i = 0; i < args.Count; i++) if (args[i] == "--timeout-ms") { timeoutIdx = i; break; }
        Assert.True(timeoutIdx >= 0 && timeoutIdx + 1 < args.Count, "--timeout-ms should have a value");
        int ms = int.Parse(args[timeoutIdx + 1]);
        Assert.True(ms >= 100 && ms <= 30000);

        // No --capture-one-frame-active-window
        Assert.DoesNotContain(args, a => a.Contains("active-window"));
    }

    [Fact]
    public void Stop_ReturnsCachedMeta_AfterSuccessfulStart()
    {
        var fake = new FakeWgcProcessRunner(new WgcHelperProcessResult
        {
            ExitCode = 0,
            StandardOutput = "RESULT: OK\nStage: Complete\nWidth: 1280\nHeight: 720\nFileSize: 50000\nCaptureMethod: WGC_D3D11_FRAME_SURFACE\nOutput: .local-data/wgc-tests/meta.png\n",
            StandardError = ""
        });
        var backend = new WgcWindowCaptureBackend(fake, "fake.exe");
        backend.Start(new CaptureConfig { SourceKind = "window", WindowHandle = new nint(1234), OutputPath = "rec.mp4" });

        var meta = backend.Stop();
        Assert.NotNull(meta);
        Assert.Equal(1280, meta.Width);
        Assert.Equal(720, meta.Height);
        Assert.Equal(50000, meta.SizeBytes);

        // Calling Stop again returns the same cached meta
        var meta2 = backend.Stop();
        Assert.Same(meta, meta2);
    }

    [Fact]
    public void Stop_WithoutStart_ReturnsEmptyMeta()
    {
        var fake = new FakeWgcProcessRunner(new WgcHelperProcessResult());
        var backend = new WgcWindowCaptureBackend(fake, "fake.exe");
        var meta = backend.Stop();
        Assert.NotNull(meta);
        Assert.Equal(0, meta.SizeBytes);
        Assert.Equal(0, meta.Width);
        Assert.Equal(0, meta.Height);
        Assert.Equal(-1, backend.ExitCode);
    }

    [Fact]
    public void OnNaturalExit_Fires_OnSuccess()
    {
        var fake = new FakeWgcProcessRunner(new WgcHelperProcessResult
        {
            ExitCode = 0,
            StandardOutput = "RESULT: OK\nStage: Complete\nWidth: 1280\nHeight: 720\nFileSize: 50000\nCaptureMethod: WGC_D3D11_FRAME_SURFACE\nOutput: .local-data/wgc-tests/meta.png\n",
            StandardError = ""
        });
        var backend = new WgcWindowCaptureBackend(fake, "fake.exe");

        int exitReceived = -1;
        OutputMeta? metaReceived = null;
        backend.OnNaturalExit((ec, m) => { exitReceived = ec; metaReceived = m; });

        backend.Start(new CaptureConfig { SourceKind = "window", WindowHandle = new nint(1234), OutputPath = "rec.mp4" });

        Assert.Equal(0, exitReceived);
        Assert.NotNull(metaReceived);
        Assert.Equal("png", metaReceived.Container);
        Assert.Equal("still-frame", metaReceived.Codec);
    }

    [Fact]
    public void Dispose_CanBeCalledMultipleTimes()
    {
        var backend = new WgcWindowCaptureBackend(new FakeWgcProcessRunner(new WgcHelperProcessResult()), "fake.exe");
        backend.Dispose();
        backend.Dispose();
        // No exception
    }

    [Fact]
    public void Start_NullConfig_ThrowsArgumentNullException()
    {
        var backend = new WgcWindowCaptureBackend(new FakeWgcProcessRunner(new WgcHelperProcessResult()), "fake.exe");
        Assert.Throws<ArgumentNullException>(() => backend.Start(null!));
    }

    // Task 41: When stdout FileSize is 0 but real PNG file exists with correct
    // size, the backend uses FileInfo.Length to populate SizeBytes.
    [Fact]
    public void Start_HelperReportsZeroBytes_UsesRealFileSizeAsFallback()
    {
        string tempPng = Path.Combine(Path.GetTempPath(), "wgc-fallback-test-" + Guid.NewGuid().ToString("N") + ".png");
        try
        {
            // Write a tiny PNG (1KB of padding) to simulate real output file
            byte[] data = new byte[1024];
            // PNG signature bytes
            data[0] = 0x89;
            data[1] = 0x50; // 'P'
            data[2] = 0x4E; // 'N'
            data[3] = 0x47; // 'G'
            data[4] = 0x0D;
            data[5] = 0x0A;
            data[6] = 0x1A;
            data[7] = 0x0A;
            File.WriteAllBytes(tempPng, data);

            var fake = new FakeWgcProcessRunner(new WgcHelperProcessResult
            {
                ExitCode = 0,
                StandardOutput =
                    "RESULT: OK\nStage: Complete\nWidth: 100\nHeight: 100\nFileSize: 0\nCaptureMethod: WGC_D3D11_FRAME_SURFACE\nOutput: " + tempPng + "\n",
                StandardError = ""
            });
            var backend = new WgcWindowCaptureBackend(fake, "fake.exe");

            backend.Start(new CaptureConfig
            {
                SourceKind = "window",
                WindowHandle = new nint(0x1234),
                OutputPath = "rec.mp4"
            });

            var meta = backend.Stop();
            Assert.NotNull(meta);
            Assert.Equal(1024, meta.SizeBytes); // real file size, not 0 from stdout
        }
        finally
        {
            if (File.Exists(tempPng))
                File.Delete(tempPng);
        }
    }

    // Task 41: When stdout FileSize differs from actual file size, prefer file size
    [Fact]
    public void Start_FileSizeMismatch_PrefersActualFileSize()
    {
        string tempPng = Path.Combine(Path.GetTempPath(), "wgc-size-mismatch-" + Guid.NewGuid().ToString("N") + ".png");
        try
        {
            // Write 2048-byte PNG file (with valid signature)
            byte[] data = new byte[2048];
            data[0] = 0x89;
            data[1] = 0x50;
            data[2] = 0x4E;
            data[3] = 0x47;
            data[4] = 0x0D;
            data[5] = 0x0A;
            data[6] = 0x1A;
            data[7] = 0x0A;
            File.WriteAllBytes(tempPng, data);

            // Helper reports a WRONG FileSize (e.g. 99999) - should use real 2048
            var fake = new FakeWgcProcessRunner(new WgcHelperProcessResult
            {
                ExitCode = 0,
                StandardOutput =
                    "RESULT: OK\nStage: Complete\nWidth: 100\nHeight: 100\nFileSize: 99999\nCaptureMethod: WGC_D3D11_FRAME_SURFACE\nOutput: " + tempPng + "\n",
                StandardError = ""
            });
            var backend = new WgcWindowCaptureBackend(fake, "fake.exe");

            backend.Start(new CaptureConfig
            {
                SourceKind = "window",
                WindowHandle = new nint(0x1234),
                OutputPath = "rec.mp4"
            });

            var meta = backend.Stop();
            Assert.NotNull(meta);
            // Real file size (2048) must be used, NOT stdout value (99999)
            Assert.Equal(2048, meta.SizeBytes);
        }
        finally
        {
            if (File.Exists(tempPng))
                File.Delete(tempPng);
        }
    }

    // Task 41: PNG signature validation - file smaller than 512 bytes triggers warning
    [Fact]
    public void Start_FileSmallerThan512Bytes_TriggersSmallSizeWarning()
    {
        string tempPng = Path.Combine(Path.GetTempPath(), "wgc-tiny-" + Guid.NewGuid().ToString("N") + ".png");
        try
        {
            byte[] data = new byte[100]; // too small
            data[0] = 0x89;
            data[1] = 0x50;
            data[2] = 0x4E;
            data[3] = 0x47;
            data[4] = 0x0D;
            data[5] = 0x0A;
            data[6] = 0x1A;
            data[7] = 0x0A;
            File.WriteAllBytes(tempPng, data);

            var fake = new FakeWgcProcessRunner(new WgcHelperProcessResult
            {
                ExitCode = 0,
                StandardOutput =
                    "RESULT: OK\nStage: Complete\nWidth: 10\nHeight: 10\nFileSize: 100\nCaptureMethod: WGC_D3D11_FRAME_SURFACE\nOutput: " + tempPng + "\n",
                StandardError = ""
            });
            var backend = new WgcWindowCaptureBackend(fake, "fake.exe");

            backend.Start(new CaptureConfig
            {
                SourceKind = "window",
                WindowHandle = new nint(0x1234),
                OutputPath = "rec.mp4"
            });

            var meta = backend.Stop();
            Assert.NotNull(meta);
            // Backend prefers real file size
            Assert.Equal(100, meta.SizeBytes);
            // Should include a warning about file size being too small
            string allWarns = string.Join(" ", meta.Warnings ?? []);
            Assert.Contains("smaller than 512", allWarns, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            if (File.Exists(tempPng))
                File.Delete(tempPng);
        }
    }

    // Task 41: PNG signature validation - invalid signature triggers warning
    [Fact]
    public void Start_InvalidPngSignature_TriggersInvalidPngWarning()
    {
        string tempPng = Path.Combine(Path.GetTempPath(), "wgc-badsig-" + Guid.NewGuid().ToString("N") + ".png");
        try
        {
            byte[] data = new byte[1024];
            // NOT a PNG signature - just zero bytes (will NOT pass 89 50 4E 47 check)
            File.WriteAllBytes(tempPng, data);

            var fake = new FakeWgcProcessRunner(new WgcHelperProcessResult
            {
                ExitCode = 0,
                StandardOutput =
                    "RESULT: OK\nStage: Complete\nWidth: 100\nHeight: 100\nFileSize: 1024\nCaptureMethod: WGC_D3D11_FRAME_SURFACE\nOutput: " + tempPng + "\n",
                StandardError = ""
            });
            var backend = new WgcWindowCaptureBackend(fake, "fake.exe");

            backend.Start(new CaptureConfig
            {
                SourceKind = "window",
                WindowHandle = new nint(0x1234),
                OutputPath = "rec.mp4"
            });

            var meta = backend.Stop();
            Assert.NotNull(meta);
            string allWarns = string.Join(" ", meta.Warnings ?? []);
            Assert.Contains("png", allWarns, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            if (File.Exists(tempPng))
                File.Delete(tempPng);
        }
    }

    // Task 41: PNG signature validation - file doesn't exist triggers warning
    [Fact]
    public void Start_MissingOutputFile_TriggersMissingFileWarning()
    {
        // Point to a non-existent path
        string nonexistent = Path.Combine(Path.GetTempPath(), "wgc-nope-" + Guid.NewGuid().ToString("N") + ".png");

        var fake = new FakeWgcProcessRunner(new WgcHelperProcessResult
        {
            ExitCode = 0,
            StandardOutput =
                "RESULT: OK\nStage: Complete\nWidth: 100\nHeight: 100\nFileSize: 1024\nCaptureMethod: WGC_D3D11_FRAME_SURFACE\nOutput: " + nonexistent + "\n",
            StandardError = ""
        });
        var backend = new WgcWindowCaptureBackend(fake, "fake.exe");

        backend.Start(new CaptureConfig
        {
            SourceKind = "window",
            WindowHandle = new nint(0x1234),
            OutputPath = "rec.mp4"
        });

        var meta = backend.Stop();
        Assert.NotNull(meta);
        string allWarns = string.Join(" ", meta.Warnings ?? []);
        // Should include a warning about missing output file
        Assert.Contains("missing", allWarns, StringComparison.OrdinalIgnoreCase);
    }
}

/// <summary>
/// RecordingEngine integration tests for WGC still-frame: verifies
/// state transitions (recording -> completed, without being
/// overwritten back to recording), output metadata semantics
/// (container=png, codec=still-frame) and the absence of
/// FFmpeg-specific warnings.
///
/// Tests inject a WgcWindowCaptureBackend wired to a fake process
/// runner — no real helper is launched, no real capture is performed,
/// no PNG is written to disk.
///
/// Audit log isolation: every test redirects AGENT_RECORDER_DATA_DIR
/// to a unique temp directory so we never write to
/// %LOCALAPPDATA%\AgentRecorder\logs in shared / CI environments.
/// The collection disables parallel execution with other tests that
/// also touch AGENT_RECORDER_DATA_DIR.
/// </summary>
[Collection("NonParallel-AgentRecorderDataDir")]
public class RecordingEngineWgcStillFrameTests
{
    private sealed class FakeTray : ITrayContext
    {
        public string HostMode => "headless";
        public bool SupportsRegionSelectionUi => false;

        public int SetRecordingCallCount;
        public int SetIdleCallCount;
        public string? LastError;

        public void RequestConfirmation(object summary, Action<bool> callback)
        {
            // Auto-approve so CreateRecording proceeds to StartCapture.
            callback(true);
        }

        public void RequestRegionSelection(int timeoutSeconds,
            Action<string, int, int, int, int, string, string> callback)
        {
            callback("display_unavailable", 0, 0, 0, 0, "", "virtual_screen");
        }

        public void SetRecording(object rec) { SetRecordingCallCount++; }
        public void SetIdle(object rec) { SetIdleCallCount++; }
        public void SetAllIdle() { SetIdleCallCount++; }
        public void ShowError(string text) { LastError = text; }
    }

    /// <summary>
    /// Redirects AGENT_RECORDER_DATA_DIR to a unique temp directory for
    /// the lifetime of the using-block; restores the original value on
    /// dispose. The caller must still keep tests non-parallel because
    /// environment variables are process-scoped state.
    /// </summary>
    private sealed class IsolatedAuditDataDir : IDisposable
    {
        private readonly string? _oldDataDir;
        public string TestDataDir { get; }

        public IsolatedAuditDataDir()
        {
            _oldDataDir = Environment.GetEnvironmentVariable("AGENT_RECORDER_DATA_DIR");
            TestDataDir = Path.Combine(
                Path.GetTempPath(),
                "AgentRecorder.Tests",
                "wgc-still-frame",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(TestDataDir);
            Environment.SetEnvironmentVariable("AGENT_RECORDER_DATA_DIR", TestDataDir);
        }

        public string ExpectedAuditLogPath =>
            Path.Combine(TestDataDir, "logs", "audit.jsonl");

        public void Dispose()
        {
            Environment.SetEnvironmentVariable("AGENT_RECORDER_DATA_DIR", _oldDataDir);
            try
            {
                if (Directory.Exists(TestDataDir))
                    Directory.Delete(TestDataDir, recursive: true);
            }
            catch
            {
                // Best-effort cleanup — never throw on teardown.
            }
        }
    }

    private static RecordingEngine CreateEngine(
        out FakeTray tray,
        IWgcHelperProcessRunner runner)
    {
        tray = new FakeTray();
        // At this point the caller has already redirected
        // AGENT_RECORDER_DATA_DIR to a test-only temp directory, so
        // AuditLogger will NOT write to %LOCALAPPDATA%\AgentRecorder.
        var logger = new AgentRecorder.Logging.AuditLogger();
        var engine = new RecordingEngine(logger);
        engine.BackendFactory = _ =>
        {
            var backend = new WgcWindowCaptureBackend(runner, "fake.exe");
            return (backend, "wgc");
        };
        engine.SetTray(tray);
        return engine;
    }

    private static JsonNode BuildWindowConfig()
    {
        // NOTE: this config isn't actually fed to ConfigParser (that would
        // need a real HWND returned by SystemQuery.EnumWindows). Instead we
        // build the Recording manually and call the test-only hook
        // RecordingEngine.StartCaptureForTests.
        const string json =
            "{\"source\": {\"type\": \"window\", \"window\": {\"hwnd\": \"0x12345\", \"title\": \"test\"}}," +
            " \"output\": {\"path\": \".local-data/wgc-tests/engine.png\"}}";
        return JsonNode.Parse(json)!;
    }

    [Fact]
    public void StartCapture_WgcStillFrameSuccess_FinalizesAsCompleted()
    {
        string? beforeDataDir = Environment.GetEnvironmentVariable("AGENT_RECORDER_DATA_DIR");
        string? tempPng = null;
        try
        {
            tempPng = WriteTempPng(size: 8192);

            using var scope = new IsolatedAuditDataDir();
            Assert.StartsWith(Path.GetTempPath(), scope.ExpectedAuditLogPath, StringComparison.Ordinal);

            var runner = new FakeWgcHelperProcessRunner(new WgcHelperProcessResult
            {
                ExitCode = 0,
                StandardOutput =
                    "RESULT: OK\nStage: Complete\nWidth: 1920\nHeight: 1080\nFileSize: 8192 bytes\nCaptureMethod: WGC_D3D11_FRAME_SURFACE\nOutput: " + tempPng + "\n",
                StandardError = ""
            });
            var engine = CreateEngine(out var tray, runner);

            var rec = new Recording
            {
                SourceType = "window",
                SourceTitle = "test",
                OutputPath = tempPng,
                Backend = new WgcWindowCaptureBackend(runner, "fake.exe"),
                BackendType = "wgc",
                Config = { SourceKind = "window", WindowHandle = new nint(0x12345), OutputPath = tempPng }
            };
            engine.StartCaptureForTests(rec, tray);

            Assert.Equal(RecState.completed, rec.State);
            Assert.True(rec.CompletedAtUtc.HasValue);
            Assert.Equal("wgc", rec.BackendType);

            var meta = rec.LastMeta;
            Assert.NotNull(meta);
            Assert.Equal("png", meta.Container);
            Assert.Equal("still-frame", meta.Codec);
            Assert.Equal(1920, meta.Width);
            Assert.Equal(1080, meta.Height);
            Assert.True(meta.OutputFileExists);
            Assert.True(meta.IsValidPngSignature);
            Assert.Equal(8192, meta.SizeBytes);

            Assert.DoesNotContain(meta.Warnings, w => w.Contains("Duration is 0", StringComparison.Ordinal));
            Assert.DoesNotContain(rec.Warnings, w => w.Contains("zero_duration", StringComparison.Ordinal));
            Assert.True(tray.SetRecordingCallCount >= 1, "SetRecording should be called before Start()");
            Assert.True(File.Exists(scope.ExpectedAuditLogPath), "Audit log should be written to isolated test directory: " + scope.ExpectedAuditLogPath);
        }
        finally
        {
            if (!string.IsNullOrEmpty(tempPng) && File.Exists(tempPng)) File.Delete(tempPng);
            string? afterDataDir = Environment.GetEnvironmentVariable("AGENT_RECORDER_DATA_DIR");
            Assert.Equal(beforeDataDir, afterDataDir);
        }
    }

    [Fact]
    public void StartCapture_WgcStillFrameLargeNonPngFile_FinalizesAsFailed()
    {
        string? beforeDataDir = Environment.GetEnvironmentVariable("AGENT_RECORDER_DATA_DIR");
        string? tempBad = null;
        try
        {
            tempBad = WriteTempNonPng(size: 8192);

            using var scope = new IsolatedAuditDataDir();

            var runner = new FakeWgcHelperProcessRunner(new WgcHelperProcessResult
            {
                ExitCode = 0,
                StandardOutput =
                    "RESULT: OK\nStage: Complete\nWidth: 1920\nHeight: 1080\nFileSize: 8192\nCaptureMethod: WGC_D3D11_FRAME_SURFACE\nOutput: " + tempBad + "\n",
                StandardError = ""
            });
            var engine = CreateEngine(out var tray, runner);

            var rec = new Recording
            {
                SourceType = "window",
                SourceTitle = "test",
                OutputPath = tempBad,
                Backend = new WgcWindowCaptureBackend(runner, "fake.exe"),
                BackendType = "wgc",
                Config = { SourceKind = "window", WindowHandle = new nint(0x12345), OutputPath = tempBad }
            };
            engine.StartCaptureForTests(rec, tray);

            Assert.Equal(RecState.failed, rec.State);
            Assert.NotNull(rec.Error);
            string allWarns = string.Join("|", rec.Warnings ?? []);
            Assert.Contains("wgc_invalid_png_signature", allWarns, StringComparison.OrdinalIgnoreCase);

            var meta = rec.LastMeta;
            Assert.NotNull(meta);
            Assert.False(meta.IsValidPngSignature);
            Assert.True(meta.OutputFileExists);
        }
        finally
        {
            if (!string.IsNullOrEmpty(tempBad) && File.Exists(tempBad)) File.Delete(tempBad);
            string? afterDataDir = Environment.GetEnvironmentVariable("AGENT_RECORDER_DATA_DIR");
            Assert.Equal(beforeDataDir, afterDataDir);
        }
    }

    [Fact]
    public void StartCapture_WgcStillFrameMissingFile_FinalizesAsFailed()
    {
        string? beforeDataDir = Environment.GetEnvironmentVariable("AGENT_RECORDER_DATA_DIR");
        string nonexistent = Path.Combine(Path.GetTempPath(), "wgc-missing-" + Guid.NewGuid().ToString("N") + ".png");
        try
        {
            using var scope = new IsolatedAuditDataDir();

            var runner = new FakeWgcHelperProcessRunner(new WgcHelperProcessResult
            {
                ExitCode = 0,
                StandardOutput =
                    "RESULT: OK\nStage: Complete\nWidth: 100\nHeight: 100\nFileSize: 1024\nCaptureMethod: WGC_D3D11_FRAME_SURFACE\nOutput: " + nonexistent + "\n",
                StandardError = ""
            });
            var engine = CreateEngine(out var tray, runner);

            var rec = new Recording
            {
                SourceType = "window",
                SourceTitle = "test",
                OutputPath = nonexistent,
                Backend = new WgcWindowCaptureBackend(runner, "fake.exe"),
                BackendType = "wgc",
                Config = { SourceKind = "window", WindowHandle = new nint(0x12345), OutputPath = nonexistent }
            };
            engine.StartCaptureForTests(rec, tray);

            Assert.Equal(RecState.failed, rec.State);
            string allWarns = string.Join("|", rec.Warnings ?? []);
            Assert.Contains("wgc_missing_output", allWarns, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            string? afterDataDir = Environment.GetEnvironmentVariable("AGENT_RECORDER_DATA_DIR");
            Assert.Equal(beforeDataDir, afterDataDir);
        }
    }

    /// <summary>Writes a temp file with valid PNG 8-byte magic header + padding. Caller cleans up.</summary>
    private static string WriteTempPng(int size = 4096)
    {
        string path = Path.Combine(Path.GetTempPath(), "wgc-engine-" + Guid.NewGuid().ToString("N") + ".png");
        byte[] data = new byte[size];
        data[0] = 0x89;
        data[1] = 0x50;
        data[2] = 0x4E;
        data[3] = 0x47;
        data[4] = 0x0D;
        data[5] = 0x0A;
        data[6] = 0x1A;
        data[7] = 0x0A;
        File.WriteAllBytes(path, data);
        return path;
    }

    /// <summary>Writes a temp file WITHOUT a PNG signature. Kept >= 512 bytes so only the signature check fails.</summary>
    private static string WriteTempNonPng(int size = 4096)
    {
        string path = Path.Combine(Path.GetTempPath(), "wgc-bad-" + Guid.NewGuid().ToString("N") + ".png");
        File.WriteAllBytes(path, new byte[size]);
        return path;
    }

    [Fact]
    public void GetStatus_AfterWgcStillFrame_ReportsPngOutput()
    {
        string? beforeDataDir = Environment.GetEnvironmentVariable("AGENT_RECORDER_DATA_DIR");
        string? tempPng = null;
        try
        {
            tempPng = WriteTempPng(size: 51200);

            using var scope = new IsolatedAuditDataDir();
            var runner = new FakeWgcHelperProcessRunner(new WgcHelperProcessResult
            {
                ExitCode = 0,
                StandardOutput =
                    "RESULT: OK\nStage: Complete\nWidth: 800\nHeight: 600\nFileSize: 51200\nCaptureMethod: WGC_D3D11_FRAME_SURFACE\nOutput: " + tempPng + "\n",
                StandardError = ""
            });
            var engine = CreateEngine(out var tray, runner);

            var rec = new Recording
            {
                SourceType = "window",
                SourceTitle = "test",
                OutputPath = tempPng,
                Backend = new WgcWindowCaptureBackend(runner, "fake.exe"),
                BackendType = "wgc",
                Config = { SourceKind = "window", WindowHandle = new nint(0x12345), OutputPath = tempPng }
            };
            engine.StartCaptureForTests(rec, tray);

            var status = engine.GetStatus(rec.Id);
            var props = status.GetType().GetProperties()
                .ToDictionary(p => p.Name, p => p.GetValue(status));
            var output = props["output"]!;
            var outputProps = output.GetType().GetProperties()
                .ToDictionary(p => p.Name, p => p.GetValue(output));

            Assert.Equal("completed", props["status"]?.ToString());
            Assert.Equal("png", outputProps["container"]?.ToString());
            Assert.Equal("still-frame", outputProps["codec"]?.ToString());
            Assert.Equal(800, Convert.ToInt32(outputProps["width"],
                System.Globalization.CultureInfo.InvariantCulture));
            Assert.Equal(600, Convert.ToInt32(outputProps["height"],
                System.Globalization.CultureInfo.InvariantCulture));
            Assert.Contains("wgc", outputProps["path"]?.ToString() ?? string.Empty,
                StringComparison.OrdinalIgnoreCase);

            Assert.True(File.Exists(scope.ExpectedAuditLogPath),
                "Audit log should be written to isolated test directory.");
        }
        finally
        {
            if (!string.IsNullOrEmpty(tempPng) && File.Exists(tempPng)) File.Delete(tempPng);
            Assert.Equal(beforeDataDir, Environment.GetEnvironmentVariable("AGENT_RECORDER_DATA_DIR"));
        }
    }

    [Fact]
    public void GetOutput_AfterWgcStillFrame_ReturnsPngMeta()
    {
        string? beforeDataDir = Environment.GetEnvironmentVariable("AGENT_RECORDER_DATA_DIR");
        string? tempPng = null;
        try
        {
            tempPng = WriteTempPng(size: 32768);

            using var scope = new IsolatedAuditDataDir();
            var runner = new FakeWgcHelperProcessRunner(new WgcHelperProcessResult
            {
                ExitCode = 0,
                StandardOutput =
                    "RESULT: OK\nStage: Complete\nWidth: 1024\nHeight: 768\nFileSize: 32768\nCaptureMethod: WGC_D3D11_FRAME_SURFACE\nOutput: " + tempPng + "\n",
                StandardError = ""
            });
            var engine = CreateEngine(out var tray, runner);

            var rec = new Recording
            {
                SourceType = "window",
                SourceTitle = "test",
                OutputPath = tempPng,
                Backend = new WgcWindowCaptureBackend(runner, "fake.exe"),
                BackendType = "wgc",
                Config = { SourceKind = "window", WindowHandle = new nint(0x12345), OutputPath = tempPng }
            };
            engine.StartCaptureForTests(rec, tray);

            var output = engine.GetOutput(rec.Id);
            var props = output.GetType().GetProperties()
                .ToDictionary(p => p.Name, p => p.GetValue(output));
            var inner = props["output"]!;
            var innerProps = inner.GetType().GetProperties()
                .ToDictionary(p => p.Name, p => p.GetValue(inner));

            Assert.Equal("png", innerProps["container"]?.ToString());
            Assert.Equal("still-frame", innerProps["codec"]?.ToString());

            var warnings = (System.Collections.IEnumerable?)props["warnings"];
            string concat = warnings == null ? string.Empty :
                string.Join("|", warnings.Cast<object>());
            Assert.DoesNotContain("Duration is 0", concat, StringComparison.Ordinal);

            Assert.True(File.Exists(scope.ExpectedAuditLogPath),
                "Audit log should be written to isolated test directory.");
        }
        finally
        {
            if (!string.IsNullOrEmpty(tempPng) && File.Exists(tempPng)) File.Delete(tempPng);
            Assert.Equal(beforeDataDir, Environment.GetEnvironmentVariable("AGENT_RECORDER_DATA_DIR"));
        }
    }

    [Fact]
    public void DefaultBackendFactory_StillSelectsFfmpeg_ForRegressionSafety()
    {
        string? beforeDataDir = Environment.GetEnvironmentVariable("AGENT_RECORDER_DATA_DIR");

        using (var scope = new IsolatedAuditDataDir())
        {
            var engine = new RecordingEngine(new AgentRecorder.Logging.AuditLogger());
            var factory = engine.BackendFactory;
            var (backend, backendType) = factory("window");

            Assert.NotNull(backend);
            Assert.Equal("ffmpeg-gdigrab", backendType);
        }

        Assert.Equal(beforeDataDir, Environment.GetEnvironmentVariable("AGENT_RECORDER_DATA_DIR"));
    }
}
