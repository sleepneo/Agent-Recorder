using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using Xunit;
using AgentRecorder.Capture;

namespace AgentRecorder.Tests;

/// <summary>
/// Tests for WGC helper IPC contract parsing + Runner behavior.
/// Uses a fake IWgcHelperProcessRunner — no real process launch, no
/// real capture, no PNG generation.
/// </summary>
public sealed class WgcHelperOutputParserTests
{
    private const string SampleSuccessOutput = @"RESULT: OK
Stage: Complete
Output: C:\Users\dev\.local-data\wgc-tests\wgc-frame.png
Width: 1280
Height: 720
FileSize: 123456
SHA-256: 3A5F2E9B8C7D
DisplayName: Notepad
CaptureMethod: WGC_D3D11_FRAME_SURFACE
HWND: 0x0000000000012345
";

    private const string SampleTimeoutOutput = @"RESULT: FAIL
Stage: FrameArrived(timeout)
HRESULT: 0x800705B4
Reason: Timed out waiting for first WGC frame.
";

    private const string SamplePathRejectedOutput = @"RESULT: FAIL
Stage: ValidateOutputPath
HRESULT: 0x80070005
Reason: Output path must be under .local-data\wgc-tests\ or %TEMP%\ / %TMP%\.
";

    // 1. parse success output
    [Fact]
    public void Parse_SuccessOutput_MapsAllFields()
    {
        var result = WgcHelperOutputParser.Parse(exitCode: 0, SampleSuccessOutput, stderr: "");

        Assert.True(result.Success);
        Assert.Equal("OK", result.ResultToken);
        Assert.Equal("Complete", result.Stage);
        Assert.Equal(@"C:\Users\dev\.local-data\wgc-tests\wgc-frame.png", result.OutputPath);
        Assert.Equal(1280, result.Width);
        Assert.Equal(720, result.Height);
        Assert.Equal(123456, result.FileSize);
        Assert.Equal("3A5F2E9B8C7D", result.Sha256);
        Assert.Equal("Notepad", result.DisplayName);
        Assert.Equal("WGC_D3D11_FRAME_SURFACE", result.CaptureMethod);
        Assert.Equal("0x0000000000012345", result.Hwnd);
        Assert.Equal(0, result.ExitCode);
        Assert.False(string.IsNullOrEmpty(result.RawStandardOutput));
    }

    // 2. parse timeout failure
    [Fact]
    public void Parse_TimeoutFailure_MapsHresultAndStage()
    {
        var result = WgcHelperOutputParser.Parse(exitCode: 1, SampleTimeoutOutput, stderr: "");

        Assert.False(result.Success);
        Assert.Equal("FAIL", result.ResultToken);
        Assert.Equal("FrameArrived(timeout)", result.Stage);
        Assert.Equal("0x800705B4", result.Hresult);
        Assert.Equal("Timed out waiting for first WGC frame.", result.Reason);
        Assert.Equal(1, result.ExitCode);
    }

    // 3. parse path rejection failure
    [Fact]
    public void Parse_PathRejection_MapsStageAndReason()
    {
        var result = WgcHelperOutputParser.Parse(exitCode: 1, SamplePathRejectedOutput, stderr: "");

        Assert.False(result.Success);
        Assert.Equal("ValidateOutputPath", result.Stage);
        Assert.Equal("0x80070005", result.Hresult);
        Assert.Contains("Output path must be under", result.Reason, StringComparison.Ordinal);
    }

    // 4. missing fields / unknown fields do not throw; raw output preserved
    [Fact]
    public void Parse_MissingAndUnknownFields_DoesNotThrow()
    {
        var partialOutput = @"RESULT: OK
Stage: Partial
UnknownField: whatever
Width: 640
";

        var result = WgcHelperOutputParser.Parse(exitCode: 0, partialOutput, stderr: "stderr noise");

        // Should still succeed on RESULT: OK and exit 0
        Assert.True(result.Success);
        Assert.Equal("Partial", result.Stage);
        Assert.Equal(640, result.Width);

        // Missing numeric fields: Height/FileSize default 0; not throw.
        Assert.Equal(0, result.Height);
        Assert.Equal(0, result.FileSize);

        // Raw output preserved for audit.
        Assert.Contains("UnknownField", result.RawStandardOutput, StringComparison.Ordinal);
        Assert.Contains("stderr noise", result.RawStandardError, StringComparison.Ordinal);
    }

