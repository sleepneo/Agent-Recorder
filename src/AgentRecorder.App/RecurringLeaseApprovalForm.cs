using System;
using System.Drawing;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Windows.Forms;
using AgentRecorder.Core.Automation;
using AgentRecorder.Infrastructure;
using AgentRecorder.Persistence;

namespace AgentRecorder.App;

internal sealed record RecurringLeaseApprovalDetails(
    string StableDisplayFingerprint, AuthorizedPhysicalRectangle DisplayBounds,
    AuthorizedPhysicalRectangle RegionWithinDisplay, int DpiX, int DpiY,
    int PhysicalWidth, int PhysicalHeight, AuthorizedDisplayOrientation Orientation,
    RecurringPlanSchedule Schedule, int MaxUses, TimeSpan MaxCumulativeDuration,
    DateTimeOffset LeaseValidUntilUtc, string OutputDirectory, string FilenamePrefix)
{
    internal static RecurringLeaseApprovalDetails FromPrepared(RecurringPlanPreparedRecord prepared) =>
        new(prepared.StableDisplayFingerprint, prepared.DisplayBounds, prepared.RegionWithinDisplay,
            prepared.DpiX, prepared.DpiY, prepared.PhysicalWidth, prepared.PhysicalHeight, prepared.Orientation,
            prepared.Request.Schedule, prepared.Request.MaxUses, prepared.Request.MaxCumulativeDuration,
            prepared.Request.LeaseValidUntilUtc, prepared.Request.OutputDirectory, prepared.Request.FilenamePrefix);
}

// No persistence, execution or scheduler dependency: the only capture is an
// ephemeral preview bitmap owned and disposed by this form.
internal sealed class RecurringLeaseApprovalForm : Form
{
    private readonly Button _approve;
    private readonly Button _reject;
    private readonly TextBox _details;
    private readonly PictureBox _preview;
    private readonly Label _fallback;
    private readonly Label _keyboard;
    private readonly Panel _scroll;
    private readonly System.Windows.Forms.Timer _timeout;
    private Bitmap? _bitmap;
    private bool _decided;
    internal RecurringLeaseApprovalResult Result { get; private set; } = RecurringLeaseApprovalResult.Rejected;

