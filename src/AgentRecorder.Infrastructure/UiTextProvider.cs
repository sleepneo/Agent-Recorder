using System;
using System.Collections.Generic;
using System.Globalization;

namespace AgentRecorder.Infrastructure;

/// <summary>
/// Lightweight in-memory text provider backed by dictionaries.
/// No external localization framework is required.
/// </summary>
public sealed class UiTextProvider : IUiTextProvider
{
    private readonly Dictionary<string, string> _texts;

    public UiTextProvider(UiLanguage language)
    {
        Language = language;
        _texts = language == UiLanguage.ZhCn ? CreateZhCn() : CreateEnUs();
    }

    public UiLanguage Language { get; }

    public string Get(string key)
    {
        if (string.IsNullOrEmpty(key))
            return "";
        return _texts.TryGetValue(key, out var value) ? value : key;
    }

    public string Format(string key, params object?[] args)
    {
        var template = Get(key);
        if (args == null || args.Length == 0)
            return template;
        try
        {
            return string.Format(CultureInfo.InvariantCulture, template, args);
        }
        catch (FormatException)
        {
            return template;
        }
    }

    private static Dictionary<string, string> CreateZhCn()
    {
        return new Dictionary<string, string>(StringComparer.Ordinal)
        {
            // Region selection form
            ["RegionSelection_Info_Default"] = "按住鼠标拖动选区，悬停窗口后点击即可选中。按住 Alt 禁用吸附。按 Enter 确认，Esc 取消。",
            ["RegionSelection_Info_Selected"] = "虚拟坐标：X={0}, Y={1}, W={2}, H={3}  |  按 Enter 确认，Esc 取消",
            ["RegionSelection_Info_TooSmall"] = "选区太小，最小尺寸为 {0}x{0} 像素。",
            ["RegionSelection_Button_Confirm"] = "确认 (Enter)",
            ["RegionSelection_Button_Cancel"] = "取消 (Esc)",
            ["RegionSelection_Coords_Virtual"] = "虚拟坐标：X={0}, Y={1}, W={2}, H={3}",
            ["RegionSelection_Coords_FormBounds"] = "窗体边界：({0}, {1}) -> ({2}, {3})",
            ["RegionSelection_Coords_VirtualScreen"] = "虚拟屏幕：({0}, {1}, {2}x{3})",
            ["RegionSelection_Display"] = "显示器：{0}",
            ["RegionSelection_Display_Unknown"] = "显示器：未知",
            ["RegionSelection_Display_UnknownWithVirtual"] = "显示器：未知 | 虚拟屏幕：({0},{1},{2}x{3})",
            ["RegionSelection_Input_X"] = "X",
            ["RegionSelection_Input_Y"] = "Y",
            ["RegionSelection_Input_W"] = "宽",
            ["RegionSelection_Input_H"] = "高",
            ["RegionSelection_Preset_1280x720"] = "1280x720",
            ["RegionSelection_Preset_1600x900"] = "1600x900",
            ["RegionSelection_Preset_1920x1080"] = "1920x1080",
            ["RegionSelection_Preset_Fit16x9"] = "适配 16:9",

            // Confirmation form
            ["Confirmation_Title"] = "Agent Recorder — 录屏确认",
            ["Confirmation_RequestTitle"] = "AI 助手请求开始录屏",
            ["Confirmation_QueuePosition"] = "队列位置：{0} / {1}",
            ["Confirmation_Info_Source"] = "录制范围",
            ["Confirmation_Info_SourceType"] = "来源类型",
            ["Confirmation_Info_CaptureSemantics"] = "捕获语义",
            ["Confirmation_Info_SourceTitle"] = "来源标题",
            ["Confirmation_Info_Duration"] = "时长",
            ["Confirmation_Info_Audio"] = "麦克风",
            ["Confirmation_Info_NoAudio"] = "无音频",
            ["Confirmation_Info_NestedRole"] = "嵌套角色",
            ["Confirmation_Info_RecordingId"] = "录制ID",
            ["Confirmation_Info_ConfirmationId"] = "确认ID",
            ["Confirmation_Info_Timeout"] = "超时时间",
            ["Confirmation_Info_ExpiresAt"] = "过期时间",
            ["Confirmation_Value_NA"] = "N/A",
            ["Confirmation_Preview_NoBounds"] = "未提供录制范围",
            ["Confirmation_Preview_Fallback"] = "无法生成预览",
            ["Confirmation_CaptureSemantics_WindowSurface"] = "窗口内容：仅捕获所选窗口内容，不包含遮挡窗口",
            ["Confirmation_CaptureSemantics_ScreenRectangle"] = "屏幕矩形：捕获窗口当前屏幕区域，可能包含遮挡窗口",
            ["Confirmation_CaptureSemantics_Display"] = "显示器画面：捕获组成后的显示器像素",
            ["Confirmation_CaptureSemantics_Region"] = "屏幕区域：捕获组成后的选定区域像素",
            ["Confirmation_Preview_WindowSurface_Label"] = "窗口内容预览（不包含遮挡窗口）",
            ["Confirmation_Preview_ScreenRectangle_Label"] = "屏幕区域预览（可能包含遮挡窗口）",
            ["Confirmation_Preview_Display_Label"] = "显示器画面预览",
            ["Confirmation_Preview_Region_Label"] = "屏幕区域预览",
            ["Confirmation_Preview_WindowSurface_Fallback"] = "无法显示窗口内容预览：{0}\n仅显示目标身份；确认后只捕获该窗口内容，不包含遮挡窗口。",
            ["Confirmation_Output_Title"] = "保存位置：",
            ["Confirmation_Output_Change"] = "更改...",
            ["Confirmation_Output_Remember"] = "记住为默认保存位置",
            ["Confirmation_Output_AutoName"] = "(自动生成文件名)",
            ["Confirmation_Timeout_Initializing"] = "正在初始化倒计时…",
            ["Confirmation_Timeout_Expired"] = "确认已过期",
            ["Confirmation_Timeout_Seconds"] = "剩余 {0} 秒后自动过期",
            ["Confirmation_Timeout_SecondsUrgent"] = "剩余 {0} 秒，请尽快确认",
            ["Confirmation_Warning"] = "录屏可能包含敏感信息。只有本地确认后才会开始录制。",
            ["Confirmation_Warning_LowVolume"] = "麦克风音量较低（{0}%），可能导致录音不清晰。建议调高音量后再开始录制。",
            ["Confirmation_Button_Approve"] = "✓ 确认",
            ["Confirmation_Button_Reject"] = "✗ 拒绝",
            ["Confirmation_FolderBrowser_Title"] = "选择视频保存位置",
            ["Confirmation_FolderBrowser_Description"] = "选择视频保存位置",

            // Close reasons (used in audit logs; still localized for readability)
            ["Confirmation_Close_Approved"] = "approved",
            ["Confirmation_Close_Rejected"] = "rejected",
            ["Confirmation_Close_Expired"] = "expired",
            ["Confirmation_Close_QueueAdvanced"] = "queue_advanced",
            ["Confirmation_Close_AppExit"] = "app_exit",
            ["Confirmation_Close_Unknown"] = "unknown",

            // Tray context
            ["Tray_Idle"] = "Agent Recorder — 空闲",
            ["Tray_WaitingConfirmation"] = "Agent Recorder — 等待确认 ({0})",
            ["Tray_Preparing"] = "Agent Recorder — 正在准备麦克风…",
            ["Tray_Countdown"] = "Agent Recorder — 倒计时 {0}…",
            ["Tray_Recording"] = "Agent Recorder — 正在录制",
            ["Tray_Recording_WithCount"] = "Agent Recorder — 正在录制（{0}条并发）",
            ["Tray_Finalizing"] = "Agent Recorder — 正在保存…",
            ["Tray_Stopping"] = "Agent Recorder — 正在停止…",
            ["Tray_Status_Preparing"] = "状态：● 正在准备麦克风…",
            ["Tray_Status_Countdown"] = "状态：● 倒计时 {0}…",
            ["Tray_Status_Finalizing"] = "状态：● 正在保存…",
            ["Tray_Menu_Confirm"] = "✓ 确认录屏 ({0}/{1})",
            ["Tray_Menu_Reject"] = "✗ 拒绝录屏 ({0}/{1})",
            ["Tray_Menu_Stop"] = "停止录制",
            ["Tray_Menu_StopAll"] = "停止全部录制（{0}）",
            ["Tray_Menu_OpenOutputDir"] = "打开输出文件夹",
            ["Tray_Menu_Exit"] = "退出",
            ["Tray_Menu_Language"] = "语言 / Language",
            ["Tray_Language_ZhCn"] = "简体中文",
            ["Tray_Language_EnUs"] = "English",
            ["Tray_Status_Idle"] = "状态：空闲",
            ["Tray_Status_Waiting"] = "状态：● 等待确认（{0}s 内请操作）",
            ["Tray_Status_Recording"] = "状态：● 正在录制",
            ["Tray_Status_RecordingWithCount"] = "状态：● 正在录制（{0}条并发）",
            ["Tray_Status_Stopping"] = "状态：● 正在停止…",
            ["Tray_Balloon_WaitingTitle"] = "✓ 请确认录屏请求",
            ["Tray_Balloon_WaitingBody"] = "当前队列有 {0} 个待确认请求。\n右键单击托盘图标确认或拒绝。",
            ["Tray_Balloon_RecordingTitle"] = "Agent Recorder",
            ["Tray_Balloon_RecordingBody"] = "开始录制",
            ["Tray_Balloon_ErrorTitle"] = "录制失败",
            ["Tray_Balloon_WindowClosedBody"] = "目标窗口已关闭，录制已停止。",
            ["Tray_Balloon_WindowMinimizedBody"] = "目标窗口已最小化，录制已停止。",
            ["Tray_Balloon_WindowResizedBody"] = "目标窗口尺寸已改变，录制已停止。",
            ["Tray_Balloon_GenericFailureBody"] = "录制已停止：{0}",
            ["Tray_RecordingFailure_Title"] = "录制未保存",
            ["Tray_RecordingFailure_WindowClosedBody"] = "目标窗口已关闭；录制已停止，未保存最终视频。",
            ["Tray_RecordingFailure_WindowMinimizedBody"] = "目标窗口已最小化；录制已停止，未保存最终视频。",
            ["Tray_RecordingFailure_SizeChangedBody"] = "目标窗口尺寸已改变；录制已停止，未保存最终视频。",
            ["Tray_RecordingFailure_CaptureSemanticsChangedBody"] = "捕获方式在确认后发生变化；录制未开始。请重新发起请求并再次确认。",
            ["Tray_RecordingFailure_GenericBody"] = "录制已停止，未保存最终视频。",
            ["Tray_RecordingFailure_Close"] = "关闭",

            // Recording stop control form
            ["StopControl_Button_Stop"] = "■ 停止",
            ["StopControl_Button_Stopping"] = "停止中...",
            ["StopControl_Tooltip"] = "停止本次录制",

            // Recording indicator phases
            ["Indicator_Preparing"] = "正在准备麦克风…",
            ["Indicator_Finalizing"] = "正在保存…",
            ["Indicator_Countdown"] = "{0}",
        };
    }

