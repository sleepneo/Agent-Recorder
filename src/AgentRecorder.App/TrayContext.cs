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

internal sealed class TrayContext : ApplicationContext, ITrayContext, IRecordingFailureNotifier
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
    private readonly IPerformanceTracer _tracer;
    private readonly ITrayBubblePolicy _bubblePolicy;
    private readonly ITrayBalloonTip _balloonTip;
    private readonly IIndicatorPresenter _indicatorPresenter;
    private readonly RecordingFailureNotificationManager _failureNotificationManager;
    private IUiTextProvider _uiText;

    // Confirmation queue
    private readonly ConfirmationQueue _confirmationQueue = new();
    private ConfirmationForm? _currentForm;
    private bool _disposed;

    public TrayContext(RecordingEngine engine, AuditLogger audit, IPerformanceTracer? tracer = null)
        : this(engine, audit, hotkeyFactory: null, tracer: tracer)
    {
    }

    internal TrayContext(RecordingEngine engine, AuditLogger audit, Func<Action, IGlobalStopHotkey>? hotkeyFactory, IWindowActivator? confirmationWindowActivator = null, IUiTextProvider? uiTextProvider = null, IPerformanceTracer? tracer = null, ITrayBubblePolicy? bubblePolicy = null, ITrayBalloonTip? balloonTip = null, IIndicatorPresenter? indicatorPresenter = null, IRecordingFailureNotificationPresenter? failureNotificationPresenter = null)
    {
        _engine = engine; _audit = audit;
        _tracer = tracer ?? NoOpPerformanceTracer.Instance;
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

        _bubblePolicy = bubblePolicy ?? new TrayBubblePolicy();
        _balloonTip = balloonTip ?? new NotifyIconBalloonTip(_icon);
        _indicatorPresenter = indicatorPresenter ?? new DefaultIndicatorPresenter(_indicatorManager);
        _failureNotificationManager = new RecordingFailureNotificationManager(
            _audit,
            () => _uiText,
            () => _activeRecordings.Count,
            failureNotificationPresenter);

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

        var confirmationId = current.ConfirmationId;
        var recordingId = current.RecordingId;

        _currentForm = new ConfirmationForm(current, position, items.Count,
            onResult: null,
            defaultOutputDirectory: OutputSettingsStore.GetEffectiveDefaultOutputDir(),
            windowActivator: _confirmationWindowActivator,
            auditLogger: (evt, payload) => _audit.Log(evt, payload),
            workingAreas: workingAreas,
            fallbackWorkingArea: fallbackWorkingArea,
            textProvider: _uiText,
            tracer: _tracer);

        _currentForm.Closed += (sender, e) =>
        {
            if (e.Decision == null) return;

            var item = _confirmationQueue.ResolveCurrent();
            if (item == null) return;

            // Capture the form reference before the UI thread nulls it out.
            var form = _currentForm;

            RunOnUi(() =>
            {
                _currentForm = null;
                UpdateConfirmationMenu();
                if (_confirmationQueue.PendingCount > 0)
                {
                    ShowCurrentConfirmation();
                }
            });

            var auditEvent = e.Decision.Approved ? "confirmation.ui_approved" : "confirmation.ui_rejected";
            FinishConfirmationWithDecision(item, e.Decision, auditEvent, confirmationId, recordingId, form);
        };

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
    /// Completes the confirmation after the form has delivered a decision.
    /// Waits for handle destruction and a DWM composition flush, then invokes
    /// the recording callback on a background thread. All timing and fallback
    /// state is audited.
    /// </summary>
    private async void FinishConfirmationWithDecision(
        PendingConfirmationItem item,
        ConfirmationDecision decision,
        string auditEvent,
        string confirmationId,
        string recordingId,
        IConfirmationDialog? form)
    {
        _audit.Log(auditEvent, new
        {
            confirmation_id = confirmationId,
            recording_id = recordingId,
            approved = decision.Approved,
            output_directory = decision.OutputDirectory ?? ""
        });

        var barrierStopwatch = Stopwatch.StartNew();
        bool usedFallback = false;
        bool flushed = false;

        try
        {
            // Wait for the native handle to be destroyed before flushing composition.
            if (form != null && form.IsHandleCreated)
            {
                var tcs = new TaskCompletionSource<object?>();
                EventHandler<ConfirmationDialogLifecycleEventArgs>? handler = null;
                handler = (_, _) =>
                {
                    form.HandleDestroyed -= handler;
                    tcs.TrySetResult(null);
                };
                form.HandleDestroyed += handler;
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                cts.Token.Register(() => tcs.TrySetCanceled());
                try { await tcs.Task; }
                catch { /* Bounded wait: proceed even if handle destruction was not observed. */ }
            }

            // Ensure DWM composition has settled after the form disappeared.
            var flushResult = DwmCompositionBarrier.Wait(TimeSpan.FromMilliseconds(200));
            flushed = flushResult.Flushed;
            usedFallback = flushResult.UsedFallback;
            barrierStopwatch.Stop();
        }
        catch
        {
            barrierStopwatch.Stop();
            usedFallback = true;
        }

        _audit.Log("confirmation.capture_safe", new
        {
            confirmation_id = confirmationId,
            recording_id = recordingId,
            approved = decision.Approved,
            barrier_ms = (long)barrierStopwatch.Elapsed.TotalMilliseconds,
            dwm_flush_completed = flushed,
            used_fallback = usedFallback
        });

        _ = Task.Run(() =>
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

    /// <summary>
    /// Fallback path when a confirmation decision must be resolved without a
    /// visible form. Performs the same background callback dispatch but skips
    /// the capture-safe barrier. Kept for completeness and source-level tests.
    /// </summary>
    private void ResolveCurrentConfirmation(ConfirmationDecision decision, string auditEvent)
    {
        var current = _confirmationQueue.Current;
        if (current == null) return;

        var confirmationId = current.ConfirmationId;
        var recordingId = current.RecordingId;

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
                // Confirmation-waiting shell balloons are permanently disabled:
                // the balloon is created before the recording becomes active and cannot be
                // reliably retracted once the user approves, so it would appear in the video.
                // Confirmation state is conveyed by the front-most form, tray menu and API.
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
    /// Closes the visible confirmation form with an approve decision; the form's
    /// <see cref="IConfirmationDialog.Closed"/> handler runs the shared capture-safe
    /// barrier and then invokes the recording callback on a background thread.
    /// </summary>
    private void ApproveFromMenu()
    {
        _currentForm?.CloseWithDecision(ConfirmationDecision.Approve(), _uiText.Get("Confirmation_Close_Approved"));
    }

    /// <summary>
    /// Triggered by tray menu item to reject (only local UI; cannot be called by HTTP API).
    /// </summary>
    private void RejectFromMenu()
    {
        _currentForm?.CloseWithDecision(ConfirmationDecision.Reject(), _uiText.Get("Confirmation_Close_Rejected"));
    }

    public void SetRecording(object rec)
    {
        var recording = rec as Recording;
        if (recording == null) return;
        RunOnUi(() =>
        {
            _activeRecordings[recording.Id] = recording;
            var resolution = TryResolveActiveParentForUi(recording, _activeRecordings);
            _indicatorManager.HideCountdownAndShowRecording(recording);
            _indicatorPresenter.ShowFor(recording, resolution.Parent, resolution.FallbackReason);
            UpdateRecordingUi();
            // "Recording started" tray balloons are intentionally never shown;
            // recording state is communicated by the indicator border, REC label,
            // floating stop button and dynamic tray icon/text.
        });
    }

    public void SetPreparing(object rec)
    {
        var recording = rec as Recording;
        if (recording == null) return;
        RunOnUi(() =>
        {
            _activeRecordings[recording.Id] = recording;
            var resolution = TryResolveActiveParentForUi(recording, _activeRecordings);
            _indicatorPresenter.ShowFor(recording, resolution.Parent, resolution.FallbackReason);
            _indicatorManager.ShowPreparing(recording, resolution.Parent, resolution.FallbackReason);
            UpdateRecordingUi();
        });
    }

    public void SetCountdown(object rec, int? remainingSeconds)
    {
        var recording = rec as Recording;
        if (recording == null) return;
        RunOnUi(() =>
        {
            _activeRecordings[recording.Id] = recording;
            if (remainingSeconds.HasValue)
            {
                _indicatorManager.ShowCountdown(recording, remainingSeconds.Value);
            }
            else
            {
                // Countdown reached zero: hide the digit overlay but keep the
                // amber preparing phase. The red REC phase is switched on only
                // by real first-frame evidence via SetRecording.
                _indicatorManager.HideCountdownOverlay(recording);
            }
            UpdateRecordingUi();
        });
    }

    public void SetFinalizing(object rec)
    {
        var recording = rec as Recording;
        if (recording == null) return;
        RunOnUi(() =>
        {
            _activeRecordings[recording.Id] = recording;
            _indicatorManager.ShowFinalizing(recording);
            UpdateRecordingUi();
        });
    }

    /// <summary>
    /// Resolves the active parent recording for a nested inner recording.
    /// Returns the parent only when the recording is an inner, its ParentRecordingId
    /// matches an active recording, that recording is an outer, and both share the
    /// same NestedSessionId (including the case where both are null).
    /// </summary>
    internal static Recording? ResolveActiveParentForUi(
        Recording recording,
        IDictionary<string, Recording> activeRecordings)
    {
        return TryResolveActiveParentForUi(recording, activeRecordings).Parent;
    }

    /// <summary>
    /// Resolves the active parent recording for a nested inner recording and preserves
    /// the exact reason when no valid parent can be returned. The fallback reason is
    /// forwarded to the indicator planner so production audit events do not fold
    /// distinct failures into a generic <c>parent_missing</c> value.
    /// </summary>
    internal static ParentResolutionResult TryResolveActiveParentForUi(
        Recording recording,
        IDictionary<string, Recording> activeRecordings)
    {
        if (!string.Equals(recording.NestedRole, "inner", StringComparison.OrdinalIgnoreCase))
            return new ParentResolutionResult(null, null);

        if (string.IsNullOrEmpty(recording.ParentRecordingId))
            return new ParentResolutionResult(null, "parent_missing");

        if (!activeRecordings.TryGetValue(recording.ParentRecordingId, out var candidate))
            return new ParentResolutionResult(null, "parent_missing");

        if (!string.Equals(candidate.NestedRole, "outer", StringComparison.OrdinalIgnoreCase))
            return new ParentResolutionResult(null, "parent_not_outer");

        if (!string.Equals(recording.NestedSessionId, candidate.NestedSessionId, StringComparison.Ordinal))
            return new ParentResolutionResult(null, "session_mismatch");

        return new ParentResolutionResult(candidate, null);
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
            _failureNotificationManager.ActiveRecordingCountChanged();
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
        _failureNotificationManager.ActiveRecordingCountChanged();
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

        // Categorize active recordings by visual phase so tray chrome can reflect
        // preparing/countdown/finalizing distinctly from the red REC state.
        int preparingCount = 0;
        int countdownCount = 0;
        int finalizingCount = 0;
        int recordingCount = 0;
        int? anyCountdownValue = null;
        foreach (var r in _activeRecordings.Values)
        {
            switch (r.State)
            {
                case RecState.preparing:
                    preparingCount++;
                    break;
                case RecState.countdown:
                    countdownCount++;
                    anyCountdownValue ??= r.Id switch
                    {
                        _ when _indicatorManager.IndicatorsForTests.TryGetValue(r.Id, out var ind) => ind.CountdownValueForTests,
                        _ => null
                    };
                    break;
                case RecState.finalizing:
                    finalizingCount++;
                    break;
                case RecState.recording:
                case RecState.stopping:
                    recordingCount++;
                    break;
            }
        }

        bool allStopping = count > 0 && stoppingCount >= count;

        // Keep text within NotifyIcon's typical 128-byte tooltip limit.
        string text;
        if (allStopping)
            text = _uiText.Get("Tray_Stopping");
        else if (finalizingCount > 0)
            text = _uiText.Get("Tray_Finalizing");
        else if (countdownCount > 0)
            text = _uiText.Format("Tray_Countdown", anyCountdownValue ?? 0);
        else if (preparingCount > 0)
            text = _uiText.Get("Tray_Preparing");
        else if (count > 1)
            text = _uiText.Format("Tray_Recording_WithCount", count);
        else
            text = _uiText.Get("Tray_Recording");
        if (text.Length > 127)
            text = text[..127];

        _icon.Text = text;

        string statusText;
        if (allStopping)
            statusText = _uiText.Get("Tray_Status_Stopping");
        else if (finalizingCount > 0)
            statusText = _uiText.Get("Tray_Status_Finalizing");
        else if (countdownCount > 0)
            statusText = _uiText.Format("Tray_Status_Countdown", anyCountdownValue ?? 0);
        else if (preparingCount > 0)
            statusText = _uiText.Get("Tray_Status_Preparing");
        else if (count > 1)
            statusText = _uiText.Format("Tray_Status_RecordingWithCount", count);
        else
            statusText = _uiText.Get("Tray_Status_Recording");
        _statusItem.Text = statusText;

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
            preparing_count = preparingCount,
            countdown_count = countdownCount,
            finalizing_count = finalizingCount,
            recording_count = recordingCount,
            state = allStopping ? "stopping" : (finalizingCount > 0 ? "finalizing" : (countdownCount > 0 ? "countdown" : (preparingCount > 0 ? "preparing" : "recording"))),
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

    private void ShowBalloonTipIfAllowed(BubbleType type, int timeout, string title, string body, ToolTipIcon icon)
    {
        if (_bubblePolicy.AllowShowBubble(type, _activeRecordings.Count))
        {
            _balloonTip.ShowBalloonTip(timeout, title, body, icon);
        }
    }

    public void ShowError(string text) =>
        RunOnUi(() => ShowBalloonTipIfAllowed(BubbleType.Error, 4000,
            _uiText.Get("Tray_Balloon_ErrorTitle"), text, ToolTipIcon.Error));

    public void ShowRecordingFailure(string recordingId, string reasonCode) =>
        RunOnUi(() => _failureNotificationManager.Request(recordingId, reasonCode));

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
        try { _failureNotificationManager.Dispose(); } catch { }
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

/// <summary>
/// Result of resolving the active parent for a nested inner recording.
/// <see cref="Parent"/> is non-null only for a fully valid parent; otherwise
/// <see cref="FallbackReason"/> carries the exact failure classification.
/// </summary>
internal sealed record ParentResolutionResult(Recording? Parent, string? FallbackReason);

/// <summary>
/// Minimal seam that decides how a recording is presented by the tray UI.
/// Production uses the default implementation that forwards to
/// <see cref="RecordingIndicatorManager"/>; tests inject a fake implementation
/// to verify the <see cref="TrayContext.SetRecording"/> wiring without showing
/// real windows.
/// </summary>
internal interface IIndicatorPresenter
{
    void ShowFor(Recording recording, Recording? parent, string? parentFallbackReason = null);
}

/// <summary>
/// Default presenter that forwards to the real <see cref="RecordingIndicatorManager"/>
/// while preserving the parent fallback reason for accurate audit logging.
/// </summary>
internal sealed class DefaultIndicatorPresenter : IIndicatorPresenter
{
    private readonly RecordingIndicatorManager _manager;

    public DefaultIndicatorPresenter(RecordingIndicatorManager manager)
    {
        _manager = manager;
    }

    public void ShowFor(Recording recording, Recording? parent, string? parentFallbackReason = null)
    {
        _manager.ShowFor(recording, parent, parentFallbackReason);
    }
}