    internal RecurringLeaseApprovalForm(RecurringLeaseApprovalDetails details,
        IScreenPreviewProvider? previewProvider = null, IUiTextProvider? textProvider = null)
    {
        var text = textProvider ?? new UiTextProvider(UiLanguageStore.LoadOrDefault());
        Text = text.Get("RecurringLeaseApproval_Title");
        AutoScaleMode = AutoScaleMode.Dpi;
        AutoScaleDimensions = new SizeF(96, 96);
        StartPosition = FormStartPosition.CenterScreen;
        FormBorderStyle = FormBorderStyle.Sizable;
        MinimumSize = new Size(560, 440);
        ClientSize = new Size(800, 760);
        Font = new Font("Segoe UI", 10);
        BackColor = Color.FromArgb(30, 33, 38); ForeColor = Color.White;
        var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2, Padding = new Padding(16) };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        Controls.Add(root);
        _scroll = new Panel { Dock = DockStyle.Fill, AutoScroll = true };
        root.Controls.Add(_scroll, 0, 0);
        var content = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 1, RowCount = 6, Padding = new Padding(4) };
        content.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        for (var i = 0; i < 6; i++) content.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        _scroll.Controls.Add(content);
        Label Copy(string key) => new() { AutoSize = true, Text = text.Get(key), Margin = new Padding(0, 0, 0, 10) };
        var heading = Copy("RecurringLeaseApproval_Heading");
        heading.ForeColor = Color.FromArgb(125, 211, 252);
        var warning = Copy("RecurringLeaseApproval_Warning");
        warning.ForeColor = Color.FromArgb(253, 186, 116);
        var previewTitle = Copy("RecurringLeaseApproval_PreviewTitle");
        content.Controls.Add(heading, 0, 0); content.Controls.Add(warning, 0, 1); content.Controls.Add(previewTitle, 0, 2);
        _preview = new PictureBox { Dock = DockStyle.Fill, Height = 190, SizeMode = PictureBoxSizeMode.Zoom,
            BackColor = Color.Black, Margin = new Padding(0, 0, 0, 10) };
        content.Controls.Add(_preview, 0, 3);
        _fallback = Copy("RecurringLeaseApproval_PreviewFallback");
        content.Controls.Add(_fallback, 0, 4);
        _details = new TextBox { Dock = DockStyle.Fill, Height = 330, ReadOnly = true, Multiline = true,
            ScrollBars = ScrollBars.Both, WordWrap = false, BackColor = Color.FromArgb(22, 24, 28), ForeColor = Color.White,
            Text = FormatDetails(details, text), Margin = new Padding(0, 0, 0, 10) };
        content.Controls.Add(_details, 0, 5);

        var footer = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, ColumnCount = 1, RowCount = 2 };
        footer.RowStyles.Add(new RowStyle(SizeType.AutoSize)); footer.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, WrapContents = true, FlowDirection = FlowDirection.RightToLeft };
        _approve = new Button { AutoSize = true, Text = text.Get("RecurringLeaseApproval_Approve"),
            BackColor = Color.FromArgb(22, 163, 74), ForeColor = Color.White, FlatStyle = FlatStyle.Flat, Padding = new Padding(12, 7, 12, 7), TabIndex = 1 };
        _reject = new Button { AutoSize = true, Text = text.Get("RecurringLeaseApproval_Reject"),
            BackColor = Color.FromArgb(185, 28, 28), ForeColor = Color.White, FlatStyle = FlatStyle.Flat, Padding = new Padding(12, 7, 12, 7), TabIndex = 0 };
        _approve.Click += (_, _) => Finish(RecurringLeaseApprovalResult.Approved);
        _reject.Click += (_, _) => Finish(RecurringLeaseApprovalResult.Rejected);
        buttons.Controls.Add(_approve); buttons.Controls.Add(_reject);
        footer.Controls.Add(buttons, 0, 0);
        _keyboard = Copy("RecurringLeaseApproval_Keyboard");
        footer.Controls.Add(_keyboard, 0, 1); root.Controls.Add(footer, 0, 1);
        void FitLabels()
        {
            var width = Math.Max(100, _scroll.ClientSize.Width - SystemInformation.VerticalScrollBarWidth - 16);
            foreach (var label in new[] { heading, warning, previewTitle, _fallback, _keyboard })
                label.MaximumSize = new Size(width, 0);
        }
        _scroll.SizeChanged += (_, _) => FitLabels();
        FitLabels();
        AcceptButton = _reject; CancelButton = _reject; ActiveControl = _reject;
        _timeout = new System.Windows.Forms.Timer { Interval = 300_000 };
        _timeout.Tick += (_, _) => Finish(RecurringLeaseApprovalResult.TimedOut);
        Shown += (_, _) => { _reject.Focus(); _timeout.Start(); };
        FormClosed += (_, _) => _timeout.Stop();
        try
        {
            var bounds = new ConfirmationCaptureBounds(checked(details.DisplayBounds.X + details.RegionWithinDisplay.X),
                checked(details.DisplayBounds.Y + details.RegionWithinDisplay.Y), details.RegionWithinDisplay.Width, details.RegionWithinDisplay.Height);
            PreviewBoundsForTests = new(bounds.X, bounds.Y, bounds.Width, bounds.Height);
            _bitmap = ConfirmationPreviewBuilder.TryBuildPreview(bounds, previewProvider ?? new GdiScreenPreviewProvider(), new Size(760, 240), out _);
            _preview.Image = _bitmap;
        }
        catch { }
        _fallback.Visible = _bitmap is null;
        if (_bitmap is not null) _fallback.Text = string.Empty;
    }

    private void Finish(RecurringLeaseApprovalResult result)
    {
        if (_decided) return;
        _decided = true; Result = result;
        _timeout.Stop(); Close();
    }

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        // A focused approval button must not override the safe Enter default.
        if ((keyData & Keys.KeyCode) is Keys.Enter or Keys.Escape)
        { Finish(RecurringLeaseApprovalResult.Rejected); return true; }
        return base.ProcessCmdKey(ref msg, keyData);
    }

    internal static RecurringLeaseApprovalResult ShowModal(RecurringLeaseApprovalDetails details,
        IUiTextProvider textProvider, CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested) return RecurringLeaseApprovalResult.HostShutdown;
        using var form = new RecurringLeaseApprovalForm(details, textProvider: textProvider);
        using var registration = cancellationToken.Register(() =>
        {
            try
            {
                if (form.IsHandleCreated) form.BeginInvoke(new Action(() => form.Finish(RecurringLeaseApprovalResult.HostShutdown)));
            }
            catch (InvalidOperationException) { }
        });
        form.Shown += (_, _) => { if (cancellationToken.IsCancellationRequested) form.Finish(RecurringLeaseApprovalResult.HostShutdown); };
        form.ShowDialog();
        return cancellationToken.IsCancellationRequested ? RecurringLeaseApprovalResult.HostShutdown : form.Result;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _timeout?.Dispose();
            if (_preview is not null) _preview.Image = null;
            _bitmap?.Dispose(); _bitmap = null; PreviewDisposedForTests = true;
        }
        base.Dispose(disposing);
    }

    private static string FormatDetails(RecurringLeaseApprovalDetails d, IUiTextProvider text)
    {
        var b = d.DisplayBounds; var r = d.RegionWithinDisplay; var s = d.Schedule;
        var culture = CultureInfo.GetCultureInfo(text.Language == UiLanguage.ZhCn ? "zh-CN" : "en-US");
        var days = s.IsDaily ? text.Get("RecurringLeaseApproval_EveryDay")
            : string.Join(", ", s.WeeklyDays.Select(day => culture.DateTimeFormat.GetDayName(day)));
        return text.Format("RecurringLeaseApproval_Details", d.StableDisplayFingerprint,
            b.X, b.Y, b.Width, b.Height, r.X, r.Y, r.Width, r.Height,
            d.DpiX, d.DpiY, d.PhysicalWidth, d.PhysicalHeight, text.Get("RecurringLeaseApproval_Orientation_" + d.Orientation),
            text.Get(s.IsDaily ? "RecurringLeaseApproval_Daily" : "RecurringLeaseApproval_Weekly"), s.TimeZoneId,
            s.LocalStartDate.ToString("yyyy-MM-dd"), s.LocalEndDate.ToString("yyyy-MM-dd"), s.LocalWallClockTime.ToString("HH:mm:ss"), days,
            s.RecordingDuration.TotalSeconds, s.LatestStartGrace.TotalSeconds, s.MaximumOccurrences,
            d.MaxUses, d.MaxCumulativeDuration.TotalSeconds, d.LeaseValidUntilUtc.ToString("O"), d.OutputDirectory, d.FilenamePrefix);
    }

    internal string DetailsTextForTests => _details.Text;
    internal string FallbackTextForTests => _fallback.Text;
    internal string KeyboardTextForTests => _keyboard.Text;
    internal ScrollBars DetailsScrollBarsForTests => _details.ScrollBars;
    internal bool ContentScrollsForTests => _scroll.AutoScroll;
    internal bool SafeDefaultsForTests => ReferenceEquals(AcceptButton, _reject) && ReferenceEquals(CancelButton, _reject) && ReferenceEquals(ActiveControl, _reject);
    internal bool HasPreviewForTests => _bitmap is not null;
    internal bool PreviewDisposedForTests { get; private set; }
    internal Rectangle PreviewBoundsForTests { get; private set; }
    internal bool DetailsWithinScrollForTests => _details.Parent == _scroll.Controls[0];
    internal bool LayoutDoesNotOverlapForTests
    {
        get
        {
            var items = _scroll.Controls[0].Controls.Cast<Control>().Where(c => c.Height > 0 && c.Width > 0).ToArray();
            return !items.SelectMany((first, i) => items.Skip(i + 1).Select(second => first.Bounds.IntersectsWith(second.Bounds))).Any(overlap => overlap);
        }
    }
    internal void DecideForTests(RecurringLeaseApprovalResult result) => Finish(result);
    internal void KeyboardForTests(Keys key) { ActiveControl = _approve; var message = new Message(); ProcessCmdKey(ref message, key); }
}
