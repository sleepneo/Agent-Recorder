using System;
namespace AgentRecorder.Infrastructure;

public interface ITrayContext
{
    /// <summary>
    /// Host mode: "tray" for interactive desktop, "headless" for server/non-interactive.
    /// </summary>
    string HostMode { get; }

    /// <summary>
    /// Whether the local UI is available for region selection.
    /// </summary>
    bool SupportsRegionSelectionUi { get; }

    /// <summary>
    /// 弹出录屏确认交互（确认窗体 + 托盘菜单，仅限本地用户操作）。
    /// callback 参数：true = 用户在本机 UI 确认，false = 用户在本机 UI 拒绝。
    /// 注意：这是唯一的确认入口，不允许通过 HTTP API 远程调用确认。
    /// </summary>
    void RequestConfirmation(object summary, Action<bool> callback);

    /// <summary>
    /// 请求本地用户进行区域选择，弹出全屏选区窗口。
    /// callback 参数：
    /// - status: "selected" / "selection_cancelled" / "selection_timeout" / "display_unavailable"
    /// - bounds: 选择的区域坐标（status=selected 时有效）
    /// - displayId: 显示器 ID
    /// - coordinateSpace: 坐标空间
    /// 注意：仅限本地 UI 交互，不允许通过 HTTP API 静默选择。
    /// </summary>
    void RequestRegionSelection(int timeoutSeconds, Action<string, int, int, int, int, string, string> callback);

    void SetRecording(object rec);
    void SetIdle(object rec);
    void SetAllIdle();
    void ShowError(string text);
}
