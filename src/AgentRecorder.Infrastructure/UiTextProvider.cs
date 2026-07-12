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
            ["Confirmation_Info_SourceTitle"] = "来源标题",
            ["Confirmation_Info_Duration"] = "时长",
            ["Confirmation_Info_Audio"] = "麦克风",
            ["Confirmation_Info_NestedRole"] = "嵌套角色",
            ["Confirmation_Info_RecordingId"] = "录制ID",
            ["Confirmation_Info_ConfirmationId"] = "确认ID",
            ["Confirmation_Info_Timeout"] = "超时时间",
            ["Confirmation_Info_ExpiresAt"] = "过期时间",
            ["Confirmation_Value_NA"] = "N/A",
            ["Confirmation_Preview_NoBounds"] = "未提供录制范围",
            ["Confirmation_Preview_Fallback"] = "无法生成预览",
            ["Confirmation_Output_Title"] = "保存位置：",
            ["Confirmation_Output_Change"] = "更改...",
            ["Confirmation_Output_Remember"] = "记住为默认保存位置",
            ["Confirmation_Output_AutoName"] = "(自动生成文件名)",
            ["Confirmation_Timeout_Initializing"] = "正在初始化倒计时…",
            ["Confirmation_Timeout_Expired"] = "确认已过期",
            ["Confirmation_Timeout_Seconds"] = "剩余 {0} 秒后自动过期",
            ["Confirmation_Timeout_SecondsUrgent"] = "剩余 {0} 秒，请尽快确认",
            ["Confirmation_Warning"] = "录屏可能包含敏感信息。只有本地确认后才会开始录制。",
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
            ["Tray_Recording"] = "Agent Recorder — 正在录制",
            ["Tray_Recording_WithCount"] = "Agent Recorder — 正在录制（{0}条并发）",
            ["Tray_Stopping"] = "Agent Recorder — 正在停止…",
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

            // Recording stop control form
            ["StopControl_Button_Stop"] = "■ 停止",
            ["StopControl_Button_Stopping"] = "停止中...",
            ["StopControl_Tooltip"] = "停止本次录制",
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
            ["Confirmation_Info_SourceTitle"] = "Source title",
            ["Confirmation_Info_Duration"] = "Duration",
            ["Confirmation_Info_Audio"] = "Audio",
            ["Confirmation_Info_NestedRole"] = "Nested role",
            ["Confirmation_Info_RecordingId"] = "Recording ID",
            ["Confirmation_Info_ConfirmationId"] = "Confirmation ID",
            ["Confirmation_Info_Timeout"] = "Timeout",
            ["Confirmation_Info_ExpiresAt"] = "Expires at",
            ["Confirmation_Value_NA"] = "N/A",
            ["Confirmation_Preview_NoBounds"] = "No capture bounds provided",
            ["Confirmation_Preview_Fallback"] = "Unable to generate preview",
            ["Confirmation_Output_Title"] = "Save location:",
            ["Confirmation_Output_Change"] = "Change...",
            ["Confirmation_Output_Remember"] = "Remember as default save location",
            ["Confirmation_Output_AutoName"] = "(auto-generated file name)",
            ["Confirmation_Timeout_Initializing"] = "Initializing countdown…",
            ["Confirmation_Timeout_Expired"] = "Confirmation expired",
            ["Confirmation_Timeout_Seconds"] = "Expires in {0} seconds",
            ["Confirmation_Timeout_SecondsUrgent"] = "{0} seconds left, please confirm now",
            ["Confirmation_Warning"] = "Recordings may contain sensitive information. Recording starts only after local confirmation.",
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
            ["Tray_Recording"] = "Agent Recorder — Recording",
            ["Tray_Recording_WithCount"] = "Agent Recorder — Recording ({0} concurrent)",
            ["Tray_Stopping"] = "Agent Recorder — Stopping…",
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

            // Recording stop control form
            ["StopControl_Button_Stop"] = "■ Stop",
            ["StopControl_Button_Stopping"] = "Stopping...",
            ["StopControl_Tooltip"] = "Stop this recording",
        };
    }
}
