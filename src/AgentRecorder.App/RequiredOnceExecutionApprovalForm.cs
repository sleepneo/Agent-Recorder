using System.Drawing;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;
using AgentRecorder.Core;
using AgentRecorder.Core.Automation;

namespace AgentRecorder.App;

internal sealed record RequiredOnceExecutionApprovalDetails(
    string DisplayName,
    string StableDisplayFingerprint,
    AuthorizedPhysicalRectangle DisplayBounds,
    AuthorizedPhysicalRectangle RegionWithinDisplay,
    DateTimeOffset ScheduledStartUtc,
    DateTimeOffset LatestStartUtc,
    TimeSpan Duration,
    string FrozenOutputFilePath);

internal enum RequiredOnceExecutionApprovalResult
{
    Approved,
    Rejected,
    TimedOut,
    HostShutdown,
    Unavailable,
}

internal interface IRequiredOncePlanExecutionUi
{
    bool IsInteractiveDesktopAvailable { get; }
    Task<RequiredOnceExecutionApprovalResult> ShowExecutionApprovalAsync(
        RequiredOnceExecutionApprovalDetails details,
        CancellationToken cancellationToken);
}

internal sealed class TrayRequiredOncePlanExecutionUi : IRequiredOncePlanExecutionUi
{
    private const string AuditEventPrefix = "required_once_execution_dialog.";
    private readonly TrayContext _tray;
    private readonly IWindowActivator _activator;
    private readonly Action<string, object> _audit;
    private readonly Func<bool> _desktopProbe;
    private readonly Func<DateTimeOffset> _utcNow;
    private readonly Func<IReadOnlyList<Rectangle>> _workingAreas;

    internal TrayRequiredOncePlanExecutionUi(
        TrayContext tray,
        Action<string, object> audit,
        IWindowActivator? activator = null,
        Func<bool>? desktopProbeForTest = null,
        Func<DateTimeOffset>? utcNowForTest = null,
        Func<IReadOnlyList<Rectangle>>? workingAreasForTest = null)
    {
        _tray = tray ?? throw new ArgumentNullException(nameof(tray));
        _audit = audit ?? throw new ArgumentNullException(nameof(audit));
        _activator = activator ?? tray.ConfirmationWindowActivator;
        _desktopProbe = desktopProbeForTest ?? InteractiveDesktopAvailable;
        _utcNow = utcNowForTest ?? (() => DateTimeOffset.UtcNow);
        _workingAreas = workingAreasForTest ?? (() => Screen.AllScreens.Select(screen => screen.WorkingArea).ToArray());
    }

    public bool IsInteractiveDesktopAvailable
    {
        get
        {
            try { return _tray.SupportsRegionSelectionUi && _desktopProbe(); }
            catch { return false; }
        }
    }

    public Task<RequiredOnceExecutionApprovalResult> ShowExecutionApprovalAsync(
        RequiredOnceExecutionApprovalDetails details,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(details);
        var requestId = Guid.NewGuid().ToString("N");
        Log(requestId, "requested", new { cancellation_requested = cancellationToken.IsCancellationRequested });
        if (cancellationToken.IsCancellationRequested)
        {
            LogUnavailable(requestId, "host_shutdown");
            return Task.FromResult(RequiredOnceExecutionApprovalResult.HostShutdown);
        }
        if (!IsInteractiveDesktopAvailable)
        {
            LogUnavailable(requestId, "interactive_desktop_unavailable");
            return Task.FromResult(RequiredOnceExecutionApprovalResult.Unavailable);
        }

        var completion = new TaskCompletionSource<RequiredOnceExecutionApprovalResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                var result = RequiredOnceExecutionApprovalForm.ShowModal(
                    details, cancellationToken, requestId, _activator,
                    (stage, payload) => Log(requestId, stage, payload),
                    _utcNow, _workingAreas);
                completion.TrySetResult(result);
            }
            catch
            {
                LogUnavailable(requestId, "dialog_creation_failed");
                completion.TrySetResult(cancellationToken.IsCancellationRequested
                    ? RequiredOnceExecutionApprovalResult.HostShutdown
                    : RequiredOnceExecutionApprovalResult.Unavailable);
            }
        }) { IsBackground = true, Name = "Required-once execution approval" };
        try
        {
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
        }
        catch
        {
            LogUnavailable(requestId, "dialog_thread_unavailable");
            completion.TrySetResult(RequiredOnceExecutionApprovalResult.Unavailable);
        }
        return completion.Task;
    }

    private void LogUnavailable(string requestId, string reasonCode) =>
        Log(requestId, "unavailable", new { reason_code = reasonCode });

    private void Log(string requestId, string stage, object payload)
    {
        try { _audit(NormalizeAuditEventName(stage), new { request_id = requestId, payload }); }
        catch { }
    }

    internal static string NormalizeAuditEventName(string stage) =>
        stage.StartsWith(AuditEventPrefix, StringComparison.Ordinal) ? stage : AuditEventPrefix + stage;

    private static bool InteractiveDesktopAvailable()
    {
        nint input = 0;
        try
        {
            if (!Environment.UserInteractive) return false;
            input = OpenInputDesktop(0, false, 1);
            var current = GetThreadDesktop(GetCurrentThreadId());
            return input != 0 && DesktopName(input) == "Default" && DesktopName(current) == "Default";
        }
        catch { return false; }
        finally { if (input != 0) CloseDesktop(input); }
    }

    private static string? DesktopName(nint desktop)
    {
        var name = new StringBuilder(256);
        return GetUserObjectInformation(desktop, 2, name, name.Capacity * 2, out _) ? name.ToString() : null;
    }

    [DllImport("user32.dll")] private static extern nint OpenInputDesktop(uint flags, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint access);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CloseDesktop(nint desktop);
    [DllImport("user32.dll")] private static extern nint GetThreadDesktop(uint threadId);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetUserObjectInformation(nint handle, int index, StringBuilder info, int length, out int needed);
}