    // 4b. duplicate fields: last value wins
    [Fact]
    public void Parse_DuplicateStage_LastWins()
    {
        var output = @"Stage: Alpha
Stage: Beta
RESULT: OK
";
        var result = WgcHelperOutputParser.Parse(exitCode: 0, output, stderr: "");
        Assert.Equal("Beta", result.Stage);
        Assert.True(result.Success);
    }

    // 4c. non-numeric Width/Height should not throw, stay at 0
    [Fact]
    public void Parse_NonNumericDimensions_NoThrow()
    {
        var output = @"RESULT: OK
Width: not-a-number
Height: 1234
";
        var result = WgcHelperOutputParser.Parse(exitCode: 0, output, stderr: "");
        Assert.Equal(0, result.Width); // couldn't parse
        Assert.Equal(1234, result.Height);
    }

    // 4d. empty input doesn't throw
    [Fact]
    public void Parse_EmptyInput_NoThrow()
    {
        var result = WgcHelperOutputParser.Parse(exitCode: 0, stdout: "", stderr: "");
        Assert.NotNull(result);
        // Empty stdout: no RESULT token, therefore not a success
        Assert.False(result.Success);
    }

    // 4e. blank input doesn't throw, doesn't crash
    [Fact]
    public void Parse_WhitespaceInput_NoThrow()
    {
        var result = WgcHelperOutputParser.Parse(exitCode: 0, "   \r\n \n", " ");
        Assert.NotNull(result);
        Assert.Null(result.ResultToken);
        // Whitespace-only stdout: no RESULT token, therefore not a success
        Assert.False(result.Success);
    }

    // 5. ExitCode != 0 + RESULT: OK in stdout must fail final success
    [Fact]
    public void Parse_NonZeroExitCodeButResultOk_TreatedAsFailure()
    {
        var result = WgcHelperOutputParser.Parse(exitCode: 1, SampleSuccessOutput, stderr: "");

        Assert.False(result.Success);
        Assert.Equal("OK", result.ResultToken); // parsed OK but exit code overrides
        Assert.Equal(1, result.ExitCode);
    }

    // 5b. ExitCode 0 but RESULT: FAIL -> failure
    [Fact]
    public void Parse_ZeroExitCodeButResultFail_TreatedAsFailure()
    {
        var result = WgcHelperOutputParser.Parse(exitCode: 0, SampleTimeoutOutput, stderr: "");

        Assert.False(result.Success);
        Assert.Equal("FAIL", result.ResultToken);
    }

    // 5c. no RESULT token at all
    [Fact]
    public void Parse_NoResultToken_TreatedAsFailure()
    {
        var output = @"Stage: IsWindow(HWND)
Width: 800";
        var result = WgcHelperOutputParser.Parse(exitCode: 0, output, stderr: "");
        Assert.False(result.Success);
        Assert.Null(result.ResultToken);
    }

    // Task 41: FileSize with " bytes" suffix (real helper stdout format)
    [Fact]
    public void Parse_FileSizeWithBytesSuffix_ParsesNumber()
    {
        var output = "RESULT: OK\nFileSize: 123456 bytes\nWidth: 1920\nHeight: 1080\n";
        var result = WgcHelperOutputParser.Parse(exitCode: 0, output, stderr: "");

        Assert.True(result.Success);
        Assert.Equal(123456, result.FileSize);
    }

    // Task 41: FileSize with "Bytes" capitalized
    [Fact]
    public void Parse_FileSizeWithBytesCapitalized_ParsesNumber()
    {
        var output = "RESULT: OK\nFileSize: 65536 Bytes\nWidth: 800\nHeight: 600\n";
        var result = WgcHelperOutputParser.Parse(exitCode: 0, output, stderr: "");

        Assert.True(result.Success);
        Assert.Equal(65536, result.FileSize);
    }

