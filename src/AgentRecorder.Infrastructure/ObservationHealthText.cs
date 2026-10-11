namespace AgentRecorder.Infrastructure;

internal static class ObservationHealthText
{
    internal static void Add(Dictionary<string, string> texts, UiLanguage language)
    {
        foreach (var (key, zh, en) in Entries) texts["Health_" + key] = language == UiLanguage.ZhCn ? zh : en;
    }
    private static readonly (string Key, string Zh, string En)[] Entries =
    {
        ("Title", "健康状态", "Health"), ("Refresh", "刷新", "Refresh"),
        ("Copy", "复制诊断摘要", "Copy diagnostic summary"), ("Name", "项目", "Item"),
        ("Status", "状态", "Status"), ("Fact", "事实详情", "Facts"),
        ("Preview", "下方为将复制的本地诊断摘要（含完整路径）。", "Local diagnostic summary to copy appears below (includes full paths)."),
        ("Loading", "正在采样；截止 3 秒。", "Sampling; deadline 3 seconds."),
        ("Complete", "采样完成；返回此页复用快照，请手动刷新。", "Sample complete; revisits reuse it. Refresh manually."),
        ("Timeout", "采样超时；仅保留已知宿主事实，其他项未检查。", "Sampling timed out; host facts retained, other items unchecked."),
        ("Busy", "宿主上一项探针尚未结束；未叠加新采样。", "A previous host probe is still running; no additional sample started."),
        ("Failed", "采样失败；其他项未知。", "Sampling failed; other items unknown."),
        ("Cancelled", "采样已取消。", "Sampling cancelled."), ("NotChecked", "未检查", "Not checked"),
        ("Ready", "已就绪", "Ready"), ("Available", "可用", "Available"),
        ("Unavailable", "不可用", "Unavailable"), ("Unsupported", "不支持", "Unsupported"),
        ("Unknown", "未知", "Unknown"), ("Idle", "空闲，按策略未注册", "Idle; unregistered by policy"),
        ("Listening", "正在监听，就绪尚未确认", "Listening; readiness unconfirmed"),
        ("Stopped", "已停止", "Stopped"), ("NotStarted", "尚未启动", "Not started"),
        ("Build", "构建版本 / 宿主", "Build / host"), ("Api", "API / 当前实例就绪", "API / instance readiness"),
        ("Data", "实例数据目录", "Instance data directory"), ("StartupDesktop", "启动桌面证据（静态）", "Startup desktop evidence (static)"),
        ("Desktop", "当前交互桌面", "Current interactive desktop"), ("Stop", "全局停止热键", "Global stop hotkey"),
        ("Chapter", "章节标记热键", "Chapter mark hotkey"), ("Wgc", "WGC 实验策略", "WGC experimental policy"),
        ("WgcHelper", "WGC helper 路径", "WGC helper path"), ("AudioHelper", "音频 helper 路径", "Audio helper path"),
        ("Microphone", "麦克风后端", "Microphone backend"), ("SystemAudio", "系统声音后端", "System audio backend"),
        ("Update", "更新检查", "Update check"), ("SupportedOnly", "后端支持；设备录制未检查。", "Backend supported; device recording not checked."),
        ("PathOnly", "仅路径发现；未运行进程、编码或录制测试。", "Path discovery only; process, encoding and recording not tested."),
        ("Static", "RuntimeReadiness 启动证据；时间为宿主接收时间，不代表当前锁屏状态。", "RuntimeReadiness startup evidence; time is host receipt time, not current lock state."),
        ("Dynamic", "Windows 交互桌面只读观察；不枚举窗口。", "Read-only Windows interactive desktop observation; no window enumeration."),
        ("Frozen", "本实例启动时冻结的目录；不读取 key 内容或旧 ready 文件。", "Directory frozen at this instance's startup; no key contents or old ready files read."),
        ("Policy_default", "未显式配置实验后端", "Experimental backend not explicitly configured"),
        ("Policy_wgc-continuous", "已配置实验后端；运行能力未检查", "Experimental backend configured; runtime capability not checked"),
        ("Window", "窗口", "Window"), ("Display", "显示器", "Display"), ("Region", "区域", "Region"),
        ("Experimental", "WGC 为实验策略；以下是冻结配置，不代表能力或录制实测。窗口捕获仍需本地确认。", "WGC is experimental; frozen configuration below does not prove capability or recording. Window capture still requires local confirmation."),
        ("Registered", "已注册", "Registered"), ("Unregistered", "未注册", "Unregistered"),
        ("Time", "采样时间", "Sample time"), ("Receipt", "证据接收时间", "Evidence receipt time"),
        ("Reason", "原因", "Reason"), ("Path", "完整路径", "Full path"), ("Source", "来源", "Source"),
        ("Policy", "策略", "Policy"), ("ReadyPath", "本实例就绪文件位置", "Instance ready file location"),
        ("Runtime", "实际监听运行态；未启动或停止时不展示预设端口。", "Actual listener runtime; no preset port shown before startup or after stop."),
        ("tool_not_found", "路径未发现或文件不可访问", "Path not found or file inaccessible"),
        ("tool_resolution_failed", "路径解析失败", "Path resolution failed"), ("path_only", "仅发现路径", "Path discovered only"),
        ("interactive_desktop", "处于交互桌面", "On interactive desktop"), ("desktop_locked", "桌面已锁定", "Desktop locked"),
        ("windows_required", "需要 Windows", "Windows required"), ("input_desktop_unavailable", "输入桌面不可访问", "Input desktop unavailable"),
        ("session_unavailable", "交互会话不可用", "Interactive session unavailable"),
        ("interactive_desktop_required", "不在所需交互桌面", "Required interactive desktop unavailable"),
        ("desktop_observation_failed", "桌面观察失败", "Desktop observation failed"),
        ("registration_failed", "热键未能注册", "Hotkey registration failed"),
        ("not_checked", "未检查", "Not checked"), ("unknown", "未知来源", "Unknown source"),
    };
}
