namespace AgentRecorder.Infrastructure;

internal static class LocalRetentionUiText
{
    internal static void Add(Dictionary<string, string> texts, UiLanguage language)
    { foreach (var (key, zh, en) in Entries) texts["RetentionApproval_" + key] = language == UiLanguage.ZhCn ? zh : en; }
    private static readonly (string, string, string)[] Entries =
    {
        ("Title", "永久删除这个视频？", "Permanently delete this video?"),
        ("Warning", "永久删除该视频，不进入回收站。此批准不授权录屏，历史记录保留。", "Permanently delete this video without using the Recycle Bin. This does not authorize recording. History is retained."),
        ("Name", "视频名称", "Video name"), ("Path", "完整路径（可选择并复制）", "Full path (select and copy)"),
        ("RecordingId", "录制标识", "Recording ID"),
        ("Completed", "录制完成时间", "Recording completed"), ("Size", "成功大小（字节）", "Successful size (bytes)"),
        ("Policy", "仅本次删除：完成时间须早于（UTC），预览不是批准", "This deletion only: completed before (UTC); preview is not approval"),
        ("KeepLatest", "保留最近完成历史段数（不保证磁盘文件完整或存在）：", "Keep latest completed history count (not a guarantee of existing/intact files):"),
        ("Approve", "允许删除这个视频", "Allow deletion of this video"), ("Cancel", "取消", "Cancel"),
        ("Keyboard", "Enter、Esc、关闭或60秒超时均取消。批准后60秒内仅允许开始一次尝试。", "Enter, Esc, closing, or a 60-second timeout cancels. Approval permits starting one attempt within 60 seconds."),
        ("Scope", "永久删除的单个视频目标", "Single video target for permanent deletion"),
        ("Expired", "确认已过期，请取消。", "Confirmation expired. Please cancel.")
    };
}