    // Task 41: FileSize with leading/trailing whitespace around value
    [Fact]
    public void Parse_FileSizeWithWhitespaceAroundBytes_ParsesNumber()
    {
        var output = "RESULT: OK\nFileSize:   98765   bytes\nWidth: 800\nHeight: 600\n";
        var result = WgcHelperOutputParser.Parse(exitCode: 0, output, stderr: "");

        Assert.True(result.Success);
        Assert.Equal(98765, result.FileSize);
    }

    // Task 41: FileSize as plain number (legacy format) still works
    [Fact]
    public void Parse_FileSizePlainNumber_StillWorks()
    {
        var output = "RESULT: OK\nFileSize: 123456\n";
        var result = WgcHelperOutputParser.Parse(exitCode: 0, output, stderr: "");

        Assert.True(result.Success);
        Assert.Equal(123456, result.FileSize);
    }
}

/// <summary>
/// Fake IWgcHelperProcessRunner that captures the arguments passed to
/// it and returns a preconfigured WgcHelperProcessResult. Does not
/// launch any real process.
/// </summary>
public sealed class FakeWgcHelperProcessRunner : IWgcHelperProcessRunner
{
    public string? LastFileName { get; private set; }
    public IReadOnlyList<string>? LastArgumentList { get; private set; }
    public int LastTimeoutMs { get; private set; }

    private readonly WgcHelperProcessResult _resultToReturn;

    public FakeWgcHelperProcessRunner(WgcHelperProcessResult resultToReturn)
    {
        _resultToReturn = resultToReturn ?? new WgcHelperProcessResult();
    }

    public WgcHelperProcessResult Run(
        string fileName,
        IReadOnlyList<string> argumentList,
        int timeoutMs,
        CancellationToken cancellationToken = default)
    {
        LastFileName = fileName;
        LastArgumentList = new List<string>(argumentList);
        LastTimeoutMs = timeoutMs;
        return _resultToReturn;
    }
}

public sealed class WgcHelperRunnerTests
{
    // 6. Runner uses ArgumentList with expected order & values
    [Fact]
    public void Run_BuildsExpectedArgumentList()
    {
        var fakeProcResult = new WgcHelperProcessResult
        {
            ExitCode = 0,
            StandardOutput = "RESULT: OK\nStage: Complete\n",
            StandardError = "",
        };
        var fake = new FakeWgcHelperProcessRunner(fakeProcResult);
        var runner = new WgcHelperRunner("C:\\tools\\wgc-native-helper.exe", fake);

        var opts = new WgcHelperOptions
        {
            Hwnd = new nint(0x12345),
            OutputPath = "C:\\Users\\dev\\.local-data\\wgc-tests\\out.png",
            TimeoutMs = 2000,
        };

        var result = runner.Run(opts);

        Assert.NotNull(fake.LastArgumentList);
        var args = fake.LastArgumentList;

        // Fixed order:
        // --capture-one-frame-window
        // <hex hwnd>
        // --i-understand-this-captures-screen
        // --output
        // <path>
        // --timeout-ms
        // <ms>
        Assert.Equal(7, args.Count);
        Assert.Equal("--capture-one-frame-window", args[0]);
        Assert.Equal("0x0000000000012345", args[1]); // hex HWND
        Assert.Equal("--i-understand-this-captures-screen", args[2]);
        Assert.Equal("--output", args[3]);
        Assert.Equal(opts.OutputPath, args[4]);
        Assert.Equal("--timeout-ms", args[5]);
        Assert.Equal("2000", args[6]);

        // FileName is the helper exe
        Assert.Equal("C:\\tools\\wgc-native-helper.exe", fake.LastFileName);

        // Final result is OK
        Assert.True(result.Success);
        Assert.Equal("Complete", result.Stage);
    }

