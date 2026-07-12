using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using AgentRecorder.Core;
using AgentRecorder.Infrastructure;
using AgentRecorder.Logging;
using AgentRecorder.Windows;

namespace AgentRecorder.App;

internal sealed class TrayContext : ApplicationContext, ITrayContext
{
    public string HostMode => "tray";
    public bool SupportsRegionSelectionUi => true;
    public bool SupportsFloatingStopButton => true;
    public bool SupportsTrayStop => true;
    public bool SupportsGlobalStopHotkey => true;
    public bool IsGlobalStopHotkeyRegistered => _globalStopHotkey?.Registered ?? false;
    public string? GlobalStopHotkeyGesture => "Ctrl+Shift+F10";

    private readonly NotifyIcon _icon;
    private readonly RecordingEngine _engine;
    private readonly AuditLogger _audit;
    private readonly Dictionary<string, Recording> _activeRecordings = new();
    private readonly HashSet<string> _stoppingIds = new();
    private readonly RecordingIndicatorManager _indicatorManager;
    private readonly TrayIconFactory _iconFactory;
    private readonly IGlobalStopHotkey? _globalStopHotkey;
    private readonly ToolStripMenuItem _statusItem;
    private readonly ToolStripMenuItem _stopItem;
    private readonly ToolStripMenuItem _approveItem;
    private readonly ToolStripMenuItem _rejectItem;
    private readonly ToolStripSeparator _confirmSep;
    private readonly ToolStripMenuItem _languageItem;
    private readonly ToolStripMenuItem _languageZhCnItem;
    private readonly ToolStripMenuItem _languageEnUsItem;
    private readonly ToolStripMenuItem _openOutputFolderItem;
    private readonly ToolStripMenuItem _exitItem;
    private readonly Control _uiInvoker;
    private readonly IWindowActivator _confirmationWindowActivator;
    private IUiTextProvider _uiText;

    // Confirmation queue
    private readonly ConfirmationQueue _confirmationQueue = new();
    private ConfirmationForm? _currentForm;
    private bool _disposed;

    public TrayContext(RecordingEngine engine, AuditLogger audit)
        : this(engine, audit, hotkeyFactory: null)
    {
    }

