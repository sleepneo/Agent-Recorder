using System;
using AgentRecorder.Infrastructure;
using AgentRecorder.Logging;

namespace AgentRecorder.Headless;

/// <summary>
/// Headless 模式下的 ITrayContext 实现：不提供本地 UI，因此不能代表本地用户批准录屏请求。
/// 所有确认请求都会被立即拒绝，并记录 audit event 说明 headless 模式无法完成本地用户确认。
/// </summary>
public sealed class HeadlessTrayContext : ITrayContext
{
    public string HostMode => "headless";
    public bool SupportsRegionSelectionUi => false;

    private readonly AuditLogger _audit;

    public HeadlessTrayContext(AuditLogger audit) => _audit = audit;

    public void RequestConfirmation(object summary, Action<bool> callback)
    {
        _audit.Log("confirmation.headless_unavailable", new
        {
            reason = "Headless API host has no local UI; cannot request or collect user confirmation.",
            action = "rejected"
        });
        // Headless 模式不能代表本地用户确认，必须立即拒绝，避免录制在无人确认的情况下开始。
        callback(false);
    }

    public void RequestRegionSelection(int timeoutSeconds,
        Action<string, int, int, int, int, string, string> callback)
    {
        _audit.Log("region_selection.headless_unavailable", new
        {
            reason = "Headless API host has no local UI; cannot show region selection UI.",
            action = "blocked"
        });
        // Headless 模式没有本地 UI，无法显示选区窗口。
        callback("display_unavailable", 0, 0, 0, 0, "", "virtual_screen");
    }

    public void SetRecording(object rec)
    {
        _audit.Log("recording.headless_set_recording", new { note = "Headless host has no UI to update." });
    }

    public void SetIdle(object rec)
    {
        _audit.Log("recording.headless_set_idle", new { note = "Headless host has no UI to update." });
    }

    public void SetAllIdle()
    {
        _audit.Log("recording.headless_set_all_idle", new { note = "Headless host has no UI to update." });
    }

    public void ShowError(string text)
    {
        _audit.Log("recording.headless_error", new { error = text });
    }
}
