using System;
using System.Diagnostics;
using System.IO;
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
    private readonly Control _uiInvoker;

    // Confirmation queue
    private readonly ConfirmationQueue _confirmationQueue = new();
    private ConfirmationForm? _currentForm;

    public TrayContext(RecordingEngine engine, AuditLogger audit)
    {
        _engine = engine; _audit = audit;

        // UI dispatcher control: a hidden control created on the UI thread, used for
        // marshalling calls from HTTP worker threads back to the WinForms UI thread.
        // We must not depend on the first open form because tray apps may have
        // zero open forms, which would cause UI operations to run on the wrong thread.
        _uiInvoker = new Control();
        _ = _uiInvoker.Handle; // Force handle creation on this thread

        var menu = new ContextMenuStrip();

        // Confirmation area (shown only when pending requests, only triggered by local user from tray menu)
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
    /// Pop up recording confirmation (only local user via tray menu or confirmation form; no HTTP API remote confirmation).
    /// </summary>
    public void RequestConfirmation(object summary, Action<bool> callback)
    {
        var s = JsonNode.Parse(JsonSerializer.Serialize(summary))!;
        var confirmationId = GetString(s, "confirmation_id");
        var recordingId = GetString(s, "recording_id");
        var timeoutSeconds = GetInt(s, "timeout_seconds") ?? 60;

        var item = new PendingConfirmationItem(
            confirmationId,
            recordingId,
            summary,
            callback,
            timeoutSeconds);

        _confirmationQueue.Enqueue(item);

        _audit.Log("confirmation.ui_queued", new
        {
            confirmation_id = confirmationId,
            recording_id = recordingId,
            queue_count = _confirmationQueue.PendingCount
        });

        RunOnUi(() =>
        {
            UpdateConfirmationMenu();

            // If no current form showing, show the queue head
            if (_currentForm == null || !_currentForm.Visible)
            {
                ShowCurrentConfirmation();
            }
        });
    }

    private void ShowCurrentConfirmation()
    {
        var current = _confirmationQueue.Current;
        if (current == null)
        {
            HideConfirmationForm();
            return;
        }

        var items = _confirmationQueue.GetAllItems();
        var position = items.IndexOf(current) + 1;

        // Close any existing form
        if (_currentForm != null)
        {
            HideConfirmationForm();
        }

        _currentForm = new ConfirmationForm(current, position, items.Count, approved =>
        {
            if (approved)
            {
                _audit.Log("confirmation.ui_approved", new
                {
                    confirmation_id = current.ConfirmationId,
                    recording_id = current.RecordingId
                });
                _confirmationQueue.ApproveCurrent();
            }
            else
            {
                _audit.Log("confirmation.ui_rejected", new
                {
                    confirmation_id = current.ConfirmationId,
                    recording_id = current.RecordingId
                });
                _confirmationQueue.RejectCurrent();
            }

            RunOnUi(() =>
            {
                HideConfirmationForm();
                UpdateConfirmationMenu();

                // Show next item if available
                if (_confirmationQueue.PendingCount > 0)
                {
                    ShowCurrentConfirmation();
                }
            });
        });

        try
        {
            _currentForm.Show();
        }
        catch (Exception ex)
        {
            _audit.Log("confirmation.form_show_error", new { error = ex.Message });
        }
    }

    private void HideConfirmationForm()
    {
        if (_currentForm != null)
        {
            try { _currentForm.CloseWithoutResult(); } catch { }
            _currentForm = null;
        }
    }

    private void UpdateConfirmationMenu()
    {
        var count = _confirmationQueue.PendingCount;
        if (count > 0)
        {
            _approveItem.Visible = true;
            _rejectItem.Visible = true;
            _confirmSep.Visible = true;
            _approveItem.Text = $"✓ 确认录屏 (1/{count})";
            _rejectItem.Text = $"✗ 拒绝录屏 (1/{count})";

            var current = _confirmationQueue.Current;
            if (current != null)
            {
                _statusItem.Text = $"状态：● 等待确认（{current.TimeoutSeconds}s 内请操作）";
                _icon.Text = $"Agent Recorder — 等待确认 ({count})";
                _icon.ShowBalloonTip(5000, "✓ 请确认录屏请求",
                    $"当前队列有 {count} 个待确认请求。\n右键单击托盘图标确认或拒绝。",
                    ToolTipIcon.Warning);
            }
        }
        else
        {
            _approveItem.Visible = false;
            _rejectItem.Visible = false;
            _confirmSep.Visible = false;

            if (_activeRecordings.Count > 0)
            {
                UpdateRecordingUi();
            }
            else
            {
                _statusItem.Text = "状态：空闲";
                _icon.Text = "Agent Recorder — 空闲";
            }
        }
    }

    /// <summary>
    /// Triggered by tray menu item to approve (only local UI; cannot be called by HTTP API).
    /// </summary>
    private void ApproveFromMenu()
    {
        var current = _confirmationQueue.Current;
        if (current == null) return;

        _audit.Log("confirmation.approved_from_menu", new
        {
            confirmation_id = current.ConfirmationId,
            recording_id = current.RecordingId
        });

        _confirmationQueue.ApproveCurrent();

        RunOnUi(() =>
        {
            HideConfirmationForm();
            UpdateConfirmationMenu();

            if (_confirmationQueue.PendingCount > 0)
            {
                ShowCurrentConfirmation();
            }
        });
    }

    /// <summary>
    /// Triggered by tray menu item to reject (only local UI; cannot be called by HTTP API).
    /// </summary>
    private void RejectFromMenu()
    {
        var current = _confirmationQueue.Current;
        if (current == null) return;

        _audit.Log("confirmation.rejected_from_menu", new
        {
            confirmation_id = current.ConfirmationId,
            recording_id = current.RecordingId
        });

        _confirmationQueue.RejectCurrent();

        RunOnUi(() =>
        {
            HideConfirmationForm();
            UpdateConfirmationMenu();

            if (_confirmationQueue.PendingCount > 0)
            {
                ShowCurrentConfirmation();
            }
        });
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
        _confirmationQueue.Clear(invokeCallbacks: false); // Don't invoke callbacks, engine manages expiration
        HideConfirmationForm();
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
    /// Request local user to select a region. Shows full-screen selection window.
    /// Only local UI interaction; no HTTP API silent selection.
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
        var displays = SystemQuery.EnumDisplays();
        var displayCount = displays.Count;
        var processId = Environment.ProcessId;
        var sessionId = Native.GetCurrentSessionId();

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
        _confirmationQueue.Clear(invokeCallbacks: false);
        HideConfirmationForm();
        try { _uiInvoker.Dispose(); } catch { }
        Application.Exit();
    }

    /// <summary>
    /// Executes an action on the WinForms UI thread.
    /// Uses a dedicated hidden _uiInvoker control instead of relying on the first open form
    /// because tray applications may have zero open forms, which would cause UI
    /// operations to incorrectly run on the calling thread (e.g., HTTP worker thread).
    /// </summary>
    private void RunOnUi(Action a)
    {
        if (_uiInvoker.InvokeRequired)
            _uiInvoker.BeginInvoke(a);
        else
            a();
    }

    private static string GetString(JsonNode node, string key)
    {
        var val = node[key];
        if (val == null) return "";
        return val.ToString();
    }

    private static int? GetInt(JsonNode node, string key)
    {
        var val = node[key];
        if (val == null) return null;
        return (int?)val;
    }
}