    internal TrayContext(RecordingEngine engine, AuditLogger audit, Func<Action, IGlobalStopHotkey>? hotkeyFactory, IWindowActivator? confirmationWindowActivator = null, IUiTextProvider? uiTextProvider = null)
    {
        _engine = engine; _audit = audit;
        _confirmationWindowActivator = confirmationWindowActivator ?? DefaultWindowActivator.Instance;
        _uiText = uiTextProvider ?? new UiTextProvider(UiLanguageStore.LoadOrDefault());
        _iconFactory = new TrayIconFactory();
        _indicatorManager = new RecordingIndicatorManager(audit, OnFloatingStopRequested, () => _uiText);

        // UI dispatcher control: a hidden, zero-size control created on the UI thread,
        // used for marshalling calls from HTTP worker threads back to the WinForms UI thread.
        // We must not depend on the first open form because tray apps may have
        // zero open forms, which would cause UI operations to run on the wrong thread.
        // Keep it invisible and zero-sized so it never appears as a blank window.
        _uiInvoker = new Control
        {
            Visible = false,
            Width = 0,
            Height = 0
        };
        _ = _uiInvoker.Handle; // Force handle creation on this thread

        var menu = new ContextMenuStrip();

        // Confirmation area (shown only when pending requests, only triggered by local user from tray menu)
        _approveItem = new ToolStripMenuItem("", null, (_, _) => ApproveFromMenu())
        {
            Visible = false,
            ForeColor = System.Drawing.Color.DarkGreen,
            Font = new System.Drawing.Font("Segoe UI", 9, System.Drawing.FontStyle.Bold)
        };
        _rejectItem = new ToolStripMenuItem("", null, (_, _) => RejectFromMenu())
        {
            Visible = false,
            ForeColor = System.Drawing.Color.DarkRed
        };
        _confirmSep = new ToolStripSeparator() { Visible = false };

        _statusItem = new ToolStripMenuItem(_uiText.Get("Tray_Status_Idle")) { Enabled = false };
        _stopItem = new ToolStripMenuItem(_uiText.Get("Tray_Menu_Stop"), null, (_, _) => StopAll("tray_menu")) { Enabled = false };

        _languageZhCnItem = new ToolStripMenuItem(_uiText.Get("Tray_Language_ZhCn"), null, (_, _) => SetLanguage(UiLanguage.ZhCn));
        _languageEnUsItem = new ToolStripMenuItem(_uiText.Get("Tray_Language_EnUs"), null, (_, _) => SetLanguage(UiLanguage.EnUs));
        _languageItem = new ToolStripMenuItem(_uiText.Get("Tray_Menu_Language"));
        _languageItem.DropDownItems.Add(_languageZhCnItem);
        _languageItem.DropDownItems.Add(_languageEnUsItem);
        UpdateLanguageMenuChecks();

        menu.Items.Add(_approveItem);
        menu.Items.Add(_rejectItem);
        menu.Items.Add(_confirmSep);
        _openOutputFolderItem = new ToolStripMenuItem(_uiText.Get("Tray_Menu_OpenOutputDir"), null, (_, _) => OpenFolder());
        _exitItem = new ToolStripMenuItem(_uiText.Get("Tray_Menu_Exit"), null, (_, _) => ExitApp());

        menu.Items.Add(_statusItem);
        menu.Items.Add(_openOutputFolderItem);
        menu.Items.Add(_stopItem);
        menu.Items.Add(_languageItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(_exitItem);

        _icon = new NotifyIcon
        {
            Icon = _iconFactory.IdleIcon,
            Visible = true,
            Text = _uiText.Get("Tray_Idle"),
            ContextMenuStrip = menu
        };

        // Register global stop hotkey on the UI thread. Failure is logged but non-fatal.
        try
        {
            _globalStopHotkey = hotkeyFactory?.Invoke(OnGlobalHotkeyPressed)
                ?? new GlobalStopHotkey(OnGlobalHotkeyPressed, onError: ex => _audit.Log("tray.global_hotkey_callback_error", new { error = ex.Message }));
            var registered = _globalStopHotkey.Register();
            _audit.Log("tray.global_hotkey_state", new
            {
                registered,
                gesture = GlobalStopHotkeyGesture,
                win32_error = registered ? 0 : Marshal.GetLastWin32Error()
            });
        }
        catch (Exception ex)
        {
            _audit.Log("tray.global_hotkey_error", new { error = ex.Message, gesture = GlobalStopHotkeyGesture });
        }
    }

    private void UpdateLanguageMenuChecks()
    {
        _languageZhCnItem.Checked = _uiText.Language == UiLanguage.ZhCn;
        _languageEnUsItem.Checked = _uiText.Language == UiLanguage.EnUs;
    }

    private void SetLanguage(UiLanguage language)
    {
        if (_uiText.Language == language)
            return;

        UiLanguageStore.Save(language);
        _audit.Log("tray.language_changed", new { language = language.ToCultureName() });

        // Refresh the in-memory text provider for all future UI operations.
        // Already-open RegionSelectionForm / ConfirmationForm instances keep their
        // original language for stability; the next newly shown window will use
        // the updated language. This avoids lifecycle risks (closing, approving,
        // or rebuilding the current request) while still applying the change
        // immediately to the tray chrome and any new interactive surfaces.
        _uiText = new UiTextProvider(language);

        UpdateLanguageMenuChecks();
        RefreshTrayMenuText();
        UpdateRecordingUi();
    }

    private void RefreshTrayMenuText()
    {
        _languageItem.Text = _uiText.Get("Tray_Menu_Language");
        _languageZhCnItem.Text = _uiText.Get("Tray_Language_ZhCn");
        _languageEnUsItem.Text = _uiText.Get("Tray_Language_EnUs");
        _openOutputFolderItem.Text = _uiText.Get("Tray_Menu_OpenOutputDir");
        _exitItem.Text = _uiText.Get("Tray_Menu_Exit");

        UpdateConfirmationMenu();

        if (_activeRecordings.Count > 0)
            UpdateRecordingUi();
        else
            SetAllIdleUi();
    }

    /// <summary>
    /// Pop up recording confirmation (only local user via tray menu or confirmation form; no HTTP API remote confirmation).
    /// </summary>
    public void RequestConfirmation(object summary, Action<ConfirmationDecision> callback)
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

        var s = JsonNode.Parse(JsonSerializer.Serialize(current.Summary))!;
        var captureBounds = ConfirmationPreviewBuilder.ParseBounds(s);
        var workingAreas = Screen.AllScreens.Select(screen => screen.WorkingArea).ToList();
        var fallbackWorkingArea = GetFallbackWorkingArea(captureBounds, workingAreas);

        _currentForm = new ConfirmationForm(current, position, items.Count,
            onResult: decision =>
            {
                ResolveCurrentConfirmation(decision, decision.Approved ? "confirmation.ui_approved" : "confirmation.ui_rejected");
            },
            defaultOutputDirectory: OutputSettingsStore.GetEffectiveDefaultOutputDir(),
            windowActivator: _confirmationWindowActivator,
            auditLogger: (evt, payload) => _audit.Log(evt, payload),
            workingAreas: workingAreas,
            fallbackWorkingArea: fallbackWorkingArea,
            textProvider: _uiText);

        try
        {
            _currentForm.Show();
        }
        catch (Exception ex)
        {
            _audit.Log("confirmation.form_show_error", new { error = ex.Message });
        }
    }