    // 7. ProcessStartInfo has required security properties
    [Fact]
    public void BuildStartInfo_SecurityPropertiesAreCorrect()
    {
        var fake = new FakeWgcHelperProcessRunner(new WgcHelperProcessResult());
        var runner = new WgcHelperRunner("C:\\tools\\wgc-native-helper.exe", fake);

        var psi = runner.BuildStartInfo(new WgcHelperOptions
        {
            Hwnd = new nint(0xABCD),
            OutputPath = "C:\\Users\\dev\\.local-data\\wgc-tests\\frame.png",
            TimeoutMs = 5000,
        });

        Assert.False(psi.UseShellExecute);
        Assert.True(psi.RedirectStandardOutput);
        Assert.True(psi.RedirectStandardError);
        Assert.True(psi.CreateNoWindow);

        // ArgumentList must contain --i-understand-this-captures-screen
        Assert.Contains("--i-understand-this-captures-screen", psi.ArgumentList);
        Assert.Contains("--capture-one-frame-window", psi.ArgumentList);
        Assert.Contains("--timeout-ms", psi.ArgumentList);

        // ArgumentList must NOT contain any --capture-one-frame-active-window
        Assert.DoesNotContain("--capture-one-frame-active-window", psi.ArgumentList);
        Assert.DoesNotContain(psi.ArgumentList, a => a != null && a.Contains("active-window"));

        // ArgumentList should NOT be empty shell-joined string
        Assert.All(psi.ArgumentList, a =>
        {
            // Each argument should be a separate token (no unexpected multi-token string)
            Assert.NotNull(a);
        });
    }

    // 8. Runner does not provide --capture-one-frame-active-window
    [Fact]
    public void BuildStartInfo_DoesNotProvideActiveWindow()
    {
        var fake = new FakeWgcHelperProcessRunner(new WgcHelperProcessResult());
        var runner = new WgcHelperRunner("C:\\tools\\wgc-native-helper.exe", fake);
        var psi = runner.BuildStartInfo(new WgcHelperOptions
        {
            Hwnd = new nint(1),
            OutputPath = "C:\\tmp\\x.png",
            TimeoutMs = 1000,
        });

        Assert.DoesNotContain(psi.ArgumentList, a => a != null && a.Contains("active-window"));
        // HWND is always provided
        Assert.Contains(psi.ArgumentList, a => a != null && a.StartsWith("0x"));
    }

    // Timeout clamping: below minimum
    [Fact]
    public void BuildStartInfo_ClampsLowTimeout()
    {
        var fake = new FakeWgcHelperProcessRunner(new WgcHelperProcessResult());
        var runner = new WgcHelperRunner("C:\\tools\\wgc-native-helper.exe", fake);
        var psi = runner.BuildStartInfo(new WgcHelperOptions
        {
            Hwnd = new nint(1),
            OutputPath = "C:\\tmp\\x.png",
            TimeoutMs = 1, // below minimum 100
        });

        // Find --timeout-ms value; it must be clamped to 100
        var idx = psi.ArgumentList.IndexOf("--timeout-ms");
        Assert.True(idx >= 0 && idx + 1 < psi.ArgumentList.Count);
        var val = psi.ArgumentList[idx + 1];
        Assert.Equal("100", val);
    }

    // Timeout clamping: above maximum
    [Fact]
    public void BuildStartInfo_ClampsHighTimeout()
    {
        var fake = new FakeWgcHelperProcessRunner(new WgcHelperProcessResult());
        var runner = new WgcHelperRunner("C:\\tools\\wgc-native-helper.exe", fake);
        var psi = runner.BuildStartInfo(new WgcHelperOptions
        {
            Hwnd = new nint(1),
            OutputPath = "C:\\tmp\\x.png",
            TimeoutMs = 999999, // above maximum 30000
        });

        var idx = psi.ArgumentList.IndexOf("--timeout-ms");
        Assert.True(idx >= 0 && idx + 1 < psi.ArgumentList.Count);
        Assert.Equal("30000", psi.ArgumentList[idx + 1]);
    }

