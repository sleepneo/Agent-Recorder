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
    /// Whether a floating stop button is shown for each active recording.
    /// Default false so test fakes do not need to implement it.
    /// </summary>
    bool SupportsFloatingStopButton => false;

    /// <summary>
    /// Whether the tray menu provides a stop entry.
    /// </summary>
    bool SupportsTrayStop => false;

    /// <summary>
    /// Whether a global stop hotkey is supported by this host.
    /// </summary>
    bool SupportsGlobalStopHotkey => false;

    /// <summary>
    /// Whether the global stop hotkey is currently registered.
    /// </summary>
    bool IsGlobalStopHotkeyRegistered => false;

    /// <summary>
    /// Human-readable gesture for the global stop hotkey, e.g. "Ctrl+Shift+F10".
    /// </summary>
    string? GlobalStopHotkeyGesture => null;

    /// <summary>
    /// Whether this host exposes the local Ctrl+Shift+F11 Chapter Marks gesture.
    /// Headless hosts keep the marks API but do not claim a local gesture.
    /// </summary>
    bool SupportsChapterMarksLocalHotkey => false;

    /// <summary>
    /// Whether the local Chapter Marks hotkey is registered right now. The tray
    /// implementation reports the dynamic while-recording state.
    /// </summary>
    bool IsChapterMarksHotkeyRegistered => false;

    /// <summary>
    /// Human-readable Chapter Marks gesture, or null when the host has no local UI.
    /// </summary>
    string? ChapterMarksHotkeyGesture => null;

    /// <summary>
    /// Registration policy for the local Chapter Marks gesture.
    /// </summary>
    string ChapterMarksHotkeyRegistrationPolicy => "while_recording";

    /// <summary>
    /// 弹出录屏确认交互（确认窗体 + 托盘菜单，仅限本地用户操作）。
    /// callback 参数：<see cref="ConfirmationDecision"/> 描述用户在本机 UI 的确认结果，
    /// 包括是否批准、本次保存目录覆盖以及是否记住为默认目录。
    /// 注意：这是唯一的确认入口，不允许通过 HTTP API 远程调用确认。
    /// </summary>
    void RequestConfirmation(RecordingConfirmationPresentation presentation, Action<ConfirmationDecision> callback);

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

    void SetRecording(RecordingUiPresentation presentation);
    /// <summary>
    /// Notifies the local host that a recording entered stopping before capture
    /// finalization. Default no-op keeps existing host fakes source-compatible.
    /// </summary>
    void SetStopping(RecordingUiPresentation presentation) { }
    void SetIdle(RecordingUiPresentation presentation);
    void SetAllIdle();
    void ShowError(string text);

    /// <summary>
    /// Shows a non-intrusive "preparing" indicator (e.g. amber border) for the
    /// recording while the microphone or backend initializes.
    /// </summary>
    void SetPreparing(RecordingUiPresentation presentation) { }

    /// <summary>
    /// Shows a countdown overlay for the recording. The engine drives the timing;
    /// the host only updates the visible number. A null
    /// <see cref="RecordingUiPresentation.CountdownRemainingSeconds"/> hides the overlay.
    /// </summary>
    void SetCountdown(RecordingUiPresentation presentation) { }

    /// <summary>
    /// Updates the existing recording indicator for screenshot-series progress.
    /// Captured/planned counts and the next due timestamp are carried by the
    /// immutable presentation. Hosts that do not have local UI may ignore this
    /// notification.
    /// </summary>
    void SetSeriesProgress(RecordingUiPresentation presentation) { }

    /// <summary>
    /// Shows a "finalizing" / "saving" indicator after screen capture has ended.
    /// </summary>
    void SetFinalizing(RecordingUiPresentation presentation) { }
}
