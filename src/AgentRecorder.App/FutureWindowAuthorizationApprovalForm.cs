using System.Drawing;
using System.Threading;
using System.Windows.Forms;
using AgentRecorder.Infrastructure;
using AgentRecorder.Windows;

namespace AgentRecorder.App;

internal sealed record FutureWindowAuthorizationApprovalDetails(
    string ExecutablePath,
    string FileIdentity,
    string Sha256,
    string? PublisherSubject,
    string? PublisherCertificateSha256,
    string? SystemAudioEndpointName,
    string? SystemAudioEndpointId,
    int MaximumDurationSeconds,
    int ValiditySeconds,
    string OutputPath,
    string UserSid,
    string SessionBinding)
{
    internal bool HasSystemAudio => !string.IsNullOrWhiteSpace(SystemAudioEndpointId);
}

/// <summary>Consent surface for a target window that does not exist yet; it deliberately shows no preview.</summary>
internal sealed class FutureWindowAuthorizationApprovalForm : Form
{
    private readonly Button _approve;
    private readonly Button _reject;
    private readonly Label _privacyWarning;
    private readonly Label _audioWarning;
    private readonly Label _keyboardNote;
    private readonly TextBox _scope;
    private readonly FlowLayoutPanel _actions;
    private readonly TableLayoutPanel _root;
    private readonly bool _showsAudioWarning;
    private readonly string _authorizationId;
    private readonly Action<string, object> _audit;
    private readonly IFutureWindowApprovalWindowPlatform _windowPlatform;
    private readonly Action? _afterNativeVisibleForTest;
    private FutureWindowApprovalOutcome _outcome = FutureWindowApprovalOutcome.Unavailable;
    private string _outcomeReason = "approval_ui_unavailable";
    private int _outcomeChosen;
    private int _cancellationRequested;
    private string _cancellationReason = "approval_ui_cancelled";
    private bool _nativeVisibilityConfirmed;
    private int _nativeVisibilityStarted;

    internal FutureWindowAuthorizationApprovalForm(
        FutureWindowAuthorizationApprovalDetails details,
        IUiTextProvider textProvider,
        string authorizationId = "fwa_test",
        Action<string, object>? audit = null,
        IFutureWindowApprovalWindowPlatform? windowPlatform = null,
        Action? afterNativeVisibleForTest = null)
    {
        ArgumentNullException.ThrowIfNull(details);
        ArgumentNullException.ThrowIfNull(textProvider);
        _authorizationId = authorizationId;
        _audit = audit ?? ((_, _) => { });
        _windowPlatform = windowPlatform ?? Win32FutureWindowApprovalWindowPlatform.Instance;
        _afterNativeVisibleForTest = afterNativeVisibleForTest;

        Text = textProvider.Get("FutureWindowApproval_Title");
        AccessibleName = textProvider.Get("FutureWindowApproval_AccessibleName");
        Name = "FutureWindowAuthorizationApprovalForm";
        StartPosition = FormStartPosition.Manual;
        FormBorderStyle = FormBorderStyle.Sizable;
        AutoScaleMode = AutoScaleMode.Dpi;
        AutoScaleDimensions = new SizeF(96F, 96F);
        MinimumSize = new Size(520, 420);
        ClientSize = new Size(900, 760);
        ShowInTaskbar = true;
        BackColor = Color.FromArgb(30, 33, 38);
        ForeColor = Color.White;
        Font = new Font("Segoe UI", 10F, FontStyle.Regular, GraphicsUnit.Point);

        _root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(20),
            ColumnCount = 1,
            RowCount = 3,
            BackColor = BackColor,
        };
        _root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        _root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        _root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        _root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        Controls.Add(_root);

