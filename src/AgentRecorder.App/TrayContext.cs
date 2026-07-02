using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Windows.Forms;
using AgentRecorder.Infrastructure;
using AgentRecorder.Core;
using AgentRecorder.Logging;
using AgentRecorder.Windows;

namespace AgentRecorder.App;

internal sealed class TrayContext : ApplicationContext, ITrayContext
{
    public string HostMode => "tray";
    public bool SupportsRegionSelectionUi => true;

    private readonly NotifyIcon _icon;
    private readonly RecordingEngine _engine;
    private readonly AuditLogger _audit;
    private readonly Dictionary<string, Recording> _activeRecordings = new();
    private readonly ToolStripMenuItem _statusItem;
    private readonly ToolStripMenuItem _stopItem;
    private readonly ToolStripMenuItem _approveItem;
    private readonly ToolStripMenuItem _rejectItem;
    private readonly ToolStripSeparator _confirmSep;
    private Action<bool>? _pendingCallback;

    // Win32 API: 将 MessageBox 强制带到前台
    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    public TrayContext(RecordingEngine engine, AuditLogger audit)
    {
        _engine = engine; _audit = audit;

        var menu = new ContextMenuStrip();

        // 确认区域（仅在有待确认请求时显示，仅由本地用户从系统托盘菜单触发）
        _approveItem = new ToolStripMenuItem("✓ 确认录屏", null, (_, _) => ApproveFromMenu())
        {
            Visible = false,
            ForeColor = System.Drawing.Color.DarkGreen,
            Font = new System.Drawing.Font("Segoe UI", 9, System.Drawing.FontStyle.Bold)
        };
        _rejectItem = new ToolStripMenuItem("✗ 拒绝录屏", null, (_, _) => RejectFromMenu())
        {
            Visible = false,
            ForeColor = System.Drawing.Color.DarkRed
        };
        _confirmSep = new ToolStripSeparator() { Visible = false };

        _statusItem = new ToolStripMenuItem("状态：空闲") { Enabled = false };
        _stopItem = new ToolStripMenuItem("停止当前录制", null, (_, _) => StopCurrent()) { Enabled = false };

        menu.Items.Add(_approveItem);
        menu.Items.Add(_rejectItem);
        menu.Items.Add(_confirmSep);
        menu.Items.Add(_statusItem);
        menu.Items.Add(new ToolStripMenuItem("打开输出文件夹", null, (_, _) => OpenFolder()));
        menu.Items.Add(_stopItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(new ToolStripMenuItem("退出", null, (_, _) => ExitApp()));

        _icon = new NotifyIcon
        {
            Icon = SystemIcons.Application,
            Visible = true,
            Text = "Agent Recorder — 空闲",
            ContextMenuStrip = menu
        };
    }

    /// <summary>
    /// 弹出录屏确认（仅限本地用户通过托盘菜单或 MessageBox 操作；不允许通过 HTTP API 远程确认）。
    /// </summary>
    public void RequestConfirmation(object summary, Action<bool> callback)
    {
        if (_pendingCallback != null)
        {
            callback(false);
            return;
        }
        _pendingCallback = callback;

        RunOnUi(() =>
        {
            _approveItem.Visible = true;
            _rejectItem.Visible = true;
            _confirmSep.Visible = true;
            _statusItem.Text = "状态：● 等待确认（60s 内请操作）";
            _icon.Text = "Agent Recorder — 等待确认";
            _icon.ShowBalloonTip(5000, "✓ 请确认录屏请求",
                "右键单击托盘图标，选择 \"确认录屏\"，\n或点击弹窗中的 \"是(Y)\" 按钮。",
                ToolTipIcon.Warning);
        });

        var s = JsonNode.Parse(JsonSerializer.Serialize(summary))!;
        var msg = "AI 助手请求开始录屏\n\n" +
                  $"录制范围：{s["source"]}\n" +
                  $"麦克风：{s["audio"]}\n" +
                  $"时长：{s["duration"]}\n" +
                  $"保存位置：{s["output"]}\n\n" +
                  "【也可以右键托盘图标，选择 \"确认录屏\" 来确认】\n\n" +
                  "录屏可能包含敏感信息。请确认是否开始。";

        var thread = new Thread(() =>
        {
            try
            {
                var r = MessageBox.Show(msg,
                    "Agent Recorder — 录屏确认 【右键托盘图标也可确认】",
                    MessageBoxButtons.YesNo,
                    MessageBoxIcon.Warning,
                    MessageBoxDefaultButton.Button2,
                    MessageBoxOptions.DefaultDesktopOnly);
                InvokePending(r == DialogResult.Yes);
            }
            catch (Exception ex)
            {
                _audit.Log("tray.messagebox_error", new { error = ex.Message });
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
    }

    /// <summary>
    /// 由托盘菜单项触发确认（仅限本地 UI；不可被 HTTP API 调用）。
    /// </summary>
    private void ApproveFromMenu()
    {
        _audit.Log("confirmation.approved_from_menu", new { });
        InvokePending(true);
    }

    /// <summary>
    /// 由托盘菜单项触发拒绝（仅限本地 UI；不可被 HTTP API 调用）。
    /// </summary>
    private void RejectFromMenu()
    {
        _audit.Log("confirmation.rejected_from_menu", new { });
        InvokePending(false);
    }

    /// <summary>
    /// 统一触发回调入口：确保只触发一次，并清理菜单状态
    /// </summary>
    private void InvokePending(bool approved)
    {
        var cb = _pendingCallback;
        _pendingCallback = null;

        RunOnUi(() =>
        {
            _approveItem.Visible = false;
            _rejectItem.Visible = false;
            _confirmSep.Visible = false;
            if (approved)
            {
                _statusItem.Text = "状态：● 正在启动录制...";
                _icon.Text = "Agent Recorder — 正在启动";
            }
            else
            {
                _statusItem.Text = "状态：空闲";
                _icon.Text = "Agent Recorder — 空闲";
            }
        });

        cb?.Invoke(approved);
    }

    public void SetRecording(object rec)
    {
        var recording = rec as Recording;
        if (recording == null) return;
        RunOnUi(() =>
        {
            _activeRecordings[recording.Id] = recording;
            UpdateRecordingUi();
            if (_activeRecordings.Count == 1)
            {
                _icon.ShowBalloonTip(2000, "Agent Recorder", "开始录制", ToolTipIcon.Info);
            }
        });
    }

    public void SetIdle(object rec)
    {
        var recording = rec as Recording;
        RunOnUi(() =>
        {
            if (recording != null)
                _activeRecordings.Remove(recording.Id);
            if (_activeRecordings.Count == 0)
                SetAllIdleUi();
            else
                UpdateRecordingUi();
        });
    }

    public void SetAllIdle() => RunOnUi(() =>
    {
        _activeRecordings.Clear();
        SetAllIdleUi();
    });

    private void UpdateRecordingUi()
    {
        int count = _activeRecordings.Count;
        string label = count > 1 ? $"（{count}条并发）" : "";
        _icon.Text = $"Agent Recorder — 正在录制{label}";
        _icon.Icon = SystemIcons.Exclamation;
        _statusItem.Text = $"状态：● 正在录制{label}";
        _stopItem.Enabled = true;
    }

    private void SetAllIdleUi()
    {
        _pendingCallback = null;
        _icon.Text = "Agent Recorder — 空闲";
        _icon.Icon = SystemIcons.Application;
        _statusItem.Text = "状态：空闲";
        _stopItem.Enabled = false;
        _approveItem.Visible = false;
        _rejectItem.Visible = false;
        _confirmSep.Visible = false;
    }

    public void ShowError(string text) =>
        RunOnUi(() => _icon.ShowBalloonTip(4000, "录制失败", text, ToolTipIcon.Error));

    /// <summary>
    /// 请求本地用户进行区域选择。弹出全屏选区窗口，用户拖拽选择后确认/取消。
    /// 仅限本地 UI 交互；不允许通过 HTTP API 静默选择。
    /// </summary>
    public void RequestRegionSelection(int timeoutSeconds,
        Action<string, int, int, int, int, string, string> callback)
    {
        // Use Interlocked for once-guarantee: callback can only fire once
        var callbackState = new CallbackState();
        Action<string, int, int, int, int, string, string> guardedCallback = (status, x, y, w, h, did, cs) =>
        {
            if (Interlocked.Exchange(ref callbackState.AlreadyCalled, 1) == 1)
            {
                // Callback already fired, ignore this call
                return;
            }
            callback(status, x, y, w, h, did, cs);
        };

        // Check displays first
        var displays = Windows.SystemQuery.EnumDisplays();
        var displayCount = displays.Count;
        var processId = Environment.ProcessId;
        var sessionId = Windows.Native.GetCurrentSessionId();

        if (displayCount == 0)
        {
            _audit.Log("region_selection.display_unavailable", new
            {
                reason = "no displays enumerated",
                host_mode = "tray",
                process_id = processId,
                session_id = sessionId,
                display_count = displayCount
            });
            guardedCallback("display_unavailable", 0, 0, 0, 0, "", "virtual_screen");
            return;
        }

        _audit.Log("region_selection.requested", new
        {
            timeout_seconds = timeoutSeconds,
            host_mode = "tray",
            process_id = processId,
            session_id = sessionId,
            display_count = displayCount
        });

        // Track if UI thread is still running
        var uiThreadCompleted = new ManualResetEventSlim(false);
        Thread? uiThread = null;

        // On timeout thread, signal UI thread to stop and wait
        void CloseUiFromTimeout()
        {
            try
            {
                // Signal UI thread to close
                callbackState.CloseRequestedFromTimeout = true;

                // Try to close the form via Control.Invoke if we have the handle
                if (callbackState.FormHandle != IntPtr.Zero)
                {
                    Native.PostMessage(callbackState.FormHandle, Native.WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
                }

                // Wait briefly for UI thread
                if (!uiThreadCompleted.Wait(2000))
                {
                    _audit.Log("region_selection.timeout_ui_close_slow", new { timeout = timeoutSeconds });
                }
            }
            catch (Exception ex)
            {
                _audit.Log("region_selection.timeout_ui_close_error", new { error = ex.Message });
            }
        }

        // Start UI thread
        uiThread = new Thread(() =>
        {
            try
            {
                _audit.Log("region_selection.ui_opening", new { thread_id = Thread.CurrentThread.ManagedThreadId });

                using var form = new RegionSelectionForm();
                callbackState.FormHandle = form.Handle;

                _audit.Log("region_selection.ui_opened", new
                {
                    thread_id = Thread.CurrentThread.ManagedThreadId,
                    form_bounds = new { x = form.Bounds.X, y = form.Bounds.Y, w = form.Bounds.Width, h = form.Bounds.Height },
                    virtual_screen = new
                    {
                        x = SystemInformation.VirtualScreen.X,
                        y = SystemInformation.VirtualScreen.Y,
                        w = SystemInformation.VirtualScreen.Width,
                        h = SystemInformation.VirtualScreen.Height
                    }
                });

                // Check if timeout was requested before showing dialog
                if (callbackState.CloseRequestedFromTimeout)
                {
                    _audit.Log("region_selection.timeout_before_show", new { timeout = timeoutSeconds });
                    guardedCallback("selection_timeout", 0, 0, 0, 0, "", "virtual_screen");
                    return;
                }

                var result = form.ShowDialog();

                // If timeout requested after ShowDialog returns, ignore user action
                if (callbackState.CloseRequestedFromTimeout)
                {
                    _audit.Log("region_selection.timeout_after_show", new
                    {
                        timeout = timeoutSeconds,
                        result_enum = result.ToString(),
                        note = "user action ignored due to timeout"
                    });
                    guardedCallback("selection_timeout", 0, 0, 0, 0, "", "virtual_screen");
                    return;
                }

                if (result == DialogResult.OK)
                {
                    var b = form.SelectedBounds;
                    _audit.Log("region_selection.selected", new
                    {
                        x = b.X,
                        y = b.Y,
                        w = b.Width,
                        h = b.Height,
                        display_id = form.DisplayId,
                        coordinate_space = form.CoordinateSpace
                    });
                    guardedCallback("selected", b.X, b.Y, b.Width, b.Height, form.DisplayId, form.CoordinateSpace);
                }
                else
                {
                    _audit.Log("region_selection.cancelled", new { result = result.ToString() });
                    guardedCallback("selection_cancelled", 0, 0, 0, 0, "", "virtual_screen");
                }
            }
            catch (Exception ex)
            {
                _audit.Log("region_selection.error", new { error = ex.Message, stack = ex.StackTrace });
                guardedCallback("error", 0, 0, 0, 0, "", "virtual_screen");
            }
            finally
            {
                uiThreadCompleted.Set();
            }
        });
        uiThread.SetApartmentState(ApartmentState.STA);
        uiThread.IsBackground = true;
        uiThread.Start();

        // Timeout thread
        var timeoutThread = new Thread(() =>
        {
            try
            {
                if (!uiThreadCompleted.Wait(timeoutSeconds * 1000))
                {
                    _audit.Log("region_selection.timeout", new
                    {
                        timeout = timeoutSeconds,
                        elapsed_ms = timeoutSeconds * 1000,
                        note = "timeout fired, closing UI"
                    });
                    CloseUiFromTimeout();
                    guardedCallback("selection_timeout", 0, 0, 0, 0, "", "virtual_screen");
                }
            }
            catch (Exception ex)
            {
                _audit.Log("region_selection.timeout_error", new { error = ex.Message });
            }
        });
        timeoutThread.IsBackground = true;
        timeoutThread.Start();
    }

    private class CallbackState
    {
        public int AlreadyCalled = 0;
        public bool CloseRequestedFromTimeout = false;
        public IntPtr FormHandle = IntPtr.Zero;
    }

    private void StopCurrent()
    {
        var ids = _activeRecordings.Keys.ToList();
        foreach (var id in ids)
        {
            try { _engine.Stop(id, "tray_stop"); } catch { }
        }
        SetAllIdle();
    }

    private void OpenFolder()
    {
        Directory.CreateDirectory(Paths.DefaultOutputDir);
        Process.Start(new ProcessStartInfo { FileName = Paths.DefaultOutputDir, UseShellExecute = true });
    }

    private void ExitApp()
    {
        _icon.Visible = false;
        Application.Exit();
    }

    private void RunOnUi(Action a)
    {
        if (Application.OpenForms.Count > 0 && Application.OpenForms[0]!.InvokeRequired)
            Application.OpenForms[0]!.Invoke(a);
        else a();
    }
}
