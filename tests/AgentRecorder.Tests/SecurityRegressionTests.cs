using System;
using System.IO;
using System.Linq;
using System.Reflection;
using Xunit;
using AgentRecorder.Capture;
using AgentRecorder.Core;
using AgentRecorder.Infrastructure;

namespace AgentRecorder.Tests;

/// <summary>
/// 安全回归测试 (Task 18)。
///
/// 安全边界：HTTP client 不得通过 API 自行批准或拒绝录屏确认。
/// 录屏确认必须是本地用户在确认窗体或托盘菜单中操作。
/// </summary>
public class SecurityRegressionTests
{
    // ---------------------------------------------------------------------
    // 路径辅助：确保测试在项目根目录下也能访问 src 文件
    // ---------------------------------------------------------------------
    private static string GetProjectRoot()
    {
        // tests/AgentRecorder.Tests/bin/Debug/... 往上 4 层
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (int i = 0; i < 8 && dir != null; i++)
        {
            if (File.Exists(Path.Combine(dir.FullName, "AgentRecorder.sln")))
                return dir.FullName;
            dir = dir.Parent;
        }
        // 回退：当前工作目录
        return Directory.GetCurrentDirectory();
    }

    private static string ReadSource(string relativeToSrc)
    {
        var root = GetProjectRoot();
        var p = Path.Combine(root, "src", relativeToSrc);
        if (!File.Exists(p))
        {
            throw new FileNotFoundException("Cannot locate source file for regression check: " + p);
        }
        return File.ReadAllText(p);
    }

    // =====================================================================
    // 1) POST /confirmations/{id}/approve 必须返回 405 METHOD_NOT_ALLOWED
    //    (通过源代码静态检查保护)
    // =====================================================================

    [Fact]
    public void ApiServer_ConfirmationApprovePost_ReturnsMethodNotAllowed()
    {
        // 保护：确认 ApiServer 路由层对 POST /confirmations/*/approve 返回 405
        string apiServer = ReadSource(Path.Combine("AgentRecorder.Api", "ApiServer.cs"));

        // 必须包含 METHOD_NOT_ALLOWED 字符串和 405 状态码
        Assert.Contains("METHOD_NOT_ALLOWED", apiServer);
        Assert.Contains("405", apiServer);

        // 必须禁止 approve 路径：包含 "approve" 的 seg[2] 检测字符串
        Assert.Contains("seg[2] == \"approve\"", apiServer);

        // 必须不包含旧的 API approve 事件：之前使用的方式已被移除
        // 例如不能有 "_tray.ApprovePending" 调用
        Assert.DoesNotContain("_tray.ApprovePending", apiServer);
        Assert.DoesNotContain("_tray.RejectPending", apiServer);
        // 接口方法本身也不应被调用：不能以任何形式调用 ApprovePending
        Assert.DoesNotContain("ApprovePending", apiServer);
        Assert.DoesNotContain("RejectPending", apiServer);
    }

    [Fact]
    public void ApiServer_ConfirmationRejectPost_ReturnsMethodNotAllowed()
    {
        string apiServer = ReadSource(Path.Combine("AgentRecorder.Api", "ApiServer.cs"));

        // 必须明确拒绝 reject 路径
        Assert.Contains("seg[2] == \"reject\"", apiServer);
        Assert.Contains("METHOD_NOT_ALLOWED", apiServer);
    }

    // =====================================================================
    // 2) ITrayContext 接口不得暴露 ApprovePending/RejectPending 公共方法
    // =====================================================================

    [Fact]
    public void TrayContextInterface_DoesNotExposeApprovalMethods()
    {
        var methods = typeof(ITrayContext).GetMethods(
            BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly);

        var names = methods.Select(m => m.Name).ToList();

        // 关键安全保证：这些方法必须不存在
        Assert.DoesNotContain("ApprovePending", names);
        Assert.DoesNotContain("RejectPending", names);

        // 同时确保合法方法仍然存在
        Assert.Contains("RequestConfirmation", names);
        Assert.Contains("SetRecording", names);
        Assert.Contains("SetIdle", names);
        Assert.Contains("ShowError", names);
    }