        var header = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            ColumnCount = 1,
            RowCount = 3,
            Margin = new Padding(0, 0, 0, 12),
        };
        header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        for (var i = 0; i < 3; i++)
            header.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        var heading = new Label
        {
            AutoSize = true,
            Text = textProvider.Get("FutureWindowApproval_Heading"),
            Font = new Font(Font, FontStyle.Bold),
            ForeColor = Color.FromArgb(125, 211, 252),
            Margin = new Padding(0, 0, 0, 8),
        };
        _privacyWarning = new Label
        {
            AutoSize = true,
            Text = textProvider.Get("FutureWindowApproval_PrivacyWarning"),
            ForeColor = Color.FromArgb(253, 186, 116),
            Margin = new Padding(0, 0, 0, 6),
            Name = "privacy_warning",
        };
        _audioWarning = new Label
        {
            AutoSize = true,
            Text = textProvider.Get("FutureWindowApproval_AudioWarning"),
            ForeColor = Color.FromArgb(253, 186, 116),
            Margin = new Padding(0, 0, 0, 4),
            Name = "audio_warning",
            Visible = details.HasSystemAudio,
        };
        _showsAudioWarning = details.HasSystemAudio;
        header.Controls.Add(heading, 0, 0);
        header.Controls.Add(_privacyWarning, 0, 1);
        header.Controls.Add(_audioWarning, 0, 2);
        _root.Controls.Add(header, 0, 0);

        _scope = new TextBox
        {
            Dock = DockStyle.Fill,
            Multiline = true,
            ReadOnly = true,
            ScrollBars = ScrollBars.Both,
            WordWrap = false,
            BackColor = Color.FromArgb(40, 44, 52),
            ForeColor = Color.White,
            BorderStyle = BorderStyle.FixedSingle,
            Text = BuildScope(details, textProvider),
            Name = "authorization_scope",
            AccessibleName = textProvider.Get("FutureWindowApproval_ScopeAccessibleName"),
            Margin = new Padding(0),
        };
        _root.Controls.Add(_scope, 0, 1);

        var footer = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            ColumnCount = 1,
            RowCount = 2,
            Margin = new Padding(0, 12, 0, 0),
        };
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        footer.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        footer.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        _actions = new FlowLayoutPanel
        {
            AutoSize = true,
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.RightToLeft,
            WrapContents = true,
            Margin = new Padding(0, 0, 0, 6),
        };
        _approve = new Button
        {
            AutoSize = true,
            Text = textProvider.Get("FutureWindowApproval_Approve"),
            DialogResult = DialogResult.Yes,
            Name = "approve_future_window",
            Margin = new Padding(8, 0, 0, 0),
        };
        _reject = new Button
        {
            AutoSize = true,
            Text = textProvider.Get("FutureWindowApproval_Reject"),
            DialogResult = DialogResult.No,
            Name = "reject_future_window",
        };
        _actions.Controls.Add(_approve);
        _actions.Controls.Add(_reject);
        footer.Controls.Add(_actions, 0, 0);

        _keyboardNote = new Label
        {
            AutoSize = true,
            Text = textProvider.Get("FutureWindowApproval_Keyboard"),
            ForeColor = Color.FromArgb(203, 213, 225),
            Margin = new Padding(0),
            Name = "keyboard_reject_note",
        };
        footer.Controls.Add(_keyboardNote, 0, 1);
        _root.Controls.Add(footer, 0, 2);

        void FitHeaderText()
        {
            var width = Math.Max(160, _root.ClientSize.Width - _root.Padding.Horizontal);
            foreach (var label in new[] { heading, _privacyWarning, _audioWarning, _keyboardNote })
                label.MaximumSize = new Size(width, 0);
        }

        _root.SizeChanged += (_, _) => FitHeaderText();
        FitHeaderText();

        // Enter and Escape are deliberately rejection keys even if focus is moved
        // to the approval button. Approval requires a deliberate button click.
        AcceptButton = _reject;
        CancelButton = _reject;
        ActiveControl = _reject;
        Shown += (_, _) =>
        {
            _reject.Focus();
            EnsureNativeVisibility();
        };
    }

    internal Button ApproveButtonForTests => _approve;
    internal Button RejectButtonForTests => _reject;
    internal string ScopeTextForTests => _scope.Text;
    internal string PrivacyWarningTextForTests => _privacyWarning.Text;
    internal string AudioWarningTextForTests => _audioWarning.Text;
    internal string AccessibleNameForTests => _scope.AccessibleName ?? string.Empty;
    internal string WindowAccessibleNameForTests => AccessibleName ?? string.Empty;
    internal bool AudioWarningVisibleForTests => _showsAudioWarning;
    internal bool ScopeCanBeInspectedForTests => _scope.ReadOnly && _scope.Multiline &&
        _scope.ScrollBars == ScrollBars.Both && !_scope.WordWrap;
    internal bool SafeDefaultsForTests => ReferenceEquals(AcceptButton, _reject) &&
        ReferenceEquals(CancelButton, _reject) && ReferenceEquals(ActiveControl, _reject);
    internal bool ButtonsFitFooterForTests => _approve.Bottom <= _actions.ClientSize.Height &&
        _reject.Bottom <= _actions.ClientSize.Height;
    internal AutoScaleMode AutoScaleModeForTests => AutoScaleMode;
    internal bool ProcessKeyForTests(Keys key)
    {
        ActiveControl = _approve;
        var message = new Message();
        return ProcessCmdKey(ref message, key);
    }
    internal bool LayoutKeepsWarningsAndActionsSeparateForTests
    {
        get
        {
            var warningParent = _privacyWarning.Parent;
            var scopeParent = _scope.Parent;
            var actionsParent = _actions.Parent;
            var keyboardParent = _keyboardNote.Parent;
            if (warningParent is null || scopeParent is null || actionsParent is null || keyboardParent is null)
                return false;
            var warning = _root.RectangleToClient(warningParent.RectangleToScreen(_privacyWarning.Bounds));
            var scope = _root.RectangleToClient(scopeParent.RectangleToScreen(_scope.Bounds));
            var actions = _root.RectangleToClient(actionsParent.RectangleToScreen(_actions.Bounds));
            var keyboard = _root.RectangleToClient(keyboardParent.RectangleToScreen(_keyboardNote.Bounds));
            return !warning.IntersectsWith(scope) && !scope.IntersectsWith(actions) &&
                !warning.IntersectsWith(actions) && !keyboard.IntersectsWith(scope) &&
                _actions.Bottom <= actionsParent.ClientSize.Height;
        }
    }

    internal FutureWindowApprovalOutcome OutcomeForTests => _outcome;
    internal bool NativeVisibilityConfirmedForTests => _nativeVisibilityConfirmed;

    internal static FutureWindowApprovalOutcome ShowModal(
        string authorizationId,
        FutureWindowAuthorizationApprovalDetails details,
        IUiTextProvider textProvider,
        Action<string, object> audit,
        CancellationToken cancellationToken,
        IFutureWindowApprovalWindowPlatform? windowPlatform = null,
        Action? afterNativeVisibleForTest = null,
        bool disableApprovalForTest = false)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            SafeAudit(audit, "cancelled", new
            {
                process_id = Environment.ProcessId,
                thread_id = Environment.CurrentManagedThreadId,
                reason_code = "approval_ui_cancelled_before_handle",
            });
            return FutureWindowApprovalOutcome.Cancelled;
        }

        using var form = new FutureWindowAuthorizationApprovalForm(
            details, textProvider, authorizationId, audit, windowPlatform, afterNativeVisibleForTest);
        if (disableApprovalForTest)
            form._approve.Enabled = false;
        using var registration = cancellationToken.Register(() =>
            form.RequestCancellationClose("approval_ui_cancelled"));
        form.Shown += (_, _) =>
        {
            if (cancellationToken.IsCancellationRequested)
                form.RequestCancellationClose("approval_ui_cancelled");
        };

        try
        {
            _ = form.ShowDialog();
            if (cancellationToken.IsCancellationRequested && form._outcome == FutureWindowApprovalOutcome.Approved)
            {
                // The durable approval transaction is still the final authority,
                // but cancellation must never be reported as an approval result.
                return FutureWindowApprovalOutcome.Cancelled;
            }
            return form._outcome;
        }
        finally
        {
            try { form.TopMost = false; } catch { }
            var outcome = form._outcome;
            form.Log("closed", new
            {
                process_id = Environment.ProcessId,
                thread_id = Environment.CurrentManagedThreadId,
                outcome = outcome.ToString().ToLowerInvariant(),
                reason_code = form._outcomeReason,
                native_visible_confirmed = form._nativeVisibilityConfirmed,
                bounds = BoundsPayload(form.Bounds),
            });
        }
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        FutureWindowApprovalNativeState? state = null;
        try { state = _windowPlatform.Inspect(Handle); } catch { }
        Log("handle_created", state is null
            ? new { process_id = Environment.ProcessId, thread_id = Environment.CurrentManagedThreadId,
                reason_code = "native_window_inspection_unavailable" }
            : NativePayload(state));
        if (Volatile.Read(ref _cancellationRequested) != 0)
            QueueCancellationClose();
        else
        {
            try { BeginInvoke(new Action(EnsureNativeVisibility)); }
            catch (InvalidOperationException) { }
        }
    }

    protected override void OnLoad(EventArgs e)
    {
        ApplySafeBounds();
        base.OnLoad(e);
    }

    protected override void OnDpiChanged(DpiChangedEventArgs e)
    {
        base.OnDpiChanged(e);
        ApplySafeBounds();
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (Interlocked.CompareExchange(ref _outcomeChosen, 1, 0) == 0)
        {
            if (Volatile.Read(ref _cancellationRequested) != 0)
            {
                _outcome = FutureWindowApprovalOutcome.Cancelled;
                _outcomeReason = _cancellationReason;
            }
            else if (DialogResult == DialogResult.Yes && _nativeVisibilityConfirmed)
            {
                _outcome = FutureWindowApprovalOutcome.Approved;
                _outcomeReason = "user_approved";
            }
            else if (DialogResult == DialogResult.No)
            {
                _outcome = FutureWindowApprovalOutcome.Rejected;
                _outcomeReason = "user_rejected";
            }
            else if (!_nativeVisibilityConfirmed)
            {
                _outcome = FutureWindowApprovalOutcome.Unavailable;
                _outcomeReason = "native_window_not_visible";
            }
            else
            {
                _outcome = FutureWindowApprovalOutcome.Rejected;
                _outcomeReason = "window_closed";
            }

            Log(_outcome switch
            {
                FutureWindowApprovalOutcome.Approved => "user_approved",
                FutureWindowApprovalOutcome.Rejected => "user_rejected",
                FutureWindowApprovalOutcome.Cancelled => "cancelled",
                _ => "display_failed",
            }, new
            {
                process_id = Environment.ProcessId,
                thread_id = Environment.CurrentManagedThreadId,
                reason_code = _outcomeReason,
                native_visible_confirmed = _nativeVisibilityConfirmed,
                bounds = BoundsPayload(Bounds),
            });
        }
        base.OnFormClosing(e);
    }

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if ((keyData & Keys.KeyCode) is Keys.Enter or Keys.Escape)
        {
            RejectAndClose((keyData & Keys.KeyCode) == Keys.Enter ? "enter_safe_reject" : "escape_safe_reject");
            return true;
        }
        return base.ProcessCmdKey(ref msg, keyData);
    }

    private void RejectAndClose(string reasonCode = "user_rejected")
    {
        if (IsDisposed)
            return;
        if (Interlocked.CompareExchange(ref _outcomeChosen, 1, 0) == 0)
        {
            _outcome = FutureWindowApprovalOutcome.Rejected;
            _outcomeReason = reasonCode;
            Log("user_rejected", new
            {
                process_id = Environment.ProcessId,
                thread_id = Environment.CurrentManagedThreadId,
                reason_code = reasonCode,
                native_visible_confirmed = _nativeVisibilityConfirmed,
                bounds = BoundsPayload(Bounds),
            });
        }
        DialogResult = DialogResult.No;
        Close();
    }

    private void RequestCancellationClose(string reasonCode)
    {
        _cancellationReason = reasonCode;
        Interlocked.Exchange(ref _cancellationRequested, 1);
        if (!IsHandleCreated || IsDisposed)
            return;
        QueueCancellationClose();
    }

    private void QueueCancellationClose()
    {
        try
        {
            if (IsDisposed || !IsHandleCreated)
                return;
            if (InvokeRequired)
                BeginInvoke(new Action(CloseForCancellation));
            else
                CloseForCancellation();
        }
        catch (ObjectDisposedException) { }
        catch (InvalidOperationException)
        {
            // OnHandleCreated checks the remembered cancellation after the HWND
            // exists, so a pre-handle cancellation is not lost.
        }
    }

    private void CloseForCancellation()
    {
        if (IsDisposed)
            return;
        if (Interlocked.CompareExchange(ref _outcomeChosen, 1, 0) == 0)
        {
            _outcome = FutureWindowApprovalOutcome.Cancelled;
            _outcomeReason = _cancellationReason;
            Log("cancelled", new
            {
                process_id = Environment.ProcessId,
                thread_id = Environment.CurrentManagedThreadId,
                reason_code = _cancellationReason,
                native_visible_confirmed = _nativeVisibilityConfirmed,
                bounds = BoundsPayload(Bounds),
            });
        }
        DialogResult = DialogResult.Abort;
        Close();
    }

    private void EnsureNativeVisibility()
    {
        if (Volatile.Read(ref _cancellationRequested) != 0)
        {
            QueueCancellationClose();
            return;
        }
        if (Interlocked.Exchange(ref _nativeVisibilityStarted, 1) != 0)
            return;

        try
        {
            TopMost = true;
            _windowPlatform.ShowNormal(Handle);
            var state = _windowPlatform.Inspect(Handle);
            if (!state.IsVisible || state.IsMinimized || !state.IsWithinWorkingArea)
            {
                _outcome = FutureWindowApprovalOutcome.Unavailable;
                _outcomeReason = !state.IsVisible ? "native_window_not_visible" :
                    state.IsMinimized ? "native_window_minimized" : "native_window_outside_work_area";
                Interlocked.Exchange(ref _outcomeChosen, 1);
                Log("display_failed", NativePayload(state, _outcomeReason));
                DialogResult = DialogResult.Abort;
                Close();
                return;
            }

            _nativeVisibilityConfirmed = true;
            Log("native_visible", NativePayload(state));
            Log("decision_waiting", new
            {
                process_id = state.ProcessId,
                thread_id = state.ThreadId,
                native_visible = state.IsVisible,
                minimized = state.IsMinimized,
                within_working_area = state.IsWithinWorkingArea,
                bounds = BoundsPayload(state.Bounds),
            });
            var foreground = _windowPlatform.Activate(Handle);
            Log("foreground_result", new
            {
                process_id = state.ProcessId,
                thread_id = state.ThreadId,
                set_foreground_succeeded = foreground.SetForegroundSucceeded,
                bring_to_top_succeeded = foreground.BringToTopSucceeded,
                foreground_confirmed = foreground.IsForeground,
                native_visible = state.IsVisible,
                minimized = state.IsMinimized,
                bounds = BoundsPayload(state.Bounds),
                working_area = BoundsPayload(state.WorkingArea),
            });
            _afterNativeVisibleForTest?.Invoke();
        }
        catch (Exception exception)
        {
            _outcome = FutureWindowApprovalOutcome.Unavailable;
            _outcomeReason = "native_window_inspection_failed";
            Interlocked.Exchange(ref _outcomeChosen, 1);
            Log("display_failed", new
            {
                process_id = Environment.ProcessId,
                thread_id = Environment.CurrentManagedThreadId,
                exception_type = exception.GetType().Name,
                reason_code = _outcomeReason,
                bounds = BoundsPayload(Bounds),
            });
            DialogResult = DialogResult.Abort;
            Close();
        }
    }

    private void ApplySafeBounds()
    {
        Rectangle workArea;
        try
        {
            workArea = Screen.FromPoint(Cursor.Position).WorkingArea;
            if (workArea.Width <= 0 || workArea.Height <= 0)
                throw new InvalidOperationException("No usable monitor working area.");
        }
        catch
        {
            workArea = Screen.PrimaryScreen?.WorkingArea ?? new Rectangle(0, 0, 1024, 768);
        }

        var width = Math.Clamp(Size.Width, 1, Math.Max(1, workArea.Width - 24));
        var height = Math.Clamp(Size.Height, 1, Math.Max(1, workArea.Height - 24));
        MinimumSize = new Size(Math.Min(520, width), Math.Min(420, height));
        Size = new Size(width, height);
        Bounds = new Rectangle(
            workArea.Left + Math.Max(0, (workArea.Width - width) / 2),
            workArea.Top + Math.Max(0, (workArea.Height - height) / 2),
            width,
            height);
    }

    private static object NativePayload(FutureWindowApprovalNativeState state, string? reasonCode = null) => new
    {
        process_id = state.ProcessId,
        thread_id = state.ThreadId,
        native_visible = state.IsVisible,
        minimized = state.IsMinimized,
        within_working_area = state.IsWithinWorkingArea,
        bounds = BoundsPayload(state.Bounds),
        working_area = BoundsPayload(state.WorkingArea),
        reason_code = reasonCode,
    };

    private void Log(string stage, object payload) => SafeAudit(_audit, stage, payload);

    private static void SafeAudit(Action<string, object> audit, string stage, object payload)
    {
        try { audit(stage, payload); } catch { }
    }

    private static object BoundsPayload(Rectangle bounds) => new
    {
        x = bounds.X,
        y = bounds.Y,
        width = bounds.Width,
        height = bounds.Height,
    };

    private static string BuildScope(
        FutureWindowAuthorizationApprovalDetails details,
        IUiTextProvider text)
    {
        var unavailable = text.Get("FutureWindowApproval_Unavailable");
        var audio = details.HasSystemAudio
            ? text.Format("FutureWindowApproval_AudioLoopback",
                details.SystemAudioEndpointName ?? unavailable,
                details.SystemAudioEndpointId!)
            : text.Get("FutureWindowApproval_AudioNone");
        return $"{text.Get("FutureWindowApproval_ExecutablePath")}\r\n{details.ExecutablePath}\r\n\r\n" +
            $"{text.Get("FutureWindowApproval_FileIdentity")}\r\n{details.FileIdentity}\r\n\r\n" +
            $"{text.Get("FutureWindowApproval_Sha256")}\r\n{details.Sha256}\r\n\r\n" +
            $"{text.Get("FutureWindowApproval_PublisherEvidence")}\r\n" +
            $"{text.Get("FutureWindowApproval_PublisherSubject")} {details.PublisherSubject ?? unavailable}\r\n" +
            $"{text.Get("FutureWindowApproval_PublisherCertificateSha256")} {details.PublisherCertificateSha256 ?? unavailable}\r\n\r\n" +
            $"{text.Get("FutureWindowApproval_TargetScope")}\r\n{text.Get("FutureWindowApproval_TargetDescription")}\r\n\r\n" +
            $"{text.Get("FutureWindowApproval_AudioScope")}\r\n{audio}\r\n" +
            $"{text.Format("FutureWindowApproval_MaximumDuration", details.MaximumDurationSeconds)}\r\n" +
            $"{text.Get("FutureWindowApproval_NoCountdown")}\r\n" +
            $"{text.Format("FutureWindowApproval_Validity", details.ValiditySeconds)}\r\n" +
            $"{text.Format("FutureWindowApproval_LatestStart",
                details.ValiditySeconds - details.MaximumDurationSeconds, details.MaximumDurationSeconds)}\r\n" +
            $"{text.Get("FutureWindowApproval_NoShortening")}\r\n\r\n" +
            $"{text.Get("FutureWindowApproval_OutputPath")}\r\n{details.OutputPath}\r\n\r\n" +
            $"{text.Get("FutureWindowApproval_UserSid")} {details.UserSid}\r\n" +
            $"{text.Get("FutureWindowApproval_SessionBinding")} {details.SessionBinding}\r\n\r\n" +
            $"{text.Get("FutureWindowApproval_NoPreview")}\r\n" +
            $"{text.Get("FutureWindowApproval_OneLaterRun")}\r\n" +
            text.Get("FutureWindowApproval_LocalRevocation");
    }
}