    // Timeout clamping: in-range passes through
    [Fact]
    public void BuildStartInfo_InRangeTimeout_PassesThrough()
    {
        var fake = new FakeWgcHelperProcessRunner(new WgcHelperProcessResult());
        var runner = new WgcHelperRunner("C:\\tools\\wgc-native-helper.exe", fake);
        var psi = runner.BuildStartInfo(new WgcHelperOptions
        {
            Hwnd = new nint(1),
            OutputPath = "C:\\tmp\\x.png",
            TimeoutMs = 1500,
        });

        var idx = psi.ArgumentList.IndexOf("--timeout-ms");
        Assert.Equal("1500", psi.ArgumentList[idx + 1]);
    }

    // Guard: null options -> throw
    [Fact]
    public void Run_NullOptions_Throws()
    {
        var fake = new FakeWgcHelperProcessRunner(new WgcHelperProcessResult());
        var runner = new WgcHelperRunner("C:\\tools\\wgc-native-helper.exe", fake);

        Assert.Throws<ArgumentNullException>(() => runner.Run(null!));
    }

    // Guard: empty helper exe path -> throw
    [Fact]
    public void Ctor_EmptyHelperExe_Throws()
    {
        Assert.Throws<ArgumentException>(() =>
            new WgcHelperRunner("", new FakeWgcHelperProcessRunner(new WgcHelperProcessResult())));
    }

    // Guard: null process runner -> throw
    [Fact]
    public void Ctor_NullProcessRunner_Throws()
    {
        Assert.Throws<ArgumentNullException>(() =>
            new WgcHelperRunner("C:\\tools\\wgc-native-helper.exe", null!));
    }

    // Negative HWND still renders hex correctly
    [Fact]
    public void Run_NegativeHwnd_RendersAsHex()
    {
        var fake = new FakeWgcHelperProcessRunner(new WgcHelperProcessResult
        {
            ExitCode = 0,
            StandardOutput = "RESULT: OK\n",
        });
        var runner = new WgcHelperRunner("C:\\tools\\wgc-native-helper.exe", fake);

        runner.Run(new WgcHelperOptions
        {
            Hwnd = unchecked((nint)(long)0xFFFFFFFFABCDABCD),
            OutputPath = "C:\\tmp\\x.png",
            TimeoutMs = 1000,
        });

        // HWND must start with "0x"
        Assert.StartsWith("0x", fake.LastArgumentList![1], StringComparison.Ordinal);
    }

    // CaptureMethod field flows through on success
    [Fact]
    public void Run_Success_PropagatesCaptureMethod()
    {
        var fake = new FakeWgcHelperProcessRunner(new WgcHelperProcessResult
        {
            ExitCode = 0,
            StandardOutput = "RESULT: OK\nStage: Complete\nCaptureMethod: WGC_D3D11_FRAME_SURFACE\n",
        });
        var runner = new WgcHelperRunner("C:\\tools\\wgc-native-helper.exe", fake);

        var result = runner.Run(new WgcHelperOptions
        {
            Hwnd = new nint(12345),
            OutputPath = "C:\\tmp\\x.png",
            TimeoutMs = 3000,
        });

        Assert.True(result.Success);
        Assert.Equal("WGC_D3D11_FRAME_SURFACE", result.CaptureMethod);
    }

    // Invalid HWND string in the output doesn't crash (treated as string)
    [Fact]
    public void Run_InvalidNumericFields_DoesNotCrash()
    {
        var fake = new FakeWgcHelperProcessRunner(new WgcHelperProcessResult
        {
            ExitCode = 1,
            StandardOutput = "RESULT: FAIL\nStage: IsWindow(HWND)\nWidth: not-a-number\nFileSize: huge\n",
        });
        var runner = new WgcHelperRunner("C:\\tools\\wgc-native-helper.exe", fake);

        var result = runner.Run(new WgcHelperOptions
        {
            Hwnd = new nint(0),
            OutputPath = "C:\\tmp\\x.png",
            TimeoutMs = 1000,
        });

        Assert.False(result.Success);
        Assert.Equal("IsWindow(HWND)", result.Stage);
        Assert.Equal(0, result.Width); // default
        Assert.Equal(0, result.FileSize); // default
    }
}