    private static Rectangle GetFallbackWorkingArea(CaptureBounds? captureBounds, IReadOnlyList<Rectangle> workingAreas)
    {
        if (captureBounds == null)
        {
            var foregroundHandle = Native.GetForegroundWindow();
            if (foregroundHandle != IntPtr.Zero)
            {
                try
                {
                    var foregroundScreen = Screen.FromHandle(foregroundHandle);
                    if (foregroundScreen != null)
                        return foregroundScreen.WorkingArea;
                }
                catch { }
            }
        }

        return Screen.PrimaryScreen?.WorkingArea
            ?? (workingAreas.Count > 0 ? workingAreas[0] : Rectangle.Empty);
    }

    private void HideConfirmationForm(string? closeReason = null)
    {
        if (_currentForm != null)
        {
            try { _currentForm.CloseWithoutResult(closeReason); } catch { }
            _currentForm = null;
        }
    }

    /// <summary>
    /// Resolves the current confirmation by removing it from the queue and executing the callback.
    /// UI updates happen synchronously on the UI thread, while the callback (which may involve
    /// heavy operations like starting FFmpeg recording) is executed on a background thread.
    /// This prevents blocking the UI thread and the queue lock during recording startup.
    /// </summary>
    private void ResolveCurrentConfirmation(ConfirmationDecision decision, string auditEvent)
    {
        var current = _confirmationQueue.Current;
        if (current == null) return;

        var confirmationId = current.ConfirmationId;
        var recordingId = current.RecordingId;

        _audit.Log(auditEvent, new
        {
            confirmation_id = confirmationId,
            recording_id = recordingId,
            approved = decision.Approved,
            output_directory = decision.OutputDirectory ?? ""
        });

        var item = _confirmationQueue.ResolveCurrent();
        if (item == null) return;

        RunOnUi(() =>
        {
            HideConfirmationForm();
            UpdateConfirmationMenu();

            if (_confirmationQueue.PendingCount > 0)
            {
                ShowCurrentConfirmation();
            }
        });

        Task.Run(() =>
        {
            try
            {
                item.InvokeCallback(decision);
            }
            catch (Exception ex)
            {
                _audit.Log("confirmation.callback_error", new
                {
                    confirmation_id = confirmationId,
                    recording_id = recordingId,
                    approved = decision.Approved,
                    error = ex.Message,
                    stack = ex.StackTrace
                });
            }
        });
    }

