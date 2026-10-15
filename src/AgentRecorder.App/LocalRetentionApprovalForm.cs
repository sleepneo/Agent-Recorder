using System.Drawing;
using System.Globalization;
using AgentRecorder.Infrastructure;
using AgentRecorder.Persistence;

namespace AgentRecorder.App;

internal sealed class LocalRetentionApprovalForm : Form
{
    private readonly LocalRetentionIntent _intent;
    private readonly IFutureWindowApprovalWindowPlatform _platform;
    private readonly Button _approve, _cancel;
    private readonly TextBox _scope;
    private readonly Label _warning, _keyboard;
    private readonly TableLayoutPanel _root;
    private readonly FlowLayoutPanel _actions;
    private readonly System.Windows.Forms.Timer _expiry;
    private bool _nativeVisible;
    private int _cancelled, _chosen;
    private LocalRetentionUiChoice _choice = LocalRetentionUiChoice.Unavailable;
    internal LocalRetentionApprovalForm(LocalRetentionIntent intent, IUiTextProvider text,
        IFutureWindowApprovalWindowPlatform? platform = null)
    {
        _intent = intent; _platform = platform ?? Win32FutureWindowApprovalWindowPlatform.Instance;
        Text = text.Get("RetentionApproval_Title"); Name = "LocalRetentionApprovalForm"; AccessibleName = Text;
        AutoScaleMode = AutoScaleMode.Dpi; AutoScaleDimensions = new SizeF(96, 96); StartPosition = FormStartPosition.Manual;
        MinimumSize = new(520, 420); ClientSize = new(820, 580); FormBorderStyle = FormBorderStyle.Sizable; ShowInTaskbar = true;
        Font = new Font("Segoe UI", 10); _root = new() { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 4, Padding = new(20) };
        _root.ColumnStyles.Add(new(SizeType.Percent, 100));
        foreach (var style in new[] { new RowStyle(SizeType.AutoSize), new RowStyle(SizeType.Percent, 100), new RowStyle(SizeType.AutoSize), new RowStyle(SizeType.AutoSize) }) _root.RowStyles.Add(style);
        Controls.Add(_root);
        _warning = new() { Text = text.Get("RetentionApproval_Warning"), AutoSize = true, Margin = new(0, 0, 0, 12) };
        var f = intent.Facts;
        _scope = new() { Multiline = true, ReadOnly = true, WordWrap = false, ScrollBars = ScrollBars.Both, Dock = DockStyle.Fill,
            AccessibleName = text.Get("RetentionApproval_Scope"), Margin = new(0), Text =
                text.Get("RetentionApproval_Name") + "\r\n" + Path.GetFileName(f.Anchor.Path) + "\r\n\r\n" +
                text.Get("RetentionApproval_Path") + "\r\n" + f.Anchor.Path + "\r\n\r\n" +
                text.Get("RetentionApproval_Completed") + "\r\n" + f.CompletedAtUtc.ToString("O", CultureInfo.InvariantCulture) + "\r\n\r\n" +
                text.Get("RetentionApproval_Size") + "\r\n" + f.Anchor.Length.ToString(CultureInfo.InvariantCulture) };
        _actions = new() { Dock = DockStyle.Fill, AutoSize = true, FlowDirection = FlowDirection.RightToLeft, WrapContents = true, Margin = new(0, 12, 0, 8) };
        _approve = new() { Text = text.Get("RetentionApproval_Approve"), AutoSize = true, Enabled = false, Margin = new(8, 0, 0, 0) };
        _cancel = new() { Text = text.Get("RetentionApproval_Cancel"), AutoSize = true };
        _keyboard = new() { Text = text.Get("RetentionApproval_Keyboard"), AutoSize = true, Margin = new(0) };
        _actions.Controls.Add(_approve); _actions.Controls.Add(_cancel);
        _root.Controls.Add(_warning, 0, 0); _root.Controls.Add(_scope, 0, 1); _root.Controls.Add(_actions, 0, 2); _root.Controls.Add(_keyboard, 0, 3);
        void FitText() { var width = Math.Max(120, _root.ClientSize.Width - _root.Padding.Horizontal); _warning.MaximumSize = new(width, 0); _keyboard.MaximumSize = new(width, 0); }
        _root.SizeChanged += (_, _) => FitText(); FitText();
        AcceptButton = _cancel; CancelButton = _cancel; ActiveControl = _cancel;
        _cancel.Click += (_, _) => Choose(LocalRetentionUiChoice.Reject);
        _approve.Click += (_, _) =>
        {
            try { Choose(_nativeVisible && !_intent.Expired && LocalApprovalWindowPresentation.Usable(_platform.Inspect(Handle)) ? LocalRetentionUiChoice.Approve : LocalRetentionUiChoice.Unavailable); }
            catch { Choose(LocalRetentionUiChoice.Unavailable); }
        };
        _expiry = new() { Interval = 200 }; _expiry.Tick += (_, _) => { RefreshValidity(); if (_intent.Expired) Choose(LocalRetentionUiChoice.Closed); };
        Shown += (_, _) =>
        {
            _cancel.Focus();
            PresentCurrentWindow();
        };
    }
    internal void PresentCurrentWindow()
    {
        try
        {
            if (_cancelled != 0 || _intent.Expired) { Choose(LocalRetentionUiChoice.Closed); return; }
            TopMost = true; var state = LocalApprovalWindowPresentation.ShowAndInspect(this, _platform);
            if (!LocalApprovalWindowPresentation.Usable(state)) { Choose(LocalRetentionUiChoice.Unavailable); return; }
            _nativeVisible = true; _ = _platform.Activate(Handle); RefreshValidity(); _expiry.Start();
        }
        catch { Choose(LocalRetentionUiChoice.Unavailable); }
    }
    internal LocalRetentionDecision Decision => new(_intent, _choice);
    internal void RefreshValidity() => _approve.Enabled = _nativeVisible && !_intent.Expired && _cancelled == 0;
    internal void RequestCancellation()
    {
        Interlocked.Exchange(ref _cancelled, 1);
        try { if (IsHandleCreated && !IsDisposed) BeginInvoke(new Action(() => Choose(LocalRetentionUiChoice.Closed))); } catch (InvalidOperationException) { }
    }
    private void Choose(LocalRetentionUiChoice choice)
    {
        if (Interlocked.CompareExchange(ref _chosen, 1, 0) != 0) return;
        _choice = choice == LocalRetentionUiChoice.Approve && (_cancelled != 0 || _intent.Expired) ? LocalRetentionUiChoice.Closed : choice;
        Close();
    }
    protected override void OnHandleCreated(EventArgs e) { base.OnHandleCreated(e); if (_cancelled != 0) RequestCancellation(); }
    protected override void OnLoad(EventArgs e) { LocalApprovalWindowPresentation.Fit(this); base.OnLoad(e); }
    protected override void OnDpiChanged(DpiChangedEventArgs e) { base.OnDpiChanged(e); LocalApprovalWindowPresentation.Fit(this); }
    protected override void OnFormClosing(FormClosingEventArgs e)
    { if (Interlocked.CompareExchange(ref _chosen, 1, 0) == 0) _choice = LocalRetentionUiChoice.Closed; _approve.Enabled = false; base.OnFormClosing(e); }
    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    { if ((keyData & Keys.KeyCode) is Keys.Enter or Keys.Escape) { Choose(LocalRetentionUiChoice.Escape); return true; } return base.ProcessCmdKey(ref msg, keyData); }
    protected override void Dispose(bool disposing) { if (disposing) _expiry.Dispose(); base.Dispose(disposing); }
    internal string ScopeText => _scope.Text;
    internal string WarningText => _warning.Text;
    internal Button ApproveButton => _approve;
    internal bool SafeDefaults => ReferenceEquals(AcceptButton, _cancel) && ReferenceEquals(CancelButton, _cancel) && ReferenceEquals(ActiveControl, _cancel);
    internal bool Inspectable => _scope.ReadOnly && _scope.Multiline && !_scope.WordWrap && _scope.ScrollBars == ScrollBars.Both;
    internal bool LayoutSeparated => _warning.Bottom <= _scope.Top && _scope.Bottom <= _actions.Top && _actions.Bottom <= _keyboard.Top;
    internal void ProcessSafetyKey(Keys key) { var m = new Message(); ProcessCmdKey(ref m, key); }
}