    private static Dictionary<string, string> CreateEnUs()
    {
        return new Dictionary<string, string>(StringComparer.Ordinal)
        {
            // Region selection form
            ["RegionSelection_Info_Default"] = "Click and drag to select a region. Hover a window and click to pick it. Hold Alt to disable snap. Press Enter to confirm, Esc to cancel.",
            ["RegionSelection_Info_Selected"] = "Virtual: X={0}, Y={1}, W={2}, H={3}  |  Enter to confirm, Esc to cancel",
            ["RegionSelection_Info_TooSmall"] = "Selection too small. Minimum size is {0}x{0} pixels.",
            ["RegionSelection_Button_Confirm"] = "Confirm (Enter)",
            ["RegionSelection_Button_Cancel"] = "Cancel (Esc)",
            ["RegionSelection_Coords_Virtual"] = "Virtual: X={0}, Y={1}, W={2}, H={3}",
            ["RegionSelection_Coords_FormBounds"] = "Form Bounds: ({0}, {1}) -> ({2}, {3})",
            ["RegionSelection_Coords_VirtualScreen"] = "Virtual Screen: ({0}, {1}, {2}x{3})",
            ["RegionSelection_Display"] = "Display: {0}",
            ["RegionSelection_Display_Unknown"] = "Display: unknown",
            ["RegionSelection_Display_UnknownWithVirtual"] = "Display: unknown | Virtual Screen: ({0},{1},{2}x{3})",
            ["RegionSelection_Input_X"] = "X",
            ["RegionSelection_Input_Y"] = "Y",
            ["RegionSelection_Input_W"] = "W",
            ["RegionSelection_Input_H"] = "H",
            ["RegionSelection_Preset_1280x720"] = "1280x720",
            ["RegionSelection_Preset_1600x900"] = "1600x900",
            ["RegionSelection_Preset_1920x1080"] = "1920x1080",
            ["RegionSelection_Preset_Fit16x9"] = "Fit 16:9",

            // Confirmation form
            ["Confirmation_Title"] = "Agent Recorder — Recording Confirmation",
            ["Confirmation_RequestTitle"] = "AI assistant requests to start recording",
            ["Confirmation_QueuePosition"] = "Queue position: {0} / {1}",
            ["Confirmation_Info_Source"] = "Source",
            ["Confirmation_Info_SourceType"] = "Source type",
            ["Confirmation_Info_CaptureSemantics"] = "Capture semantics",
            ["Confirmation_Info_SourceTitle"] = "Source title",
            ["Confirmation_Info_Duration"] = "Duration",
            ["Confirmation_Info_Audio"] = "Microphone",
            ["Confirmation_Info_NoAudio"] = "No audio",
            ["Confirmation_Info_NestedRole"] = "Nested role",
            ["Confirmation_Info_RecordingId"] = "Recording ID",
            ["Confirmation_Info_ConfirmationId"] = "Confirmation ID",
            ["Confirmation_Info_Timeout"] = "Timeout",
            ["Confirmation_Info_ExpiresAt"] = "Expires at",
            ["Confirmation_Value_NA"] = "N/A",
            ["Confirmation_Preview_NoBounds"] = "No capture bounds provided",
            ["Confirmation_Preview_Fallback"] = "Unable to generate preview",
            ["Confirmation_CaptureSemantics_WindowSurface"] = "Window surface: selected window content only; covering windows are excluded",
            ["Confirmation_CaptureSemantics_ScreenRectangle"] = "Screen rectangle: current screen area; covering windows may be included",
            ["Confirmation_CaptureSemantics_Display"] = "Display surface: composed display pixels",
            ["Confirmation_CaptureSemantics_Region"] = "Region rectangle: composed pixels in the selected area",
            ["Confirmation_Preview_WindowSurface_Label"] = "Window-content preview (covering windows excluded)",
            ["Confirmation_Preview_ScreenRectangle_Label"] = "Screen-area preview (covering windows may be included)",
            ["Confirmation_Preview_Display_Label"] = "Display preview",
            ["Confirmation_Preview_Region_Label"] = "Screen-area preview",
            ["Confirmation_Preview_WindowSurface_Fallback"] = "Window-content preview unavailable: {0}\nIdentity only; approval still captures only this window and excludes covering windows.",
            ["Confirmation_Output_Title"] = "Save location:",
            ["Confirmation_Output_Change"] = "Change...",
            ["Confirmation_Output_Remember"] = "Remember as default save location",
            ["Confirmation_Output_AutoName"] = "(auto-generated file name)",
            ["Confirmation_Timeout_Initializing"] = "Initializing countdown…",
            ["Confirmation_Timeout_Expired"] = "Confirmation expired",
            ["Confirmation_Timeout_Seconds"] = "Expires in {0} seconds",
            ["Confirmation_Timeout_SecondsUrgent"] = "{0} seconds left, please confirm now",
            ["Confirmation_Warning"] = "Recordings may contain sensitive information. Recording starts only after local confirmation.",
            ["Confirmation_Warning_LowVolume"] = "Microphone volume is low ({0}%). Recording may be unclear. Consider increasing the volume before starting.",
            ["Confirmation_Button_Approve"] = "✓ Confirm",
            ["Confirmation_Button_Reject"] = "✗ Reject",
            ["Confirmation_FolderBrowser_Title"] = "Choose video save location",
            ["Confirmation_FolderBrowser_Description"] = "Choose video save location",

            // Close reasons (must remain stable for audit/event logs)
            ["Confirmation_Close_Approved"] = "approved",
            ["Confirmation_Close_Rejected"] = "rejected",
            ["Confirmation_Close_Expired"] = "expired",
            ["Confirmation_Close_QueueAdvanced"] = "queue_advanced",
            ["Confirmation_Close_AppExit"] = "app_exit",
            ["Confirmation_Close_Unknown"] = "unknown",

            // Tray context
            ["Tray_Idle"] = "Agent Recorder — Idle",
            ["Tray_WaitingConfirmation"] = "Agent Recorder — Pending confirmation ({0})",
            ["Tray_Preparing"] = "Agent Recorder — Preparing microphone…",
            ["Tray_Countdown"] = "Agent Recorder — Countdown {0}…",
            ["Tray_Recording"] = "Agent Recorder — Recording",
            ["Tray_Recording_WithCount"] = "Agent Recorder — Recording ({0} concurrent)",
            ["Tray_Finalizing"] = "Agent Recorder — Saving…",
            ["Tray_Stopping"] = "Agent Recorder — Stopping…",
            ["Tray_Status_Preparing"] = "Status: ● Preparing microphone…",
            ["Tray_Status_Countdown"] = "Status: ● Countdown {0}…",
            ["Tray_Status_Finalizing"] = "Status: ● Saving…",
            ["Tray_Menu_Confirm"] = "✓ Confirm recording ({0}/{1})",
            ["Tray_Menu_Reject"] = "✗ Reject recording ({0}/{1})",
            ["Tray_Menu_Stop"] = "Stop recording",
            ["Tray_Menu_StopAll"] = "Stop all recordings ({0})",
            ["Tray_Menu_OpenOutputDir"] = "Open output folder",
            ["Tray_Menu_Exit"] = "Exit",
            ["Tray_Menu_Language"] = "Language / 语言",
            ["Tray_Language_ZhCn"] = "简体中文",
            ["Tray_Language_EnUs"] = "English",
            ["Tray_Status_Idle"] = "Status: Idle",
            ["Tray_Status_Waiting"] = "Status: ● Pending confirmation (act within {0}s)",
            ["Tray_Status_Recording"] = "Status: ● Recording",
            ["Tray_Status_RecordingWithCount"] = "Status: ● Recording ({0} concurrent)",
            ["Tray_Status_Stopping"] = "Status: ● Stopping…",
            ["Tray_Balloon_WaitingTitle"] = "✓ Please confirm recording request",
            ["Tray_Balloon_WaitingBody"] = "There are {0} pending confirmation requests.\nRight-click the tray icon to confirm or reject.",
            ["Tray_Balloon_RecordingTitle"] = "Agent Recorder",
            ["Tray_Balloon_RecordingBody"] = "Recording started",
            ["Tray_Balloon_ErrorTitle"] = "Recording failed",
            ["Tray_Balloon_WindowClosedBody"] = "The target window closed; recording stopped.",
            ["Tray_Balloon_WindowMinimizedBody"] = "The target window was minimized; recording stopped.",
            ["Tray_Balloon_WindowResizedBody"] = "The target window changed size; recording stopped.",
            ["Tray_Balloon_GenericFailureBody"] = "Recording stopped: {0}",
            ["Tray_RecordingFailure_Title"] = "Recording not saved",
            ["Tray_RecordingFailure_WindowClosedBody"] = "The target window was closed; recording stopped and no final video was saved.",
            ["Tray_RecordingFailure_WindowMinimizedBody"] = "The target window was minimized; recording stopped and no final video was saved.",
            ["Tray_RecordingFailure_SizeChangedBody"] = "The target window changed size; recording stopped and no final video was saved.",
            ["Tray_RecordingFailure_CaptureSemanticsChangedBody"] = "The capture method changed after confirmation; recording did not start. Please retry and confirm again.",
            ["Tray_RecordingFailure_GenericBody"] = "Recording stopped and no final video was saved.",
            ["Tray_RecordingFailure_Close"] = "Close",

            // Recording stop control form
            ["StopControl_Button_Stop"] = "■ Stop",
            ["StopControl_Button_Stopping"] = "Stopping...",
            ["StopControl_Tooltip"] = "Stop this recording",

            // Recording indicator phases
            ["Indicator_Preparing"] = "Preparing microphone...",
            ["Indicator_Finalizing"] = "Saving...",
            ["Indicator_Countdown"] = "{0}",
        };
    }
}