    private void UpdateConfirmationMenu()
    {
        var count = _confirmationQueue.PendingCount;
        if (count > 0)
        {
            _approveItem.Visible = true;
            _rejectItem.Visible = true;
            _confirmSep.Visible = true;
            _approveItem.Text = _uiText.Format("Tray_Menu_Confirm", 1, count);
            _rejectItem.Text = _uiText.Format("Tray_Menu_Reject", 1, count);

            var current = _confirmationQueue.Current;
            if (current != null)
            {
                _statusItem.Text = _uiText.Format("Tray_Status_Waiting", current.TimeoutSeconds);
                _icon.Text = _uiText.Format("Tray_WaitingConfirmation", count);
                _icon.ShowBalloonTip(5000, _uiText.Get("Tray_Balloon_WaitingTitle"),
                    _uiText.Format("Tray_Balloon_WaitingBody", count),
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
                _statusItem.Text = _uiText.Get("Tray_Status_Idle");
                _icon.Text = _uiText.Get("Tray_Idle");
                _icon.Icon = _iconFactory.IdleIcon;
                _stopItem.Enabled = false;
            }
        }
    }

    /// <summary>
    /// Triggered by tray menu item to approve (only local UI; cannot be called by HTTP API).
    /// </summary>
    private void ApproveFromMenu()
    {
        ResolveCurrentConfirmation(ConfirmationDecision.Approve(), "confirmation.approved_from_menu");
    }

    /// <summary>
    /// Triggered by tray menu item to reject (only local UI; cannot be called by HTTP API).
    /// </summary>
    private void RejectFromMenu()
    {
        ResolveCurrentConfirmation(ConfirmationDecision.Reject(), "confirmation.rejected_from_menu");
    }

    public void SetRecording(object rec)
    {
        var recording = rec as Recording;
        if (recording == null) return;
        RunOnUi(() =>
        {
            _activeRecordings[recording.Id] = recording;
            _indicatorManager.ShowFor(recording);
            UpdateRecordingUi();
            if (_activeRecordings.Count == 1)
            {
                _icon.ShowBalloonTip(2000, _uiText.Get("Tray_Balloon_RecordingTitle"), _uiText.Get("Tray_Balloon_RecordingBody"), ToolTipIcon.Info);
            }
        });
    }

    public void SetIdle(object rec)
    {
        var recording = rec as Recording;
        RunOnUi(() =>
        {
            if (recording != null)
            {
                _activeRecordings.Remove(recording.Id);
                _stoppingIds.Remove(recording.Id);
                _indicatorManager.CloseFor(recording.Id, "recording.set_idle");
            }
            if (_activeRecordings.Count == 0)
                SetAllIdleUi();
            else
                UpdateRecordingUi();
        });
    }

    public void SetAllIdle() => RunOnUi(() =>
    {
        _activeRecordings.Clear();
        _stoppingIds.Clear();
        _indicatorManager.CloseAll("recording.set_all_idle");
        _confirmationQueue.Clear(invokeCallbacks: false); // Don't invoke callbacks, engine manages expiration
        HideConfirmationForm();
        SetAllIdleUi();
    });

    private void OnFloatingStopRequested(string recordingId)
    {
        StopRecording(recordingId, "floating_button");
    }

    private void OnGlobalHotkeyPressed()
    {
        StopAll("global_hotkey");
    }

    private void StopAll(string trigger)
    {
        var ids = _activeRecordings.Keys.ToList();
        if (ids.Count == 0)
        {
            _audit.Log("recording.stop_requested_local", new
            {
                trigger,
                active_count = 0,
                recording_ids = Array.Empty<string>()
            });
            return;
        }

        _audit.Log("recording.stop_requested_local", new
        {
            trigger,
            active_count = ids.Count,
            recording_ids = ids.ToArray()
        });

        foreach (var id in ids)
        {
            StopRecording(id, trigger);
        }

        UpdateRecordingUi();
    }

    private void StopRecording(string recordingId, string trigger)
    {
        if (!_activeRecordings.ContainsKey(recordingId))
            return;

        if (!_stoppingIds.Add(recordingId))
            return; // already stopping

        _audit.Log("recording_stop_control.stopping", new { recording_id = recordingId, trigger });
        UpdateRecordingUi();

        Task.Run(() =>
        {
            try
            {
                _engine.Stop(recordingId, trigger);
            }
            catch (Exception ex)
            {
                _audit.Log("recording.stop_error", new { recording_id = recordingId, trigger, error = ex.Message });
                RunOnUi(() =>
                {
                    _stoppingIds.Remove(recordingId);
                    _indicatorManager.ResetStopControlAfterFailure(recordingId);
                    UpdateRecordingUi();
                });
            }
        });
    }

    private void UpdateRecordingUi()
    {
        int count = _activeRecordings.Count;
        int stoppingCount = _stoppingIds.Count;
        bool allStopping = count > 0 && stoppingCount >= count;

        // Keep text within NotifyIcon's typical 128-byte tooltip limit.
        string text = count > 1
            ? _uiText.Format("Tray_Recording_WithCount", count)
            : _uiText.Get("Tray_Recording");
        if (allStopping)
            text = _uiText.Get("Tray_Stopping");
        if (text.Length > 127)
            text = text[..127];

        _icon.Text = text;
        _statusItem.Text = count > 1
            ? _uiText.Format("Tray_Status_RecordingWithCount", count)
            : _uiText.Get("Tray_Status_Recording");
        if (allStopping)
            _statusItem.Text = _uiText.Get("Tray_Status_Stopping");

        if (allStopping)
        {
            _icon.Icon = _iconFactory.StoppingIcon;
            _stopItem.Enabled = false;
            _stopItem.Text = _uiText.Get("Tray_Status_Stopping");
        }
        else if (count > 0)
        {
            _icon.Icon = _iconFactory.RecordingIcon;
            _stopItem.Enabled = true;
            _stopItem.Text = count > 1
                ? _uiText.Format("Tray_Menu_StopAll", count)
                : _uiText.Get("Tray_Menu_Stop");
        }
        else
        {
            SetAllIdleUi();
            return;
        }

        _audit.Log("tray.recording_state_changed", new
        {
            active_count = count,
            stopping_count = stoppingCount,
            state = allStopping ? "stopping" : "recording",
            nested_roles = _activeRecordings.Values.Select(r => r.NestedRole ?? "none").ToArray()
        });
    }

    private void SetAllIdleUi()
    {
        _icon.Text = _uiText.Get("Tray_Idle");
        _icon.Icon = _iconFactory.IdleIcon;
        _statusItem.Text = _uiText.Get("Tray_Status_Idle");
        _stopItem.Enabled = false;
        _stopItem.Text = _uiText.Get("Tray_Menu_Stop");
        _approveItem.Visible = false;
        _rejectItem.Visible = false;
        _confirmSep.Visible = false;
    }

    public void ShowError(string text) =>
        RunOnUi(() => _icon.ShowBalloonTip(4000, _uiText.Get("Tray_Balloon_ErrorTitle"), text, ToolTipIcon.Error));

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

                // Load last selected region to pre-populate the selection UI.
                Rectangle? initialBounds = null;
                var lastState = RegionSelectionStateStore.Load();
                if (lastState != null)
                {
                    initialBounds = new Rectangle(lastState.X, lastState.Y, lastState.Width, lastState.Height);
                }

                using var form = CreateRegionSelectionForm(initialBounds, e => _audit.Log(e.EventName, e.Payload), _uiText);
                callbackState.FormHandle = form.Handle;

                _audit.Log("region_selection.ui_opened", new
                {
                    stage = "handle_created",
                    thread_id = Thread.CurrentThread.ManagedThreadId,
                    form_handle = form.Handle.ToInt64(),
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

    /// <summary>
    /// Centralizes the production wiring that ensures the audit callback is attached
    /// before the form constructor emits <c>region_selection.ui_created</c>.
    /// This is the only supported way for production code to create a region selection form.
    /// </summary>
    internal static RegionSelectionForm CreateRegionSelectionForm(Rectangle? initialBounds,
        Action<RegionSelectionForm.RegionSelectionAuditEventArgs> auditCallback,
        IUiTextProvider textProvider)
    {
        return new RegionSelectionForm(initialBounds, onAuditEvent: auditCallback,
            textProvider: textProvider);
    }

    private class CallbackState
    {
        public int AlreadyCalled = 0;
        public bool CloseRequestedFromTimeout = false;
        public IntPtr FormHandle = IntPtr.Zero;
    }

    private void OpenFolder()
    {
        Directory.CreateDirectory(Paths.DefaultOutputDir);
        Process.Start(new ProcessStartInfo { FileName = Paths.DefaultOutputDir, UseShellExecute = true });
    }

    private void ExitApp()
    {
        DisposeResources();
        Application.Exit();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            DisposeResources();
        }
        base.Dispose(disposing);
    }

    private void DisposeResources()
    {
        if (_disposed)
            return;
        _disposed = true;

        try { _globalStopHotkey?.Dispose(); } catch { }
        _indicatorManager.CloseAll("recording.app_exit");
        _confirmationQueue.Clear(invokeCallbacks: false);
        HideConfirmationForm("app_exit");

        // Hide the icon before disposing the NotifyIcon and the icons it may reference.
        try { _icon.Visible = false; } catch { }
        try { _icon.Dispose(); } catch { }
        try { _icon.ContextMenuStrip?.Dispose(); } catch { }

        try { _iconFactory?.Dispose(); } catch { }
        try { _uiInvoker?.Dispose(); } catch { }
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