// The App's host factory owns this authority through the deletion coordinator.
// Neither API input nor an agent can select this adapter or approve its intent.
internal sealed class LocalRetentionApprovalUi(IUiTextProvider text) : ILocalRetentionApprovalUi
{
    internal static LocalRetentionApproval ForLocalHost(AgentRecorder.Core.RecordingEngine engine, SqliteOperationalStore store,
        SqliteOrdinaryRecordingHistoryStore writer, string sid, string session, string canonicalVideosRoot, CancellationToken hostStopping)
        => new(engine, store, writer, sid, session, canonicalVideosRoot,
            new LocalRetentionApprovalUi(new UiTextProvider(UiLanguageStore.LoadOrDefault())), hostStopping: hostStopping);

    public LocalRetentionUiSession Begin(LocalRetentionIntent intent, CancellationToken cancellation)
    {
        var result = new TaskCompletionSource<LocalRetentionDecision>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                using var form = new LocalRetentionApprovalForm(intent, text);
                using var registration = cancellation.Register(form.RequestCancellation);
                if (!cancellation.IsCancellationRequested && !intent.Expired) form.ShowDialog();
                result.TrySetResult(form.Decision);
            }
            catch { result.TrySetResult(new(intent, LocalRetentionUiChoice.Unavailable)); }
        }) { IsBackground = true, Name = "LocalRetentionDeletionApproval" };
        try { thread.SetApartmentState(ApartmentState.STA); thread.Start(); }
        catch { result.TrySetResult(new(intent, LocalRetentionUiChoice.Unavailable)); return new(result.Task, Task.FromResult(!thread.IsAlive)); }
        return new(result.Task, Task.Run(() => { thread.Join(); return true; }));
    }
}