internal sealed class RequiredOnceExecutionApprovalForm : Form
{
    private const int BoundsInset = 12;
    // The coordinator retains its 60-second cancellation as a final safety net.
    private const int DialogTimeoutSeconds = 55;
    private readonly RequiredOnceExecutionApprovalDetails _details;
    private readonly CancellationToken _cancellationToken;
    private readonly IWindowActivator _activator;
    private readonly Action<string, object> _audit;
    private readonly Func<DateTimeOffset> _utcNow;
    private readonly Func<IReadOnlyList<Rectangle>> _workingAreas;
    private readonly DateTimeOffset _boundedDeadlineUtc;
    private readonly System.Windows.Forms.Timer _deadlineTimer;
    private readonly System.Windows.Forms.Timer _foregroundRetryTimer;
    private readonly ExplicitClickButton _approveButton;
    private RequiredOnceExecutionApprovalResult _result = RequiredOnceExecutionApprovalResult.Unavailable;
    private bool _completed;
    private int _foregroundAttempts;

    internal RequiredOnceExecutionApprovalForm(
        RequiredOnceExecutionApprovalDetails details,
        CancellationToken cancellationToken,
        string requestId,
        IWindowActivator activator,
        Action<string, object> audit,
        Func<DateTimeOffset> utcNow,
        Func<IReadOnlyList<Rectangle>> workingAreas)
    {
        _details = details ?? throw new ArgumentNullException(nameof(details));
        _cancellationToken = cancellationToken;
        RequestId = requestId;
        _activator = activator ?? throw new ArgumentNullException(nameof(activator));
        _audit = audit ?? throw new ArgumentNullException(nameof(audit));
        _utcNow = utcNow ?? throw new ArgumentNullException(nameof(utcNow));
        _workingAreas = workingAreas ?? throw new ArgumentNullException(nameof(workingAreas));
        DateTimeOffset createdAt;
        try { createdAt = _utcNow(); }
        catch { createdAt = DateTimeOffset.MinValue; }
        _boundedDeadlineUtc = createdAt.Offset == TimeSpan.Zero
            ? Min(details.LatestStartUtc, createdAt.AddSeconds(DialogTimeoutSeconds))
            : details.LatestStartUtc;

        Text = "确认开始计划录制 / Confirm scheduled recording";
        StartPosition = FormStartPosition.Manual;
        FormBorderStyle = FormBorderStyle.Sizable;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = true;
        TopMost = true;
        AutoScaleMode = AutoScaleMode.Dpi;
        AutoScaleDimensions = new SizeF(96F, 96F);
        MinimumSize = new Size(520, 410);
        ClientSize = new Size(760, 570);
        Font = new Font("Segoe UI", 9F);
        BackColor = Color.FromArgb(248, 249, 251);
        ForeColor = Color.FromArgb(28, 35, 45);
        KeyPreview = true;

        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(20),
            ColumnCount = 1,
            RowCount = 3,
            BackColor = BackColor,
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 72));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 60));

        var title = new Label
        {
            Dock = DockStyle.Fill,
            Text = "这次计划现在准备开始。请确认本次录制范围。\nThis scheduled run is ready. Confirm this capture.",
            Font = new Font("Segoe UI", 12F, FontStyle.Bold),
            TextAlign = ContentAlignment.MiddleLeft,
        };
        layout.Controls.Add(title, 0, 0);

        var absoluteX = checked(details.DisplayBounds.X + details.RegionWithinDisplay.X);
        var absoluteY = checked(details.DisplayBounds.Y + details.RegionWithinDisplay.Y);
        var summary = string.Join(Environment.NewLine, new[]
        {
            $"显示器 / Display: {details.DisplayName}",
            $"物理区域 / Physical region: X {absoluteX}, Y {absoluteY}, {details.RegionWithinDisplay.Width} × {details.RegionWithinDisplay.Height} px",
            $"时长 / Duration: {details.Duration.TotalSeconds.ToString("0", CultureInfo.InvariantCulture)} seconds",
            "音频 / Audio: 不录制 / Not captured",
            $"计划窗口 / Start window (UTC): {details.ScheduledStartUtc:yyyy-MM-dd HH:mm:ss} – {details.LatestStartUtc:yyyy-MM-dd HH:mm:ss}",
            $"冻结输出文件 / Frozen destination: {details.FrozenOutputFilePath}",
        });
        var body = new TextBox
        {
            Dock = DockStyle.Fill,
            Multiline = true,
            ReadOnly = true,
            WordWrap = true,
            ScrollBars = ScrollBars.Vertical,
            Text = summary,
            TabStop = true,
            BorderStyle = BorderStyle.FixedSingle,
            BackColor = Color.White,
            ForeColor = ForeColor,
            Margin = new Padding(0, 0, 0, 10),
        };
        layout.Controls.Add(body, 0, 1);

        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.RightToLeft,
            WrapContents = false,
            Padding = new Padding(0, 12, 0, 0),
        };
        _approveButton = new ExplicitClickButton
        {
            Text = "开始本次录制 / Start once",
            AutoSize = true,
            MinimumSize = new Size(180, 34),
            Margin = new Padding(8, 0, 0, 0),
            DialogResult = DialogResult.None,
        };
        _approveButton.MouseClick += (_, _) => Finish(RequiredOnceExecutionApprovalResult.Approved, "explicit_mouse_click");
        var reject = new Button
        {
            Text = "不录制 / Skip",
            AutoSize = true,
            MinimumSize = new Size(130, 34),
            Margin = new Padding(8, 0, 0, 0),
            DialogResult = DialogResult.None,
        };
        reject.Click += (_, _) => Finish(RequiredOnceExecutionApprovalResult.Rejected, "explicit_skip");
        buttons.Controls.Add(_approveButton);
        buttons.Controls.Add(reject);
        layout.Controls.Add(buttons, 0, 2);
        Controls.Add(layout);
        AcceptButton = null;
        CancelButton = null;

        _deadlineTimer = new System.Windows.Forms.Timer { Interval = 100 };
        _deadlineTimer.Tick += (_, _) => CheckDeadline();
        _foregroundRetryTimer = new System.Windows.Forms.Timer { Interval = 250 };
        _foregroundRetryTimer.Tick += (_, _) =>
        {
            _foregroundRetryTimer.Stop();
            if (!_completed && !EnsureForeground())
                RecordForegroundUnconfirmedOrFailClosed();
        };
    }

    internal string RequestId { get; }
    internal RequiredOnceExecutionApprovalResult Result => _result;
    internal bool DeadlineTimerEnabledForTests => _deadlineTimer.Enabled;
    internal bool IsCompletedForTests => _completed;
    internal int ForegroundAttemptsForTests => _foregroundAttempts;
    internal ExplicitClickButton ApproveButtonForTests => _approveButton;

    internal static RequiredOnceExecutionApprovalResult ShowModal(
        RequiredOnceExecutionApprovalDetails details,
        CancellationToken cancellationToken,
        string requestId,
        IWindowActivator activator,
        Action<string, object> audit,
        Func<DateTimeOffset> utcNow,
        Func<IReadOnlyList<Rectangle>> workingAreas,
        Action? beforeHandleForTest = null,
        Action? afterHandleForTest = null,
        Action<RequiredOnceExecutionApprovalForm>? afterShownForTest = null)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            try { audit("unavailable", new { reason_code = "host_shutdown" }); }
            catch { }
            return RequiredOnceExecutionApprovalResult.HostShutdown;
        }

        using var form = new RequiredOnceExecutionApprovalForm(
            details, cancellationToken, requestId, activator, audit, utcNow, workingAreas);
        beforeHandleForTest?.Invoke();
        if (cancellationToken.IsCancellationRequested)
            return RequiredOnceExecutionApprovalResult.HostShutdown;

        _ = form.Handle;
        using var registration = cancellationToken.Register(static state =>
            ((RequiredOnceExecutionApprovalForm)state!).RequestHostShutdown(), form);
        afterHandleForTest?.Invoke();
        if (cancellationToken.IsCancellationRequested)
        {
            form.Finish(RequiredOnceExecutionApprovalResult.HostShutdown, "host_shutdown", closeWindow: false);
            return form.Result;
        }

        form.AfterShownForTest = afterShownForTest;
        _ = form.ShowDialog();
        return form.Result;
    }

    internal static Rectangle ComputeVisibleBounds(
        Rectangle approvedDisplayBounds,
        Size desiredSize,
        IReadOnlyList<Rectangle> workingAreas,
        Rectangle fallbackWorkingArea,
        int inset = BoundsInset)
    {
        if (inset < 0) throw new ArgumentOutOfRangeException(nameof(inset));
        var areas = workingAreas.Where(area => area.Width > 0 && area.Height > 0).ToArray();
        if (areas.Length == 0 && fallbackWorkingArea.Width > 0 && fallbackWorkingArea.Height > 0)
            areas = new[] { fallbackWorkingArea };
        if (areas.Length == 0)
            return new Rectangle(0, 0, Math.Max(1, desiredSize.Width), Math.Max(1, desiredSize.Height));

        var target = new Rectangle(approvedDisplayBounds.X, approvedDisplayBounds.Y,
            Math.Max(0, approvedDisplayBounds.Width), Math.Max(0, approvedDisplayBounds.Height));
        var centerX = target.Width == 0 ? target.X : target.Left + target.Width / 2;
        var centerY = target.Height == 0 ? target.Y : target.Top + target.Height / 2;
        var area = areas
            .OrderBy(candidate => DistanceSquaredToRectangle(centerX, centerY, candidate))
            .First();
        if (DistanceSquaredToRectangle(centerX, centerY, area) != 0 &&
            fallbackWorkingArea.Width > 0 && fallbackWorkingArea.Height > 0)
            area = fallbackWorkingArea;

        var maxWidth = Math.Max(1, area.Width - Math.Min(inset * 2, Math.Max(0, area.Width - 1)));
        var maxHeight = Math.Max(1, area.Height - Math.Min(inset * 2, Math.Max(0, area.Height - 1)));
        var width = Math.Clamp(desiredSize.Width, 1, maxWidth);
        var height = Math.Clamp(desiredSize.Height, 1, maxHeight);
        var x = Math.Clamp(centerX - width / 2, area.Left, area.Right - width);
        var y = Math.Clamp(centerY - height / 2, area.Top, area.Bottom - height);
        return new Rectangle(x, y, width, height);
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        Log("handle_created", new
        {
            handle_created = true,
            bounds = BoundsPayload(Bounds),
        });
    }

    protected override void OnLoad(EventArgs e)
    {
        base.OnLoad(e);
        ApplySafeBounds(ToRectangle(_details.DisplayBounds));
    }

    protected override void OnDpiChanged(DpiChangedEventArgs e)
    {
        base.OnDpiChanged(e);
        ApplySafeBounds(Bounds);
    }

    private void ApplySafeBounds(Rectangle preferredBounds)
    {
        try
        {
            var areas = _workingAreas();
            var fallback = Screen.PrimaryScreen?.WorkingArea ?? (areas.Count > 0 ? areas[0] : new Rectangle(0, 0, 1024, 768));
            var visibleBounds = ComputeVisibleBounds(preferredBounds, Size, areas, fallback);
            MinimumSize = new Size(Math.Min(MinimumSize.Width, visibleBounds.Width), Math.Min(MinimumSize.Height, visibleBounds.Height));
            Bounds = visibleBounds;
        }
        catch
        {
            var fallback = Screen.PrimaryScreen?.WorkingArea ?? new Rectangle(0, 0, 1024, 768);
            var visibleBounds = ComputeVisibleBounds(preferredBounds, Size, new[] { fallback }, fallback);
            MinimumSize = new Size(Math.Min(MinimumSize.Width, visibleBounds.Width), Math.Min(MinimumSize.Height, visibleBounds.Height));
            Bounds = visibleBounds;
        }
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        Log("shown", new
        {
            visible = Visible,
            topmost = TopMost,
            bounds = BoundsPayload(Bounds),
            taskbar_entry = ShowInTaskbar,
        });
        _deadlineTimer.Start();
        if (!EnsureForeground())
            _foregroundRetryTimer.Start();
        AfterShownForTest?.Invoke(this);
    }

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        var key = keyData & Keys.KeyCode;
        if (key == Keys.Enter)
        {
            Finish(RequiredOnceExecutionApprovalResult.Rejected, "enter_safe_skip");
            return true;
        }
        if (key == Keys.Escape)
        {
            Finish(RequiredOnceExecutionApprovalResult.Rejected, "escape_safe_skip");
            return true;
        }
        return base.ProcessCmdKey(ref msg, keyData);
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (!_completed)
            Finish(RequiredOnceExecutionApprovalResult.Rejected, "window_closed", closeWindow: false);
        base.OnFormClosing(e);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _deadlineTimer.Dispose();
            _foregroundRetryTimer.Dispose();
        }
        base.Dispose(disposing);
    }

    internal void SimulateApproveMouseClickForTest() => _approveButton.SimulateExplicitClick();

    internal void ProcessKeyForTest(Keys key)
    {
        var msg = Message.Create(Handle, 0x0100, (IntPtr)(int)key, IntPtr.Zero);
        _ = ProcessCmdKey(ref msg, key);
    }

    internal void CheckDeadlineForTest() => CheckDeadline();
    internal void RetryForegroundForTest()
    {
        _foregroundRetryTimer.Stop();
        if (!_completed && !EnsureForeground())
            RecordForegroundUnconfirmedOrFailClosed();
    }

    private Action<RequiredOnceExecutionApprovalForm>? AfterShownForTest { get; set; }

    private bool EnsureForeground()
    {
        if (_completed || !IsHandleCreated || !Visible) return false;
        var attempt = ++_foregroundAttempts;
        var topMostApplied = false;
        var foregroundSet = false;
        var broughtToTop = false;
        var foreground = false;
        Log("foreground_attempt", new
        {
            attempt,
            visible = Visible,
            topmost = TopMost,
            bounds = BoundsPayload(Bounds),
        });
        try { topMostApplied = _activator.SetTopMost(Handle); } catch { }
        TopMost = true;
        try { foregroundSet = _activator.SetForeground(Handle); } catch { }
        try { foreground = _activator.GetForegroundWindow() == Handle; } catch { }
        if (!foreground && !foregroundSet)
        {
            try { broughtToTop = _activator.BringToTop(Handle); } catch { }
            try { foreground = _activator.GetForegroundWindow() == Handle; } catch { }
        }
        Log("foreground_result", new
        {
            attempt,
            topmost_set_success = topMostApplied,
            set_foreground_window_success = foregroundSet,
            bring_window_to_top_success = broughtToTop,
            foreground_confirmed = foreground,
            visible = Visible,
            topmost = TopMost,
            bounds = BoundsPayload(Bounds),
        });
        return foreground;
    }

    private void CheckDeadline()
    {
        if (_completed) return;
        DateTimeOffset now;
        try { now = _utcNow(); }
        catch
        {
            Finish(RequiredOnceExecutionApprovalResult.Unavailable, "execution_clock_unavailable");
            return;
        }
        if (now.Offset != TimeSpan.Zero)
        {
            Finish(RequiredOnceExecutionApprovalResult.Unavailable, "execution_clock_unavailable");
        }
        else if (now >= _details.LatestStartUtc)
        {
            Finish(RequiredOnceExecutionApprovalResult.TimedOut, "latest_start_window_missed");
        }
        else if (now >= _boundedDeadlineUtc)
        {
            Finish(RequiredOnceExecutionApprovalResult.TimedOut, "execution_confirmation_timed_out");
        }
        else if (_cancellationToken.IsCancellationRequested)
        {
            Finish(RequiredOnceExecutionApprovalResult.HostShutdown, "host_shutdown");
        }
    }

    private void RequestHostShutdown()
    {
        if (_completed || IsDisposed) return;
        try
        {
            if (IsHandleCreated && InvokeRequired)
                BeginInvoke((Action)FinishHostShutdownOnUiThread);
            else
                FinishHostShutdownOnUiThread();
        }
        catch
        {
            // A closing or not-yet-pumped form is already fail-closed; the caller also observes cancellation.
        }
    }

    private void RecordForegroundUnconfirmedOrFailClosed()
    {
        if (!IsVisibleOnWorkingArea())
        {
            Finish(RequiredOnceExecutionApprovalResult.Unavailable, "window_not_visible_on_interactive_desktop");
            return;
        }
        Log("foreground_unconfirmed_but_visible", new
        {
            reason_code = "foreground_activation_denied",
            foreground_attempts = _foregroundAttempts,
            visible = Visible,
            topmost = TopMost,
            taskbar_entry = ShowInTaskbar,
            bounds = BoundsPayload(Bounds),
        });
    }

    private bool IsVisibleOnWorkingArea()
    {
        if (!Visible || !TopMost || !ShowInTaskbar) return false;
        try { return _workingAreas().Any(area => area.Width > 0 && area.Height > 0 && area.Contains(Bounds)); }
        catch { return false; }
    }

    private void FinishHostShutdownOnUiThread()
    {
        if (_completed || IsDisposed) return;
        CheckDeadline();
        if (!_completed)
            Finish(RequiredOnceExecutionApprovalResult.HostShutdown, "host_shutdown");
    }

    private void Finish(RequiredOnceExecutionApprovalResult result, string reason, bool closeWindow = true)
    {
        if (_completed) return;
        _completed = true;
        _result = result;
        _deadlineTimer.Stop();
        _foregroundRetryTimer.Stop();
        switch (result)
        {
            case RequiredOnceExecutionApprovalResult.Approved:
                Log("user_approved", new { reason_code = reason, visible = Visible, bounds = BoundsPayload(Bounds) });
                break;
            case RequiredOnceExecutionApprovalResult.Rejected:
                Log("user_rejected", new { reason_code = reason, visible = Visible, bounds = BoundsPayload(Bounds) });
                break;
            case RequiredOnceExecutionApprovalResult.TimedOut:
                Log("timeout", new { reason_code = reason, visible = Visible, bounds = BoundsPayload(Bounds) });
                break;
            default:
                Log("unavailable", new { reason_code = reason, visible = Visible, handle_created = IsHandleCreated });
                break;
        }
        DialogResult = result switch
        {
            RequiredOnceExecutionApprovalResult.Approved => DialogResult.OK,
            RequiredOnceExecutionApprovalResult.Rejected => DialogResult.Cancel,
            _ => DialogResult.Abort,
        };
        if (closeWindow && !IsDisposed && (Visible || IsHandleCreated))
        {
            try { Close(); } catch { }
        }
    }

    private void Log(string stage, object payload)
    {
        try { _audit("required_once_execution_dialog." + stage, payload); }
        catch { }
    }

    private static object BoundsPayload(Rectangle bounds) => new
    {
        x = bounds.X,
        y = bounds.Y,
        width = bounds.Width,
        height = bounds.Height,
    };

    private static Rectangle ToRectangle(AuthorizedPhysicalRectangle bounds) =>
        new(bounds.X, bounds.Y, bounds.Width, bounds.Height);

    private static long DistanceSquaredToRectangle(int x, int y, Rectangle rectangle)
    {
        var dx = x < rectangle.Left ? rectangle.Left - x : x > rectangle.Right ? x - rectangle.Right : 0;
        var dy = y < rectangle.Top ? rectangle.Top - y : y > rectangle.Bottom ? y - rectangle.Bottom : 0;
        return (long)dx * dx + (long)dy * dy;
    }

    private static DateTimeOffset Min(DateTimeOffset left, DateTimeOffset right) => left <= right ? left : right;

    internal sealed class ExplicitClickButton : Button
    {
        internal void SimulateExplicitClick() =>
            OnMouseClick(new MouseEventArgs(MouseButtons.Left, 1, Math.Max(1, Width / 2), Math.Max(1, Height / 2), 0));
    }
}