    [Fact]
    public void TrayContextInterfaceSource_DoesNotContainApprovalSignatures()
    {
        // 源代码级别的保护（双保险）
        string src = ReadSource(Path.Combine("AgentRecorder.Infrastructure", "ITrayContext.cs"));
        Assert.DoesNotContain("ApprovePending", src);
        Assert.DoesNotContain("RejectPending", src);
    }

    // =====================================================================
    // 3) WGC feature flag 默认行为：不设置 → 使用 FFmpeg gdigrab
    // =====================================================================

    [Fact]
    public void WindowCaptureBackend_WithoutFlag_UsesFfmpegGdigrab()
    {
        // 确保之前设置的环境变量不影响测试
        Environment.SetEnvironmentVariable("AGENT_RECORDER_WINDOW_BACKEND", null);

        var (backend, type) = CaptureBackendSelector.Select("window");

        Assert.NotNull(backend);
        Assert.Equal("ffmpeg-window-region", type);
        Assert.IsType<FfmpegCaptureBackend>(backend);
    }

    [Fact]
    public void WindowCaptureBackend_WithWgcFlag_UsesWgcStub()
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

    // =====================================================================
    // 4) WgcWindowCaptureBackend 不能静默成功 — 必须以明确异常暴露错误
    //    (a) 构造函数在没有 helper exe 时抛出清晰异常
    //    (b) Start 在无效 HWND / 非 window source 时抛出清晰异常
    // =====================================================================

    [Fact]
    public void WgcBackend_Start_FailsClearlyOnInvalidSource()
    {
        var fakeRunner = new FakeWgcHelperProcessRunner(new WgcHelperProcessResult());
        var backend = new WgcWindowCaptureBackend(fakeRunner, "fake.exe");
        var cfg = new CaptureConfig { SourceKind = "window", WindowHandle = 0 };

        var ex = Assert.Throws<InvalidOperationException>(() => backend.Start(cfg));
        Assert.Contains("WGC", ex.Message, StringComparison.Ordinal);
        Assert.Contains("WindowHandle", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void WgcBackend_Start_FailsClearlyWhenHelperReturnsFailure()
    {
        var fakeRunner = new FakeWgcHelperProcessRunner(new WgcHelperProcessResult
        {
            ExitCode = 1,
            StandardOutput = "RESULT: FAIL\nStage: IsWindow(HWND)\nHRESULT: 0x80070057\n",
            StandardError = ""
        });
        var backend = new WgcWindowCaptureBackend(fakeRunner, "fake.exe");
        var cfg = new CaptureConfig { SourceKind = "window", WindowHandle = new nint(1234), OutputPath = "out.png" };

        var ex = Assert.Throws<InvalidOperationException>(() => backend.Start(cfg));
        Assert.Contains("WGC", ex.Message, StringComparison.Ordinal);
        Assert.Contains("0x80070057", ex.Message, StringComparison.Ordinal);
        Assert.Contains("Stage", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void WgcBackend_Start_RequiresExePath_ForSafety()
    {
        // Default ctor defers exe-resolution to Start() time. Start() with a
        // valid config must throw a clear exception mentioning WGC / the exe
        // path / env var because no helper executable is available in the
        // test environment.
        var backend = new WgcWindowCaptureBackend();
        var cfg = new CaptureConfig { SourceKind = "window", WindowHandle = new nint(1234), OutputPath = "out.png" };

        var ex = Assert.ThrowsAny<Exception>(() => backend.Start(cfg));
        Assert.True(
            ex.Message.Contains("WGC", StringComparison.Ordinal) ||
            ex.Message.Contains("helper", StringComparison.OrdinalIgnoreCase),
            "Expected exception to mention WGC or helper (got: " + ex.Message + ")"
        );
    }

    // =====================================================================
    // 5) 启动脚本安全检查：默认不开启 WGC
    // =====================================================================

    [Fact]
    public void StartServerScript_DoesNotSetWgcByDefault()
    {
        var root = GetProjectRoot();
        var script = File.ReadAllText(Path.Combine(root, "scripts", "start-server.ps1"));

        // 默认 WindowBackend 参数为空
        Assert.Contains("WindowBackend = \"\"", script);

        // 必须有条件分支：只有显式传 -WindowBackend wgc 时才设置
        Assert.Contains("if ($WindowBackend -eq \"wgc\")", script);

        // 通过缩进层级追踪：找出所有设置 WGC 的赋值行，检查它们是否在 if 块内
        // PowerShell 的 if/else 块通过缩进来界定
        var lines = script.Split('\n');
        var baseIndent = -1; // 尚未找到 if 块

        bool HasLeadingIfBefore(int lineIdx)
        {
            // 找到在此行之前最近的非空行，检查它是否是 if 语句
            for (int i = lineIdx - 1; i >= 0; i--)
            {
                var prev = lines[i].TrimEnd();
                if (string.IsNullOrEmpty(prev)) continue;
                var prevIndent = prev.Length - prev.TrimStart().Length;
                var prevContent = prev.TrimStart();
                if (prevContent.StartsWith("if ", StringComparison.Ordinal) ||
                    prevContent.StartsWith("else", StringComparison.Ordinal))
                    return true;
                // 如果遇到同级或更外层的语句（缩进 <= 基础缩进），则不在 if 块内
                if (prevIndent <= baseIndent && !string.IsNullOrEmpty(prev.Trim()))
                    break;
            }
            return false;
        }

        // 检查每行：找 WGC 赋值行（不在 if 块内的才报错）
        foreach (var rawLine in lines)
        {
            var line = rawLine.TrimEnd();
            if (string.IsNullOrEmpty(line) || line.TrimStart().StartsWith("#")) continue;

            var trimmed = line.TrimStart();
            var leadingSpaces = line.Length - trimmed.Length;

            // 找到 WGC 赋值行
            if (trimmed.Contains("AGENT_RECORDER_WINDOW_BACKEND") && trimmed.Contains("= \"wgc\""))
            {
                // 确认是在 if 块内（缩进更深）
                bool insideIf = HasLeadingIfBefore(Array.IndexOf(lines, rawLine));
                Assert.True(insideIf,
                    $"WGC 赋值必须在 if 块内。行: {line.Trim()}。" +
                    "如果是在 if 块外（无条件执行），这会破坏安全默认值。");
            }
        }
    }

    [Fact]
    public void BuildAndStartScript_DoesNotSetWgcByDefault()
    {
        var root = GetProjectRoot();
        var script = File.ReadAllText(Path.Combine(root, "scripts", "build-and-start.ps1"));

        // 之前有 bug: 无条件设置 AGENT_RECORDER_WINDOW_BACKEND=wgc
        // 修复后：应被移除或注释
        Assert.DoesNotContain("psi.EnvironmentVariables[\"AGENT_RECORDER_WINDOW_BACKEND\"] = \"wgc\"", script);
    }

    // =====================================================================
    // 6) 回归脚本不应调用 API approve（它们应等待手动用户确认）
    // =====================================================================

    [Fact]
    public void RecordingFlowScript_DoesNotCallConfirmationApprove()
    {
        // 确保回归脚本不包含 POST /confirmations/{id}/approve 作为自动步骤
        // （那会误导开发者认为 API 自确认是可行的）
        // 注意：脚本中可以有注释说明 API 返回 405，但不能有实际的 Invoke-RestMethod 调用
        var root = GetProjectRoot();
        var script = File.ReadAllText(Path.Combine(root, "scripts", "test-recording-flow.ps1"));

        // 检查是否存在 Invoke-RestMethod 调用 POST 到 confirmations/*/approve
        var approveCallPattern = System.Text.RegularExpressions.Regex.IsMatch(
            script,
            @"Invoke-RestMethod.*confirmations.*approve.*-Method\s+POST",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        Assert.False(approveCallPattern,
            "test-recording-flow.ps1 不应包含 POST /confirmations/*/approve 调用 (需要手动用户确认)");
    }

    // =====================================================================
    // 7) TrayContext.RequestConfirmation 不再使用 MessageBox 作为主确认路径
    // =====================================================================

    [Fact]
    public void TrayContext_RequestConfirmation_DoesNotUseMessageBoxAsMainPath()
    {
        // 源代码级检查：RequestConfirmation 不应该调用 MessageBox.Show 作为主确认路径
        string trayContext = ReadSource(Path.Combine("AgentRecorder.App", "TrayContext.cs"));

        // RequestConfirmation 方法不应该包含 MessageBox.Show
        // 注意：MessageBox.Show 在 TrayContext 中已完全移除
        Assert.DoesNotContain("MessageBox.Show", trayContext);
    }

    // =====================================================================
    // 8) TrayContext 使用 ConfirmationQueue
    // =====================================================================

    [Fact]
    public void TrayContext_UsesConfirmationQueue()
    {
        string trayContext = ReadSource(Path.Combine("AgentRecorder.App", "TrayContext.cs"));

        // 必须使用 ConfirmationQueue
        Assert.Contains("ConfirmationQueue", trayContext);
        Assert.Contains("_confirmationQueue", trayContext);

        // 必须使用 ConfirmationForm
        Assert.Contains("ConfirmationForm", trayContext);

        // 必须移除旧的 _pendingCallback 单回调模式
        Assert.DoesNotContain("_pendingCallback", trayContext);
    }

    // =====================================================================
    // 9) TrayContext.RunOnUi 不依赖 Application.OpenForms[0]
    // =====================================================================

    [Fact]
    public void TrayContext_RunOnUi_DoesNotDependOnOpenForms()
    {
        // 源码级检查：TrayContext 必须有独立的 UI dispatcher，
        // 不能依赖第一个打开的窗体，因为托盘应用可能没有打开的窗体，
        // 这会导致 HTTP worker 线程上直接执行 UI 操作。
        string trayContext = ReadSource(Path.Combine("AgentRecorder.App", "TrayContext.cs"));

        // 必须有 _uiInvoker 或类似的独立调度机制
        Assert.Contains("_uiInvoker", trayContext);
        // 不应再使用 OpenForms 索引作为调度方式
        Assert.DoesNotContain("OpenForms[0]", trayContext);
    }

    // =====================================================================
    // 10) TrayContext 程序化关闭必须使用 CloseWithoutResult
    // =====================================================================

    [Fact]
    public void TrayContext_ProgrammaticClose_UsesCloseWithoutResult()
    {
        // 源码级检查：所有程序化关闭当前确认窗体的路径都必须使用 CloseWithoutResult，
        // 不能使用普通的 Close()，否则会误触发用户 X 关闭语义（reject callback）。
        string trayContext = ReadSource(Path.Combine("AgentRecorder.App", "TrayContext.cs"));

        // 必须包含 CloseWithoutResult 调用（现在支持传入 close reason）
        Assert.Contains("CloseWithoutResult", trayContext);

        // 不应包含普通的 _currentForm.Close() 调用
        Assert.DoesNotContain("_currentForm.Close()", trayContext);
    }

    // =====================================================================
    // 11) TrayContext 确认 callback 不应同步运行在 UI 路径
    // =====================================================================

    [Fact]
    public void TrayContext_ConfirmationCallback_NotOnUiThread()
    {
        // 源码级检查：TrayContext 的确认/拒绝操作不应在 UI 线程同步执行外部 callback，
        // callback（可能包含录制启动重逻辑）应该在后台线程执行。
        string trayContext = ReadSource(Path.Combine("AgentRecorder.App", "TrayContext.cs"));

        // 必须包含后台调度，例如 Task.Run
        Assert.Contains("Task.Run(", trayContext);

        // 必须包含统一的确认解析方法
        Assert.Contains("ResolveCurrentConfirmation", trayContext);

        // ApproveFromMenu 方法体内部不应直接调用 ApproveCurrent
        // （因为 ApproveCurrent 会同步执行 callback，阻塞 UI 线程）
        var approveFromMenuPattern = new System.Text.RegularExpressions.Regex(
            @"private\s+void\s+ApproveFromMenu\s*\([^)]*\)\s*\{[\s\S]*?ApproveCurrent\s*\(",
            System.Text.RegularExpressions.RegexOptions.Multiline);
        Assert.DoesNotMatch(approveFromMenuPattern, trayContext);

        // RejectFromMenu 方法体内部不应直接调用 RejectCurrent
        var rejectFromMenuPattern = new System.Text.RegularExpressions.Regex(
            @"private\s+void\s+RejectFromMenu\s*\([^)]*\)\s*\{[\s\S]*?RejectCurrent\s*\(",
            System.Text.RegularExpressions.RegexOptions.Multiline);
        Assert.DoesNotMatch(rejectFromMenuPattern, trayContext);
    }
}
